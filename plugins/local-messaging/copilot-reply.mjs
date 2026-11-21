import { createHash, randomUUID } from "node:crypto";
import { link, readFile, rm, writeFile } from "node:fs/promises";
import { pathToFileURL } from "node:url";
import { Api, freezeCommand } from "../../src/LocalMessaging.Web/api.js";

export const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

export function loopbackApi(base) {
  const url = new URL(base);
  if (url.protocol !== "http:" || url.hostname !== "127.0.0.1" || url.pathname !== "/api/v1/" || url.search || url.hash || url.username || url.password) {
    throw new Error("Local Messaging wake requires the IPv4 loopback frontend API URL.");
  }
  return new Api(url.href, (address, options) => fetch(address, { ...options, redirect: "error" }));
}

export async function requireCopilot(api, ownerId, epoch) {
  const status = await api.get("status");
  if (status.storeEpoch !== epoch || status.capabilities?.profileAcknowledgements !== true || status.capabilities.maxThreadDepth !== 3) {
    throw new Error("Copilot reply mode requires the configured store, public acknowledgements, and the v1 thread depth limit.");
  }
  const profile = await api.get(`users/${ownerId}`);
  if (profile.id !== ownerId || profile.handle !== "copilot") throw new Error("Copilot reply mode requires the @copilot profile.");
  return status;
}

function requestId(kind, epoch, ownerId, messageId) {
  const bytes = createHash("sha256").update(`local-messaging/copilot/v1/${kind}/${epoch}/${ownerId}/${messageId}`).digest();
  bytes[6] = (bytes[6] & 15) | 128; // UUIDv8, using a stable application-specific hash.
  bytes[8] = (bytes[8] & 63) | 128;
  const hex = bytes.toString("hex", 0, 16);
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

async function committed(api, id) {
  let receipt;
  try { receipt = await api.get(`requests/${id}`); }
  catch (error) {
    if (error.status === 404 && error.code === "request_not_committed") return null;
    throw error;
  }
  if (receipt.state !== "committed" || receipt.requestId !== id || receipt.operation !== "createObject") {
    throw new Error("Expected a matching committed Local Messaging receipt.");
  }
  return receipt;
}

async function sendAndVerify(api, command) {
  let failure;
  try { await api.send(command); }
  catch (error) { failure = error; }
  const receipt = await committed(api, command.requestId);
  if (!receipt) throw failure ?? new Error("Local Messaging write is not confirmed; retry the original command.");
  return receipt;
}

async function existingReply(api, messageId, matches) {
  let page;
  do {
    page = await api.get(`messages/${messageId}/thread`, { limit: 25, asOf: page?.snapshotCursor, pageToken: page?.nextPageToken });
    const reply = page.items.find(matches);
    if (reply) return reply;
  } while (page.nextPageToken);
  return null;
}

async function freezeReply(cursorFile, command, makeCommand) {
  const file = new URL(`copilot-${command.requestId}.json`, cursorFile);
  const temporary = new URL(`${randomUUID()}.reply.tmp`, cursorFile);
  // Publish a complete envelope without replacing another attempt's frozen draft.
  try {
    await writeFile(temporary, command.json, { encoding: "utf8", flag: "wx" });
    try { await link(temporary, file); }
    catch (error) { if (error.code !== "EEXIST") throw error; }
    const saved = await readFile(file, "utf8");
    const frozen = makeCommand(JSON.parse(saved)?.dataObject?.content?.text);
    if (frozen.json !== saved) throw new Error("Saved Copilot reply does not match its scope.");
    return frozen;
  } finally {
    await rm(temporary, { force: true });
  }
}

export async function acknowledgeAndReply({ base, channelId, ownerId, messageId, epoch, cursorFile, text }) {
  if (![channelId, ownerId, messageId, epoch].every(id => typeof id === "string" && uuid.test(id))) throw new Error("Invalid Copilot reply scope.");
  const api = loopbackApi(base);
  const journal = new URL(cursorFile);
  if (journal.protocol !== "file:" || journal.host || journal.pathname.endsWith("/")) throw new Error("Expected a local cursor file URL.");
  const status = await requireCopilot(api, ownerId, epoch);
  const message = await api.get(`messages/${messageId}`);
  if (message.id !== messageId || message.channelId !== channelId || !uuid.test(message.sessionId) ||
      !uuid.test(message.authorUserId) || message.authorUserId === ownerId || ![1, 2, 3].includes(message.depth)) {
    throw new Error("Message is outside the Copilot reply scope.");
  }
  const atLimit = message.depth === 3;
  const parent = atLimit ? message.parentMessageId : messageId;
  if (!uuid.test(parent)) throw new Error("Invalid Copilot reply parent.");
  const reference = atLimit ? `In reply to message ${messageId}:\n\n` : "";
  if (typeof text !== "string" || !text.trim()) throw new Error("An in-channel text response is required before acknowledging.");
  const body = reference + text.replace(/\r\n?/g, "\n");
  if (body.length > (status.limits?.textLength ?? 16384) || /[^\t\n\P{Cc}]/u.test(body)) throw new Error("Invalid Copilot reply text.");

  const matches = item => item && uuid.test(item.id) && item.authorUserId === ownerId && item.channelId === channelId &&
    item.sessionId === message.sessionId && item.parentMessageId === parent &&
    (!atLimit || (item.id !== messageId && item.content?.text?.startsWith(reference)));
  const replyId = requestId("reply", epoch, ownerId, messageId);
  const prior = await committed(api, replyId);
  let reply = prior?.dataObjects?.message;
  if (prior && !matches(reply)) throw new Error("Committed reply did not match the original message.");
  if (!reply) reply = await existingReply(api, messageId, matches);

  const ackId = requestId("ack", epoch, ownerId, messageId);
  const ack = await committed(api, ackId) ?? await sendAndVerify(api, freezeCommand({ type: "acknowledgement", messageId }, ownerId, null, ackId));
  const acknowledgement = ack.dataObjects?.acknowledgement;
  if (acknowledgement?.messageId !== messageId || acknowledgement.profile?.id !== ownerId || acknowledgement.profile.handle !== "copilot") {
    throw new Error("Committed acknowledgement did not match the original message and @copilot profile.");
  }
  if (!reply) {
    const makeCommand = replyText => freezeCommand({
      type: "message", channel: { mode: "existing", id: channelId }, session: { mode: "existing", id: message.sessionId },
      content: { type: "text", text: replyText }, parentMessageId: parent, targetUserIds: []
    }, ownerId, null, replyId);
    const command = await freezeReply(journal, makeCommand(body), makeCommand);
    const receipt = await sendAndVerify(api, command);
    reply = receipt.dataObjects?.message;
    if (!matches(reply)) throw new Error("Committed reply did not match the original message.");
  }
  return { acknowledgement: "committed", reply: "committed", replyMessageId: reply.id };
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
  try {
    let input = "";
    process.stdin.setEncoding("utf8");
    for await (const chunk of process.stdin) {
      input += chunk;
      if (input.length > 128 * 1024) throw new Error("Copilot reply input is too large.");
    }
    console.log(JSON.stringify(await acknowledgeAndReply(JSON.parse(input))));
  } catch {
    console.error("Copilot acknowledge/reply not fully confirmed. Retry this helper with the same scope; do not send a new request manually.");
    process.exitCode = 1;
  }
}
