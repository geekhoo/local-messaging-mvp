# Host follow-up: public acknowledgements and participant rosters

Historical implementation brief. See [public profile actions](../../docs/public-profile-actions.md) for the implemented behavior. The checks below describe the original scope; they are not a test report.

## Deliverables

1. Store an explicit, durable acknowledgement on the host for each `(messageId, acknowledgingUserId)` pair. Record the host timestamp and make repeated requests idempotent, including retries after a lost response. Validate the message and profile, and enforce uniqueness in SQLite. Do not modify immutable messages or count a replay, scroll, delivery, or subscription cursor as an acknowledgement.
2. Expose a bounded, paginated acknowledgement query by message to **everyone** who can view that message, not only the sender. Include public profile information and acknowledgement time. The current host has no authenticated identity; label this as a profile acknowledgement, not proof that a verified human read the message.
3. Add explicit channel and session participation with a public roster for each scope. Define join/leave independently from delivery subscriptions. Represent membership transitions with append-only operations if retaining the product's create-only model. Specify whether session participation implies channel participation; keep that rule consistent in queries, events, and clients. A roster represents joined profiles, not live presence or access control.
4. Update Contracts, Application, Infrastructure and the generated EF migration/model snapshot, both Host surfaces and OpenAPI, the shared client, web UI, WinUI, and the Claude Code plugin. Advertise capabilities so new clients can detect older hosts. On Claude Code, read all active subscriptions in bounded pages; acknowledge a message only after it has been surfaced and the acknowledgement is authorized. Deduplicate overlapping deliveries by message ID. Do not say an acknowledgement succeeded until the server commits it.

## Acceptance checks

- A profile acknowledges a message, retrying creates no duplicate, and another profile sees it in Claude Code, web, and WinUI. Restart/restore and lost-response tests cover durable state and reconciliation.
- Multiple profiles can acknowledge one message independently; different messages and invalid IDs cannot be conflated. An inbox read alone does not silently create an acknowledgement.
- Joining/leaving a channel or session updates only the documented scopes; closing an unrelated delivery subscription does not change a public roster. A roster is public, paginated, and consistent after reconnect.
- Test with real SQLite and both API surfaces. Preserve loopback/TLS boundaries and immutable message behavior. Run Windows UI and exact HTTP/3 verification; report unavailable gates as blocked, not passed.
