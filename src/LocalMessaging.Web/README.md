# Framework-free web client

The host serves these local assets at the frontend listener. Open the configured frontend URL, normally `http://127.0.0.1:7444/`. No Node installation, framework, build step, CDN, or authentication service is required to run the client.

The application uses the source v1 API contract and `X-Local-Messaging-Client: 1` on every API/stream/image fetch. It creates its own broad catalog/message subscription after loading a consistent snapshot. It never reuses a subscription copied from another tab.

## Modules

- `app.js`: profile, catalog, conversation, mentions, dialogs, drafts, pending-command reconciliation, and stream orchestration.
- `api.js`: local API paths, header, deadlines, Problem Details, immutable command envelopes, multipart bytes, and subscription operations.
- `stream.js`: incremental UTF-8/SSE framing, independent cursor, byte-level idle watchdog, and bounded reconnect.
- `state.js`: exact 64-bit cursor comparison, bounded per-profile/session drafts, and canonical ID merging.
- `views.js`: explicit DOM construction, safe literal text, thread hierarchy, and bounded image loading/object-URL disposal.

Use Ctrl+Enter (or Command+Enter) to send. Target search adds explicit user IDs; typed `@handle` tokens are resolved by the host. Replies do not inherit their parent's targets. The composer stores drafts in this tab's memory, bound to the original profile/session. A retry reuses the original request ID, caller, JSON, and image bytes. Editing creates a new command identity.

Loaded roots/mentions and each thread use sliding windows capped at 200 items, so subsequent pages remain reachable; at most eight expanded threads and twelve in-memory drafts are retained. Up to six unresolved commands may remain visible. The broad stream discards unloaded message payloads after advancing safely. Images load near the viewport, with four concurrent fetches and at most 24 retained image URLs. Offscreen/view-disposed images release their temporary URLs.

## Development checks

From `src/LocalMessaging.Web`, run `npm test` to check the API client, retry behavior, SSE parsing, and bounded view state. Node is only needed for development.

With Playwright and Chromium available, run `node .\tests\public-lists-browser.mjs` to check acknowledgements and rosters in Chromium against a stub API. Use the integrated scenario below to check behavior against a host.

## Real-browser integration

Start the isolated scenario host, then run:

```sh
node tests/browser-scenario.mjs http://127.0.0.1:37444 browser-integration-result.json
```

The runner needs Playwright and an executable Chromium as development tools. It uses the shared test PNG from the solution test fixtures. A portable test validator proves transport/storage/browser integration only; Windows WIC, WinUI and HTTP/3 remain separate platform gates. If Chromium cannot launch, the runner records `blocked`, emits no passed UI checks, and exits with code 2.
