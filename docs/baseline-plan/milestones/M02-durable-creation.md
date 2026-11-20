# M02 — Durable creation and ownership

**Status:** planned · **Gate:** not_run · **Full cumulative review:** mandatory

## Objective

Create users, channels, sessions, and root text through one durable command pipeline with receipts, events, atomicity, and retry recovery.

## Entry

M01 must have passed its full cumulative review and zero-unresolved-findings exit gate. Every task also requires its explicit dependency and checkpoint-entry gates.

## Exit

All creation combinations preserve owners and committed identity through real SQLite and actual HTTP/3 requests. All checkpoints below and MG01–MG06 must pass. Resolve every issue/warning and rerun cumulative verification on the final corrected candidate.

**Scope note:** Targets, replies, images, and subscriptions remain explicitly unavailable until their planned tasks.

## Task map

| Task | Outcome | Task dependencies | Checkpoint-entry gate | Checkpoint |
| --- | --- | --- | --- | --- |
| T007 | Create and retrieve an idempotent user | T002, T004, T005 | None | CP03 |
| T008 | Create a channel with exactly one initial session | T007 | None | CP03 |
| T009 | Create independently owned sessions in existing or new channels | T008 | None | CP03 |
| T010 | Publish immutable root text through all container combinations | T009 | CP03 | CP04 |
| T011 | Exercise real persisted publishing across both API surfaces | T010 | CP03 | CP04 |
| T012 | Recover uncertain writes and concurrent retries | T010, T011 | CP03 | CP04 |

## Integrated checkpoints

### CP03 — Idempotent profiles and independent ownership

**Required task gates:** T007, T008, T009. **Prior checkpoint gates:** None within this milestone. **Status:** not_run.

**Preconditions**

- M01 accepted; clean temporary store; caller A and B are created through the real API.

**Integrated steps**

1. Create/retry A and B; query users, receipts, and committed events.
2. A creates a channel with its initial session; B creates a session in A's channel.
3. Create a session with a new channel; reject missing/existing-ID mismatches and reopen the store.

**Expected results**

- Duplicate request identity creates one profile/event and returns the same result; handle normalization is enforced.
- A owns its channel/first session; B owns only the new session; new-channel session creation makes exactly one session.
- IDs, owners, receipts, and ordered events survive reopen; invalid operations leave no partial objects.

**Platform:** Windows 11 x64; real Kestrel and SQLite where implemented; actual browser/WinUI for UI assertions

**Planned verification command**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope checkpoint -Id CP03 -Configuration Release
```

**Evidence:** one integrated-candidate [checkpoint record](../templates/checkpoint-result.md), actual commands/results, fixture identity, applicable diagnostics, and closure links. Apply CG01–CG05; constituent task passes alone do not suffice.

**Unlocks:** T010, T011, T012 and CP04.

### CP04 — Persisted publish and uncertain-write recovery

**Required task gates:** T010, T011, T012. **Prior checkpoint gates:** CP03. **Status:** not_run.

**Preconditions**

- Integrated real host/store and exact-H3 native client; deterministic process-fault harness.

**Integrated steps**

1. Publish text into existing session, new session, and new channel/session via M and F.
2. Lose a response after commit, retry the original request through the other permitted surface, and compare the canonical receipt.
3. Interrupt before/after commit and issue concurrent same/different request IDs; inspect recovered rows/events/receipts.

**Expected results**

- Actual persisted publish returns HTTP/3 version 3.0; M and F share domain behavior.
- Compound message creation produces the prescribed graph and event order; equivalent retry adds no rows/events.
- Only committed graphs survive; changed caller/operation/payload conflicts; finite write waits expose actionable errors.

**Platform:** Windows 11 x64; real Kestrel and SQLite where implemented; actual browser/WinUI for UI assertions

**Planned verification command**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope checkpoint -Id CP04 -Configuration Release
```

**Evidence:** one integrated-candidate [checkpoint record](../templates/checkpoint-result.md), actual commands/results, fixture identity, applicable diagnostics, and closure links. Apply CG01–CG05; constituent task passes alone do not suffice.

**Unlocks:** M02 full review; after acceptance, T013 and M03.

## Atomic task cards

Every card inherits TG01–TG08 from [the gate policy](../plan/02-gates-and-reviews.md). Commands are planned interfaces implemented by T001, not executed evidence in this package. The [source specification](../specs/local-messaging-design-specification-v1.md) is authoritative; section/requirement/acceptance references below are local traceability.

### T007 — Create and retrieve an idempotent user

**Outcome:** The first real write slice: create/query a user together with its durable event and request receipt through the shared API.

**Owner role:** backend implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T002, T004, T005. **Previous milestone gate:** M01. **Checkpoint-entry gates:** None. **Contributes to:** CP03.

**Source:** sections 6, 7.1, 8.3, 9, 12.2, 14; requirements R04, R05, R13, R16, R19; supporting/related scenarios A08, A15, A16, A43. **Primary closure ownership:** A08.

**Subtasks**

- **T007-ST01** — Implement command normalization/hash, global request-ID namespace, write gate, explicit transaction, and normalized receipt storage.
- **T007-ST02** — Add user creation with caller bootstrap rules, handle normalization/uniqueness, detail/list queries, and receipt retrieval.
- **T007-ST03** — Append user.created in the same transaction and assign persistent creation sequence.
- **T007-ST04** — Run the scenario through messaging and frontend surfaces, including restart and duplicate requests.

**Acceptance criteria — all required**

- **T007-AC01** — One valid command creates exactly one user, one user.created event, and one committed receipt.
- **T007-AC02** — Same normalized request ID/payload returns the same user and event count, including after restart.
- **T007-AC03** — Changed payload/caller/operation under an existing request ID returns 409 without writes.
- **T007-AC04** — Handle normalization and uniqueness, bounds, and null/absent caller rules behave exactly as specified.
- **T007-AC05** — Both surfaces use one handler and equivalent contracts; selecting/creating a user requires no authentication.
- **T007-AC06** — Targets/parents/images/coupled-subscribe features not implemented yet cannot be silently ignored by other command variants.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T007 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T007 -Configuration Release
```

**Observable demonstration:** POST a profile, GET its user/receipt, restart, retry it, then submit a conflicting payload and inspect persisted counts.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No passwords, account sessions, user edit/delete, or delayed idempotency retrofit.

### T008 — Create a channel with exactly one initial session

**Outcome:** A channel-create API slice that atomically creates and exposes a caller-owned channel and first session.

**Owner role:** backend implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T007. **Previous milestone gate:** M01. **Checkpoint-entry gates:** None. **Contributes to:** CP03.

**Source:** sections 7.1, 7.2, 8.1, 9, 15.1; requirements R04, R05, R07, R09, R17; supporting/related scenarios A09, A10, A12. **Primary closure ownership:** A09.

**Subtasks**

- **T008-ST01** — Implement channel request handler and the default General initial-session title.
- **T008-ST02** — Add channel/session detail and basic directory queries needed to inspect results.
- **T008-ST03** — Create ordered channel.created/session.created events and full receipt object references.
- **T008-ST04** — Test ownership, defaults, invalid caller/input, retries, and rollback.

**Acceptance criteria — all required**

- **T008-AC01** — Every accepted channel create produces exactly one channel and one initial session, both owned by the caller.
- **T008-AC02** — A supplied first-session title is preserved; omission uses General.
- **T008-AC03** — The event order is channel then session; the receipt distinguishes resolved objects from newly created objects.
- **T008-AC04** — Any invalid initial-session/channel input rolls back all objects, events, and receipt.
- **T008-AC05** — Same-request retries return original IDs; unknown caller IDs do not create users or containers implicitly.
- **T008-AC06** — The new IDs are queryable through both surfaces and are suitable subscription scopes once subscriptions are implemented.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T008 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T008 -Configuration Release
```

**Observable demonstration:** Create a channel with and without an initial title, query the two owners, retry, then submit invalid first-session input and compare database counts.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No per-channel worker, channel memberships, ownership privileges, or channel rename/archive.

### T009 — Create independently owned sessions in existing or new channels

**Outcome:** A session-create slice covering both existing-channel and new-channel selectors with independent session ownership.

**Owner role:** backend implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T008. **Previous milestone gate:** M01. **Checkpoint-entry gates:** None. **Contributes to:** CP03.

**Source:** sections 7.1, 8.4, 15.2; requirements R04, R05, R08, R09, R17; supporting/related scenarios A11, A12, A14. **Primary closure ownership:** A11, A14.

**Subtasks**

- **T009-ST01** — Implement explicit existing/new channel resolution for a session request.
- **T009-ST02** — Assign caller ownership to the new session without changing an existing channel.
- **T009-ST03** — Treat a session with a new channel as its one initial session.
- **T009-ST04** — Add runnable A/B ownership, missing-ID, invalid-selector, and replay scenarios.

**Acceptance criteria — all required**

- **T009-AC01** — User B can create a session in A's channel; the channel remains owned by A and the new session is owned by B.
- **T009-AC02** — A session command selecting a new channel creates one channel and exactly one session.
- **T009-AC03** — Unknown existing channel IDs return 404; conflicting selector fields are rejected without implicit creation.
- **T009-AC04** — All objects/events/receipt commit atomically with stable IDs on retry.
- **T009-AC05** — Any logical user has the same create/view capabilities regardless of recorded owner.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T009 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T009 -Configuration Release
```

**Observable demonstration:** Create A/B, let A create a channel, let B create a session there, then create B's session with a new channel and verify both graphs.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No transfer of ownership, owner-only permissions, or extra default session on the new-channel path.

### T010 — Publish immutable root text through all container combinations

**Outcome:** A runnable text-message create/query slice for existing sessions, new sessions, and new channel/session graphs.

**Owner role:** backend implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T009. **Previous milestone gate:** M01. **Checkpoint-entry gates:** CP03. **Contributes to:** CP04.

**Source:** sections 7.1, 7.2, 8.5, 9, 11.1, 14; requirements R04, R05, R07, R08, R12, R17; supporting/related scenarios A10, A12, A13, A43. **Primary closure ownership:** A12, A13.

**Subtasks**

- **T010-ST01** — Implement root text content, server-assigned author/root/depth/sequence, and message detail/basic history.
- **T010-ST02** — Reuse the command transaction for all three container selection combinations.
- **T010-ST03** — Validate channel/session consistency and text bounds before any commit.
- **T010-ST04** — Include ordered graph events and normalized receipts; fail explicitly on future unsupported message fields.

**Acceptance criteria — all required**

- **T010-AC01** — Existing channel/session text creates only the message; new-session and new-channel forms create the intended graph with correct ownership.
- **T010-AC02** — Root messages have null parent, own root ID, depth 1, caller author, and committed string sequence.
- **T010-AC03** — Invalid or mismatched existing references, whitespace-only text, oversize input, and unauthorized write fields leave zero partial writes.
- **T010-AC04** — Channel/session/message creation events are ordered and invisible before commit.
- **T010-AC05** — Retrying an unchanged request returns original IDs and does not append new events.
- **T010-AC06** — Nonempty targets, parent IDs, image content, or coupled subscription options remain explicitly unavailable until their implementing task passes.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T010 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T010 -Configuration Release
```

**Observable demonstration:** Send and query text for each container combination, inspect owner/author/root fields, then test invalid references and rollback.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No threads, mentions, images, or silent acceptance of fields reserved for later tasks.

### T011 — Exercise real persisted publishing across both API surfaces

**Outcome:** A runnable cross-surface command/receipt scenario that proves actual stored messages over exact HTTP/3 and frontend HTTP/1.1.

**Owner role:** contracts implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T010. **Previous milestone gate:** M01. **Checkpoint-entry gates:** CP03. **Contributes to:** CP04.

**Source:** sections 5, 9, 13, 20.1; requirements R02, R03, R05, R19; supporting/related scenarios A03, A15, A16. **Primary closure ownership:** A03.

**Subtasks**

- **T011-ST01** — Complete consistent command response metadata, Location generation, receipt retrieval, and method/error mapping for implemented routes.
- **T011-ST02** — Add a native persisted-publication scenario using exact H3 and direct loopback handlers.
- **T011-ST03** — Retry the same request through the other surface and compare normalized results and database/event counts.
- **T011-ST04** — Verify receipt unknown/in-flight semantics and avoid claiming byte-identical transport metadata.

**Acceptance criteria — all required**

- **T011-AC01** — A genuine H3 command commits a message and reports response.Version 3.0; querying the stored message proves the real handler was used.
- **T011-AC02** — The same command retried through F returns the same created IDs and no extra rows/events.
- **T011-AC03** — Receipt queries return committed state and operation; no visible receipt maps to request_not_committed without proving a request could never commit.
- **T011-AC04** — Location/relative paths derive from the configured accepted surface and cannot be poisoned by Host.
- **T011-AC05** — Errors and body validation are consistent across M/F; normal-operation diagnostics are clean.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T011 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T011 -Configuration Release
```

**Observable demonstration:** Publish a text message using M with exact H3, query it via F, replay via F, and verify one committed graph and receipt.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No synthetic probe credited as persisted-publication acceptance; no extra public test route.

### T012 — Recover uncertain writes and concurrent retries

**Outcome:** A reusable process-level write/fault scenario and corrected command behavior for commit boundaries, collisions, and finite contention.

**Owner role:** reliability implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T010, T011. **Previous milestone gate:** M01. **Checkpoint-entry gates:** CP03. **Contributes to:** CP04.

**Source:** sections 9.2, 12.2, 12.5, 14.6, 20.2; requirements R05, R13, R19; supporting/related scenarios A10, A15, A16, A34, A35. **Primary closure ownership:** No exclusive source-scenario closure; task criteria still mandatory.

**Subtasks**

- **T012-ST01** — Add narrowly scoped test-host fault hooks before commit and after commit/before response; keep them out of production routes.
- **T012-ST02** — Exercise concurrent identical and different request IDs against real SQLite.
- **T012-ST03** — Enforce bounded writer-gate/provider waits and map contention without acknowledging uncommitted writes.
- **T012-ST04** — Inspect receipts/events/entities after forced process termination and restart.

**Acceptance criteria — all required**

- **T012-AC01** — Failure before commit leaves no graph/receipt/event; failure after commit returns the original result on retry.
- **T012-AC02** — Concurrent identical requests yield one committed result; conflicting uses of the same ID yield deterministic conflict.
- **T012-AC03** — Concurrent distinct requests remain consistent with required FKs, event order, and no shared-context errors.
- **T012-AC04** — Write wait/provider timeout is finite and returns the specified retryable error while preserving the original retry identity.
- **T012-AC05** — The fault scenario runs repeatedly with explicit synchronization points, not timing-only sleeps; expected injected failures are distinguished from unexpected diagnostics.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T012 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T012 -Configuration Release
```

**Observable demonstration:** Terminate the host at each controlled write boundary, restart and retry, then run concurrent requests and compare all durable objects to receipts/events.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No production crash API, persistent job queue, or exactly-once client-display claim.

## M02 mandatory full review and closure

After CP03, CP04 pass, review all implemented functionality from M01 through M02, including inherited code and behavior. Apply all ten review dimensions and MG01–MG06 in [the gate policy](../plan/02-gates-and-reviews.md).

Use [the milestone template](../templates/milestone-review.md). Record every finding/warning, implement and independently verify fixes, then run the cumulative suite and checkpoint scenarios on the final corrected candidate. No unresolved issue/warning of any severity can remain. A test-only pass does not replace full review.

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope milestone -Id M02 -Configuration Release
```

**Acceptance record must identify:** final commit, effective configuration/dependency versions, required platform observations, task/checkpoint evidence, zero-open-findings result, reviewer, and relevant artifact hashes. This milestone is currently **not run**.
