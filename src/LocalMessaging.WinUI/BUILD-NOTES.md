# Native build and validation

The product project defaults to an unpackaged Windows x64 application with both .NET and Windows App SDK runtime content self-contained. Its native controls are constructed in C#; it is a real WinUI 3 desktop application, with no embedded browser.

From the workspace root, run `pwsh -NoProfile -File .\eng\build.ps1` on Windows. Use the [workspace publication script](../../README.md#publish-and-run) to package WinUI with its host and the verifier. Publishing only the UI project does not produce a runnable product folder.

The native launcher only runs the known sibling path `../host/LocalMessaging.Host.exe`. Endpoint metadata never supplies an executable path. Closing the frontend cancels its own work and leaves the host running.

Use `LocalMessaging.WinUI.exe --endpoint C:\LocalMessaging-Test\endpoint.json --no-launch-host` to connect to an explicitly started isolated qualification host. Without `--no-launch-host`, the known sibling host receives `--state-dir` for that descriptor directory. A custom descriptor never falls back to the user's default store. Native settings are scoped beside that descriptor.

## Managed-only checks

The full build needs Windows resource tools (`mt.exe` and `MakePri.exe`). To diagnose C# compilation separately, use the command below. It does not package or run the app.

```sh
dotnet msbuild src/LocalMessaging.WinUI/LocalMessaging.WinUI.csproj -target:ResolveReferences,Compile -p:Configuration=Release -p:WindowsAppSDKSelfContained=false -p:UseSharedCompilation=false -m:1 -nodeReuse:false -verbosity:minimal
```

Run the actual headless WIC corpus on Windows:

```powershell
dotnet run --project tests/LocalMessaging.Platform.WindowsTests -c Release
```

On a non-Windows system the corpus exits with code 2 and an explicit blocked result. It does not use a fake decoder or mark the Windows gate passed.

Portable client checks use:

```sh
dotnet run --project tests/LocalMessaging.ClientTests -c Release
dotnet run --project tests/LocalMessaging.NativeModelTests -c Release
```

These exercise the incremental SSE parser, byte watchdog, frozen JSON/image ownership, HTTP header/error behavior, and apply-before-cursor ordering. The HTTP fixture is identified as a client fixture, not product SQLite/HTTP3/WinUI acceptance evidence.


The native model suite compiles the actual `ShellViewModel`, `ThreadProjection` and `NativeOptions` sources into a portable fixture. It verifies serialized subscription ownership, immediate navigation cancellation, canceled-bootstrap recovery, composer-scope admission, selected objects beyond the first page, and the snapshot/live-thread race. It does not instantiate WinUI controls or close native Windows acceptance criteria.

The runnable [console client](../../samples/LocalMessaging.ConsoleClient/README.md) demonstrates the shared client against a separately running real host. Its default is strict HTTP/3 M; `--frontend` explicitly chooses F. It can operate alongside WinUI with a separate subscription/cursor.
