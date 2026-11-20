# 03 — Verification and traceability

## Source and evidence

The tables below carry the original R01–R19 statements and A01–A50 scenarios from [source sections 2 and 20.2](../specs/local-messaging-design-specification-v1.md). Each acceptance scenario has **one primary closure owner**; earlier tasks may supply evidence and later tasks may repeat it. T040 performs final cumulative qualification of all fifty against the actual release candidate. Primary ownership is not a claim that execution has occurred.

Every task's Txxx-ACxx criteria are additional implementation acceptance detail. They do not replace or weaken the original scenarios. All application evidence status is **not_run** in this package.

## Required verification environments

| Layer | Required method | Insufficient substitute |
| --- | --- | --- |
| Pure contract/domain rules | Executable fixtures and focused unit tests | Reading DTO declarations |
| SQLite transactions/constraints/migrations | Actual Microsoft.Data.Sqlite/EF-backed temporary files and process-level scenarios | EF InMemory provider or mock repository |
| HTTP/3 readiness/publish/SSE | Actual Windows Kestrel, persisted localhost certificate, native exact-version client; inspect negotiated response version | HTTPS alone, TCP success, synthetic echo labeled domain success |
| Stream correctness | Real durable finite replay plus real streaming; deterministic byte fragmentation and race injection | Only parsing one complete string |
| Browser | Actual browser on F, including console/network/Origin behavior and local assets | JavaScript unit tests alone |
| WinUI | Actual Windows native process and picker/dispatcher/lifecycle behavior | Cross-platform compilation or mocked view model alone |
| Restore/shutdown | Real process/store lock/WAL-sidecar operations with controlled interruption | Merely copying a database while the host writes |
| Release | Exact published folder on clean Windows 11 x64 without development SDK/Node | Developer machine already carrying missing runtimes |

Pure tests can run elsewhere for fast feedback, but they cannot satisfy Windows, QUIC, WIC, WinUI, or clean-machine gates. Missing required execution means blocked. Testing need not all occur on one machine: build/unit evidence and clean-machine runtime evidence must identify the same source/configuration/dependency candidate and exact distributed artifact hashes.

## Planned verification interface

T001 implements this entry point; it does not exist in this documentation package.

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T007 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T007 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope checkpoint -Id CP03 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope milestone -Id M02 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope acceptance -Id A08 -Configuration Release
```

The script dispatches to real build/test/scenario executables and emits machine-readable and human-readable results. Unknown IDs, missing runners, unimplemented required checks, unsupported platforms, or missing prerequisites return nonzero and a precise not-run/blocked/failed status. Tests must never return PASS for a placeholder.

Milestone scope runs the cumulative executable checks; it does not perform or bypass the independent full review. Manual UI checks remain legitimate evidence when actual steps, expected/observed results, environment and reviewer are recorded.

T038–T040 add `-AgainstPackage <absolute-folder>` and `-EvidencePath <absolute-folder>` to development orchestration. The clean VM uses a separately published self-contained verification executable, with the planned form `LocalMessaging.Verify.exe --scope acceptance --id A03 --product <folder> --evidence <folder>`. The verification executable and exact CLI are implemented and documented by T040; neither PowerShell 7 nor a development SDK becomes a product runtime prerequisite. Production endpoints expose no fault-injection controls.

## Acceptance coverage: original A01–A50

| ID | Original scenario and required result | Primary closure task | Checkpoint | Milestone |
| --- | --- | --- | --- | --- |
| A01 | Start with no frontend open → Host is ready and serves API/subscriptions | T035 | CP13 | M06 |
| A02 | Close both frontends → Host remains running; native pub/sub still works | T035 | CP13 | M06 |
| A03 | Publish via native exact HTTP/3 request → Server commits; response.Version is 3.0 | T011 | CP04 | M02 |
| A04 | Subscribe via HTTP/3 → SSE arrives over HTTP/3 and flushes after commit | T022 | CP08 | M04 |
| A05 | Disable QUIC or supply bad certificate → Startup fails clearly; no false HTTP/3-ready status | T037 | CP13 | M06 |
| A06 | Access routes on wrong ports and attempt Host spoofing → Valid authority/wrong surface returns 404; invalid authority returns 400; static assets remain frontend-only | T023 | CP08 | M04 |
| A07 | Browser uses loopback HTTP/1.1 → Full functionality without browser HTTP/3 certificate requirements | T029 | CP12 | M05 |
| A08 | Create user, then retry same request → One user/event; same ID; normalized handle uniqueness works | T007 | CP03 | M02 |
| A09 | Create channel → Exactly one initial session; both owners are caller | T008 | CP03 | M02 |
| A10 | Compound create fails on first message/image → No channel/session/message/image/receipt is committed | T016 | CP06 | M03 |
| A11 | B creates session in A's channel → New session owned by B; channel still owned by A | T009 | CP03 | M02 |
| A12 | Existing IDs disagree or do not exist → Clear 404/422; no implicit creation | T010 | CP04 | M02 |
| A13 | Create message with new channel/session → Three objects and ordered events commit atomically | T010 | CP04 | M02 |
| A14 | Create session with new channel → Exactly one session, not an additional hidden initial session | T009 | CP03 | M02 |
| A15 | Lose response after commit and retry → Same IDs/result; no duplicate events | T024 | CP08 | M04 |
| A16 | Reuse request ID for changed data, caller, or operation → 409 idempotency_conflict | T021 | CP07 | M04 |
| A17 | Query subscription after create and host restart → Same saved definition; connection count reflects runtime | T023 | CP08 | M04 |
| A18 | Create channel/session after catalog/channel subscription → Appropriate discovery events are received | T022 | CP08 | M04 |
| A19 | Filter matches by channel and target simultaneously → One event in that subscription | T023 | CP08 | M04 |
| A20 | Two clients share a subscription with different cursors → Independent replay; neither consumes the other's events | T023 | CP08 | M04 |
| A21 | Commit between empty stream read and waiting → Wake/poll eventually delivers event without loss | T023 | CP08 | M04 |
| A22 | Slow/stalled consumer and concurrent publishers → Bounded resources; consumer closes/replays; publishers progress | T039 | CP14 | M06 |
| A23 | Publish during initial snapshot loading → Snapshot plus replay contains each relevant creation once after deduplication | T024 | CP08 | M04 |
| A24 | Type known @handle or use explicit target ID → Target row/inbox created; duplicates collapse | T014 | CP05 | M03 |
| A25 | Unknown mention, escaped mention, email, short/invalid token → Exact documented grammar; no accidental targeting | T014 | CP05 | M03 |
| A26 | Targeted message viewed by another user; reply without explicit targets → Public history visible; reply targets are not inherited | T015 | CP05 | M03 |
| A27 | Create root, child, grandchild → Depths 1/2/3 and root references correct | T015 | CP05 | M03 |
| A28 | Reply to level 3 → 422; no new message | T015 | CP05 | M03 |
| A29 | Reply to parent from another session → Rejected by domain rules/composite FK | T015 | CP05 | M03 |
| A30 | Send valid PNG/JPEG multipart → One message/image transaction; correct descriptor and bytes | T016 | CP06 | M03 |
| A31 | Oversize, spoofed, corrupt, animated, or dimension-bomb image → Rejected within configured resource bounds | T016 | CP06 | M03 |
| A32 | Read events/history containing images → Descriptors only; no base64/BLOB in JSON | T020 | CP07 | M04 |
| A33 | Send an HTML-looking text payload → Displayed as text in both clients | T040 | CP14 | M06 |
| A34 | Concurrent writes against real SQLite file → Consistent constraints, bounded busy behavior, no shared-context races | T039 | CP14 | M06 |
| A35 | Restart around transaction/notification boundary → Only committed data persists; replay recovers it | T039 | CP14 | M06 |
| A36 | Apply migrations, reopen database → Schema/version valid; no EnsureCreated conflict | T005 | CP02 | M01 |
| A37 | Backup and official restore → Domain/images/receipts coherent; epoch rotates; clients resynchronize | T036 | CP13 | M06 |
| A38 | Hold a slow stream for an extended period → No long-lived DB reader/transaction preventing normal checkpointing | T039 | CP14 | M06 |
| A39 | Same end-to-end scenario in WinUI and web → Equivalent objects, ownership, targets, threads, and errors | T040 | CP14 | M06 |
| A40 | Switch profile while a send is unresolved → Original caller retained; no relabelled send/subscription | T040 | CP14 | M06 |
| A41 | Fragment SSE across arbitrary byte boundaries → Correct UTF-8/frame parsing and cursor advancement | T040 | CP14 | M06 |
| A42 | Clean supported Windows VM → Self-contained host/WinUI start; webapp works without Node/CDN | T040 | CP14 | M06 |
| A43 | Select another profile without credentials → Same creation/viewing abilities; ownership adds no privilege | T040 | CP14 | M06 |
| A44 | Attempt domain edit/delete → No supported endpoint | T018 | CP06 | M03 |
| A45 | Close races with stream attachment → No indefinitely active post-close consumer; closure queryable | T023 | CP08 | M04 |
| A46 | Sparse filtered event query → Empty pages advance safely; no skipped matching event | T020 | CP07 | M04 |
| A47 | Cross-origin web form/fetch/image attempts → API origin/header protections hold; same-origin app still works | T040 | CP14 | M06 |
| A48 | Shutdown during normal operation → Bounded drain; surviving committed commands recover by receipt | T035 | CP13 | M06 |
| A49 | Epoch mismatch, future cursor, or stale subscription → Explicit recovery response; no endless silent wait | T036 | CP13 | M06 |
| A50 | Windows proxy/PAC configured → Supplied local clients and readiness probe still connect directly to loopback | T024 | CP08 | M04 |

[The acceptance CSV](../data/acceptance-coverage.csv) and [full JSON](../data/implementation-plan.json) also record supporting tasks, later regressions, the verification method, planned command, platform, and final T040 qualification.

Supporting references on a task do not necessarily close a scenario. Early H3/synthetic-stream probes support A03/A04; T011 and T022 close the actual persisted paths. T013 proves as-of pages, while T024 closes snapshot-plus-replay A23. T006 proves decoder feasibility; T016 closes full image API A31. Parser fixtures support A41; T040 closes both real frontend consumption paths. T005 closes initial migrate/reopen A36; later migration/upgrade changes require refreshed evidence.

Later regression tasks are not prerequisites of an earlier closure owner. Conversely, a primary owner must actually perform the complete source scenario even if earlier supporting work passed. All fifty remain subject to final candidate qualification.

## Requirement coverage: R01–R19

| ID | Source requirement | Implementation tasks | Source acceptance references |
| --- | --- | --- | --- |
| R01 | Run independently in the background | T001, T003, T035, T037, T038 | A01, A02 |
| R02 | Local HTTP/3/QUIC pub/sub | T003, T004, T011, T022, T023, T024, T035, T039 | A03, A04, A05 |
| R03 | Separate frontend API | T003, T004, T011, T018, T022, T024, T025, T029, T030, T034, T035 | A06, A07 |
| R04 | User/channel/session/message dataobjects | T002, T005, T007, T008, T009, T010, T013, T014, T015, T016, T017 | A08–A14 |
| R05 | Request contract carries dataobject | T002, T007, T008, T009, T010, T011, T012, T018, T021, T024 | A08–A16 |
| R06 | Queryable subscription response | T002, T019, T020, T021, T022, T023, T024, T029, T034, T036, T039 | A17–A22 |
| R07 | New channel creates first session; caller owns both | T008, T010, T021, T026, T031 | A09, A10 |
| R08 | Another caller creates/owns a session in an existing channel | T009, T010, T021, T026, T031 | A11 |
| R09 | Publish all channels and sessions for discovery/subscription | T008, T009, T013, T019, T020, T022, T025, T030 | A18, A23 |
| R10 | Target users with `@user` | T014, T015, T016, T017, T020, T027, T032 | A24–A26 |
| R11 | Threads no deeper than three levels | T015, T016, T017, T027, T032 | A27–A29 |
| R12 | Text and image messages | T006, T010, T014, T016, T018, T026, T028, T031, T033 | A30–A33 |
| R13 | EF Core + local SQLite | T005, T007, T012, T013, T016, T017, T019, T020, T023, T036, T037, T039 | A34–A38 |
| R14 | WinUI 3 frontend | T001, T030, T031, T032, T033, T034, T038 | A39–A42 |
| R15 | HTML/JS/CSS frontend without a framework | T001, T025, T026, T027, T028, T029, T038 | A39–A42 |
| R16 | Basic user creation without authentication | T007, T025, T030 | A08, A43 |
| R17 | Every user can create channel/session/message | T008, T009, T010, T014, T015, T025, T026, T027, T030, T031, T032 | A11, A43 |
| R18 | No content edit/delete in initial scope | T018 | A44 |
| R19 | Minimal reliability and operational behavior | T004, T005, T006, T007, T011, T012, T013, T016, T017, T018, T019, T020, T021, T022, T023, T024, T028, T029, T033, T034, T035, T036, T037, T038, T039 | A15–A16, A20–A22, A34–A38 |

Every row is also covered by final qualification T040. All 20 recorded source design decisions D01–D20 remain authoritative; the complete source is bundled. No decision is replaced by a task summary.

## Focused suites and fixtures

Use isolated temporary stores and explicit caller A/B fixtures. Reconcile IDs, request receipts, committed event sequences, image digests, and client-visible state. Include the source's mention grammar, finite replay bounds, three-level hierarchy, image corpus, and complete creation matrix.

Race tests should control the relevant boundary deterministically: before/after transaction commit, before response, before wake/poll, and during close/attach. Keep hooks in internal harness/test builds; do not introduce public crash or delay routes. Time-based assertions must test the specified finite bound with documented scheduling tolerance; they must not merely wait a guessed duration and declare success.

For T039, use the source's representative workload: 100 channels, 1,000 sessions, 20,000 text messages, 100 representative images, and up to 20 consumers. Report actual hardware, effective settings, latency/resource observations, and reconciliation results. The plan makes no unmeasured throughput or latency promise. Verify configured bounds and progress; do not relax thresholds to pass.

## Evidence freshness and negative tests

A passing record identifies exact source commit, configuration, effective limits, dependency inputs, OS/runtime, command, result and artifact hashes where relevant. A changed integration candidate requires impact analysis and reruns; full milestone closure always uses the final corrected candidate's cumulative suite.

Expected 404/422/conflict, invalid-certificate startup failure, and deliberately interrupted connections are passing negative scenarios only when the expected failure is asserted. Unexpected warnings or failures remain findings and block closure under [the gate policy](02-gates-and-reviews.md).

## Final release acceptance

T040 and CP14 assemble current evidence for every A01–A50, including actual both-frontend, H3, maintenance, browser boundary, and clean-machine behavior. M06 then performs the separate full cumulative review and resolves every finding/warning. If remediation changes binaries, regenerate the package, refresh affected acceptance evidence, rerun the cumulative gate, and review the final package identity.
