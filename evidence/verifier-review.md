# Independent final verifier review

**Reviewer:** store implementation agent, reviewing the root-owned qualification runner and wrapper plus the published-host runner.

**Method:** source/control-flow review against the supplied task criteria and acceptance catalogue. The review did not execute Windows binaries, registry changes, WIC, HTTP/3, or WinUI. Root-owned build and runtime evidence remain separate from this review.

## Result

The concrete findings below have been corrected in the reviewed source. No additional blocking source defect was identified in this review scope. This result does not accept a task, checkpoint, milestone, or release gate; actual required Windows execution and independent gate decisions remain necessary.

| ID | Finding | Verified correction |
|---|---|---|
| VR01 | Package verification could report the invoking checkout's commit as the tested package identity. | Package candidate commit and SHA-256 are read from the product manifest; invoker source commit is recorded separately. Default evidence paths use the package identity and a unique run timestamp. |
| VR02 | Verification-tool inventory did not require Release, checked only final files for reparse points, and ran after the published host had already executed. | The verifier requires Release/win-x64/self-contained identity matching the product, rejects reparse points in the root and all directory/file entries, verifies complete size/hash inventory, and runs those checks before published-host qualification. |
| VR03 | PowerShell accepted case-insensitive scope/ID spelling that the C# runner rejected. | The wrapper canonicalizes scope and IDs before validation and forwarding. |
| VR04 | Task-oriented scopes discarded mandatory Txxx-ACxx criteria; T002 could have an empty source-scenario selection. | The runner separately selects every required task criterion, reports each one, and requires exact-candidate criterion evidence. Missing criteria block execution evidence completion; an empty task-criterion selection cannot pass. |
| VR05 | A passing host suite could retain unexpected Warning/Error/Critical logs, including fallback diagnostic failures written only to stderr. | Published-host phases parse bounded JSONL records and reject those severities without category-wide exclusions. Every child validates fully drained stderr; normal children require empty stderr. Only the two asserted negative child labels permit their exact expected error line, and truncated/oversized stderr cannot satisfy that check. Diagnostic logs are saved even when cleanup/diagnostic checks fail. |
| VR06 | Package-mode invocation depended unconditionally on git and could fail before reaching the self-contained verifier on a machine without git. | Source revision discovery now checks tool availability; package identity remains authoritative without git. |
| VR07 | Solution-wide `-r win-x64` causes NETSDK1134 and source helper lookup used an x64 directory that common projects do not use. | Full solution restore/build use the x64 solution platform without a solution-wide RID; WinUI retains its project RID. The wrapper uses common projects' actual `bin/Release/net10.0/` path. Per-project publication still selects win-x64. |
| VR08 | Observation timestamps could accept ambiguous or incomplete forms, and warning closure was not explicit per required observation. | The runner accepts exactly UTC timestamps ending in Z, either without a fraction or with one through seven required fraction digits. Every required case and task-criterion record must state `unexpectedWarnings: 0`. Actual .NET parsing rejects `.Z`, overprecision, invalid dates, date/time-only strings and offset-only timestamps. |
| VR09 | Evidence paths within a product or verifier inventory could be created before later rejection, including when the product manifest is invalid. | Both entry points reject equal/descendant inventory paths before creating directories or reports. The PowerShell guard runs whenever the product absolute path has been resolved, independently of manifest validity. Actual negative wrapper fixtures confirm no report or new child directory appears inside either inventory. |
| VR10 | Application lookup can return multiple matching paths; invoking the Source array forms one invalid executable name. | Build, publish and verify select the first matching application. The actual PATH with four git matches now invokes the selected executable successfully and reaches the expected validation result. |

## Critical package-runner assumptions checked

- Product manifest validation checks safe relative paths, duplicate entries, reparse points, complete inventory, byte lengths and SHA-256 values. The package contains the expected host/WinUI executables and .NET runtime files, and the product is rechecked after the host scenario.
- The tested host is the fixed packaged `host/LocalMessaging.Host.exe`. Endpoint metadata does not select an executable. Startup probes tie the descriptor to the child process, endpoints, instance and epoch, and verify effective configured limits.
- Publication/subscription checks include actual exact HTTP/3 requests on the Windows path. Separate portable/source fixtures cannot satisfy this path.
- Process crash recovery, coherent offline backup/restore, epoch rotation, persisted images/receipts and subscription closure are exercised through the actual packaged host and maintenance CLI.
- Missing UI, proxy/PAC and clean-machine observations remain blocking. Required observation records identify the exact candidate/package and a recorded reviewer identity, with a strict UTC observation timestamp, zero unexpected warnings and nonempty linked evidence. Task criteria are additional to the source acceptance scenarios.
- Nonzero helper results fail or block qualification; missing helper binaries block it. Source/portable verification explicitly retains unaccepted gate status.

## Final narrow execution checks

`verifier-helper-validation.json` and its log record actual .NET timestamp parsing using the eight formats extracted from the reviewed C# source, source-extracted path helper checks, parsing of the wrapper, and actual wrapper invocations using isolated synthetic manifests. Equal/descendant product and verifier paths are rejected before writes; a malformed manifest does not bypass the guard; a similarly prefixed sibling path reaches the expected Linux platform block. `script-validation.json` and its log also verify that the selected git application is executable in this environment with multiple git matches.

These are focused Linux checks of the final validation corrections. They do not execute a Windows package or replace product, UI, registry, WIC or HTTP/3 qualification.

## Reproduce the focused checks

The reusable source is retained at `tests/validate-verifier-guards.ps1`. Run from the checkout with PowerShell 7.2 or later:

```powershell
pwsh -NoProfile -File tests/validate-verifier-guards.ps1 -OutputPath evidence/verifier-helper-validation.json
```

The repository helper completed 38 checks in this Linux execution and records the reviewed file hashes itself. Its paths derive from the checkout and current PowerShell installation. Reports include explicit failures and skipped checks; no platform qualification is inferred from a helper result.

Actual wrapper fixtures use a unique temporary directory containing synthetic manifests and no executable host or verifier. The helper cannot qualify or launch a real product through those fixtures.

## Reviewed source snapshot

The hashes below identify the source inspected when this review record was written. Later changes to these files require affected review/verification.

| File | SHA-256 |
|---|---|
| `tests/LocalMessaging.Verify/Program.cs` | `2167c674d7be7ca902367dde8907d8ad19464795bffe41386095221d1280dc12` |
| `eng/verify.ps1` | `167b7db05281e1886d08b6f255ff22e467e249a07521f6bd6a8e8ea1fa4b6b41` |
| `tests/LocalMessaging.Scenarios/PublishedQualification.cs` | `ed596ea30ae04790c5f2dcbb350aaeda47569052e4cb6ec7779c8d90d49ff1d2` |
| `tests/validate-verifier-guards.ps1` | `0ece54a22512d07a92f748ed413c2abba3d46b7c658dc121059f3e4d95484d56` |

Recorded at 2026-11-20T10:45:49.923969Z.
