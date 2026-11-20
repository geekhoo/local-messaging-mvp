#Requires -Version 5.1
<#
.SYNOPSIS
Request bounded shutdown of the verified bundled host instance.
.DESCRIPTION
Runs on Windows PowerShell 5.1 or PowerShell 7. Validates the saved descriptor,
known process path and live instance before using the frontend-only stop API.
It never kills a process or follows HTTP redirects/proxies.
.EXAMPLE
powershell -NoProfile -File C:\Apps\LocalMessaging\eng\stop.ps1 -ProductDirectory C:\Apps\LocalMessaging
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProductDirectory,
    [string]$StateDirectory = (Join-Path $env:LOCALAPPDATA 'LocalMessaging'),
    [ValidateRange(5, 120)][int]$TimeoutSeconds = 20
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'The product stop command requires Windows.' }
foreach ($value in @($ProductDirectory, $StateDirectory)) {
    if ($value -notmatch '^[A-Za-z]:[\\/]' -or $value.Contains('"') -or $value.IndexOfAny([char[]]"`r`n`0") -ge 0) { throw 'Product and state directories must be absolute local paths.' }
}
$ProductDirectory = [IO.Path]::GetFullPath($ProductDirectory)
$StateDirectory = [IO.Path]::GetFullPath($StateDirectory)
$knownHost = Join-Path $ProductDirectory 'host\LocalMessaging.Host.exe'
$descriptorPath = Join-Path $StateDirectory 'endpoint.json'
if (-not (Test-Path -LiteralPath $descriptorPath -PathType Leaf)) { [ordered]@{ state = 'already_stopped'; reason = 'No endpoint descriptor.' } | ConvertTo-Json -Compress; return }
if ((Get-Item -LiteralPath $descriptorPath).Length -gt 16384) { throw 'The endpoint descriptor exceeds its expected size.' }
$descriptor = [IO.File]::ReadAllText($descriptorPath) | ConvertFrom-Json
$instance = [Guid]::Empty
$epoch = [Guid]::Empty
if ($descriptor.contractVersion -ne 1 -or -not [Guid]::TryParseExact([string]$descriptor.instanceId, 'D', [ref]$instance) -or $instance -eq [Guid]::Empty -or
    -not [Guid]::TryParseExact([string]$descriptor.storeEpoch, 'D', [ref]$epoch) -or $epoch -eq [Guid]::Empty) { throw 'The descriptor identity is invalid; no stop was sent.' }
$frontend = [Uri]$descriptor.frontendBaseUrl
$messaging = [Uri]$descriptor.messagingBaseUrl
if (-not $frontend.IsAbsoluteUri -or $frontend.Scheme -cne 'http' -or $frontend.Host -cne '127.0.0.1' -or
    $frontend.Port -lt 1024 -or $frontend.AbsolutePath -cne '/api/v1/' -or $frontend.UserInfo -or $frontend.Query -or $frontend.Fragment -or
    -not $messaging.IsAbsoluteUri -or $messaging.Scheme -cne 'https' -or $messaging.Host -cne 'localhost' -or
    $messaging.Port -lt 1024 -or $messaging.AbsolutePath -cne '/messaging/v1/' -or $messaging.UserInfo -or $messaging.Query -or $messaging.Fragment -or
    $frontend.Port -eq $messaging.Port) { throw 'The descriptor is not the supported pair of local endpoints; no stop was sent.' }
$running = Get-Process -Id ([int]$descriptor.processId) -ErrorAction SilentlyContinue
if (-not $running) { [ordered]@{ state = 'already_stopped'; reason = 'The descriptor process no longer exists.' } | ConvertTo-Json -Compress; return }
if (-not [string]::Equals([IO.Path]::GetFullPath($running.Path), $knownHost, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The descriptor process is not the known bundled host executable; no stop was sent.'
}
Add-Type -AssemblyName System.Net.Http
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.UseProxy = $false
$handler.AllowAutoRedirect = $false
$http = New-Object System.Net.Http.HttpClient($handler)
$http.Timeout = [TimeSpan]::FromSeconds(3)
$http.MaxResponseContentBufferSize = 65536
$http.DefaultRequestHeaders.Add('X-Local-Messaging-Client', '1')
try {
    $statusResponse = $http.GetAsync([Uri]::new($frontend, 'status')).GetAwaiter().GetResult()
    try {
        if (-not $statusResponse.IsSuccessStatusCode) { throw "The host is not ready to verify shutdown (HTTP $([int]$statusResponse.StatusCode)). No stop was sent." }
        $status = $statusResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        if ($status.instanceId -ne $descriptor.instanceId -or $status.storeEpoch -ne $descriptor.storeEpoch -or
            $status.endpoints.frontend -cne $descriptor.frontendBaseUrl -or $status.endpoints.messaging -cne $descriptor.messagingBaseUrl) {
            throw 'The live host does not match the descriptor instance and epoch; no stop was sent.'
        }
    }
    finally { $statusResponse.Dispose() }
    $current = [IO.File]::ReadAllText($descriptorPath) | ConvertFrom-Json
    if ($current.instanceId -ne $descriptor.instanceId -or $current.processId -ne $descriptor.processId) { throw 'The descriptor changed during verification; retry stop.' }
    $content = New-Object System.Net.Http.StringContent('{}', [Text.Encoding]::UTF8, 'application/json')
    try {
        $stopResponse = $http.PostAsync([Uri]::new($frontend, 'host/stop'), $content).GetAwaiter().GetResult()
        try {
            if (-not $stopResponse.IsSuccessStatusCode) { throw "The stop API returned HTTP $([int]$stopResponse.StatusCode)." }
        }
        finally { $stopResponse.Dispose() }
    }
    finally { $content.Dispose() }
    if (-not $running.WaitForExit($TimeoutSeconds * 1000)) { throw 'The host has not finished its bounded shutdown. No process was killed; inspect its diagnostic log.' }
    [ordered]@{ state = 'stopped'; instanceId = $descriptor.instanceId; processId = $descriptor.processId } | ConvertTo-Json -Compress
}
finally { $http.Dispose() }
