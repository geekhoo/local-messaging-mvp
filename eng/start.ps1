#Requires -Version 5.1
<#
.SYNOPSIS
Start or reuse the independently running, bundled messaging host.
.DESCRIPTION
Runs on Windows PowerShell 5.1 or PowerShell 7. Uses only the known executable
under ProductDirectory/host, bypasses HTTP proxies for loopback probes, and
verifies descriptor, process path, instance, epoch and actual HTTP/3 readiness.
It does not install startup registration or change PowerShell execution policy.
.EXAMPLE
powershell -NoProfile -File C:\Apps\LocalMessaging\eng\start.ps1 -ProductDirectory C:\Apps\LocalMessaging -OpenWeb
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProductDirectory,
    [string]$StateDirectory = (Join-Path $env:LOCALAPPDATA 'LocalMessaging'),
    [ValidateRange(1024, 65535)][int]$MessagingPort = 7443,
    [ValidateRange(1024, 65535)][int]$FrontendPort = 7444,
    [ValidateRange(5, 120)][int]$ReadyTimeoutSeconds = 25,
    [switch]$OpenWeb
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'The product launcher requires Windows x64. Portable verification does not qualify the product runtime.'
}
if ($MessagingPort -eq $FrontendPort) { throw 'MessagingPort and FrontendPort must be distinct.' }

function Get-AbsoluteLocalPath([string]$Value, [string]$Name) {
    if ($Value -notmatch '^[A-Za-z]:[\\/]' -or $Value.Contains('"') -or $Value.IndexOfAny([char[]]"`r`n`0") -ge 0) {
        throw "$Name must be an absolute local path without control characters. UNC paths are not supported."
    }
    $full = [IO.Path]::GetFullPath($Value)
    if ($full.Length -eq [IO.Path]::GetPathRoot($full).Length) { return $full }
    return $full.TrimEnd([char[]]'\/')
}
function Quote-NativeArgument([string]$Value) {
    # Windows native argv escaping: quote paths and double terminal backslashes.
    return '"' + ($Value -replace '(\\+)$', '$1$1') + '"'
}
$ProductDirectory = Get-AbsoluteLocalPath $ProductDirectory 'ProductDirectory'
$StateDirectory = Get-AbsoluteLocalPath $StateDirectory 'StateDirectory'
foreach ($mutable in @($StateDirectory)) {
    if ($mutable.Equals($ProductDirectory, [StringComparison]::OrdinalIgnoreCase) -or
        $mutable.StartsWith($ProductDirectory.TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'State and data must remain outside the product directory so replacing binaries preserves user data.'
    }
}
$knownHost = Join-Path $ProductDirectory 'host\LocalMessaging.Host.exe'
if (-not (Test-Path -LiteralPath $knownHost -PathType Leaf)) { throw "The known bundled host executable is missing: $knownHost" }
$descriptorPath = Join-Path $StateDirectory 'endpoint.json'
$frontendBase = "http://127.0.0.1:$FrontendPort/api/v1/"
$messagingBase = "https://localhost:$MessagingPort/messaging/v1/"
$script:lastProbeProblem = 'No current endpoint descriptor is visible.'
Add-Type -AssemblyName System.Net.Http
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.UseProxy = $false
$handler.AllowAutoRedirect = $false
$http = New-Object System.Net.Http.HttpClient($handler)
$http.Timeout = [TimeSpan]::FromSeconds(2)
$http.MaxResponseContentBufferSize = 65536
$http.DefaultRequestHeaders.Add('X-Local-Messaging-Client', '1')

function Get-VerifiedReadyHost {
    $response = $null
    try {
        if (-not (Test-Path -LiteralPath $descriptorPath -PathType Leaf)) { return $null }
        if ((Get-Item -LiteralPath $descriptorPath).Length -gt 16384) { throw 'The endpoint descriptor exceeds its expected size.' }
        $descriptor = [IO.File]::ReadAllText($descriptorPath) | ConvertFrom-Json
        $instance = [Guid]::Empty
        $epoch = [Guid]::Empty
        if ($descriptor.contractVersion -ne 1 -or $descriptor.ready -ne $true -or
            -not [Guid]::TryParseExact([string]$descriptor.instanceId, 'D', [ref]$instance) -or $instance -eq [Guid]::Empty -or
            -not [Guid]::TryParseExact([string]$descriptor.storeEpoch, 'D', [ref]$epoch) -or $epoch -eq [Guid]::Empty -or
            $descriptor.frontendBaseUrl -cne $frontendBase -or $descriptor.messagingBaseUrl -cne $messagingBase -or
            $descriptor.certificateSha256 -notmatch '^[0-9a-fA-F]{64}$') {
            throw 'The descriptor does not match this configured product endpoint.'
        }
        $running = Get-Process -Id ([int]$descriptor.processId) -ErrorAction Stop
        if (-not [string]::Equals([IO.Path]::GetFullPath($running.Path), $knownHost, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The descriptor process is not the known bundled host executable.'
        }
        $response = $http.GetAsync($frontendBase + 'status').GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) { throw "The host status returned $([int]$response.StatusCode)." }
        $status = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        if ($status.ready -ne $true -or $status.instanceId -ne $descriptor.instanceId -or
            $status.storeEpoch -ne $descriptor.storeEpoch -or $status.startupProbe.result -ne 'http3_exact_verified' -or
            $status.startupProbe.httpVersion -ne '3.0' -or $status.endpoints.frontend -cne $frontendBase -or
            $status.endpoints.messaging -cne $messagingBase) {
            throw 'The status did not verify the descriptor instance, epoch and actual HTTP/3 readiness.'
        }
        return $descriptor
    }
    catch { $script:lastProbeProblem = $_.Exception.Message; return $null }
    finally { if ($response) { $response.Dispose() } }
}

try {
    $ready = Get-VerifiedReadyHost
    $outcome = 'already_running'
    if (-not $ready) {
        [IO.Directory]::CreateDirectory($StateDirectory) | Out-Null
        $arguments = @('--state-dir', (Quote-NativeArgument $StateDirectory), '--messaging-port', [string]$MessagingPort,
            '--frontend-port', [string]$FrontendPort)
        $stdout = Join-Path $StateDirectory 'last-startup.stdout.log'
        $stderr = Join-Path $StateDirectory 'last-startup.stderr.log'
        $launched = $null
        $launchProblem = ''
        try {
            $launched = Start-Process -FilePath $knownHost -ArgumentList ($arguments -join ' ') -WorkingDirectory (Split-Path -Parent $knownHost) `
                -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
        }
        catch {
            # Overlapping launchers can contend for the fixed diagnostic files.
            # A valid concurrently started instance still wins the readiness check.
            $launchProblem = $_.Exception.Message
        }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while ($timer.Elapsed.TotalSeconds -lt $ReadyTimeoutSeconds) {
            Start-Sleep -Milliseconds 250
            $ready = Get-VerifiedReadyHost
            if ($ready) { break }
            # A second launch may lose the store lock while the first is starting.
            # Continue probing for the valid winner; never kill another process.
        }
        if (-not $ready) {
            $details = if (Test-Path -LiteralPath $stderr -PathType Leaf) { ([IO.File]::ReadAllText($stderr)).Trim() } else { '' }
            if ($details.Length -gt 4096) { $details = $details.Substring(0, 4096) }
            throw "The host did not become ready within $ReadyTimeoutSeconds seconds. $script:lastProbeProblem $launchProblem $details"
        }
        $outcome = if ($launched -and $ready.processId -eq $launched.Id) { 'started' } else { 'reused_concurrent_start' }
    }
    if ($OpenWeb) { Start-Process -FilePath "http://127.0.0.1:$FrontendPort/" | Out-Null }
    [ordered]@{ state = $outcome; instanceId = $ready.instanceId; processId = $ready.processId; storeEpoch = $ready.storeEpoch;
        frontend = $frontendBase; messaging = $messagingBase; http3 = 'verified_by_host_self_probe' } | ConvertTo-Json -Compress
}
finally { $http.Dispose() }
