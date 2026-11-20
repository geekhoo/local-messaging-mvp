import { ApiError, CLIENT_HEADER, CLIENT_VERSION, responseError } from "./api.js";
import { EventPosition } from "./state.js";

// Incremental SSE framing. Frames dispatch only at a blank line; truncated EOF is discarded.
export class SseParser {
  constructor(maxBytes = 256 * 1024) {
    this.decoder = new TextDecoder("utf-8", { fatal: true });
    this.encoder = new TextEncoder();
    this.maxBytes = maxBytes;
    this.line = ""; this.skipLf = false; this.started = false; this.resetFrame();
  }
  resetFrame() { this.data = []; this.event = ""; this.id = null; this.retry = null; this.frameBytes = 0; }
  push(bytes, final = false) {
    const text = this.decoder.decode(bytes, { stream: !final });
    const frames = [];
    for (let i = 0; i < text.length; i++) {
      const character = text[i];
      if (!this.started) { this.started = true; if (character === "\uFEFF") continue; }
      if (this.skipLf) { this.skipLf = false; if (character === "\n") continue; }
      if (character === "\r" || character === "\n") {
        this.processLine(frames);
        this.line = "";
        this.skipLf = character === "\r";
      } else {
        this.line += character;
        // UTF-16 count bounds the temporary buffer; final UTF-8 accounting is exact per line.
        if (this.line.length > this.maxBytes) throw new Error("An SSE line exceeds the frame limit.");
      }
    }
    if (final) { this.line = ""; this.resetFrame(); }
    return frames;
  }
  processLine(frames) {
    this.frameBytes += this.encoder.encode(this.line).byteLength + 1;
    if (this.frameBytes > this.maxBytes) throw new Error("An SSE frame exceeds the frame limit.");
    if (this.line === "") {
      if (this.data.length) frames.push({ event: this.event || "message", data: this.data.join("\n"), id: this.id, retry: this.retry });
      this.resetFrame(); return;
    }
    if (this.line.startsWith(":")) return;
    const colon = this.line.indexOf(":");
    const field = colon < 0 ? this.line : this.line.slice(0, colon);
    let value = colon < 0 ? "" : this.line.slice(colon + 1);
    if (value.startsWith(" ")) value = value.slice(1);
    switch (field) {
      case "data": this.data.push(value); break;
      case "event": this.event = value; break;
      case "id": if (!value.includes("\0")) this.id = value; break;
      case "retry": if (/^[0-9]+$/.test(value)) this.retry = Math.min(Number(value), 300000); break;
    }
  }
}

export function pause(ms, signal) {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) { reject(signal.reason); return; }
    const end = () => { signal?.removeEventListener("abort", cancel); resolve(); };
    const timer = setTimeout(end, ms);
    const cancel = () => { clearTimeout(timer); signal?.removeEventListener("abort", cancel); reject(signal.reason); };
    signal?.addEventListener("abort", cancel, { once: true });
  });
}

export class LiveSubscription {
  constructor(api, descriptor, { onEvent, onState, onReset, onFatal }, options = {}) {
    this.api = api; this.subscription = descriptor;
    this.onEvent = onEvent; this.onState = onState; this.onReset = onReset; this.onFatal = onFatal;
    this.position = new EventPosition(descriptor.startCursor);
    this.stopController = new AbortController();
    this.idleMs = options.idleMs || 45000;
    this.retryBaseMs = options.retryBaseMs || 500;
    this.running = null;
  }
  start() {
    if (!this.running) this.running = this.run();
    return this.running;
  }
  stop() { this.stopController.abort(new DOMException("View closed", "AbortError")); }
  async run() {
    let failures = 0;
    while (!this.stopController.signal.aborted) {
      const attempt = new AbortController();
      const cancelled = () => attempt.abort(this.stopController.signal.reason);
      this.stopController.signal.addEventListener("abort", cancelled, { once: true });
      let watchdog;
      const alive = () => { clearTimeout(watchdog); watchdog = setTimeout(() => attempt.abort(new DOMException("Stream idle timeout", "TimeoutError")), this.idleMs); };
      let reader;
      let retryAfter = 0;
      let received = false;
      try {
        this.onState(failures ? "reconnecting" : "starting");
        alive();
        const response = await this.api.fetch(this.api.url("subscriptions/" + encodeURIComponent(this.subscription.id) + "/stream", { after: this.position.cursor }), {
          headers: { [CLIENT_HEADER]: CLIENT_VERSION, Accept: "text/event-stream" },
          signal: attempt.signal, cache: "no-store", credentials: "omit", mode: "same-origin"
        });
        if (!response.ok) throw await responseError(response);
        if (!(response.headers.get("Content-Type") || "").toLowerCase().startsWith("text/event-stream") || !response.body) throw new ApiError({ code: "invalid_stream", detail: "The host returned an invalid event stream." });
        this.onState("connected");
        const parser = new SseParser();
        reader = response.body.getReader();
        for (;;) {
          const { value, done } = await reader.read();
          if (done) { parser.push(new Uint8Array(), true); break; }
          if (value.byteLength) { alive(); received = true; }
          let frames;
          try { frames = parser.push(value); }
          catch { throw new ApiError({ code: "invalid_stream", detail: "The event stream contains invalid UTF-8 or an oversized frame." }); }
          for (const frame of frames) {
            let data;
            try { data = JSON.parse(frame.data); } catch { throw new ApiError({ code: "invalid_stream", detail: "The event stream contains malformed JSON." }); }
            if (data.contractVersion !== 1) throw new ApiError({ code: "unsupported_contract_version", detail: "Update this client to read the host's event contract." });
            if (frame.event === "stream.closed") throw new ApiError({ status: 410, code: "subscription_closed", detail: "The subscription was closed." });
            if (["store_changed", "cursor_ahead_of_store", "subscription_closed"].includes(frame.event)) {
              throw new ApiError({ status: frame.event === "subscription_closed" ? 410 : 409, code: frame.event, detail: "The saved stream position needs a fresh snapshot." });
            }
            if (["database_busy", "storage_unavailable", "host_not_ready"].includes(frame.event)) {
              // The host may report a temporary failure after SSE headers, then close this socket.
              // Reconnect with the same consumer cursor; the saved subscription is still active.
              throw new ApiError({ status: 503, code: frame.event, detail: "The local host temporarily interrupted the event stream." });
            }
            if (frame.event === "server.stopping") throw new ApiError({ status: 503, code: "server_stopping", detail: "The local host is stopping." });
            if (frame.event === "stream.checkpoint") {
              if (!frame.id || frame.id !== data.cursor) throw new ApiError({ code: "invalid_stream", detail: "The stream checkpoint has inconsistent cursors." });
              this.position.advance(frame.id);
              continue;
            }
            if (!frame.id || (data.cursor && frame.id !== data.cursor)) throw new ApiError({ code: "invalid_stream", detail: "An event has an inconsistent cursor." });
            if (this.position.shouldApply(frame.id)) {
              await this.onEvent({ ...data, eventType: data.eventType || frame.event, cursor: frame.id });
              this.position.advance(frame.id);
            }
          }
        }
      } catch (error) {
        if (this.stopController.signal.aborted) break;
        if (error instanceof ApiError && error.needsResync) { this.onReset(error); return; }
        if (error instanceof ApiError && (["unsupported_contract_version", "invalid_stream"].includes(error.code) || (error.status >= 400 && error.status < 500 && error.status !== 429))) {
          this.onState("update-required", error.message); this.onFatal(error); return;
        }
        // Parsing/order failures are protocol failures, not an endless network retry.
        if (!(error instanceof ApiError) && error.name !== "AbortError" && error.name !== "TimeoutError" && !(error instanceof TypeError)) {
          this.onState("unavailable", error.message); this.onFatal(error); return;
        }
        retryAfter = error.retryAfterMs || 0;
        this.onState("reconnecting", error.message);
      } finally {
        clearTimeout(watchdog);
        attempt.abort();
        try { await reader?.cancel(); } catch { /* A disconnected reader is already released. */ }
        reader?.releaseLock();
        this.stopController.signal.removeEventListener("abort", cancelled);
      }
      if (received) failures = 0;
      const backoff = Math.min(15000, this.retryBaseMs * 2 ** Math.min(failures++, 5));
      this.onState("reconnecting");
      try { await pause(Math.max(retryAfter, Math.round(backoff * (.8 + Math.random() * .4))), this.stopController.signal); } catch { break; }
    }
  }
}
