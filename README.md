# Local Messaging

Local Messaging runs on Windows 11 x64. A per-user .NET host stores messages in SQLite and serves a browser app and a native WinUI 3 app. The [console sample](samples/LocalMessaging.ConsoleClient/README.md) and [coding-agent plugin](plugins/local-messaging/README.md) can connect to the same host.

A full Windows x64 build passed with zero warnings. That is build evidence, not release qualification. See [what was verified](docs/validation.md).

## What you can do

- Create a profile, channel, or session. A new channel starts with one session. Another profile can start its own session without taking over the channel.
- Send text or a PNG/JPEG, target a profile by `@handle`, and reply up to three levels deep, including the original message. Targets appear in Mentions but remain public.
- Acknowledge a message or join a public roster. Neither changes what you receive through a delivery subscription. See [public profile actions](docs/public-profile-actions.md).

Local profiles are labels, not logins. Everyone using the host can see the same messages. Messages cannot be edited or deleted.

## Source layout

| Path | Responsibility |
| --- | --- |
| `src/LocalMessaging.Contracts` | Public DTOs, discriminators, protocol options, errors, limits |
| `src/LocalMessaging.Application` | Pure normalization, validation, canonical commands, cursors |
| `src/LocalMessaging.Infrastructure` | EF entities/migration, transactional commands, queries/replay, maintenance |
| `src/LocalMessaging.Host` | Kestrel listeners, routes, multipart/SSE, certificate/readiness, process lifecycle |
| `src/LocalMessaging.Platform.Windows` | Explicit built-in PNG/JPEG WIC validation |
| `src/LocalMessaging.Client` | Frozen commands, direct local HTTP/3/F clients, bounded SSE/cursor recovery |
| `src/LocalMessaging.WinUI` | Native WinUI controls, model, images, isolated endpoint startup |
| `src/LocalMessaging.Web` | Framework-free static application and development tests |
| `samples/LocalMessaging.ConsoleClient` | Runnable native pub/sub example |
| `tests` | Real SQLite, HTTP, client, native-model, WIC and published-product verification |
| `eng` | Build, verification, publication, start, stop, opt-in autostart scripts |
| `docs/baseline-plan` | Original specification and complete plan, preserved unchanged |
| `evidence` | Actual execution/review records; historical and final evidence are distinguished |

The small pure domain rule layer resides in Application rather than an empty additional Domain assembly. Its responsibilities remain separate from EF persistence and HTTP/UI code; see [implementation decisions](docs/implementation-decisions.md).

## Build on Windows

Use Windows 11 x64, the **10.0.404** .NET SDK selected by `global.json`, PowerShell 7.2+ for development scripts, and the Windows/WinUI build prerequisites. NuGet restore supplies the pinned Windows App SDK and Windows SDK build dependencies. The product's published executables do not require a development SDK, Node, or PowerShell 7.

Pinned inputs are in `global.json`, `Directory.Packages.props`, `Directory.Build.targets`, and every `packages.lock.json`. EF Core is 10.0.15, Windows App SDK is 2.7.0, and the .NET runtime packs are 10.0.15. Windows targeting uses `net10.0-windows10.0.26100.0`.

From the repository root:

```powershell
pwsh -NoProfile -File eng/build.ps1
```

This restores locked dependencies and builds `LocalMessaging.slnx` in Release/x64 with warnings treated as errors. A successful build is compilation evidence; it does not accept the runtime or UI gates.

For portable implementation checks only:

```powershell
pwsh -NoProfile -File eng/build.ps1 -CoreOnly
pwsh -NoProfile -File eng/verify.ps1 -Scope portable -IncludeWorkload
```

Portable mode builds `LocalMessaging.Core.slnx`; it excludes the WinUI application. It exercises real SQLite and actual loopback HTTP through an internal test host. Its exact-known-image fixture is explicitly not a WIC replacement, and it exposes no production fallback switch.

This source folder can build without Git, but publishing requires a clean Git commit. To publish these files from an extracted workspace, import and commit them first. `source-history.bundle`, if present, contains the original delivery's history, not the later workspace changes.

## Publish and run

Publish from a clean committed Windows checkout:

```powershell
pwsh -NoProfile -File eng/publish.ps1 -OutputPath C:\builds\LocalMessaging-candidate
```

The script creates two folders. `product` contains the self-contained Host, self-contained WinUI app, self-contained console client, web assets, and operator scripts. `verify` contains separate self-contained qualification executables. Both have file inventories, SHA-256 digests, dependency versions, and the same source commit. The product excludes tests, private keys, certificates, endpoint descriptors, and databases.

Start the host and open the web app:

```powershell
powershell -NoProfile -File C:\builds\LocalMessaging-candidate\product\eng\start.ps1 `
  -ProductDirectory C:\builds\LocalMessaging-candidate\product -OpenWeb
```

Open the native app:

```powershell
& C:\builds\LocalMessaging-candidate\product\winui\LocalMessaging.WinUI.exe
```

The normal web address is `http://127.0.0.1:7444/`. The native messaging endpoint is `https://localhost:7443/messaging/v1/`. On first start the host creates a current-user localhost certificate and publishes `endpoint.json` only after its exact HTTP/3 self-probe verifies the running instance. The reusable client pins this certificate while checking hostname and validity. No certificate-validation bypass is provided.

Closing either frontend leaves the host running. To request shutdown:

```powershell
powershell -NoProfile -File C:\builds\LocalMessaging-candidate\product\eng\stop.ps1 `
  -ProductDirectory C:\builds\LocalMessaging-candidate\product
```

State defaults to `%LOCALAPPDATA%\LocalMessaging`, with the database under `data`. Keep state outside the product folder. [Operator instructions](docs/operators.md) cover alternate ports/stores, startup registration, backup, restore, certificate renewal, and upgrades.

## Use the application

Create a profile or select an existing one. Create a channel; the first session appears with the channel owner as its owner. Select another profile and create a session in the same channel to exercise independent session ownership. Send text or a PNG/JPEG, select targets or type a known `@handle`, and open a thread to reply. A root is level 1, its child level 2, and its grandchild level 3. A reply below level 3 is rejected.

The browser tab and native window maintain their own selected user and subscription cursor. A send freezes its request ID, original caller, payload, and image bytes. An uncertain retry reuses those values, even if the selected profile changes. In-flight errors and reconnection states are visible; store epoch changes trigger resnapshot and resubscribe behavior.

The console sample exercises the same API without either frontend. See [its runnable commands](samples/LocalMessaging.ConsoleClient/README.md) and [the API guide](docs/api.md). The live machine-readable OpenAPI document is available at `/api/v1/openapi.json` with the required client header.

## Verify a Windows candidate

The planned task/demo/checkpoint/milestone/acceptance entry points are implemented. Unknown IDs, missing tools/evidence, failed checks, and missing Windows execution never return a passing gate.

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T007 -AgainstPackage C:\builds\LocalMessaging-candidate\product
pwsh -NoProfile -File eng/verify.ps1 -Scope checkpoint -Id CP08 -AgainstPackage C:\builds\LocalMessaging-candidate\product
pwsh -NoProfile -File eng/verify.ps1 -Scope milestone -Id M06 -AgainstPackage C:\builds\LocalMessaging-candidate\product `
  -Observations C:\evidence\observations.json
```

On a clean target machine, use the separate executable directly; no SDK or Node is required:

```powershell
& C:\builds\LocalMessaging-candidate\verify\runner\LocalMessaging.Verify.exe `
  --scope acceptance --id All `
  --product C:\builds\LocalMessaging-candidate\product `
  --evidence C:\evidence\LocalMessaging `
  --observations C:\evidence\observations.json
```

Exit 0 means the requested executable evidence is complete, 1 a failure, 2 invalid invocation, and 3 blocked/incomplete. Milestone acceptance is a separate independent review. The original plan requires a full cumulative review and closure of every issue/warning at every milestone. [The Windows qualification guide](docs/windows-qualification.md) documents required actual UI and platform observations and the evidence schema. A copied template or a successful compilation cannot satisfy those observations.

## Documents

- [Validation and limitations](docs/validation.md)
- [Operator runbook](docs/operators.md)
- [API and client usage](docs/api.md)
- [Public acknowledgements and rosters](docs/public-profile-actions.md)
- [Coding-agent plugin](plugins/local-messaging/README.md)
- [Windows qualification and review gates](docs/windows-qualification.md)
- [Implementation decisions](docs/implementation-decisions.md)
- [Task implementation/evidence matrix](docs/task-matrix.csv)
- [Original specification and plan](docs/baseline-plan/README.md)
