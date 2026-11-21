const UUID = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
const CURSOR = new RegExp("^(" + UUID + "):(0|[1-9][0-9]*)$", "i");

export function cursorParts(cursor) {
  const match = CURSOR.exec(cursor || "");
  if (!match) throw new Error("Invalid event cursor.");
  const sequence = BigInt(match[2]);
  if (sequence > 9223372036854775807n) throw new Error("Event cursor is out of range.");
  return { epoch: match[1].toLowerCase(), sequence };
}

export function compareCursors(a, b) {
  const left = cursorParts(a), right = cursorParts(b);
  if (left.epoch !== right.epoch) throw new Error("The store history has changed.");
  return left.sequence < right.sequence ? -1 : left.sequence > right.sequence ? 1 : 0;
}

export function compareSequence(a, b) {
  const left = BigInt(a.sequence || a.createdSequence || "0"), right = BigInt(b.sequence || b.createdSequence || "0");
  return left < right ? -1 : left > right ? 1 : 0;
}

export function mergeById(existing, incoming, maximum = 200, newestFirst = false) {
  const map = new Map(existing.map(item => [item.id, item]));
  for (const item of incoming) if (item?.id) map.set(item.id, { ...map.get(item.id), ...item });
  const values = [...map.values()].sort((a, b) => compareSequence(a, b) * (newestFirst ? -1 : 1));
  return values.slice(0, maximum);
}

export function appendPageWindow(existing, incoming, maximum = 200, newestFirst = false) {
  // Keep the page just requested even after the bounded window is full.
  return mergeById(existing, incoming, Number.MAX_SAFE_INTEGER, newestFirst).slice(-maximum);
}

export class DraftStore {
  constructor(maximum = 12) { this.maximum = maximum; this.items = new Map(); this.revision = 0; }
  key(epoch, caller, session, parent = null) { return [epoch, caller, session, ...(parent ? [parent] : [])].join("/"); }
  get(epoch, caller, session, parent = null) {
    if (!epoch || !caller || !session) return null;
    const key = this.key(epoch, caller, session, parent);
    if (!this.items.has(key)) this.items.set(key, { key, epoch, callerUserId: caller, sessionId: session, text: "", image: null, imageName: "", targetUserIds: [], revision: ++this.revision });
    const value = this.items.get(key);
    this.items.delete(key); this.items.set(key, value);
    while (this.items.size > this.maximum) this.items.delete(this.items.keys().next().value);
    return value;
  }
  touch(draft) { draft.revision = ++this.revision; }
  clearIfUnchanged(key, revision) {
    const value = this.items.get(key);
    if (!value || value.revision !== revision) return false;
    this.items.delete(key);
    return true;
  }
  clear() { this.items.clear(); }
}

export class EventPosition {
  constructor(cursor) { cursorParts(cursor); this.cursor = cursor; }
  shouldApply(cursor) { return compareCursors(cursor, this.cursor) > 0; }
  advance(cursor) {
    if (compareCursors(cursor, this.cursor) < 0) throw new Error("Stream cursor moved backwards.");
    this.cursor = cursor;
  }
}

export function safePreference(storage, key, value) {
  try {
    if (arguments.length > 2) { storage?.setItem(key, value); return value; }
    return storage?.getItem(key) || null;
  } catch { return null; }
}

export function newView() {
  return { kind: "conversation", roots: [], mentions: [], threads: new Map(), replyTarget: null, snapshotCursor: null, nextPageToken: null, loading: false, pendingEvents: [], seenMessages: new Set(), activity: 0, atLatest: true };
}

export function rememberMessage(view, id) {
  if (view.seenMessages.has(id)) return false;
  view.seenMessages.add(id);
  while (view.seenMessages.size > 1024) view.seenMessages.delete(view.seenMessages.values().next().value);
  return true;
}

export function stampMessages(items, cursor) {
  return items.map(item => ({ ...item, _countCursor: cursor, _ackCursor: cursor }));
}

export function countIncomingReply(item, cursor, isNew, isVisible, isOwn) {
  if (!isNew || (item._countCursor && cursor && compareCursors(cursor, item._countCursor) <= 0)) return;
  item.replyCount = (item.replyCount || 0) + 1;
  if (cursor) item._countCursor = cursor;
  if (!isVisible && !isOwn) item.newReply = true;
}

export function applyCommittedAcknowledgement(item, acknowledgement, cursor) {
  if (item.id !== acknowledgement.messageId || !acknowledgement.profile?.handle ||
      (item._ackCursor && compareCursors(cursor, item._ackCursor) <= 0)) return false;
  item._ackCursor = cursor;
  item.acknowledgementCount = (item.acknowledgementCount || 0) + 1;
  item.latestAcknowledgingHandle = acknowledgement.profile.handle;
  return true;
}
