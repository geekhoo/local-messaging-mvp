# Windows qualification and gate completion

A [Windows build](validation.md) has passed, but the workspace does not contain a complete Windows release qualification record. Qualify the exact package you intend to distribute. A build, a portable run, or a successful manual launch cannot replace the runtime and review steps below.

The [original plan](baseline-plan/README.md) defines milestone prerequisites and independent reviews. The earlier source delivery did not close those gates.

## Build machine

Use a supported Windows 11 x64 machine with .NET SDK 10.0.404, PowerShell 7.2+ and the WinUI/Windows SDK build prerequisites. Build from the clean, reviewed Git commit you intend to publish. The bundle from the original source delivery does not contain later workspace changes.

```powershell
pwsh -NoProfile -File eng/build.ps1
pwsh -NoProfile -File eng/verify.ps1 -Scope portable -IncludeWorkload
pwsh -NoProfile -File eng/publish.ps1 -OutputPath C:\builds\LocalMessaging-reviewed
```

The first command must build the complete solution with zero warnings. Portable source checks remain supporting evidence even when executed on Windows. Publication must preserve both the self-contained .NET runtime and the Windows App SDK runtime; merely copying managed assemblies is insufficient. `publish.ps1` verifies required files, native SQLite, the XAML runtime and product hygiene, then emits matching product/verifier manifests. It refuses a dirty source tree or overwriting an existing candidate.

Run the actual Windows source suite as well:

```powershell
dotnet run --project tests/LocalMessaging.Scenarios -c Release -- --suite windows --evidence C:\evidence\source-windows.json
dotnet run --project tests/LocalMessaging.Platform.WindowsTests -c Release
```

These exercise genuine Windows WIC/HTTP/3 and deterministic internal race points. The package runner additionally launches the actual distributed Host as a separate process and tests restart, unread-response recovery followed by real process termination, official maintenance, saved subscriptions and exact artifact inventory.

## Clean target machine

Copy `product` and the separate `verify` folder together to a clean Windows 11 x64 client machine with no development SDK or Node. Use a fresh local state directory and unused ports. Do not alter the product's contents after its manifest has been written. Do not put evidence, profiles or databases under `product` or `verify`.

```powershell
& C:\candidate\verify\runner\LocalMessaging.Verify.exe `
  --scope acceptance --id A03 `
  --product C:\candidate\product `
  --evidence C:\evidence\A03
```

The runner verifies both inventories and their common candidate, starts only the known packaged executable in isolated state, checks its descriptor and real readiness, uses exact HTTP/3 publish/SSE, exercises finite/live HTTP paths, tests PNG/JPEG, checks bounded slow-consumer recovery and SQLite checkpointing, performs restart/backup/restore, and runs the separate store/client/model/WIC/workload helpers. Its test-specific lower heartbeat/write deadlines are recorded with effective configuration; product default limits remain unchanged. Unexpected normal-operation Warning/Error/Critical logs or stderr fail qualification; specifically asserted negative commands are identified separately.

The verifier does not install test hooks in public APIs. The ordinary product executable still refuses unavailable QUIC or invalid selected certificates. Deliberate termination applies only to a process the runner created against its isolated test store. Its cleanup removes only a certificate proven to have been created by that isolated run.

## Actual UI and platform observations

Automated API/model/component checks do not establish that a real window, picker, browser, dispatcher, or proxy configuration works. The following source acceptance cases require actual recorded observations in addition to automated evidence:

| Case | Actual observation required |
| --- | --- |
| A02 | Open both frontends, close them, and verify the independent host continues serving native pub/sub. |
| A07 | Use the real browser over F/HTTP1 for all normal create/view/content/live flows. |
| A33 | Display the same HTML-looking payload as literal text in both actual frontends. |
| A37 | Perform official restore and observe both clients discard stale epoch/cursors and reconstruct state. |
| A39 | Execute the complete A/B ownership, targets, thread depths, validation and image scenario in both UIs. |
| A40 | Delay/lose a send response, switch profiles and navigate; confirm the original command/caller and separate subscription ownership remain intact in each UI. |
| A41 | Exercise arbitrary fragmentation through both actual consuming frontends; component parser tests are supporting evidence. |
| A42 | Start self-contained Host/WinUI and web on the declared clean machine without SDK/Node/CDN. |
| A43 | Select another profile without credentials and demonstrate the same create/view abilities. |
| A47 | Exercise actual cross-origin form/fetch/image attempts and normal same-origin operation; distinguish asserted negative browser diagnostics from unexpected ones. |
| A48 | Stop during ordinary live work, observe bounded drain and reconcile outstanding committed commands by their frozen receipt IDs. |
| A50 | With actual Windows proxy/PAC configured, verify readiness, console client and WinUI still reach loopback directly. |

Use [the native walkthrough](../tests/LocalMessaging.WinUI.Manual.md), the console sample, and the browser scenario under `src/LocalMessaging.Web/tests/browser-scenario.mjs`. The browser script is a development tool requiring Playwright and a permitted browser; it is not part of the production web app. Point it at a fresh isolated product/fixture as its documented invocation specifies. For release evidence, use the production Windows host and real WIC; the Linux known-image fixture cannot qualify image validation.

The minimum paired scenario is: A creates a profile/channel/initial session; B creates a profile and independently owned session; both send text and real PNG/JPEG; target by handle and explicit ID; confirm public visibility; build depths 1/2/3 and reject 4; query roots/flat/thread/mentions; publish during a history load; open two consumers with independent state; disconnect/reconnect; switch profile during unresolved sends; close/reopen UI; restart host; back up and restore; verify no duplicate objects or silent cursor stalls. Observe keyboard/focus, file picker cancellation, visible errors, bounds, and both light/dark layouts.

## Observation records and task criteria

Copy `qualification-observations.template.json` to an evidence directory outside the product. Fill its candidate commit and SHA-256 of the exact `product/manifest.json`, reviewer and environment. Keep every unexecuted item `not_run`. Only after actual execution, record steps, the observed result, unexpected warning count and relative paths to nonempty evidence files. The evidence can be concise text, actual logs/JSON, or requested UI captures. Do not write success merely because a field is mandatory.

The template contains the twelve source cases above and **all 235 task-specific acceptance criteria** from the baseline. Task/demo/checkpoint/milestone scopes require the relevant Txxx-ACxx records in addition to source Axx cases. A task such as T002 has mandatory criteria even though its source-acceptance-reference list is empty; an empty reference list can never make that task pass. For a criterion covered by an automated suite, record the concrete check/result and link its actual report. For an actual UI/platform criterion, record that observation.

```powershell
& C:\candidate\verify\runner\LocalMessaging.Verify.exe `
  --scope acceptance --id All `
  --product C:\candidate\product `
  --evidence C:\evidence\final-run `
  --observations C:\evidence\observations.json
```

The runner requires matching artifact identity, an identified reviewer, complete relevant criteria and nonempty referenced evidence. Missing/blocked observations result in exit 3. Invalid invocation returns 2; execution/assertion failure returns 1. All requested executable evidence passing returns 0. Direct `LocalMessaging.Verify` scope names and IDs use the casing shown in help; the PowerShell wrapper canonicalizes its accepted input.

## Checkpoints and milestone reviews

Use `eng/verify.ps1` with the original task/demo/checkpoint/milestone/acceptance IDs and `-AgainstPackage`. It records package identity separately from the invoking source checkout. `-EvidencePath` selects a new output directory; otherwise a dated directory is created. On a clean machine the standalone EXE is the route that avoids development PowerShell/SDK/Node.

The wrapper and executable deliberately do **not** set a milestone to accepted merely because checks exit zero. Follow TG01–TG08, CG01–CG05 and MG01–MG06 in the unchanged plan. For each milestone, conduct the full cumulative RV01–RV10 review, close every finding/warning with verification, rerun the corrected candidate's affected checks and full cumulative suite, have an independent reviewer check closure/candidate identity, and create the explicit exit record. Required prior checkpoint/milestone decisions remain prerequisites. A source change invalidates affected evidence; a changed product requires a new manifest and matching observations.

The delivered `evidence/reviews` records document the source review performed here and its platform blockers. They are not substitutes for the missing Windows exit records. Historical evidence remains clearly identified; final runtime evidence should reference the exact new candidate/artifact rather than inheriting an old passing label.
