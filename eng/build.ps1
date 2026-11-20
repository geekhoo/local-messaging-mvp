#Requires -Version 7.2
<#
.SYNOPSIS
Build the pinned Release candidate with locked restore and zero warnings.
.DESCRIPTION
Requires PowerShell 7.2+ and the SDK selected by global.json. Windows 11 x64 is
required for the full solution. -CoreOnly explicitly builds portable supporting
projects and does not qualify WinUI, WIC, HTTP/3, or Windows deployment.
.EXAMPLE
pwsh -NoProfile -File eng/build.ps1
.EXAMPLE
pwsh -NoProfile -File eng/build.ps1 -CoreOnly -DotNet /absolute/path/to/dotnet
#>
[CmdletBinding()]
param(
    [ValidateSet('Release')][string]$Configuration = 'Release',
    [switch]$CoreOnly,
    [string]$DotNet = 'dotnet',
    [string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputPath) { $OutputPath = Join-Path $repository 'artifacts/build' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$isWindowsHost = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
$isX64 = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [Runtime.InteropServices.Architecture]::X64
if (-not $CoreOnly -and (-not $isWindowsHost -or -not $isX64)) {
    throw 'BLOCKED: the full Windows x64 solution requires Windows x64. Use -CoreOnly explicitly for portable supporting build evidence.'
}
$dotnetCommand = (Get-Command $DotNet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$solution = Join-Path $repository $(if ($CoreOnly) { 'LocalMessaging.Core.slnx' } else { 'LocalMessaging.slnx' })
if (-not (Test-Path -LiteralPath $solution -PathType Leaf)) { throw "Solution is missing: $solution" }
[IO.Directory]::CreateDirectory($OutputPath) | Out-Null

function Invoke-CheckedDotNet([string[]]$Arguments, [string]$Name) {
    Write-Host ('dotnet ' + ($Arguments -join ' '))
    $log = Join-Path $OutputPath ($Name + '.log')
    $lines = @(& $dotnetCommand @Arguments 2>&1 | Tee-Object -FilePath $log)
    $exitCode = $LASTEXITCODE
    $lines | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) { throw "$Name failed with exit code $exitCode. See $log" }
    if (@($lines | Where-Object { [string]$_ -match '(?i)\bwarning\s+[a-z]+\d+\b|:\s*warning\b' }).Count -gt 0) {
        throw "$Name emitted a warning. Resolve it before accepting the build; see $log"
    }
}

Push-Location $repository
try {
    $scope = if ($CoreOnly) { 'core' } else { 'windows-x64' }
    $properties = @('-m:1', '-p:UseSharedCompilation=false', '-p:TreatWarningsAsErrors=true')
    $restoreArguments = @('restore', $solution, '--locked-mode', '--disable-parallel') + $properties
    $buildArguments = @('build', $solution, '--no-restore', '-c', $Configuration, '--nologo', '-warnaserror') + $properties
    if (-not $CoreOnly) {
        # A solution-wide RuntimeIdentifier is rejected by the SDK. WinUI declares win-x64 itself;
        # other projects compile normally and publication selects win-x64 per project.
        $restoreArguments += '-p:Platform=x64'
        $buildArguments += '-p:Platform=x64'
    }
    Invoke-CheckedDotNet $restoreArguments ($scope + '-restore')
    Invoke-CheckedDotNet $buildArguments ($scope + '-build')
    $sdk = (& $dotnetCommand --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not record the selected SDK.' }
    $report = [ordered]@{
        schemaVersion = 1; result = 'passed'; scope = $scope; configuration = $Configuration
        sdk = $sdk; executedAtUtc = [DateTime]::UtcNow.ToString('o')
        os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        runtimeQualification = 'not_run'; warningCount = 0
        limitations = if ($CoreOnly) { @('Portable compilation only. Windows, HTTP/3, WIC, WinUI and published-product runtime gates remain separate.') } else { @('Compilation only. Execute the Windows/runtime and published-product gates separately.') }
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputPath ($scope + '-build.json')) -Encoding utf8NoBOM
    Write-Host "Release $scope build passed with zero warnings. Runtime qualification is separate."
}
finally { Pop-Location }
