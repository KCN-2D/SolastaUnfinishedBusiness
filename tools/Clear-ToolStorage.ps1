[CmdletBinding()]
param(
    [string]$PythonPath,
    [string]$EvidenceRoot,
    [string[]]$PreserveDirectory=@(),
    [ValidateRange(1,10)][int]$KeepAcceptedCatalogs=2,
    [ValidateRange(0,9223372036854775807)][long]$MaxTestBytes=40000000000,
    [switch]$Apply
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'game-test/GameTest.Common.ps1')
if(-not $PythonPath){$PythonPath=Join-Path $PSScriptRoot 'asset-authoring/workspace/Python/Scripts/python.exe'}
if(-not $EvidenceRoot){$EvidenceRoot=Join-Path $script:GameTestArtifactRoot 'ToolStorage'}
$evidence=Get-GtPath $EvidenceRoot
if(-not(Test-GtWithin $evidence $script:GameTestArtifactRoot)){throw 'Storage reports must be inside game-test/artifacts.'}
Assert-GtPlainPath $evidence
if($Apply){
    foreach($name in @('Solasta','Unity','blender')){
        $state=Get-VerificationProcessState -Name $name
        if(-not $state.querySucceeded -or $state.matches.Count){throw "Storage cleanup requires a verified stopped $name process."}
    }
}
$storageLock=Enter-GtLock
$operation=New-GtDirectory $evidence 'ToolStorage'
$freeBefore=([IO.DriveInfo]::new([IO.Path]::GetPathRoot($script:GameTestRepo))).AvailableFreeSpace
$report=[ordered]@{schemaVersion=1;kind='tool-storage';evidenceDirectory=$operation;status='running';applied=[bool]$Apply;startedUtc=[datetime]::UtcNow.ToString('O');
    availableBytesBefore=$freeBefore;availableBytesAfter=$null;availableBytesChange=$null;resourceLifecycle=$null;testCleanup=$null}
try{
    $arguments=@((Join-Path $PSScriptRoot 'asset-authoring/resource_storage.py'),'--output',$operation,'--keep-accepted',"$KeepAcceptedCatalogs")
    if($Apply){$arguments+='--apply'}
    if(Test-Path -LiteralPath $PythonPath -PathType Leaf){
        & $PythonPath @arguments *> (Join-Path $operation 'ResourceStorage.log')
        if($LASTEXITCODE -ne 0){throw 'Resource lifecycle planning failed; see ResourceStorage.log.'}
        $lifecycle=Read-GtObject (Join-Path $operation 'ResourceLifecycle.json')
    }else{
        $lifecycle=[pscustomobject]@{status='retained-no-authoring-environment';reason='The optional authoring Python environment is not installed.';plans=@()}
    }
    $report.resourceLifecycle=$lifecycle
    $cleanup=Join-Path $PSScriptRoot 'game-test/Clear-GameTestArtifacts.ps1'
    foreach($plan in $lifecycle.plans){
        if($plan.files -eq 0){continue}
        & $cleanup -ArtifactRoot $plan.root -GeneratedPayloadPlanPath $plan.plan -ReferencePath (Join-Path $operation 'RetainedInputs.json') -PreserveDirectory $PreserveDirectory -Apply:$Apply
    }
    . (Join-Path $PSScriptRoot 'asset-authoring/UnityAssetBuild/BackgroundEditor.ps1')
    $unityRoot=Join-Path $script:AssetAuthoringWorkspace 'UnityWorkspace'
    $projects=@(if(Test-Path -LiteralPath $unityRoot -PathType Container){
        Get-ChildItem -LiteralPath $unityRoot -Recurse -File -Filter storage-owner.json | ForEach-Object {
            $owner=Read-GtObject $_.FullName
            if($owner.project -and (Test-GtWithin $owner.project $unityRoot)){ $owner.project }
        } | Sort-Object -Unique
    })
    $authoringReferences=@(if(Test-Path -LiteralPath (Join-Path $script:GameTestArtifactRoot 'StorageReferences.json')){Join-Path $script:GameTestArtifactRoot 'StorageReferences.json'})
    $authoringPreserve=@($PreserveDirectory | Where-Object {Test-GtWithin $_ $script:AssetAuthoringWorkspace})
    $report['authoringCleanup']=@(foreach($project in $projects){
        Clear-AssetAuthoringJobs -Project $project -ReferencePath $authoringReferences -PreserveDirectory $authoringPreserve -KeepLatest 2 -MaxRetainedBytes 20000000000 -Apply:$Apply
    })
    $report.testCleanup=& $cleanup -ArtifactRoot $script:GameTestArtifactRoot -PruneSuperseded -MaxRetainedBytes $MaxTestBytes -KeepLatestPerCase 2 -PreserveDirectory (@($operation)+$PreserveDirectory) -Apply:$Apply
    & $cleanup -ArtifactRoot $script:GameTestArtifactRoot -PruneReports -PruneLooseFiles -PreserveDirectory (@($operation)+$PreserveDirectory) -Apply:$Apply | Out-Null
    $report.status='PASS'
}catch{
    $report.status='ERROR';$report.reason=$_.Exception.Message
    throw
}finally{
    $report.availableBytesAfter=([IO.DriveInfo]::new([IO.Path]::GetPathRoot($script:GameTestRepo))).AvailableFreeSpace
    $report.availableBytesChange=$report.availableBytesAfter-$freeBefore
    $report.completedUtc=[datetime]::UtcNow.ToString('O')
    try{Write-GtJson (Join-Path $operation 'Result.json') $report}
    finally{$storageLock.ReleaseMutex();$storageLock.Dispose()}
}
Write-Output "Tool storage report: $operation"
