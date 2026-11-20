# 02 — Gates, checkpoints, and full milestone reviews

## Acceptance model

A **task** is a coherent increment that builds, runs, and can be tested after its accepted prerequisites. It fulfills its individually identified acceptance criteria and has a working demonstration. Its implementation can be broken into subtasks, but subtasks do not advance a checkpoint independently.

A **checkpoint** combines multiple accepted tasks and proves their behavior together on one integrated candidate. A **milestone** combines checkpoints and closes only after a full cumulative review, verified remediation of every issue/warning, and final corrected-candidate verification.

These are engineering gates, not requests for routine user permission. Implementers and reviewers may be people or agents. Use a reviewer independent of the change author; a second agent is sufficient. User input is needed only if an actual unresolved product decision or separately unauthorized external action requires it.

## Task gate — TG01–TG08

| ID | Check | Pass criterion |
| --- | --- | --- |
| TG01 | Ready | All dependency tasks, specified checkpoint-entry gates, and previous milestone exit pass; required environment and bounded scope are available. |
| TG02 | Build | Clean Release restore/build succeeds for every introduced or affected project with zero compiler, analyzer, restore, and package warnings. |
| TG03 | Run | The task demonstration executes the real increment and produces the observable result on its required platform. |
| TG04 | Acceptance | Every Txxx-ACxx criterion has recorded passing evidence; no unimplemented, skipped, mocked-platform, or missing-runner result counts as pass. |
| TG05 | Regression | The affected regression set passes, including real SQLite/transport/UI behavior whenever those layers are changed. |
| TG06 | Review and closure | An independent reviewer examines the increment and affected contracts/error paths; all introduced or exposed findings and unexpected warnings are verified closed. |
| TG07 | Evidence | Actual commands, results, environment, commit/configuration/dependency identity, and evidence paths are recorded without fabricated success. |
| TG08 | Integration | The task is integrated with accepted prerequisites, rebuilt/rechecked as needed, and leaves a runnable candidate. An isolated branch result alone cannot advance a checkpoint. |

All task cards inherit these gates. A task pass means the whole increment is accepted, including its test/scenario code and necessary docs; it is not a percentage of subtasks completed.

## Checkpoint gate — CG01–CG05

| ID | Check | Pass criterion |
| --- | --- | --- |
| CG01 | Prerequisites | All required tasks, prior checkpoint gates, and prior milestone exit are accepted. |
| CG02 | One candidate | The task changes are integrated on one identified commit/configuration; applicable evidence remains valid after integration. |
| CG03 | Integrated scenario | The checkpoint steps and every expected result pass against the integrated candidate on the specified environment. |
| CG04 | Clean result | All checkpoint findings and unexpected diagnostics are resolved and independently verified; affected task gates are refreshed. |
| CG05 | Evidence and progression | The checkpoint record links current evidence and lists exactly the next work unlocked. Missing evidence blocks progression. |

Record the exact checkpoint preconditions, steps, actual observations, and expected results. If integration changes behavior or exposes a defect, create a finding, fix it, and refresh affected task/checkpoint evidence before passing.

## Milestone gate — MG01–MG06

| ID | Check | Pass criterion |
| --- | --- | --- |
| MG01 | Checkpoint completeness | Every checkpoint assigned to the milestone passes on the integrated candidate. |
| MG02 | Full cumulative review | Review the entire implemented product, including inherited functionality, against the ten review dimensions below. |
| MG03 | Resolve every issue and warning | Every finding of every severity has a verified closure; unresolved compiler/analyzer/restore/package/runtime/browser warnings are zero. |
| MG04 | Corrected-candidate verification | After remediation, run the complete cumulative verification suite for implemented functionality and all relevant checkpoint scenarios on the final corrected candidate. |
| MG05 | Independent final review | A reviewer checks final changes, closure evidence, remaining-risk claims, source alignment, and the exact candidate identity; test execution alone is insufficient. |
| MG06 | Exit record | Record final commit, effective configuration, dependency versions, artifacts/hashes, coverage and review evidence. Only then mark the milestone accepted and unlock the next one. |

**Every milestone requires a full review and resolution of all issues and warnings.** This applies equally to M01 and the final release. Severity does not create an exemption. An issue inherited from an earlier milestone, newly exposed by the current one, must also be resolved before exit.

### Full cumulative review scope

| ID | Dimension | Review scope |
| --- | --- | --- |
| RV01 | Requirements and scope | Verify all implemented requirements and current milestone outcomes; account explicitly for future planned features without representing them as implemented. |
| RV02 | Contracts | Check DTOs, discriminators, errors, receipts, subscription results, cursor semantics, and actual OpenAPI together. |
| RV03 | Domain invariants | Check ownership, compound atomic creation, canonical targets/public visibility, immutable content, and three total thread levels. |
| RV04 | Persistence | Review real SQLite constraints/migrations, write transactions, idempotency, context lifetimes, crash recovery, and restore preparation where implemented. |
| RV05 | Transport | Review actual HTTP/3/TLS readiness, loopback listeners, authority/surface separation, direct proxy bypass, SSE framing/cancellation, and certificate handling. |
| RV06 | Concurrency and resource bounds | Review commit/wake and close/attach races, slow consumers, independent cursors, bounded scans/writes/decodes, and cleanup. |
| RV07 | Implemented frontends | Review workflows, safe rendering, keyboard/focus/labels, native dispatcher usage, parity, uncertain sends, profile/tab state, reconnect, and bounded caches. |
| RV08 | Local trust boundary | Check loopback/Origin/header checks, image validation, safe text, local certificate handling, and the honest no-auth/public-data model. |
| RV09 | Operations | Review currently implemented startup/stop, status/logging, migrations, configuration, packaging, upgrade preservation, backup/restore, and run instructions. |
| RV10 | Quality and maintenance | Inspect warnings, tests, dependencies, dead code, harness-only faults/probes, evidence freshness, and drift between docs/contracts/behavior. |

At M01 the review covers all implemented platform, contract, transport, storage and image-validation work. At M02–M04 it also covers all domain/query/subscription/client code introduced to date. At M05 it includes both actual frontends. At M06 it covers the complete packaged product and operations. A future planned feature is recorded as outside the current implemented scope, not marked passed; a defect in already implemented behavior cannot be renamed future work.

## Findings and warning resolution

Use [the findings register](../templates/findings.csv). Each entry records source gate, severity/type, reproduction or diagnostic, owner, affected requirements, fix, verification, reviewer, and closure candidate.

| State | Meaning | Counts as closed? |
| --- | --- | --- |
| open | Confirmed or pending triage | No |
| in_progress | Fix under implementation | No |
| ready_for_verification | Fix proposed, not independently verified | No |
| closed_fixed | Root cause corrected and verified on identified candidate | Yes |
| closed_not_an_issue | Evidence demonstrates false/inapplicable report; independent reviewer agrees | Yes |
| closed_duplicate | Links a canonical finding that is itself verified closed | Yes, only with closed canonical finding |

“Accepted risk,” “deferred,” lowering severity, moving a real warning into a baseline, broad suppression, disabling a failing test, lowering limits to hide failure, and skipping a required platform do **not** resolve an issue. An upstream dependency/tool warning that cannot be fixed still blocks the milestone; record the concrete blocker.

Compiler, analyzer, restore, and package warnings must be zero. Normal runtime and browser operation must have zero unexpected warnings. Deliberate negative tests may produce expected rejection/failure diagnostics only when the test explicitly asserts the expected condition and normal-operation evidence is clean. These are expected test observations, not a mechanism to reclassify ordinary warnings.

Fix the underlying cause of a diagnostic. A proven false report may be closed with evidence and independent review; any narrowly scoped diagnostic configuration must be justified by that demonstrated false report, never used to conceal an unresolved warning.

## State transitions

Tasks begin `planned` with `gate_status: not_run`. They move through ready, in_progress, in_review, and passed; failures return to remediation and unavailable prerequisites become blocked. Gates use not_run, running, passed, failed, blocked, or stale. Checkpoint and milestone acceptance is explicit, separate from task completion.

After a relevant change, mark affected prior evidence stale. Preserve historical evidence, but do not count it as current. Changes to shared contracts, storage, serialization, event matching, identity, or transport normally affect multiple tasks and checkpoint scenarios.

## Review and remediation loop

1. Integrate the candidate and run the checkpoint gates.
2. Perform the full cumulative review and record every issue/warning.
3. Implement bounded remediation increments, each with a runnable reproduction and verification.
4. Have an independent reviewer verify fixes and close findings.
5. Run the cumulative suite and checkpoint scenarios on the final corrected candidate.
6. Recheck the final changes and closure evidence; record exact commit/configuration/artifacts.
7. Accept the milestone only when all gate predicates hold. Otherwise leave it blocked or in review.

An isolated task branch may pass focused checks, but only integrated evidence advances checkpoints. A code fix after final review invalidates the affected review/evidence and requires re-verification. No gate can be auto-approved solely because a script exited zero.

## Minimal records

Use one [task result](../templates/task-result.md), one [checkpoint result](../templates/checkpoint-result.md), and one [milestone review](../templates/milestone-review.md) per candidate, plus the findings register. Plain files are enough; no workflow service or new governance application is part of the MVP.
