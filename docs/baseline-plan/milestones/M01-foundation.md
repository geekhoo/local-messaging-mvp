# M01 — Foundation and transport qualification

**Status:** planned · **Gate:** not_run · **Full cumulative review:** mandatory

## Objective

Run the pinned Windows host and WinUI baseline, prove real HTTP/3 readiness, enforce local boundaries, initialize SQLite, and qualify bounded image decoding.

## Entry

Begin with the supplied specification and access to a Windows 11 x64 development/verification environment. Every task also requires its explicit dependency and checkpoint-entry gates.

## Exit

A real Windows baseline can be built and operated; transport/platform risks are exercised before domain features. All checkpoints below and MG01–MG06 must pass. Resolve every issue/warning and rerun cumulative verification on the final corrected candidate.

**Scope note:** No full domain publish/subscription claim; synthetic probes are supporting evidence only.

## Task map

| Task | Outcome | Task dependencies | Checkpoint-entry gate | Checkpoint |
| --- | --- | --- | --- | --- |
| T001 | Build and run the pinned Windows solution | None | None | CP01 |
| T002 | Execute the versioned contract and validation rules | T001 | None | CP01 |
| T003 | Run a genuine local HTTP/3 host and readiness probe | T001 | None | CP01 |
| T004 | Enforce listener and browser request boundaries | T002, T003 | CP01 | CP02 |
| T005 | Initialize and reopen the real SQLite store | T002, T003 | CP01 | CP02 |
| T006 | Validate image decoding in the unpackaged host environment | T001 | CP01 | CP02 |

## Integrated checkpoints

### CP01 — Executable baseline and real transport

**Required task gates:** T001, T002, T003. **Prior checkpoint gates:** None within this milestone. **Status:** not_run.

**Preconditions**

- Pinned Windows development environment and clean isolated work directory; no pre-existing host on test ports.

**Integrated steps**

1. Restore/build the integrated solution and run the contract-fixture validation executable.
2. Start the actual host, let its post-start exact-H3 probe complete, and inspect observed protocol and certificate evidence.
3. Run harness-only streaming/protocol fixtures and open/close the actual WinUI baseline.

**Expected results**

- Every introduced project builds without warnings; contract acceptance/rejection fixtures match the specification.
- Observed response version is 3.0 on the native probe; invalid certificate/disabled QUIC is an asserted negative result.
- Host and window start/stop coherently; no synthetic probe is labeled a committed publish or durable subscription.

**Platform:** Windows 11 x64; real Kestrel and SQLite where implemented; actual browser/WinUI for UI assertions

**Planned verification command**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope checkpoint -Id CP01 -Configuration Release
```

**Evidence:** one integrated-candidate [checkpoint record](../templates/checkpoint-result.md), actual commands/results, fixture identity, applicable diagnostics, and closure links. Apply CG01–CG05; constituent task passes alone do not suffice.

**Unlocks:** T004, T005, T006 integration and CP02.

### CP02 — Local boundary and durable platform

**Required task gates:** T004, T005, T006. **Prior checkpoint gates:** CP01. **Status:** not_run.

**Preconditions**

- Integrated baseline; temporary local data directory; real browser and WIC corpus.

**Integrated steps**

1. Initialize/migrate the store, stop, reopen, and inspect epoch/schema/SQLite configuration.
2. Use actual requests to exercise wrong-port, authority, Origin, required-header, and static-file checks.
3. Run valid/invalid image corpus in the unpackaged headless host environment; verify bounded decode cleanup.

**Expected results**

- One persistent epoch and valid schema survive reopen; a second host cannot take the same store.
- Valid-authority/wrong-surface is 404; invalid authority is 400; static assets stay on F; rejected requests have no side effects.
- WIC validates only the permitted formats within bounds without requiring a WinUI window or package identity.

**Platform:** Windows 11 x64; real Kestrel and SQLite where implemented; actual browser/WinUI for UI assertions

**Planned verification command**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope checkpoint -Id CP02 -Configuration Release
```

**Evidence:** one integrated-candidate [checkpoint record](../templates/checkpoint-result.md), actual commands/results, fixture identity, applicable diagnostics, and closure links. Apply CG01–CG05; constituent task passes alone do not suffice.

**Unlocks:** M01 full cumulative review; after M01 passes, T007 and M02.

## Atomic task cards

Every card inherits TG01–TG08 from [the gate policy](../plan/02-gates-and-reviews.md). Commands are planned interfaces implemented by T001, not executed evidence in this package. The [source specification](../specs/local-messaging-design-specification-v1.md) is authoritative; section/requirement/acceptance references below are local traceability.

### T001 — Build and run the pinned Windows solution

**Outcome:** A reproducible Windows solution that starts a minimal host and native window, plus a real verification entry point used by later tasks.

**Owner role:** foundation implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** None. **Previous milestone gate:** None. **Checkpoint-entry gates:** None. **Contributes to:** CP01.

**Source:** sections 3.1, 4.3, 18.3; requirements R01, R14, R15; supporting/related scenarios A42. **Primary closure ownership:** No exclusive source-scenario closure; task criteria still mandatory.

**Subtasks**

- **T001-ST01** — Pin qualified .NET 10, EF, Windows target, and stable Windows App SDK inputs; record the Windows build and dependency inventory.
- **T001-ST02** — Create the named product projects, test projects, and a small LocalMessaging.Scenarios console harness.
- **T001-ST03** — Add eng/verify.ps1 with task/demo/checkpoint/milestone/acceptance scopes, nonzero failure/unknown-ID exits, and evidence output.
- **T001-ST04** — Build Release, start/stop the minimal host, and open/close the native shell on Windows 11 x64.

**Acceptance criteria — all required**

- **T001-AC01** — A fresh checkout restores pinned dependencies and builds every introduced project in Release with zero build/restore/analyzer warnings.
- **T001-AC02** — The actual host process starts and exits cleanly; an actual WinUI window opens and closes on the declared Windows target.
- **T001-AC03** — The scenario harness is runnable and reports environment, candidate commit, command, exit status, and unimplemented capability state.
- **T001-AC04** — The verifier rejects unknown IDs and unimplemented checks; it cannot report a missing runner or skipped execution as PASS.
- **T001-AC05** — Product web assets need no frontend framework or Node dependency; no domain feature is falsely declared complete.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T001 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T001 -Configuration Release
```

**Observable demonstration:** From a clean checkout, run the verifier, observe a real host process and WinUI window, close them, and inspect the captured build/environment results.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No messaging-domain behavior, user authentication, deployment installer, or milestone review automation platform.

### T002 — Execute the versioned contract and validation rules

**Outcome:** A compiled contract library with executable valid/invalid fixtures for the complete v1 request, result, subscription, and error shapes.

**Owner role:** contracts implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T001. **Previous milestone gate:** None. **Checkpoint-entry gates:** None. **Contributes to:** CP01.

**Source:** sections 6.2, 7.4, 8, 9, 13.5, 20.1; requirements R04, R05, R06; supporting/related scenarios Task-specific criteria. **Primary closure ownership:** No exclusive source-scenario closure; task criteria still mandatory.

**Subtasks**

- **T002-ST01** — Implement discriminated DTOs and strict request serialization/deserialization; distinguish full DTO fields from shortened documentation examples.
- **T002-ST02** — Freeze selector mode=new, receipt operation/state fields, string sequences, UUID handling, and normalization rules.
- **T002-ST03** — Create valid and invalid fixtures for all create variants, subscription operations, optional fields, and Problem Details.
- **T002-ST04** — Run the compiled contract harness against fixtures; future unsupported host operations stay explicitly unavailable.

**Acceptance criteria — all required**

- **T002-AC01** — Every valid fixture round-trips without loss and every invalid discriminator, unknown request property, or contradictory selector fails predictably.
- **T002-AC02** — UUIDs, string-encoded sequences, UTC timestamps, caller bootstrap rules, and receipt versus subscription state match the specification.
- **T002-AC03** — Canonical image request hashing inputs are documented as normalized request fields plus verified image-byte digest; boundary and filename do not change identity.
- **T002-AC04** — Omitted/default/empty option normalization is deterministic; target-ID ordering does not produce a different canonical request hash.
- **T002-AC05** — Fixture execution calls the actual contract code and exits nonzero on a mismatch; no required contract field is inferred from a shortened example.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T002 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T002 -Configuration Release
```

**Observable demonstration:** Run a table of valid new/existing requests and malformed envelopes through the compiled validator and inspect the normalized JSON/error outputs.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No database writes, speculative version 2, or alternate payload format.

### T003 — Run a genuine local HTTP/3 host and readiness probe

**Outcome:** A real Kestrel HTTP/3-only listener and separate frontend listener, with a persisted localhost certificate and exact-version startup proof.

**Owner role:** transport implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T001. **Previous milestone gate:** None. **Checkpoint-entry gates:** None. **Contributes to:** CP01.

**Source:** sections 3.2, 5.1, 5.3, 5.4, 18.2; requirements R01, R02, R03; supporting/related scenarios A03, A04, A05, A50. **Primary closure ownership:** No exclusive source-scenario closure; task criteria still mandatory.

**Subtasks**

- **T003-ST01** — Provision/load the per-user certificate with SAN, EKU, persisted private key, and constrained native pin validation.
- **T003-ST02** — Bind the configured loopback messaging and frontend addresses with explicit protocols.
- **T003-ST03** — Run the native version-3 exact self-probe after Kestrel starts, directly without proxy use.
- **T003-ST04** — Exercise finite JSON and synthetic SSE transport probes in the verification harness, not permanent public production endpoints.

**Acceptance criteria — all required**

- **T003-AC01** — The messaging probe uses RequestVersionExact=3.0 and verifies response.Version=3.0; a TCP-only port check is never accepted as QUIC proof.
- **T003-AC02** — Frontend HTTP/1.1 responds independently; the browser is not required to trust a self-signed HTTP/3 certificate.
- **T003-AC03** — Wrong pin, hostname mismatch, expiry, unavailable QUIC, and occupied configured ports fail clearly with no false-ready descriptor.
- **T003-AC04** — A restart loads a persisted private key and native clients reject arbitrary certificates; no accept-any validation is present.
- **T003-AC05** — The self-probe runs after server binding and bypasses configured Windows proxy/PAC settings.
- **T003-AC06** — Probe results are marked supporting transport evidence, not final completion of persisted-publication A03 or durable-subscription A04.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T003 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T003 -Configuration Release
```

**Observable demonstration:** Start real Kestrel, run exact-H3 JSON/SSE probes, repeat with an invalid certificate and proxy settings, and inspect actual negotiated versions and startup outcomes.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No raw QUIC protocol, SignalR, external broker, browser H3 workaround, or unsupported public probe route.

### T004 — Enforce listener and browser request boundaries

**Outcome:** A runnable host that enforces real-port separation, authority/origin/header checks, and local static-content policy before requests reach application handlers.

**Owner role:** transport implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T002, T003. **Previous milestone gate:** None. **Checkpoint-entry gates:** CP01. **Contributes to:** CP02.

**Source:** sections 5.2, 13.1, 19, 20.1; requirements R02, R03, R19; supporting/related scenarios A06, A47. **Primary closure ownership:** No exclusive source-scenario closure; task criteria still mandatory.

**Subtasks**

- **T004-ST01** — Apply LocalPort surface selection before endpoint and static/default-file middleware.
- **T004-ST02** — Implement exact allowed authority/Origin and required client-header handling, including native requests with absent browser metadata.
- **T004-ST03** — Serve a minimal same-origin static page with local-only CSP and test the frontend fetch path.
- **T004-ST04** — Exercise JSON and multipart boundary checks through actual HTTP requests; ensure checks remain shared by later routes.

**Acceptance criteria — all required**

- **T004-AC01** — Valid-authority wrong-surface requests return 404; invalid authority returns 400; Host spoofing cannot switch surfaces.
- **T004-AC02** — Static/default assets are available only on the frontend listener.
- **T004-AC03** — Cross-origin/null-Origin browser mutations and simple form posts without required header are rejected before any handler side effect.
- **T004-AC04** — Same-origin browser fetch and native requests with the required header succeed through permitted routes.
- **T004-AC05** — No wildcard CORS, all-interface binding, unsafe forwarded-host trust, or unbounded request parsing is introduced.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T004 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T004 -Configuration Release
```

**Observable demonstration:** Use a real browser and native client to call the right/wrong listener, spoof authority, and submit same-origin/cross-origin requests against the current host.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No authentication, API keys, remote access, or user-role model.

### T005 — Initialize and reopen the real SQLite store

**Outcome:** A host that creates/migrates a real SQLite file, holds a single-instance store lock, and reports durable store readiness and epoch.

**Owner role:** persistence implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T002, T003. **Previous milestone gate:** None. **Checkpoint-entry gates:** CP01. **Contributes to:** CP02.

**Source:** sections 14, 18.2, 18.5; requirements R04, R13, R19; supporting/related scenarios A34, A36. **Primary closure ownership:** A36.

**Subtasks**

- **T005-ST01** — Implement explicit EF mappings, constraints, indices, AppMetadata, and initial migrations for the specified tables.
- **T005-ST02** — Apply absolute data paths, FK/WAL/FULL policy, finite timeout, and short context factory lifetimes.
- **T005-ST03** — Acquire the exclusive data-directory lock before initialization; integrate database and transport readiness.
- **T005-ST04** — Add real-file startup/reopen/constraint fixtures and migration-failure recovery checks.

**Acceptance criteria — all required**

- **T005-AC01** — Starting with an empty directory applies migrations; reopening preserves the same store epoch, schema, and data.
- **T005-AC02** — A competing host cannot independently use the same store; unrelated occupied ports/data are not overwritten.
- **T005-AC03** — Effective SQLite FK, WAL, synchronous, and timeout settings match the design and are verified using the real provider.
- **T005-AC04** — Required FK/composite-parent/root/unique/check constraints exist and are exercised with real SQLite fixtures.
- **T005-AC05** — Migration or initialization failure prevents application readiness and never silently recreates the store; EnsureCreated is not mixed with migrations.
- **T005-AC06** — Contexts and connections are not shared across concurrent units of work; readiness descriptor contains the actual persistent epoch.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T005 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T005 -Configuration Release
```

**Observable demonstration:** Start the host on a temporary data directory, inspect status/pragmas/schema, stop/reopen, and attempt competing startup and invalid relational inserts through the test fixture.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No domain creation API yet, background migration repair, or alternative database.

### T006 — Validate image decoding in the unpackaged host environment

**Outcome:** A runnable WIC validation component that accepts bounded PNG/JPEG inputs without WinUI, package identity, or a window in the host.

**Owner role:** platform implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T001. **Previous milestone gate:** None. **Checkpoint-entry gates:** CP01. **Contributes to:** CP02.

**Source:** sections 3.1, 17.2, 17.3; requirements R12, R19; supporting/related scenarios A31. **Primary closure ownership:** No exclusive source-scenario closure; task criteria still mandatory.

**Subtasks**

- **T006-ST01** — Add a narrow Win32 WIC wrapper using explicit built-in PNG/JPEG codecs.
- **T006-ST02** — Check container, dimensions, pixel/stride arithmetic, animation chunks, and decoded data within configured bounds.
- **T006-ST03** — Bound concurrent validation and release COM/stream/pixel resources deterministically.
- **T006-ST04** — Create a corpus/scenario runner for valid, corrupt, spoofed, oversized, animated, and extreme-dimension images.

**Acceptance criteria — all required**

- **T006-AC01** — Valid PNG/JPEG files decode completely and produce verified format, dimensions, byte length, and SHA-256 metadata.
- **T006-AC02** — The runner works in the unpackaged headless host context without HWND/MSIX/WinUI dependence.
- **T006-AC03** — Disallowed type, malformed data, animation, excessive bytes/dimensions/pixels, and arithmetic overflow are rejected.
- **T006-AC04** — Concurrency is bounded at two validations and buffers/native resources are released after success or failure.
- **T006-AC05** — Component evidence is labeled supporting A31 evidence; the full persisted image API is not claimed complete until T016.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T006 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T006 -Configuration Release
```

**Observable demonstration:** Run the image corpus through the actual native wrapper in a console/host environment and inspect acceptance, rejection codes, and bounded allocations.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No upload endpoint, thumbnails, transformations, additional formats, or third-party codec discovery.

## M01 mandatory full review and closure

After CP01, CP02 pass, review all implemented functionality from M01 through M01, including inherited code and behavior. Apply all ten review dimensions and MG01–MG06 in [the gate policy](../plan/02-gates-and-reviews.md).

Use [the milestone template](../templates/milestone-review.md). Record every finding/warning, implement and independently verify fixes, then run the cumulative suite and checkpoint scenarios on the final corrected candidate. No unresolved issue/warning of any severity can remain. A test-only pass does not replace full review.

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope milestone -Id M01 -Configuration Release
```

**Acceptance record must identify:** final commit, effective configuration/dependency versions, required platform observations, task/checkpoint evidence, zero-open-findings result, reviewer, and relevant artifact hashes. This milestone is currently **not run**.
