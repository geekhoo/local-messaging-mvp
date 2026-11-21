import test from "node:test";
import assert from "node:assert/strict";
import { DraftStore, EventPosition, compareCursors, cursorParts, mergeById, appendPageWindow, safePreference, stampMessages, countIncomingReply, applyCommittedAcknowledgement } from "../state.js";
const epoch = "ffffffff-ffff-4fff-8fff-ffffffffffff";
const cursor = sequence => epoch + ":" + sequence;

test("64-bit sequence ordering does not lose JavaScript precision", () => {
  assert.equal(compareCursors(cursor("9007199254740992"), cursor("9007199254740993")), -1);
  assert.equal(cursorParts(cursor("9223372036854775807")).sequence, 9223372036854775807n);
  assert.throws(() => cursorParts(cursor("9223372036854775808")), /range/);
  assert.throws(() => cursorParts(cursor("-1")), /Invalid/);
  assert.throws(() => compareCursors(cursor(1), "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee:1"), /changed/);
});

test("consumer positions remain independent and cannot advance backwards", () => {
  const a = new EventPosition(cursor(0)), b = new EventPosition(cursor(0));
  a.advance(cursor(6));
  assert.equal(b.cursor, cursor(0));
  assert.equal(a.shouldApply(cursor(6)), false);
  assert.equal(b.shouldApply(cursor(6)), true);
  assert.throws(() => a.advance(cursor(5)), /backwards/);
});

test("drafts retain caller/session identity across profile switches and edited sends", () => {
  const drafts = new DraftStore(3);
  const alice = drafts.get(epoch, "alice", "session");
  alice.text = "Original"; drafts.touch(alice);
  const capturedRevision = alice.revision;
  const bob = drafts.get(epoch, "bob", "session");
  assert.equal(bob.text, "");
  alice.text = "Edited while the earlier send was unresolved"; drafts.touch(alice);
  assert.equal(drafts.clearIfUnchanged(alice.key, capturedRevision), false);
  assert.equal(drafts.get(epoch, "alice", "session").text, alice.text);
  assert.equal(drafts.get(epoch, "bob", "session").callerUserId, "bob");
  assert.equal(drafts.clearIfUnchanged(alice.key, alice.revision), true);
});

test("inactive drafts and merged message state are bounded", () => {
  const drafts = new DraftStore(2);
  drafts.get(epoch, "a", "1"); drafts.get(epoch, "a", "2"); drafts.get(epoch, "a", "3");
  assert.equal(drafts.items.size, 2);
  const items = mergeById([{ id: "one", sequence: "1", text: "old" }], [
    { id: "one", sequence: "1", text: "canonical" }, { id: "two", sequence: "9007199254740993" }, { id: "three", sequence: "9007199254740994" }
  ], 2, true);
  assert.deepEqual(items.map(item => item.id), ["three", "two"]);
});

test("browser preference storage failure does not prevent in-memory operation", () => {
  const storage = { getItem() { throw new Error("Denied"); }, setItem() { throw new Error("Full"); } };
  assert.equal(safePreference(storage, "key"), null);
  assert.equal(safePreference(storage, "key", "value"), null);
});

test("bounded history windows keep newly requested older pages instead of discarding them", () => {
  const values = Array.from({ length: 250 }, (_, index) => ({ id: String(index + 1), sequence: String(index + 1) }));
  const roots = appendPageWindow(values.slice(50).reverse(), values.slice(0, 50).reverse(), 200, true);
  assert.equal(roots.length, 200);
  assert.equal(roots[0].sequence, "200");
  assert.equal(roots.at(-1).sequence, "1");
  const replies = appendPageWindow(values.slice(0, 200), values.slice(200), 200);
  assert.equal(replies[0].sequence, "51");
  assert.equal(replies.at(-1).sequence, "250");
});


test("reply and acknowledgement indicators merge snapshot summaries and deduplicate live cursors", () => {
  const first = stampMessages([{ id: "root", replyCount: 2, acknowledgementCount: 1, latestAcknowledgingHandle: "alice" }], cursor(10))[0];
  countIncomingReply(first, cursor(10), true, false, false);
  assert.equal(first.replyCount, 2, "Snapshot history must not look like a new reply.");
  assert.equal(first.newReply, undefined);
  countIncomingReply(first, cursor(11), true, false, false);
  assert.equal(first.replyCount, 3);
  assert.equal(first.newReply, true);
  countIncomingReply(first, cursor(12), true, true, false);
  assert.equal(first.replyCount, 4);
  assert.equal(first.newReply, true, "An open thread does not clear an earlier unread reply.");
  countIncomingReply(first, cursor(13), false, false, false);
  assert.equal(first.replyCount, 4, "A deduplicated message event does not increment again.");

  const acknowledgement = { messageId: "root", profile: { handle: "copilot" } };
  assert.equal(applyCommittedAcknowledgement(first, acknowledgement, cursor(10)), false);
  assert.equal(applyCommittedAcknowledgement(first, acknowledgement, cursor(14)), true);
  assert.equal(first.acknowledgementCount, 2);
  assert.equal(first.latestAcknowledgingHandle, "copilot");
  assert.equal(applyCommittedAcknowledgement(first, acknowledgement, cursor(14)), false);
  assert.equal(first.acknowledgementCount, 2, "The same committed acknowledgement cursor is applied once.");
});

test("thread drafts are isolated from the main-session draft and each parent", () => {
  const drafts = new DraftStore(12);
  const main = drafts.get(epoch, "alice", "session");
  main.text = "keep this root draft";
  const reply = drafts.get(epoch, "alice", "session", "parent-one");
  reply.text = "nested reply";
  assert.notEqual(main.key, reply.key);
  assert.notEqual(reply.key, drafts.get(epoch, "alice", "session", "parent-two").key);
  assert.equal(drafts.get(epoch, "alice", "session").text, "keep this root draft");
});
