# Actual WinUI and paired-frontend qualification

Use a real Windows desktop and record your results against the exact package you test. This checklist is not a test result. See [Windows qualification](../docs/windows-qualification.md) for evidence requirements.

Use the candidate's normal self-contained publication, with the host in `product/host` and WinUI in `product/winui`. Record the exact source revision, dependency lock hashes, Windows build, candidate file hashes, commands and observed results in the task/checkpoint/milestone evidence templates.

## Isolated startup

1. Create a new absolute qualification directory outside the installation folder. Start the host with `--state-dir <directory>`, and distinct test ports if the default local host is already running. Wait for its actual exact-HTTP/3 readiness result.
2. Launch `LocalMessaging.WinUI.exe --endpoint <directory>/endpoint.json --no-launch-host`. Confirm the native UI connects to that isolated host, without touching another local store.
3. Repeat from a stopped isolated host using `--endpoint <directory>/endpoint.json` and the default test configuration. Confirm only the known sibling host executable is launched and receives the isolated state directory.
4. Start a second native instance. Verify it gets its own subscription. Closing one frontend must not close the other's subscription or stop the host.

The native settings file is stored next to the selected descriptor. An explicit endpoint that is absent/unready never falls back to the user's default store.

## Native feature scenario

| Step | Expected observation | Related source scenarios |
| --- | --- | --- |
| Create users Alice and Bryan, then switch profiles | No authentication. Immutable normalized handle and independent active selection | A08, A43 |
| As Alice, create a channel and initial session | Exactly one session; channel and session owner labels both Alice | A09 |
| As Bryan, create another session in Alice's channel | Session owner Bryan; channel owner remains Alice | A11 |
| Send plain text, emoji and HTML-looking content | Literal native text; preserved Unicode and canonical author/time | A13, A33 |
| Select a user using target autocomplete; also type the same @handle | One canonical target; message remains public; Mentions navigates to its thread | A24–A26 |
| Build root/reply/nested reply | Correct levels 1/2/3, nested ancestor layout, Reply disabled at 3 | A27–A29 |
| Attach PNG and JPEG using the associated desktop picker | Visible bounded preview, optional caption, multipart send, actual content reload | A30 |
| Try corrupt, oversized and animated fixtures | Actionable server error and retained draft; no partial conversation/image writes | A10, A31 |
| Load multiple history and thread pages | Stable ordering, bounded loaded window, required ancestors; no detached nested roots | A23, A39 |
| Retry an uncertain image/text send | Frozen request ID, original caller and identical image bytes; one committed object | A15, A40 |
| Switch user during an unresolved send | The pending command retains its original caller; UI never relabels the author | A40 |
| Publish while native snapshot is loading | Complete canonical state after replay; no visible duplicate | A23 |
| Hold a quiet subscription through several heartbeat periods | No false reconnect; bytes/comments maintain liveness | A41 |
| Disconnect/restart host | Visible bounded reconnect; current independent cursor resumes correctly | A35, A49 |
| Perform an official offline restore, then reconnect | New epoch resets scoped caches/drafts; profile IDs revalidated; fresh history loaded | A37, A49 |

## Public profile actions

- Acknowledge a message as Alice, then switch to Bryan and open its acknowledgement list. Bryan should see Alice's profile and timestamp. Reading alone must not add Bryan to the list. Retry Alice's acknowledgement after a lost response and verify there is still just one entry for her.
- Join a session and check the session and channel rosters in both frontends. Join the channel directly, then leave the session: the direct channel join remains. Close a delivery subscription and confirm that neither roster changes.

## Review regression scenarios

In addition to the portable model checks, repeat these interactions with actual native controls:

- Delay profile/history responses, rapidly select A/B/C and multiple sessions, then click Send or target/reply/image controls. Composer actions remain disabled during transitions, old drafts remain under their original scope, and only one owned stream remains.
- Delay a snapshot history request during Reconnect/Refresh, then choose another session. The obsolete request cancels promptly and the resulting view still has a live subscription.
- Delay the first thread response, publish a reply through the other frontend, then release the response. The reply appears under its real ancestor without a second refresh.
- Lose responses to user/channel/session creation. Reopen the same creation form and retry the original request/owner. Verify exactly one committed object and retained form values. A duplicate handle error must allow correcting the saved form.
- Hold a message send in flight and try attaching/removing an image, editing targets, clearing a reply, or switching profiles. No mutation is silently erased and the sent command retains its frozen scope.
- Visit at least 17 sessions with unconfirmed drafts. The 16-entry saved-draft bound is enforced; existing pending commands are retained and composition in a new session is unavailable until one is resolved.
- Switch image-bearing drafts rapidly. Obsolete previews cancel and share the bounded image-load gate; no expanding backlog accumulates. A changing upload file never reads beyond 8 MiB.
- Select a session beyond the first 50 catalog rows, reconnect, and verify the same session remains selected.

## Paired frontend checkpoints

At CP11, execute the same target/thread/image/error scenario in the real browser and native UI against the same host, then compare persisted IDs and ownership/routing results.

At CP12, leave both real frontends open. Alternate messages from each, including replies in an open thread and targeted image captions. Verify independent subscriptions/cursors, no duplicate visible messages, literal rendering, cancellation of obsolete navigation loads, and stable caller identity through an interrupted send.

The open native thread applies new replies when their ancestor context is loaded. When a live reply needs an unloaded ancestor, it displays an activity notice and the thread's Refresh action queries authoritative context. A reply never becomes a false root.

## Release checks

On a clean Windows machine without a development SDK or Node, copy the complete candidate product and repeat the startup and paired scenario. Run the real WIC corpus, native exact-H3 publish/subscription suite, backup/restore and shutdown checks. Close both frontends and verify native pub/sub still works.

Record any picker, dispatcher, runtime-library, resource-loading, focus/keyboard, certificate, package or rendering issue as a finding. Resolve and reverify every issue/warning before closing a milestone. Do not convert this unexecuted checklist into a passing record.
