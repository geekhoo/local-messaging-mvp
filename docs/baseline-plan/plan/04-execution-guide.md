# 04 — Execution guide

## First implementation action

Execute [T001](../milestones/M01-foundation.md): qualify and pin the Windows build environment, create the solution and verification entry point, then build/run the real host and WinUI baseline. Do not start with a large batch of disconnected entity/repository/UI scaffolds.

The source's project layout is the starting point. Keep Contracts, Domain, Application, Infrastructure, Host, reusable Client, WinUI and framework-free Web responsibilities distinct. Add focused test projects and a small scenario/verification executable only as needed to make the increments runnable. Both frontends call the same application behavior through F; neither opens SQLite.

## Working a task

1. Read its complete card, source sections, prerequisites, and relevant acceptance coverage.
2. Confirm the previous milestone and checkpoint-entry gates; choose an isolated working branch/directory if multiple developers or agents are active.
3. Define the concrete demonstration fixtures and result before changing code.
4. Implement subtasks together as one runnable increment. Keep unsupported future capability explicit.
5. Run the task/demo commands and affected regressions on the required environment.
6. Record actual evidence, review the code/contracts/error paths, and resolve every finding or unexpected warning.
7. Integrate with accepted prerequisites, rerun impacted checks, and accept TG01–TG08.
8. When all required tasks are accepted, execute the checkpoint on one integrated candidate.

A task may require multiple commits. “Atomic” describes an independently verifiable delivery increment after its prerequisites, not a mandatory single commit or isolated program. A verification-tool task qualifies only if it delivers an executable reusable harness and corrected behavior; a generic “review everything” checklist is a gate rather than a task.

## Shared implementation rules

- Put receipt hashing, global request-ID conflict behavior, EventLog, and transaction boundaries into the first write.
- Route M and F to shared command/query handlers while enforcing the listener boundary before route/static dispatch.
- Use real SQLite constraints and short context lifetimes; no network waits while a DB reader/transaction remains open.
- Implement durable finite replay first; live SSE repeatedly consumes that same bounded source.
- Freeze caller/request/payload at send creation, including image bytes. A profile change cannot relabel a retry.
- Keep synthetic transport probes, deliberate crash hooks and qualification seeders outside public production routes.
- Implement simple client workflows and bounded state; preserve no-auth/public data and create-only scope.
- Use supported versions established by current implementation-time checks; record exact pins rather than silently floating dependencies.

## Integration and independent work

Integrate small accepted increments in dependency order. Coordinate changes to shared DTOs, command handlers, migrations and event filters. Each lane may use its own branch, but checkpoint evidence comes from their combined revision.

Web and native feature work may proceed in parallel after M04. The browser and native text checkpoints can pass independently. Content parity then combines both thread/target and image slices; live parity combines both recovery slices. Early WinUI tasks assume the host is already running, avoiding a dependency on later packaging/launchers.

Do not automatically rerun every historical test after every tiny change. Select meaningful affected regressions at task/checkpoint level. Every milestone still requires the full cumulative implemented-scope suite on its final corrected candidate, plus complete review.

## Remediation tasks

Give fixes stable IDs such as `FIX-M04-001`, linked to the finding and original task/checkpoint. A fix card needs a bounded outcome, reproduction, build/run/test steps, acceptance criteria, dependencies and independent verification. A fix may not remove a source requirement, hide a warning or disable a required check.

If a planned task proves too large, preserve its original traceability and split into runnable child tasks (for example T016A/T016B). Update the JSON/CSV/task catalogue and checkpoint dependencies together; retain the original task as a grouping record until every child gate passes. Do not count such future changes in the present 40-task baseline.

## Evidence layout

A lightweight implementation repository can use:

```text
evidence/<candidate>/T007/task-result.md
evidence/<candidate>/T007/build.log
evidence/<candidate>/T007/test-results.json
evidence/<candidate>/CP03/checkpoint-result.md
evidence/<candidate>/M02/milestone-review.md
evidence/<candidate>/findings.csv
evidence/<candidate>/candidate.json
```

The layout is illustrative; T001 implements consistent actual paths. Capture test logs/results and useful UI observations, not secrets, private keys or unnecessary message/image payloads. Use fixture data for screenshots. A text summary is sufficient where an image adds no evidence.

For every record include candidate commit, configuration/effective settings, dependency lock/pin identity, Windows build/runtime, actual command, exit status, expected/observed result, reviewer, finding links, and relevant package hashes. Use the provided templates without pre-filling success.

## Command semantics

The planned `eng/verify.ps1` scopes are task, demo, checkpoint, milestone, and acceptance. Every command shown in task cards is a requirement to implement the corresponding runner. A command must fail clearly if its check is unimplemented. A demo is not a substitute for automated assertions; automated tests are not a substitute for observing required actual UI/platform behavior.

The task card's wrapper can orchestrate multiple checks. It must publish their individual outcomes and not collapse a required blocked/skipped check into success. The independent review decision is recorded separately from executable results.

## Managing change and uncertainty

The [original specification](../specs/local-messaging-design-specification-v1.md) remains unchanged. [Clarifications](05-spec-clarifications.md) settle narrow implementation details without changing product behavior. Record additional implementation decisions with their source and affected tasks. A material product/scope contradiction requires explicit resolution before proceeding; routine engineering choices within the source do not require a new approval round.

Do not represent unqualified platforms as supported. Windows 11 x64 is the release gate. Any secondary Windows Server host experiment is separate and does not establish WinUI Server support. A machine lacking required capabilities blocks that gate.

## Milestone handoff

After a milestone passes, publish its review record, exact candidate identity, checkpoint/task evidence, zero-open-findings result, and next ready task IDs. Keep the runnable candidate available for the next stage. M06 additionally hands over the self-contained product, run/stop/backup/restore instructions, package manifest, effective defaults, and complete A01–A50 evidence.

The planning package's [validation report](../VALIDATION.md) verifies document integrity only. It must never be copied into an application milestone as passing implementation evidence.
