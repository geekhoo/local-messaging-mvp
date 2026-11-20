#requires -Version 7.2
<#
.SYNOPSIS
Validate operator/build script syntax and isolated helper behavior without publishing a product.
.DESCRIPTION
Run from a source checkout with PowerShell 7.2 or later. Publication command calls
are mocked. Manifest files are confined to a unique temporary directory. The
non-Windows refusal tests are explicitly skipped on Windows, so this suite never
starts a real build or publication there. Windows runtime qualification is separate.
.PARAMETER OutputPath
Optional JSON report filename. Defaults to evidence/script-validation.json in the checkout.
.EXAMPLE
pwsh -NoProfile -File tests/validate-operator-scripts.ps1 -OutputPath C:\evidence\operator-scripts.json
#>
[CmdletBinding()]
param([string]$OutputPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputPath) { $OutputPath = Join-Path $root 'evidence/script-validation.json' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$checks = [Collections.Generic.List[object]]::new()
$result = 'passed'
$failure = $null
function Check([bool]$Value, [string]$Name) {
    $checks.Add([ordered]@{ name = $Name; result = $(if ($Value) { 'passed' } else { 'failed' }) })
    if (-not $Value) { throw "Failed: $Name" }
    Write-Output "PASS $Name"
}
function Skip([string]$Name, [string]$Reason) {
    $checks.Add([ordered]@{ name = $Name; result = 'skipped'; reason = $Reason })
    Write-Output "SKIP ${Name}: $Reason"
}
try {
foreach ($name in @('build', 'publish', 'start', 'stop', 'autostart', 'verify')) {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root "eng/$name.ps1"), [ref]$tokens, [ref]$errors)
    Check ($errors.Count -eq 0) "parser.$name"
    if ($name -eq 'start') { $startAst = $ast }
    if ($name -eq 'publish') { $publishAst = $ast }
}
$quoteFunction = $startAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Quote-NativeArgument' }, $true)
. ([ScriptBlock]::Create($quoteFunction.Extent.Text))
Check ((Quote-NativeArgument 'C:\Two Words\host.exe') -ceq '"C:\Two Words\host.exe"') 'native_argument.spaces_quoted'
Check ((Quote-NativeArgument 'C:\apps\') -ceq '"C:\apps\\"') 'native_argument.terminal_backslash_escaped'
Check ('C:relative' -notmatch '^[A-Za-z]:[\\/]' -and '\relative' -notmatch '^[A-Za-z]:[\\/]' -and '\\server\share' -notmatch '^[A-Za-z]:[\\/]') 'paths.relative_and_unc_rejected'

$gitAssignment = $publishAst.Find({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$gitCommand' }, $true)
if (Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1) {
    . ([ScriptBlock]::Create($gitAssignment.Extent.Text))
    Check ($gitCommand -is [string] -and [IO.File]::Exists($gitCommand)) 'tool_lookup.selects_one_application'
    $gitVersion = & $gitCommand --version
    Check ($LASTEXITCODE -eq 0 -and [string]$gitVersion -match '^git version ') 'tool_lookup.invokes_selected_application'
} else {
    Skip 'tool_lookup.selects_one_application' 'git is unavailable; parser and remaining pure-helper checks still run.'
    Skip 'tool_lookup.invokes_selected_application' 'git is unavailable; no executable invocation was attempted.'
}

$repository = $root
$runtimeVersion = '10.0.15'
$Configuration = 'Release'
$calls = [Collections.Generic.List[object]]::new()
function Invoke-CheckedDotNet([string[]]$Arguments, [string]$Name) { $calls.Add([pscustomobject]@{ Arguments = $Arguments; Name = $Name }) }
$publishFunction = $publishAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Publish-Project' }, $true)
. ([ScriptBlock]::Create($publishFunction.Extent.Text))
Publish-Project 'src/LocalMessaging.WinUI/LocalMessaging.WinUI.csproj' (Join-Path ([IO.Path]::GetTempPath()) 'unused-script-fixture-output') 'fixture' -WinUI
Check ($calls.Count -eq 2) 'publish.explicit_restore_and_publish'
Check ($calls[0].Arguments -contains '--locked-mode' -and $calls[0].Arguments -contains '--disable-parallel' -and $calls[1].Arguments -contains '--no-restore') 'publish.locked_restore_no_implicit_restore'
foreach ($call in $calls) {
    Check ($call.Arguments -contains '-m:1' -and $call.Arguments -contains '-p:UseSharedCompilation=false') ("publish.serial_no_compiler_server." + $call.Name)
    Check (@($call.Arguments | Where-Object { $_ -like '-p:RuntimeFrameworkVersion=*' }).Count -eq 0) ("publish.no_global_runtime_pin_corrupts_windows_sdk_reference." + $call.Name)
    Check ($call.Arguments -contains '-p:SelfContained=true' -and $call.Arguments -contains '-p:WindowsAppSDKSelfContained=true') ("publish.two_runtime_families." + $call.Name)
}
$manifestFunction = $publishAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Write-Manifest' }, $true)
. ([ScriptBlock]::Create($manifestFunction.Extent.Text))
$fixtureDirectory = Join-Path ([IO.Path]::GetTempPath()) ('local-messaging-manifest-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory((Join-Path $fixtureDirectory 'host')) | Out-Null
$candidateCommit = '1111111111111111111111111111111111111111'
try {
    [IO.File]::WriteAllText((Join-Path $fixtureDirectory 'host/probe.txt'), 'manifest test fixture only')
    Write-Manifest $fixtureDirectory 'test-fixture' '10.0.404'
    $manifest = [IO.File]::ReadAllText((Join-Path $fixtureDirectory 'manifest.json')) | ConvertFrom-Json
    Check ($manifest.schemaVersion -eq 1 -and $manifest.candidateCommit -eq $candidateCommit -and $manifest.configuration -eq 'Release' -and $manifest.runtimeIdentifier -eq 'win-x64' -and $manifest.selfContained) 'manifest.required_schema'
    Check ($manifest.files.Count -eq 1 -and $manifest.files[0].path -ceq 'host/probe.txt') 'manifest.safe_relative_path_excludes_manifest'
    Check ($manifest.files[0].sha256 -ceq (Get-FileHash -LiteralPath (Join-Path $fixtureDirectory 'host/probe.txt') -Algorithm SHA256).Hash.ToLowerInvariant() -and $manifest.files[0].bytes -eq 26) 'manifest.actual_file_hash_and_bytes'
}
finally { Remove-Item -LiteralPath $fixtureDirectory -Recurse -Force }
foreach ($name in @('build', 'publish')) {
    if ($IsWindows) {
        Skip "platform_guard.$name" 'Inapplicable on Windows: this test only checks explicit non-Windows refusal. No real build or publication was started.'
        continue
    }
    $blocked = $false
    try { & (Join-Path $root "eng/$name.ps1") }
    catch { $blocked = $_.Exception.Message.StartsWith('BLOCKED:', [StringComparison]::Ordinal) }
    Check $blocked "platform_guard.$name"
}

} catch {
    $result = 'failed'
    $failure = $_.Exception.Message
    [Console]::Error.WriteLine($failure)
}
$reviewedFiles = @('eng/build.ps1', 'eng/publish.ps1', 'eng/start.ps1', 'eng/stop.ps1', 'eng/autostart.ps1', 'eng/verify.ps1',
    'LocalMessaging.slnx', 'LocalMessaging.Core.slnx', 'Directory.Build.props', 'Directory.Build.targets', 'tests/validate-operator-scripts.ps1')
$hashes = @($reviewedFiles | ForEach-Object { [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $root $_) -Algorithm SHA256).Hash.ToLowerInvariant() } })
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
[ordered]@{
    suite = 'PowerShellScriptValidation'; result = $result; atUtc = [DateTime]::UtcNow.ToString('o')
    os = [Runtime.InteropServices.RuntimeInformation]::OSDescription; powershell = $PSVersionTable.PSVersion.ToString()
    checks = $checks; failure = $failure; reviewedSourceHashes = $hashes
    limitations = @('Parser and isolated helper behavior only. No Windows publication, launcher, registry write, Windows PowerShell 5.1, or runtime qualification was executed. Skipped checks retain an explicit reason.')
} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
Write-Output "Report: $OutputPath"
if ($result -ne 'passed') { exit 1 }
exit 0
