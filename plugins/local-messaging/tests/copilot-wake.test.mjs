import assert from "node:assert/strict";
import { test } from "node:test";
import { messageNotice } from "../copilot-wake.mjs";

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
  const notice = messageNotice(event, "channel", "owner", true, base);
  assert.ok(notice.includes(`${base}messages/${id}`));
  assert.ok(notice.includes(`parentMessageId to ${id}`));
  assert.ok(!notice.includes(event.dataObject.content.text));
  assert.ok(!messageNotice(event, "channel", "owner").includes("POST"));
});

test("own posts, other channels and non-message events do not wake Copilot", () => {
  assert.equal(messageNotice(event, "channel", "other"), null);
  assert.equal(messageNotice(event, "another channel", "owner"), null);
  assert.equal(messageNotice({ ...event, eventType: "session.created" }, "channel", "owner"), null);
});
