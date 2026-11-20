export const CLIENT_HEADER = "X-Local-Messaging-Client";
export const CLIENT_VERSION = "1";

export class ApiError extends Error {
  constructor(problem, retryAfterMs = 0) {
    super(problem.detail || problem.title || "The local host could not complete the request.");
    this.name = "ApiError";
    this.status = problem.status || 0;
    this.code = problem.code || "request_failed";
    this.errors = problem.errors || {};
    this.traceId = problem.traceId || null;
    this.retryAfterMs = retryAfterMs;
  }
  get uncertain() { return ["connection_lost", "invalid_command_response"].includes(this.code) || this.status >= 500; }
  get needsResync() { return ["store_changed", "cursor_ahead_of_store", "subscription_closed"].includes(this.code); }
}

export function retryAfterMilliseconds(value, now = Date.now()) {
  if (!value) return 0;
  const seconds = Number(value);
  if (Number.isFinite(seconds) && seconds >= 0) return Math.min(seconds * 1000, 300000);
  const date = Date.parse(value);
  return Number.isNaN(date) ? 0 : Math.min(Math.max(0, date - now), 300000);
}

export async function responseError(response) {
  let problem;
  try { problem = await response.json(); } catch { problem = { title: "Local host request failed", status: response.status }; }
  return new ApiError({ ...problem, status: response.status }, retryAfterMilliseconds(response.headers.get("Retry-After")));
}

export function deepFreeze(value) {
  if (value && typeof value === "object" && !(value instanceof Blob)) {
    for (const item of Object.values(value)) deepFreeze(item);
    Object.freeze(value);
  }
  return value;
}

export function freezeCommand(dataObject, callerUserId, image = null, requestId = crypto.randomUUID()) {
  const envelope = deepFreeze(structuredClone({
    contractVersion: 1, requestId, callerUserId: callerUserId || null, dataObject
  }));
  return Object.freeze({
    requestId, callerUserId: callerUserId || null, envelope,
    json: JSON.stringify(envelope),
    image: image ? image.slice(0, image.size, image.type) : null
  });
}

export class Api {
  constructor(base = "/api/v1/", fetchImpl = globalThis.fetch.bind(globalThis)) {
    this.base = base.endsWith("/") ? base : base + "/";
    this.fetch = fetchImpl;
  }
  url(path, query = {}) {
    if (!path || path.startsWith("/") || /^[a-z][a-z0-9+.-]*:/i.test(path) || path.includes("\\") || path.split("/").includes("..")) {
      throw new Error("An API-relative path is required.");
    }
    const values = new URLSearchParams();
    for (const [key, value] of Object.entries(query)) if (value !== undefined && value !== null && value !== "") values.set(key, String(value));
    const suffix = values.toString();
    return this.base + path + (suffix ? "?" + suffix : "");
  }
  async request(path, { method = "GET", body, query, signal, timeoutMs = 15000, binary = false } = {}) {
    const abort = new AbortController();
    const externalAbort = () => abort.abort(signal.reason);
    if (signal?.aborted) externalAbort();
    else signal?.addEventListener("abort", externalAbort, { once: true });
    const timer = setTimeout(() => abort.abort(new DOMException("The host did not respond in time.", "TimeoutError")), timeoutMs);
    const headers = { [CLIENT_HEADER]: CLIENT_VERSION };
    if (body !== undefined && !(body instanceof FormData)) headers["Content-Type"] = "application/json";
    try {
      const response = await this.fetch(this.url(path, query), {
        method, headers, body, signal: abort.signal, credentials: "omit", mode: "same-origin", cache: binary ? "force-cache" : "no-store"
      });
      if (!response.ok) throw await responseError(response);
      if (binary) {
        const contentType = (response.headers.get("Content-Type") || "").split(";")[0].trim();
        if (!["image/png", "image/jpeg"].includes(contentType)) throw new ApiError({ code: "invalid_image_response", detail: "The host returned an unsupported image type." });
        if (Number(response.headers.get("Content-Length")) > 8 * 1024 * 1024) throw new ApiError({ code: "image_too_large", detail: "The image exceeds the configured limit." });
        const image = await response.blob();
        if (image.size > 8 * 1024 * 1024) throw new ApiError({ code: "image_too_large", detail: "The image exceeds the configured limit." });
        return image;
      }
      const result = await response.json();
      if (result.contractVersion !== undefined && result.contractVersion !== 1) throw new ApiError({ code: "unsupported_contract_version", detail: "This host uses a newer contract. Update the client." });
      return result;
    } catch (error) {
      if (error instanceof ApiError) throw error;
      if (signal?.aborted) throw signal.reason || error;
      throw new ApiError({ code: "connection_lost", detail: "The local host is unavailable or the request timed out. The command may already be committed; retry its original request." });
    } finally {
      clearTimeout(timer);
      signal?.removeEventListener("abort", externalAbort);
    }
  }
  get(path, query, signal) { return this.request(path, { query, signal }); }
  post(path, value, signal) { return this.request(path, { method: "POST", body: JSON.stringify(value), signal }); }
  send(command, signal) {
    let body = command.json;
    if (command.image) {
      body = new FormData();
      body.append("request", new Blob([command.json], { type: "application/json" }), "request.json");
      body.append("image", command.image, command.image.type === "image/jpeg" ? "image.jpg" : "image.png");
    }
    return this.request("requests", { method: "POST", body, signal, timeoutMs: 30000 }).then(receipt => {
      if (receipt.state !== "committed" || receipt.requestId !== command.requestId) {
        throw new ApiError({ code: "invalid_command_response", detail: "The command response did not identify a committed receipt. Retry the original request to reconcile it." });
      }
      return receipt;
    });
  }
  image(path, signal) { return this.request(path, { signal, binary: true, timeoutMs: 30000 }); }
  createSubscription(callerUserId, after, requestId = crypto.randomUUID(), signal) {
    return this.post("subscriptions", {
      contractVersion: 1, requestId, callerUserId,
      filter: { catalog: true, allMessages: true, channelIds: [], sessionIds: [], targetUserId: null },
      start: { mode: "after", cursor: after }
    }, signal);
  }
  closeSubscription(subscription, signal, requestId = crypto.randomUUID()) {
    return this.post("subscriptions/" + encodeURIComponent(subscription.id) + "/close", {
      contractVersion: 1, requestId, callerUserId: subscription.ownerUserId
    }, signal);
  }
}
