# M06 — Operations and release qualification

**Status:** planned · **Gate:** not_run · **Full cumulative review:** mandatory

## Objective

Complete independent startup/shutdown, diagnostics, safe maintenance, self-contained distribution, workload/fault qualification, and clean-machine verification.

## Entry

M05 must have passed its full cumulative review and zero-unresolved-findings exit gate. Every task also requires its explicit dependency and checkpoint-entry gates.

## Exit

The exact release candidate satisfies all A01–A50 and every milestone finding/warning is resolved and reverified. All checkpoints below and MG01–MG06 must pass. Resolve every issue/warning and rerun cumulative verification on the final corrected candidate.

**Scope note:** Release qualification and the final full review are separate gates; neither is implied by a successful build.

## Task map

| Task | Outcome | Task dependencies | Checkpoint-entry gate | Checkpoint |
| --- | --- | --- | --- | --- |
| T035 | Operate the independent host through launch and stop | T029, T034 | None | CP13 |
| T036 | Back up and restore the local store safely | T035 | None | CP13 |
| T037 | Expose bounded configuration and operator diagnostics | T035 | None | CP13 |
| T038 | Publish the self-contained Windows folder package | T035, T036, T037 | CP13 | CP14 |
| T039 | Run repeatable workload and fault qualification | T036, T037 | CP13 | CP14 |
| T040 | Qualify the published MVP on a clean Windows machine | T038, T039 | CP13 | CP14 |

## Integrated checkpoints

### CP13 — Independent operation and safe maintenance

**Required task gates:** T035, T036, T037. **Prior checkpoint gates:** None within this milestone. **Status:** not_run.

**Preconditions**

- M05 accepted; source-build candidate with complete UIs; isolated test data/cert/config directory.

**Integrated steps**

1. Start without a UI, launch WinUI via the known bundled-path convention, close both UIs, and verify native pub/sub still works.
2. Inspect effective diagnostics; exercise port/certificate/lock/config failures, optional startup enable/disable, and bounded Stop.
3. Back up, add later history, restore with before/after-publication interruptions, then reopen both clients.

**Expected results**

- Single host lifecycle is independent and direct-loopback readiness is truthful; no opt-in autostart is enabled silently.
- Failures are actionable and logs bounded/non-content; committed requests survive shutdown through receipts.
- Old store remains coherent before restore publication; published restored history has a new epoch and no old sidecars; clients resynchronize.

**Platform:** Windows 11 x64; real Kestrel and SQLite where implemented; actual browser/WinUI for UI assertions

**Planned verification command**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope checkpoint -Id CP13 -Configuration Release
```

**Evidence:** one integrated-candidate [checkpoint record](../templates/checkpoint-result.md), actual commands/results, fixture identity, applicable diagnostics, and closure links. Apply CG01–CG05; constituent task passes alone do not suffice.

**Unlocks:** T038, T039, T040 and CP14.

### CP14 — Qualified distributable MVP candidate

**Required task gates:** T038, T039, T040. **Prior checkpoint gates:** CP13. **Status:** not_run.

**Preconditions**

- Completed published product folder; exact hashes/commit/configuration; clean Windows 11 x64 VM without development SDK or Node; separate self-contained verification executable.

**Integrated steps**

1. Run the actual distributed host/WinUI/static assets and full paired UI plus exact-H3 scenario.
2. Run the deterministic representative workload/fault suite against those exact binaries and reconcile committed/replayed/client state.
3. Execute or fully reenact every A01–A50 with current evidence, maintenance and upgrade preservation included; prepare the full-review packet.

**Expected results**

- Both .NET and Windows App SDK runtime content is sufficient on the clean machine; no development-tool runtime dependency is required.
- Resource/transaction/race/cursor limits remain correct under the declared workload; no committed relevant event is lost.
- All fifty source scenarios have passing evidence; zero unresolved warnings/findings remain; package identity matches the reviewed candidate.

**Platform:** Windows 11 x64; real Kestrel and SQLite where implemented; actual browser/WinUI for UI assertions

**Planned verification command**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope checkpoint -Id CP14 -Configuration Release
```

**Evidence:** one integrated-candidate [checkpoint record](../templates/checkpoint-result.md), actual commands/results, fixture identity, applicable diagnostics, and closure links. Apply CG01–CG05; constituent task passes alone do not suffice.

**Unlocks:** M06 full cumulative review, remediation, final corrected-candidate verification, then MVP release acceptance.

## Atomic task cards

Every card inherits TG01–TG08 from [the gate policy](../plan/02-gates-and-reviews.md). Commands are planned interfaces implemented by T001, not executed evidence in this package. The [source specification](../specs/local-messaging-design-specification-v1.md) is authoritative; section/requirement/acceptance references below are local traceability.

### T035 — Operate the independent host through launch and stop

**Outcome:** Product start/stop paths, native known-path host launch, optional sign-in startup, and independent background lifetime.

**Owner role:** operations implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T029, T034. **Previous milestone gate:** M05. **Checkpoint-entry gates:** None. **Contributes to:** CP13.

**Source:** sections 5.4, 16.4, 18.1, 18.2, 18.4; requirements R01, R02, R03, R19; supporting/related scenarios A01, A02, A05, A48, A50. **Primary closure ownership:** A01, A02, A48.

**Subtasks**

- **T035-ST01** — Add Start/Stop launcher behavior and WinUI known-installation-path host startup with bounded readiness.
- **T035-ST02** — Handle existing host, stale descriptor, duplicate launch, port/lock failure, and actual instance verification.
- **T035-ST03** — Add opt-in current-user sign-in startup registration/unregistration without enabling it by default.
- **T035-ST04** — Implement frontend-only operator stop, drain/cancellation, and descriptor/lock cleanup.

**Acceptance criteria — all required**

- **T035-AC01** — Host starts and serves real commands/subscriptions with no UI open; closing both UIs leaves exact-H3 messaging operational.
- **T035-AC02** — WinUI starts only the bundled known executable; endpoint metadata cannot choose an arbitrary executable.
- **T035-AC03** — Duplicate launch reuses one valid host; stale descriptor/conflicting port/lock produces useful failure instead of overwrite or port drift.
- **T035-AC04** — Optional sign-in startup changes only on an explicit operator choice and can be removed.
- **T035-AC05** — Stop responds then performs a bounded drain; known committed work can be reconciled after restart.
- **T035-AC06** — Direct-loopback clients/probes remain correct with proxy/PAC settings; user-logoff survival is not claimed.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T035 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T035 -Configuration Release
```

**Observable demonstration:** Launch headlessly, open/close both UIs, publish over exact H3, try duplicate/stale startup, then stop and restart while reconciling a committed command.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No Windows Service installer, elevation requirement, machine-wide autostart, or automatic startup registration without operator choice.

### T036 — Back up and restore the local store safely

**Outcome:** Runnable offline maintenance commands that preserve database consistency and publish restored history with a new epoch.

**Owner role:** operations implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T035. **Previous milestone gate:** M05. **Checkpoint-entry gates:** None. **Contributes to:** CP13.

**Source:** sections 11.2, 15.5, 18.6; requirements R06, R13, R19; supporting/related scenarios A35, A37, A49. **Primary closure ownership:** A37, A49.

**Subtasks**

- **T036-ST01** — Acquire the exclusive lock before inspecting or copying live data; create a coherent safety backup.
- **T036-ST02** — Prepare/validate a temporary restored database and rotate epoch/close subscriptions transactionally before publication.
- **T036-ST03** — Checkpoint/close stores and coordinate old WAL/SHM retirement with atomic replacement on the same volume.
- **T036-ST04** — Exercise deterministic interruption before/after publication and client reinitialization.

**Acceptance criteria — all required**

- **T036-AC01** — Backup contains coherent entities, images, receipts, and events; an active store cannot be replaced.
- **T036-AC02** — Before-publication interruption leaves the old usable history/epoch; after publication the restored history always has its new epoch.
- **T036-AC03** — No old sidecar can be applied to the replacement, and a failed validation/replace preserves the current store or documented recoverable safety backup.
- **T036-AC04** — Normal restart does not rotate epoch; official restore closes restored active subscriptions.
- **T036-AC05** — Both frontends and native client detect the new epoch, revalidate profile IDs, and reload instead of silently waiting on stale cursors.
- **T036-AC06** — The maintenance command clearly reports manual-file-replacement limits and never silently discards the current store.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T036 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T036 -Configuration Release
```

**Observable demonstration:** Create data/images, stop and back up, add later history, restore under controlled interruption points, and reopen both clients to verify correct history/epoch behavior.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No backup scheduling service, cloud backup, retention management, or restore while the live store is in use.

### T037 — Expose bounded configuration and operator diagnostics

**Outcome:** A usable status/diagnostics/configuration surface with effective values, explicit startup errors, and bounded non-content logs.

**Owner role:** operations implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T035. **Previous milestone gate:** M05. **Checkpoint-entry gates:** None. **Contributes to:** CP13.

**Source:** sections 5, 17.2, 18.2, 18.4, 18.7, 20.1; requirements R01, R13, R19; supporting/related scenarios A05, A34, A36, A48, A50. **Primary closure ownership:** A05.

**Subtasks**

- **T037-ST01** — Finalize effective port/data/certificate/resource configuration and validation.
- **T037-ST02** — Expose version, readiness, actual H3 probe result, epoch/head, limits, and live stream count.
- **T037-ST03** — Add structured bounded log rotation and diagnostic IDs without content or secret payloads.
- **T037-ST04** — Exercise alternate configuration and certificate/port/lock/migration/storage failures.

**Acceptance criteria — all required**

- **T037-AC01** — Status reflects the actual effective configuration and observed probe state rather than configured intent.
- **T037-AC02** — Invalid values and port/certificate/lock/migration/storage failures prevent false readiness with actionable diagnostics.
- **T037-AC03** — Logs omit message text, image bytes, complete request bodies, file-picker paths, and private-key material.
- **T037-AC04** — Log growth is bounded, stream counts return to baseline, and normal operation emits no unexpected warnings.
- **T037-AC05** — Timeout handling remains finite and uses provider behavior without unlimited retry wrappers.
- **T037-AC06** — There is no silent protocol fallback, new-store reset, or warning suppression to conceal a problem.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T037 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T037 -Configuration Release
```

**Observable demonstration:** Run with alternate ports/data location and small resource bounds, inspect status/log rotation, then cause startup/storage failures and review their diagnostics.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No remote telemetry service, dashboards, authentication additions, or unbounded support dumps.

### T038 — Publish the self-contained Windows folder package

**Outcome:** A repeatable Release publication producing the host, WinUI, static assets, and their required runtime/native dependencies.

**Owner role:** release implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T035, T036, T037. **Previous milestone gate:** M05. **Checkpoint-entry gates:** CP13. **Contributes to:** CP14.

**Source:** sections 18.3, 21; requirements R01, R14, R15, R19; supporting/related scenarios A42. **Primary closure ownership:** No exclusive source-scenario closure; task criteria still mandatory.

**Subtasks**

- **T038-ST01** — Publish pinned win-x64 host and WinUI outputs with both .NET and Windows App SDK self-contained settings.
- **T038-ST02** — Assemble a clean product folder and dependency/version/hash manifest with run/stop/maintenance instructions.
- **T038-ST03** — Keep user data/config/certificates outside the binary distribution and verify binary replacement preserves data.
- **T038-ST04** — Separate any qualification runner from the product payload and prove the published executables start.

**Acceptance criteria — all required**

- **T038-AC01** — Both runtime families and required native/static files are present; product execution does not require a development SDK or Node/CDN.
- **T038-AC02** — The package records exact source commit, dependency versions, target platform, configuration, and artifact hashes.
- **T038-AC03** — Publication succeeds with zero build/publish/packaging warnings and excludes test hooks/private keys/user data.
- **T038-AC04** — Replacing application binaries does not overwrite the existing database or selected data directory.
- **T038-AC05** — The actual published host/WinUI launch and expose the same contracts, limits, and version described by the manifest.
- **T038-AC06** — Run/stop/backup/restore instructions match the packaged command interfaces.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T038 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T038 -Configuration Release
```

**Observable demonstration:** Publish into an empty folder, inspect the manifest, run the produced executables, replace binaries over a preserved test installation, and requery its data.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No MSIX/Store publication, single-file promise, updater, ARM64/Linux release, or bundled user certificate/private key.

### T039 — Run repeatable workload and fault qualification

**Outcome:** A reusable runnable qualification harness that seeds realistic data and reconciles persisted, delivered, and recovered results under load/faults.

**Owner role:** qualification implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T036, T037. **Previous milestone gate:** M05. **Checkpoint-entry gates:** CP13. **Contributes to:** CP14.

**Source:** sections 12, 17.2, 20.2, 20.3; requirements R02, R06, R13, R19; supporting/related scenarios A20, A21, A22, A34, A35, A38, A45, A46, A49. **Primary closure ownership:** A22, A34, A35, A38.

**Subtasks**

- **T039-ST01** — Add deterministic seeding for 100 channels, 1,000 sessions, 20,000 text messages, 100 representative images, and up to 20 consumers.
- **T039-ST02** — Exercise concurrent writes, sparse filters, slow readers, close/attach, empty-read/wait, and process restart boundaries.
- **T039-ST03** — Report actual hardware/configuration, resource bounds, latency observations, and event/receipt reconciliation.
- **T039-ST04** — Execute against the release candidate or repeat against final packaged binaries before release closure.

**Acceptance criteria — all required**

- **T039-AC01** — The documented dataset/scenarios are reproducible and bounded without modifying production semantics or public fault routes.
- **T039-AC02** — Every retained committed matching event is recoverable, no uncommitted data appears, and duplicate client application is prevented.
- **T039-AC03** — Slow streams do not retain database readers, indefinitely block writers, or exceed configured stream/write/payload limits.
- **T039-AC04** — Finite contention and close/attach behavior remain correct; stream/connection/native-image resources return to their expected baseline.
- **T039-AC05** — Results report measured values and the exact environment; no unmeasured performance promise is asserted.
- **T039-AC06** — Expected injected failure diagnostics are asserted separately; all unexpected warnings/findings are resolved before the task passes.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T039 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T039 -Configuration Release
```

**Observable demonstration:** Seed the representative store, run concurrent consumers/publishers with controlled faults, and inspect the executable reconciliation/resource report.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No benchmark marketing claims, invented latency target, distributed load platform, or masking failures by relaxing limits.

### T040 — Qualify the published MVP on a clean Windows machine

**Outcome:** A runnable release-smoke/handoff procedure with evidence from the actual packaged binaries and both real frontends on a clean supported Windows target.

**Owner role:** qualification implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T038, T039. **Previous milestone gate:** M05. **Checkpoint-entry gates:** CP13. **Contributes to:** CP14.

**Source:** sections 16, 18, 19, 20; requirements R01, R02, R03, R04, R05, R06, R07, R08, R09, R10, R11, R12, R13, R14, R15, R16, R17, R18, R19; supporting/related scenarios A01, A02, A03, A04, A05, A06, A07, A08, A09, A10, A11, A12, A13, A14, A15, A16, A17, A18, A19, A20, A21, A22, A23, A24, A25, A26, A27, A28, A29, A30, A31, A32, A33, A34, A35, A36, A37, A38, A39, A40, A41, A42, A43, A44, A45, A46, A47, A48, A49, A50. **Primary closure ownership:** A33, A39, A40, A41, A42, A43, A47.

**Subtasks**

- **T040-ST01** — Publish a separate self-contained verification runner where needed so the clean machine needs no development SDK/Node to exercise the product.
- **T040-ST02** — Run the shared A/B container, targeted text/image, three-level thread, invalid-level, retry/reconnect, and profile-switch scenario in both UIs.
- **T040-ST03** — Exercise genuine native H3, local boundary failures, independent lifetime, and maintenance against the exact package.
- **T040-ST04** — Reconcile A01-A50 coverage, package hashes, operator instructions, and remaining findings for the milestone review.

**Acceptance criteria — all required**

- **T040-AC01** — The actual packaged product runs on a clean Windows 11 x64 machine without a development SDK, Node, or external script/CDN dependency.
- **T040-AC02** — WinUI and browser complete the same full scenario and produce equivalent persisted objects, owner fields, target/thread semantics, and errors.
- **T040-AC03** — Both UI processes demonstrate safe rendering, fragmented-stream recovery, explicit caller switching, and independent consumption.
- **T040-AC04** — Actual persisted H3 publication/subscription, UI-close host lifetime, stop/restart, and backup/restore pass against the candidate package.
- **T040-AC05** — All A01-A50 have current passing evidence with the relevant platform/method; missing/skipped/unexecuted required checks cannot count as pass.
- **T040-AC06** — Normal-operation compiler/analyzer/package/runtime/browser warnings and review findings are zero; injected negative outcomes are explicitly asserted.
- **T040-AC07** — Candidate commit, effective configuration, dependency versions, product hashes, and evidence are recorded; the separate M06 full review is still required.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T040 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T040 -Configuration Release
```

**Observable demonstration:** Install/copy the published folder onto a clean Windows target, run the verification executable and real UI scenarios, and produce the evidence index for the final full review.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No approval-by-checklist shortcut, skipped platform gate, app deployment to another service, or claim that running tests alone closes the milestone.

## M06 mandatory full review and closure

After CP13, CP14 pass, review all implemented functionality from M01 through M06, including inherited code and behavior. Apply all ten review dimensions and MG01–MG06 in [the gate policy](../plan/02-gates-and-reviews.md).

Use [the milestone template](../templates/milestone-review.md). Record every finding/warning, implement and independently verify fixes, then run the cumulative suite and checkpoint scenarios on the final corrected candidate. No unresolved issue/warning of any severity can remain. A test-only pass does not replace full review.

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope milestone -Id M06 -Configuration Release
```

**Acceptance record must identify:** final commit, effective configuration/dependency versions, required platform observations, task/checkpoint evidence, zero-open-findings result, reviewer, and relevant artifact hashes. This milestone is currently **not run**.
