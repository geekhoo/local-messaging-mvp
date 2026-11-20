# Store implementation review

**Scope:** `LocalMessaging.Application`, `LocalMessaging.Infrastructure`, and the real SQLite store verification harness. This is supporting implementation evidence. It does not accept a milestone or qualify Windows, HTTP/3, WIC decoding, or WinUI.

## Implemented behavior

- Strict v1 request normalization, unknown/duplicate member rejection, global request identities, normalized canonical hashing, and immutable receipt replay.
- EF Core SQLite mappings and an actual generated initial migration. The store holds an exclusive directory lock, opens short-lived contexts, applies foreign-key/FULL/WAL settings, checks integrity, and rejects unsupported migration history before readiness.
- Atomic creation of users, channels, sessions, text/image messages, targets, events, receipts, and coupled subscriptions. Owner fields preserve channel/session independence. Mention resolution and explicit targets produce one canonical public target set. Thread parents remain in the same session and depth is limited to three levels.
- Snapshot-bound catalogue/message/thread queries, scoped continuation tokens, ancestor context, bounded image metadata projections, live operational subscription pages, and durable finite event replay with safe scan checkpoints.
- Offline coherent backup and restore, retained safety copies, epoch rotation and subscription closure before publication, checked checkpoint/sidecar retirement, and same-volume replacement.

## Executed checks

The Release store build completed with **zero warnings and zero errors**. The commands used the pinned .NET 10 SDK and centrally pinned EF Core 10.0.15:

```text
dotnet build tests/LocalMessaging.StoreTests/LocalMessaging.StoreTests.csproj -c Release --no-restore -m:1 --nologo
dotnet tests/LocalMessaging.StoreTests/bin/Release/net10.0/LocalMessaging.StoreTests.dll --output evidence/store-tests.json
dotnet tests/LocalMessaging.StoreTests/bin/Release/net10.0/LocalMessaging.StoreTests.dll --workload --output evidence/store-workload.json
```

`store-tests.json` and `store-tests.log` record **33 passing scenarios and zero failures**. These include normal and conflicting retries, concurrent identical commands, compound rollback, configured limits, targeting/threading, image-byte identity, snapshot pages, sparse/bounded replay, independent consumers, restart, restore interruptions, actual relational constraint rejection, a held SQLite writer timeout, unknown migration history, and preservation of corrupt database bytes.

`store-workload.json` and `store-workload.log` record a complete workload with 100 channels, 1,000 sessions, 20,000 text messages, 100 image messages, and 20 independent finite event consumers. Every consumer reconciled all 20,100 committed message IDs and reached sequence 21,203. Deliberate duplicate replay was deduplicated, identical request retries created no second message, and WAL checkpoint completed after the consumers finished. The JSON report contains the actual environment, effective limits, timings, process memory, and database size. These measurements describe this execution only.

## Findings resolved during implementation

| Finding | Resolution and evidence |
|---|---|
| A handwritten initial migration snapshot omitted EF model metadata and triggered `PendingModelChangesWarning`. | Replaced it with actual `dotnet-ef` 10.0.15 generated migration, designer, and immutable model snapshot. Fresh migration/startup passes without suppressing the warning. |
| The first workload harness could stop against a stale per-page head when publication finished during a read. | The publisher now supplies its final committed head through a completion task; each consumer must reach that exact head before stopping. The complete workload then reconciled every committed ID for all 20 consumers. |
| Injected timeout limits were derived after validating the fallback settings. | Validate the effective derived timeouts before constructing the provider configuration. Actual writer contention is bounded and leaves no receipt or partial graph. |
| Direct SQLite I/O/full/open exceptions needed the same application error mapping as wrapped update exceptions. | Both direct and wrapped write-path failures map to `storage_unavailable`; no retry loop was added. Physical disk-full fault qualification remains a platform scenario rather than an asserted result here. |

## Boundary and remaining qualification

The application accepts an internal `ValidatedImage` from the host's decoder and verifies byte limits, dimensions, media type, and digest before persisting it. The Linux store harness supplies structurally framed PNG fixtures directly. Its image scenarios prove atomic storage and identity behavior; they do not claim that WIC executed.

The workload uses actual file-backed SQLite and finite replay consumers. HTTP stream admission, slow network writes, close/attach races, exact HTTP/3 negotiation, Windows process interruption, WIC adversarial image decoding, and published Windows frontend execution require their corresponding host/platform evidence. No unexecuted platform gate is represented as passed.
