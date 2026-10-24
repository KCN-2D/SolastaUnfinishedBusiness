[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameExePath, [string]$TargetCommit='HEAD', [string]$OutputPath)
. (Join-Path $PSScriptRoot 'GameTest.Common.ps1')
if ($OutputPath) {
    $OutputPath=Get-GtPath $OutputPath
    if (-not (Test-GtWithin $OutputPath $script:GameTestArtifactRoot) -or $OutputPath -eq (Get-GtPath $script:GameTestArtifactRoot)) {
        throw 'OutputPath must name a report file within tools/game-test/artifacts.'
    }
    Assert-GtPlainPath $OutputPath
}
$result = Get-GtEnvironment $GameExePath $TargetCommit
if ($OutputPath) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
    Write-GtJson $OutputPath $result
}
$result | ConvertTo-Json -Depth 20
