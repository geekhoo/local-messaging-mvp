# Operator runbook

## Supported deployment and state

The release target is a per-user Windows 11 x64 installation. Host, WinUI and web assets are distributed as an unpackaged folder. The host runs independently of the UI; it is not a Windows service and stops at user logoff. Optional startup registration runs it again at sign-in.

Default locations and listeners:

| Item | Default |
| --- | --- |
| Mutable state | `%LOCALAPPDATA%\LocalMessaging` |
| Database | `<state>\data\messaging.db` |
| Active endpoint descriptor | `<state>\endpoint.json` |
| Certificate selector | `<state>\certificate.thumbprint` |
| Diagnostics | `<state>\logs\host.jsonl` and up to three rotated files |
| F, frontend API/web | `http://127.0.0.1:7444/` |
| M, native pub/sub | `https://localhost:7443/messaging/v1/`, HTTP/3 only |

Use local storage, not a UNC/network share. Keep product files and mutable state in separate directories. One running process holds the selected store lock. Another instance may use a different state/data directory and two different ports. The process writes its descriptor atomically after readiness and removes only a descriptor identifying its own instance on shutdown.

## Start, inspect, stop

The scripts under the published `product\eng` folder support Windows PowerShell 5.1 and PowerShell 7. They do not alter execution policy. Run them under your organization's existing script policy; direct EXE launch is also supported.

```powershell
$product = 'C:\Apps\LocalMessaging'
$state = 'C:\Users\YourName\AppData\Local\LocalMessaging'
powershell -NoProfile -File "$product\eng\start.ps1" -ProductDirectory $product -StateDirectory $state -OpenWeb
& "$product\winui\LocalMessaging.WinUI.exe" --endpoint "$state\endpoint.json"
```

The launcher starts only the known bundled host path. It reuses a running instance only after validating its descriptor, process path, instance ID, epoch, endpoints, readiness, and the host's completed HTTP/3 probe. Native `--no-launch-host` restricts a window to an already running host; it is useful for qualification. A custom descriptor never silently selects the default user store.

Status requires the same header as all API requests:

```powershell
Invoke-RestMethod 'http://127.0.0.1:7444/api/v1/status' -Headers @{ 'X-Local-Messaging-Client' = '1' }
```

Status includes the instance, version, readiness, epoch, head cursor, effective limits, active stream count, endpoints, and startup probe. A ready production host reports an exact HTTP version of `3.0` for that probe. Merely seeing a TCP port open is not readiness.

```powershell
powershell -NoProfile -File "$product\eng\stop.ps1" -ProductDirectory $product -StateDirectory $state
```

The stop script checks that it is talking to the expected host, then asks it to stop accepting writes and drain active work. If a send was interrupted, look up its receipt or retry with the original request ID, caller, and payload.

## Alternate ports and configuration

```powershell
powershell -NoProfile -File "$product\eng\start.ps1" -ProductDirectory $product -StateDirectory 'C:\Messaging-Test' -MessagingPort 17443 -FrontendPort 17444
& "$product\winui\LocalMessaging.WinUI.exe" --endpoint 'C:\Messaging-Test\endpoint.json' --no-launch-host
```

For advanced configuration, start the host directly:

```powershell
& "$product\host\LocalMessaging.Host.exe" --config 'C:\Messaging-Config\host.json'
```

Example `host.json`:

```json
{
  "stateDirectory": "C:\\Messaging-State",
  "dataDirectory": "C:\\Messaging-Data",
  "messagingPort": 7443,
  "frontendPort": 7444,
  "limits": {
    "activeStreams": 64,
    "streamWriteSeconds": 30,
    "heartbeatSeconds": 15,
    "clientIdleSeconds": 45
  }
}
```

Omitted limits retain defaults. Unknown fields and unsupported shapes fail validation. Ports must be distinct and between 1024 and 65535. Limits can be lowered within documented hard ceilings; invalid relationships, such as an idle timeout no greater than the heartbeat interval, are rejected. CLI options override the corresponding file settings. `--data-dir` is available for advanced direct-host operation; standard launchers deliberately use `<StateDirectory>\data`.

Default ceilings include 64 KiB JSON, 8 MiB image bytes, 9 MiB multipart, 8,192 pixels per side, 20 million decoded pixels, 16,384 text characters, 4,096 caption characters, 20 targets, 100 filter IDs, 200 page items, 1,000 examined event headers per scan, 1 MiB event-batch payload, 256 KiB event frames, 64 active streams, two simultaneous image validations and eight image responses. SQLite admission and busy timeouts are finite. Query/replay never holds a database reader across a network write.

## Optional startup at sign-in

Startup registration is opt-in and current-user only:

```powershell
powershell -NoProfile -File "$product\eng\autostart.ps1" -Enable -ProductDirectory $product -StateDirectory $state
powershell -NoProfile -File "$product\eng\autostart.ps1" -Disable
```

The single HKCU Run entry launches the known host directly. It does not grant privileges, install a service, or introduce a scheduled task. The script rejects a command exceeding the Windows Run value length limit. Re-register after moving the product folder. Start/stop/build/publication never enable autostart implicitly.

## Offline backup

Stop the host first. The maintenance command acquires the same exclusive store lock and refuses to operate on a live store.

```powershell
& "$product\host\LocalMessaging.Host.exe" --state-dir $state --backup 'C:\Messaging-Backups\before-upgrade.db'
```

Use a new destination. The command creates a coherent SQLite backup, including users, content, image bytes, receipts, events and subscription definitions. A raw copy of an actively written main `.db` file is not an equivalent backup. Backups contain public local messages and image data, so retain them in an appropriate location.

## Official restore

With the host stopped:

```powershell
& "$product\host\LocalMessaging.Host.exe" --state-dir $state --restore 'C:\Messaging-Backups\before-upgrade.db'
```

Restore validates the backup and migration history, creates a safety backup of current state, prepares a restored copy with a **new epoch** and **closed saved subscriptions**, checkpoints sidecars, then atomically publishes it. The returned record identifies the safety backup and new database. Existing clients must discard stale cursors, read a fresh snapshot, and create a new subscription. The content and immutable receipts remain coherent; the official restore path does not reuse the old event-stream epoch.

If validation or preparation fails, the current store is retained. If the restore has already been published, its new epoch is already durable. The integration tests exercise both sides of this publication boundary. Do not manually swap a main file while retaining unrelated WAL/SHM sidecars.

## Upgrade and certificate maintenance

Before upgrading, stop, back up, and retain the previous product folder. Replace the product folder without moving or deleting the state directory. Start the new binary against the existing state. Unknown future migration histories and corruption fail clearly rather than recreating an empty database. If a migration is pending, a pre-migration backup is created.

On first run the host creates a one-year localhost server certificate in the current user's Personal (`My`) certificate store, using persisted user-key storage. It does not install a machine-wide root. Alternatively, supply `--certificate-thumbprint` for an existing current-user certificate with an accessible private key, localhost SAN, Server Authentication EKU, and valid dates.

An expired, missing, or invalid selected certificate blocks startup. To renew, stop the host, retain the old selector for audit, and deliberately select a newly provisioned valid certificate or move the old selector aside so first-start provisioning can create a new one. Restart and let the host write its new descriptor only after exact HTTP/3 readiness. Supplied native clients reload the trusted local descriptor during controlled reconnection; they do not accept an arbitrary server certificate. Do not disable certificate checks to resolve a startup error.

## Diagnostics and capacity

Host logs rotate at about 1 MiB with up to three old files. Records contain timestamp, event category, trace/request ID where available, status and elapsed time. They omit request/message/image bodies and private keys. A failed diagnostic volume emits one bounded stderr warning per failure episode and cannot convert an already committed command into a failed operation; successful logging resumes after recovery.

`database_busy`, `storage_unavailable`, `resource_limit_reached`, and `host_not_ready` are explicit retry/reconciliation conditions. Preserve the frozen request while investigating. `store_changed`, future cursors, and closed subscriptions require the documented resnapshot/re-subscribe path. A stalled subscriber has a bounded write deadline; publishers continue, and the subscriber can recover from durable finite replay.

If startup reports `quic_unavailable`, check Windows QUIC support and local UDP policy. The host will not start in HTTP/1.1-only mode. See the [API guide](api.md) for request errors.
