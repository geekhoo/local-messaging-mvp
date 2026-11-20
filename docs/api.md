# API and client guide

## Transport and local boundary

| Surface | Base URL | Protocol | Intended callers |
| --- | --- | --- | --- |
| M | `https://localhost:7443/messaging/v1/` | Exact HTTP/3 over QUIC/TLS 1.3 | Reusable .NET client, integration clients |
| F | `http://127.0.0.1:7444/api/v1/` | HTTP/1.1 | WinUI and browser frontend |

Configured ports are advertised in the verified local endpoint descriptor. Every API request, including reads, image downloads, status, replay and streams, requires `X-Local-Messaging-Client: 1`. Browser Origin must match the configured F origin; cross-site Fetch Metadata is rejected. There is no permissive CORS policy. The listeners are loopback only, routes are segregated by the actual local port, and Host authority must match the selected surface.

The product web page lives at the F root. It obtains images through header-bearing fetches and creates bounded blob URLs. Do not embed the API's image content URL directly in a third-party page.

Use the .NET console sample for exact HTTP/3. A stock `curl` binary is not assumed to have HTTP/3 support. Both frontends deliberately use F and do not require browser trust of the native endpoint certificate.

## Create request and receipt

`POST requests` accepts a versioned envelope. For user creation, the caller is absent/null; all other creations require the ID of an existing user. UUIDs use canonical hyphenated form. A request ID is a global idempotency identity across create, subscribe, and close operations.

```json
{
  "contractVersion": 1,
  "requestId": "5b904ef6-4ae3-4acb-8ae8-3ed60a7ee0ce",
  "callerUserId": null,
  "dataObject": { "type": "user", "handle": "alice", "displayName": "Alice" }
}
```

A new command returns 201; a committed identical retry returns 200 with `replayed: true`. Both expose the same committed IDs and result. Query that result with `GET requests/{requestId}`. Changing normalized data, original caller, operation or image bytes under a committed request ID returns 409 `idempotency_conflict`.

The client must retain the original request ID, caller and payload while an outcome is uncertain. Multipart boundary and filename are transport details; changing them does not change an otherwise identical image command. SHA-256 of the original image bytes participates in the command's semantic identity.

Create a channel with a caller and these data fields:

```json
{
  "type": "channel",
  "name": "Architecture",
  "description": "Local design discussion",
  "initialSessionTitle": "General"
}
```

One transaction creates the channel and exactly one initial session. Both owners equal the caller. A session command with an existing channel is:

```json
{
  "type": "session",
  "channel": { "mode": "existing", "id": "f43f250a-50ad-435c-968a-79eaab93327f" },
  "title": "API review"
}
```

The session belongs to its caller while the channel owner remains unchanged. `mode: new` carries a channel name and optional description; creating a new channel through a session/message command never creates an extra hidden initial session.

## Text, targeting and threads

Message data selects both containers explicitly:

```json
{
  "type": "message",
  "channel": { "mode": "new", "name": "New discussion" },
  "session": { "mode": "new", "title": "First session" },
  "content": { "type": "text", "text": "Hello @alice" },
  "parentMessageId": null,
  "targetUserIds": []
}
```

The new channel, session, message, targets, ordered durable events and receipt commit atomically. Existing selectors use `mode: existing` and an `id`. Missing or inconsistent existing IDs are rejected; they never cause implicit creation.

Known `@handle` mentions and explicit target IDs are normalized to a deduplicated union. Unknown valid mention handles reject the command. Escaped mentions, email addresses, too-short tokens and invalid tokens follow the grammar in the original specification. Targeting is public. A reply does not inherit its parent's targets.

Use `parentMessageId` to reply. Root messages have depth 1; children depth 2; grandchildren depth 3. Replies below level 3 and cross-session parents are rejected. Root identity and depth are server-derived and protected by relational constraints. Clients display text using native text controls or safe DOM text nodes.

## Public profile actions

Send these as JSON `dataObject` values in `POST requests`, with an existing `callerUserId` and no `subscribe` block:

- `acknowledgement`: provide `messageId`. A profile can acknowledge a message once; reading it does not count.
- `participation`: provide exactly one of `channelId` or `sessionId` and a boolean `joined`. This does not subscribe or unsubscribe the caller.

Check `profileAcknowledgements` and `participantRosters` in `GET status` before using these features; an omitted flag means unsupported. See [public profile actions](public-profile-actions.md) for roster rules and event delivery.

## Images

An image message uses `multipart/form-data` with exactly two parts:

1. `request`: UTF-8 `application/json`, containing the normal envelope with `content: { "type": "image", "caption": "Optional caption" }`.
2. `image`: the original PNG or JPEG bytes with the correct `image/png` or `image/jpeg` content type.

Duplicate/unknown parts, content-type mismatches, oversized bodies, unsupported/corrupt/animated images, and excessive dimensions are rejected before domain persistence. WIC validates through the explicit Windows built-in codec, including bounded full decode. Filenames are not stored as trusted paths.

Returned content contains an image descriptor with ID, MIME type, byte length, dimensions, SHA-256 and `contentPath`. History, events and receipts contain descriptors only. `GET images/{id}/content` returns the original bytes with MIME, ETag and `nosniff`; image response concurrency is bounded.

## Queries

| Resource | Methods/routes |
| --- | --- |
| Users | `GET users`, `GET users/{id}`, `GET users/{id}/mentions` |
| Channels | `GET channels`, `GET channels/{id}`, `GET channels/{id}/sessions` |
| Sessions/history | `GET sessions/{id}`, `GET sessions/{id}/messages` |
| Messages/threads | `GET messages/{id}`, `GET messages/{id}/thread` |
| Public profile actions | `GET messages/{id}/acknowledgements`, `GET channels/{id}/participants`, `GET sessions/{id}/participants` |
| Receipts | `GET requests/{requestId}` |
| Images | `GET images/{id}/content` |
| Subscriptions | `POST subscriptions`, `GET subscriptions`, `GET subscriptions/{id}`, `POST subscriptions/{id}/close` |
| Replay/live | `GET subscriptions/{id}/events`, `GET subscriptions/{id}/stream` |
| Operations | `GET status`, `GET openapi.json`; `POST host/stop` on F only |

No domain update/delete routes exist. List pages use `limit`, `pageToken`, and `asOf` where applicable. User prefix search uses `handlePrefix`; subscription ownership filtering uses `ownerUserId`. Session history supports `view=roots` or `view=flat`. Thread results include the root, selected page and ancestor context needed to render that page.

Domain pages return `items`, `snapshotCursor`, and `nextPageToken`. Capture H from status, read all initial pages with `asOf=H`, then subscribe after H. Tokens bind the resource, scope, filter and snapshot; using one with a different query is rejected. Concurrent publications do not change the pinned page's content or reply counts.

## Saved subscriptions, replay and SSE

```json
{
  "contractVersion": 1,
  "requestId": "5dcc12f0-78e0-46a1-bc53-3ebf07468aaf",
  "callerUserId": "c48a72b6-0d6d-4900-94c6-c336826a8b2a",
  "filter": { "catalog": true, "allMessages": true },
  "start": { "mode": "now" }
}
```

Start can be `now`, `beginning`, or `after` with a cursor. Filter branches include catalog, all messages, channel IDs, session IDs and a target user. They combine with OR and produce each matching event once. Saved definitions survive process restart; each connected consumer has its own cursor. One consumer never removes another's events.

A create envelope may additionally contain `subscribe: { "scope": "resolvedChannel", "start": "beforeRequest" }`; other scopes are `catalog` and `resolvedSession`. Objects and that saved subscription share the transaction, and the response makes the subscription queryable. User creation does not accept a coupled subscription.

Finite replay returns an ordered bounded page, `nextCursor`, `hasMore` and the current head. It caps examined headers and materialized payload bytes, so even an empty sparse-filter page may advance. A matching event excluded by a full byte budget is not skipped. Use the returned continuation rather than guessing from the last visible event.

SSE uses explicit event types and event IDs. Domain frames and `stream.checkpoint` advance a consumer only after the corresponding data/control has been applied. Comment heartbeats contribute to liveness. ID-less stopping/closed/store-change controls do not acknowledge data. Resume with `after` or `Last-Event-ID`; conflicting values are rejected. Every stream write and idle wait is bounded. Durable replay repairs lost wakes, reconnects, and deliberately closed slow consumers.

Cursors contain the store epoch and a decimal 64-bit sequence. The JavaScript app uses exact integer handling; it does not convert event positions to imprecise floating-point values. Restoring a store changes the epoch, so clients rebuild state from a new snapshot rather than silently waiting on an incompatible cursor.

## Errors and diagnostics

Errors use Problem Details with `code`, HTTP status, trace ID, request ID when available, and validation errors where appropriate. Typical classes are 400 malformed/unknown contract fields, 404 missing reference/receipt, 409 idempotency or epoch/cursor conflict, 410 closed subscription, 413 byte limit, 415 unsupported content/image type, 422 domain/image validation, 429 bounded resource capacity, and 503 readiness/storage/busy conditions. Generated OpenAPI and the original spec define exact operation schemas and codes.

The API intentionally has no authentication boundary among local processes. Loopback/authority/Origin/header checks constrain browser access; they do not make a claimed user ID a verified identity.
