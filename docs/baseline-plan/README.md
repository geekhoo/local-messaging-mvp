# Local Messaging MVP — implementation plan

**Version 1.0 · 20 November 2026**

This package turns the supplied design specification into an executable development plan: **6 milestones, 14 checkpoints, 40 atomic tasks, 160 subtasks, and 235 individually identified task acceptance criteria**. It also maps every original **R01–R19 requirement** and **A01–A50 acceptance scenario**.

**Status: planning complete; application work and all implementation gates are planned / not run.** This package contains specifications, plans, indexes, and evidence templates. It does not contain the application or claim that any application build, Windows execution, or acceptance test has passed.

## Start here

1. Read [the roadmap](plan/01-roadmap.md) for sequence, dependencies, and parallel lanes.
2. Read [gates and full reviews](plan/02-gates-and-reviews.md) before starting implementation.
3. Execute T001 in [M01 — foundation](milestones/M01-foundation.md), then use each milestone's complete task cards.
4. Use [verification and traceability](plan/03-verification-and-traceability.md) to connect evidence to the original requirements.
5. Follow [the execution guide](plan/04-execution-guide.md) for integration, remediation, and progression.

## What constitutes progress

A task delivers one buildable, runnable, testable increment after its prerequisites. Its subtasks together implement that increment; an interface or unfinished scaffold alone is not an accepted task.

**Task gates → integrated checkpoint → milestone full review → next milestone.** Every checkpoint combines several tasks. Every milestone requires a full review of everything implemented so far, resolution of **all issues and warnings, regardless of severity**, and successful cumulative verification on the corrected candidate. Passing task tests alone never closes a milestone.

The scripts shown in task cards are a **planned command interface that T001 must implement**. They are not files supplied by this planning package, and the listed invocations are not evidence of execution.

## Package contents

| Location | Purpose |
| --- | --- |
| [Original specification](specs/local-messaging-design-specification-v1.md) | Supplied source, preserved byte-for-byte |
| [Roadmap](plan/01-roadmap.md) | Six milestones, fourteen checkpoints, sequencing and parallel work |
| [Gates and reviews](plan/02-gates-and-reviews.md) | Task/checkpoint/milestone gates and mandatory zero-unresolved-findings closure |
| [Verification and traceability](plan/03-verification-and-traceability.md) | Platform rules, all 50 source scenarios, all 19 requirements |
| [Execution guide](plan/04-execution-guide.md) | Developer workflow, commands, evidence, integration and remediation |
| [Specification clarifications](plan/05-spec-clarifications.md) | Explicit implementation interpretations without modifying source |
| [Milestone task catalogues](milestones/M01-foundation.md) | Six files containing every task, subtask, criterion, demo and checkpoint |
| [Full machine-readable plan](data/implementation-plan.json) | Canonical task, dependency, gate, checkpoint and traceability data |
| [Task CSV](data/tasks.csv) | Importable task summary |
| [Acceptance CSV](data/acceptance-coverage.csv) | One closure owner for each A01–A50 |
| [Requirement CSV](data/requirements-coverage.csv) | R01–R19 implementation mapping |
| [Task template](templates/task-result.md) | Per-task evidence and review record |
| [Checkpoint template](templates/checkpoint-result.md) | Integrated scenario evidence |
| [Milestone review template](templates/milestone-review.md) | Full review, closure and final candidate identity |
| [Findings register](templates/findings.csv) | Empty register for issues and warnings |
| [Package validation](VALIDATION.md) | Checks performed on these documents, distinct from application checks |
| [Package manifest](PACKAGE-MANIFEST.json) | File hashes, source fingerprint and counts |

## Source authority

The [supplied specification](specs/local-messaging-design-specification-v1.md) governs product behavior. This plan refines delivery order, acceptance ownership, and execution gates. If a task contradicts source behavior, resolve the discrepancy before implementing it; do not silently weaken the source.

Original specification SHA-256: `6c69d61a7ceb93c11a180a54cf8d49001397c9ed14cb3b5f2ae746d8c6d05186`. Original size: **114,645 bytes**.

All execution records start without success claims. The complete plan can be read offline after extracting the ZIP. No external project-management service is required.
