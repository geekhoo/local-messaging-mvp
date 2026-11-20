# M04 — Durable pub/sub and reusable client

**Status:** planned · **Gate:** not_run · **Full cumulative review:** mandatory

## Objective

Save/query subscriptions, implement bounded event replay, couple commands and subscriptions, stream over both listeners, and recover with the native client.

## Entry

M03 must have passed its full cumulative review and zero-unresolved-findings exit gate. Every task also requires its explicit dependency and checkpoint-entry gates.

## Exit

Persisted content and durable real-time delivery work end to end across snapshot, restart, sparse filters, and slow consumers. All checkpoints below and MG01–MG06 must pass. Resolve every issue/warning and rerun cumulative verification on the final corrected candidate.

**Scope note:** No frontend completion claim until M05; synthetic network proof cannot replace real persisted event evidence.

## Task map

| Task | Outcome | Task dependencies | Checkpoint-entry gate | Checkpoint |
| --- | --- | --- | --- | --- |
| T019 | Create, query, and close saved subscriptions | T018 | None | CP07 |
| T020 | Query durable events with safe bounded cursors | T019 | None | CP07 |
| T021 | Create objects and subscriptions atomically | T019, T020 | None | CP07 |
| T022 | Stream committed events over HTTP/3 and frontend HTTP | T020, T021 | CP07 | CP08 |
| T023 | Bound and recover concurrent live subscriptions | T022 | CP07 | CP08 |
| T024 | Run a reusable native client through snapshot and reconnect | T021, T022, T023 | CP07 | CP08 |

## Integrated checkpoints

### CP07 — Queryable durable subscriptions

**Required task gates:** T019, T020, T021. **Prior checkpoint gates:** None within this milestone. **Status:** not_run.

**Preconditions**

- M03 accepted; saved events containing catalog, text, images, and targets; controlled sparse/overlap filters.

**Integrated steps**

1. Create subscriptions for now/beginning/after, restart, query descriptors, and close idempotently.
2. Page finite replay using small count/byte budgets and long nonmatching spans; compare to a reference event set.
3. Create objects plus subscription atomically using beforeRequest and resolved scopes, then retry and query their events.

**Expected results**

- Definitions/start points/closed states persist; receipt state and subscription state remain distinct.
- Filters use union matching once per event; safe examined cursors advance on empty pages without skipping an excluded matching event.
- Coupled creation captures intended events including the automatic initial session; all parts commit or roll back together.

**Platform:** Windows 11 x64; real Kestrel and SQLite where implemented; actual browser/WinUI for UI assertions

**Planned verification command**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope checkpoint -Id CP07 -Configuration Release
```

**Evidence:** one integrated-candidate [checkpoint record](../templates/checkpoint-result.md), actual commands/results, fixture identity, applicable diagnostics, and closure links. Apply CG01–CG05; constituent task passes alone do not suffice.

**Unlocks:** T022, T023, T024 and CP08.

### CP08 — Recoverable live delivery and native client

**Required task gates:** T022, T023, T024. **Prior checkpoint gates:** CP07. **Status:** not_run.

**Preconditions**

- Integrated exact-H3 and F clients; real store; deterministic stream/race/fault harness.

**Integrated steps**

1. Run actual durable SSE over M and F; attach independent consumers and publish matching catalog/text/image events.
2. Inject empty-read/wait and close/attach races; stall one stream; fragment UTF-8/frame boundaries and wait through quiet heartbeats.
3. Load snapshot H while publishing, restart after commit/before notification, resume from safe cursors, and repeat with configured Windows proxy/PAC.

**Expected results**

- Actual H3 event frames are sent after commit; every matching retained event is recoverable and overlapping filters do not duplicate it.
- Consumers do not consume each other's cursor; close ends raced attachments; slow streams are bounded and release DB readers before network waits.
- Snapshot plus replay reconciles exactly once in client state; native client recovers with stable request/caller identity and direct loopback transport.

**Platform:** Windows 11 x64; real Kestrel and SQLite where implemented; actual browser/WinUI for UI assertions

**Planned verification command**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope checkpoint -Id CP08 -Configuration Release
```

**Evidence:** one integrated-candidate [checkpoint record](../templates/checkpoint-result.md), actual commands/results, fixture identity, applicable diagnostics, and closure links. Apply CG01–CG05; constituent task passes alone do not suffice.

**Unlocks:** M04 full review; after acceptance, parallel T025 web and T030 WinUI tracks.

## Atomic task cards

Every card inherits TG01–TG08 from [the gate policy](../plan/02-gates-and-reviews.md). Commands are planned interfaces implemented by T001, not executed evidence in this package. The [source specification](../specs/local-messaging-design-specification-v1.md) is authoritative; section/requirement/acceptance references below are local traceability.

### T019 — Create, query, and close saved subscriptions

**Outcome:** Durable subscription definitions with now/beginning/after starts, query/list/close operations, and idempotent operation receipts.

**Owner role:** subscriptions implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T018. **Previous milestone gate:** M03. **Checkpoint-entry gates:** None. **Contributes to:** CP07.

**Source:** sections 9.3, 10, 13.5, 14.2; requirements R06, R09, R13, R19; supporting/related scenarios A17, A49. **Primary closure ownership:** No exclusive source-scenario closure; task criteria still mandatory.

**Subtasks**

- **T019-ST01** — Validate/canonicalize filter branches, referenced IDs, starting cursor, owner, and scope limits.
- **T019-ST02** — Persist subscription create/close with globally namespaced request receipts.
- **T019-ST03** — Implement metadata/list queries and separate committed receipt state from current lifecycle state.
- **T019-ST04** — Run restart/retry/closed-resource/mismatched-caller scenarios.

**Acceptance criteria — all required**

- **T019-AC01** — Create returns an inspectable persisted definition and correct numeric start head for each supported mode.
- **T019-AC02** — Subscription definition and historical receipt survive restart; create/close retries do not create extra subscriptions.
- **T019-AC03** — Close preserves queryable metadata; new consumption returns 410, and mismatched supplied closer is rejected by the specified correctness rule.
- **T019-AC04** — Unknown filter IDs, empty filters, malformed/future/wrong-epoch cursors, and oversized filter sets fail correctly.
- **T019-AC05** — Create/close does not generate domain events; a closed historical subscription is not recreated by a command retry.
- **T019-AC06** — Live connection count is zero when no stream is attached and is explicitly transient rather than a delivery acknowledgement.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T019 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T019 -Configuration Release
```

**Observable demonstration:** Create several filters/start modes, restart, query/list, close, and replay the original create and close requests while inspecting historical/current state separately.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No subscription expiry, membership, shared-consumer acknowledgement, or unverified auth claims.

### T020 — Query durable events with safe bounded cursors

**Outcome:** A finite replay endpoint that implements OR matching, stable order, safe sparse scanning, and byte/count budgets.

**Owner role:** subscriptions implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T019. **Previous milestone gate:** M03. **Checkpoint-entry gates:** None. **Contributes to:** CP07.

**Source:** sections 10.2, 11.1, 11.2, 11.3; requirements R06, R09, R10, R13, R19; supporting/related scenarios A19, A32, A46, A49. **Primary closure ownership:** A32, A46.

**Subtasks**

- **T020-ST01** — Implement catalog/channel/session/target/all-message matching over committed events.
- **T020-ST02** — Apply cursor/start/epoch/head validation and materialize only matching payloads.
- **T020-ST03** — Bound headers scanned, result count, and 1 MiB payload bytes; calculate nextCursor from safely examined data.
- **T020-ST04** — Exercise empty/sparse/overlapping filters and boundary-size payloads.

**Acceptance criteria — all required**

- **T020-AC01** — Overlapping branches return one logical event in ascending sequence order.
- **T020-AC02** — An empty sparse page can advance nextCursor; iteration reaches the captured head without repeated scanning or missed matches.
- **T020-AC03** — No nextCursor skips a matching event excluded by count or byte budget.
- **T020-AC04** — At most 1,000 headers, requested bounded items, and 1 MiB matching payloads are processed per batch; one allowed event can always make progress.
- **T020-AC05** — All cursor error codes and conflicting cursor inputs behave as specified.
- **T020-AC06** — Image events carry descriptors only and no query retains a context while a consumer waits.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T020 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T020 -Configuration Release
```

**Observable demonstration:** Iterate sparse and overlapping subscriptions with one-item and small-byte budgets; compare the complete result set to a separately evaluated committed-event fixture.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No in-memory-only replay source, exclusive queue semantics, or retention trimming.

### T021 — Create objects and subscriptions atomically

**Outcome:** A create-and-subscribe path whose objects, ordered events, receipt, and saved filter share one transaction.

**Owner role:** backend implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T019, T020. **Previous milestone gate:** M03. **Checkpoint-entry gates:** None. **Contributes to:** CP07.

**Source:** sections 7.2, 8.7, 9, 10.1, 12.2, 13.5; requirements R05, R06, R07, R08, R19; supporting/related scenarios A10, A16, A17, A18. **Primary closure ownership:** A16.

**Subtasks**

- **T021-ST01** — Resolve catalog/resolvedChannel/resolvedSession scope for every supported create variant.
- **T021-ST02** — Capture the pre-command head and construct the matching subscription start cursor.
- **T021-ST03** — Commit or roll back subscriptions with all other command changes and reuse the global receipt namespace.
- **T021-ST04** — Exercise coupled failures, user-bootstrap rejection, cross-operation request-ID conflicts, and retry after subscription close.

**Acceptance criteria — all required**

- **T021-AC01** — Channel creation can resolve its automatically created initial session; session/message variants resolve their selected/new containers correctly.
- **T021-AC02** — beforeRequest starts before the command's own events, allowing relevant creation events to be queried.
- **T021-AC03** — An invalid coupled scope or any failing object validation leaves no subscription or partial graph.
- **T021-AC04** — A replay returns the original subscription reference even if it is now closed; it never reopens/replaces it.
- **T021-AC05** — Request ID conflicts are enforced across object create, subscription create, and close, including nullable bootstrap caller.
- **T021-AC06** — User bootstrap rejects subscribe; ordinary sends can omit it without creating a new saved subscription.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T021 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T021 -Configuration Release
```

**Observable demonstration:** Create a new channel/session/message with a coupled session subscription, query its creation events, close it, retry the original request, and inject a coupled rollback.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No automatic subscription per ordinary message or asynchronous request-job flow.

### T022 — Stream committed events over HTTP/3 and frontend HTTP

**Outcome:** The real subscription SSE endpoint on both surfaces, reusing finite replay and continuing live delivery.

**Owner role:** transport implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T020, T021. **Previous milestone gate:** M03. **Checkpoint-entry gates:** CP07. **Contributes to:** CP08.

**Source:** sections 5, 11.4, 11.5, 12.3; requirements R02, R03, R06, R09, R19; supporting/related scenarios A04, A18, A21, A38. **Primary closure ownership:** A04, A18.

**Subtasks**

- **T022-ST01** — Format durable events/checkpoints as SSE with flushing, stream cache/compression policy, and heartbeat comments.
- **T022-ST02** — Read bounded batches then release DbContext/reader before network writes.
- **T022-ST03** — Add post-commit broadcast wake notifications with the fallback poll to avoid relying on notification delivery.
- **T022-ST04** — Attach exact-H3 and frontend consumers and publish actual domain objects.

**Acceptance criteria — all required**

- **T022-AC01** — Exact-H3 subscription responses have Version 3.0 and deliver actual committed events after transaction commit.
- **T022-AC02** — Frontend consumers receive equivalent event framing and payloads over HTTP/1.1.
- **T022-AC03** — Replay transitions to live delivery without skipping events; channel subscriptions include future sessions and catalog subscriptions see new containers.
- **T022-AC04** — Checkpoint IDs advance only after prior matching events are flushed; comments/control events obey their cursor rules.
- **T022-AC05** — Quiet streams receive heartbeats, and cancellation disposes stream resources.
- **T022-AC06** — No context/reader/transaction is kept open while waiting for a client, and established SSE does not receive an appended JSON error document.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T022 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T022 -Configuration Release
```

**Observable demonstration:** Attach two real streams using M and F, create channels/sessions/messages, inspect actual event order/versions, and wait through a quiet heartbeat interval.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No synthetic SSE credited as A04, single competing-reader notification queue, or non-durable event bus.

### T023 — Bound and recover concurrent live subscriptions

**Outcome:** Correct stream admission/closure, independent cursors, backpressure, and restart recovery under controlled concurrency.

**Owner role:** reliability implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T022. **Previous milestone gate:** M03. **Checkpoint-entry gates:** CP07. **Contributes to:** CP08.

**Source:** sections 10.3, 11.5, 12.3, 12.4, 12.5, 17.2; requirements R02, R06, R13, R19; supporting/related scenarios A06, A17, A19, A20, A21, A22, A35, A38, A45. **Primary closure ownership:** A06, A17, A19, A20, A21, A45.

**Subtasks**

- **T023-ST01** — Coordinate durable active checks, registry registration, close/cancellation, and per-batch lifecycle rechecks.
- **T023-ST02** — Enforce stream-count and write-time limits without blocking publishers on consumer speed.
- **T023-ST03** — Add synchronized race/fault scenarios for empty-read/wait, close/attach, and commit/before-wake restart.
- **T023-ST04** — Rerun surface isolation with the complete subscription route set and compare independent consumer results.

**Acceptance criteria — all required**

- **T023-AC01** — Two consumers of one subscription replay independently; no shared cursor causes one to steal events.
- **T023-AC02** — A close/attach race cannot leave an indefinitely live closed subscription; already transmitted frames are handled according to documented semantics.
- **T023-AC03** — A slow/stalled receiver is bounded/closed, releases resources, and cannot indefinitely block ordinary publishers.
- **T023-AC04** — Commit-after-empty-read and crash-after-commit-before-wake scenarios lose no committed matching event after recovery.
- **T023-AC05** — Registry counts return to baseline after detach/timeout; query metadata reports actual current connections.
- **T023-AC06** — Actual subscription/stream routes preserve listener isolation and deduplicate overlapping filters.
- **T023-AC07** — The deterministic scenarios use synchronization hooks confined to verification tooling and produce no unexpected normal-operation warnings.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T023 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T023 -Configuration Release
```

**Observable demonstration:** Run two offset consumers, race a close with an attach, stall one receiver, and kill/restart after commit before wake; reconcile results with SQLite.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No per-client durable acknowledgements, infinite retry loops, or blanket timeout increases to mask failures.

### T024 — Run a reusable native client through snapshot and reconnect

**Outcome:** A reusable .NET client and executable sample flow that creates/queries content, initializes from a snapshot, and recovers event consumption.

**Owner role:** client implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T021, T022, T023. **Previous milestone gate:** M03. **Checkpoint-entry gates:** CP07. **Contributes to:** CP08.

**Source:** sections 5.3, 5.4, 9, 11.5, 13.3, 16.6; requirements R02, R03, R05, R06, R19; supporting/related scenarios A15, A23, A35, A41, A49, A50. **Primary closure ownership:** A15, A23, A50.

**Subtasks**

- **T024-ST01** — Implement shared serialization/errors, stable command retries, direct-loopback HTTP modes, exact-H3 certificate/version validation, and cancellation.
- **T024-ST02** — Use ResponseHeadersRead and incremental SSE parsing with byte/comment liveness and bounded reconnect.
- **T024-ST03** — Implement snapshot-at-H plus replay, idempotent state application, store-epoch reset, and independent client cursors.
- **T024-ST04** — Exercise text/image/coupled commands, fragmented streams, host restart, unknown outcomes, and configured proxy/PAC.

**Acceptance criteria — all required**

- **T024-AC01** — The same client supports F and explicit M mode, with actual exact-H3 evidence and no proxy detour.
- **T024-AC02** — Uncertain text/image/coupled sends retry the original caller/request/payload and reconcile the committed result.
- **T024-AC03** — Snapshot plus concurrent publication/replay produces the complete committed view without duplicate applied messages.
- **T024-AC04** — Split UTF-8/CRLF/multiline frames parse correctly; keepalive bytes prevent false idle reconnects.
- **T024-AC05** — There is one reconnect loop per subscription with bounded jitter/backoff, cancellation, and Retry-After handling.
- **T024-AC06** — Epoch mismatch/future cursor/closed subscription produces explicit resynchronization rather than a silent wait.
- **T024-AC07** — The runnable sample uses real host/SQLite behavior; parser-only fixtures are supporting evidence for later dual-frontend A41 closure.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T024 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T024 -Configuration Release
```

**Observable demonstration:** Run the native sample, load a snapshot while another caller publishes, interrupt/restart the host, and confirm complete canonical state after reconnect.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No offline send queue, UI framework, automatic remote endpoint support, or runtime dependency on an agent platform.

## M04 mandatory full review and closure

After CP07, CP08 pass, review all implemented functionality from M01 through M04, including inherited code and behavior. Apply all ten review dimensions and MG01–MG06 in [the gate policy](../plan/02-gates-and-reviews.md).

Use [the milestone template](../templates/milestone-review.md). Record every finding/warning, implement and independently verify fixes, then run the cumulative suite and checkpoint scenarios on the final corrected candidate. No unresolved issue/warning of any severity can remain. A test-only pass does not replace full review.

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope milestone -Id M04 -Configuration Release
```

**Acceptance record must identify:** final commit, effective configuration/dependency versions, required platform observations, task/checkpoint evidence, zero-open-findings result, reviewer, and relevant artifact hashes. This milestone is currently **not run**.
