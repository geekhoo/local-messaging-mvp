#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('portable','task','demo','checkpoint','milestone','acceptance')][string]$Scope,
    [string]$Id='All',
    [ValidateSet('Release')][string]$Configuration='Release',
    [string]$AgainstPackage,
    [string]$EvidencePath,
    [string]$Observations,
    [switch]$IncludeWorkload,
    [switch]$NoBuild
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$Scope=$Scope.ToLowerInvariant()
$Id=if($Id -ieq 'All'){'All'}else{$Id.ToUpperInvariant()}
$root=Split-Path $PSScriptRoot -Parent
$plan=Get-Content -LiteralPath (Join-Path $root 'docs/baseline-plan/data/implementation-plan.json') -Raw | ConvertFrom-Json
$valid=$false
switch($Scope) {
    'portable' { $valid=$Id -eq 'All' }
    {$_ -in 'task','demo'} { $valid=@($plan.tasks | Where-Object id -EQ $Id).Count -eq 1 }
    'checkpoint' { $valid=@($plan.checkpoints | Where-Object id -EQ $Id).Count -eq 1 }
    'milestone' { $valid=@($plan.milestones | Where-Object id -EQ $Id).Count -eq 1 }
    'acceptance' { $valid=$Id -eq 'All' -or @($plan.acceptance_coverage | Where-Object id -EQ $Id).Count -eq 1 }
}
if(-not $valid) { Write-Error "Unknown ID '$Id' for scope '$Scope'. No checks ran." -ErrorAction Continue;exit 2 }
if($AgainstPackage -and $Scope -eq 'portable') { Write-Error 'Portable scope cannot qualify a Windows package.' -ErrorAction Continue;exit 2 }
$candidate='uncommitted'
$gitCommand=Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if($gitCommand) {
    Push-Location $root
    try {
        $revision=& $gitCommand.Source rev-parse --verify HEAD 2>$null
        if($LASTEXITCODE -eq 0) { $candidate=[string]$revision }
    } finally { Pop-Location }
}
$invokerSourceCommit=$candidate
$packageManifestHash=$null
$packageIdentityError=$null
$product=$null
if($AgainstPackage) {
    try {
        $product=[IO.Path]::GetFullPath($AgainstPackage)
        $manifestPath=Join-Path $product 'manifest.json'
        $packageManifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if($packageManifest.candidateCommit -notmatch '^[0-9a-f]{40,64}$') { throw 'The package manifest has no valid candidate commit.' }
        $candidate=[string]$packageManifest.candidateCommit
        $packageManifestHash=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    } catch { $candidate='unknown-package';$packageIdentityError=$_.Exception.Message }
}
if(-not $EvidencePath) { $EvidencePath=Join-Path $root ('evidence/runtime/'+$candidate.Substring(0,[Math]::Min(12,$candidate.Length))+'/'+$Scope+'-'+$Id+'/'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')) }
$EvidencePath=[IO.Path]::GetFullPath($EvidencePath)
if($AgainstPackage -and $product) {
    foreach($inventoryRoot in @($product,(Join-Path (Split-Path $product -Parent) 'verify'))) {
        $inventoryPrefix=[IO.Path]::GetFullPath($inventoryRoot).TrimEnd([char[]]'\/')
        if($EvidencePath.Equals($inventoryPrefix,[StringComparison]::OrdinalIgnoreCase) -or $EvidencePath.StartsWith($inventoryPrefix+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
            Write-Error 'Evidence must be outside product and verification-tool inventories.' -ErrorAction Continue;exit 2
        }
    }
}
[IO.Directory]::CreateDirectory($EvidencePath) | Out-Null
$steps=[Collections.Generic.List[object]]::new()
$blockers=[Collections.Generic.List[string]]::new()
$exitCode=0
$result='running'
$started=[DateTime]::UtcNow

function Invoke-Check {
    param([string]$Name,[string]$Executable,[string[]]$Arguments,[int[]]$AllowedCodes=@(0),[int]$TimeoutSeconds=600)
    $log=Join-Path $EvidencePath ($Name+'.log')
    $info=[Diagnostics.ProcessStartInfo]::new($Executable)
    $info.WorkingDirectory=$root
    $info.UseShellExecute=$false
    $info.RedirectStandardOutput=$true
    $info.RedirectStandardError=$true
    foreach($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::new();$process.StartInfo=$info
    $watch=[Diagnostics.Stopwatch]::StartNew()
    Write-Host "Running $Name"
    if(-not $process.Start()) { throw "Could not start $Executable" }
    $outTask=$process.StandardOutput.ReadToEndAsync()
    $errTask=$process.StandardError.ReadToEndAsync()
    if(-not $process.WaitForExit($TimeoutSeconds*1000)) {
        $process.Kill($true);$process.WaitForExit();throw "$Name exceeded its finite timeout ($TimeoutSeconds seconds)."
    }
    $output=$outTask.GetAwaiter().GetResult()+$errTask.GetAwaiter().GetResult()
    [IO.File]::WriteAllText($log,$output)
    $code=$process.ExitCode
    $steps.Add([ordered]@{name=$Name;command=@($Executable)+$Arguments;exitCode=$code;elapsedMs=$watch.ElapsedMilliseconds;log=[IO.Path]::GetFileName($log);result=$(if($code -eq 0){'passed'}elseif($code -in 2,3){'blocked'}else{'failed'})})
    Write-Host "$Name exited $code; log: $log"
    $process.Dispose()
    if($code -notin $AllowedCodes) { throw "$Name failed with exit code $code. See $log" }
    return $code
}

try {
    if($packageIdentityError) { throw $packageIdentityError }
    if($AgainstPackage) {
        $product=[IO.Path]::GetFullPath($AgainstPackage)
        $runner=Join-Path (Split-Path $product -Parent) 'verify/runner/LocalMessaging.Verify.exe'
        if(-not $IsWindows) { $blockers.Add('Published qualification requires actual Windows 11 x64.');$exitCode=3 }
        elseif(-not (Test-Path -LiteralPath $runner -PathType Leaf)) { $blockers.Add("Missing separately published verifier: $runner");$exitCode=3 }
        else {
            $arguments=@('--scope',$Scope,'--id',$Id,'--product',$product,'--evidence',(Join-Path $EvidencePath 'package'))
            if($Observations) { $arguments+=@('--observations',[IO.Path]::GetFullPath($Observations)) }
            $exitCode=Invoke-Check 'published-qualification' $runner $arguments @(0,3) 1800
            if($exitCode -eq 3) { $blockers.Add('Published runner reported required evidence unavailable. Inspect package/qualification.json.') }
        }
    } else {
        $dotnet=(Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
        $node=(Get-Command node -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
        if(-not $NoBuild) {
            $pwsh=(Get-Process -Id $PID).Path
            $buildArgs=@('-NoProfile','-File',(Join-Path $PSScriptRoot 'build.ps1'),'-Configuration',$Configuration)
            if([IO.Path]::GetFileNameWithoutExtension($pwsh) -eq 'dotnet') { $buildArgs=@((Join-Path $PSHOME 'pwsh.dll'))+$buildArgs }
            if(-not $IsWindows -or $Scope -eq 'portable') { $buildArgs+='-CoreOnly' }
            [void](Invoke-Check 'build' $pwsh $buildArgs)
        }
        $assemblyBase='bin/'+$Configuration+'/net10.0/'
        [void](Invoke-Check 'contracts' $dotnet @((Join-Path $root ('tests/LocalMessaging.Scenarios/'+$assemblyBase+'LocalMessaging.Scenarios.dll')),'--suite','contracts','--evidence',(Join-Path $EvidencePath 'contracts.json')))
        [void](Invoke-Check 'store' $dotnet @((Join-Path $root ('tests/LocalMessaging.StoreTests/'+$assemblyBase+'LocalMessaging.StoreTests.dll')),'--output',(Join-Path $EvidencePath 'store.json')))
        [void](Invoke-Check 'client' $dotnet @((Join-Path $root ('tests/LocalMessaging.ClientTests/'+$assemblyBase+'LocalMessaging.ClientTests.dll'))))
        [void](Invoke-Check 'native-model' $dotnet @((Join-Path $root ('tests/LocalMessaging.NativeModelTests/'+$assemblyBase+'LocalMessaging.NativeModelTests.dll'))))
        [void](Invoke-Check 'transport' $dotnet @((Join-Path $root ('tests/LocalMessaging.Scenarios/'+$assemblyBase+'LocalMessaging.Scenarios.dll')),'--suite','transport','--evidence',(Join-Path $EvidencePath 'transport.json')))
        $webTests=@(Get-ChildItem -LiteralPath (Join-Path $root 'src/LocalMessaging.Web/tests') -Filter '*.test.js' | Sort-Object Name | ForEach-Object FullName)
        if($webTests.Count -eq 0) { throw 'Missing web component tests.' }
        [void](Invoke-Check 'web' $node (@('--test','--test-reporter=tap')+$webTests))
        foreach($js in (Get-ChildItem -LiteralPath (Join-Path $root 'src/LocalMessaging.Web') -Filter '*.js')) {
            [void](Invoke-Check ('syntax-'+$js.BaseName) $node @('--check',$js.FullName))
        }
        if($IncludeWorkload -or $Id -in 'T039','T040','CP14','M06' -or ($Scope -eq 'acceptance' -and $Id -in 'All','A22','A34','A35','A38')) {
            [void](Invoke-Check 'workload' $dotnet @((Join-Path $root ('tests/LocalMessaging.StoreTests/'+$assemblyBase+'LocalMessaging.StoreTests.dll')),'--workload','--output',(Join-Path $EvidencePath 'workload.json')))
        }
        if($Scope -ne 'portable') {
            if(-not $IsWindows) { $blockers.Add('The plan requires Windows 11 x64, real HTTP/3/WIC and actual implicated frontends. Portable results are supporting evidence only.');$exitCode=3 }
            else {
                [void](Invoke-Check 'windows-runtime' $dotnet @((Join-Path $root ('tests/LocalMessaging.Scenarios/'+$assemblyBase+'LocalMessaging.Scenarios.dll')),'--suite','windows','--evidence',(Join-Path $EvidencePath 'windows-runtime.json')))
                [void](Invoke-Check 'wic' $dotnet @((Join-Path $root ('tests/LocalMessaging.Platform.WindowsTests/'+$assemblyBase+'LocalMessaging.Platform.WindowsTests.dll'))))
                $blockers.Add('Supporting source checks completed. Final plan-scope qualification requires -AgainstPackage and any required actual UI/platform observations; no checkpoint/milestone is automatically accepted.')
                $exitCode=3
            }
        }
    }
    $result=if($exitCode -eq 0){'passed'}elseif($exitCode -eq 3){'blocked'}else{'failed'}
} catch {
    $result='failed';$exitCode=1;$blockers.Add($_.Exception.Message)
    Write-Error $_ -ErrorAction Continue
} finally {
    $report=[ordered]@{
        schemaVersion=1;scope=$Scope;id=$Id;result=$result;exitCode=$exitCode;candidateCommit=$candidate
        invokerSourceCommit=$invokerSourceCommit;packageManifestSha256=$packageManifestHash
        configuration=$Configuration;startedAtUtc=$started.ToString('O');completedAtUtc=[DateTime]::UtcNow.ToString('O')
        os=[Runtime.InteropServices.RuntimeInformation]::OSDescription
        architecture=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        command=[Environment]::CommandLine;usedExistingBuild=[bool]$NoBuild;steps=$steps;blockers=$blockers
        gateStatus='not_accepted';reviewRequired='Independent full review and closure of every finding/warning remain explicit gates.'
        qualification=$(if($Scope -eq 'portable'){'Supporting portable implementation evidence only; excludes Windows/H3/WIC/WinUI/browser/clean-machine acceptance.'}else{'See individual results and required observations; missing execution cannot pass.'})
    }
    $report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $EvidencePath 'verification.json') -Encoding utf8NoBOM
}
Write-Host "Verification result: $result (exit $exitCode). $EvidencePath"
exit $exitCode
