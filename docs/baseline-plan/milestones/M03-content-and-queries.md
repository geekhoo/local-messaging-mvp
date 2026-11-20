# M03 — Content and query completeness

**Status:** planned · **Gate:** not_run · **Full cumulative review:** mandatory

## Objective

Complete stable catalog/history queries, targeting, three-level threads, atomic image messaging, and content API introspection.

## Entry

M02 must have passed its full cumulative review and zero-unresolved-findings exit gate. Every task also requires its explicit dependency and checkpoint-entry gates.

## Exit

The complete content model is usable through both API surfaces with bounded query and image behavior. All checkpoints below and MG01–MG06 must pass. Resolve every issue/warning and rerun cumulative verification on the final corrected candidate.

**Scope note:** Snapshot queries are complete; snapshot-plus-replay acceptance waits for M04.

## Task map

| Task | Outcome | Task dependencies | Checkpoint-entry gate | Checkpoint |
| --- | --- | --- | --- | --- |
| T013 | Browse a stable paginated catalog | T011, T012 | None | CP05 |
| T014 | Publish targeted messages and query mentions | T013 | None | CP05 |
| T015 | Create and retrieve three-level threads | T013, T014 | None | CP05 |
| T016 | Publish and retrieve atomic image messages | T006, T014, T015 | CP05 | CP06 |
| T017 | Page message history and thread-aware projections | T013, T014, T015 | CP05 | CP06 |
| T018 | Expose and exercise the completed content API contract | T016, T017 | CP05 | CP06 |

## Integrated checkpoints

### CP05 — Stable browsing, targets, and threads

**Required task gates:** T013, T014, T015. **Prior checkpoint gates:** None within this milestone. **Status:** not_run.

**Preconditions**

- M02 accepted; A/B users and sessions; small page sizes to force boundaries.

**Integrated steps**

1. Capture H, page catalog while publishing additional objects, and compare the complete as-of result.
2. Send explicit and parsed targets, duplicates, escaped/email/unknown handles; query public and mentions history.
3. Build a root/child/grandchild, read it across pages, and attempt a fourth level and cross-session parent.

**Expected results**

- As-of pages contain all and only eligible objects at H without duplicates/gaps.
- Target rows and mentions collapse duplicates; grammar is exact; targeting adds no private visibility and replies inherit no targets.
- Depths/root references are correct; invalid reply/target paths roll back without orphan objects.

**Platform:** Windows 11 x64; real Kestrel and SQLite where implemented; actual browser/WinUI for UI assertions

**Planned verification command**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope checkpoint -Id CP05 -Configuration Release
```

**Evidence:** one integrated-candidate [checkpoint record](../templates/checkpoint-result.md), actual commands/results, fixture identity, applicable diagnostics, and closure links. Apply CG01–CG05; constituent task passes alone do not suffice.

**Unlocks:** T016, T017, T018 and CP06.

### CP06 — Complete content API

**Required task gates:** T016, T017, T018. **Prior checkpoint gates:** CP05. **Status:** not_run.

**Preconditions**

- Integrated content API; image corpus; history/thread pages small enough to expose context behavior.

**Integrated steps**

1. Create targeted/threaded PNG and JPEG messages in existing/new conversations; retrieve/hash the bytes.
2. Retry equivalent multipart input with a changed boundary/filename; reject invalid/oversized/corrupt files atomically.
3. Read root/flat/thread/history projections at H, inspect descriptors and OpenAPI, and exercise content routes/errors on both listeners.

**Expected results**

- One transaction covers image, containers, message, targets, events, and receipt; retries preserve identity.
- No BLOB/base64/file-path payload appears in JSON; image resources and response concurrency are bounded.
- Queries return appropriate ancestors/counts at H; documented DTOs/errors match real behavior; domain edit/delete remains unsupported.

**Platform:** Windows 11 x64; real Kestrel and SQLite where implemented; actual browser/WinUI for UI assertions

**Planned verification command**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope checkpoint -Id CP06 -Configuration Release
```

**Evidence:** one integrated-candidate [checkpoint record](../templates/checkpoint-result.md), actual commands/results, fixture identity, applicable diagnostics, and closure links. Apply CG01–CG05; constituent task passes alone do not suffice.

**Unlocks:** M03 full review; after acceptance, T019 and M04.

## Atomic task cards

Every card inherits TG01–TG08 from [the gate policy](../plan/02-gates-and-reviews.md). Commands are planned interfaces implemented by T001, not executed evidence in this package. The [source specification](../specs/local-messaging-design-specification-v1.md) is authoritative; section/requirement/acceptance references below are local traceability.

### T013 — Browse a stable paginated catalog

**Outcome:** Complete user/channel/session list and detail queries with snapshot-aware continuation while writes continue.

**Owner role:** queries implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T011, T012. **Previous milestone gate:** M02. **Checkpoint-entry gates:** None. **Contributes to:** CP05.

**Source:** sections 10.4, 13.2, 13.3, 14.4; requirements R04, R09, R13, R19; supporting/related scenarios A23. **Primary closure ownership:** No exclusive source-scenario closure; task criteria still mandatory.

**Subtasks**

- **T013-ST01** — Implement catalog filters, stable creation-sequence ordering, limits, and validated page tokens.
- **T013-ST02** — Capture/provide a snapshot head and apply CreatedSequence <= H across related queries.
- **T013-ST03** — Return owner IDs and suggested scope filters without a separate topic registry.
- **T013-ST04** — Add concurrent-publish pagination scenarios with small pages.

**Acceptance criteria — all required**

- **T013-AC01** — Users, channels, and sessions can be listed and retrieved through both surfaces with correct independent owner fields.
- **T013-AC02** — Paging at H neither includes later creations nor skips/duplicates eligible earlier objects.
- **T013-AC03** — Page tokens preserve the snapshot/filter/order and reject malformed or wrong-epoch data.
- **T013-AC04** — Default/max page sizes are enforced; any counts use the same H.
- **T013-AC05** — Catalog tests are supporting snapshot evidence; full snapshot-plus-replay A23 remains open until T024.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T013 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T013 -Configuration Release
```

**Observable demonstration:** Capture H, page a channel and session directory while creating more containers, and show a stable as-of result plus a fresh result containing new objects.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No full-text search, mutable count cache, membership directory, or topic-registration service.

### T014 — Publish targeted messages and query mentions

**Outcome:** Canonical @handle/explicit-user targeting committed with messages and available through the mentions query.

**Owner role:** backend implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T013. **Previous milestone gate:** M02. **Checkpoint-entry gates:** None. **Contributes to:** CP05.

**Source:** sections 7.4, 8.2, 10.2, 13, 15.3; requirements R04, R10, R12, R17; supporting/related scenarios A24, A25, A26. **Primary closure ownership:** A24, A25.

**Subtasks**

- **T014-ST01** — Implement the exact mention delimiter/maximal-token/escape grammar and case-insensitive handle resolution.
- **T014-ST02** — Union textual and explicit target IDs, deduplicate, validate users/target limit, and persist target rows atomically.
- **T014-ST03** — Add target DTO/event projection and paginated as-of mentions history.
- **T014-ST04** — Exercise valid/unknown/literal/email/short-token cases and public conversation visibility.

**Acceptance criteria — all required**

- **T014-AC01** — Known handles and explicit IDs produce one canonical target set; duplicate sources create one MessageTargets row.
- **T014-AC02** — Unknown syntactically valid handles or nonexistent explicit IDs fail the complete compound request with the specified error.
- **T014-AC03** — Escaped/email/invalid-short/overlong/leading-digit cases follow the exact frozen grammar.
- **T014-AC04** — Mentions history returns each targeted message once with correct ordering/snapshot semantics.
- **T014-AC05** — Other logical users still see the message in session history; targeting adds routing and does not imply privacy.
- **T014-AC06** — Caption targeting uses the same parser when image commands become available; no second inconsistent parser is introduced.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T014 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T014 -Configuration Release
```

**Observable demonstration:** Send known/unknown/literal mentions and explicit duplicates, query the target feed and public history, and verify failed compound sends create nothing.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No private messages, notification push service, read receipts, or automatic target inheritance.

### T015 — Create and retrieve three-level threads

**Outcome:** Threaded create/query behavior with server-computed root/depth and paginated ancestor context.

**Owner role:** backend implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T013, T014. **Previous milestone gate:** M02. **Checkpoint-entry gates:** None. **Contributes to:** CP05.

**Source:** sections 7.3, 7.4, 8.6, 13.4, 14.3; requirements R04, R10, R11, R17; supporting/related scenarios A26, A27, A28, A29. **Primary closure ownership:** A26, A27, A28, A29.

**Subtasks**

- **T015-ST01** — Implement parent validation and root/depth assignment in the existing transaction handler.
- **T015-ST02** — Enforce same-session parent/root relationships through domain rules and real SQLite constraints.
- **T015-ST03** — Implement thread-root resolution and bounded reply pages with missing ancestor context.
- **T015-ST04** — Exercise level limits, cross-session parents, and non-inherited targets.

**Acceptance criteria — all required**

- **T015-AC01** — Root/reply/nested reply produce depths 1/2/3 and correct common root IDs.
- **T015-AC02** — Replying to level 3 or a message in another session fails with zero object/event/receipt writes.
- **T015-AC03** — Clients cannot override author, depth, root, or timestamps.
- **T015-AC04** — Small thread pages return needed ancestors without inventing detached roots or duplicating context.
- **T015-AC05** — Replies have only their own explicit/textual targets and remain publicly viewable.
- **T015-AC06** — Self-root insertion and composite parent/root FKs work against real SQLite, not only mocked storage.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T015 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T015 -Configuration Release
```

**Observable demonstration:** Create a targeted root, reply, and nested reply; retrieve one-item pages; reject level 4 and cross-session parents; inspect the non-inherited target set.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No reparenting, editing, deletion, unlimited nesting, or alternate root-depth convention.

### T016 — Publish and retrieve atomic image messages

**Outcome:** A complete multipart image-message slice including bounded validation, persistence, descriptors, byte retrieval, and idempotent retry.

**Owner role:** backend implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T006, T014, T015. **Previous milestone gate:** M02. **Checkpoint-entry gates:** CP05. **Contributes to:** CP06.

**Source:** sections 7.2, 8.2, 12.2, 17; requirements R04, R10, R11, R12, R13, R19; supporting/related scenarios A10, A30, A31, A32. **Primary closure ownership:** A10, A30, A31.

**Subtasks**

- **T016-ST01** — Accept exactly the request JSON and one image part; reuse WIC validation before taking the write gate.
- **T016-ST02** — Commit image BLOB, message/containers, target rows, events, and receipt in one transaction.
- **T016-ST03** — Add content retrieval with verified MIME, nosniff, digest ETag, and bounded response concurrency.
- **T016-ST04** — Run valid/invalid image, caption mention, threaded image, byte-digest, and retry/rollback scenarios.

**Acceptance criteria — all required**

- **T016-AC01** — Valid PNG/JPEG messages work in existing/new container combinations and return bytes matching the stored digest.
- **T016-AC02** — The 8 MiB binary/9 MiB request, dimension/pixel, part-count, and decoder bounds are enforced with actionable errors.
- **T016-AC03** — Invalid image/target/parent input leaves no orphan image or partially created conversation.
- **T016-AC04** — Equivalent retries with the same bytes and canonical JSON return the original result despite filename/boundary changes; changed bytes conflict.
- **T016-AC05** — Event, receipt, and history JSON contain descriptors only; no binary/base64 or local file path leaks.
- **T016-AC06** — Image content is fetched after bounded database materialization and response concurrency never exceeds the configured limit.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T016 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T016 -Configuration Release
```

**Observable demonstration:** Send a PNG/JPEG with a caption target into a new conversation, retrieve and hash it, retry with a new multipart boundary, and reject malformed/oversized images atomically.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No separate upload staging, image edits, thumbnails, image reuse IDs, external URLs, or new formats.

### T017 — Page message history and thread-aware projections

**Outcome:** A complete bounded message-query surface for roots, flat history, single messages, threads, and mentions with consistent DTOs.

**Owner role:** queries implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T013, T014, T015. **Previous milestone gate:** M02. **Checkpoint-entry gates:** CP05. **Contributes to:** CP06.

**Source:** sections 11.2, 13.2, 13.4, 14.4; requirements R04, R10, R11, R13, R19; supporting/related scenarios A23, A32. **Primary closure ownership:** No exclusive source-scenario closure; task criteria still mandatory.

**Subtasks**

- **T017-ST01** — Finish roots/flat view behavior and stable pagination/order across all message queries.
- **T017-ST02** — Project consistent channel/session/author/target/content fields without loading image BLOBs.
- **T017-ST03** — Compute reply counts at H and include only required thread ancestor context.
- **T017-ST04** — Exercise concurrent appends, tiny page sizes, invalid tokens, and unloaded-root navigation.

**Acceptance criteria — all required**

- **T017-AC01** — Root and flat views return their specified membership/order; all detail/history DTOs agree for the same message.
- **T017-AC02** — Queries at H exclude later roots/replies and counts use H; continuation never loses or duplicates eligible messages.
- **T017-AC03** — Thread context is bounded to required ancestors and does not consume the requested item limit.
- **T017-AC04** — Image-bearing rows are projected without fetching BLOB bytes for ordinary history.
- **T017-AC05** — Query/page limits and epoch/head checks are enforced before materializing unbounded data.
- **T017-AC06** — Navigation by a message ID resolves its proper thread/session even when its root is outside the current history page.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T017 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T017 -Configuration Release
```

**Observable demonstration:** Seed roots/replies/targets, page each history mode while publishing, follow a nested-message link, and compare projections and counts at the fixed head.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No full-text search, offline cache, whole-conversation preload, or speculative query language.

### T018 — Expose and exercise the completed content API contract

**Outcome:** An accurate local OpenAPI document, status limits, and executable content-contract matrix for the implemented non-subscription surface.

**Owner role:** contracts implementer. **Reviewer:** independent reviewer. **Status:** planned / not_run.

**Task dependencies:** T016, T017. **Previous milestone gate:** M02. **Checkpoint-entry gates:** CP05. **Contributes to:** CP06.

**Source:** sections 6.2, 13, 17.2, 19, 20.1; requirements R03, R05, R12, R18, R19; supporting/related scenarios A06, A32, A44, A47. **Primary closure ownership:** A44.

**Subtasks**

- **T018-ST01** — Generate local OpenAPI from complete DTOs and actual routes; document request/response/error/multipart cases.
- **T018-ST02** — Expose effective limits and feature availability without declaring pending pub/sub implementation complete.
- **T018-ST03** — Run the same content contract matrix against M and F, including allowed methods and boundary protections.
- **T018-ST04** — Resolve schema/handler/query/status drift and confirm malformed requests have no side effects.

**Acceptance criteria — all required**

- **T018-AC01** — OpenAPI and returned objects agree on required fields, discriminators, sequence encoding, receipt operation/state, and content shapes.
- **T018-AC02** — Every specified content/query route is reachable on its correct surface and guarded before handler execution.
- **T018-AC03** — No domain PATCH/PUT/DELETE operation exists; unsupported methods receive the documented outcome.
- **T018-AC04** — Multipart and JSON requests enforce declared limits, origin/header rules, and stable Problem Details across M/F.
- **T018-AC05** — Image bytes stay out of all JSON contracts; caller-supplied server-owned fields are rejected.
- **T018-AC06** — The executable matrix fails on a missing route/schema check and leaves the current cumulative build runnable.

**Run and verify**

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope demo -Id T018 -Configuration Release
pwsh -NoProfile -File eng/verify.ps1 -Scope task -Id T018 -Configuration Release
```

**Observable demonstration:** Fetch OpenAPI/status, execute the content route/error matrix on both listeners, and compare the observed responses to schema and storage effects.

**Required evidence:** [task result](../templates/task-result.md), actual build/test/demo output, one result per criterion, affected regression results, independent review and verified finding closure. Use the real implicated Windows/HTTP/3/SQLite/browser/WinUI layer; missing platform execution blocks the gate.

**Excluded from this task**

- No public fault endpoints, auth scheme in OpenAPI, or declaration that future subscriptions already work.

## M03 mandatory full review and closure

After CP05, CP06 pass, review all implemented functionality from M01 through M03, including inherited code and behavior. Apply all ten review dimensions and MG01–MG06 in [the gate policy](../plan/02-gates-and-reviews.md).

Use [the milestone template](../templates/milestone-review.md). Record every finding/warning, implement and independently verify fixes, then run the cumulative suite and checkpoint scenarios on the final corrected candidate. No unresolved issue/warning of any severity can remain. A test-only pass does not replace full review.

```powershell
pwsh -NoProfile -File eng/verify.ps1 -Scope milestone -Id M03 -Configuration Release
```

**Acceptance record must identify:** final commit, effective configuration/dependency versions, required platform observations, task/checkpoint evidence, zero-open-findings result, reviewer, and relevant artifact hashes. This milestone is currently **not run**.
