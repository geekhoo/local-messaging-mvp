# Native components: independent review and validation

## Scope and outcome

The reusable .NET client, WinUI source, headless WIC component, native model tests, and runnable console sample are implemented. The independent reviewer was the separate `/root/implement_web` agent; implementation and fixes were owned by `/root/implement_native`. The reviewer reread the corrected source and the targeted supporting tests and reported no further concrete native source defect in the final pass.

**This closes the recorded source-review findings only. It does not pass a Windows milestone, checkpoint, task gate, actual WinUI control scenario, WIC runtime scenario, or clean-machine release gate.** Those require execution on supported Windows 11 x64. The environment used here was Debian Linux with the pinned .NET 10 SDK.

The final source inventory and SHA-256 digests are recorded in [native-components/results.json](native-components/results.json). Build-generated directories are excluded from that inventory. Product self-contained settings have not been changed to disguise the missing Windows toolchain execution.

## Observed verification

| Check | Observed result | Evidence |
| --- | --- | --- |
| Reusable client parser/HTTP/recovery regression suite | 18 passed, 0 failed | [client-tests.log](native-components/client-tests.log) |
| Actual native view-model/projection source in a portable HTTP fixture | 8 passed, 0 failed | [native-model-tests.log](native-components/native-model-tests.log) |
| Runnable console sample against actual HostRuntime/Kestrel/EF/SQLite in explicit F mode | 9 passed, including creation, ownership, frozen retry, multipart persistence, snapshot/replay, independent closure and canonical history | [console-sample-runtime.json](native-components/console-sample-runtime.json), [transcript](native-components/console-sample-transcript.json), [watch output](native-components/console-watch.jsonl) |
| Console sample build | Succeeded; 0 warnings, 0 errors | [console-client-build.log](native-components/console-client-build.log) |
| WIC component/corpus build | Succeeded; 0 warnings, 0 errors | [wic-build.log](native-components/wic-build.log) |
| Actual WinUI C# source compiled against its Windows references | Succeeded; 0 warnings, 0 errors; diagnostic managed-compile target only | [native-managed-compile.log](native-components/native-managed-compile.log) |
| Normal self-contained WinUI build attempted on Linux | Blocked: Microsoft `mt.exe` cannot execute on Linux; command failed with exit 1 / tool exit 126 | [native-full-build-attempt.log](native-components/native-full-build-attempt.log) |
| Actual WIC corpus execution on Linux | Explicit blocked result, exit 2; no substitute decoder used | [wic-runtime.log](native-components/wic-runtime.log) |

The console runtime used an explicitly identified portable host fixture. Its image validator accepts exact known fixture bytes; it is not WIC qualification. The portable host intentionally does not publish a production HTTP/3-ready endpoint descriptor. The console test supplied a test-only descriptor built from the observed F status identity/epoch, with no usable M certificate pin. No HTTP/3 readiness or Windows acceptance is inferred from those nine checks.

The native model suite source-links the actual `ShellViewModel`, `ThreadProjection`, and `NativeOptions`; it does not instantiate or simulate WinUI controls. The actual Windows follow-up is specified in [LocalMessaging.WinUI.Manual.md](../tests/LocalMessaging.WinUI.Manual.md).

## Findings and verified source closure

| ID | Concrete finding | Correction | Verification level |
| --- | --- | --- | --- |
| N01 | Concurrent profile/reconnect reloads could overwrite the tracked consumer and leak an earlier subscription. | A single asynchronous transition gate owns profile/catalog/navigation/subscription mutation. Old subscriptions close using their recorded owner; stream recovery does not await itself. | Native-model fixture gates the first subscription response, overlaps A/B/C and reconnect, then verifies one remaining owner and every original closure. |
| N02 | Composer controls could mutate the original draft during an in-flight send and have their changes erased by completion. | Composer mutation controls and callbacks are guarded while sending; successful clearing also checks original draft revision and frozen-command identity. | Independent source review; actual controls retained as a Windows regression scenario. |
| N03 | Duplicate, older, malformed or cross-epoch SSE IDs could be applied or rewind a cursor. | Consumer validates epoch-qualified Int64 cursor, body/envelope agreement, event type, sequence and checkpoint cursor; skips older/duplicate positions; awaits application before advancement. | Client tests cover duplication, out-of-order delivery, mismatched envelopes/checkpoints, cross-epoch resync and apply-before-advance. |
| N04 | A reply arriving during the initial thread query could disappear after the older query response. | Root identity is established before awaiting; a bounded projection buffers live events during page loads and merges them afterward. Missing ancestors never become false roots. | Native-model race and bounded-buffer tests pass. |
| N05 | User/channel/session creation lost retry identity after an unconfirmed response; reopening could create a second object. | Creation forms retain frozen request, original caller/container, and field values. Retrying an uncertain command uses the same envelope. Definitive validation allows editing with a new request. | Independent source review; actual form interactions retained as Windows regression scenarios. |
| N06 | The nominal 16-draft bound could grow when every saved draft had an uncertain command. | A strict admission policy retains existing pending drafts and disables new composition when no slot is available. | Independent source review and explicit 17-session Windows regression scenario. |
| N07 | Any 2xx JSON object was treated as a committed receipt. | Client requires supported contract, committed state, matching request ID and a valid store epoch before success. | Client fixture rejects uncommitted and unrelated successful HTTP responses without losing retry identity. |
| N08 | Preview decoding bypassed cancellation/concurrency bounds, and file size could change after a pre-read check. | Preview work shares the bounded image gate and cancels obsolete operations; upload bytes are copied through a limit enforced during every read. | Client file-bound/ownership test passes; preview/control execution remains a Windows gate. |
| N09 | Duplicate-handle rejection left the creation form permanently read-only. | `handle_already_exists` and definitive reference/validation failures clear only the pending command while preserving editable form fields. | Independent source review; duplicate-handle correction is in the Windows checklist. |
| N10 | Serialized navigation waited behind obsolete history instead of canceling it promptly. | Navigation cancels the current view token before waiting for transition ownership and skips superseded queued requests. Deliberate cancellation does not show an API failure. | A 30-second held HTTP history request is canceled and the new selection completes within the fixture's 3-second bound. |
| N11 | Canceling selected history during snapshot bootstrap could abort before a new stream was installed. | Bootstrap tolerates navigation-only cancellation of the selected view but still creates replay after the captured catalog H before releasing ownership. | Held-bootstrap/navigation fixture verifies the new view and exactly one owned subscription. |
| N12 | Model identity could change while the previous draft remained enabled, silently assigning a new send to another user/session. | Model exposes transition state and a user/session/epoch composer scope. UI disables composition during transitions and rechecks exact scope synchronously before freezing a command. Reply navigation rechecks caller and session after awaits. | Held-profile/history fixtures verify old and transitional scopes cannot compose, and only the final matching scope is admitted. |
| N13 | Default JSON escaping could expand legal maximum-length non-ASCII text beyond the 64 KiB request budget. | JSON request serialization preserves UTF-8 in an application/json body; it is never embedded as HTML. | Actual HTTP client test sends 16,384 Chinese characters within 64 KiB with unchanged content. |
| N14 | Host's ID-less temporary storage controls could be mistaken for malformed durable events. | Temporary `database_busy`, `storage_unavailable`, and `host_not_ready` controls trigger bounded reconnect without advancing a cursor. | Client fixture interrupts with a temporary control, reconnects, and applies the next durable event once. |
| N15 | Selection beyond the first 50 sessions could lose its label or disappear on reload. | Missing selected DTOs are queried explicitly and merged into the bounded navigation window. | Native-model fixture selects session 51 and preserves it across a snapshot reload. |
| N16 | Obsolete image-card loads could remain queued after a full view rerender. | Each rendered history/thread view cancels its previous image requests; cache and concurrent-load counts remain bounded. | Independent source review; rapid image navigation is included in actual Windows qualification. |

All listed corrections were included in the reviewed source. “Source closed” does not replace the pending UI/runtime observations identified in the last column.

## Independent persistence review

The native implementer additionally read the application normalizer, transaction/receipt path, event scan, snapshot/thread queries, relational constraints, and backup/restore code owned by the separate store implementer. No high-impact defect was identified in that read. The examined invariants included:

- Domain objects, targets, event snapshots, coupled subscription and request receipt commit in one transaction.
- An event scan advances only through examined positions and stops before an excluded matching event when result/byte budgets are reached.
- Immutable domain queries and reply counts use a captured H; thread pages include required ancestor context.
- Parent/session/depth checks and relational keys prevent cross-session or fourth-level replies.
- Restore prepares the new epoch and closes retained subscriptions before publishing the replacement database; coherent safety backup and WAL checkpoint handling precede publication.

This was a source review, not additional Windows filesystem, crash, SQLite durability or release qualification.

## Remaining platform gates

Run the full default self-contained build, actual WIC corpus, real WinUI controls/picker/dispatcher scenario, default exact-M console flow, pin renewal/proxy/restart checks, simultaneous web/native/sample consumers, and clean-machine package verification on the supported Windows target. Record the actual distributed artifact hashes and resolve every observed issue/warning before accepting the affected task, checkpoint or milestone.
