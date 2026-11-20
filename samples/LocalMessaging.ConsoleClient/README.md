# Reusable .NET client sample

This executable uses `LocalMessaging.Client` against the actual running host. Its default transport is **M with exact HTTP/3**: proxy bypass, hostname and expiry checks, and the SHA-256 certificate pin loaded from the local endpoint descriptor. It never falls back to HTTP/1.1. `--frontend` explicitly selects F for comparison or portable client development.

Build with `dotnet build samples/LocalMessaging.ConsoleClient -c Release`. Run `--help` for every command and option. The root Windows publication includes a self-contained build of this sample under `product/console`; running its published executable requires no development SDK.

## Create and watch a conversation

With the real host running, these PowerShell commands create a user and a channel with its initial session. Supply the isolated descriptor if using a qualification store; otherwise omit `--endpoint` to use the per-user default.

```powershell
$client = '.\LocalMessaging.ConsoleClient.exe'
$endpoint = 'C:\LocalMessaging-Test\endpoint.json'
$user = & $client --endpoint $endpoint user --handle sample_user --name 'Sample user' | ConvertFrom-Json
$userId = $user.dataObjects.user.id
$channel = & $client --endpoint $endpoint channel --user $userId --name 'Sample channel' --subscribe resolvedChannel | ConvertFrom-Json
$channelId = $channel.dataObjects.channel.id
$sessionId = $channel.dataObjects.session.id
& $client --endpoint $endpoint text --user $userId --channel $channelId --session $sessionId --text 'Hello from the .NET client'
& $client --endpoint $endpoint watch --user $userId --channel $channelId --session $sessionId
```

`watch` captures H, streams paged catalog and optional selected-session history at H, then attaches a separate subscription starting after H. Publication during snapshot load is replayed afterward. Each process owns its own cursor and closes only its own subscription. Applied event lines precede cursor advancement; duplicate or older positions do not reapply. Keep WinUI and this process open together, publish from either frontend, and observe independent delivery. Ctrl+C cancels the sample, closes its subscription when reachable, and leaves the host running. A store change reloads a new snapshot and revalidates the selected user ID.

Use `--seconds 10` for a bounded watch. Connection diagnostics go to stderr; commands return one committed receipt on stdout and watches return JSON lines. `get --path 'users?limit=50'` also exposes ordinary queries, including `requests/<request-id>` for reconciliation.

## Stable uncertain retries

The reusable client freezes the caller, JSON envelope and image bytes before its first attempt. Automatic retries in one run reuse those values. Every create command prints its complete frozen request to stderr before sending, and accepts an explicit `--request-id`. `--save-request C:\path\new-request.json` additionally saves the exact envelope before transmission without overwriting an existing file.

After an unconfirmed outcome, look up `requests/<request-id>` or use `send --file C:\path\new-request.json`. For an image, also provide `--image C:\path\original.png` with the **same original bytes**. A changed payload under an already committed request ID is rejected by the host. This sample has no offline outbox or hidden automatic replay across process launches.

```powershell
& $client --endpoint $endpoint image --user $userId --channel $channelId --session $sessionId --image 'C:\Images\example.png' --caption 'PNG with @sample_user' --save-request 'C:\LocalMessaging-Test\image-request.json'
& $client --endpoint $endpoint send --file 'C:\LocalMessaging-Test\image-request.json' --image 'C:\Images\example.png'
```

Text and image commands accept `--parent` and comma-separated `--targets`. `session` creates another session in an existing channel under the selected caller. A raw `send` envelope also exercises atomic new-channel/new-session/message contracts and coupled subscriptions. All domain validation stays in the host.

For release testing, run the sample on Windows with its default HTTP/3 transport. `--frontend` only exercises HTTP/1.1. See [Windows qualification](../../docs/windows-qualification.md).
