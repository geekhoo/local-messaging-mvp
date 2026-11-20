# Implementation validation report

## What is verified

The Windows x64 Release build passed on November 20, 2026 with zero warnings. The local build report is `artifacts/build/windows-x64-build.json` (not tracked in Git). Its report records runtime qualification as `not_run`. The `artifacts\product` folder in this workspace is not a qualified release.

The earlier delivery passed portable checks on Linux. The [original delivery manifest](../DELIVERY-MANIFEST.json) identifies that snapshot, not the current workspace. The results below belong to that delivery; they do not establish coverage of later changes such as public acknowledgements, rosters, or the coding-agent plugin.

## Portable results from the original delivery

| Check | Observed result | Scope and evidence |
| --- | --- | --- |
| Locked restore / portable Release build | Passed, zero warnings/errors | 13 .NET projects in `LocalMessaging.Core.slnx`; `evidence/runtime/final` and `final-build` |
| Public contract fixtures | 6 passed | Typed discriminators, round-trip identity, required fields, unknown fields/types; final contracts JSON |
| Real-file SQLite/application scenarios | 33 passed | Transactions, receipts, owner invariants, compound rollback, targets, threads, images, subscriptions, snapshots/replay, contention, migration/corruption preservation, backup/restore fault boundaries |
| Integrated actual HTTP/F scenarios | 74 passed | Real Kestrel plus real SQLite; finite/live subscriptions, multipart, boundaries, lost wake, stalled socket, checkpointing, logging failure and shutdown |
| Reusable .NET client | 18 passed | Actual HTTP fixture and SSE fragmentation/recovery/identity/cursor/bounds tests |
| Actual native model/projection source | 8 passed | Source-linked models, delayed navigation/profile/reload and thread-event races; no WinUI controls instantiated |
| Framework-free web components | 24 passed | API, frozen retry, stream parser/control handling, independent cursors, bounded state/paging; zero failed/skipped/TODO |
| Console sample on explicit F | 9 passed | Actual Kestrel/EF/SQLite; ownership, retry, multipart persistence, snapshot/live and independent closure; native-components report |
| Representative store workload | Passed | 100 channels, 1,000 sessions, 20,000 text and 100 image messages, 20 independent finite consumers; each reconciles 20,100 messages and the final head |
| WinUI managed source | Passed, zero warnings/errors | `ResolveReferences,Compile` diagnostic target against actual Windows references; native-components report |
| WIC component/corpus compilation | Passed, zero warnings/errors | Compiler evidence only; runtime explicitly blocked on Linux |
| Build/operator scripts | Parser and focused helper checks passed | PowerShell 7.6.10 on Linux; quoting, pinned/locked arguments, hashes/inventory and platform rejection; Windows PowerShell/registry/runtime remains pending |
| Qualification CLI guards | Executed with explicit outcomes | Unknown IDs and prohibited evidence destinations reject; supported Windows scopes return blocked here; final CLI-guard report |

These counts are not additive release acceptance totals. Some suites overlap, and a source acceptance scenario can require actual UI/platform evidence even when its underlying model/API checks pass. All A01–A50 formal statuses and all 235 task criteria remain explicit in the CSV matrices; portable checks do not silently close them.

## Reliability evidence

The integrated transport suite deliberately leaves a real socket response unread with a small receive window. While it is stalled, 500 text publications commit, a WAL checkpoint succeeds, the stream reaches its configured write deadline, and finite replay recovers the complete set. A deterministic hook places a commit between an empty initial stream read and the next wait; polling recovers the event. The close/attach registry test controls the race and verifies no indefinitely active post-close registration.

The same live HTTP suite replaces the isolated diagnostic directory with a file to cause a real log I/O failure. The command still returns 201, its receipt is queryable, retry remains idempotent, and exactly one fallback diagnostic is emitted per failure episode. Restoring the path resumes logging. Conflict outcomes are logged as 409 with their request ID, and message payloads do not appear in normal host logs.

Store tests use actual Microsoft.Data.Sqlite/EF files rather than EF InMemory or mocked repositories. They assert real relational constraints and a genuinely held writer timeout. Unsupported migration history and corrupt bytes are preserved on failure. Deterministic transaction/restore fault points exercise both sides of durable publication. The separately implemented Windows package runner additionally terminates its own actual host process after observing a committed unread response, restarts, retries the frozen command, and checks official maintenance; that Windows path is compiled but not executed here.

The representative workload's earlier recorded run completed in 36.08 seconds with approximately 274 MB peak process working set and a 108.8 MB database after a successful truncating checkpoint. These are observations from this container and direct finite store consumers, not a Windows HTTP/3 throughput promise. The final rerun's actual measurements are preserved separately in the final workload report. WIC is not inferred from the known-valid image inputs used by the store workload.

## Independent review and remediation

Work was reviewed across separate implementers: the web implementer reviewed native/client/WIC source; the native implementer reviewed persistence and cursor/restore invariants; the store implementer reviewed Host error/logging behavior and the qualification tools; the web implementer also reviewed operation scripts and the published-host runner.

Recorded fixes include:

- Restricting static middleware to F, logging translated status codes, and containing diagnostic-volume failure without altering committed outcomes.
- Replacing the incorrect handwritten EF snapshot with the actual generated migration/model snapshot, without suppressing the pending-model warning.
- Correcting runtime-pack pinning so it does not overwrite the Windows SDK reference version; adding a valid x64 solution configuration; removing an unsupported solution-wide runtime identifier.
- Closing native transition/subscription races, preserving creation retry identity, validating committed receipts and cursors, protecting drafts during profile/navigation changes, buffering thread events during queries, and bounding image/draft resources.
- Keeping web paging reachable after cache limits and reconnecting correctly on ID-less temporary storage controls.
- Binding verifier results to the actual package, validating both inventories before executing, requiring all selected task criteria, rejecting unexpected diagnostics, and preventing evidence writes into immutable product/verifier inventories.

Detailed source-review records are `evidence/native-review.md`, `evidence/store-review.md`, `evidence/verifier-review.md`, and the cumulative records under `evidence/reviews`. Source-review closure is not an assertion that the pending actual Windows/UI checks passed.

## Still needed for release

- Publish a clean, committed Windows candidate with matching product and verifier manifests.
- Run the packaged HTTP/3, WIC, WinUI and browser checks against that candidate.
- On a clean Windows machine without the SDK or Node, verify startup, sign-in registration, shutdown and proxy/PAC behavior.
- Record the observations and complete the independent milestone reviews. The status matrices track the original plan, not later feature coverage.

## Reproduction

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope portable -IncludeWorkload
```

For the full Windows build/publication and exact packaged qualification, follow [windows-qualification.md](windows-qualification.md). Build/runtime evidence and the independent milestone acceptance decision remain separate. The complete deliverable is source and verification material; it is **not** represented as a qualified prebuilt Windows release.
