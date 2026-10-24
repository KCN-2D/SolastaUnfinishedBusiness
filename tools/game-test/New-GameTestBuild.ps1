[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GameExePath,
    [string]$TargetCommit='HEAD',
    [string]$EvidenceRoot
)
. (Join-Path $PSScriptRoot 'GameTest.Common.ps1')
if (-not $EvidenceRoot) { $EvidenceRoot = $script:GameTestArtifactRoot }
$directory = New-GtDirectory $EvidenceRoot 'Build'
$result = [ordered]@{ schemaVersion=1; status='ERROR'; reason='Build has not completed.'; startedUtc=[datetime]::UtcNow.ToString('O'); stage='Stage/SolastaUnfinishedBusiness'; environment=$null; manifest=@() }
try {
    $result.environment = Get-GtEnvironment $GameExePath $TargetCommit
    # The formal wrapper overrides SolastaInstallDir with its disposable reference snapshot.
    & (Join-Path $script:GameTestBuildRoot 'BuildCustomLocalPackage.cmd') *> (Join-Path $directory 'Build.log')
    if ($LASTEXITCODE -ne 0) { throw "Package build failed with exit code $LASTEXITCODE." }
    $after = Get-GtSourceIdentity $TargetCommit
    if ($after.digest -ne $result.environment.source.digest) { throw 'Source or index changed during the build. Build again after edits finish.' }
    foreach ($buildInput in $result.environment.buildInputs) { if ((Get-GtHash $buildInput.path) -ne $buildInput.sha256) { throw 'The build wrapper or runtime patch changed during the build.' } }
    $output = Join-Path $script:GameTestBuildRoot 'Output\SolastaUnfinishedBusiness'
    Assert-GtFile (Join-Path $output 'SolastaUnfinishedBusiness.dll')
    $result.manifest = @(Get-GtManifest $output)
    $stage = Join-Path $directory $result.stage
    foreach ($entry in $result.manifest) { Copy-GtFile (Join-Path $output $entry.path) (Join-Path $stage $entry.path) }
    Assert-GtManifest $stage $result.manifest
    $zip = Join-Path $script:GameTestBuildRoot 'SolastaUnfinishedBusiness.zip'
    Copy-GtFile $zip (Join-Path $directory 'SolastaUnfinishedBusiness.zip')
    $result['zipSha256'] = Get-GtHash $zip
    $result['builtUbSha256'] = Get-GtHash (Join-Path $stage 'SolastaUnfinishedBusiness.dll')
    $result['packageInfo'] = Read-GtObject (Join-Path $stage 'Info.json')
    $result.status = 'PASS'; $result.reason = 'Formal package build and staging hash verification succeeded.'
}
catch { $result.reason=$_.Exception.Message; $_ | Out-String | Set-Content -LiteralPath (Join-Path $directory 'Build-error.log') -Encoding UTF8 }
finally { $result['completedUtc']=[datetime]::UtcNow.ToString('O'); Write-GtJson (Join-Path $directory 'Build.json') $result }
Write-Host "Build directory: $directory"
if ($result.status -ne 'PASS') { throw $result.reason }
$directory
