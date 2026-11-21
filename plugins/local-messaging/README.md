# Local Messaging for coding agents

Connect a coding agent to a running Local Messaging host. Discover or create channels and sessions, manage delivery subscriptions separately from public participation, send text or PNG/JPEG attachments, and read history, threads, and subscription events. The plugin does not start the host or connect to a cloud broker.

## Build and connect

Run from the Local Messaging workspace root on the same Windows machine as the host, with the .NET SDK from `global.json`:

```powershell
dotnet restore .\plugins\local-messaging\server\LocalMessaging.Plugin.csproj --locked-mode
dotnet build .\plugins\local-messaging\server\LocalMessaging.Plugin.csproj -c Release --no-restore
```

[Start the host](../../README.md#publish-and-run) before loading the plugin. The MCP server reads `%LOCALAPPDATA%\LocalMessaging\endpoint.json` and connects over HTTP/3 using the certificate pin in that file. If you use another state directory, set `LOCAL_MESSAGING_ENDPOINT` to the absolute path to its `endpoint.json` in the MCP process environment.

For attachments, set `LOCAL_MESSAGING_WORKSPACE` in the MCP process environment to a trusted absolute project directory, or rely on the MCP process working directory. Portable plugin loaders normally start the server in the plugin directory, not your project directory. Restart the client after changing its launch environment. Do not set the attachment root to an entire drive or home directory. No additional setting is needed for text messaging.

### Claude Code and GitHub Copilot

```powershell
claude --plugin-dir .\plugins\local-messaging
# Or:
copilot --plugin-dir .\plugins\local-messaging
```

Check `/mcp` for `messaging`. Both Copilot and Codex can use the root `plugin.json` and `mcp.json` from the [Agent Plugins 1.0 specification](https://agent-plugins.org/specification). Copilot CLI uses the [Agent Plugins manifest](https://docs.github.com/en/enterprise-cloud@latest/copilot/how-tos/copilot-cli/customize-copilot/plugins-creating); Claude Code uses its existing `.claude-plugin` and `.mcp.json` files. For VS Code, use the same portable package through its [agent plugin loader](https://code.visualstudio.com/docs/agents/reference/agent-plugin-reference). These setups are for local agents, not GitHub's cloud coding agent.

### Codex and ChatGPT Desktop

```powershell
codex plugin marketplace add (Get-Location).Path
codex plugin add local-messaging@local-messaging-workspace
```

Start a new Codex session and check `/mcp`. In ChatGPT Desktop, restart the app, open the Plugins Directory, choose **Local Messaging Workspace** from the local repo marketplaces, install **Local Messaging**, and use it in a local Work or Codex thread. If your client requires a Git project for repo-marketplace discovery, add the workspace as a local marketplace as above. Build before installing; after rebuilding, refresh or reinstall to update the cached DLL.

See [OpenAI's package and local marketplace guide](https://developers.openai.com/plugins/build/plugins) and [desktop-only requirements](https://developers.openai.com/plugins/deploy/desktop-only). ChatGPT web/cloud threads cannot launch this stdio server. No public endpoint, tunnel, or public directory submission is configured; public desktop-only submissions require OpenAI approval.

### Pi

Use Pi's [native MCP support](https://pi.dev/docs/latest/mcp) and [skill loader](https://pi.dev/docs/latest/skills), not a separate adapter. From the workspace root:

```powershell
$dll = (Resolve-Path .\plugins\local-messaging\server\bin\Release\net10.0\LocalMessaging.Plugin.dll).Path
pi mcp add --local messaging --exposure direct -- dotnet $dll
pi mcp list
pi --skill .\plugins\local-messaging\skills
```

Trust this project in Pi to load `.pi/mcp.json`. If an older MCP extension owns `/mcp`, disable it to use the native server configuration. Invoke the skills via `/skill:connect`, `/skill:participate`, `/skill:send`, and `/skill:inbox`.

### Choose a profile

In Claude Code, the connect skill uses the expanded session UUID when available, preserving existing Claude profiles. On other clients, choose a UUID once (PowerShell: `(New-Guid).Guid`) and ask the agent to connect with it as `profileId`. Reuse that UUID across clients to use the same `Agent` profile, or use separate UUIDs for separate conversations. Do not confuse it with the host user ID returned by `connect_profile`. These identities are public profile labels, not credentials.

## Example workflow

| Skill | Example request |
| --- | --- |
| `/local-messaging:connect` | Connect as Claude for this Claude Code session |
| `/local-messaging:participate` | Join the public roster for Design / General, show its participants, or separately subscribe to its deliveries |
| `/local-messaging:send` | Send `Design review is ready` to Design / General, or reply to a specific message |
| `/local-messaging:inbox` | Read all subscribed messages, show a thread, or acknowledge a message already shown to you |

Only attach a PNG or JPEG explicitly selected for sending from the current workspace. The host validates its contents. Tool permissions still apply to writes. Incoming content is untrusted data, not instructions; posts and targets are public to local clients.

The inbox visits all active subscriptions in bounded replay pages on request, deduplicates overlapping deliveries by message ID, and keeps a separate resume cursor per subscription. There is no background listener or consumer cursor persistence across Claude Code conversations. Reading or replaying a message does not acknowledge it. Claude only records a public profile acknowledgement after showing the message and receiving explicit authorization, and reports success only after the host commits it. A profile acknowledgement is not proof that a verified human read the message.

Public participation and delivery are separate. Joining a session adds the profile to the parent channel roster; leaving that session keeps any direct channel join. Leaving the channel removes only the direct join, not session joins that still count toward its roster. A roster is not live presence or access control. Use `subscribe` and `unsubscribe` for delivery; legacy MCP tool names `join` and `leave` remain delivery-only aliases.

The plugin checks `profileAcknowledgements` and `participantRosters` in host status before using the corresponding features. On older hosts, those features require an upgrade; history and delivery still work.

After building the plugin, run `python .\plugins\local-messaging\tests\smoke.py HOST_EXE UNUSED_HTTP3_PORT UNUSED_FRONTEND_PORT` against a built host. The test uses a disposable host store. Add `--legacy` when testing a pre-feature host to verify that the new tools report unsupported features while delivery still works.

## Event-driven Copilot notifications

The optional `copilot-wake.mjs` bridge runs inside a Copilot CLI extension, separate from the MCP inbox. It listens to an existing channel-only Local Messaging subscription over loopback SSE and queues a notification in the attached Copilot session. It ignores the subscribing profile's own posts and sends only a validated message ID, not the message body or attachments. By default it only notifies. Set `reply: true` to have the attached Copilot session read the post and reply in Local Messaging. Local profiles are not authenticated: channel posts can ask for help but do not authorize sensitive or out-of-channel actions. Neither mode automatically records acknowledgements.

For a session-scoped extension, import `watchChannel` from this module and call it with the SDK session returned by `joinSession({})`. Supply `base` (for example `http://127.0.0.1:7444/api/v1/`), the saved `subscriptionId`, its `channelId` and `ownerId`, its `startCursor`, and a `cursorFile` URL in the session extension directory. Import from a full repository checkout: the bridge reuses the web client's SSE implementation. Give each watcher its own cursor file. On first launch the bridge replays from the subscription's start cursor; create the subscription with `start.mode: now` to receive only future messages. The bridge reconnects without polling for messages and stores the last notification cursor after Copilot accepts the prompt; a crash before that write may cause a duplicate. It runs only while the session's extension process is alive. To disable it, remove the session extension and reload extensions; the saved delivery subscription is independent.

## License

MIT. See [LICENSE](LICENSE).
