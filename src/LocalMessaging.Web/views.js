import { compareSequence } from "./state.js";

export function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined && text !== null) node.textContent = text;
  return node;
}

export function formatError(error) {
  const fields = Object.entries(error.errors || {}).flatMap(([key, values]) =>
    (Array.isArray(values) ? values : [values]).map(value => key + ": " + value));
  return [error.message || "The request could not complete.", ...fields, error.traceId ? "Diagnostic ID: " + error.traceId : ""].filter(Boolean).join("\n");
}

export function userLabel(user, id = "") { return user ? user.displayName + " · @" + user.handle : "User " + id.slice(0, 8); }

export class ImageViews {
  constructor(api) { this.api = api; this.generation = 0; this.items = new Set(); this.urls = new Map(); this.queue = []; this.active = 0; this.observer = null; }
  clear() {
    this.generation++;
    this.observer?.disconnect(); this.observer = null;
    for (const item of this.items) item.abort?.abort();
    for (const url of this.urls.values()) URL.revokeObjectURL(url);
    this.items.clear(); this.urls.clear(); this.queue = []; this.active = 0;
  }
  release(item) {
    item.abort?.abort();
    if (this.urls.has(item)) { URL.revokeObjectURL(this.urls.get(item)); this.urls.delete(item); }
    item.loaded = false;
    item.placeholder.textContent = "Image · scroll to load";
    item.container.replaceChildren(item.placeholder);
  }
  append(container, descriptor, caption) {
    const placeholder = element("div", "image-placeholder", "Image · scroll to load");
    container.append(placeholder);
    const item = { container, placeholder, descriptor, caption, generation: this.generation, visible: false, pending: false, loaded: false, failed: false, abort: null };
    this.items.add(item);
    if (typeof IntersectionObserver === "function") {
      if (!this.observer) this.observer = new IntersectionObserver(entries => {
        for (const entry of entries) {
          const current = [...this.items].find(value => value.container === entry.target);
          if (!current) continue;
          current.visible = entry.isIntersecting;
          if (current.visible) this.enqueue(current);
          else this.release(current);
        }
        this.pump();
      }, { root: document.getElementById("timeline"), rootMargin: "150px" });
      this.observer.observe(container);
    } else {
      const button = element("button", "text-button", "Load image");
      button.addEventListener("click", () => { item.visible = true; item.failed = false; this.enqueue(item); });
      placeholder.replaceChildren(button);
    }
  }
  enqueue(item) {
    if (!item.visible || item.pending || item.loaded || item.failed || item.generation !== this.generation || this.queue.includes(item)) return;
    this.queue.push(item); this.pump();
  }
  pump() {
    // Only near-viewport images are loaded; decoded/object-URL retention is bounded separately from message pages.
    while (this.active < 4 && this.urls.size + this.active < 24 && this.queue.length) {
      const item = this.queue.shift();
      if (!item.visible || item.generation !== this.generation || item.loaded || item.pending) continue;
      item.pending = true; item.abort = new AbortController(); this.active++;
      const signal = item.abort.signal;
      item.placeholder.textContent = "Loading image…";
      this.api.image(item.descriptor.contentPath, signal).then(blob => {
        if (signal.aborted || !item.visible || item.generation !== this.generation) return;
        const url = URL.createObjectURL(blob); this.urls.set(item, url); item.loaded = true;
        const image = element("img");
        image.alt = item.caption || "Image message"; image.src = url; image.loading = "lazy";
        if (item.descriptor.width) image.width = item.descriptor.width;
        if (item.descriptor.height) image.height = item.descriptor.height;
        image.addEventListener("error", () => {
          this.release(item); item.failed = true; item.placeholder.textContent = "The image could not be displayed.";
        }, { once: true });
        item.container.replaceChildren(image);
      }).catch(error => {
        if (!signal.aborted && item.generation === this.generation) {
          item.failed = true; item.placeholder.textContent = "Image unavailable. " + error.message;
          const retry = element("button", "text-button", "Retry image");
          retry.addEventListener("click", () => { item.failed = false; this.enqueue(item); }); item.placeholder.append(retry);
        }
      }).finally(() => {
        if (item.generation !== this.generation) return;
        this.active--; item.pending = false;
        if (signal.aborted && item.visible) this.enqueue(item);
        this.pump();
      });
    }
  }
}

export function renderNavigationTree(container, model, actions) {
  const fragment = document.createDocumentFragment();
  if (!model.channels.length) fragment.append(element("p", "section-note", "Nothing here yet."));
  for (const channel of model.channels) {
    const expanded = model.expanded.has(channel.id);
    const group = element("div", "tree-group");
    const row = element("button", "tree-row channel-row");
    row.type = "button"; row.setAttribute("aria-expanded", String(expanded));
    row.setAttribute("aria-current", String(channel.id === model.channelId));
    const chevron = element("span", "tree-chevron", "▸"); chevron.setAttribute("aria-hidden", "true");
    const channelLabels = element("div", "tree-labels");
    channelLabels.append(element("strong", "", "# " + channel.name), element("small", "", model.owner(channel.ownerUserId)));
    row.append(chevron, channelLabels);
    const known = model.sessionsByChannel.get(channel.id);
    if (!expanded && known?.loaded && known.items.length) row.append(element("span", "tree-count", String(known.items.length)));
    row.addEventListener("click", () => actions.chooseChannel(channel.id));
    group.append(row);
    if (expanded) {
      const children = element("div", "tree-children");
      const entry = model.sessionsByChannel.get(channel.id);
      if (entry?.loading) children.append(element("p", "section-note", "Loading sessions…"));
      else if (!entry?.items.length) children.append(element("p", "section-note", "No sessions yet."));
      for (const session of entry?.items || []) {
        const child = element("button", "tree-row session-row");
        child.type = "button"; child.setAttribute("aria-current", String(session.id === model.sessionId));
        const sessionLabels = element("div", "tree-labels");
        sessionLabels.append(element("strong", "", session.title), element("small", "", model.owner(session.ownerUserId)));
        child.append(sessionLabels);
        child.addEventListener("click", () => actions.chooseSession(channel.id, session.id));
        children.append(child);
      }
      const add = element("button", "small-button tree-action session-add", "+ New session");
      add.type = "button"; add.disabled = !model.profileId;
      add.title = model.profileId ? "Create session in # " + channel.name : "Select a user to create sessions.";
      add.addEventListener("click", () => actions.newSession(channel.id));
      children.append(add);
      if (entry?.page) {
        const more = element("button", "load-button", "Load more sessions");
        more.type = "button"; more.disabled = entry.loading;
        more.addEventListener("click", () => actions.moreSessions(channel.id));
        children.append(more);
      }
      group.append(children);
    }
    fragment.append(group);
  }
  container.replaceChildren(fragment);
  const current = container.querySelector('.session-row[aria-current="true"]') || container.querySelector('.channel-row[aria-current="true"]');
  current?.scrollIntoView({ block: "nearest" });
}

export function renderTimeline(container, state, hooks, images) {
  images.clear();
  const sessionTitles = new Map([...(state.sessionsByChannel?.values() || [])].flatMap(entry => entry.items).map(session => [session.id, session.title]));
  const fragment = document.createDocumentFragment();
  const items = state.view.kind === "mentions" ? state.view.mentions : [...state.view.roots].sort(compareSequence);
  if (!items.length) {
    const empty = element("div", "empty-state");
    empty.append(element("div", "empty-symbol", state.view.kind === "mentions" ? "@" : "✦"));
    const selected = Boolean(state.profileId);
    empty.append(element("h2", "", state.view.kind === "mentions" ? "You're all caught up" : state.sessionId ? "Give this conversation a beginning" : selected ? "Choose a conversation" : "Start with a local user"));
    empty.append(element("p", "", state.view.kind === "mentions" ? "Messages that target your handle or user ID appear here. They remain visible in public conversation history." : state.sessionId ? "Send the first text or image. Everyone can take part." : selected ? "Select a channel and session, or create a new channel." : "Create a handle and display name, or select an existing profile."));
    if (!selected) {
      const button = element("button", "primary", "Create user");
      button.addEventListener("click", hooks.createUser); empty.append(button);
    }
    fragment.append(empty);
  }
  function messageCard(message, { context = false, mention = false, showThread = false } = {}) {
    const card = element("article", "message");
    card.dataset.messageId = message.id; card.dataset.depth = String(message.depth);
    const user = state.users.get(message.authorUserId);
    const head = element("div", "message-head");
    head.append(element("span", "avatar", (user?.displayName || user?.handle || "?").slice(0, 2).toUpperCase()));
    head.append(element("span", "author", userLabel(user, message.authorUserId)));
    const time = element("time");
    time.dateTime = message.createdAtUtc;
    const date = new Date(message.createdAtUtc);
    time.textContent = Number.isNaN(date.valueOf()) ? "" : date.toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
    time.title = message.createdAtUtc || "";
    head.append(time);
    if (message.depth > 1) head.append(element("span", "level-label", "Level " + message.depth + " of 3"));
    card.append(head);
    if (context) card.append(element("div", "context-label", "Parent context"));
    const content = message.content || {};
    const text = content.type === "image" ? content.caption : content.text;
    if (text) card.append(element("div", "message-body", text));
    if (content.type === "image" && content.image?.contentPath) {
      const box = element("div", "message-image");
      card.append(box); images.append(box, content.image, content.caption);
    }
    if (message.targetUserIds?.length) {
      const targets = element("div", "message-targets");
      for (const id of message.targetUserIds) targets.append(element("span", "chip", "@" + (state.users.get(id)?.handle || id.slice(0, 8))));
      card.append(targets);
    }
    const actions = element("div", "message-actions");
    if (mention) {
      card.append(element("div", "message-location", sessionTitles.get(message.sessionId) || "Session " + message.sessionId.slice(0, 8)));
      const navigate = element("button", "text-button", "Open conversation →");
      navigate.addEventListener("click", () => hooks.navigate(message)); actions.append(navigate);
    } else {
      const reply = element("button", "text-button reply-button" + (message.newReply ? " new-reply" : ""), "Reply");
      const count = message.replyCount ?? 0;
      if (count) {
        const badge = element("span", "reply-count", String(count));
        badge.setAttribute("aria-hidden", "true"); reply.append(badge);
      }
      if (message.newReply) reply.append(element("span", "reply-new-label", "New reply"));
      reply.setAttribute("aria-label", `Reply${count ? ` (${count} ${count === 1 ? "reply" : "replies"})` : ""}${message.newReply ? ", New reply" : ""}`);
      reply.disabled = !state.profileId || message.depth >= 3;
      if (message.depth >= 3) reply.title = "A thread has at most three levels.";
      reply.addEventListener("click", () => hooks.reply(message)); actions.append(reply);
      if (showThread) {
        const opened = state.view.threads.has(message.id);
        const thread = element("button", "text-button", opened ? "Hide thread ?" : "Open thread ?");
        thread.setAttribute("aria-expanded", String(opened));
        thread.addEventListener("click", () => hooks.thread(message)); actions.append(thread);
      }
    }
    if (state.capabilities?.profileAcknowledgements) {
      const count = message.acknowledgementCount || 0;
      const handle = message.latestAcknowledgingHandle;
      const label = count ? `Acknowledged${handle ? ` by @${handle}` : ""}${count > 1 ? ` +${count - 1}` : ""}` : "Profile acknowledgements";
      const acknowledgements = element("button", "text-button" + (count ? " acknowledged" : ""), label);
      if (count) acknowledgements.setAttribute("aria-label", `${label}. View profile acknowledgements (${count})`);
      acknowledgements.addEventListener("click", () => hooks.acknowledgements(message));
      actions.append(acknowledgements);
    }
    card.append(actions);
    return card;
  }
  for (const item of items) {
    const card = messageCard(item, { mention: state.view.kind === "mentions", showThread: state.view.kind !== "mentions" });
    const thread = state.view.threads.get(item.id);
    if (thread && state.view.kind !== "mentions") {
      const threadBox = element("div", "thread");
      const replies = new Map([...thread.contextMessages, ...thread.items].map(m => [m.id, m]));
      const resultIds = new Set(thread.items.map(m => m.id));
      function children(parentId) {
        for (const reply of [...replies.values()].filter(m => m.parentMessageId === parentId).sort(compareSequence)) {
          threadBox.append(messageCard(reply, { context: !resultIds.has(reply.id) }));
          if (reply.depth < 3) children(reply.id);
        }
      }
      children(item.id);
      if (!replies.size) threadBox.append(element("p", "section-note", "No replies yet."));
      if (thread.nextPageToken) {
        const more = element("button", "load-button", "Load more replies");
        more.addEventListener("click", () => hooks.moreThread(item.id)); threadBox.append(more);
      }
      if (thread.items.length >= 200) threadBox.append(element("p", "section-note", "Showing a 200-reply window. Continue paging, or reopen the thread to start again."));
      if (state.view.replyTarget?.threadRootMessageId === item.id) threadBox.append(hooks.replyComposer(state.view.replyTarget));
      card.append(threadBox);
    }
    fragment.append(card);
  }
  container.replaceChildren(fragment);
}
