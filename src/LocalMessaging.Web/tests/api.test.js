import test from "node:test";
import assert from "node:assert/strict";
import { Api, ApiError, freezeCommand, retryAfterMilliseconds } from "../api.js";

test("a frozen command preserves exact caller, request ID, text, targets and image bytes", async () => {
  const input = { type: "message", content: { type: "image", caption: "Original" }, targetUserIds: ["b"] };
  const image = new Blob([Uint8Array.of(1, 2, 3)], { type: "image/png" });
  const command = freezeCommand(input, "alice", image, "11111111-1111-4111-8111-111111111111");
  input.content.caption = "Changed"; input.targetUserIds.push("c");
  assert.equal(command.envelope.dataObject.content.caption, "Original");
  assert.deepEqual(command.envelope.dataObject.targetUserIds, ["b"]);
  assert.equal(command.callerUserId, "alice");
  assert.deepEqual([...new Uint8Array(await command.image.arrayBuffer())], [1, 2, 3]);
  assert.throws(() => { command.envelope.callerUserId = "bob"; }, TypeError);
});

test("multipart retry preserves command JSON and byte content while using API header", async () => {
  const captured = [];
  const api = new Api("/api/v1/", async (url, options) => {
    captured.push({ url, options });
    const request = JSON.parse(await options.body.get("request").text());
    return new Response(JSON.stringify({ contractVersion: 1, state: "committed", requestId: request.requestId }), { headers: { "Content-Type": "application/json" } });
  });
  const command = freezeCommand({ type: "message", content: { type: "image", caption: "text" } }, "alice", new Blob(["png"], { type: "image/png" }));
  await api.send(command); await api.send(command);
  assert.equal(captured.length, 2);
  assert.equal(captured[0].url, "/api/v1/requests");
  for (const entry of captured) {
    assert.equal(entry.options.headers["X-Local-Messaging-Client"], "1");
    assert.equal(entry.options.headers["Content-Type"], undefined, "browser sets multipart boundary");
    assert.equal(await entry.options.body.get("request").text(), command.json);
    assert.equal(await entry.options.body.get("image").text(), "png");
    assert.equal(entry.options.credentials, "omit");
  }
});

test("failed HTTP responses retain field errors and Retry-After", async () => {
  const api = new Api("/api/v1/", async () => new Response(JSON.stringify({ code: "resource_limit_reached", detail: "Later", errors: { image: ["Wait"] } }), { status: 429, headers: { "Retry-After": "2" } }));
  await assert.rejects(api.get("status"), error => error instanceof ApiError && error.retryAfterMs === 2000 && error.errors.image[0] === "Wait");
  assert.equal(retryAfterMilliseconds("5"), 5000);
  assert.equal(retryAfterMilliseconds("invalid"), 0);
});

test("all API paths remain relative to the local surface", () => {
  const api = new Api();
  for (const path of ["https://example.com/file", "/outside", "../file", "images/../outside", "a\\b"]) assert.throws(() => api.url(path), /relative/);
  assert.equal(api.url("users", { handlePrefix: "gee", limit: 20 }), "/api/v1/users?handlePrefix=gee&limit=20");
});

test("subscription closure always uses descriptor owner rather than selected caller", async () => {
  let body;
  const api = new Api("/api/v1/", async (_url, options) => {
    body = JSON.parse(options.body);
    return new Response(JSON.stringify({ contractVersion: 1, state: "committed", subscriptionState: "closed" }));
  });
  await api.closeSubscription({ id: "sub", ownerUserId: "original-owner" }, undefined, "close-id");
  assert.equal(body.callerUserId, "original-owner");
  assert.equal(body.requestId, "close-id");
});

test("network loss exposes uncertainty without inventing a successful receipt", async () => {
  const api = new Api("/api/v1/", async () => { throw new TypeError("Disconnected"); });
  await assert.rejects(api.get("requests/id"), error => error instanceof ApiError && error.uncertain && error.code === "connection_lost");
});

test("unsupported contract and malformed command acknowledgement are distinct outcomes", async () => {
  const api = new Api("/api/v1/", async () => new Response(JSON.stringify({ contractVersion: 2, state: "committed" })));
  await assert.rejects(api.get("status"), error => error.code === "unsupported_contract_version" && !error.uncertain);
  const malformed = new Api("/api/v1/", async () => new Response(JSON.stringify({ contractVersion: 1, state: "committed", requestId: "wrong" })));
  await assert.rejects(malformed.send(freezeCommand({ type: "user", handle: "user", displayName: "User" }, null)), error => error.code === "invalid_command_response" && error.uncertain);
});
