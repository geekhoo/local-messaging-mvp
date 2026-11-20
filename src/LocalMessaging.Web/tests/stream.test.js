import test from "node:test";
import assert from "node:assert/strict";
import { SseParser, LiveSubscription } from "../stream.js";
import { Api } from "../api.js";

const epoch = "ffffffff-ffff-4fff-8fff-ffffffffffff";
const cursor = sequence => epoch + ":" + sequence;
const encode = value => new TextEncoder().encode(value);
const frame = (sequence, text = "hello") => "id: " + cursor(sequence) + "\nevent: message.created\ndata: " + JSON.stringify({ contractVersion: 1, cursor: cursor(sequence), sequence: String(sequence), dataObject: { id: "m" + sequence, content: { type: "text", text } } }) + "\n\n";
const descriptor = { id: "subscription", ownerUserId: "caller", startCursor: cursor(0) };

test("every possible two-chunk split preserves UTF-8, CRLF and multiline data", () => {
  const input = encode("\uFEFF: comment\r\nid: " + cursor(4) + "\r\nevent: test\r\ndata: {\"text\":\r\ndata: \"汉字 ✦ 😀\"}\r\n\r\n");
  for (let split = 0; split <= input.length; split++) {
    const parser = new SseParser();
    const values = [...parser.push(input.slice(0, split)), ...parser.push(input.slice(split)), ...parser.push(new Uint8Array(), true)];
    assert.equal(values.length, 1, "split " + split);
    assert.equal(values[0].id, cursor(4));
    assert.equal(values[0].event, "test");
    assert.deepEqual(JSON.parse(values[0].data), { text: "汉字 ✦ 😀" });
  }
});

test("one-byte chunks support mixed CR, LF and CRLF endings", () => {
  const parser = new SseParser(), values = [];
  for (const byte of encode("data: one\r\rdata: two\n\ndata: three\r\n\r\n")) values.push(...parser.push(Uint8Array.of(byte)));
  assert.deepEqual(values.map(value => value.data), ["one", "two", "three"]);
});

test("comments and ID-only frames do not become domain events; IDs do not leak to controls", () => {
  const parser = new SseParser();
  const values = parser.push(encode(": keepalive\n\nid: 9\n\ndata: root\nid: 10\n\nevent: server.stopping\ndata: {\"contractVersion\":1}\n\n"));
  assert.equal(values.length, 2);
  assert.equal(values[0].id, "10");
  assert.equal(values[1].id, null);
});

test("unterminated EOF is discarded and never advances an applied cursor", () => {
  const parser = new SseParser();
  assert.deepEqual(parser.push(encode("id: 12\ndata: incomplete\n")), []);
  assert.deepEqual(parser.push(new Uint8Array(), true), []);
});

test("frame/line bounds reject malformed unbounded input", () => {
  assert.throws(() => new SseParser(16).push(encode("data: " + "x".repeat(17))), /limit/);
  assert.throws(() => new SseParser(20).push(encode("data: 12345678\ndata: 12345678\n\n")), /limit/);
  assert.throws(() => new SseParser().push(Uint8Array.of(0xff)), /encoded data|encoding/i);
});

test("a live consumer applies before advancing and ignores duplicate event positions", async () => {
  let live;
  const applied = [];
  const api = new Api("/api/v1/", async (url, options) => {
    assert.match(url, /after=ffffffff/);
    assert.equal(options.headers["X-Local-Messaging-Client"], "1");
    const body = new ReadableStream({
      start(controller) {
        controller.enqueue(encode(frame(1) + frame(1) + frame(2)));
        options.signal.addEventListener("abort", () => controller.error(options.signal.reason), { once: true });
      }
    });
    return new Response(body, { headers: { "Content-Type": "text/event-stream" } });
  });
  live = new LiveSubscription(api, descriptor, {
    onEvent: async event => {
      assert.equal(live.position.cursor, cursor(event.sequence === "1" ? 0 : 1));
      applied.push(event.sequence);
      if (event.sequence === "2") live.stop();
    },
    onState() {}, onReset() { assert.fail("unexpected reset"); }, onFatal(error) { assert.fail(error.message); }
  });
  const firstStart = live.start();
  assert.equal(live.start(), firstStart, "a subscription has exactly one run loop");
  await firstStart;
  assert.deepEqual(applied, ["1", "2"]);
  assert.equal(live.position.cursor, cursor(2));
});

test("apply failure leaves the event cursor unchanged", async () => {
  const api = new Api("/api/v1/", async () => new Response(frame(1), { headers: { "Content-Type": "text/event-stream" } }));
  let fatal;
  const live = new LiveSubscription(api, descriptor, {
    onEvent() { throw new Error("View application failed"); }, onState() {}, onReset() {},
    onFatal(error) { fatal = error; }
  });
  await live.start();
  assert.match(fatal.message, /View application failed/);
  assert.equal(live.position.cursor, cursor(0));
});

test("quiet comment bytes reset the idle watchdog without advancing the cursor", async () => {
  let aborted = false, requests = 0, handle;
  const api = new Api("/api/v1/", async (_url, options) => {
    requests++;
    const body = new ReadableStream({
      start(controller) {
        handle = setInterval(() => controller.enqueue(encode(": keepalive\n\n")), 5);
        options.signal.addEventListener("abort", () => { aborted = true; clearInterval(handle); controller.error(options.signal.reason); }, { once: true });
      },
      cancel() { clearInterval(handle); }
    });
    return new Response(body, { headers: { "Content-Type": "text/event-stream" } });
  });
  const live = new LiveSubscription(api, descriptor, { onEvent() { assert.fail("comment dispatched"); }, onState() {}, onReset() {}, onFatal(error) { assert.fail(error.message); } }, { idleMs: 25 });
  const running = live.start();
  await new Promise(resolve => setTimeout(resolve, 80));
  assert.equal(aborted, false);
  assert.equal(requests, 1);
  assert.equal(live.position.cursor, cursor(0));
  live.stop();
  await running;
});

test("unsupported contract stops consumption; store change requests a fresh snapshot", async () => {
  for (const mode of ["version", "epoch"]) {
    let reset = 0, fatal = 0;
    const api = new Api("/api/v1/", async () => mode === "version"
      ? new Response('event: message.created\ndata: {"contractVersion":2}\n\n', { headers: { "Content-Type": "text/event-stream" } })
      : new Response(JSON.stringify({ code: "store_changed", detail: "Restore" }), { status: 409, headers: { "Content-Type": "application/problem+json" } }));
    const live = new LiveSubscription(api, descriptor, { onEvent() { assert.fail("should not apply"); }, onState() {}, onReset() { reset++; }, onFatal() { fatal++; } });
    await live.start();
    assert.equal(reset, mode === "epoch" ? 1 : 0);
    assert.equal(fatal, mode === "version" ? 1 : 0);
    assert.equal(live.position.cursor, cursor(0));
  }
});

test("ID-less restore control requests resynchronization without advancing cursor", async () => {
  let reset;
  const api = new Api("/api/v1/", async () => new Response('event: store_changed\ndata: {"contractVersion":1}\n\n', { headers: { "Content-Type": "text/event-stream" } }));
  const live = new LiveSubscription(api, descriptor, { onEvent() { assert.fail("control is not a domain event"); }, onState() {}, onReset(error) { reset = error; }, onFatal(error) { assert.fail(error.message); } });
  await live.start();
  assert.equal(reset.code, "store_changed");
  assert.equal(live.position.cursor, cursor(0));
});

test("ID-less temporary host controls reconnect at the same cursor without replacing the subscription", { timeout: 1500 }, async () => {
  for (const code of ["database_busy", "storage_unavailable", "host_not_ready"]) {
    let attempts = 0, applied = 0, live;
    const api = new Api("/api/v1/", async url => {
      attempts++;
      assert.equal(new URL(url, "http://127.0.0.1").searchParams.get("after"), cursor(0));
      const body = attempts === 1
        ? "event: " + code + '\ndata: {"contractVersion":1}\n\nevent: stream.closed\ndata: {"contractVersion":1}\n\n'
        : frame(1);
      return new Response(body, { headers: { "Content-Type": "text/event-stream" } });
    });
    live = new LiveSubscription(api, descriptor, {
      onEvent() { assert.equal(live.position.cursor, cursor(0)); applied++; live.stop(); },
      onState() {}, onReset() { assert.fail("temporary controls must preserve the subscription"); },
      onFatal(error) { assert.fail(error.message); }
    }, { retryBaseMs: 1 });
    await live.start();
    assert.equal(attempts, 2);
    assert.equal(applied, 1);
    assert.equal(live.position.cursor, cursor(1));
  }
});
