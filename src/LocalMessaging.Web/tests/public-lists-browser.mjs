import assert from "node:assert/strict";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

// Browser-only client regression test with a deterministic API fixture.
const modules = process.env.CODEX_PRIMARY_RUNTIME_NODE_MODULES;
const { chromium } = await import(modules ? pathToFileURL(join(modules, "playwright", "index.mjs")).href : "playwright");
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH });
const base = "http://127.0.0.1:37444";
const storeEpoch = "00000000-0000-0000-0000-000000000001";
const snapshotCursor = storeEpoch + ":12";
const alice = { id: "alice", displayName: "Alice", handle: "alice" };
const bob = { id: "bob", displayName: "Bob", handle: "bob" };
const message = { id: "root", authorUserId: bob.id, sessionId: "s", content: { type: "text", text: "Public root" },
  depth: 1, threadRootMessageId: "root", sequence: "10", createdAtUtc: "2026-11-20T00:00:00Z", replyCount: 1 };
const reply = { ...message, id: "reply", parentMessageId: "root", depth: 2, sequence: "11" };
const pageOf = (items, nextPageToken = null) => ({ items, snapshotCursor, nextPageToken });
const posts = [], reads = [];
let capabilities, acknowledged = false;
const lostResponse = Promise.withResolvers();
const firstWrite = Promise.withResolvers();
try {
  const page = await browser.newPage();
  page.setDefaultTimeout(5000);
  await page.route(base + "/**", async route => {
    const request = route.request(), url = new URL(request.url());
    const script = url.pathname.slice(1);
    if (/^[a-z-]+\.(js|css|svg)$/.test(script) || url.pathname === "/") {
      return route.fulfill({ path: fileURLToPath(new URL("../" + (script || "index.html"), import.meta.url)) });
    }
    const json = value => route.fulfill({ json: value });
    switch (url.pathname.replace("/api/v1/", "")) {
      case "status": return json({ storeEpoch, headCursor: snapshotCursor, capabilities });
      case "users": return json(pageOf([alice, bob]));
      case "channels": return json(pageOf([{ id: "c", name: "Channel", ownerUserId: bob.id }]));
      case "channels/c/sessions": return json(pageOf([{ id: "s", title: "Session", channelId: "c", ownerUserId: bob.id }]));
      case "sessions/s/messages": return json(pageOf([message]));
      case "messages/root/thread": return json({ ...pageOf([reply]), root: message, contextMessages: [] });
      case "users/alice/mentions": return json(pageOf([reply]));
      case "subscriptions": return json({ subscription: { id: "sub", ownerUserId: alice.id, startCursor: snapshotCursor } });
      case "subscriptions/sub/close": return json({ state: "committed" });
      case "subscriptions/sub/stream": return route.fulfill({ contentType: "text/event-stream", body: ": ready\n\n" });
      case "messages/root/acknowledgements": {
        reads.push(url);
        const item = profile => ({ profile, acknowledgedUtcMs: 1795132800000 });
        if (url.searchParams.has("pageToken")) return json(pageOf([item(bob)]));
        return json(pageOf(acknowledged ? [item(alice)] : [], "next"));
      }
      case "channels/c/participants":
      case "sessions/s/participants": reads.push(url); return json(pageOf([ { profile: bob } ]));
      case "requests": {
        posts.push(request.postData());
        const { requestId, dataObject } = request.postDataJSON();
        if (dataObject.type === "acknowledgement") {
          acknowledged = true;
          if (posts.length === 1) { firstWrite.resolve(); await lostResponse.promise; return route.abort(); }
        }
        const dataObjects = dataObject.type === "participation" ? { participation: { ...dataObject, channelId: "c", channelJoined: true } } : {};
        return json({ requestId, state: "committed", storeEpoch, created: [], dataObjects });
      }
      default: return route.fulfill({ status: 404, json: { code: "not_found", detail: url.pathname } });
    }
  });
  const button = name => page.getByRole("button", { name, exact: true });
  const status = text => page.locator("#public-command-status").filter({ hasText: text }).waitFor();
  const close = () => button("Close").click();
  const feedback = page.locator("#notice");
  await page.goto(base);
  await page.locator('#profile option[value="alice"]').waitFor({ state: "attached" });
  await page.locator("#profile").selectOption(alice.id);
  await page.getByText("Public root", { exact: true }).waitFor();
  assert.equal(await button("Profile acknowledgements").count(), 0);
  assert.equal(await button("Channel participants").count(), 0);
  assert.equal(await button("Session participants").count(), 0);
  assert.equal(reads.length, 0);

  capabilities = { profileAcknowledgements: true, participantRosters: true };
  await page.reload();
  await button("Profile acknowledgements").click();
  await button("Next page").click();
  await page.locator("#public-items").getByText(/Bob/).waitFor();
  assert.ok(await page.locator("#public-items time").getAttribute("datetime"));
  assert.equal(reads[0].searchParams.get("limit"), "25");
  assert.equal(reads[1].searchParams.get("asOf"), snapshotCursor);
  assert.equal(reads[1].searchParams.get("pageToken"), "next");
  assert.equal(posts.length, 0, "viewing and paging do not acknowledge");

  await button("Acknowledge as this profile").click();
  await firstWrite.promise;
  await status(/Waiting for the host/);
  assert.doesNotMatch(await feedback.textContent(), /committed/);
  lostResponse.resolve();
  await status(/No commit confirmed/);
  await close();
  await button("Retry original").click();
  await feedback.filter({ hasText: "Profile acknowledgement committed." }).waitFor();
  assert.equal(posts[0], posts[1], "retry preserves the frozen request ID and caller");
  assert.deepEqual(JSON.parse(posts[0]).dataObject, { type: "acknowledgement", messageId: "root" });
  assert.equal(JSON.parse(posts[0]).callerUserId, alice.id);
  await button("Profile acknowledgements").click();
  await page.locator("#public-items").getByText(/Alice/).waitFor();
  await close();

  await page.getByRole("button", { name: /1 reply/ }).click();
  await page.locator('[data-message-id="reply"]').getByRole("button", { name: "Profile acknowledgements" }).waitFor();
  await page.locator("#mentions").click();
  await page.locator('[data-message-id="reply"]').getByRole("button", { name: "Profile acknowledgements" }).waitFor();
  await page.locator("#channels .session-row").click();
  await button("Session participants").click();
  await page.locator("#public-items").getByText(/Bob/).waitFor();
  await button("Join session").click();
  await status(/Session join committed/);
  await close();
  await button("Channel participants").click();
  await button("Leave direct channel membership").click();
  await status(/Direct channel leave committed.*was on the channel roster/);
  assert.deepEqual(posts.slice(2).map(raw => JSON.parse(raw).dataObject), [
    { type: "participation", sessionId: "s", joined: true },
    { type: "participation", channelId: "c", joined: false }
  ]);
  assert.ok(posts.every(raw => JSON.parse(raw).callerUserId === alice.id));
  await close();
  await page.locator("#profile").selectOption(bob.id);
  await page.locator("#draft-owner").filter({ hasText: "Sending as @bob" }).waitFor();
  await button("Profile acknowledgements").click();
  await page.locator("#public-items").getByText(/Alice/).waitFor();
  assert.equal(posts.length, 4, "another profile can see the acknowledgement without writing one");
  console.log("Public profile browser regression passed.");
} finally {
  await browser.close();
}
