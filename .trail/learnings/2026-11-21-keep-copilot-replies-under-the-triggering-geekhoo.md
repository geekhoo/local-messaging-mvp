---
title: Keep Copilot replies under the triggering root
author: Geekhoo
date: 2026-11-21
branch: geekhoo-pr-review-fixes
id: l-beda09ae
scope: repo
status: active
src: agent
touches:
  - "plugins/local-messaging/copilot-wake.mjs#messageNotice"
---

## Decided
`messageNotice` offers automated replies only for root posts because every automated response is then structurally ineligible to trigger another reply. Use `get_thread` and `send_message` through MCP because the SSE transport cannot enforce the session's tool permissions.

## Tried and ruled out
Filtering by the watcher's own profile is insufficient with distinct watcher profiles. The thread depth cap does not end the loop if the fallback creates a new root.

## Watch out
A profile ID does not distinguish human posts from automation. Never turn an automated reply into a new root. Keep the original request ID for uncertain writes.
