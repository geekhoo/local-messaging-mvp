import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { pathToFileURL } from "node:url";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { acknowledgeAndReply } from "../../../plugins/local-messaging/copilot-reply.mjs";

// Run only against a separately started, isolated integration-test store.
const base = process.argv[2] || "http://127.0.0.1:7444";
const output = process.argv[3];
const location = new URL(base);
if (location.protocol !== "http:" || !["127.0.0.1", "localhost"].includes(location.hostname)) throw new Error("Use the local frontend listener of an isolated integration fixture.");
let playwright;
try { playwright = await import("playwright"); }
catch {
  const modules = process.env.CODEX_PRIMARY_RUNTIME_NODE_MODULES;
  if (!modules) throw new Error("Install Playwright as a development tool or provide CODEX_PRIMARY_RUNTIME_NODE_MODULES.");
  playwright = await import(pathToFileURL(join(modules, "playwright", "index.mjs")).href);
}
const executablePath = process.env.CHROMIUM_PATH || (existsSync("/usr/bin/chromium") ? "/usr/bin/chromium" : undefined);
let browser;
try { browser = await playwright.chromium.launch({ headless: true, executablePath, args: ["--no-sandbox"] }); }
catch (error) {
  const blocked = { scope: "Browser integration prerequisite", status: "blocked", checks: [], base, reason: "The installed browser could not launch in this environment. No UI acceptance check ran.", detail: error.message.split("\n").filter(line => /FATAL|Operation not permitted|unexpected operator/.test(line)).slice(0, 4).join("\n") || error.message.slice(0, 800), observedAtUtc: new Date().toISOString() };
  if (output) await writeFile(output, JSON.stringify(blocked, null, 2) + "\n");
  console.log(JSON.stringify(blocked, null, 2));
  process.exit(2);
}
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
const page = await context.newPage();
const failures = [];
const expectedNetwork = [];
let allowAbortedRequest = false;
page.on("pageerror", error => failures.push(error.message));
page.on("console", message => {
  if (message.type() !== "error" && message.type() !== "warning") return;
  if (allowAbortedRequest && /ERR_FAILED|Failed to fetch|Failed to load resource/.test(message.text())) expectedNetwork.push(message.text());
  else failures.push(message.text());
});
const report = { scope: "Real-browser integration against the supplied local test host; not Windows/WinUI/WIC qualification", base, startedAtUtc: new Date().toISOString(), checks: [], expectedDiagnostics: expectedNetwork, unexpectedDiagnostics: failures, status: "running" };
const mark = name => report.checks.push({ name, result: "passed" });
const suffix = Date.now().toString(36);
const aHandle = "copilot", bHandle = "b_" + suffix;
const headers = { "X-Local-Messaging-Client": "1" };
const get = async path => {
  const result = await context.request.get(base + "/api/v1/" + path, { headers });
  assert.equal(result.status(), 200, await result.text());
  return result.json();
};
const waitForText = async text => page.getByText(text, { exact: true }).first().waitFor({ timeout: 15000 });
const command = async (caller, object) => {
  const result = await context.request.post(base + "/api/v1/requests", { headers, data: { contractVersion: 1, requestId: crypto.randomUUID(), callerUserId: caller, dataObject: object } });
  assert.equal(result.status(), 201, await result.text());
  return result.json();
};
const createUser = async (handle, name) => {
  await page.locator("#new-user").click();
  await page.locator("#user-handle").fill(handle); await page.locator("#user-name").fill(name);
  await page.locator("#user-form button[type=submit]").click();
  await page.locator("#user-dialog").waitFor({ state: "hidden" });
  await page.waitForFunction(value => document.querySelector("#profile").selectedOptions[0]?.textContent.includes(value), handle);
  await page.waitForFunction(() => document.querySelector("#connection-label").textContent === "Connected");
  return page.locator("#profile").inputValue();
};
try {
  await page.goto(base, { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => document.querySelector("#connection-label").textContent === "Connected", { timeout: 15000 });
  const a = await createUser(aHandle, "Alice " + suffix);
  await page.locator("#new-channel").click();
  await page.locator("#channel-name").fill("copilot");
  await page.locator("#first-session").fill("Initial");
  await page.locator("#channel-form button[type=submit]").click();
  await page.locator("#channel-dialog").waitFor({ state: "hidden" });
  await page.locator("#conversation-title").filter({ hasText: "Initial" }).waitFor();
  const channels = await get("channels?limit=200");
  const channel = channels.items.find(item => item.name === "copilot");
  assert.equal(channel.ownerUserId, a);
  let sessions = await get("channels/" + channel.id + "/sessions");
  assert.equal(sessions.items.length, 1);
  assert.equal(sessions.items[0].ownerUserId, a);
  mark("Browser profile creation and atomic channel/initial-session ownership");
  const b = await createUser(bHandle, "Bryan " + suffix);
  await page.locator('#channels .tree-group:has(.channel-row[aria-current="true"]) .session-add').click(); await page.locator("#session-title").fill("General");
  await page.locator("#session-form button[type=submit]").click();
  await page.locator("#session-dialog").waitFor({ state: "hidden" });
  await page.locator("#conversation-title").filter({ hasText: "General" }).waitFor();
  sessions = await get("channels/" + channel.id + "/sessions");
  const session = sessions.items.find(item => item.title === "General");
  assert.equal(session.ownerUserId, b);
  assert.equal((await get("channels/" + channel.id)).ownerUserId, a);
  mark("Different caller owns new session while original channel owner is preserved");
  const unsafe = '<img src=x onerror="window.__injected=1"> @' + aHandle;
  await page.locator("#message-text").fill('<img src=x onerror="window.__injected=1"> @' + aHandle.slice(0, 3));
  await page.locator("#target-suggestions button").filter({ hasText: aHandle }).click();
  await page.locator("#message-text").press("Backspace");
  await page.locator("#composer").evaluate(form => form.requestSubmit());
  await waitForText(unsafe);
  let messages = await get("sessions/" + session.id + "/messages?view=flat&limit=200");
  const root = messages.items.find(item => item.content.text === unsafe);
  assert.deepEqual(root.targetUserIds, [a]);
  assert.equal(await page.evaluate(() => window.__injected), undefined);
  assert.equal(await page.locator("#timeline .message-body img").count(), 0);
  mark("Literal safe rendering and canonical explicit/textual target union");
  const rootActions = page.locator(`article[data-message-id="${root.id}"] > .message-actions`);
  await page.locator("#message-text").fill("Leave this main draft unchanged");
  const replyContent = "Level two " + suffix + "\nsecond line";
  const journal = await mkdtemp(join(tmpdir(), "local-messaging-browser-"));
  try {
    const bridge = { base: new URL("/api/v1/", base).href, channelId: channel.id, ownerId: a, messageId: root.id,
      epoch: (await get("status")).storeEpoch, cursorFile: pathToFileURL(join(journal, "cursor")).href };
    const committed = await acknowledgeAndReply({ ...bridge, text: replyContent.replace("\n", "\r\n") });
    assert.equal(committed.acknowledgement, "committed");
    assert.equal(committed.reply, "committed");
  } finally { await rm(journal, { recursive: true, force: true }); }
  await rootActions.getByRole("button", { name: `Acknowledged by @${aHandle}. View profile acknowledgements (1)` }).waitFor();
  await rootActions.getByRole("button", { name: /Acknowledged by/ }).click();
  await page.locator("#public-items").getByText(new RegExp(aHandle)).waitFor();
  await page.locator("#public-dialog").getByRole("button", { name: "Close", exact: true }).click();
  await rootActions.getByRole("button", { name: "Reply (1 reply), New reply", exact: true }).click();
  await rootActions.getByRole("button", { name: "Reply (1 reply)", exact: true }).waitFor();
  await rootActions.getByRole("button", { name: /Acknowledged by/ }).waitFor();
  assert.equal(await page.locator("#thread-reply-text").evaluate(el => el === document.activeElement), true);
  messages = await get("sessions/" + session.id + "/messages?view=flat&limit=200");
  const child = messages.items.find(item => item.content.text === replyContent);
  assert.deepEqual(child.targetUserIds, []);
  await page.locator(`article[data-message-id="${child.id}"] > .message-actions`).getByRole("button", { name: "Reply", exact: true }).click();
  const editor = page.locator("#thread-reply-text");
  const nestedText = "Level three " + suffix + "\nthird line";
  await editor.fill("Level three " + suffix); await editor.press("Enter"); await editor.pressSequentially("third line");
  await page.getByRole("button", { name: "Send reply", exact: true }).click();
  const rendered = page.locator(".thread .message-body").filter({ hasText: "third line" });
  await rendered.waitFor();
  assert.equal(await rendered.textContent(), nestedText);
  assert.equal(await editor.inputValue(), "");
  assert.equal(await page.locator("#message-text").inputValue(), "Leave this main draft unchanged");
  messages = await get("sessions/" + session.id + "/messages?view=flat&limit=200");
  const nested = messages.items.find(item => item.content.text === nestedText);
  assert.equal(nested.depth, 3); assert.equal(nested.threadRootMessageId, root.id); assert.equal(nested.parentMessageId, child.id);
  assert.equal(await page.locator(`article[data-message-id="${nested.id}"] > .message-actions`).getByRole("button", { name: "Reply", exact: true }).isDisabled(), true);
  mark("Two-profile live acknowledgement, accessible reply badge, and focused multiline nested send preserve main draft");
  await rootActions.getByRole("button", { name: /Hide thread/ }).click();
  const rootLines = "Root line " + suffix + "\nmore lines";
  await page.locator("#message-text").fill(rootLines.replace("\n", "\r\n"));
  await page.locator("#send").click();
  await page.locator(".message-body").filter({ hasText: "more lines" }).waitFor();
  await page.locator("#refresh-view").click();
  await rootActions.getByRole("button", { name: "Reply (2 replies)", exact: true }).waitFor();
  await rootActions.getByRole("button", { name: /Acknowledged by/ }).waitFor();
  await rootActions.getByRole("button", { name: /Open thread/ }).click();
  await rendered.waitFor();
  assert.equal(await rendered.textContent(), nestedText);
  assert.equal(await rendered.evaluate(el => getComputedStyle(el).whiteSpace), "pre-wrap");
  assert.equal(await page.locator(".message-body").filter({ hasText: "more lines" }).textContent(), rootLines);
  mark("Newline rendering and committed indicators survive snapshot reload without a new-reply highlight");
  // A valid two-by-two PNG; real WIC decoding is a separate Windows gate when the host uses a portable validator.
  const png = await readFile(new URL("../../../tests/LocalMessaging.Scenarios/fixtures/sample.png", import.meta.url));
  await page.locator("#image-file").setInputFiles({ name: "pixel.png", mimeType: "image/png", buffer: png });
  await page.locator("#message-text").fill("Image " + suffix); await page.locator("#send").click();
  await waitForText("Image " + suffix);
  await page.waitForFunction(() => [...document.querySelectorAll("#timeline .message-image img")].some(image => image.complete && image.naturalWidth > 0));
  messages = await get("sessions/" + session.id + "/messages?view=flat&limit=200");
  assert.ok(messages.items.some(item => item.content.type === "image" && item.content.image.contentPath));
  mark("Browser multipart image send and header-protected object-URL rendering");
  const second = await context.newPage();
  await second.goto(base, { waitUntil: "domcontentloaded" });
  await second.waitForFunction(() => document.querySelector("#connection-label").textContent === "Connected");
  await second.locator("#profile").selectOption(a);
  await second.waitForFunction(() => document.querySelector("#connection-label").textContent === "Connected");
  assert.equal(await page.locator("#profile").inputValue(), b);
  await second.locator("#mentions").click();
  await second.getByText(unsafe, { exact: true }).waitFor();
  mark("Separate tabs retain caller identity and selected-user mentions");
  // Hold a committed response while the selected profile changes.
  let release, committed;
  const releasePromise = new Promise(resolve => { release = resolve; });
  const commitPromise = new Promise(resolve => { committed = resolve; });
  const pendingText = "Pending identity " + suffix;
  let capturedCaller, capturedMessage;
  await page.route("**/api/v1/requests", async route => {
    const body = route.request().postDataJSON();
    if (body.dataObject?.content?.text !== pendingText) { await route.continue(); return; }
    capturedCaller = body.callerUserId;
    const response = await route.fetch(); capturedMessage = (await response.json()).dataObjects.message;
    committed(); await releasePromise; await route.fulfill({ response });
  });
  await page.locator("#message-text").fill(pendingText); await page.locator("#send").click();
  await commitPromise; await page.locator("#profile").selectOption(a);
  await page.waitForFunction(() => document.querySelector("#connection-label").textContent === "Connected");
  release();
  await page.waitForFunction(() => document.querySelector("#pending-section").hidden);
  assert.equal(capturedCaller, b); assert.equal(capturedMessage.authorUserId, b);
  assert.equal(await page.locator("#profile").inputValue(), a);
  await page.unroute("**/api/v1/requests");
  mark("Unresolved send keeps original caller across profile switch");
  // A lost committed response must retry its exact command identity.
  await page.locator('#channels .tree-group:has(.channel-row[aria-current="true"]) .session-row').filter({ hasText: "General" }).click();
  const lostText = "Lost response " + suffix;
  let first = true; const ids = [];
  allowAbortedRequest = true;
  await page.route("**/api/v1/requests", async route => {
    const body = route.request().postDataJSON();
    if (body.dataObject?.content?.text !== lostText) { await route.continue(); return; }
    ids.push(body.requestId);
    if (first) { first = false; await route.fetch(); await route.abort("failed"); }
    else await route.continue();
  });
  await page.locator("#message-text").fill(lostText); await page.locator("#send").click();
  await page.getByRole("button", { name: "Retry original", exact: true }).click();
  await page.waitForFunction(() => document.querySelector("#pending-section").hidden);
  await page.unroute("**/api/v1/requests");
  allowAbortedRequest = false;
  assert.equal(ids.length, 2); assert.equal(ids[0], ids[1]);
  messages = await get("sessions/" + session.id + "/messages?view=flat&limit=200");
  assert.equal(messages.items.filter(item => item.content.text === lostText).length, 1);
  mark("Lost-response retry reuses identity and produces one committed message");
  const incoming = "Live from another caller " + suffix;
  await command(b, { type: "message", channel: { mode: "existing", id: channel.id }, session: { mode: "existing", id: session.id }, content: { type: "text", text: incoming } });
  await waitForText(incoming);
  mark("Live durable stream updates the browser from another caller");
  await page.setViewportSize({ width: 390, height: 844 });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  mark("Narrow viewport has no horizontal document overflow");
  assert.deepEqual(failures, []);
  report.status = "passed";
} catch (error) {
  report.status = "failed"; report.failure = error.stack;
  process.exitCode = 1;
} finally {
  report.completedAtUtc = new Date().toISOString();
  await context.close(); await browser.close();
  if (output) await writeFile(output, JSON.stringify(report, null, 2) + "\n");
  console.log(JSON.stringify(report, null, 2));
}
