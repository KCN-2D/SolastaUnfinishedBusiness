[CmdletBinding()]
param(
    [string]$PythonPath,
    [string]$EvidenceRoot,
    [string[]]$PreserveDirectory=@(),
    [ValidateRange(1,10)][int]$KeepAcceptedCatalogs=2,
    [ValidateRange(0,36500)][double]$MaxTestAgeDays=0,
    [ValidateRange(0,9223372036854775807)][long]$MaxTestBytes=40000000000,
    [switch]$Apply
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'game-test/GameTest.Common.ps1')

function Get-ToolAuthoringProject([string]$Root){
    if(-not[IO.Directory]::Exists($Root)){return}
    $pending=[Collections.Generic.Stack[string]]::new();$pending.Push((Get-GtPath $Root))
    while($pending.Count){
        $directory=$pending.Pop();Assert-GtPlainPath $directory
        $marker=Join-Path $directory '.solasta-asset-authoring-project'
        if([IO.File]::Exists($marker)){Assert-GtPlainPath $marker}
        if([IO.File]::Exists($marker) -and [IO.File]::ReadAllText($marker).Trim() -ceq 'SolastaUBAssetAuthoring:UnityProject:1'){
            $directory
            # Generated Assets and Library belong to this project. They cannot
            # declare additional cleanup roots inside the parent's ownership.
            continue
        }
        foreach($child in [IO.Directory]::EnumerateDirectories($directory)){
            if(([IO.File]::GetAttributes($child) -band [IO.FileAttributes]::ReparsePoint) -eq 0){$pending.Push($child)}
        }
    }
}
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
    availableBytesBefore=$freeBefore;availableBytesAfter=$null;availableBytesChange=$null;resourceLifecycle=$null;resourceCleanup=@();testCleanup=$null}
try{
    $arguments=@((Join-Path $PSScriptRoot 'asset-authoring/resource_storage.py'),'--output',$operation,'--keep-accepted',"$KeepAcceptedCatalogs")
    foreach($directory in $PreserveDirectory){
        $arguments+=@('--retain-directory',(Get-GtPath $directory))
    }
    if($Apply){$arguments+='--apply'}
    if(Test-Path -LiteralPath $PythonPath -PathType Leaf){
        $previousErrorPreference=$ErrorActionPreference
        try{
            $ErrorActionPreference='Continue'
            & $PythonPath @arguments *> (Join-Path $operation 'ResourceStorage.log')
            $lifecycleExitCode=$LASTEXITCODE
        }finally{$ErrorActionPreference=$previousErrorPreference}
        if($lifecycleExitCode -ne 0){throw 'Resource lifecycle planning failed; see ResourceStorage.log.'}
        $lifecycle=Read-GtObject (Join-Path $operation 'ResourceLifecycle.json')
    }else{
        $lifecycle=[pscustomobject]@{status='retained-no-authoring-environment';reason='The optional authoring Python environment is not installed.';plans=@()}
    }
    $report.resourceLifecycle=$lifecycle
    $cleanup=Join-Path $PSScriptRoot 'game-test/Clear-GameTestArtifacts.ps1'
    foreach($plan in $lifecycle.plans){
        if($plan.files -eq 0){continue}
        $report.resourceCleanup+=& $cleanup -ArtifactRoot $plan.root -GeneratedPayloadPlanPath $plan.plan -ReferencePath (Join-Path $operation 'RetainedInputs.json') -PreserveDirectory $PreserveDirectory -Apply:$Apply -PassThru
    }
    . (Join-Path $PSScriptRoot 'asset-authoring/UnityAssetBuild/BackgroundEditor.ps1')
    $unityRoot=Join-Path $script:AssetAuthoringWorkspace 'UnityWorkspace'
    $projects=@(Get-ToolAuthoringProject $unityRoot | Sort-Object -Unique)
    $authoringReferences=@(if(Test-Path -LiteralPath (Join-Path $script:GameTestArtifactRoot 'StorageReferences.json')){Join-Path $script:GameTestArtifactRoot 'StorageReferences.json'})
    $authoringPreserve=@($PreserveDirectory | Where-Object {Test-GtWithin $_ $script:AssetAuthoringWorkspace})
    $report['authoringCleanup']=@(if($projects.Count){
        $operationPaths=Clear-AssetAuthoringJobs -Project $projects -ReferencePath $authoringReferences -PreserveDirectory $authoringPreserve -KeepLatest 2 -MaxRetainedBytes 20000000000 -Apply:$Apply
        foreach($path in $operationPaths){
            $result=Read-GtObject $path
            [pscustomobject]@{path=$path;sha256=Get-GtHash $path;status=$result.status;entries=$result.actions.Count;
                logicalBytes=[long]($result.actions | Measure-Object bytes -Sum | ForEach-Object {$_.Sum});retainedBytes=$result.retainedBytes;budgetSatisfied=$result.budgetSatisfied}
        }
    })
    $report.testCleanup=& $cleanup -ArtifactRoot $script:GameTestArtifactRoot -PruneSuperseded -MaxAgeDays $MaxTestAgeDays -MaxRetainedBytes $MaxTestBytes -KeepLatestPerCase 2 -PreserveDirectory (@($operation)+$PreserveDirectory) -Apply:$Apply -PassThru
    $report['metadataCleanup']=& $cleanup -ArtifactRoot $script:GameTestArtifactRoot -PruneReports -PruneLooseFiles -PreserveDirectory (@($operation)+$PreserveDirectory) -Apply:$Apply -PassThru
    if(Test-Path -LiteralPath $PythonPath -PathType Leaf){
        $previousErrorPreference=$ErrorActionPreference
        try{
            $ErrorActionPreference='Continue'
            & $PythonPath (Join-Path $PSScriptRoot 'asset-authoring/resource_storage.py') --output $operation --inventory-only *> (Join-Path $operation 'StorageInventory.log')
            $inventoryExitCode=$LASTEXITCODE
        }finally{$ErrorActionPreference=$previousErrorPreference}
        if($inventoryExitCode -ne 0){throw 'Tool storage inventory failed; see StorageInventory.log.'}
        $report['toolInventory']=Join-Path $operation 'StorageInventory.json'
    }
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
