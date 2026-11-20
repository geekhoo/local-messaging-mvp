#Requires -Version 7.2
<#
.SYNOPSIS
Publish the Windows x64 candidate and separate self-contained verification tools.
.DESCRIPTION
Requires PowerShell 7.2+, Windows x64, the pinned SDK, and a clean committed
candidate. Linux cannot execute this publication gate. -OutputPath is an output
root: product/ contains the application, verify/ contains qualification tools.
An existing product/ or verify/ is never overwritten. Select a fresh output root
for another candidate. No user data, endpoint descriptors or certificates belong
in either publish input or the product directory.
.EXAMPLE
pwsh -NoProfile -File eng/publish.ps1 -OutputPath C:\builds\local-messaging-candidate
#>
[CmdletBinding()]
param(
    [ValidateSet('Release')][string]$Configuration = 'Release',
    [string]$DotNet = 'dotnet',
    [string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows) -or
    [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'BLOCKED: publishing the product requires Windows x64, including the Windows App SDK build tools. Linux publication is not a substitute.'
}
if (-not $OutputPath) { $OutputPath = Join-Path $repository 'artifacts' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$product = Join-Path $OutputPath 'product'
$verification = Join-Path $OutputPath 'verify'
if ((Test-Path -LiteralPath $product) -or (Test-Path -LiteralPath $verification)) {
    throw 'The output already contains product/ or verify/. Choose a fresh -OutputPath; existing artifacts and data are preserved.'
}
$dotnetCommand = (Get-Command $DotNet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$gitCommand = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$candidateCommit = (& $gitCommand -C $repository rev-parse --verify HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $candidateCommit -notmatch '^[0-9a-f]{40,64}$') { throw 'A committed source candidate is required before publication.' }
$changes = @(& $gitCommand -C $repository status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) { throw 'Commit and review the source candidate before publishing. The working tree contains changes or cannot be verified.' }
[IO.Directory]::CreateDirectory($OutputPath) | Out-Null
$staging = Join-Path $OutputPath ('.publish-' + [Guid]::NewGuid().ToString('N'))
$stagedProduct = Join-Path $staging 'product'
$stagedVerification = Join-Path $staging 'verify'
$logs = Join-Path $OutputPath ('publish-logs-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff'))
[IO.Directory]::CreateDirectory($logs) | Out-Null
$runtimeVersion = '10.0.15'

function Invoke-CheckedDotNet([string[]]$Arguments, [string]$Name) {
    Write-Host ('dotnet ' + ($Arguments -join ' '))
    $log = Join-Path $logs ($Name + '.log')
    $lines = @(& $dotnetCommand @Arguments 2>&1 | Tee-Object -FilePath $log)
    $exitCode = $LASTEXITCODE
    $lines | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) { throw "$Name failed with exit code $exitCode. See $log" }
    if (@($lines | Where-Object { [string]$_ -match '(?i)\bwarning\s+[a-z]+\d+\b|:\s*warning\b' }).Count -gt 0) {
        throw "$Name emitted a warning. Resolve it before publication; see $log"
    }
}

function Publish-Project([string]$Project, [string]$Destination, [string]$Name, [switch]$WinUI) {
    $projectPath = Join-Path $repository $Project
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) { throw "Publish project is missing: $Project" }
    # Directory.Build.targets pins Microsoft.NETCore.App and Microsoft.AspNetCore.App.
    # A global RuntimeFrameworkVersion would incorrectly rewrite the Windows SDK reference-pack version.
    $properties = @('-m:1', '-p:UseSharedCompilation=false', '-p:TreatWarningsAsErrors=true', '-p:Platform=x64',
        '-p:SelfContained=true', '-p:PublishTrimmed=false', '-p:PublishSingleFile=false')
    if ($WinUI) { $properties += '-p:WindowsAppSDKSelfContained=true' }
    Invoke-CheckedDotNet (@('restore', $projectPath, '--locked-mode', '--disable-parallel', '-r', 'win-x64') + $properties) ($Name + '-restore')
    Invoke-CheckedDotNet (@('publish', $projectPath, '--no-restore', '-c', $Configuration, '-r', 'win-x64',
        '--self-contained', 'true', '-o', $Destination, '--nologo', '-warnaserror') + $properties) ($Name + '-publish')
}

function Write-Manifest([string]$Directory, [string]$Kind, [string]$Sdk) {
    $files = @(Get-ChildItem -LiteralPath $Directory -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($Directory, $_.FullName).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            bytes = $_.Length
        }
    })
    [xml]$packages = Get-Content -LiteralPath (Join-Path $repository 'Directory.Packages.props') -Raw
    $versions = @($packages.Project.ItemGroup.PackageVersion | ForEach-Object { [ordered]@{ package = $_.Include; version = $_.Version } })
    [ordered]@{
        schemaVersion = 1; candidateCommit = $candidateCommit; configuration = $Configuration
        runtimeIdentifier = 'win-x64'; selfContained = $true; kind = $Kind
        sdk = $Sdk; runtimeVersion = $runtimeVersion; packages = $versions
        createdAtUtc = [DateTime]::UtcNow.ToString('o'); runtimeQualification = 'not_run'; files = $files
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Directory 'manifest.json') -Encoding utf8NoBOM
}

Push-Location $repository
try {
    $sdk = (& $dotnetCommand --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not record the selected SDK.' }
    Publish-Project 'src/LocalMessaging.Host/LocalMessaging.Host.csproj' (Join-Path $stagedProduct 'host') 'host'
    Publish-Project 'src/LocalMessaging.WinUI/LocalMessaging.WinUI.csproj' (Join-Path $stagedProduct 'winui') 'winui' -WinUI
    Publish-Project 'samples/LocalMessaging.ConsoleClient/LocalMessaging.ConsoleClient.csproj' (Join-Path $stagedProduct 'console') 'console-client'
    Publish-Project 'tests/LocalMessaging.Verify/LocalMessaging.Verify.csproj' (Join-Path $stagedVerification 'runner') 'verify-runner'
    Publish-Project 'tests/LocalMessaging.Scenarios/LocalMessaging.Scenarios.csproj' (Join-Path $stagedVerification 'scenarios') 'verify-scenarios'
    Publish-Project 'tests/LocalMessaging.StoreTests/LocalMessaging.StoreTests.csproj' (Join-Path $stagedVerification 'store') 'verify-store'
    Publish-Project 'tests/LocalMessaging.ClientTests/LocalMessaging.ClientTests.csproj' (Join-Path $stagedVerification 'client') 'verify-client'
    Publish-Project 'tests/LocalMessaging.NativeModelTests/LocalMessaging.NativeModelTests.csproj' (Join-Path $stagedVerification 'native-model') 'verify-native-model'
    Publish-Project 'tests/LocalMessaging.Platform.WindowsTests/LocalMessaging.Platform.WindowsTests.csproj' (Join-Path $stagedVerification 'wic') 'verify-wic'
    foreach ($relative in @('host/LocalMessaging.Host.exe', 'host/coreclr.dll', 'host/wwwroot/index.html',
        'winui/LocalMessaging.WinUI.exe', 'winui/coreclr.dll', 'console/LocalMessaging.ConsoleClient.exe', 'console/coreclr.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stagedProduct $relative) -PathType Leaf)) { throw "A required published product file is missing: $relative" }
    }
    if (@(Get-ChildItem -LiteralPath (Join-Path $stagedProduct 'host') -Recurse -File -Filter 'e_sqlite3.dll').Count -eq 0) { throw 'The published host lacks native SQLite.' }
    if (@(Get-ChildItem -LiteralPath (Join-Path $stagedProduct 'winui') -Recurse -File -Filter 'Microsoft.UI.Xaml.dll').Count -eq 0) { throw 'The published WinUI app lacks its self-contained Windows App SDK XAML runtime.' }
    [IO.Directory]::CreateDirectory((Join-Path $stagedProduct 'eng')) | Out-Null
    foreach ($script in @('start.ps1', 'stop.ps1', 'autostart.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination (Join-Path $stagedProduct 'eng') }
    Copy-Item -LiteralPath (Join-Path $repository 'README.md') -Destination $stagedProduct
    [IO.Directory]::CreateDirectory((Join-Path $stagedProduct 'docs')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository 'docs/operators.md') -Destination (Join-Path $stagedProduct 'docs/operators.md')
    $forbidden = @(Get-ChildItem -LiteralPath $stagedProduct -Recurse -File | Where-Object {
        $_.Extension.ToLowerInvariant() -in @('.db', '.sqlite', '.sqlite3', '.pfx', '.p12', '.pem', '.key', '.cer', '.crt') -or
        $_.Name -eq 'endpoint.json' -or $_.Name -match '^LocalMessaging\.(?:.*Tests|Scenarios|Verify)\.'
    })
    if ($forbidden.Count -gt 0) { throw 'The staged product contains a test binary, certificate, private key, endpoint descriptor or database. Publication stopped.' }
    Write-Manifest $stagedProduct 'product' $sdk
    Write-Manifest $stagedVerification 'verification-tools' $sdk
    Move-Item -LiteralPath $stagedProduct -Destination $product
    Move-Item -LiteralPath $stagedVerification -Destination $verification
    [ordered]@{ result = 'published'; product = $product; verificationTools = $verification; candidateCommit = $candidateCommit; runtimeQualification = 'not_run' } | ConvertTo-Json
    Write-Host 'Publication completed without warnings. Execute qualification against this exact product and manifest before accepting release gates.'
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
