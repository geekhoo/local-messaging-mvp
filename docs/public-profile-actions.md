# Public profile acknowledgements and participant rosters

Acknowledging a message is an explicit public action. Reading a message does not acknowledge it, and an acknowledgement does not prove that a verified person read it.

Joining a session adds your profile to its channel roster. Leaving the session keeps any direct channel join. Leaving the channel removes only its direct join; active session joins still count. Rosters show participation, not presence, permissions, or delivery subscriptions.

## Store model

- `Acknowledgements` — one row per `(MessageId, UserId)` with a unique database constraint; a host timestamp is set once at commit. Retrying the same request ID replays the committed receipt; a second request from the same profile for the same message commits an empty, receipt-backed no-op. Immutable messages and receipts are never modified.
- `Participation` — append-only transitions (`ChannelId`, nullable `SessionId`, `UserId`, `Joined`), timestamped and event-linked. The channel roster is the active (latest transition) membership from all session scopes plus the direct channel scope.
- Both tables are event-linked and part of the single transactional command pipeline. Replay filters include acknowledgement and participation events for matching channels and sessions. An explicit `allAcknowledgements: true` branch also matches `acknowledgement.created`; global `catalog`/`allMessages` and target-only filters still do not match these events on their own.

## Host surface

- Capability flags `profileAcknowledgements` and `participantRosters` on GET `status`; older hosts simply omit them, and clients hide the features.
- `liveAcknowledgementSummaries` advertises `allAcknowledgements` filter support (replaying `acknowledgement.created`). Snapshot message summaries expose `acknowledgementCount` and `latestAcknowledgingHandle` as of the snapshot cursor; immutable message receipts never change.
- Writes flow through the existing frozen `/requests` create pipeline with `callerUserId`; `subscribe` is rejected for these data objects.
- Bounded, paginated snapshot reads with `limit`, `asOf`, and `pageToken`, visible to anyone who can view the object:
  - `GET /messages/{id}/acknowledgements` → `{profile:{id,handle,displayName}, acknowledgedUtcMs}`
  - `GET /channels/{id}/participants` and `GET /sessions/{id}/participants` → `{profile}`
- A profile acknowledgement is self-asserted, not proof that a verified person read a message; an inbox or replay read never creates one.

## Clients

Web, WinUI, the shared .NET client, and the Claude Code plugin detect capabilities from status and expose explicit acknowledgements/rosters/participation. Success is reported only after the host commits the write; lost responses are retried with the frozen request ID and reconciled through the receipt. The plugin acknowledges a message only after surfacing it and receiving explicit user authorization, visits all active subscriptions in bounded pages, and deduplicates overlapping deliveries by message ID.
