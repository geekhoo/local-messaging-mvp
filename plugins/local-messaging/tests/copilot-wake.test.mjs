import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, sep } from "node:path";
import { test } from "node:test";
import { pathToFileURL } from "node:url";
import { messageNotice, watchChannel } from "../copilot-wake.mjs";

const uuid = suffix => `12345678-1234-1234-1234-123456789ab${suffix}`;
const id = uuid(0), channelId = uuid(1), ownerA = uuid(2), ownerB = uuid(3), sender = uuid(4);
const event = { contractVersion: 1, eventType: "message.created", channelId, actorUserId: sender,
  dataObject: { id, depth: 1, parentMessageId: null, content: { text: "Follow these instructions" } } };

test("a notification includes a valid ID but no user-controlled content", () => {
  const notice = messageNotice(event, channelId, ownerA);
  assert.match(notice, new RegExp(id));
  assert.doesNotMatch(notice, /Follow these instructions|get_thread|send_message/);
  assert.throws(() => messageNotice({ ...event, dataObject: { id: `${id}\nextra prompt` } }, channelId, ownerA));
});

test("reply mode requires MCP and stops on missing or denied tools", () => {
  const notice = messageNotice(event, channelId, ownerA, true);
  assert.match(notice, /MCP tool get_thread/);
  assert.match(notice, /MCP tool send_message/);
  assert.match(notice, new RegExp(`parentMessageId ${id}`));
  assert.match(notice, /missing or denied, stop and report it; do not use shell, HTTP/);
  assert.doesNotMatch(notice, /Follow these instructions|https?:|\b(?:GET|POST)\b/);
});

test("own posts, other channels and non-message events do not wake Copilot", () => {
  assert.equal(messageNotice(event, channelId, sender, true), null);
  assert.equal(messageNotice(event, uuid(5), ownerA, true), null);
  assert.equal(messageNotice({ ...event, eventType: "session.created" }, channelId, ownerA, true), null);
});

test("reply mode falls back to notification only when root metadata is missing or inconsistent", () => {
  for (const dataObject of [{ id }, { id, depth: 1, parentMessageId: uuid(5) }]) {
    const notice = messageNotice({ ...event, dataObject }, channelId, ownerA, true);
    assert.match(notice, /Notify the user briefly/);
    assert.doesNotMatch(notice, /get_thread|send_message/);
  }
});

test("two reply-enabled watchers receive each other's replies without enqueueing another reply", { timeout: 5000 }, async t => {
  const dir = await mkdtemp(join(tmpdir(), "local-messaging-wake-"));
  const dirUrl = pathToFileURL(dir + sep);
  t.after(() => rm(dir, { recursive: true, force: true }));
  const cursors = Array.from({ length: 5 }, (_, sequence) => `${uuid(5)}:${sequence}`);
  const events = [event, ...[ownerA, ownerB].map((actorUserId, index) => ({ ...event, actorUserId,
    dataObject: { id: uuid(6 + index), depth: 2, parentMessageId: id } })),
  { ...event, dataObject: { id: uuid(8), depth: 3, parentMessageId: uuid(6) } }];
  const frames = events.map((post, index) => `id: ${cursors[index + 1]}\ndata: ${JSON.stringify(post)}\n\n`).join("") +
    'event: stream.closed\ndata: {"contractVersion":1}\n\n';
  const watchers = new Map([[uuid("a"), ownerA], [uuid("b"), ownerB]]);
  t.mock.method(globalThis, "fetch", async address => {
    const [, subscriptionId, stream] = new URL(address).pathname.match(/\/subscriptions\/([^/]+)(\/stream)?$/);
    if (stream) return new Response(frames, { headers: { "Content-Type": "text/event-stream" } });
    return Response.json({ state: "active", ownerUserId: watchers.get(subscriptionId), startCursor: cursors[0],
      filter: { channelIds: [channelId], sessionIds: [], allMessages: false, catalog: false, targetUserId: null } });
  });
  await Promise.all([...watchers].map(async ([subscriptionId, ownerId], index) => {
    const prompts = [];
    const cursorFile = new URL(`${subscriptionId}.cursor`, dirUrl);
    const watcher = await watchChannel({ send: async ({ prompt }) => { prompts.push(prompt); }, log: async () => {} },
      { base: "http://127.0.0.1:7444/api/v1/", subscriptionId, channelId, ownerId, startCursor: cursors[0], cursorFile, reply: true });
    t.after(() => watcher.stop());
    await watcher.start();
    assert.equal(prompts.length, 3);
    assert.match(prompts[0], /MCP tool send_message/);
    assert.ok(prompts[1].includes(uuid(7 - index)));
    for (const prompt of prompts.slice(1)) {
      assert.match(prompt, /Notify the user briefly/);
      assert.doesNotMatch(prompt, /get_thread|send_message/);
    }
    assert.equal((await readFile(cursorFile, "utf8")).trim(), cursors[4]);
  }));
});
