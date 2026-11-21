import test from "node:test";
import assert from "node:assert/strict";
import { Api } from "../api.js";
import { publicCapabilities, publicAction, readPublicPage } from "../public-lists.js";

test("public profile capabilities are independent, opt-in flags", () => {
  assert.deepEqual(publicCapabilities({}), { profileAcknowledgements: false, participantRosters: false, liveAcknowledgementSummaries: false });
  assert.deepEqual(publicCapabilities({ capabilities: { profileAcknowledgements: true, participantRosters: "true" } }), {
    profileAcknowledgements: true, participantRosters: false, liveAcknowledgementSummaries: false
  });
  assert.deepEqual(publicCapabilities({ capabilities: { participantRosters: true } }), {
    profileAcknowledgements: false, participantRosters: true, liveAcknowledgementSummaries: false
  });
});

test("public actions address exactly one scope and keep leave explicit", () => {
  assert.deepEqual(publicAction("message", "m", true), { type: "acknowledgement", messageId: "m" });
  assert.deepEqual(publicAction("channel", "c", true), { type: "participation", channelId: "c", joined: true });
  assert.deepEqual(publicAction("session", "s", false), { type: "participation", sessionId: "s", joined: false });
});

test("public list continuation is bounded and reuses the server snapshot", async () => {
  const requests = [];
  const page = { items: [], snapshotCursor: "epoch:90", nextPageToken: "opaque+token" };
  const api = new Api("/api/v1/", async (url, options) => {
    assert.equal(options.method, "GET"); requests.push(url); return Response.json(page);
  });
  const message = { kind: "message", id: "m" };
  await readPublicPage(api, message, await readPublicPage(api, message));
  await readPublicPage(api, { kind: "channel", id: "c" });
  await readPublicPage(api, { kind: "session", id: "s" });
  assert.deepEqual(requests, [
    "/api/v1/messages/m/acknowledgements?limit=25",
    "/api/v1/messages/m/acknowledgements?limit=25&asOf=epoch%3A90&pageToken=opaque%2Btoken",
    "/api/v1/channels/c/participants?limit=25", "/api/v1/sessions/s/participants?limit=25"
  ]);
});
