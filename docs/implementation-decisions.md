# Implementation decisions and source alignment

The original design specification and plan under `baseline-plan` are unchanged. This document records implementation details, not a replacement product scope.

## Build inputs and runtime pins

The implementation uses SDK 10.0.404, EF Core/SQLite 10.0.15, Windows App SDK 2.7.0, and Windows target 10.0.26100.0. Every project has a NuGet lockfile, and restore/build/publication reject warnings. `RuntimeIdentifiers=win-x64` makes Windows dependency graphs part of the checked-in lock inputs while portable projects remain buildable without selecting a Windows runtime at execution.

`Directory.Build.targets` pins only the .NET and ASP.NET Core runtime packs. A global `RuntimeFrameworkVersion` also changes the Windows SDK reference pack and causes NU1505. The full solution builds as x64 without a solution-wide runtime identifier (which causes NETSDK1134); publication supplies `win-x64` per project.

WinUI builds its controls in C# and ships with both the .NET and Windows App SDK runtimes. See [native build notes](../src/LocalMessaging.WinUI/BUILD-NOTES.md) for managed-only diagnostics.

## Domain organization

Pure input/domain normalization and cursor rules are in Application. Transaction-dependent invariants are applied in Infrastructure's command pipeline before and during the single SQLite transaction. This keeps rules, persistence and transport separate without adding an empty Domain project solely to mirror a proposed folder diagram. HTTP and both UIs reuse the same engine; no alternate ownership/thread/target implementation exists in a frontend.

SQLite enforces relational constraints in addition to request validation. Generate migrations and snapshots from the EF model; do not use `EnsureCreated` on a migrated store. The [public profile actions](public-profile-actions.md) migration adds acknowledgements and append-only participation without changing message rows.

## Persistence and delivery

Every creation transaction writes the object graph, target rows, image bytes if any, durable ordered events, immutable receipt, and optional coupled subscription together. Only committed work signals live listeners. Request IDs share one namespace across operation kinds. Canonical request semantics normalize defaults/IDs/targets and include image-byte digest while excluding multipart filename/boundary.

Queries use short contexts and immutable snapshot H. Finite replay first scans bounded headers, then materializes bounded matching payloads. The returned examined cursor does not move past an excluded matching event. SSE repeatedly uses that finite replay source, performs network writes after database disposal, broadcasts wake signals to independent consumers and polls as a safety net. Close/attach registration uses a generation check rather than an unbounded set of closed subscription IDs.

Maintenance uses the same exclusive process/store lock. Restore prepares the replacement's new epoch and closed subscriptions before publishing the main file, with sidecar checkpointing and a safety backup. Fault tests exercise before/after commit and before/after restore publication without adding public fault-injection routes.

## Replies, acknowledgements and line breaks

Message text canonicalizes CRLF and bare CR to LF in Application before control-character and length checks; the Web and WinUI clients submit editor text unchanged and render stored LF with native text layout or pre-wrap. Custom line-break tests cover root and nested messages.

Reply counts, committed acknowledgement counts and the latest acknowledging handle are derived in the same query as each message and pinned to the snapshot cursor; immutable messages and receipts are unchanged. `liveAcknowledgementSummaries` advertises snapshot summaries plus the optional `allAcknowledgements` replay branch. That branch is explicit so existing `allMessages` consumers keep their event contract. Both frontends opt in only when the capability is present, merge live events once per snapshot cursor, highlight incoming replies only for messages outside the open thread, and clear the highlight without clearing counts when the thread opens. Thread composers are separate from the session draft and send a frozen command for the selected parent, including nested parents.

## Local trust and image behavior

M is genuinely HTTP/3-only with TLS 1.3. Readiness is false until an exact HTTP/3 native client receives the correct running instance. F is loopback HTTP/1.1 and serves both frontends. Endpoint dispatch checks actual local port and authority before route/static handling; static middleware is additionally restricted to F and excludes its API namespace.

The current-user localhost certificate persists its private key in the user key store. Supplied clients bypass system HTTP proxies for loopback, compare the advertised SHA-256 pin, and still validate hostname/dates. This is local certificate pinning, not authentication of an application user. There is no accept-any-certificate path.

The WIC validator selects built-in PNG/JPEG codecs explicitly, rejects animated PNG, validates PNG integrity, checks decoded dimensions with checked arithmetic, and fully decodes in bounded stripes under a two-slot semaphore. The portable transport harness accepts only one exact known PNG fixture; that deliberately narrow fixture tests multipart/transaction/descriptor/byte transport without pretending to validate WIC.

## Client concurrency and bounds

Commands are frozen before their first send, with immutable request identity, original caller, JSON and image bytes. Both clients validate that a response is the matching committed receipt before clearing a draft. Retry and reconciliation use the same command; changing profiles does not relabel it. Streams parse incremental UTF-8 and SSE, measure byte-level liveness, validate epoch/sequence/checkpoint agreement, deduplicate replay, and advance a cursor only after application succeeds.

The native model serializes selection/reload transitions, preserves subscription ownership while closing an old consumer, and cancels obsolete view work. A current-scope admission check prevents an old visible draft from being frozen under a newly selected user/session. Thread views buffer live replies arriving during their snapshot query. Development tests compile these actual model source files into a portable harness; actual picker/dispatcher/window behavior remains a Windows gate.

## WinUI startup requirements (code-only application)

The WinUI application intentionally has no XAML markup: `MessagingApp` and all controls are constructed in C#. Three requirements follow from that shape, each failing as an opaque startup crash (`0xC000027B` in `Microsoft.UI.Xaml.dll`) when unmet. The Application class implements `IXamlMetadataProvider` and delegates to WinUI's built-in `Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider`; the XAML compiler normally generates this provider from `App.xaml`/Page items and a code-only project gets none (microsoft-ui-xaml#7606). `Application.Resources` is not touched in the constructor — `XamlControlsResources` is merged in `OnLaunched`, where the XAML host is ready (microsoft-ui-xaml#4639). And the XAML compiler's `$(AssemblyName).pri` resource index is copied beside the published executable by the `IncludeAppPriInPublish` target, because the publish item set omits it and unpackaged `ms-appx` resource resolution requires it at startup.

The browser is native ES modules with no production dependencies. Per-tab state isolates profiles/subscriptions. Text is rendered with safe DOM operations. Images load near the viewport, with four in flight and a bounded retained URL cache. History and thread pages use sliding bounded windows so paging remains reachable after the cache fills. The sidebar is a single accordion tree of channels and their sessions: selecting a channel expands its sessions in place, session lists load lazily per expanded channel into an LRU-bounded map of at most twelve channels, and live session events merge into already-loaded lists. These are implementation choices within the source's bounded-memory requirement.

## Diagnostics and operations

The Host has a small rotating JSONL provider. Command outcome logging occurs after exceptions have been translated into their final HTTP status and includes a validated request ID where available. File I/O failures are contained; they produce one stderr warning per episode rather than changing a committed command's HTTP result. A real HTTP regression obstructs the log path, verifies 201/receipt/retry behavior, restores it, and checks logging resumes without payload leakage.

Launcher/stop scripts verify the descriptor against a known bundled executable and live instance. Optional autostart is a deliberate HKCU Run operation, separate from ordinary start and publication. Product and verifier output folders have independent file manifests bound to the same source commit. These manifests establish inventory/evidence consistency; they are not a vendor code-signing mechanism.

## Verification boundaries

The portable solution, real SQLite suites, actual F HTTP server, stalled socket/replay/checkpoint tests, native models, and JavaScript components supply concrete implementation evidence. The production CLI still refuses a non-Windows runtime and cannot silently substitute the test fixture.

See [validation status](validation.md) for recorded results and [Windows qualification](windows-qualification.md) for the remaining release checks. Portable fixtures cannot qualify production HTTP/3, WIC, or actual desktop controls.
