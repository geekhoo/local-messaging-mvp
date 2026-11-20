# Build and operator script review

**Review scope:** the five build/publication/operator scripts, final solution configuration, runtime-pack pinning, and source-helper output paths. This is source and portable helper evidence, not Windows runtime qualification.

## Final corrections verified

- Solution restore/build use the explicit x64 solution configuration without a solution-wide `-r win-x64`. WinUI declares its RID in its own project; publication selects the RID separately for every project. This resolves the concrete NETSDK1134 integration failure without changing the Windows product target.
- The full solution declares both Any CPU and x64 configurations. Common verification projects retain their normal `bin/Release/net10.0/` output path, and the wrapper uses that path. WinUI has its own x64 Windows output.
- The .NET and ASP.NET Core runtime packs are pinned through targeted FrameworkReference metadata in `Directory.Build.targets`. Publication does not set a global RuntimeFrameworkVersion that would corrupt the unrelated Windows SDK reference-pack selection.
- Application lookup selects the first matching executable before invoking it. This closes an actual multi-match PATH failure reproduced with four git installations. Locked restore, Release-only execution, serial MSBuild, disabled shared compilation and warning rejection remain in place. Full Windows publication remains unavailable through the script on Linux.
- Product and verifier inventories have separate manifests tied to the same candidate. User state, certificates and databases remain outside the product payload. The script refuses to overwrite an existing publication.
- Startup uses the fixed bundled executable, verifies the live descriptor/process/instance/epoch and actual host HTTP/3 readiness, and tolerates another valid launcher winning a concurrent startup. Stop verifies the live instance before requesting graceful shutdown. Optional startup registration changes only HKCU Run after an explicit enable/disable request.

## Verification and limits

The actual PowerShell 7.6.10 parser and pure-helper checks passed again on the final scripts. `script-validation.json` and `script-validation.log` record parsing, selected-application invocation, native argument quoting, locked publication arguments, two runtime-family settings, manifest schema/hash/byte counts, and explicit Linux platform rejection. These checks did not launch a Windows process or change a registry.

A peer agent independently reviewed startup/stop/autostart source. Windows process launch, Windows PowerShell 5.1, runtime package publication, registry registration and sign-in behavior still require actual Windows execution. No unexecuted platform operation is marked passed by this record.

## Reproduce the focused checks

The reusable source is retained at `tests/validate-operator-scripts.ps1`. Run from the checkout with PowerShell 7.2 or later:

```powershell
pwsh -NoProfile -File tests/validate-operator-scripts.ps1 -OutputPath evidence/script-validation.json
```

The repository helper completed 24 checks in this Linux execution and records the reviewed file hashes itself. Its paths derive from the checkout and current PowerShell installation. Reports include explicit failures and skipped checks; no platform qualification is inferred from a helper result.

The non-Windows build/publication refusal checks are explicitly inapplicable on Windows. The helper never starts real publication: publication calls in pure checks are mocked, and manifest fixtures use a unique temporary directory.

## Reviewed source snapshot

| File | SHA-256 |
|---|---|
| `eng/build.ps1` | `ba6b741710eecfda68333c5fafed579c47f926d35429a5b415ec19e4afa04594` |
| `eng/publish.ps1` | `597969f803eddfd247b6bfabee8639974c2dd9e6c7d51adf14db610c36a84c5d` |
| `eng/start.ps1` | `c39bea679fb476a379cad0222e42a13243202e16744329120154a5f05eb1ca9d` |
| `eng/stop.ps1` | `ae25b5cfaecfca888a95ec5bc5bd455aa4f3f605b82b75b372a27ffe38c24f43` |
| `eng/autostart.ps1` | `2ef664456ac20dd9153d8da68b5a159196e05897c426c2b1e4809316faf006b1` |
| `eng/verify.ps1` | `167b7db05281e1886d08b6f255ff22e467e249a07521f6bd6a8e8ea1fa4b6b41` |
| `LocalMessaging.slnx` | `260d1b780c79881538add60953af52be6092c2ef271a13b324eb86b7ebc7fc99` |
| `LocalMessaging.Core.slnx` | `44f7a6ab6af300a7d79ddc671aba3cc67021caf6995b3403dd2297a64b8f9e59` |
| `Directory.Build.props` | `88b135a97888010906fb33e7f2704db89029b845f22653a12ff5839296b8ed94` |
| `Directory.Build.targets` | `7e7bd2837a175a8c94b1e7469041c1fd1478327181c63b433f20b7c5cee3c143` |
| `tests/validate-operator-scripts.ps1` | `240c11e0819767076ab52dacb22723809c5235d6c352c2b4f30aed55322e9493` |

Recorded at 2026-11-20T10:45:49.923969Z.
