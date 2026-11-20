# 05 — Specification clarifications for implementation

These are narrow execution interpretations of the [unchanged source specification](../specs/local-messaging-design-specification-v1.md). They resolve places where a short example or prose description could otherwise produce incompatible implementations. They do not expand MVP scope.

| ID | Source | Implementation rule | First owner |
| --- | --- | --- | --- |
| CL01 | Sections 1.1, 8 | “Create selection” is descriptive prose. The canonical selector discriminator in JSON is `mode: "new"`; `mode: "existing"` uses an ID. Unknown existing IDs never create implicitly. | T002 |
| CL02 | Sections 8.2, 12.2, 17 | Image request identity includes canonical semantic JSON and SHA-256 of verified image bytes. Multipart boundary and filename are not semantic. Verified format/length/dimensions derive from the bytes; arbitrary unverified file metadata is not a new identity input. | T002 / T016 |
| CL03 | Sections 6, 9, 11, 14 | Section 9 examples intentionally shorten DTOs. Implement full object/event contracts from the model and detailed definitions; do not treat omitted example fields as forbidden or optional by accident. | T002 |
| CL04 | Sections 9.2, 10.3, 13.5 | Request receipt state `committed` is distinct from subscription lifecycle state `active/closed`; a close response can have `state=committed` and `subscriptionState=closed`. | T019 |
| CL05 | Sections 3.1, 18.3 | Keep the documented major technology baseline; verify and pin supported stable patch/tool/package versions at implementation time. Do not infer runtime completeness merely from a successful developer-machine run. | T001 / T038 |
| CL06 | Sections 5.3, 20.2 | Early exact-H3 probe and synthetic stream evidence qualify platform feasibility only. Full A03/A04 need persisted publish and actual durable SSE. Harness probes/fault hooks are not public product routes. | T003 / T011 / T022 |
| CL07 | Sections 11.2, 15.5, 18.6 | Prepare and validate the restored DB, rotate epoch and close restored subscriptions before publication. Coordinate WAL/SHM retirement before atomic replacement. Test interruption on both sides of publication. | T036 |
| CL08 | Sections 3.1, 18.3 | Windows 11 x64 is the MVP release target. Optional Server 2025 host qualification is separate; no native WinUI Server support claim follows from host support. | T001 / T040 |
| CL09 | Sections 16.3, 16.6 | Last-used profile persistence is only a default for a newly initialized client. Each browser tab has its own active caller/subscription/cursor. Profile switching preserves every in-flight command's original caller. | T025 / T029 / T034 |
| CL10 | Sections 7.3, 16 | “Max 3 deep” means three total levels: root 1, reply 2, nested reply 3. It does not mean three replies below a root. | T015 / T027 / T032 |
| CL11 | Sections 7.4, 19 | `@user` targeting is public routing/mentions, not access control or private messaging. Ownership is attribution; every logical user has the same creation/viewing abilities. | T014 |
| CL12 | Sections 11.4, 16.6 | Stream liveness watches bytes, including comments/heartbeats, rather than domain messages only. A quiet healthy stream must not be reconnected repeatedly. | T022 / T024 / T029 / T034 |
| CL13 | Sections 17.3, 18 | The host's image decoder uses narrow Win32 WIC with explicit PNG/JPEG codecs and no package identity/WinUI dependency. Test the unpackaged headless context. | T006 / T016 |
| CL14 | User's milestone rule; source section 20 | All genuine issues/warnings require verified closure. Deliberately asserted negative-test diagnostics are separate expected observations; no warning baseline or accepted-risk exception closes a real issue. | Every task and milestone |
| CL15 | Sections 4.3, 20.3 | Verification wrappers and small scenario executables are implementation deliverables introduced by T001 and expanded by later tasks. Their documented commands in this planning package have not been executed. | T001 / T039 / T040 |
| CL16 | Sections 16.4, 18 | Early native UI tasks use an explicitly started host. Known-path host launch, optional startup registration, and packaged readiness UX are completed in T035, avoiding circular frontend/package dependencies. | T030 / T035 |

If implementation evidence reveals a conflict that these clarifications do not settle, record it against the relevant task and source section. Preserve the original file, resolve the behavior explicitly, and update plan/coverage together.
