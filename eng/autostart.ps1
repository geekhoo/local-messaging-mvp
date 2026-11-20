#Requires -Version 5.1
<#
.SYNOPSIS
Explicitly enable or disable this user's optional sign-in startup registration.
.DESCRIPTION
Runs on Windows PowerShell 5.1 or PowerShell 7. Changes only HKCU Run. Nothing
installs this registration automatically. The value launches the known bundled
host directly, so sign-in does not require PowerShell 7, a development SDK, a UI,
an execution-policy change, or a scheduled task. It does not survive user logoff.
.EXAMPLE
powershell -NoProfile -File eng/autostart.ps1 -Enable -ProductDirectory C:\Apps\LocalMessaging
.EXAMPLE
powershell -NoProfile -File eng/autostart.ps1 -Disable
#>
[CmdletBinding(DefaultParameterSetName = 'Enable')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Enable')][switch]$Enable,
    [Parameter(Mandatory = $true, ParameterSetName = 'Disable')][switch]$Disable,
    [Parameter(Mandatory = $true, ParameterSetName = 'Enable')][string]$ProductDirectory,
    [Parameter(ParameterSetName = 'Enable')][string]$StateDirectory = (Join-Path $env:LOCALAPPDATA 'LocalMessaging'),
    [Parameter(ParameterSetName = 'Enable')][ValidateRange(1024, 65535)][int]$MessagingPort = 7443,
    [Parameter(ParameterSetName = 'Enable')][ValidateRange(1024, 65535)][int]$FrontendPort = 7444
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Optional sign-in startup is supported only on Windows.' }
if (($PSCmdlet.ParameterSetName -eq 'Enable' -and -not $Enable) -or ($PSCmdlet.ParameterSetName -eq 'Disable' -and -not $Disable)) { throw 'Specify an affirmative -Enable or -Disable action.' }
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$name = 'LocalMessaging'
if ($PSCmdlet.ParameterSetName -eq 'Disable') {
    if (Test-Path -LiteralPath $key) {
        $existing = Get-ItemProperty -LiteralPath $key -Name $name -ErrorAction SilentlyContinue
        if ($existing) { Remove-ItemProperty -LiteralPath $key -Name $name }
    }
    [ordered]@{ state = 'disabled'; scope = 'current_user'; registryValue = $name } | ConvertTo-Json -Compress
    return
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
function Quote-NativeArgument([string]$Value) { return '"' + ($Value -replace '(\\+)$', '$1$1') + '"' }
$ProductDirectory = Get-AbsoluteLocalPath $ProductDirectory 'ProductDirectory'
$StateDirectory = Get-AbsoluteLocalPath $StateDirectory 'StateDirectory'
foreach ($mutable in @($StateDirectory)) {
    if ($mutable.Equals($ProductDirectory, [StringComparison]::OrdinalIgnoreCase) -or
        $mutable.StartsWith($ProductDirectory.TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'State and data must remain outside the product directory.'
    }
}
$knownHost = Join-Path $ProductDirectory 'host\LocalMessaging.Host.exe'
if (-not (Test-Path -LiteralPath $knownHost -PathType Leaf)) { throw "The known bundled host executable is missing: $knownHost" }
$arguments = @((Quote-NativeArgument $knownHost), '--state-dir', (Quote-NativeArgument $StateDirectory),
    '--messaging-port', [string]$MessagingPort, '--frontend-port', [string]$FrontendPort)
$command = $arguments -join ' '
# https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys
if ($command.Length -gt 260) { throw 'The HKCU Run command exceeds its supported length. Use shorter product/state paths or launch manually.' }
New-Item -Path $key -Force | Out-Null
New-ItemProperty -LiteralPath $key -Name $name -Value $command -PropertyType String -Force | Out-Null
[ordered]@{ state = 'enabled'; scope = 'current_user'; registryValue = $name; command = $command } | ConvertTo-Json -Compress
