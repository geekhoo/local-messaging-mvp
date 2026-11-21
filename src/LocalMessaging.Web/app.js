import { Api, ApiError, freezeCommand } from "./api.js";
import { LiveSubscription } from "./stream.js";
import { DraftStore, compareCursors, mergeById, appendPageWindow, newView, rememberMessage, safePreference, stampMessages, countIncomingReply, applyCommittedAcknowledgement } from "./state.js";
import { element, formatError, userLabel, renderNavigationTree, renderTimeline, ImageViews } from "./views.js";
import { publicCapabilities, publicScopes, publicAction, readPublicPage } from "./public-lists.js";

const $ = id => document.getElementById(id);
const api = new Api();
const images = new ImageViews(api);
const drafts = new DraftStore(12);
let local;
try { local = globalThis.localStorage; } catch { local = null; }
const preferenceKey = "local-messaging:" + location.origin + api.base;
const state = {
  epoch: null, head: null, profileId: null, users: new Map(), channels: [], sessionsByChannel: new Map(),
  expanded: new Set(), channelId: null, sessionId: null, view: newView(), channelPage: null,
  userPage: null, catalogSnapshot: null, pending: new Map(), mentionsCount: 0,
  capabilities: publicCapabilities()
};
let live = null, subscription = null, subscriptionAttempt = null;
let bootGeneration = 0, bootAbort = null, bootRetry = null;
let viewGeneration = 0, viewAbort = null, previewUrl = null, suggestAbort = null, suggestTimer = null;
const closeQueue = new Map();
const dialogContexts = new WeakMap();
const formCommands = new WeakMap();
const channelChoices = $("channels");
const publicDialog = $("public-dialog");
let publicList = null;
const callbacks = {
  createUser: () => openDialog("user-dialog"),
  reply: beginReply, replyComposer: renderReplyComposer, thread: toggleThread, moreThread: loadMoreThread, navigate: navigateMessage,
  acknowledgements: message => openPublicList("message", message.id)
};

function showNotice(message) {
  $("notice").textContent = message; $("notice").hidden = !message;
}
function showError(error, target = $("error-banner")) {
  target.textContent = error ? formatError(error) : ""; target.hidden = !error;
}
function connection(status, detail = "") {
  const labels = { starting: "Starting", connected: "Connected", reconnecting: "Reconnecting", unavailable: "Unavailable", "update-required": "Update required" };
  $("connection").dataset.state = status;
  $("connection-label").textContent = labels[status] || status;
  $("connection").title = detail;
}
function currentUser() { return state.users.get(state.profileId); }
function owner(id) { return "Owner: " + userLabel(state.users.get(id), id); }
function mergeCatalog(existing, incoming, maximum = 200) {
  const map = new Map(existing.map(item => [item.id, item]));
  for (const item of incoming) if (item?.id) map.set(item.id, item);
  const values = [...map.values()];
  const result = values.slice(-maximum);
  const selected = values.find(item => item.id === state.channelId || item.id === state.sessionId);
  if (selected && !result.some(item => item.id === selected.id)) { result.shift(); result.unshift(selected); }
  return result;
}
function putUsers(users) {
  for (const user of users) state.users.set(user.id, user);
  while (state.users.size > 500) {
    const first = [...state.users.keys()].find(id => id !== state.profileId);
    if (!first) break;
    state.users.delete(first);
  }
}
function sessionsEntry(channelId) {
  let entry = state.sessionsByChannel.get(channelId);
  if (entry) state.sessionsByChannel.delete(channelId);
  else entry = { items: [], page: null, loading: false, loaded: false, error: null };
  state.sessionsByChannel.set(channelId, entry);
  while (state.sessionsByChannel.size > 12) {
    const oldest = state.sessionsByChannel.keys().next().value;
    if (oldest === channelId || oldest === state.channelId) break;
    state.sessionsByChannel.delete(oldest);
  }
  return entry;
}
function sessionsFor(channelId) { return state.sessionsByChannel.get(channelId)?.items || []; }
function renderProfiles() {
  const select = $("profile");
  const options = [new Option("Select user", "")];
  for (const user of state.users.values()) options.push(new Option(userLabel(user), user.id));
  if (state.userPage) options.push(new Option("Load more users…", "__more__"));
  select.replaceChildren(...options); select.value = state.profileId || "";
}
function renderNav() {
  renderProfiles();
  renderNavigationTree(channelChoices, {
    channels: state.channels, sessionsByChannel: state.sessionsByChannel, expanded: state.expanded,
    channelId: state.channelId, sessionId: state.sessionId, profileId: state.profileId, owner
  }, { chooseChannel, chooseSession, newSession: openSessionDialog, moreSessions: loadMoreSessions });
  const channel = state.channels.find(item => item.id === state.channelId);
  $("channel-detail").textContent = channel ? "" : "Select a channel";
  $("channel-detail").hidden = Boolean(channel);
  $("new-channel").disabled = !state.profileId;
  $("mentions").disabled = !state.profileId;
  $("mentions").setAttribute("aria-current", String(state.view.kind === "mentions"));
  $("mentions-count").textContent = state.mentionsCount > 99 ? "99+" : String(state.mentionsCount);
  $("mentions-count").hidden = !state.mentionsCount;
  $("more-channels").hidden = !state.channelPage;
  if (state.view.kind === "mentions") {
    $("conversation-eyebrow").textContent = "ATTENTION, NOT PRIVACY";
    $("conversation-title").textContent = "Your mentions";
    $("conversation-subtitle").textContent = "Messages targeting @" + (currentUser()?.handle || "") + " across all sessions";
  } else {
    const session = sessionsFor(state.channelId).find(item => item.id === state.sessionId);
    $("conversation-eyebrow").textContent = channel ? "# " + channel.name : "LOCAL WORKSPACE";
    $("conversation-title").textContent = session?.title || (state.profileId ? "Choose a conversation" : "A place to think together");
    $("conversation-subtitle").textContent = session ? owner(session.ownerUserId) : (state.profileId ? "Browse a channel, or create somewhere new to talk." : "Create a user to start a conversation.");
  }
  $("refresh-view").hidden = !state.profileId;
  $("channel-participants").hidden = !state.capabilities.participantRosters || !state.channelId;
  $("session-participants").hidden = !state.capabilities.participantRosters || !state.sessionId || state.view.kind !== "conversation";
}
function renderContent({ scroll = false } = {}) {
  const oldScroll = $("timeline").scrollTop;
  const editor = document.activeElement?.matches(".thread-reply-text") ? document.activeElement : null;
  const target = editor?.dataset.parentId;
  const selection = editor && [editor.selectionStart, editor.selectionEnd, editor.selectionDirection];
  renderTimeline($("timeline"), state, callbacks, images);
  if (target && state.view.replyTarget?.id === target) {
    const next = $("timeline").querySelector(".thread-reply-text");
    next?.focus({ preventScroll: true });
    next?.setSelectionRange(...selection);
  }
  const count = state.view.kind === "mentions" ? state.view.mentions.length : state.view.roots.length;
  $("history-controls").hidden = !count && !state.view.nextPageToken;
  $("more-messages").hidden = !state.view.nextPageToken;
  $("more-messages").textContent = state.view.kind === "mentions" ? "Load older mentions" : "Load earlier messages";
  $("history-note").textContent = count >= 200 ? "200-item window · Keep paging or refresh for latest" : count + " loaded";
  $("activity").hidden = !state.view.activity;
  $("activity-label").textContent = state.view.activity + (state.view.activity === 1 ? " new reply or activity" : " new replies or activities");
  $("timeline").scrollTop = scroll ? $("timeline").scrollHeight : oldScroll;
}
function draft() { return drafts.get(state.epoch, state.profileId, state.sessionId); }
function renderComposer() {
  const enabled = state.profileId && state.sessionId && state.view.kind === "conversation";
  $("composer").hidden = !enabled;
  if (!enabled) { clearPreview(); return; }
  const value = draft();
  $("message-text").value = value.text;
  $("message-text").maxLength = value.image ? 4096 : 16384;
  $("message-text").required = !value.image;
  $("message-text").placeholder = value.image ? "Add a caption… @handles work here too." : "Write a message… Use @handle to get someone's attention.";
  $("message-label").textContent = value.image ? "Image caption" : "Message";
  $("draft-owner").textContent = "Sending as @" + (currentUser()?.handle || "") + " · draft stays with this profile and session";
  $("target-chips").replaceChildren(...value.targetUserIds.map(id => {
    const chip = element("span", "chip", "@" + (state.users.get(id)?.handle || id.slice(0, 8)));
    const remove = element("button", "", "×"); remove.type = "button"; remove.setAttribute("aria-label", "Remove target");
    remove.addEventListener("click", () => { value.targetUserIds = value.targetUserIds.filter(target => target !== id); drafts.touch(value); renderComposer(); });
    chip.append(remove); return chip;
  }));
  clearPreview();
  $("image-preview").hidden = !value.image;
  if (value.image) {
    previewUrl = URL.createObjectURL(value.image); $("preview-image").src = previewUrl;
    $("image-name").textContent = value.imageName;
    $("image-size").textContent = (value.image.size / 1024).toFixed(1) + " KiB";
  }
  updateCount();
}
function clearPreview() {
  $("preview-image").removeAttribute("src");
  if (previewUrl) URL.revokeObjectURL(previewUrl);
  previewUrl = null;
}
function updateCount() {
  const maximum = draft()?.image ? 4096 : 16384;
  $("character-count").textContent = $("message-text").value.length.toLocaleString() + " / " + maximum.toLocaleString();
}
function renderAll() { renderNav(); renderContent(); renderComposer(); renderPending(); }

function stopStream(close = true) {
  live?.stop(); live = null;
  if (subscription && close) {
    closeQueue.set(subscription.id, { descriptor: subscription, epoch: state.epoch, requestId: crypto.randomUUID() });
    while (closeQueue.size > 20) closeQueue.delete(closeQueue.keys().next().value);
  }
  subscription = null;
}
async function drainClosures() {
  for (const [id, entry] of closeQueue) {
    if (entry.epoch !== state.epoch) { closeQueue.delete(id); continue; }
    try { await api.closeSubscription(entry.descriptor, undefined, entry.requestId); closeQueue.delete(id); }
    catch (error) {
      if ([404, 410].includes(error.status)) closeQueue.delete(id);
      else if (!error.uncertain && error.status !== 429) { showError(error); closeQueue.delete(id); }
      else break;
    }
  }
}

async function bootstrap(preferred = state.profileId || safePreference(local, preferenceKey + ":last-profile")) {
  clearTimeout(bootRetry); bootRetry = null;
  const generation = ++bootGeneration;
  bootAbort?.abort(); bootAbort = new AbortController();
  const signal = bootAbort.signal;
  stopStream(true);
  viewAbort?.abort(); viewGeneration++;
  clearPublicList();
  if (publicDialog.open) publicDialog.close();
  if (state.capabilities.profileAcknowledgements || state.capabilities.participantRosters) {
    state.capabilities = publicCapabilities();
    renderNav(); renderContent(); renderPending();
  }
  connection("starting"); showError(null);
  try {
    const status = await api.get("status", undefined, signal);
    if (generation !== bootGeneration) return;
    if (!status.headCursor || !status.storeEpoch || status.ready === false) throw new ApiError({ status: 503, code: "host_not_ready", detail: "The local host is still starting." });
    const changed = state.epoch && state.epoch !== status.storeEpoch;
    if (changed) {
      drafts.clear(); state.view = newView(); state.channelId = null; state.sessionId = null;
      subscriptionAttempt = null; state.users.clear(); state.sessionsByChannel.clear(); state.expanded.clear();
      for (const record of state.pending.values()) if (record.epoch !== status.storeEpoch) record.phase = "history-changed";
      showNotice("The local store changed. Profiles and conversations were reloaded. Review any pending commands from before the change.");
    }
    state.epoch = status.storeEpoch; state.head = status.headCursor; state.catalogSnapshot = status.headCursor;
    state.capabilities = publicCapabilities(status);
    const [users, channels] = await Promise.all([
      api.get("users", { asOf: state.head, limit: 200 }, signal),
      api.get("channels", { asOf: state.head, limit: 50 }, signal)
    ]);
    if (generation !== bootGeneration) return;
    state.users.clear(); putUsers(users.items); state.userPage = users.nextPageToken;
    state.channels = channels.items; state.channelPage = channels.nextPageToken;
    if (preferred && !state.users.has(preferred)) {
      try { putUsers([await api.get("users/" + encodeURIComponent(preferred), undefined, signal)]); }
      catch (error) { if (error.status !== 404) throw error; preferred = null; }
    }
    if (generation !== bootGeneration) return;
    state.profileId = preferred && state.users.has(preferred) ? preferred : null;
    state.mentionsCount = 0; state.view = newView();
    if (!state.channels.some(channel => channel.id === state.channelId)) state.channelId = state.channels[0]?.id || null;
    if (state.channelId) {
      const entry = sessionsEntry(state.channelId);
      const sessions = await api.get("channels/" + encodeURIComponent(state.channelId) + "/sessions", { asOf: state.head, limit: 50 }, signal);
      if (generation !== bootGeneration) return;
      entry.items = sessions.items; entry.page = sessions.nextPageToken;
      entry.loaded = true; entry.loading = false; entry.error = null;
      state.expanded.add(state.channelId);
      if (!entry.items.some(session => session.id === state.sessionId)) state.sessionId = entry.items[0]?.id || null;
    } else { state.sessionId = null; }
    if (state.profileId && state.sessionId) await loadView({ asOf: state.head });
    if (generation !== bootGeneration) return;
    renderAll(); await drainClosures();
    if (generation !== bootGeneration) return;
    if (!state.profileId) { connection("connected", "Select a user to start live updates."); return; }
    safePreference(local, preferenceKey + ":last-profile", state.profileId);
    if (!subscriptionAttempt || subscriptionAttempt.owner !== state.profileId || subscriptionAttempt.epoch !== state.epoch) {
      subscriptionAttempt = { owner: state.profileId, epoch: state.epoch, after: state.head, requestId: crypto.randomUUID() };
    }
    const attempt = subscriptionAttempt;
    const receipt = await api.createSubscription(attempt.owner, attempt.after, attempt.requestId, signal, state.capabilities.liveAcknowledgementSummaries);
    if (generation !== bootGeneration) {
      if (receipt.subscription) {
        closeQueue.set(receipt.subscription.id, { descriptor: receipt.subscription, epoch: attempt.epoch, requestId: crypto.randomUUID() });
        void drainClosures();
      }
      return;
    }
    if (!receipt.subscription?.id || !receipt.subscription.startCursor) throw new ApiError({ code: "invalid_contract", detail: "The subscription result is missing its descriptor." });
    subscriptionAttempt = null; subscription = receipt.subscription;
    live = new LiveSubscription(api, subscription, {
      onEvent: event => { if (generation === bootGeneration) applyEvent(event); },
      onState: (value, detail) => { if (generation === bootGeneration) connection(value, detail); },
      onReset: () => { if (generation === bootGeneration) void bootstrap(state.profileId); },
      onFatal: error => { if (generation === bootGeneration) showError(error); }
    });
    void live.start();
  } catch (error) {
    if (signal.aborted || generation !== bootGeneration) return;
    connection(error.code === "unsupported_contract_version" ? "update-required" : "unavailable", error.message);
    showError(error); renderAll();
    if (error.uncertain || error.status === 429 || error.status === 503) {
      bootRetry = setTimeout(() => bootstrap(preferred), Math.max(error.retryAfterMs || 0, 5000 + Math.random() * 2000));
    }
  }
}

const sessionLoads = new Map();
async function ensureSessions(channelId, { asOf = null, signal = null } = {}) {
  const entry = sessionsEntry(channelId);
  if (entry.loaded) return entry;
  if (sessionLoads.has(channelId)) return sessionLoads.get(channelId);
  entry.loading = true; entry.error = null; renderNav();
  const load = (async () => {
    try {
      const page = await api.get("channels/" + encodeURIComponent(channelId) + "/sessions", { asOf: asOf || state.head, limit: 50 }, signal);
      entry.items = mergeCatalog(entry.items, page.items); entry.page = page.nextPageToken; entry.loaded = true;
    } catch (error) {
      entry.error = error;
      if (!signal?.aborted) handleReadError(error);
    } finally {
      entry.loading = false; sessionLoads.delete(channelId); renderNav();
    }
    return entry;
  })();
  sessionLoads.set(channelId, load);
  return load;
}

async function selectChannel(id, sessionId = null) {
  if (!state.profileId) { showNotice("Select a user before opening a conversation."); return; }
  const generation = ++viewGeneration;
  viewAbort?.abort(); viewAbort = new AbortController();
  const signal = viewAbort.signal;
  state.channelId = id; state.sessionId = null; state.view = newView();
  state.expanded.add(id);
  renderAll();
  try {
    const status = await api.get("status", undefined, signal);
    if (status.storeEpoch !== state.epoch) { void bootstrap(state.profileId); return; }
    const entry = await ensureSessions(id, { asOf: status.headCursor, signal });
    if (generation !== viewGeneration) return;
    if (sessionId && !entry.items.some(s => s.id === sessionId)) entry.items.push(await api.get("sessions/" + encodeURIComponent(sessionId), undefined, signal));
    if (generation !== viewGeneration) return;
    state.sessionId = sessionId || entry.items[0]?.id || null;
    if (state.sessionId) await loadView({ asOf: status.headCursor });
    renderAll();
  } catch (error) { if (!signal.aborted) handleReadError(error); }
}

function chooseChannel(id) {
  if (!state.profileId) { showNotice("Select a user before opening a conversation."); return; }
  if (state.channelId === id && state.expanded.has(id)) { state.expanded.delete(id); renderNav(); return; }
  if (state.channelId === id) { state.expanded.add(id); renderNav(); void ensureSessions(id); return; }
  void selectChannel(id);
}
function chooseSession(channelId, id) {
  if (!state.profileId) { showNotice("Select a user before opening a conversation."); return; }
  if (channelId === state.channelId) void selectSession(id);
  else void selectChannel(channelId, id);
}
async function loadMoreSessions(channelId) {
  const entry = state.sessionsByChannel.get(channelId);
  if (!entry?.page || entry.loading) return;
  entry.loading = true; renderNav();
  try {
    const page = await api.get("channels/" + encodeURIComponent(channelId) + "/sessions", { pageToken: entry.page, limit: 50 });
    if (state.sessionsByChannel.get(channelId) === entry) {
      entry.items = mergeCatalog(entry.items, page.items); entry.page = page.nextPageToken;
    }
  } catch (error) { handleReadError(error); }
  finally { entry.loading = false; renderNav(); }
}
function openSessionDialog(channelId) {
  if (!state.profileId) return;
  $("session-channel-label").textContent = "In #" + (state.channels.find(item => item.id === channelId)?.name || "");
  openDialog("session-dialog", { channelId });
}

async function selectSession(id) {
  if (id === state.sessionId && state.view.kind === "conversation") return;
  state.sessionId = id; state.view = newView(); showError(null, $("composer-error"));
  renderAll(); await loadView(); renderComposer();
}

function handleReadError(error) {
  if (error.needsResync) void bootstrap(state.profileId);
  else showError(error);
}

async function loadView({ asOf = null, append = false } = {}) {
  if (!state.profileId || (state.view.kind !== "mentions" && !state.sessionId)) return;
  const view = state.view, kind = view.kind;
  const generation = ++viewGeneration;
  viewAbort?.abort(); viewAbort = new AbortController();
  const signal = viewAbort.signal;
  view.loading = true;
  try {
    let snapshot = append ? view.snapshotCursor : asOf;
    if (!snapshot) {
      const status = await api.get("status", undefined, signal);
      if (status.storeEpoch !== state.epoch) { void bootstrap(state.profileId); return; }
      snapshot = status.headCursor;
    }
    const path = kind === "mentions" ? "users/" + encodeURIComponent(state.profileId) + "/mentions" : "sessions/" + encodeURIComponent(state.sessionId) + "/messages";
    const page = await api.get(path, { asOf: snapshot, pageToken: append ? view.nextPageToken : null, limit: 50, view: kind === "mentions" ? null : "roots" }, signal);
    if (generation !== viewGeneration || view !== state.view) return;
    if (!append) { view.roots = []; view.mentions = []; view.threads.clear(); view.replyTarget = null; view.seenMessages.clear(); view.activity = 0; view.atLatest = true; }
    else view.atLatest = false;
    if (kind === "mentions") view.mentions = append ? appendPageWindow(view.mentions, stampMessages(page.items, page.snapshotCursor), 200, true) : mergeById([], stampMessages(page.items, page.snapshotCursor), 200, true);
    else {
      const roots = stampMessages(page.items, page.snapshotCursor);
      view.roots = append ? appendPageWindow(view.roots, roots, 200, true) : mergeById([], roots, 200, true);
      const rootIds = new Set(view.roots.map(item => item.id));
      for (const id of view.threads.keys()) if (!rootIds.has(id)) view.threads.delete(id);
    }
    view.snapshotCursor = page.snapshotCursor; view.nextPageToken = page.nextPageToken;
    view.loading = false;
    const pending = view.pendingEvents.splice(0);
    for (const event of pending) applyTimelineEvent(event, false);
    renderNav(); renderContent({ scroll: !append && kind !== "mentions" });
  } catch (error) { if (!signal.aborted) handleReadError(error); }
  finally { if (generation === viewGeneration && view === state.view) view.loading = false; }
}

function applyEvent(event) {
  const object = event.dataObject;
  if (!object?.id) return; // An unknown optional event type can be safely advanced.
  switch (event.eventType) {
    case "user.created": putUsers([object]); renderNav(); break;
    case "channel.created": state.channels = mergeCatalog(state.channels, [object]); renderNav(); break;
    case "session.created": {
      const entry = state.sessionsByChannel.get(object.channelId);
      if (entry?.loaded) { entry.items = mergeCatalog(entry.items, [object]); renderNav(); }
      break;
    }
    case "message.created":
      if (object.targetUserIds?.includes(state.profileId) && state.view.kind !== "mentions") { state.mentionsCount++; renderNav(); }
      queueTimelineEvent(event);
      break;
    case "acknowledgement.created":
      queueTimelineEvent(event);
      if (publicDialog.open && publicList?.kind === "message" && publicList.id === object.messageId) void loadPublicList();
      break;
  }
}

function queueTimelineEvent(event) {
  if (state.view.loading) {
    if (state.view.pendingEvents.length >= 1000) {
      state.view.pendingEvents = []; state.view.activity++; void loadView();
    } else state.view.pendingEvents.push(event);
  } else applyTimelineEvent(event);
}

function applyTimelineEvent(event, render = true) {
  if (event.eventType !== "acknowledgement.created") return applyMessage(event.dataObject, event.cursor, render);
  const view = state.view;
  if (!view.snapshotCursor || compareCursors(event.cursor, view.snapshotCursor) <= 0) return;
  const copies = new Set([...view.roots, ...view.mentions]);
  for (const thread of view.threads.values()) {
    for (const item of [...thread.items, ...thread.contextMessages]) copies.add(item);
  }
  let changed = false;
  for (const item of copies) changed = applyCommittedAcknowledgement(item, event.dataObject, event.cursor) || changed;
  if (changed && render) renderContent();
}

function applyMessage(message, cursor, render = true) {
  const view = state.view;
  if (!message?.id || !view.snapshotCursor) return;
  if (cursor && compareCursors(cursor, view.snapshotCursor) <= 0) return;
  if (view.kind === "mentions") {
    if (!message.targetUserIds?.includes(state.profileId)) return;
    const first = rememberMessage(view, message.id);
    if (!view.atLatest) { if (first) view.activity++; if (render) renderContent(); return; }
    view.mentions = mergeById(view.mentions, [message], 200, true);
  } else {
    if (message.sessionId !== state.sessionId) return;
    const first = rememberMessage(view, message.id);
    if (!message.parentMessageId) {
      if (!view.atLatest && !view.roots.some(item => item.id === message.id)) { if (first) view.activity++; }
      else view.roots = mergeById(view.roots, [message], 200, true);
    } else {
      const root = view.roots.find(item => item.id === message.threadRootMessageId);
      const thread = view.threads.get(message.threadRootMessageId);
      const own = message.authorUserId === state.profileId;
      if (root) countIncomingReply(root, cursor, first, !!thread, own);
      if (thread && (!cursor || compareCursors(cursor, thread.snapshotCursor) > 0)) {
        for (const item of [...thread.items, ...thread.contextMessages]) {
          if (item.id === message.parentMessageId) countIncomingReply(item, cursor, first, true, own);
        }
        if (thread.items.length < 200) thread.items = mergeById(thread.items, [message], 200);
        else if (first && own && view.replyTarget?.id === message.parentMessageId) {
          // Keep the selected parent as context when the bounded reply window moves.
          const parent = view.replyTarget;
          thread.items = appendPageWindow(thread.items, [message], 200);
          if (parent.depth > 1 && !thread.items.some(item => item.id === parent.id) && !thread.contextMessages.some(item => item.id === parent.id)) thread.contextMessages = [...thread.contextMessages.slice(-199), parent];
        } else if (first) view.activity++;
      } else if (!thread && first) view.activity++;
    }
  }
  if (render) renderContent();
}

async function toggleThread(message) {
  if (state.view.threads.has(message.threadRootMessageId)) { state.view.threads.delete(message.threadRootMessageId);
    if (state.view.replyTarget?.threadRootMessageId === message.threadRootMessageId) state.view.replyTarget = null;
    renderContent(); return; }
  await openThread(message.id);
}
async function openThread(messageId) {
  const generation = viewGeneration, sessionId = state.sessionId, view = state.view;
  if (view.loading) return;
  view.loading = true;
  try {
    const result = await api.get("messages/" + encodeURIComponent(messageId) + "/thread", { limit: 50 }, viewAbort?.signal);
    if (generation !== viewGeneration || sessionId !== state.sessionId) return;
    while (state.view.threads.size >= 8) state.view.threads.delete(state.view.threads.keys().next().value);
    result.items = stampMessages(result.items, result.snapshotCursor);
    result.contextMessages = stampMessages(result.contextMessages, result.snapshotCursor);
    state.view.threads.set(result.root.id, result);
    state.view.roots = mergeById(state.view.roots, [{ ...stampMessages([result.root], result.snapshotCursor)[0], newReply: false }], 200, true);
    for (const item of result.items) rememberMessage(state.view, item.id);
    renderContent();
  } catch (error) { if (error.name !== "AbortError") handleReadError(error); }
  finally {
    if (generation === viewGeneration && view === state.view) {
      view.loading = false;
      for (const event of view.pendingEvents.splice(0)) applyTimelineEvent(event, false);
      renderContent();
    }
  }
}
async function loadMoreThread(rootId) {
  const thread = state.view.threads.get(rootId), generation = viewGeneration;
  if (!thread?.nextPageToken) return;
  try {
    const result = await api.get("messages/" + encodeURIComponent(rootId) + "/thread", { asOf: thread.snapshotCursor, pageToken: thread.nextPageToken, limit: 50 }, viewAbort?.signal);
    if (generation !== viewGeneration || thread !== state.view.threads.get(rootId)) return;
    const existing = [...thread.contextMessages, ...thread.items];
    const incoming = [...stampMessages(result.contextMessages, thread.snapshotCursor), ...stampMessages(result.items, result.snapshotCursor)];
    const available = new Map([...incoming, ...existing].map(item => [item.id, item]));
    thread.items = appendPageWindow(thread.items, result.items.map(item => available.get(item.id)), 200);
    const included = new Set(thread.items.map(item => item.id));
    const needed = new Set(thread.items.filter(item => item.parentMessageId && item.parentMessageId !== rootId && !included.has(item.parentMessageId)).map(item => item.parentMessageId));
    thread.contextMessages = [...needed].map(id => available.get(id)).filter(Boolean);
    thread.nextPageToken = result.nextPageToken;
    for (const item of result.items) rememberMessage(state.view, item.id);
    renderContent();
  } catch (error) { if (error.name !== "AbortError") handleReadError(error); }
}
async function beginReply(message) {
  if (message.depth >= 3) { showNotice("A thread has at most three levels."); return; }
  const view = state.view;
  if (!state.profileId) return;
  if (!view.threads.has(message.threadRootMessageId)) await openThread(message.id);
  if (state.view !== view || !view.threads.has(message.threadRootMessageId)) return;
  view.replyTarget = message;
  renderContent();
  $("timeline").querySelector(".thread-reply-text")?.focus();
}

function renderReplyComposer(parent) {
  const value = drafts.get(state.epoch, state.profileId, state.sessionId, parent.id);
  const form = element("form", "thread-composer");
  const label = element("label", "draft-owner", "Replying to " + userLabel(state.users.get(parent.authorUserId), parent.authorUserId));
  const content = parent.content?.text || parent.content?.caption || "Image";
  label.append(element("span", "reply-quote", content.slice(0, 120) + (content.length > 120 ? "..." : "")));
  const text = element("textarea", "thread-reply-text");
  text.id = "thread-reply-text"; text.dataset.parentId = parent.id; label.htmlFor = text.id;
  text.value = value.text; text.rows = 3; text.required = true; text.maxLength = 16384;
  const actions = element("div", "message-actions");
  const send = element("button", "primary", "Send reply"); send.type = "submit";
  const cancel = element("button", "text-button", "Cancel reply"); cancel.type = "button";
  actions.append(send, cancel);
  const error = element("p", "field-error"); error.setAttribute("role", "alert");
  const original = () => [...state.pending.values()].find(item => item.draftKey === value.key && item.draftRevision === value.revision && item.phase !== "history-changed");
  const record = original();
  send.disabled = record?.phase === "sending";
  showError(record?.error, error);
  text.addEventListener("input", () => { value.text = text.value; drafts.touch(value); showError(null, error); send.disabled = false; });
  text.addEventListener("keydown", event => {
    if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) { event.preventDefault(); form.requestSubmit(); }
  });
  cancel.addEventListener("click", () => { state.view.replyTarget = null; renderContent(); });
  form.addEventListener("submit", async event => {
    event.preventDefault();
    try {
      if (!value.text.trim()) throw new Error("Write a reply.");
      let pending = original();
      if (pending?.phase === "sending") return;
      if (!pending) {
        const command = freezeCommand({ type: "message", channel: { mode: "existing", id: state.channelId },
          session: { mode: "existing", id: value.sessionId }, content: { type: "text", text: value.text },
          parentMessageId: parent.id, targetUserIds: [] }, value.callerUserId);
        pending = enqueue(command, { label: "Reply", draftKey: value.key, draftRevision: value.revision, replyRootId: parent.threadRootMessageId });
      }
      send.disabled = true;
      const result = await runCommand(pending);
      if (!result) showError(pending.error, error);
      else if (state.view.replyTarget?.id === parent.id) $("thread-reply-text")?.focus({ preventScroll: true });
    } catch (failure) { showError(failure, error); }
    finally { send.disabled = false; }
  });
  form.append(label, text, actions, error);
  return form;
}
async function navigateMessage(message) {
  try {
    if (!state.channels.some(item => item.id === message.channelId)) state.channels.push(await api.get("channels/" + encodeURIComponent(message.channelId)));
    await selectChannel(message.channelId, message.sessionId);
    await openThread(message.id);
    $("conversation").focus();
  } catch (error) { handleReadError(error); }
}

function clearPublicList() {
  publicList?.abort?.abort();
  publicList = null;
}
function openPublicList(kind, id, name) {
  if (!id || !state.capabilities[publicScopes[kind].capability]) return;
  clearPublicList();
  publicList = { kind, id, page: null, abort: null };
  const message = kind === "message";
  $("public-title").textContent = message ? "Profile acknowledgements" : (name || (kind === "channel" ? "Channel" : "Session")) + " participants";
  $("public-description").textContent = message
    ? "Public profile acknowledgements are not proof that a verified person read this message."
    : kind === "channel"
      ? "The profiles that joined this channel. Rosters are public: membership, not live presence. Leaving removes only your direct membership here."
      : "The profiles that joined this session. Rosters are public: membership, not live presence. Joining also joins the channel; leaving the session keeps your channel membership.";
  $("public-caller").textContent = state.profileId ? "Acting as " + userLabel(currentUser(), state.profileId) : "Select a profile to take action.";
  $("public-join").textContent = message ? "Acknowledge as this profile" : kind === "channel" ? "Join channel directly" : "Join session";
  $("public-leave").textContent = kind === "channel" ? "Leave direct channel membership" : "Leave session";
  $("public-leave").hidden = message;
  setPublicCommandStatus("");
  publicDialog.showModal();
  renderPublicActions();
  void loadPublicList();
}
function pendingPublicAction(list, data) {
  const { key, type } = publicScopes[list.kind];
  return data.type === type && data[key] === list.id;
}
function renderPublicActions() {
  if (!publicList || !publicDialog.open) return;
  const pending = [...state.pending.values()].find(record => record.epoch === state.epoch &&
    record.command.callerUserId === state.profileId && pendingPublicAction(publicList, record.command.envelope.dataObject));
  $("public-actions").hidden = !state.profileId;
  $("public-join").disabled = $("public-leave").disabled = Boolean(pending);
  if (pending) setPublicCommandStatus(pending.phase === "sending" ? "Waiting for the host to commit…" :
    "No commit confirmed. Close this dialog to retry the original command or dismiss it under Commands needing attention.");
}
function setPublicCommandStatus(message) {
  $("public-command-status").textContent = message;
  $("public-command-status").hidden = !message;
}
async function loadPublicList(next = false) {
  const list = publicList;
  if (!list || !publicDialog.open || (next && (!list.page?.nextPageToken || list.abort))) return;
  list.abort?.abort();
  const abort = list.abort = new AbortController();
  if (!next) { list.page = null; $("public-items").replaceChildren(); $("public-items").hidden = true; }
  showError(null, $("public-error"));
  $("public-page-status").textContent = "Loading…";
  $("public-next").hidden = $("public-next").disabled = true;
  try {
    const page = await readPublicPage(api, list, next ? list.page : null, abort.signal);
    if (abort.signal.aborted || publicList !== list || !publicDialog.open) return;
    list.page = page;
    $("public-items").replaceChildren(...page.items.map(item => {
      const row = element("li", "", userLabel(item.profile));
      if (list.kind === "message") {
        const date = new Date(item.acknowledgedUtcMs);
        const time = element("time", "", date.toLocaleString());
        time.dateTime = date.toISOString();
        row.append(" — ", time);
      }
      return row;
    }));
    $("public-items").hidden = !page.items.length;
    $("public-page-status").textContent = page.items.length ? "" : "No entries in this snapshot.";
  } catch (error) {
    if (abort.signal.aborted || publicList !== list || !publicDialog.open) return;
    $("public-page-status").textContent = "";
    showError(error, $("public-error"));
  } finally {
    if (!abort.signal.aborted && publicList === list && publicDialog.open) {
      list.abort = null;
      $("public-next").hidden = !list.page?.nextPageToken;
      $("public-next").disabled = false;
    }
  }
}
async function submitPublicAction(joined) {
  const list = publicList, caller = state.profileId;
  if (!list || !publicDialog.open || !caller || $("public-join").disabled || !state.capabilities[publicScopes[list.kind].capability]) return;
  const label = list.kind === "message" ? "Profile acknowledgement" : (list.kind === "channel" ? "Direct channel" : "Session") + (joined ? " join" : " leave");
  try {
    const command = freezeCommand(publicAction(list.kind, list.id, joined), caller);
    await runCommand(enqueue(command, { label, publicKind: list.kind }));
  } catch (error) { setPublicCommandStatus(formatError(error)); }
}

function renderPending() {
  renderPublicActions();
  $("pending-section").hidden = !state.pending.size;
  const nodes = [];
  for (const record of state.pending.values()) {
    const item = element("div", "pending-item");
    const description = element("div");
    const phaseLabels = { sending: "Sending", uncertain: "Outcome unknown · retry safely", failed: "Not committed", "history-changed": "Sent before the store changed · review required" };
    description.append(element("strong", "", record.label + " · " + (phaseLabels[record.phase] || record.phase)));
    description.append(element("p", "", "Original caller: " + (record.callerLabel || "new user") + (record.sessionLabel ? " · " + record.sessionLabel : "")));
    if (record.error) description.append(element("p", "", formatError(record.error)));
    item.append(description);
    if ((record.phase === "uncertain" || record.phase === "failed") &&
        (!record.publicKind || state.capabilities[publicScopes[record.publicKind].capability])) {
      const retry = element("button", "secondary", "Retry original");
      retry.addEventListener("click", () => runCommand(record)); item.append(retry);
    }
    if (record.phase !== "sending") {
      const dismiss = element("button", "text-button", "Dismiss");
      dismiss.addEventListener("click", () => { state.pending.delete(record.command.requestId); renderPending(); });
      item.append(dismiss);
    }
    nodes.push(item);
  }
  $("pending-list").replaceChildren(...nodes);
}
function enqueue(command, metadata) {
  if (state.pending.size >= 6) throw new Error("Resolve or dismiss a pending command before starting another.");
  const record = { command, ...metadata, phase: "sending", epoch: state.epoch, callerLabel: userLabel(state.users.get(command.callerUserId), command.callerUserId || ""), selectedProfileAtStart: state.profileId, error: null };
  state.pending.set(command.requestId, record); renderPending();
  return record;
}
async function runCommand(record) {
  if (record.phase === "history-changed" || (record.publicKind && !state.capabilities[publicScopes[record.publicKind].capability])) return;
  record.phase = "sending"; record.error = null; renderPending();
  try {
    const receipt = await api.send(record.command);
    record.phase = "committed";
    state.pending.delete(record.command.requestId);
    if (record.draftKey) drafts.clearIfUnchanged(record.draftKey, record.draftRevision);
    const objects = receipt.dataObjects || {};
    if (receipt.storeEpoch !== state.epoch) { showNotice("Saved just after the local store changed. Reloading the view."); void bootstrap(state.profileId); return receipt; }
    if (record.publicKind) {
      const channelJoined = objects.participation?.channelJoined;
      const membership = typeof channelJoined === "boolean" ? (channelJoined ? " At commit, the profile was on the channel roster." : " At commit, the profile was not on the channel roster.") : "";
      const status = record.label + " committed." + membership;
      renderPending();
      showNotice(status);
      if (publicDialog.open && publicList) {
        const matchesScope = pendingPublicAction(publicList, record.command.envelope.dataObject);
        if (matchesScope && record.command.callerUserId === state.profileId) setPublicCommandStatus(status);
        if (matchesScope || (publicList.kind === "channel" && objects.participation?.channelId === publicList.id)) void loadPublicList();
      }
      return receipt;
    }
    if (objects.user) putUsers([objects.user]);
    if (objects.channel) state.channels = mergeCatalog(state.channels, [objects.channel]);
    if (objects.session) {
      const entry = state.sessionsByChannel.get(objects.session.channelId);
      if (entry) entry.items = mergeCatalog(entry.items, [objects.session]);
    }
    if (objects.message) {
      const event = { dataObject: objects.message, cursor: receipt.storeEpoch + ":" + objects.message.sequence };
      queueTimelineEvent(event);
    }
    if (record.replyRootId) renderContent();
    renderNav(); renderPending(); renderComposer();
    showNotice(record.label + " saved.");
    // Navigation failure does not turn an acknowledged commit into an unknown command.
    if (objects.user && record.selectedProfileAtStart === state.profileId) void bootstrap(objects.user.id);
    else if (objects.session && !objects.message && record.command.callerUserId === state.profileId) void selectChannel(objects.channel?.id || objects.session.channelId, objects.session.id);
    return receipt;
  } catch (error) {
    record.phase = error.uncertain || error.status === 429 ? "uncertain" : "failed";
    record.error = error; renderPending();
    return null;
  }
}

function openDialog(id, overrides = {}) {
  const dialog = $(id); showError(null, dialog.querySelector(".field-error"));
  dialogContexts.set(dialog, { callerUserId: state.profileId, channelId: state.channelId, ...overrides });
  dialog.showModal();
}
function wireDialog(formId, dialogId, makeObject, label) {
  $(formId).addEventListener("submit", async event => {
    event.preventDefault();
    const form = event.currentTarget, button = form.querySelector('button[type="submit"]');
    button.disabled = true; showError(null, form.querySelector(".field-error"));
    try {
      const context = dialogContexts.get($(dialogId)) || { callerUserId: state.profileId, channelId: state.channelId };
      const object = makeObject(new FormData(form), context);
      const caller = object.type === "user" ? null : context.callerUserId;
      if (object.type !== "user" && !caller) throw new Error("Select a user first.");
      let record = formCommands.get(form);
      if (!record || record.phase === "committed" || record.phase === "history-changed" || record.command.callerUserId !== caller || JSON.stringify(record.command.envelope.dataObject) !== JSON.stringify(object)) {
        record = enqueue(freezeCommand(object, caller), { label });
        formCommands.set(form, record);
      } else if (!state.pending.has(record.command.requestId)) {
        state.pending.set(record.command.requestId, record);
      }
      const result = await runCommand(record);
      if (result) { $(dialogId).close(); form.reset(); formCommands.delete(form); }
      else showError(record.error, form.querySelector(".field-error"));
    } catch (error) { showError(error, form.querySelector(".field-error")); }
    finally { button.disabled = false; }
  });
}
for (const close of document.querySelectorAll(".close-dialog")) close.addEventListener("click", () => close.closest("dialog").close());
publicDialog.addEventListener("close", () => { if (!publicDialog.open) clearPublicList(); });
$("channel-participants").addEventListener("click", () => openPublicList("channel", state.channelId, state.channels.find(item => item.id === state.channelId)?.name));
$("session-participants").addEventListener("click", () => openPublicList("session", state.sessionId, sessionsFor(state.channelId).find(item => item.id === state.sessionId)?.title));
$("public-join").addEventListener("click", () => submitPublicAction(true));
$("public-leave").addEventListener("click", () => submitPublicAction(false));
$("public-refresh").addEventListener("click", () => loadPublicList());
$("public-next").addEventListener("click", () => loadPublicList(true));
wireDialog("user-form", "user-dialog", fields => ({ type: "user", handle: fields.get("handle"), displayName: fields.get("displayName") }), "User");
wireDialog("channel-form", "channel-dialog", fields => ({ type: "channel", name: fields.get("name"), description: fields.get("description") || null, initialSessionTitle: fields.get("initialSessionTitle") || null }), "Channel and first session");
wireDialog("session-form", "session-dialog", (fields, context) => ({ type: "session", channel: { mode: "existing", id: context.channelId }, title: fields.get("title") }), "Session");
$("new-user").addEventListener("click", () => openDialog("user-dialog"));
$("empty-create-user").addEventListener("click", () => openDialog("user-dialog"));
$("new-channel").addEventListener("click", () => openDialog("channel-dialog"));
$("profile").addEventListener("change", async event => {
  if (event.target.value === "__more__") {
    event.target.value = state.profileId || "";
    try {
      const page = await api.get("users", { asOf: state.catalogSnapshot, pageToken: state.userPage, limit: 200 });
      putUsers(page.items); state.userPage = page.nextPageToken; renderProfiles();
    } catch (error) { handleReadError(error); }
    return;
  }
  const id = event.target.value || null;
  showError(null, $("composer-error")); hideSuggestions();
  void bootstrap(id);
});
$("reconnect").addEventListener("click", () => bootstrap(state.profileId));
$("refresh-view").addEventListener("click", () => loadView());
$("show-latest").addEventListener("click", () => loadView());
$("more-messages").addEventListener("click", () => loadView({ append: true }));
$("mentions").addEventListener("click", async () => {
  state.view = newView(); state.view.kind = "mentions"; state.mentionsCount = 0;
  renderAll(); await loadView();
});
$("more-channels").addEventListener("click", async () => {
  try {
    const page = await api.get("channels", { asOf: state.catalogSnapshot, pageToken: state.channelPage, limit: 50 });
    state.channels = mergeCatalog(state.channels, page.items); state.channelPage = page.nextPageToken; renderNav();
  } catch (error) { handleReadError(error); }
});
$("message-text").addEventListener("input", () => {
  const value = draft(); if (!value) return;
  value.text = $("message-text").value; drafts.touch(value); updateCount(); showError(null, $("composer-error"));
});
$("message-text").addEventListener("keydown", event => {
  const popup = $("target-suggestions");
  if (!popup.hidden && ["ArrowDown", "ArrowUp", "Escape", "Enter"].includes(event.key)) {
    const options = [...popup.querySelectorAll("button")];
    const index = options.indexOf(document.activeElement);
    if (event.key === "Escape") { event.preventDefault(); hideSuggestions(); return; }
    if (event.key === "ArrowDown") { event.preventDefault(); (options[index + 1] || options[0])?.focus(); return; }
    if (event.key === "ArrowUp") { event.preventDefault(); (options[index - 1] || options[options.length - 1])?.focus(); return; }
    if (event.key === "Enter" && !event.ctrlKey && !event.metaKey && options.length) { event.preventDefault(); options[index >= 0 ? index : 0].click(); return; }
  }
  if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) { event.preventDefault(); $("composer").requestSubmit(); }
});
$("image-file").addEventListener("change", async event => {
  const file = event.target.files[0], value = draft();
  event.target.value = "";
  if (!file || !value) return;
  try {
    if (!["image/png", "image/jpeg"].includes(file.type)) throw new Error("Choose one PNG or JPEG image.");
    if (file.size > 8 * 1024 * 1024) throw new Error("The image must be at most 8 MiB.");
    if (value.text.length > 4096) throw new Error("Reduce the caption to 4,096 characters before attaching an image.");
    // The Blob owns a copy; retries do not reread a subsequently modified filesystem file.
    value.image = new Blob([await file.arrayBuffer()], { type: file.type }); value.imageName = file.name;
    drafts.touch(value); renderComposer(); showError(null, $("composer-error"));
  } catch (error) { showError(error, $("composer-error")); }
});
$("remove-image").addEventListener("click", () => { const value = draft(); if (value) { value.image = null; value.imageName = ""; drafts.touch(value); renderComposer(); } });
$("composer").addEventListener("submit", async event => {
  event.preventDefault();
  const value = draft(); if (!value || !state.channelId) return;
  showError(null, $("composer-error"));
  try {
    if (!value.image && !value.text.trim()) throw new Error("Write a message or choose an image.");
    if (value.text.length > (value.image ? 4096 : 16384)) throw new Error("This content exceeds its length limit.");
    const object = {
      type: "message", channel: { mode: "existing", id: state.channelId }, session: { mode: "existing", id: value.sessionId },
      content: value.image ? { type: "image", caption: value.text || null } : { type: "text", text: value.text },
      parentMessageId: null, targetUserIds: [...value.targetUserIds]
    };
    let record = [...state.pending.values()].find(item => item.draftKey === value.key && item.draftRevision === value.revision && item.phase !== "history-changed");
    if (record?.phase === "sending") return;
    if (!record) {
      const command = freezeCommand(object, value.callerUserId, value.image);
      record = enqueue(command, { label: value.image ? "Image message" : "Message", draftKey: value.key, draftRevision: value.revision, sessionLabel: sessionsFor(state.channelId).find(s => s.id === value.sessionId)?.title });
    }
    $("send").disabled = true;
    const result = await runCommand(record);
    if (!result && state.profileId === value.callerUserId && state.sessionId === value.sessionId) showError(record.error, $("composer-error"));
    if (result) $("timeline").scrollTop = $("timeline").scrollHeight;
  } catch (error) { showError(error, $("composer-error")); }
  finally { $("send").disabled = false; }
});

function hideSuggestions() {
  clearTimeout(suggestTimer); suggestAbort?.abort();
  $("target-suggestions").hidden = true; $("target-suggestions").replaceChildren();
  $("message-text").setAttribute("aria-expanded", "false");
}
function mentionToken() {
  const field = $("message-text");
  const match = /(^|[\s(])@([a-z][a-z0-9_]*)$/i.exec(field.value.slice(0, field.selectionStart));
  return match ? { handle: match[2], start: field.selectionStart - match[2].length - 1 } : null;
}
function applyMention(value, user) {
  const field = $("message-text"), token = mentionToken();
  hideSuggestions();
  if (!token || draft() !== value) return;
  if (value.targetUserIds.length >= 20) { showError(new Error("Choose at most 20 distinct target users."), $("composer-error")); return; }
  const rest = field.value.slice(field.selectionStart);
  const spacing = /^[\s]/.test(rest) ? "" : " ";
  value.text = field.value.slice(0, token.start) + "@" + user.handle + spacing + rest;
  if (!value.targetUserIds.includes(user.id)) value.targetUserIds.push(user.id);
  drafts.touch(value); renderComposer();
  const caret = token.start + user.handle.length + 1 + spacing.length;
  $("message-text").focus(); $("message-text").setSelectionRange(caret, caret);
}
$("message-text").addEventListener("input", () => {
  hideSuggestions();
  const token = mentionToken();
  if (!token) return;
  suggestTimer = setTimeout(async () => {
    suggestAbort = new AbortController();
    const signal = suggestAbort.signal, value = draft();
    try {
      const page = await api.get("users", { handlePrefix: token.handle.toLowerCase(), limit: 20 }, signal);
      if (signal.aborted || draft() !== value || !mentionToken()) return;
      putUsers(page.items);
      const buttons = page.items.filter(user => !value.targetUserIds.includes(user.id)).map(user => {
        const button = element("button", "", userLabel(user)); button.type = "button"; button.setAttribute("role", "option");
        button.addEventListener("click", () => applyMention(value, user));
        return button;
      });
      $("target-suggestions").replaceChildren(...(buttons.length ? buttons : [element("p", "section-note", "No matching users.")]));
      $("target-suggestions").hidden = false;
      $("message-text").setAttribute("aria-expanded", "true");
    } catch (error) { if (!signal.aborted) showError(error, $("composer-error")); }
  }, 180);
});
document.addEventListener("click", event => { if (!event.target.closest(".suggestions") && event.target !== $("message-text")) hideSuggestions(); });
$("theme").addEventListener("click", () => {
  const current = document.documentElement.dataset.theme || (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
  const next = current === "dark" ? "light" : "dark";
  document.documentElement.dataset.theme = next; safePreference(local, preferenceKey + ":theme", next);
});
const theme = safePreference(local, preferenceKey + ":theme");
if (theme === "dark" || theme === "light") document.documentElement.dataset.theme = theme;
// Deliberately no storage listener: another tab cannot replace this tab's active identity.
addEventListener("pagehide", () => { live?.stop(); bootAbort?.abort(); viewAbort?.abort(); images.clear(); clearPreview(); });
void bootstrap();
