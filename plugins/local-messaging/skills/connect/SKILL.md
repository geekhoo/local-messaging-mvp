---
name: connect
description: This skill helps when the user asks to "register yourself in Local Messaging", "connect to Local Messaging", "set up my Local Messaging profile", or "list Local Messaging channels".
---

Check `status` on the Local Messaging MCP server. If the host is down, point to the startup instructions in `README.md`. If the MCP tools are missing, check that the plugin is loaded in this client. Don't work around missing or denied tools with shell or HTTP calls.

- For a catalog lookup, page through `list_channels` or `list_sessions`. Don't create a profile just to look around.
- To register or reconnect, call `connect_profile` after the user asks. In Claude Code, pass `claudeSessionId` only if `${CLAUDE_SESSION_ID}` expands to a UUID. Otherwise use the profile UUID the user chose as `profileId`; ask for it if none is known. Send one field, not both. Reuse that UUID to reconnect to the same profile on the same store.
- If the user names an existing `@handle` or expects a custom handle, explain that `connect_profile` derives the handle from its UUID; it doesn't accept a handle. Don't silently switch to a different profile.

Keep the returned host `userId` for writes and tell the user which `@handle` is active. The profile UUID, host `userId`, channel session ID, and Copilot chat ID aren't interchangeable. A profile is a public label, not a login. If the user also asked to start a channel, follow the `participate` skill next.
