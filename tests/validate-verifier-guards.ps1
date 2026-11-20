#requires -Version 7.2
<#
.SYNOPSIS
Exercise exact UTC observation parsing, source path guards and actual wrapper rejection fixtures.
.DESCRIPTION
Run from the source checkout using PowerShell 7.2 or later. The suite reads the
reviewed C# formats/helper and invokes eng/verify.ps1 using the current PowerShell
installation. Synthetic inventories exist only in a unique temporary directory;
they have no executable verifier or host. No product, Windows UI or real runtime
qualification is started. The wrapper must reject inventory-local evidence before
writing and must block an incomplete synthetic package outside the inventories.
.PARAMETER OutputPath
Optional JSON report filename. Defaults to evidence/verifier-helper-validation.json in the checkout.
.EXAMPLE
pwsh -NoProfile -File tests/validate-verifier-guards.ps1 -OutputPath C:\evidence\verifier-guards.json
#>
[CmdletBinding()]
param([string]$OutputPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputPath){$OutputPath=Join-Path $root 'evidence/verifier-helper-validation.json'}
$OutputPath=[IO.Path]::GetFullPath($OutputPath)
$powerShellExecutable=(Get-Process -Id $PID).Path
$powerShellPrefix=@()
if([IO.Path]::GetFileNameWithoutExtension($powerShellExecutable) -eq 'dotnet'){$powerShellPrefix=@((Join-Path $PSHOME 'pwsh.dll'))}
$checks=[Collections.Generic.List[object]]::new()
$result='passed'
$failure=$null
function Check([bool]$Value,[string]$Name) {
    $checks.Add([ordered]@{name=$Name;result=$(if($Value){'passed'}else{'failed'})})
    if(-not $Value){throw "Failed: $Name"}
    Write-Output "PASS $Name"
}
try {
$source=[IO.File]::ReadAllText((Join-Path $root 'tests/LocalMessaging.Verify/Program.cs'))
$match=[regex]::Match($source,'(?s)DateTimeOffset\.TryParseExact\(observations\["observedAtUtc"\]\?\.GetValue<string>\(\),\s*\[(?<formats>.*?)\],CultureInfo.InvariantCulture')
Check $match.Success 'utc.formats_extracted_from_reviewed_source'
[string[]]$formats=@([regex]::Matches($match.Groups['formats'].Value,'"([^"]+)"') | ForEach-Object {$_.Groups[1].Value})
Check ($formats.Count -eq 8) 'utc.eight_explicit_formats'
$cases=@(
    @('2026-11-20T10:00:00Z',$true),
    @('2026-11-20T10:00:00.1Z',$true),
    @('2026-11-20T10:00:00.12Z',$true),
    @('2026-11-20T10:00:00.123Z',$true),
    @('2026-11-20T10:00:00.1234Z',$true),
    @('2026-11-20T10:00:00.12345Z',$true),
    @('2026-11-20T10:00:00.123456Z',$true),
    @('2026-11-20T10:00:00.1234567Z',$true),
    @('2026-11-20T10:00:00.Z',$false),
    @('2026-11-20T10:00:00.12345678Z',$false),
    @('2026-02-30T10:00:00Z',$false),
    @('2026-11-20',$false),
    @('10:00:00Z',$false),
    @('2026-11-20T10:00:00+00:00',$false),
    @('2026-11-20T10:00:00',$false),
    @('2026-11-20T10:00:00z',$false),
    @('2026-11-20T10:00:00Z ',$false)
)
foreach($case in $cases){
    $parsed=[DateTimeOffset]::MinValue
    $actual=[DateTimeOffset]::TryParseExact($case[0],$formats,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal,[ref]$parsed)
    Check ($actual -eq $case[1]) ("utc."+$case[0])
}
$method=[regex]::Match($source,'(?s)    private static bool IsWithin\(string root,string candidate\).*?(?=    private static async Task ValidateVerifierManifestAsync)').Value
Check (-not [string]::IsNullOrWhiteSpace($method)) 'path.helper_extracted_from_reviewed_source'
$pathHelperType = Add-Type ('using System; using System.IO; public static class ReviewedPathHelper'+[Guid]::NewGuid().ToString('N')+' {'+$method.Replace('private static bool','public static bool')+'}') -PassThru
$pathFixture = Join-Path ([IO.Path]::GetTempPath()) 'reviewed-path-helper'
$pathProduct = Join-Path $pathFixture 'product'
Check ($pathHelperType::IsWithin($pathProduct, $pathProduct)) 'path.equal_rejected'
Check ($pathHelperType::IsWithin($pathProduct, (Join-Path $pathProduct 'child'))) 'path.descendant_rejected'
Check ($pathHelperType::IsWithin($pathProduct+[IO.Path]::DirectorySeparatorChar, (Join-Path $pathProduct 'child/../'))) 'path.normalized_equal_rejected'
Check (-not $pathHelperType::IsWithin($pathProduct, (Join-Path $pathFixture 'product-extra/child'))) 'path.sibling_prefix_allowed'
$tokens=$null;$errors=$null
[void][Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'eng/verify.ps1'),[ref]$tokens,[ref]$errors)
Check ($errors.Count -eq 0) 'parser.verify'
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('local-messaging-verifier-review-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory((Join-Path $fixture 'product')) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $fixture 'verify')) | Out-Null
[IO.File]::WriteAllText((Join-Path $fixture 'product/manifest.json'),'{"candidateCommit":"1111111111111111111111111111111111111111"}')
function Invoke-Wrapper([string]$Evidence) {
    $info=[Diagnostics.ProcessStartInfo]::new($powerShellExecutable)
    $info.UseShellExecute=$false;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    foreach($argument in ($powerShellPrefix + @('-NoLogo','-NoProfile','-File',(Join-Path $root 'eng/verify.ps1'),'-Scope','Acceptance','-Id','a01','-AgainstPackage',(Join-Path $fixture 'product'),'-EvidencePath',$Evidence))) {$info.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($info)
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    if(-not $process.WaitForExit(20000)){$process.Kill($true);throw 'Wrapper fixture exceeded 20 seconds.'}
    $out=$stdout.GetAwaiter().GetResult();$err=$stderr.GetAwaiter().GetResult();$code=$process.ExitCode;$process.Dispose()
    return [pscustomobject]@{code=$code;stdout=$out;stderr=$err}
}
try {
    foreach($relative in @('product','product/evidence','verify','verify/evidence')) {
        $evidence=Join-Path $fixture $relative
        $fixtureResult=Invoke-Wrapper $evidence
        if($fixtureResult.code -ne 2){Write-Output ($fixtureResult | ConvertTo-Json -Depth 8)}
        Check ($fixtureResult.code -eq 2 -and $fixtureResult.stderr.Contains('Evidence must be outside')) ("wrapper.inventory_rejection."+$relative)
        Check (-not [IO.File]::Exists((Join-Path $evidence 'verification.json'))) ("wrapper.no_report_in_inventory."+$relative)
        if($relative.EndsWith('/evidence')){Check (-not [IO.Directory]::Exists($evidence)) ("wrapper.no_directory_in_inventory."+$relative)}
    }
    $allowed=Invoke-Wrapper (Join-Path $fixture 'product-extra')
    Check ($allowed.code -eq 3) 'wrapper.outside_inventory_unavailable_package_block_remains'
    Check ([IO.File]::Exists((Join-Path $fixture 'product-extra/verification.json'))) 'wrapper.outside_inventory_report_exists'
    [IO.File]::WriteAllText((Join-Path $fixture 'product/manifest.json'),'{}')
    $invalid=Invoke-Wrapper (Join-Path $fixture 'product/invalid-evidence')
    if($invalid.code -ne 2){Write-Output ($invalid | ConvertTo-Json -Depth 8)}
    Check ($invalid.code -eq 2 -and -not [IO.Directory]::Exists((Join-Path $fixture 'product/invalid-evidence'))) 'wrapper.invalid_manifest_still_protects_inventory'
}
finally {Remove-Item -LiteralPath $fixture -Recurse -Force}

} catch {
    $result='failed'
    $failure=$_.Exception.Message
    [Console]::Error.WriteLine($failure)
}
$reviewedFiles=@('tests/LocalMessaging.Verify/Program.cs','eng/verify.ps1','tests/LocalMessaging.Scenarios/PublishedQualification.cs','tests/validate-verifier-guards.ps1')
$hashes=@($reviewedFiles | ForEach-Object {[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $root $_) -Algorithm SHA256).Hash.ToLowerInvariant()}})
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
[ordered]@{
    suite='FinalVerifierSourceHelperChecks';result=$result;atUtc=[DateTime]::UtcNow.ToString('o')
    os=[Runtime.InteropServices.RuntimeInformation]::OSDescription;powershell=$PSVersionTable.PSVersion.ToString()
    runtime=[Environment]::Version.ToString();checks=$checks;failure=$failure;reviewedSourceHashes=$hashes
    limitations=@('Source-extracted .NET helpers and actual PowerShell wrapper negative fixtures only. No Windows runtime or product qualification was executed.')
} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
Write-Output "Report: $OutputPath"
if($result -ne 'passed'){exit 1}
exit 0
