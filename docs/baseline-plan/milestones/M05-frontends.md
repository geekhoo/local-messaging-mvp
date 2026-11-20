# M05 — Web and WinUI feature parity

**Status:** planned · **Gate:** not_run · **Full cumulative review:** mandatory

## Objective

Build complete framework-free web and WinUI clients with equal content, live update, identity, and recovery behavior.

## Entry

M04 must have passed its full cumulative review and zero-unresolved-findings exit gate. Every task also requires its explicit dependency and checkpoint-entry gates.

## Exit

Both actual frontends run together against the same host and demonstrate equivalent persisted results and errors. All checkpoints below and MG01–MG06 must pass. Resolve every issue/warning and rerun cumulative verification on the final corrected candidate.

**Scope note:** Early native tasks use an already running host; packaged launch/startup integration belongs to M06.

## Task map

| Task | Outcome | Task dependencies | Checkpoint-entry gate | Checkpoint |
| --- | --- | --- | --- | --- |
| T025 | Run the web shell with profiles and catalog | T024 | None | CP09 |
| T026 | Create web conversations and send root text | T025 | None | CP09 |
| T027 | Use web threads, targets, and mentions | T026 | CP09 | CP11 |
| T028 | Send and render images in the webapp | T026 | CP09 | CP11 |
| T029 | Keep the browser view live and recover safely | T027, T028 | None | CP12 |
| T030 | Run the native shell with profiles and catalog | T024 | None | CP10 |
| T031 | Create native conversations and send root text | T030 | None | CP10 |
| T032 | Use native threads, targets, and mentions | T031 | CP10 | CP11 |
| T033 | Send and render images in WinUI | T031 | CP10 | CP11 |
| T034 | Keep the native view live and recover safely | T032, T033 | None | CP12 |

## Integrated checkpoints

### CP09 — Browser text conversation

**Required task gates:** T025, T026. **Prior checkpoint gates:** None within this milestone. **Status:** not_run.

**Preconditions**

- M04 accepted; host started explicitly; actual browser on the same-origin F address.

**Integrated steps**

1. From an empty profile state, create/select A and B without credentials and browse channels/sessions.
2. A creates a channel; B creates a session; each sends root text and reloads.
3. Submit HTML-looking text and force an uncertain send before a profile change; retry with its original frozen identity.

**Expected results**

- Browser uses local HTML/CSS/ES modules with no framework/Node/CDN runtime dependency.
- Owners and persisted text match the source rules after reload.
- Text renders literally; uncertain retries cannot change caller/content/request ID; visible errors preserve the draft.

**Platform:** Windows 11 x64; real Kestrel and SQLite where implemented; actual browser/WinUI for UI assertions

**Planned verification command**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope checkpoint -Id CP09 -Configuration Release
```

**Evidence:** one integrated-candidate [checkpoint record](../templates/checkpoint-result.md), actual commands/results, fixture identity, applicable diagnostics, and closure links. Apply CG01–CG05; constituent task passes alone do not suffice.

**Unlocks:** T027 and T028; CP10 may proceed independently; CP11 waits for both.

### CP10 — Native text conversation

**Required task gates:** T030, T031. **Prior checkpoint gates:** None within this milestone. **Status:** not_run.

**Preconditions**

- M04 accepted; host started explicitly; actual unpackaged WinUI process on Windows 11 x64.

**Integrated steps**

1. Open WinUI, create/select A and B, and browse owners/channels/sessions through F.
2. Repeat channel/session/text creation and reopen the native app.
3. Exercise loading/cancellation, HTML-looking text, and uncertain retry around a profile change.

**Expected results**

- Native UI uses the shared client and never opens SQLite; network work does not block the dispatcher.
- Ownership, persistence, and errors match the shared A/B text contract scenario; actual cross-frontend comparison occurs at CP11.
- Literal text remains safe; pending send identity is stable; closing the UI leaves the already-running host intact.

**Platform:** Windows 11 x64; real Kestrel and SQLite where implemented; actual browser/WinUI for UI assertions

**Planned verification command**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope checkpoint -Id CP10 -Configuration Release
```

**Evidence:** one integrated-candidate [checkpoint record](../templates/checkpoint-result.md), actual commands/results, fixture identity, applicable diagnostics, and closure links. Apply CG01–CG05; constituent task passes alone do not suffice.

**Unlocks:** T032 and T033; CP09 may proceed independently; CP11 waits for both.

### CP11 — Content parity across both frontends

**Required task gates:** T027, T028, T032, T033. **Prior checkpoint gates:** CP09, CP10. **Status:** not_run.

**Preconditions**

- Both text checkpoints accepted; same host and equivalent scenario fixtures; reload/manual refresh allowed until live tasks pass.

**Integrated steps**

1. In each UI send targeted text, navigate Mentions, create three thread levels, and page an expanded thread.
2. Send PNG/JPEG with caption and targets, reload images, then submit invalid/oversized input.
3. Compare persisted objects/errors and inspect literal rendering, picker behavior, focus/labels, and resource cleanup.

**Expected results**

- Both clients preserve public targeting, exact depth limits, no target inheritance, and complete ancestor context.
- Images/captions/targets survive reload; errors preserve drafts and immutable retry identity.
- Web object URLs/native resources are released; actual WinUI picker works unpackaged; no unsupported rich-text/auth/edit feature appears.

**Platform:** Windows 11 x64; real Kestrel and SQLite where implemented; actual browser/WinUI for UI assertions

**Planned verification command**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope checkpoint -Id CP11 -Configuration Release
```

**Evidence:** one integrated-candidate [checkpoint record](../templates/checkpoint-result.md), actual commands/results, fixture identity, applicable diagnostics, and closure links. Apply CG01–CG05; constituent task passes alone do not suffice.

**Unlocks:** CP12 once T029 and T034 are accepted; those tasks can develop after their own content dependencies.

### CP12 — Live frontend parity and identity recovery

**Required task gates:** T029, T034. **Prior checkpoint gates:** CP11. **Status:** not_run.

**Preconditions**

- Integrated web and native clients open together; two browser tabs; controlled host interruption/stream fragmentation.

**Integrated steps**

1. Load both UIs while publishing; alternate targeted text/image/thread sends from each frontend.
2. Interrupt/restart connections, keep streams quiet through heartbeats, and fragment frames; compare final state with persisted history.
3. Switch one profile during an unresolved send; close only that client's old subscription; exercise tab isolation and bounded inactive cache.

**Expected results**

- Both clients converge to every committed relevant message without duplicate visible state.
- Quiet healthy streams stay connected; one bounded reconnect loop runs per subscription; cancellation stops obsolete work.
- Original caller survives unresolved sends; consumers remain independent; another tab's stored last-used profile does not change active caller.

**Platform:** Windows 11 x64; real Kestrel and SQLite where implemented; actual browser/WinUI for UI assertions

**Planned verification command**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope checkpoint -Id CP12 -Configuration Release
```

**Evidence:** one integrated-candidate [checkpoint record](../templates/checkpoint-result.md), actual commands/results, fixture identity, applicable diagnostics, and closure links. Apply CG01–CG05; constituent task passes alone do not suffice.

**Unlocks:** M05 full cumulative review; after acceptance, T035 and M06.

## Atomic task cards

Every card inherits TG01–TG08 from [the gate policy](../plan/02-gates-and-reviews.md). Commands are planned interfaces implemented by T001, not executed evidence in this package. The [source specification](../specs/local-messaging-design-specification-v1.md) is authoritative; section/requirement/acceptance references below are local traceability.

### T025 — Run the web shell with profiles and catalog

**Outcome:** A framework-free browser app that creates/selects profiles and browses real channel/session data.

**Owner role:** web implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T024. **Previous milestone gate:** M04. **Checkpoint-entry gates:** None. **Contributes to:** CP09.

**Source:** sections 13.1, 16.1, 16.2, 16.5, 19; requirements R03, R09, R15, R16, R17; supporting/related scenarios A07, A43, A47. **Primary closure ownership:** No exclusive source-scenario closure; task criteria still mandatory.

**Subtasks**

- **T025-ST01** — Serve semantic HTML, local CSS/ES modules, small in-memory state, and the shared-origin fetch API layer.
- **T025-ST02** — Implement empty-store create user, explicit profile selection, and remembered last-used default.
- **T025-ST03** — Render channel/session navigation and independent ownership labels from API queries.
- **T025-ST04** — Add real-browser interactions and baseline keyboard/focus/console checks.

**Acceptance criteria — all required**

- **T025-AC01** — An empty store offers Create User and the selected profile is retrieved again after reload without any login flow.
- **T025-AC02** — Channel/session directories are queryable and owner labels remain distinct.
- **T025-AC03** — All API requests include the required header and same-origin policy; no CDN/framework/Node runtime is required.
- **T025-AC04** — Message/user-provided labels are rendered through safe text nodes; browser console has no unexpected errors/warnings.
- **T025-AC05** — The active caller is per tab; another tab's localStorage change cannot switch it automatically.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T025 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T025 -Configuration Release
```

**Observable demonstration:** Open F in a real browser, create/select profiles, browse a seeded channel/session directory, reload, and inspect requests and console.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No messaging composer yet, framework adoption, browser-managed auth, or host startup from arbitrary page JavaScript.

### T026 — Create web conversations and send root text

**Outcome:** A browser slice for creating containers, viewing root history, and sending/reconciling text drafts.

**Owner role:** web implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T025. **Previous milestone gate:** M04. **Checkpoint-entry gates:** None. **Contributes to:** CP09.

**Source:** sections 7.1, 9.2, 13.4, 16.2; requirements R07, R08, R12, R15, R17; supporting/related scenarios A09, A11, A13, A15, A33. **Primary closure ownership:** No exclusive source-scenario closure; task criteria still mandatory.

**Subtasks**

- **T026-ST01** — Add channel/initial-session and session creation dialogs using canonical selectors.
- **T026-ST02** — Add paginated root history and plain-text composer with server canonical-result merging.
- **T026-ST03** — Preserve immutable in-flight caller/request/payload and actionable failed/uncertain states.
- **T026-ST04** — Exercise A/B ownership and response/event-independent duplicate handling with real API calls.

**Acceptance criteria — all required**

- **T026-AC01** — A creates exactly one initial session with a new channel; B creates a session there without changing the channel owner.
- **T026-AC02** — Sent root text persists, reloads, and displays author/targets/content through canonical DTOs.
- **T026-AC03** — Uncertain retries reuse the same request identity; editing a failed draft allocates a new ID.
- **T026-AC04** — Validation failures preserve the draft and show field-specific errors; Sent means committed, not read/delivered.
- **T026-AC05** — HTML-looking content renders literally and never creates executable DOM.
- **T026-AC06** — Loaded root pages remain bounded and chronological display does not corrupt pagination order.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T026 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T026 -Configuration Release
```

**Observable demonstration:** In the browser create A's channel and B's session, send/reload text, simulate a lost response, retry, and inspect one stored and visible message.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No editor/delete UI, offline queue, rich text, or recipient read receipts.

### T027 — Use web threads, targets, and mentions

**Outcome:** A runnable browser thread/target experience with paginated context and a profile-specific mentions view.

**Owner role:** web implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T026. **Previous milestone gate:** M04. **Checkpoint-entry gates:** CP09. **Contributes to:** CP11.

**Source:** sections 7.3, 7.4, 13.4, 16; requirements R10, R11, R15, R17; supporting/related scenarios A24, A25, A26, A27, A28, A29, A39. **Primary closure ownership:** No exclusive source-scenario closure; task criteria still mandatory.

**Subtasks**

- **T027-ST01** — Add expandable threads, reply context, and server-root/depth rendering.
- **T027-ST02** — Add handle autocomplete, explicit target chips, and selected-user mentions history.
- **T027-ST03** — Surface server validation without changing targeting/public visibility semantics.
- **T027-ST04** — Exercise small reply pages and messages whose roots are outside loaded history.

**Acceptance criteria — all required**

- **T027-AC01** — Root/reply/nested reply render as three levels; Reply is disabled at level 3 and backend rejection is still surfaced.
- **T027-AC02** — Pagination supplies missing ancestors and never displays a nested reply as a new root.
- **T027-AC03** — Known mentions/explicit targets display consistently; unknown recognized handles preserve the draft and show their error.
- **T027-AC04** — Mentions show only matching targets for the selected profile; public session history remains visible to others.
- **T027-AC05** — Reply targets are not silently inherited; target chips derive from canonical server IDs.
- **T027-AC06** — The flow remains keyboard-usable with visible focus and labelled controls.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T027 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T027 -Configuration Release
```

**Observable demonstration:** Send targeted text, open it from Mentions, create a three-level thread, page replies, and try a fourth-level or unknown-target send.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No private conversations, inherited recipients, reactions, or unlimited nesting.

### T028 — Send and render images in the webapp

**Outcome:** A browser image-message slice with one-file selection, multipart send, safe display, and stable retry identity.

**Owner role:** web implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T026. **Previous milestone gate:** M04. **Checkpoint-entry gates:** CP09. **Contributes to:** CP11.

**Source:** sections 16.5, 17; requirements R12, R15, R19; supporting/related scenarios A30, A31, A32, A33, A39. **Primary closure ownership:** No exclusive source-scenario closure; task criteria still mandatory.

**Subtasks**

- **T028-ST01** — Add file selection/preview/caption and a multipart command built from a frozen draft snapshot.
- **T028-ST02** — Fetch image content with the required header and render/revoke object URLs.
- **T028-ST03** — Preserve failures/uncertain sends and enforce client hints without replacing server validation.
- **T028-ST04** — Exercise valid, invalid, oversized, and retried images through the actual browser.

**Acceptance criteria — all required**

- **T028-AC01** — Valid PNG/JPEG with optional caption sends and reloads from the persisted image content route.
- **T028-AC02** — Invalid/oversized content produces the server error and preserves the editable draft.
- **T028-AC03** — Retries use unchanged bytes/caller/request ID even when the transport multipart boundary changes.
- **T028-AC04** — Ordinary JSON/event/history contains descriptors only, with no base64 or file path.
- **T028-AC05** — Object URLs and obsolete image loads are disposed, and concurrent fetching stays bounded.
- **T028-AC06** — No returned text or image filename is inserted as unsafe HTML.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T028 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T028 -Configuration Release
```

**Observable demonstration:** Select and send a PNG/JPEG, reload it, retry an uncertain image send, and reject a corrupt/oversized file while checking the draft and resource cleanup.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No external image URLs, multiple attachments, browser image transformation, or upload staging.

### T029 — Keep the browser view live and recover safely

**Outcome:** A web client that performs snapshot/replay startup, applies live SSE idempotently, and survives reconnect/profile/store changes.

**Owner role:** web implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T027, T028. **Previous milestone gate:** M04. **Checkpoint-entry gates:** None. **Contributes to:** CP12.

**Source:** sections 11, 13.3, 16.3, 16.5, 16.6; requirements R03, R06, R15, R19; supporting/related scenarios A07, A20, A23, A39, A40, A41, A49. **Primary closure ownership:** A07.

**Subtasks**

- **T029-ST01** — Implement incremental fetch-SSE parsing, byte/comment watchdog, checkpoints, and one bounded reconnect loop.
- **T029-ST02** — Integrate initial snapshot H with live replay and response/event canonical merging.
- **T029-ST03** — Implement explicit profile switching with old-owner subscription closure and in-flight identity preservation.
- **T029-ST04** — Keep per-tab identity/cursor/subscription isolated and bound inactive-conversation cache state.

**Acceptance criteria — all required**

- **T029-AC01** — Split UTF-8 and CR/LF/CRLF/multiline SSE fixtures and real streams apply correctly; heartbeat comments keep quiet connections alive.
- **T029-AC02** — Publishing during initial load and response/event reordering produce a complete view with one visible canonical message.
- **T029-AC03** — Profile switches never relabel pending sends; closing the old subscription does not affect other clients.
- **T029-AC04** — Another tab's storage events cannot change the active caller, and cloned/reloaded tabs do not take exclusive ownership of a shared subscription.
- **T029-AC05** — Connection loss shows useful state; reconnect is bounded, honors Retry-After, and stops on cancellation or unsupported contract.
- **T029-AC06** — Store-epoch change clears scoped state, revalidates profile IDs, and reloads history.
- **T029-AC07** — The browser completes every implemented feature using the frontend listener without browser HTTP/3 dependence.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T029 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T029 -Configuration Release
```

**Observable demonstration:** Keep two tabs open while publishing, interrupt the host, reconnect, switch a profile around an unresolved send, and observe correct history and independent clients.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No IndexedDB message replica, offline send queue, or live stream per channel.

### T030 — Run the native shell with profiles and catalog

**Outcome:** A real WinUI frontend connected to the host through the shared .NET client, with profiles and catalog navigation.

**Owner role:** winui implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T024. **Previous milestone gate:** M04. **Checkpoint-entry gates:** None. **Contributes to:** CP10.

**Source:** sections 16.1, 16.2, 16.4; requirements R03, R09, R14, R16, R17; supporting/related scenarios A02, A39, A43. **Primary closure ownership:** No exclusive source-scenario closure; task criteria still mandatory.

**Subtasks**

- **T030-ST01** — Connect native views/view models to the reusable client and dispatcher-safe result updates.
- **T030-ST02** — Implement create/select user and channel/session navigation with independent owner labels.
- **T030-ST03** — Use cancellation and a visible host-unavailable state; assume the host is manually running at this increment.
- **T030-ST04** — Add actual Windows UI smoke steps and focused view-model integration checks.

**Acceptance criteria — all required**

- **T030-AC01** — The native app opens on the qualified Windows target and creates/selects real profiles without authentication.
- **T030-AC02** — Catalog data and ownership come from API DTOs; the native process never opens SQLite.
- **T030-AC03** — UI interaction remains responsive during network work and obsolete loads are cancelled.
- **T030-AC04** — Closing the native window disposes its connection work without stopping the host.
- **T030-AC05** — Unavailable host errors are actionable and do not launch any path supplied by untrusted endpoint metadata.
- **T030-AC06** — Basic focus order, control labels, and keyboard navigation work.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T030 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T030 -Configuration Release
```

**Observable demonstration:** With the real host running, open WinUI, create/select a user, navigate channels/sessions, then close the window and verify host status remains reachable.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No automatic host launcher yet, embedded browser replacement, native database access, or UI framework change.

### T031 — Create native conversations and send root text

**Outcome:** A WinUI slice for creating containers, viewing root history, and sending/reconciling text.

**Owner role:** winui implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T030. **Previous milestone gate:** M04. **Checkpoint-entry gates:** None. **Contributes to:** CP10.

**Source:** sections 7.1, 9.2, 13.4, 16.2, 16.4; requirements R07, R08, R12, R14, R17; supporting/related scenarios A09, A11, A13, A15, A33. **Primary closure ownership:** No exclusive source-scenario closure; task criteria still mandatory.

**Subtasks**

- **T031-ST01** — Add native container dialogs, paginated root history, and plain-text composer.
- **T031-ST02** — Bind canonical server results and Sending/Checking/Failed/Sent states.
- **T031-ST03** — Keep an in-flight draft's caller/request/content immutable across retries.
- **T031-ST04** — Exercise the shared A/B ownership and uncertain-send contract scenario in the native UI.

**Acceptance criteria — all required**

- **T031-AC01** — Native channel creation produces exactly one initial session; B's new session does not change A's channel ownership.
- **T031-AC02** — Committed text survives reopening and is rendered literally, including HTML-looking input.
- **T031-AC03** — An uncertain retry produces one committed/visible message with its original author and request ID.
- **T031-AC04** — Validation failures preserve drafts and show specific errors; an edited draft receives a new request ID.
- **T031-AC05** — Network/image-independent background work does not block the dispatcher or leave obsolete selection results in the active view.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T031 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T031 -Configuration Release
```

**Observable demonstration:** Run the shared A/B container-and-text scenario in WinUI, reopen history, and reconcile a forced lost response.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No rich-text editor, edit/delete, offline queue, or UI-specific domain rules.

### T032 — Use native threads, targets, and mentions

**Outcome:** A native threaded conversation and mentions view matching the web behavior.

**Owner role:** winui implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T031. **Previous milestone gate:** M04. **Checkpoint-entry gates:** CP10. **Contributes to:** CP11.

**Source:** sections 7.3, 7.4, 13.4, 16.4; requirements R10, R11, R14, R17; supporting/related scenarios A24, A25, A26, A27, A28, A29, A39. **Primary closure ownership:** No exclusive source-scenario closure; task criteria still mandatory.

**Subtasks**

- **T032-ST01** — Add thread expansion, reply context, and bounded paginated ancestor rendering.
- **T032-ST02** — Add handle autocomplete/target chips and selected-profile mentions history.
- **T032-ST03** — Map server errors to the composer while preserving the original input.
- **T032-ST04** — Exercise the shared thread/target scenario with actual native controls.

**Acceptance criteria — all required**

- **T032-AC01** — Three total message levels render correctly and level-4 replies are unavailable/rejected.
- **T032-AC02** — A nested message reached from Mentions resolves the correct thread without creating a false root.
- **T032-AC03** — Canonical target IDs, unknown-handle errors, and reply target behavior match the shared contract scenario; replies do not inherit targets.
- **T032-AC04** — Other profiles retain visibility of targeted conversation history.
- **T032-AC05** — The native UI uses the same contracts and no extra ownership restrictions.
- **T032-AC06** — Keyboard operation and focus remain usable through target selection and reply cancellation.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T032 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T032 -Configuration Release
```

**Observable demonstration:** Send a targeted native message, open it from Mentions, page a three-level thread, and verify validation outcomes against the shared contract scenario.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No native-only targeting grammar, private-message UI, or additional thread depth.

### T033 — Send and render images in WinUI

**Outcome:** A native one-image composition, upload, and display slice that works in the unpackaged application.

**Owner role:** winui implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T031. **Previous milestone gate:** M04. **Checkpoint-entry gates:** CP10. **Contributes to:** CP11.

**Source:** sections 16.4, 17; requirements R12, R14, R19; supporting/related scenarios A30, A31, A32, A33, A39. **Primary closure ownership:** No exclusive source-scenario closure; task criteria still mandatory.

**Subtasks**

- **T033-ST01** — Use a correctly desktop-associated file picker and one-image/caption draft.
- **T033-ST02** — Send frozen multipart commands through the shared client and reconcile uncertain outcomes.
- **T033-ST03** — Fetch bytes asynchronously, present images on the dispatcher, and cancel/dispose obsolete work.
- **T033-ST04** — Exercise valid/invalid image cases with actual file selection on Windows.

**Acceptance criteria — all required**

- **T033-AC01** — The picker works in the unpackaged WinUI app and valid PNG/JPEG messages send and reload.
- **T033-AC02** — Errors from invalid/oversized files preserve the draft and show the server's reason.
- **T033-AC03** — Retry identity includes the unchanged image bytes, original caller, and request ID.
- **T033-AC04** — No image BLOB is fetched through ordinary JSON, and no UI-thread database/network blocking is introduced.
- **T033-AC05** — Image objects/streams are disposed when no longer needed, with bounded concurrent loads.
- **T033-AC06** — Native image behavior matches the shared image contract, validation corpus, and payload limits.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T033 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T033 -Configuration Release
```

**Observable demonstration:** Select a local PNG/JPEG through the native picker, send/reload it, retry an uncertain send, and observe rejected malformed inputs without losing the draft.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No extra image formats, editor, asset staging, or UI-specific persistence.

### T034 — Keep the native view live and recover safely

**Outcome:** A native live client that integrates snapshot/replay, dispatcher-safe events, profile isolation, and reconnect.

**Owner role:** winui implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T032, T033. **Previous milestone gate:** M04. **Checkpoint-entry gates:** None. **Contributes to:** CP12.

**Source:** sections 11, 13.3, 16.3, 16.4, 16.6; requirements R03, R06, R14, R19; supporting/related scenarios A20, A23, A39, A40, A41, A49. **Primary closure ownership:** No exclusive source-scenario closure; task criteria still mandatory.

**Subtasks**

- **T034-ST01** — Wire reusable stream/reconnect behavior into view models without duplicate loops or UI-thread blocking.
- **T034-ST02** — Merge response/event updates and query unloaded conversation history when selected.
- **T034-ST03** — Implement profile switching using recorded subscription owner and preserve unresolved-send identity.
- **T034-ST04** — Handle epoch changes, startup/unavailable states, navigation cancellation, and bounded loaded state.

**Acceptance criteria — all required**

- **T034-AC01** — Publishing during initial load or reconnect yields complete canonical state with no duplicate visible messages.
- **T034-AC02** — Quiet heartbeats remain healthy because liveness tracks bytes/comments, not only parsed domain events.
- **T034-AC03** — Switching profiles never changes an in-flight caller or closes another client's subscription.
- **T034-AC04** — Changing conversation cancels obsolete loads and cannot overwrite a newly selected conversation.
- **T034-AC05** — Store-epoch mismatch triggers profile revalidation and a fresh snapshot.
- **T034-AC06** — One native reconnect loop exists per active subscription and shutdown disposes it.
- **T034-AC07** — The native frontend and a second T024 sample consumer operate together with independent cursor positions.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T034 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T034 -Configuration Release
```

**Observable demonstration:** Open WinUI and the T024 native sample consumer together, alternate sends, interrupt/restart the host, change the WinUI profile around a pending command, and compare persisted/visible results.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No background host inside WinUI, shared consumer cursor, or native offline database.

## M05 mandatory full review and closure

After CP09, CP10, CP11, CP12 pass, review all implemented functionality from M01 through M05, including inherited code and behavior. Apply all ten review dimensions and MG01–MG06 in [the gate policy](../plan/02-gates-and-reviews.md).

Use [the milestone template](../templates/milestone-review.md). Record every finding/warning, implement and independently verify fixes, then run the cumulative suite and checkpoint scenarios on the final corrected candidate. No unresolved issue/warning of any severity can remain. A test-only pass does not replace full review.

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope milestone -Id M05 -Configuration Release
```

**Acceptance record must identify:** final commit, effective configuration/dependency versions, required platform observations, task/checkpoint evidence, zero-open-findings result, reviewer, and relevant artifact hashes. This milestone is currently **not run**.
