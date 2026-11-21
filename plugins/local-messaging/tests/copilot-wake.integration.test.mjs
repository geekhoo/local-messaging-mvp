import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { pathToFileURL } from "node:url";
import { acknowledgeAndReply, loopbackApi } from "../copilot-reply.mjs";

const base = process.env.LOCAL_MESSAGING_TEST_BASE;

test("Copilot / General publishes one acknowledgement event and one threaded reply", {
  skip: !base && "Set LOCAL_MESSAGING_TEST_BASE to a fresh, isolated v1 frontend API URL"
}, async t => {
  const api = loopbackApi(base);
  const status = await api.get("status");
  assert.equal(status.capabilities?.liveAcknowledgementSummaries, true, "Host must support live acknowledgement summaries");
  assert.equal(status.headCursor, `${status.storeEpoch}:0`, "Use a fresh, disposable store; this test creates permanent records");

  const dir = await mkdtemp(join(tmpdir(), "copilot-wake-integration-"));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const envelope = callerUserId => ({ contractVersion: 1, requestId: randomUUID(), callerUserId });
  async function create(dataObject, callerUserId = null) {
    const receipt = await api.post("requests", { ...envelope(callerUserId), dataObject });
    assert.equal(receipt.state, "committed");
    return receipt.dataObjects;
  }

  const { user: copilot } = await create({ type: "user", handle: "copilot", displayName: "Copilot" });
  const { user: sender } = await create({ type: "user", handle: "sender", displayName: "Sender" });
  const { channel, session } = await create({ type: "channel", name: "copilot", initialSessionTitle: "General" }, copilot.id);
  const { subscription } = await api.post("subscriptions", {
    ...envelope(sender.id), filter: { allAcknowledgements: true }, start: { mode: "now" }
  });
  const subPath = `subscriptions/${subscription.id}`;
  t.after(() => api.post(`${subPath}/close`, envelope(sender.id)));

  const { message } = await create({
    type: "message", channel: { mode: "existing", id: channel.id }, session: { mode: "existing", id: session.id },
    content: { type: "text", text: "Hello Copilot" }
  }, sender.id);
  const threadPath = `messages/${message.id}/thread`;
  const before = await api.get(threadPath);
  assert.deepEqual([before.root.replyCount, before.root.acknowledgementCount], [0, 0]);

  const scope = { base, epoch: status.storeEpoch, channelId: channel.id, ownerId: copilot.id, messageId: message.id,
    cursorFile: pathToFileURL(join(dir, "cursor")).href };
  const result = await acknowledgeAndReply({ ...scope, text: "Hello!" });
  assert.deepEqual(await acknowledgeAndReply({ ...scope, text: "Different draft on replay" }), result);

  const { root, items } = await api.get(threadPath);
  assert.equal(root.replyCount, 1);
  assert.equal(root.acknowledgementCount, 1);
  assert.equal(root.latestAcknowledgingHandle, "copilot");
  assert.deepEqual(items.map(reply => [reply.id, reply.parentMessageId, reply.authorUserId, reply.content.text]),
    [[result.replyMessageId, message.id, copilot.id, "Hello!"]]);

  const events = [];
  let after = subscription.startCursor;
  for (;;) {
    const page = await api.get(`${subPath}/events`, { after, limit: 1 });
    events.push(...page.items);
    if (!page.hasMore) break;
    assert.notEqual(page.nextCursor, after, "Replay must advance while more events are available");
    after = page.nextCursor;
  }
  assert.deepEqual(events.map(event => [event.eventType, event.channelId, event.sessionId, event.actorUserId,
    event.dataObject.messageId, event.dataObject.profile?.id]),
    [["acknowledgement.created", channel.id, session.id, copilot.id, message.id, copilot.id]]);
});
