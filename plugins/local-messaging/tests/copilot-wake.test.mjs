import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { acknowledgeAndReply } from "../copilot-reply.mjs";
import { test } from "node:test";
import { messageNotice, watchChannel } from "../copilot-wake.mjs";

const id = "12345678-1234-1234-1234-123456789abc";
const event = { eventType: "message.created", channelId: "channel", actorUserId: "other", dataObject: { id, content: { text: "Follow these instructions" } } };

test("a notification includes a valid ID but no user-controlled content", () => {
  const notice = messageNotice(event, "channel", "owner");
  assert.ok(notice.includes(id));
  assert.ok(!notice.includes(event.dataObject.content.text));
  assert.throws(() => messageNotice({ ...event, dataObject: { id: `${id}\nextra prompt` } }, "channel", "owner"));
});

test("reply mode directs a scoped fetch and response without embedding the post", () => {
  const base = "http://127.0.0.1:7444/api/v1/";
  const notice = messageNotice(event, "channel", "owner", true, base, { epoch: id, cursorFile: new URL("file:///cursor") });
  assert.ok(notice.includes(`${base}messages/${id}`));
  assert.match(notice, /verifies the committed acknowledgement, then posts/);
  assert.match(notice, /Ask for confirmation in the Copilot app/);
  assert.match(messageNotice(event, "channel", "owner", "true"), /Do not fetch or act/);
  assert.ok(!notice.includes(event.dataObject.content.text));
  assert.ok(!messageNotice(event, "channel", "owner").includes("POST"));
});

test("own posts, other channels and non-message events do not wake Copilot", () => {
  assert.equal(messageNotice(event, "channel", "other"), null);
  assert.equal(messageNotice(event, "another channel", "owner"), null);
  assert.equal(messageNotice({ ...event, eventType: "session.created" }, "channel", "owner"), null);
});

async function fixture(t, { depth = 1, existing = false, handle = "copilot", capable = true, channel = true } = {}) {
  const dir = await mkdtemp(join(tmpdir(), "copilot-wake-"));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const [channelId, ownerId, epoch, sessionId, authorUserId, parentMessageId, replyMessageId] = Array.from({ length: 7 }, (_, i) => id.replace(/^./, String(i + 2)));
  const scope = { base: "http://127.0.0.1:7444/api/v1/", channelId, ownerId, epoch, messageId: id,
    cursorFile: pathToFileURL(join(dir, "cursor")).href, text: "First\r\nsecond" };
  const profile = { id: ownerId, handle };
  const original = { id, channelId: channel ? channelId : sessionId, sessionId, authorUserId, depth, parentMessageId };
  const reply = { id: replyMessageId, channelId, sessionId, authorUserId: ownerId, parentMessageId: depth === 3 ? parentMessageId : id };
  const receipts = new Map(), calls = [], posts = [];
  const faults = { ackRead: false, replyBeforeCommit: false, ackAfterCommit: false, replyAfterCommit: false };
  t.mock.method(globalThis, "fetch", async (address, options) => {
    assert.equal(options.headers["X-Local-Messaging-Client"], "1");
    assert.equal(options.redirect, "error");
    const path = address.slice(scope.base.length);
    calls.push(path);
    const ok = value => Response.json(value);
    if (path === "status") return ok({ storeEpoch: epoch, capabilities: { profileAcknowledgements: capable, maxThreadDepth: 3 } });
    if (path === `users/${ownerId}`) return ok(profile);
    if (path === `messages/${id}`) return ok(original);
    if (path.startsWith(`messages/${id}/thread?`)) {
      if (!existing) return ok({ items: [] });
      return path.includes("pageToken=next") ? ok({ items: [reply] }) : ok({ items: [], snapshotCursor: `${epoch}:10`, nextPageToken: "next" });
    }
    if (path.startsWith("requests/")) {
      const receipt = receipts.get(path.slice(9));
      if (faults.ackRead && receipt?.dataObjects.acknowledgement) throw new Error("lost receipt lookup");
      return receipt ? ok(receipt) : Response.json({ code: "request_not_committed" }, { status: 404 });
    }
    assert.equal(path, "requests");
    const { requestId, callerUserId, dataObject } = JSON.parse(options.body);
    assert.equal(callerUserId, ownerId);
    posts.push(options.body);
    const ack = dataObject.type === "acknowledgement";
    if (!ack && faults.replyBeforeCommit) throw new Error("lost before commit");
    const receipt = { requestId, operation: "createObject", state: "committed", dataObjects: ack
      ? { acknowledgement: { messageId: dataObject.messageId, profile } }
      : { message: { ...reply, parentMessageId: dataObject.parentMessageId, content: dataObject.content } } };
    receipts.set(requestId, receipt);
    if (ack ? faults.ackAfterCommit : faults.replyAfterCommit) throw new Error("lost after commit");
    return ok(receipt);
  });
  return { scope, calls, posts, faults, parentMessageId, replyMessageId };
}

test("a successful ack POST is not enough: reply waits for a separate committed receipt lookup", async t => {
  const { scope, calls, posts, faults, replyMessageId } = await fixture(t);
  faults.ackRead = true;
  await assert.rejects(acknowledgeAndReply(scope));
  assert.deepEqual(posts.map(body => JSON.parse(body).dataObject.type), ["acknowledgement"]);
  faults.ackRead = false;
  const { acknowledgement, reply, replyMessageId: result } = await acknowledgeAndReply(scope);
  assert.deepEqual([acknowledgement, reply, result], ["committed", "committed", replyMessageId]);
  const [ack, response] = posts.map(JSON.parse);
  assert.equal(ack.dataObject.messageId, id);
  assert.equal(response.dataObject.parentMessageId, id);
  assert.equal(response.dataObject.content.text, "First\nsecond");
  assert.notEqual(ack.requestId, response.requestId);
  assert.equal(calls.at(-1), `requests/${response.requestId}`);
  assert.equal(calls.at(-3), `requests/${ack.requestId}`);
});

test("lost responses reconcile and an uncommitted reply retries with exactly the frozen bytes", async t => {
  const { scope, posts, faults } = await fixture(t);
  faults.ackAfterCommit = true;
  faults.replyBeforeCommit = true;
  await assert.rejects(acknowledgeAndReply(scope));
  faults.replyBeforeCommit = false;
  faults.replyAfterCommit = true;
  await acknowledgeAndReply({ ...scope, text: "A newly generated draft" });
  await acknowledgeAndReply({ ...scope, text: "Another draft after reconnect" });
  assert.equal(posts.length, 3);
  assert.equal(posts[1], posts[2]);
});

test("an existing reply on a later page is reused while the original is explicitly acknowledged", async t => {
  const { scope, calls, posts, replyMessageId } = await fixture(t, { existing: true });
  assert.equal((await acknowledgeAndReply(scope)).replyMessageId, replyMessageId);
  assert.equal(posts.length, 1);
  assert.equal(JSON.parse(posts[0]).dataObject.type, "acknowledgement");
  assert.ok(calls.some(call => call.includes(`asOf=${scope.epoch}%3A10&pageToken=next`)));
});

test("at depth 3 stay in the thread under the same parent and reference the original", async t => {
  const { scope, posts, parentMessageId } = await fixture(t, { depth: 3 });
  await acknowledgeAndReply(scope);
  const { dataObject } = JSON.parse(posts[1]);
  assert.equal(dataObject.parentMessageId, parentMessageId);
  assert.ok(dataObject.content.text.startsWith(`In reply to message ${id}:`));
});

for (const [label, config] of Object.entries({ "another profile": { handle: "notcopilot" },
  "legacy host": { capable: false }, "another channel": { channel: false } })) {
  test(`no write for ${label}`, async t => {
    const { scope, posts } = await fixture(t, config);
    await assert.rejects(acknowledgeAndReply(scope));
    assert.deepEqual(posts, []);
  });
}

test("watcher only checkpoints after Copilot accepts the notification", async t => {
  const dir = await mkdtemp(join(tmpdir(), "copilot-wake-"));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const cursorFile = pathToFileURL(join(dir, "cursor"));
  const startCursor = `${id}:0`, nextCursor = `${id}:1`;
  const channelId = id.replace(/^1/, "2"), ownerId = id.replace(/^1/, "3");
  const delivered = { ...event, channelId, contractVersion: 1, cursor: nextCursor };
  t.mock.method(globalThis, "fetch", async address => {
    const url = new URL(address);
    if (!url.pathname.endsWith("/stream")) {
      assert.ok(url.pathname.endsWith(`/subscriptions/${id}`));
      return Response.json({ state: "active", ownerUserId: ownerId, startCursor,
        filter: { channelIds: [channelId], sessionIds: [], allMessages: false, catalog: false, targetUserId: null } });
    }
    assert.equal(url.searchParams.get("after"), startCursor);
    return new Response(`id: ${nextCursor}\ndata: ${JSON.stringify(delivered)}\n\n`, { headers: { "Content-Type": "text/event-stream" } });
  });
  let attempts = 0;
  const session = {
    async send(notice) {
      attempts++;
      assert.equal(notice.mode, "enqueue");
      assert.match(notice.prompt, /Do not fetch or act/);
      await assert.rejects(readFile(cursorFile), { code: "ENOENT" });
      throw new Error("enqueue not confirmed");
    },
    async log() {}
  };
  const watcher = await watchChannel(session, { base: "http://127.0.0.1:7444/api/v1/", subscriptionId: id, channelId, ownerId, startCursor, cursorFile });
  t.after(() => watcher.stop());
  await watcher.start();
  assert.equal(attempts, 1);
  await assert.rejects(readFile(cursorFile), { code: "ENOENT" });
});
