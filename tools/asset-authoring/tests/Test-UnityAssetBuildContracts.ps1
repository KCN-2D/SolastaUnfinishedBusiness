[CmdletBinding(DefaultParameterSetName = 'All')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'All')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Background')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Materials')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Meshes')]
    [Parameter(Mandatory = $true, ParameterSetName = 'CliTimeout')]
    [Parameter(Mandatory = $true, ParameterSetName = 'CallerStop')] [string] $UnityPath,
    [Parameter(Mandatory = $true, ParameterSetName = 'All')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Background')] [string] $FbxPath,
    [Parameter(Mandatory = $true, ParameterSetName = 'All')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Background')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Materials')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Meshes')]
    [Parameter(Mandatory = $true, ParameterSetName = 'CliTimeout')]
    [Parameter(Mandatory = $true, ParameterSetName = 'CallerStop')] [string] $GuiProjectPath,
    [Parameter(ParameterSetName = 'All')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Materials')] [string] $MaterialRecipePath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Materials')] [switch] $MaterialsOnly,
    [Parameter(Mandatory = $true, ParameterSetName = 'Meshes')] [string] $MeshRecipePath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Meshes')] [switch] $MeshesOnly,
    [string] $WorkspaceRoot,
    [Parameter(Mandatory = $true, ParameterSetName = 'Background')] [switch] $BackgroundEditorOnly,
    [Parameter(Mandatory = $true, ParameterSetName = 'CliTimeout')] [switch] $CliTimeoutOnly,
    [Parameter(Mandatory = $true, ParameterSetName = 'CallerStop')] [switch] $CallerStopOnly,
    [Parameter(Mandatory = $true, ParameterSetName = 'Storage')] [switch] $StorageOnly,
    [Parameter(ParameterSetName = 'Storage')] [string] $StorageEvidenceRoot,
    [string] $UnityCliPath,
    [switch] $InstallGuiWorker,
    [ValidateRange(5, 300)] [int] $TimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Invoke-AuthoringStorageContracts([string]$OutputDirectory) {
    $candidate=Join-Path $OutputDirectory 'Sources/tools/asset-authoring'
    foreach($relative in @('UnityAssetBuild/BackgroundEditor.ps1','Invoke-UnityAssetBuild.ps1','Invoke-AssetAuthoring.ps1')) {
        $destination=Join-Path $candidate $relative
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
        [IO.File]::Copy((Join-Path (Join-Path $PSScriptRoot '..') $relative),$destination)
    }
$sourcePaths=@((Join-Path $candidate 'UnityAssetBuild/BackgroundEditor.ps1'),(Join-Path $candidate 'Invoke-UnityAssetBuild.ps1'),(Join-Path $candidate 'Invoke-AssetAuthoring.ps1'))
$sourceHashes=@($sourcePaths|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})
. (Join-Path $candidate 'UnityAssetBuild/BackgroundEditor.ps1')
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $candidate 'Invoke-AssetAuthoring.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'The authoring source did not parse.'}
$function=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Clear-BlenderAuthoringOutputs'},$true))
. ([scriptblock]::Create($function[0].Extent.Text))
$workbench=Join-Path $OutputDirectory 'workspace';[void][IO.Directory]::CreateDirectory($workbench)
$script:BlenderProcesses=@();$script:BlenderQueryFails=$false;$script:ProcessQueries=0
function Get-CimInstance {param($ClassName,$Filter,$Property)$script:ProcessQueries++;if($script:BlenderQueryFails){throw 'fixture process query failed'};return $script:BlenderProcesses}
$checks=[Collections.Generic.List[object]]::new()
function Need([string]$Name,[bool]$Passed){$checks.Add([ordered]@{name=$Name;passed=$Passed});if(-not $Passed){Write-Host "FAIL $Name"}}
function Rejected([scriptblock]$Action){try{&$Action|Out-Null;return $false}catch{return $true}}
function RejectedAs([scriptblock]$Action,[string]$Pattern){try{&$Action|Out-Null;return $false}catch{return $_.Exception.Message -match $Pattern}}
function Save([string]$Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))}
function Directory([string]$Path){[void][IO.Directory]::CreateDirectory($Path);return $Path}
function Project([string]$Name){
 $root=Directory (Join-Path $workbench $Name);[IO.File]::WriteAllText((Join-Path $root '.solasta-asset-authoring-workspace'),'SolastaUBAssetAuthoring:UnityWorkspace:1')
 $project=Directory (Join-Path $root 'Project');[IO.File]::WriteAllText((Join-Path $project '.solasta-asset-authoring-project'),'SolastaUBAssetAuthoring:UnityProject:1');[void](Directory (Join-Path $project 'Assets'));return $project
}
function Job([string]$Project,[string]$Name,[string[]]$Deps=@(),[bool]$Seal=$true,$Session=$null){
 $job=Directory (Join-Path $Project ('.asset-authoring-jobs/'+$Name));$assets=Directory (Join-Path $Project ('Assets/Generated/'+$Name))
 [IO.File]::WriteAllBytes((Join-Path $assets 'mesh.asset'),[byte[]]::new(2048));[IO.File]::WriteAllText(($assets+'.meta'),'guid: '+[guid]::NewGuid().ToString('N'))
 $result=Join-Path $job 'output.json';Save $result @{success=$true}
 $ownership=Register-AssetAuthoringJob -Project $Project -JobDirectory $job -AssetDirectory $assets -ResultPath $result -DependencyPath $Deps -Session $Session
 if($Seal){Complete-AssetAuthoringJob $ownership $true;$data=Get-Content -LiteralPath $ownership.path -Raw|ConvertFrom-Json;$data.completedUtc=[DateTime]::UtcNow.AddDays(-8).ToString('O');Save $ownership.path $data}
 return [pscustomobject]@{job=$job;assets=$assets;ownership=$ownership}
}
function Session([string]$Project){
 $id=[guid]::NewGuid().ToString('N');$path=Directory (Join-Path $Project ('.asset-authoring-jobs/BackgroundSessions/'+$id))
 Save (Join-Path $path 'configuration.json') @{ownerId=$id;project=$Project}
 Save (Join-Path $path 'session.json') @{ownerId=$id;status='complete';editor=$null;cli=$null;cliChildren=@();editorReleased=$true;cliReleased=$true;cliChildrenReleased=$true;error=$null}
 [IO.File]::WriteAllText((Join-Path $path 'Editor.log'),'native editor diagnostic')
 return [pscustomobject]@{RunDirectory=$path;OwnerId=$id}
}
function HoldOperation([string]$Scope){
 $id=[guid]::NewGuid().ToString('N');$script=Join-Path $OutputDirectory ($id+'.ps1');$ready=Join-Path $OutputDirectory ($id+'.ready');$release=Join-Path $OutputDirectory ($id+'.release')
 [IO.File]::WriteAllText($script,@'
param($Helper,$Scope,$Ready,$Release)
. $Helper
$operation=Enter-AssetStorageOperation $Scope
try{[IO.File]::WriteAllText($Ready,'ready');while(-not [IO.File]::Exists($Release)){Start-Sleep -Milliseconds 20}}finally{$operation.ReleaseMutex();$operation.Dispose()}
'@)
 $exe=(Get-Process -Id $PID).Path;$helper=Join-Path $candidate 'UnityAssetBuild/BackgroundEditor.ps1'
 $line=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$script,$helper,$Scope,$ready,$release)|ForEach-Object{'"'+$_+'"'}
 $process=Start-Process -FilePath $exe -ArgumentList ($line -join ' ') -WindowStyle Hidden -PassThru
 $deadline=[DateTime]::UtcNow.AddSeconds(10);while(-not [IO.File]::Exists($ready)-and -not $process.HasExited -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 20}
 if(-not [IO.File]::Exists($ready)){throw 'Fixture storage lease did not start.'}
 return [pscustomobject]@{process=$process;release=$release}
}
function ReleaseOperation($Lease){[IO.File]::WriteAllText($Lease.release,'release');if(-not $Lease.process.WaitForExit(10000)){$Lease.process.Kill();throw 'Fixture storage lease did not exit.'};$Lease.process.Dispose()}
function UnityClean([string]$Project,[hashtable]$Options=@{},[switch]$Apply){
 $args=@{Project=$Project;MaxAgeDays=1;Apply=[bool]$Apply};foreach($key in $Options.Keys){$args[$key]=$Options[$key]}
 $path=Clear-AssetAuthoringJobs @args;return Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
}
function BlenderRun([string]$Root,[string]$Name,[string]$Status='passed',[string[]]$Dependencies=@()){
 $path=Directory (Join-Path $Root $Name);$inputs=Directory (Join-Path $path 'InputSources');$archive=Join-Path $inputs 'source.py';[IO.File]::WriteAllText($archive,'# source snapshot')
 $mesh=Join-Path $path 'mesh.obj';[IO.File]::WriteAllBytes($mesh,[byte[]]::new(2048))
 $archiveHash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
 $data=@{schemaVersion=1;status=$Status;output=$path;startedUtc=[DateTime]::UtcNow.AddDays(-9).ToString('O');completedUtc=[DateTime]::UtcNow.AddDays(-8).ToString('O');
  pipeline_sha256=$archiveHash;input_archive=@(@{source='E:\dummy-source\source.py';archive=$archive;sha256=$archiveHash});
  artifacts_sha256=@(@{path='InputSources/source.py';sha256=$archiveHash},@{path='mesh.obj';sha256=(Get-FileHash -LiteralPath $mesh -Algorithm SHA256).Hash});dependencies=$Dependencies}
 Save (Join-Path $path 'run.json') $data;return $path
}
function BlenderClean([string]$Root,[hashtable]$Options=@{},[switch]$Apply){
 $args=@{Root=$Root;ReferenceFiles=@();Preserve=@();Latest=2;Age=1;Budget=0;Delete=[bool]$Apply};foreach($key in $Options.Keys){if($key -eq 'References'){$args.ReferenceFiles=$Options[$key]}else{$args[$key]=$Options[$key]}}
 $path=Clear-BlenderAuthoringOutputs @args;return Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
}

$project=Project 'unity-preview';$job=Job $project 'first'
$r=UnityClean $project
Need 'Unity dry run keeps output and paired Assets' ((Test-Path $job.job)-and(Test-Path $job.assets)-and $r.actions.Count -eq 1 -and -not $r.actions[0].applied)
$r=UnityClean $project -Apply
Need 'Unity age cleanup retires job, assets and sibling meta together' (-not(Test-Path $job.job)-and -not(Test-Path $job.assets)-and -not(Test-Path ($job.assets+'.meta')))
$r=UnityClean $project -Apply;Need 'Unity cleanup is idempotent' ($r.actions.Count -eq 0)
Need 'durable Unity diagnostic survives retirement' ((Get-ChildItem (Join-Path (Split-Path $project) 'StorageCleanup') -Filter Journal.jsonl -Recurse -File).Count -ge 1)
$project=Project 'unity-preserve';$job=Job $project 'hard';$ref=Join-Path $OutputDirectory 'references.json';Save $ref @{schemaVersion=1;paths=@($job.assets)}
$r=UnityClean $project @{ReferencePath=@($ref);MaxRetainedBytes=1} -Apply
Need 'current referenced Unity assets keep their paired job above byte budget' ((Test-Path $job.job)-and(Test-Path $job.assets)-and -not $r.budgetSatisfied)
$project=Project 'unity-running';$job=Job $project 'running' @() $false
$r=UnityClean $project -Apply
Need 'incomplete Unity output is preserved' ((Test-Path $job.job)-and(Test-Path $job.assets)-and $r.actions.Count -eq 0)
$project=Project 'unity-modified';$job=Job $project 'edited';[IO.File]::AppendAllText((Join-Path $job.assets 'mesh.asset'),'changed')
$r=UnityClean $project
Need 'modified Unity output is retained' ((Test-Path $job.job)-and $r.actions.Count -eq 0 -and $r.retained.Count -gt 0)
$project=Project 'unity-unmarked';[IO.File]::WriteAllText((Join-Path $project '.solasta-asset-authoring-project'),'wrong')
Need 'unmarked Unity project is rejected' (Rejected {UnityClean $project -Apply})
$project=Project 'unity-protected';$outside=Directory (Join-Path $OutputDirectory 'outside')
Need 'job outside registered project is rejected' (Rejected {Register-AssetAuthoringJob $project $outside (Join-Path $project 'Assets/Generated') (Join-Path $outside 'output.json')})
Need 'Assets root is protected for persistent projects' (Rejected {Register-AssetAuthoringJob $project (Join-Path $project '.asset-authoring-jobs/foo') (Join-Path $project 'Assets') (Join-Path $project '.asset-authoring-jobs/foo/result.json')})
Need 'Editor source folder is protected' (Rejected {Register-AssetAuthoringJob $project (Join-Path $project '.asset-authoring-jobs/foo') (Join-Path $project 'Assets/Editor') (Join-Path $project '.asset-authoring-jobs/foo/result.json')})
$project=Project 'unity-locked';[void](Directory (Join-Path $project 'Temp'));$lock=[IO.File]::Open((Join-Path $project 'Temp/UnityLockfile'),[IO.FileMode]::Create,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{Need 'an open native project lock blocks cleanup' (Rejected {UnityClean $project -Apply})}finally{$lock.Dispose()}

$project=Project 'unity-chain';$donor=Job $project 'old-donor';$consumer=Job $project 'new-consumer' @((Join-Path $donor.assets 'mesh.asset'))
$r=UnityClean $project
Need 'Unity preview retires consumers before their input owners' ($r.actions.Count -eq 2 -and $r.actions[0].jobDirectory -eq $consumer.job -and $r.actions[1].jobDirectory -eq $donor.job)
$r=UnityClean $project -Apply
Need 'Unity consumer and obsolete donor retire in dependency order' (-not(Test-Path $consumer.job)-and -not(Test-Path $donor.job)-and $r.actions[0].jobDirectory -eq $consumer.job)
$project=Project 'unity-hard-chain';$donor=Job $project 'old-donor';$consumer=Job $project 'new-consumer' @((Join-Path $donor.assets 'mesh.asset'))
Save $ref @{schemaVersion=1;paths=@($consumer.job)};$r=UnityClean $project @{ReferencePath=@($ref);MaxRetainedBytes=1} -Apply
Need 'referenced Unity consumer keeps its source job and assets' ((Test-Path $consumer.job)-and(Test-Path $donor.job)-and(Test-Path $donor.assets)-and -not $r.budgetSatisfied)
$project=Project 'unity-running-chain';$donor=Job $project 'old-donor';$consumer=Job $project 'pending' @((Join-Path $donor.assets 'mesh.asset')) $false
$r=UnityClean $project -Apply
Need 'incomplete Unity consumer keeps its declared donor' ((Test-Path $consumer.job)-and(Test-Path $donor.job))
$project=Project 'unity-failure-chain';$donor=Job $project 'old-donor';$consumer=Job $project 'changed' @((Join-Path $donor.assets 'mesh.asset'));[IO.File]::AppendAllText((Join-Path $consumer.assets 'mesh.asset'),'changed')
Need 'Unity consumer validation failure preserves donor' ((Rejected {UnityClean $project -Apply})-and(Test-Path $consumer.job)-and(Test-Path $donor.job))
$project=Project 'unity-missing-file';$job=Job $project 'edited';Remove-Item -LiteralPath (Join-Path $job.assets 'mesh.asset')
$r=UnityClean $project
Need 'a missing file inside a surviving Unity output is not ignored' ((Test-Path $job.job)-and $r.actions.Count -eq 0)
$project=Project 'unity-overlap';$job=Job $project 'first';$other=Directory (Join-Path $project '.asset-authoring-jobs/second')
Need 'two jobs cannot claim the same asset directory' (Rejected {Register-AssetAuthoringJob -Project $project -JobDirectory $other -AssetDirectory $job.assets -ResultPath (Join-Path $other 'result.json')})
$project=Project 'unity-tampered-owner';$job=Job $project 'edited' @() $false;$data=Get-Content $job.ownership.path -Raw|ConvertFrom-Json;$data.assetDirectory=Join-Path $OutputDirectory 'outside-assets';Save $job.ownership.path $data
Need 'completion rejects altered out-of-scope asset ownership' (Rejected {Complete-AssetAuthoringJob $job.ownership $true})
$project=Project 'unity-live-creator';$job=Job $project 'complete'
$r=UnityClean $project -Apply
Need 'a completed job is not pinned by its still-running caller shell' (-not(Test-Path $job.job))
$project=Project 'unity-session';$session=Session $project;$job=Job $project 'ended' @() $true $session
$r=UnityClean $project -Apply
Need 'closed background session retires after its last job' (-not(Test-Path $job.job)-and -not(Test-Path $session.RunDirectory)-and $r.actions.Count -eq 2 -and $r.actions[0].jobDirectory -eq $job.job)
$project=Project 'unity-session-kept';$session=Session $project;$job=Job $project 'current' @() $true $session;Save $ref @{schemaVersion=1;paths=@($job.assets)}
$r=UnityClean $project @{ReferencePath=@($ref)} -Apply
Need 'current job keeps its background diagnostics' ((Test-Path $job.job)-and(Test-Path $session.RunDirectory))
$project=Project 'unity-session-shared';$session=Session $project;$one=Job $project 'one' @() $true $session;$two=Job $project 'two' @() $true $session;Save $ref @{schemaVersion=1;paths=@($two.job)}
$r=UnityClean $project @{ReferencePath=@($ref)} -Apply
Need 'shared background session survives while any consumer remains' (-not(Test-Path $one.job)-and(Test-Path $two.job)-and(Test-Path $session.RunDirectory))
$project=Project 'unity-session-active';$session=Session $project;$process=Get-Process -Id $PID
Save (Join-Path $session.RunDirectory 'session.json') @{ownerId=$session.OwnerId;status='ready';editor=@{pid=$PID;path=$process.Path;startTimeUtc=$process.StartTime.ToUniversalTime().ToString('O')};cli=$null;cliChildren=@();editorReleased=$false;cliReleased=$true;cliChildrenReleased=$true;error=$null}
$r=UnityClean $project -Apply
Need 'actual matching session process keeps background output' ((Test-Path $session.RunDirectory)-and $r.actions.Count -eq 0)
$project=Project 'unity-disposable';$workspace=Split-Path $project;$jobRoot=Directory (Join-Path $workspace 'disposable');$disposable=Directory (Join-Path $jobRoot 'Project');[IO.File]::WriteAllText((Join-Path $disposable '.solasta-asset-authoring-project'),'SolastaUBAssetAuthoring:UnityProject:1');$assets=Directory (Join-Path $disposable 'Assets');[IO.File]::WriteAllText((Join-Path $assets 'mesh.asset'),'mesh');$result=Join-Path $jobRoot 'result.json';Save $result @{success=$true}
$owner=Register-AssetAuthoringJob -Project $disposable -JobDirectory $jobRoot -AssetDirectory $assets -ResultPath $result -DisposableProject;Complete-AssetAuthoringJob $owner $true;$data=Get-Content $owner.path -Raw|ConvertFrom-Json;$data.completedUtc=[DateTime]::UtcNow.AddDays(-8).ToString('O');Save $owner.path $data
$r=UnityClean $disposable -Apply
Need 'generated disposable project retires as one owned output' (-not(Test-Path $jobRoot))
$project=Project 'unity-public-entry';$job=Job $project 'ended';$path=& (Join-Path $candidate 'Invoke-UnityAssetBuild.ps1') -Cleanup -GuiProjectPath $project -MaxAgeDays 1
$r=Get-Content $path -Raw|ConvertFrom-Json
Need 'Unity public cleanup entry needs no Editor executable or launch' ((Test-Path $job.job)-and $r.status -eq 'complete'-and -not $r.applied)

$root=Directory (Join-Path $workbench 'BlenderPreview');$run=BlenderRun $root 'first';$r=BlenderClean $root
Need 'Blender dry run retains output' ((Test-Path $run)-and $r.actions.Count -eq 1 -and -not $r.actions[0].applied)
$r=BlenderClean $root -Apply;Need 'completed signed Blender output expires' (-not(Test-Path $run))
$root=Directory (Join-Path $workbench 'BlenderFailed');$run=BlenderRun $root 'failure' 'failed';$r=BlenderClean $root -Apply
Need 'failed but completed signed Blender output can expire' (-not(Test-Path $run))
$root=Directory (Join-Path $workbench 'BlenderPointer');$run=BlenderRun $root 'current';$pointer=Join-Path $root 'last-success.json';Save $pointer @{status='passed';output=$run};Save $ref @{schemaVersion=1;paths=@($pointer)};$r=BlenderClean $root @{Budget=1;References=@($ref)} -Apply
Need 'explicit current success pointer protects its target above budget' ((Test-Path $run)-and(Test-Path $pointer)-and -not $r.budgetSatisfied)
$root=Directory (Join-Path $workbench 'BlenderRunning');$run=BlenderRun $root 'running' 'running';$r=BlenderClean $root -Apply
Need 'incomplete Blender output is preserved' ((Test-Path $run)-and $r.actions.Count -eq 0)
$root=Directory (Join-Path $workbench 'BlenderActive');$run=BlenderRun $root 'active';$script:BlenderProcesses=@([pscustomobject]@{ProcessId=123;ExecutablePath='E:\tools\blender.exe';CommandLine=('blender --output "'+$run+'"')});$r=BlenderClean $root -Apply
Need 'actual matching Blender output process prevents retirement' ((Test-Path $run)-and $r.actions.Count -eq 0)
$script:BlenderProcesses=@();$script:BlenderQueryFails=$true;$r=BlenderClean $root -Apply
Need 'unknown Blender process query preserves output' ((Test-Path $run)-and $r.actions.Count -eq 0)
$script:BlenderQueryFails=$false
$root=Directory (Join-Path $workbench 'BlenderChanged');$run=BlenderRun $root 'edited';[IO.File]::AppendAllText((Join-Path $run 'mesh.obj'),'edited');$r=BlenderClean $root
Need 'changed Blender output is retained' ((Test-Path $run)-and $r.actions.Count -eq 0)
$root=Directory (Join-Path $workbench 'BlenderUnexpected');$run=BlenderRun $root 'edited';[IO.File]::WriteAllText((Join-Path $run 'user-note.txt'),'keep');$r=BlenderClean $root
Need 'undeclared files prevent Blender retirement' ((Test-Path $run)-and $r.actions.Count -eq 0)
Need 'tool/software folders cannot be Blender cleanup roots' (Rejected {BlenderClean (Directory (Join-Path $workbench 'Tools/Output')) -Apply})
Need 'authoring workspace root itself cannot be removed' (Rejected {BlenderClean $workbench -Apply})
$root=Directory (Join-Path $workbench 'BlenderChain');$donor=BlenderRun $root 'donor';$consumer=BlenderRun $root 'consumer' 'passed' @((Join-Path $donor 'mesh.obj'))
$r=BlenderClean $root
Need 'Blender preview plans consumer before its donor' ($r.actions.Count -eq 2 -and $r.actions[0].path -eq $consumer -and $r.actions[1].path -eq $donor)
$r=BlenderClean $root -Apply
Need 'obsolete Blender dependency chain retires without orphaned consumer' (-not(Test-Path $consumer)-and -not(Test-Path $donor))
$root=Directory (Join-Path $workbench 'BlenderHardChain');$donor=BlenderRun $root 'donor';$consumer=BlenderRun $root 'consumer' 'passed' @((Join-Path $donor 'mesh.obj'));Save $ref @{schemaVersion=1;paths=@($consumer)}
$r=BlenderClean $root @{References=@($ref);Budget=1} -Apply
Need 'current Blender consumer retains native input output chain' ((Test-Path $consumer)-and(Test-Path $donor)-and -not $r.budgetSatisfied)
$root=Directory (Join-Path $workbench 'BlenderRunningChain');$donor=BlenderRun $root 'donor';$consumer=BlenderRun $root 'pending' 'running' @((Join-Path $donor 'mesh.obj'));$r=BlenderClean $root -Apply
Need 'incomplete Blender consumer preserves declared donor' ((Test-Path $consumer)-and(Test-Path $donor))
$root=Directory (Join-Path $workbench 'BlenderFailureChain');$donor=BlenderRun $root 'donor';$consumer=BlenderRun $root 'bad-consumer' 'passed' @((Join-Path $donor 'mesh.obj'));[IO.File]::AppendAllText((Join-Path $consumer 'mesh.obj'),'changed')
Need 'Blender validation failure preserves its input owner' ((Rejected {BlenderClean $root -Apply})-and(Test-Path $consumer)-and(Test-Path $donor))
$publicRoot=Directory (Join-Path $candidate ('workspace/PublicStorageContract-'+[guid]::NewGuid().ToString('N')));$run=BlenderRun $publicRoot 'ended'
$path=& (Join-Path $candidate 'Invoke-AssetAuthoring.ps1') -Cleanup -OutputRoot $publicRoot -MaxAgeDays 1
$r=Get-Content $path -Raw|ConvertFrom-Json
Need 'Blender public cleanup entry needs no Blender executable or launch' ((Test-Path $run)-and $r.status -eq 'complete'-and -not $r.applied)
$project=Project 'unity-operation-lock';$job=Job $project 'pending' @() $false;$lease=HoldOperation $project
try{
 Need 'another process cannot register a Unity consumer during cleanup lease' (RejectedAs {Register-AssetAuthoringJob -Project $project -JobDirectory (Join-Path $project '.asset-authoring-jobs/other') -AssetDirectory (Join-Path $project 'Assets/Generated/other') -ResultPath (Join-Path $project '.asset-authoring-jobs/other/result.json')} 'Another authoring operation owns this storage scope')
 Need 'another process cannot complete a Unity producer during the same lease' (RejectedAs {Complete-AssetAuthoringJob $job.ownership $true} 'Another authoring operation owns this storage scope')
 Need 'Unity cleanup cannot pass an active producer storage lease' ((RejectedAs {UnityClean $project -Apply} 'Another authoring operation owns this storage scope')-and(Test-Path $job.job))
 if($PSVersionTable.PSEdition -eq 'Core'){
  Need 'Editor startup obeys the same project lease before any launch' (RejectedAs {Start-AssetBackgroundEditor $project (Get-Process -Id $PID).Path (Get-Process -Id $PID).Path ('0'*64) 5} 'Another authoring operation owns this storage scope')
 }else{Need 'PS5 refuses unsupported background startup before any launch' (RejectedAs {Start-AssetBackgroundEditor $project (Get-Process -Id $PID).Path (Get-Process -Id $PID).Path ('0'*64) 5} 'requires PowerShell 7')}
}finally{ReleaseOperation $lease}
Complete-AssetAuthoringJob $job.ownership $true
Need 'completion works after the competing lease is released' ((Get-Content $job.ownership.path -Raw|ConvertFrom-Json).state -eq 'completed')
$root=Directory (Join-Path $workbench 'BlenderOperation');$run=BlenderRun $root 'existing';$lease=HoldOperation $root
try{Need 'Blender cleanup cannot race a producer output-root lease' ((RejectedAs {BlenderClean $root -Apply} 'Another authoring operation owns this storage scope')-and(Test-Path $run))}finally{ReleaseOperation $lease}
$r=BlenderClean $root -Apply
Need 'Blender output can retire after its producer lease ends' (-not(Test-Path $run))
$root=Directory (Join-Path $workbench 'BlenderPointerNames');$old=@();foreach($name in @('pilot','reviewed','corrected','tangent-fixed')){$run=BlenderRun $root $name;Save (Join-Path $root ('last-'+$name+'-success.json')) @{status='passed';output=$run};$old+= $run}
$r=BlenderClean $root @{Budget=1} -Apply
Need 'unique success pointer names cannot permanently pin obsolete outputs' (@($old|Where-Object{Test-Path $_}).Count -eq 0 -and @(Get-ChildItem $root -Filter 'last*success.json' -File).Count -eq 0)
$root=Directory (Join-Path $workbench 'BlenderPointerRetained');$run=BlenderRun $root 'current';$pointer=Join-Path $root 'last-current-success.json';Save $pointer @{status='passed';output=$run};Save $ref @{schemaVersion=1;paths=@($run)};$r=BlenderClean $root @{References=@($ref);Budget=1} -Apply
Need 'an explicitly retained target keeps its valid success pointer' ((Test-Path $run)-and(Test-Path $pointer))
$installation=Directory (Join-Path $workbench 'InstalledGame')
[IO.File]::WriteAllBytes((Join-Path $installation 'Solasta.exe'),[byte[]](0))
$installedInput=Join-Path (Directory (Join-Path $installation 'Data')) 'native.bundle'
[IO.File]::WriteAllBytes($installedInput,[byte[]](7,8,9,10))
$installedHash=(Get-FileHash -LiteralPath $installedInput -Algorithm SHA256).Hash
Need 'read-only dependencies may identify installed native inputs' ((Get-AssetStoragePath $installedInput -ReadOnlyReference) -eq $installedInput)
Need 'installed inputs remain forbidden owned storage roots' (Rejected {Get-AssetStoragePath $installedInput})
Need 'read-only references still reject network paths and drive roots' ((Rejected {Get-AssetStoragePath '\\dummy-host\share\input.bundle' -ReadOnlyReference}) -and (Rejected {Get-AssetStoragePath ([IO.Path]::GetPathRoot($installation)) -ReadOnlyReference}))
$project=Project 'unity-read-only-input'
$job=Job $project 'consumer' @($installedInput)
$record=Get-Content -LiteralPath $job.ownership.path -Raw|ConvertFrom-Json
Need 'Unity registration and completion retain installed read-only dependency identity' ($record.state -eq 'completed' -and $record.dependencies.Count -eq 1 -and $record.dependencies[0] -eq $installedInput)
$r=UnityClean $project -Apply
Need 'Unity retirement never promotes installed dependencies to deletion candidates' (-not(Test-Path $job.job) -and (Get-FileHash -LiteralPath $installedInput -Algorithm SHA256).Hash -eq $installedHash -and @($r.actions|Where-Object{(Test-AssetStorageWithin $_.jobDirectory $installation) -or (Test-AssetStorageWithin $_.assetDirectory $installation)}).Count -eq 0)
Need 'Unity owned job and asset paths cannot target an installation' ((Rejected {Register-AssetAuthoringJob -Project $project -JobDirectory (Join-Path $installation 'owned') -AssetDirectory (Join-Path $project 'Assets/Generated/invalid-job') -ResultPath (Join-Path $installation 'owned/result.json')}) -and
    (Rejected {Register-AssetAuthoringJob -Project $project -JobDirectory (Join-Path $project '.asset-authoring-jobs/invalid-assets') -AssetDirectory (Join-Path $installation 'Assets') -ResultPath (Join-Path $project '.asset-authoring-jobs/invalid-assets/result.json')}))
$installedReference=Join-Path $installation 'references.json';Save $installedReference @{schemaVersion=1;paths=@('Data/native.bundle')}
$references=Get-AssetStorageReferences @($installedReference);Assert-AssetStorageReferences $references
Need 'reference manifests and entries may be read from an installation' ($references.paths.Contains($installedInput) -and $references.paths.Contains($installedReference))
$run=@{};$DependencyPath=@($installedInput);$declaredSources=@()
$assignment=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq '$run[''dependencies'']'},$true))
if($assignment.Count -ne 1){throw 'The normal Blender dependency assignment is not unique.'}
. ([scriptblock]::Create($assignment[0].Extent.Text))
Need 'normal Blender dependency registration admits installed read-only inputs' ($run.dependencies.Count -eq 1 -and $run.dependencies[0] -eq $installedInput)
$sourceAssignment=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq '$declaredSources'},$true))
if($sourceAssignment.Count -ne 1){throw 'The declared authoring source resolution is not unique.'}
$SourcePath=@($installedInput,$installedInput)
. ([scriptblock]::Create($sourceAssignment[0].Extent.Text))
Need 'declared authoring sources admit read-only files and normalize repeated paths' ($declaredSources.Count -eq 1 -and $declaredSources[0] -eq $installedInput)
$SourcePath=@($installation)
Need 'declared authoring sources reject directories' (Rejected {. ([scriptblock]::Create($sourceAssignment[0].Extent.Text))})
$SourcePath=@((Join-Path $installation 'missing.py'))
Need 'declared authoring sources reject missing files' (Rejected {. ([scriptblock]::Create($sourceAssignment[0].Extent.Text))})
$SourcePath=@($installedReference)
. ([scriptblock]::Create($sourceAssignment[0].Extent.Text))
. ([scriptblock]::Create($assignment[0].Extent.Text))
Need 'declared execution sources also retain their cleanup dependency' ($run.dependencies.Count -eq 2 -and $installedReference -in $run.dependencies -and $installedInput -in $run.dependencies)

$root=Directory (Join-Path $workbench 'BlenderReadOnlyInput');$output=BlenderRun $root 'consumer' 'passed' @($installedInput);$r=BlenderClean $root -Apply
Need 'Blender retirement leaves installed dependencies unchanged and outside candidate roots' (-not(Test-Path $output) -and (Get-FileHash -LiteralPath $installedInput -Algorithm SHA256).Hash -eq $installedHash -and @($r.actions|Where-Object{Test-AssetStorageWithin $_.path $installation}).Count -eq 0)
Need 'installed paths cannot be enumerated as owned output payloads' (Rejected {Get-AssetStorageFiles @($installation) $null})


# Real Windows readers exercise the common atomic publisher rather than an I/O mock.
if(-not ('SolastaAssetAuthoring.Contracts.AtomicReader' -as [type])){
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Threading;
namespace SolastaAssetAuthoring.Contracts {
    public sealed class AtomicReader : IDisposable {
        private readonly ManualResetEvent ready = new ManualResetEvent(false);
        private readonly Thread thread;
        private Exception failure;
        public AtomicReader(string path, int milliseconds) {
            thread = new Thread(() => {
                try { using(var stream=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read)) { ready.Set(); Thread.Sleep(milliseconds); } }
                catch(Exception error) { failure=error; ready.Set(); }
            });
            thread.IsBackground=true; thread.Start();
            if(!ready.WaitOne(3000)) throw new Exception("Reader did not acquire its real file handle.");
            if(failure!=null) throw new IOException("Reader could not acquire its file handle.",failure);
        }
        public void Dispose() { if(!thread.Join(3000)) throw new Exception("Reader did not stop."); ready.Dispose(); }
    }
}
'@
}
$script:AtomicWriterWaits=0
function Start-Sleep {param([int]$Milliseconds) $script:AtomicWriterWaits++; Microsoft.PowerShell.Utility\Start-Sleep -Milliseconds $Milliseconds}
$writerRoot=Directory (Join-Path $workbench 'AtomicPublication')
$writerTarget=Join-Path $writerRoot 'value.json'
Write-AssetWorkerJson $writerTarget @{value=0}
Need 'atomic JSON initial publication succeeds' ((Get-Content -LiteralPath $writerTarget -Raw|ConvertFrom-Json).value -eq 0)
Write-AssetWorkerJson $writerTarget @{value=1}
Need 'atomic JSON replacement succeeds' ((Get-Content -LiteralPath $writerTarget -Raw|ConvertFrom-Json).value -eq 1)
$reader=[SolastaAssetAuthoring.Contracts.AtomicReader]::new($writerTarget,200)
$script:AtomicWriterWaits=0
try{Write-AssetWorkerJson $writerTarget @{value=2}}finally{$reader.Dispose()}
Need 'a real JSON reader sharing conflict is retried within the bound' ($script:AtomicWriterWaits -gt 0 -and $script:AtomicWriterWaits -le 40)
Need 'atomic JSON publishes after its reader releases' ((Get-Content -LiteralPath $writerTarget -Raw|ConvertFrom-Json).value -eq 2)
$reader=[SolastaAssetAuthoring.Contracts.AtomicReader]::new($writerTarget,1500)
$script:AtomicWriterWaits=0;$writerFailure=$null
try{Write-AssetWorkerJson $writerTarget @{value=3}}catch{$writerFailure=$_.Exception}finally{$reader.Dispose()}
Need 'a persistent JSON sharing conflict fails after forty waits' ($script:AtomicWriterWaits -eq 40 -and $null -ne $writerFailure)
Need 'atomic JSON failure identifies its destination' ($null -ne $writerFailure -and $writerFailure.Message.Contains($writerTarget))
Need 'previous JSON survives a persistent reader lock' ((Get-Content -LiteralPath $writerTarget -Raw|ConvertFrom-Json).value -eq 2)
$writerDirectory=Directory (Join-Path $writerRoot 'directory')
$script:AtomicWriterWaits=0;$writerFailure=$null
try{Write-AssetWorkerJson $writerDirectory @{value=4}}catch{$writerFailure=$_.Exception}
Need 'unrelated JSON publication errors are not retried' ($null -ne $writerFailure -and $script:AtomicWriterWaits -eq 0)
Need 'a directory target survives failed JSON publication' ([IO.Directory]::Exists($writerDirectory))
Need 'atomic JSON owned temporary files are removed' (@(Get-ChildItem -LiteralPath $writerRoot -File|Where-Object{$_.Name -match '\.(writing|previous)-'}).Count -eq 0)
Remove-Item -LiteralPath Function:Start-Sleep

# The same publisher accepts structured metadata from PowerShell and CLR receipt DTOs.
if(-not ('SolastaAssetAuthoring.Contracts.MetadataEnvelope' -as [type])){
    Add-Type -TypeDefinition 'namespace SolastaAssetAuthoring.Contracts { public sealed class MetadataEnvelope { public object payload; public object AdaptedPayload { get; set; } } }'
}
function JsonDepthControl([int]$Depth){$value=[ordered]@{leaf='complete'};for($i=0;$i -lt $Depth;$i++){$value=[ordered]@{next=$value}};return $value}
function JsonDepthRejected([scriptblock]$Action){try{&$Action|Out-Null;return $false}catch{return $_.Exception.GetBaseException() -is [FormatException]}}
$metadata=[ordered]@{stages=@([ordered]@{measurements=(JsonDepthControl 24)})}
Write-AssetWorkerJson $writerTarget $metadata
$metadataRead=Get-Content -LiteralPath $writerTarget -Raw|ConvertFrom-Json
Need 'atomic JSON retains structured deep stage metadata' ((ConvertTo-AssetWorkerJson $metadataRead -Compress) -ceq (ConvertTo-AssetWorkerJson $metadata -Compress))
Need 'JSON supported depth boundary stays structured' ((ConvertTo-AssetWorkerJson (JsonDepthControl 100) -Compress) -notmatch '@\{')
Need 'JSON first unsupported depth is explicitly rejected' (JsonDepthRejected {ConvertTo-AssetWorkerJson (JsonDepthControl 101)})
$beforeDepthFailure=(Get-FileHash -LiteralPath $writerTarget -Algorithm SHA256).Hash
Need 'JSON depth failure preserves the previous destination' ((JsonDepthRejected {Write-AssetWorkerJson $writerTarget (JsonDepthControl 101)}) -and (Get-FileHash -LiteralPath $writerTarget -Algorithm SHA256).Hash -ceq $beforeDepthFailure)
Need 'CLR receipt public fields cannot hide excessive metadata depth' (JsonDepthRejected {$dto=[SolastaAssetAuthoring.Contracts.MetadataEnvelope]::new();$dto.payload=JsonDepthControl 101;ConvertTo-AssetWorkerJson $dto})
Need 'CLR receipt adapted properties cannot hide excessive metadata depth' (JsonDepthRejected {$dto=[SolastaAssetAuthoring.Contracts.MetadataEnvelope]::new();$dto.AdaptedPayload=JsonDepthControl 101;ConvertTo-AssetWorkerJson $dto})
Need 'JSON metadata cycles are rejected within the same bound' (JsonDepthRejected {$cycle=@{};$cycle.next=$cycle;ConvertTo-AssetWorkerJson $cycle})
Need 'JSON mixed array object nesting preserves the depth bound' (JsonDepthRejected {$mixed=@{leaf=1};for($i=0;$i -lt 40;$i++){$mixed=[pscustomobject]@{parts=@(@{child=$mixed})}};ConvertTo-AssetWorkerJson $mixed})
$scalarMetadata=@([datetime]'2026-01-01T00:00:00Z',[datetimeoffset]'2026-01-01T00:00:00Z',[guid]::Empty,[uri]'https://example.invalid/',[timespan]::FromSeconds(1),[DayOfWeek]::Monday,[decimal]1.5,[System.Numerics.BigInteger]42,[Management.Automation.SwitchParameter]::new($true))
Need 'JSON existing scalar date time identifier and numeric values remain unchanged' ((ConvertTo-AssetWorkerJson $scalarMetadata -Compress) -ceq ($scalarMetadata|ConvertTo-Json -Depth 100 -Compress))
foreach($scalar in @([datetime]'2026-01-01T00:00:00Z',[guid]::Empty,'scalar-control')){
    $extended=[psobject]$scalar;$extended|Add-Member -NotePropertyName payload -NotePropertyValue (JsonDepthControl 101) -Force
    Need ('JSON extended '+$scalar.GetType().Name+' metadata is bounded') (JsonDepthRejected {ConvertTo-AssetWorkerJson $extended})
}
Need 'JSON dictionary extended metadata is bounded' (JsonDepthRejected {$extended=[psobject]@{known=1};$extended|Add-Member -NotePropertyName payload -NotePropertyValue (JsonDepthControl 101);ConvertTo-AssetWorkerJson $extended})
Need 'JSON dictionary Values key cannot hide deeper metadata' (JsonDepthRejected {$keyed=@{Values=1;payload=(JsonDepthControl 101)};ConvertTo-AssetWorkerJson $keyed})
$keyed=@{PSObject=1;Properties=2;Value=3;Values=4}
Need 'JSON dictionary intrinsic-like keys remain ordinary values' ((ConvertTo-AssetWorkerJson $keyed -Compress) -ceq ($keyed|ConvertTo-Json -Depth 100 -Compress))
$metadataJournal=Join-Path $writerRoot 'metadata.jsonl';Write-AssetStorageEvent $metadataJournal $metadata
$metadataRead=Get-Content -LiteralPath $metadataJournal -Raw|ConvertFrom-Json
Need 'durable JSONL retains structured deep metadata' ((ConvertTo-AssetWorkerJson $metadataRead -Compress) -ceq (ConvertTo-AssetWorkerJson $metadata -Compress))
$beforeDepthFailure=(Get-FileHash -LiteralPath $metadataJournal -Algorithm SHA256).Hash
Need 'JSONL rejects excess depth before changing its existing journal' ((JsonDepthRejected {Write-AssetStorageEvent $metadataJournal (JsonDepthControl 101)}) -and (Get-FileHash -LiteralPath $metadataJournal -Algorithm SHA256).Hash -ceq $beforeDepthFailure)
Need 'JSON depth failure leaves no owned temporary files' (@(Get-ChildItem -LiteralPath $writerRoot -File|Where-Object{$_.Name -match '\.(writing|previous)-'}).Count -eq 0)

foreach($source in $sourceHashes){if((Get-FileHash -LiteralPath $source.path -Algorithm SHA256).Hash -cne $source.sha256){throw 'Candidate source changed during its contracts.'}}
$result=[ordered]@{status=$(if(@($checks|Where-Object{-not $_.passed}).Count){'FAIL'}else{'PASS'});scope='Actual authoring storage sources in owned miniature files. No Unity/Blender/game launch; Blender process census is an external-boundary fixture.';checks=@($checks.ToArray());processQueries=$script:ProcessQueries;sourceHashes=$sourceHashes;powerShell=$PSVersionTable.PSVersion.ToString()}
Save (Join-Path $OutputDirectory 'Result.json') $result
Write-Host ($result.status+': '+$checks.Count+' contracts; '+$OutputDirectory)
if($result.status -ne 'PASS'){throw 'Authoring storage contracts failed.'}
return [pscustomobject]$result
}

if ($StorageOnly) {
    if (-not $StorageEvidenceRoot) {
        $StorageEvidenceRoot=Join-Path $PSScriptRoot ('../workspace/StorageContracts/'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8))
    }
    Invoke-AuthoringStorageContracts ([IO.Path]::GetFullPath($StorageEvidenceRoot))
    return
}

$launcher = Join-Path $PSScriptRoot '..\Invoke-UnityAssetBuild.ps1'
$project = (Resolve-Path -LiteralPath $GuiProjectPath).ProviderPath
$fbx = if (!$MaterialsOnly -and !$MeshesOnly -and !$CliTimeoutOnly -and !$CallerStopOnly) { (Resolve-Path -LiteralPath $FbxPath).ProviderPath } else { $null }
$marker = Join-Path $project '.solasta-asset-authoring-project'
if (!(Test-Path -LiteralPath $marker -PathType Leaf) -or
    [IO.File]::ReadAllText($marker).Trim() -ne 'SolastaUBAssetAuthoring:UnityProject:1') { throw 'A marked authoring project is required.' }
$jobs = Join-Path $project '.asset-authoring-jobs'
[void][IO.Directory]::CreateDirectory((Join-Path $jobs 'BackgroundSessions'))
$checks = [Collections.Generic.List[object]]::new()
function Add-Check([string] $Name, [bool] $Passed, [object] $Evidence) {
    $checks.Add([pscustomobject]@{ name = $Name; passed = $Passed; evidence = $Evidence })
    if (!$Passed) { throw "Contract failed: $Name" }
}
function Invoke-MeshBlendRecipeContracts {
    $tokens=$null;$parseErrors=$null
    $validationAst=[Management.Automation.Language.Parser]::ParseFile($launcher,[ref]$tokens,[ref]$parseErrors)
    if($parseErrors.Count){throw ($parseErrors|Out-String)}
    foreach($name in @('Assert-RecipeFields','Test-FiniteNumber','Assert-MeshNumbers','Assert-MeshRecipe')){
        $definition=@($validationAst.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true))
        if($definition.Count -ne 1){throw 'The shared recipe validator ownership changed.'}
        Invoke-Expression $definition[0].Extent.Text
    }
    $mesh=[ordered]@{name='BlendIndexControl';vertices=@(0,0,0,1,0,0,0,1,0);normals=@(0,0,1,0,0,1,0,0,1);tangents=@(1,0,0,1,1,0,0,1,1,0,0,1);
        uvChannels=@(@{channel=0;dimension=2;values=@(0,0,1,0,0,1)});subMeshes=@(@{topology='Triangles';indices=@(0,1,2)});bounds=@{center=@(.5,.5,0);size=@(1,1,0)};indexFormat='UInt16';blendShapes=@()}
    $unicode=[string]([char]0x8868)+[char]0x60C5+'.'+[char]0x53E3
    foreach($name in @('Control','Other','Control',$unicode,'__UBBlendShapeSlot_00000000','control')){
        $i=$mesh.blendShapes.Count
        $mesh.blendShapes+=@{name=$name;frames=@(@{weight=25;vertices=@(0,0,($i+.125),0,0,0,0,0,0);normals=@(0,0,0,0,0,0,0,0,0);tangents=@(0,0,0,0,0,0,0,0,0)},
            @{weight=100;vertices=@(0,0,($i+.5),0,0,0,0,0,0);normals=@(0,0,0,0,0,0,0,0,0);tangents=@(0,0,0,0,0,0,0,0,0)})}
    }
    $recipe=[ordered]@{schemaVersion=1;bundleName='generic_blend_index_control';meshes=@($mesh)}
    $before=$recipe|ConvertTo-Json -Depth 30 -Compress
    $positive=$before|ConvertFrom-Json
    Assert-MeshRecipe $positive
    Add-Check 'mesh-preflight-physical-repeated-and-unicode-slots' ($positive.meshes[0].blendShapes.Count -eq 6 -and $positive.meshes[0].blendShapes[0].name -ceq $positive.meshes[0].blendShapes[2].name) $null
    Add-Check 'mesh-preflight-does-not-collapse-or-reorder-frame-inputs' (($positive|ConvertTo-Json -Depth 30 -Compress) -ceq $before) $null
    foreach($kind in @('empty-name','control-name','missing-frames','duplicate-weight','descending-weight','nan-weight','truncated-position','truncated-normal','truncated-tangent','unknown-shape-field','unknown-frame-field','invalid-index')){
        $bad=$before|ConvertFrom-Json;$shape=$bad.meshes[0].blendShapes[2]
        switch($kind){
            'empty-name' {$shape.name=' '}
            'control-name' {$shape.name="Invalid`nName"}
            'missing-frames' {$shape.frames=@()}
            'duplicate-weight' {$shape.frames[1].weight=$shape.frames[0].weight}
            'descending-weight' {$shape.frames[1].weight=0}
            'nan-weight' {$shape.frames[1].weight=[double]::NaN}
            'truncated-position' {$shape.frames[0].vertices=@(0,0,0)}
            'truncated-normal' {$shape.frames[0].normals=@(0,0,0)}
            'truncated-tangent' {$shape.frames[0].tangents=@(0,0,0)}
            'unknown-shape-field' {$shape|Add-Member -NotePropertyName foreign -NotePropertyValue $true}
            'unknown-frame-field' {$shape.frames[0]|Add-Member -NotePropertyName foreign -NotePropertyValue $true}
            'invalid-index' {$bad.meshes[0].subMeshes[0].indices[2]=99}
        }
        $rejected=$false;try{Assert-MeshRecipe $bad}catch{$rejected=$true}
        Add-Check ('mesh-preflight-reject-'+$kind) $rejected $null
    }
}
if ($MeshesOnly) {
    Invoke-MeshBlendRecipeContracts
    . (Join-Path $PSScriptRoot '../UnityAssetBuild/BackgroundEditor.ps1')
    $editor=(Resolve-Path -LiteralPath $UnityPath).ProviderPath
    $cli=if ($UnityCliPath) { (Resolve-Path -LiteralPath $UnityCliPath).ProviderPath } else { Join-Path $env:LOCALAPPDATA 'Unity/bin/unity.exe' }
    $recipePath=(Resolve-Path -LiteralPath $MeshRecipePath).ProviderPath
    $sourceHash=(Get-FileHash -LiteralPath (Join-Path $project 'Assets/Editor/AssetBundleBuilder.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
    $recipeHash=(Get-FileHash -LiteralPath $recipePath -Algorithm SHA256).Hash
    $ownedSession=$null; $failure=$null; $completed=$false
    $resultPath=Join-Path $jobs ('contracts-meshes-'+[guid]::NewGuid().ToString('N')+'.json')
    function MeshJobIds { @(Get-ChildItem -LiteralPath $jobs -Directory | Where-Object Name -Match '^[a-f0-9]{32}$' | ForEach-Object Name) }
    function Invoke-MeshQueueContract($Recipe, [string]$Kind = 'mesh-templates', [object[]]$InputFiles = @()) {
        $id=[guid]::NewGuid().ToString('N');$job=Join-Path $jobs $id;$input=Join-Path $project ('Assets/AuthoringJobs/'+$id+'/Input')
        [void][IO.Directory]::CreateDirectory($job);[void][IO.Directory]::CreateDirectory($input)
        [IO.File]::WriteAllText((Join-Path $job '.solasta-asset-authoring-job'),'SolastaUBAssetAuthoring:UnityJob:1')
        foreach ($asset in $InputFiles) {
            $destination=Join-Path $input $asset.path
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
            [IO.File]::Copy($asset.source,$destination,$false)
        }
        $copied=Join-Path $input 'mesh-recipe.json'
        [IO.File]::WriteAllText($copied,($Recipe|ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))
        $request=@{schemaVersion=1;jobId=$id;kind=$Kind;inputSha256=(Get-FileHash -LiteralPath $copied -Algorithm SHA256).Hash.ToLowerInvariant()}
        [IO.File]::WriteAllText((Join-Path $job 'request.writing'),($request|ConvertTo-Json))
        [IO.File]::Move((Join-Path $job 'request.writing'),(Join-Path $job 'request.json'))
        $path=Join-Path $job 'Output/unity-report.json';$deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while(!(Test-Path -LiteralPath $path) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 250}
        if(!(Test-Path -LiteralPath $path)){throw 'The mesh contract worker did not return a report.'}
        return @{id=$id;job=$job;path=$path;report=(Get-Content -LiteralPath $path -Raw|ConvertFrom-Json)}
    }
    function Assert-MeshStages($Report, [string]$Label, [int]$Count) {
        foreach($stage in @('createdMeshes','savedMeshes','meshes')) {
            $rows=@($Report.$stage)
            Add-Check ($Label+'-'+$stage) ($rows.Count -eq $Count -and @($rows|Where-Object {!$_.exact -or !$_.boundsExact -or !$_.skinExact -or !$_.blendShapesExact}).Count -eq 0 -and
                @($rows|ForEach-Object channels|Where-Object {!$_.exact}).Count -eq 0 -and
                @($rows|ForEach-Object blendShapes|Where-Object {!$_.exact -or !$_.channelIdentityExact -or !$_.firstLookupExact -or $_.nameHash -ne $_.expectedNameHash}).Count -eq 0 -and
                @($rows|ForEach-Object blendShapes|ForEach-Object frames|Where-Object {!$_.exact -or !$_.weightExact}).Count -eq 0 -and
                @($rows|ForEach-Object blendShapes|ForEach-Object frames|ForEach-Object channels|Where-Object {!$_.exact}).Count -eq 0) $Report.jobId
        }
    }
    $arguments=@{UnityPath=$UnityPath;GuiProjectPath=$project;TimeoutSeconds=$TimeoutSeconds}
    if ($WorkspaceRoot) { $arguments.WorkspaceRoot=$WorkspaceRoot }
    try {
        $ownedSession=Start-AssetBackgroundEditor $project $editor $cli $sourceHash $TimeoutSeconds
        $before=MeshJobIds
        & $launcher @arguments -MeshRecipePath $recipePath
        $created=@(MeshJobIds|Where-Object {$_ -notin $before})
        Add-Check 'unique-mesh-job' ($created.Count -eq 1) $created
        $reportPath=Join-Path $jobs ($created[0]+'/Output/unity-report.json')
        $report=Get-Content -LiteralPath $reportPath -Raw|ConvertFrom-Json
        $recipe=Get-Content -LiteralPath $recipePath -Raw|ConvertFrom-Json
        Add-Check 'mesh-native-full-channel-readback' ($report.success -and $report.validationScope -eq 'mesh_templates' -and
            @($report.meshes).Count -eq @($recipe.meshes).Count -and @($report.meshes|Where-Object {!$_.exact -or !$_.boundsExact}).Count -eq 0 -and
            @($report.meshes|ForEach-Object channels|Where-Object {!$_.exact}).Count -eq 0) $reportPath
        Add-Check 'mesh-scene-state-preserved' ($report.activeScenePathBefore -eq $report.activeScenePathAfter -and
            $report.activeSceneDirtyBefore -eq $report.activeSceneDirtyAfter -and $report.sceneCountBefore -eq $report.sceneCountAfter) $reportPath
        Assert-MeshStages $report 'mesh-native' @($recipe.meshes).Count
        $fixture=Join-Path $jobs ('MeshInputContracts/'+[guid]::NewGuid().ToString('N'))
        [void][IO.Directory]::CreateDirectory($fixture)
        $triangle=@{name='StaticTemplateControl';vertices=@(0,0,0,1,0,0,0,1,0);normals=@(0,0,1,0,0,1,0,0,1);tangents=@(1,0,0,1,1,0,0,1,1,0,0,1);
            uvChannels=@(@{channel=0;dimension=2;values=@(0,0,1,0,0,1)});subMeshes=@(@{topology='Triangles';indices=@(0,1,2)});bounds=@{center=@(0.5,0.5,0);size=@(1,1,0)};indexFormat='UInt16'}
        $skinned=($triangle|ConvertTo-Json -Depth 20|ConvertFrom-Json);$skinned.name='SkinnedTemplateControl'
        $skinned|Add-Member -NotePropertyName skin -NotePropertyValue @{bindPoses=@(1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1,1,0,0,0,0,1,0,0,0,0,1,0,-0.5,0,0,1);
            boneWeights=@(1,0,0,0,0.75,0.25,0,0,0.5,0.5,0,0);boneIndices=@(0,0,0,0,0,1,0,0,1,0,0,0)}
        $skinned|Add-Member -NotePropertyName blendShapes -NotePropertyValue @(
            @{name='DeformA';frames=@(
                @{weight=25;vertices=@(0,0,0.125,0,0,0,0,0,0);normals=@(0.0625,0,0,0,0,0,0,0,0);tangents=@(0,0.03125,0,0,0,0,0,0,0)},
                @{weight=100;vertices=@(0,0,0.5,0,0,0,0,0,0);normals=@(0.25,0,0,0,0,0,0,0,0);tangents=@(0,0.125,0,0,0,0,0,0,0)})},
            @{name='DeformB';frames=@(@{weight=100;vertices=@(0,0,0,-0.25,0,0,0,0,0);normals=@(0,0,0,0,0.125,0,0,0,0);tangents=@(0,0,0,0,0,-0.0625,0,0,0)})})
        $repeated=($skinned.blendShapes[0]|ConvertTo-Json -Depth 20|ConvertFrom-Json)
        $unicode=($skinned.blendShapes[1]|ConvertTo-Json -Depth 20|ConvertFrom-Json);$unicode.name=[string]([char]0x8868)+[char]0x60C5+'.'+[char]0x53E3
        $temporary=($skinned.blendShapes[1]|ConvertTo-Json -Depth 20|ConvertFrom-Json);$temporary.name='__UBBlendShapeSlot_00000000'
        $caseSensitive=($skinned.blendShapes[1]|ConvertTo-Json -Depth 20|ConvertFrom-Json);$caseSensitive.name='deforma'
        $skinned.blendShapes+=@($repeated,$unicode,$temporary,$caseSensitive)
        $skinRecipe=@{schemaVersion=1;bundleName='mesh_skin_contract';meshes=@($triangle,$skinned)}
        $skinPath=Join-Path $fixture 'skin-multiframe.json'
        [IO.File]::WriteAllText($skinPath,($skinRecipe|ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))
        $skinHash=(Get-FileHash -LiteralPath $skinPath -Algorithm SHA256).Hash
        $skinJob=Invoke-MeshQueueContract $skinRecipe 'mesh-template-contracts'
        Add-Check 'mesh-skin-multiframe-native-reload' ($skinJob.report.success -and $skinJob.report.validationScope -eq 'mesh_template_contracts' -and
            @($skinJob.report.meshes).Count -eq 2 -and $skinJob.report.meshes[0].boneCount -eq 0 -and $skinJob.report.meshes[0].blendShapeCount -eq 0 -and
            $skinJob.report.meshes[1].boneCount -eq 2 -and $skinJob.report.meshes[1].blendShapeCount -eq 6 -and
            @($skinJob.report.meshes[1].blendShapes[0].frames).Count -eq 2 -and @($skinJob.report.meshes[1].blendShapes[1].frames).Count -eq 1) $skinJob.path
        Assert-MeshStages $skinJob.report 'mesh-skin-multiframe' 2
        $mutations=@($skinJob.report.meshValidationContracts)
        Add-Check 'mesh-validator-mutation-count' ($mutations.Count -eq 10) $skinJob.path
        foreach($kind in @('bind-pose','bone-weight','bone-index','blend-shape-order','blend-frame-weight','blend-vertices','blend-normals','blend-tangents','blend-name-hash','blend-frame-range')) {
            $row=@($mutations|Where-Object kind -eq $kind)
            Add-Check ('mesh-reloaded-validator-reject-'+$kind) ($row.Count -eq 1 -and $row[0].rejected -and $row[0].rejection) $skinJob.path
        }
        Add-Check 'mesh-skin-fixture-unchanged' ((Get-FileHash -LiteralPath $skinPath -Algorithm SHA256).Hash -eq $skinHash) $skinPath
        foreach($different in @($true,$false)) {
            $pair=($skinned|ConvertTo-Json -Depth 30|ConvertFrom-Json);$pair.name=if($different){'RepeatedDistinctControl'}else{'RepeatedIdenticalControl'}
            $pair.blendShapes=@(($skinned.blendShapes[0]|ConvertTo-Json -Depth 20|ConvertFrom-Json),($skinned.blendShapes[0]|ConvertTo-Json -Depth 20|ConvertFrom-Json))
            if($different){$pair.blendShapes[1].frames[0].vertices[0]+=0.25}
            $pairJob=Invoke-MeshQueueContract @{schemaVersion=1;bundleName='mesh_repeated_channel_control';meshes=@($pair)} 'mesh-template-contracts'
            Add-Check ($pair.name+'-native-readback') $pairJob.report.success $pairJob.path
            Assert-MeshStages $pairJob.report $pair.name 1
            $pairMutations=@($pairJob.report.meshValidationContracts);$order=@($pairMutations|Where-Object kind -eq 'blend-shape-order')
            Add-Check ($pair.name+'-observable-order-contract') ($order.Count -eq 1 -and $(if($different){$order[0].applicable -and $order[0].rejected -and $order[0].rejection.Contains('DeltaVertices')}else{-not $order[0].applicable -and -not $order[0].rejected -and $order[0].notApplicableReason})) $pairJob.path
            Add-Check ($pair.name+'-other-mutations-remain-applicable') ($pairMutations.Count -eq 10 -and @($pairMutations|Where-Object{$_.kind -ne 'blend-shape-order' -and (-not $_.applicable -or -not $_.rejected)}).Count -eq 0) $pairJob.path
        }
        # Generic color, linear data and tangent-space normal inputs share the same mesh bundle.
        Add-Type -AssemblyName System.Drawing
        $textureDirectory=Join-Path $fixture 'Textures';[void][IO.Directory]::CreateDirectory($textureDirectory)
        $textures=@();$textureInputs=@()
        foreach ($name in @('NormalControl','ColorControl','DataControl')) {
            $extension=if($name -eq 'DataControl'){'.tga'}else{'.png'}
            $file=Join-Path $textureDirectory ($name+$extension);$bitmap=[Drawing.Bitmap]::new(8,8)
            try {
                foreach($x in 0..7){foreach($y in 0..7){$bitmap.SetPixel($x,$y,[Drawing.Color]::FromArgb(255,96+$x*8,96+$y*8,240))}}
                if($extension -eq '.png') {$bitmap.Save($file,[Drawing.Imaging.ImageFormat]::Png)}
                else {
                    $tga=New-Object byte[] (18+8*8*3);$tga[2]=2;$tga[12]=8;$tga[14]=8;$tga[16]=24;$tga[17]=32
                    foreach($y in 0..7){foreach($x in 0..7){$pixel=$bitmap.GetPixel($x,$y);$index=18+($y*8+$x)*3;$tga[$index]=$pixel.B;$tga[$index+1]=$pixel.G;$tga[$index+2]=$pixel.R}}
                    [IO.File]::WriteAllBytes($file,$tga)
                }
            } finally {$bitmap.Dispose()}
            $path='Textures/'+$name+$extension;$hash=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
            $textures+=@{name=$name;path=$path;sha256=$hash;normalMap=($name -eq 'NormalControl');sRGB=($name -eq 'ColorControl');mipmaps=($name -ne 'ColorControl')}
            $textureInputs+=@{path=$path;source=$file;sha256=$hash}
        }
        $textureRecipe=@{schemaVersion=1;bundleName='mesh_texture_contract';meshes=$skinRecipe.meshes;textures=$textures}
        $texturePath=Join-Path $fixture 'mesh-textures.json';[IO.File]::WriteAllText($texturePath,($textureRecipe|ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))
        $before=MeshJobIds;& $launcher @arguments -MeshRecipePath $texturePath
        $created=@(MeshJobIds|Where-Object {$_ -notin $before});Add-Check 'mesh-textures-unique-native-job' ($created.Count -eq 1) $created
        $textureReportPath=Join-Path $jobs ($created[0]+'/Output/unity-report.json');$textureReport=Get-Content -LiteralPath $textureReportPath -Raw|ConvertFrom-Json
        Assert-MeshStages $textureReport 'mesh-with-textures' 2
        foreach($stage in @('createdTextures','savedTextures','textures')) {
            $rows=@($textureReport.$stage)
            Add-Check ('mesh-textures-'+$stage) ($rows.Count -eq 3 -and @($rows|Where-Object {!$_.exact -or !$_.importSettingsExact -or !$_.rawSha256 -or !$_.graphicsFormat -or $_.width -ne 8 -or $_.height -ne 8}).Count -eq 0) $textureReportPath
        }
        Add-Check 'mesh-texture-normalmap-linear-full-mips' ($textureReport.textures[0].normalMap -and !$textureReport.textures[0].sRGB -and !$textureReport.textures[0].heightConversion -and $textureReport.textures[0].mipCount -eq 4) $textureReportPath
        Add-Check 'mesh-texture-color-srgb-no-mips' (!$textureReport.textures[1].normalMap -and $textureReport.textures[1].sRGB -and $textureReport.textures[1].mipCount -eq 1) $textureReportPath
        Add-Check 'mesh-texture-data-linear-full-mips' (!$textureReport.textures[2].normalMap -and !$textureReport.textures[2].sRGB -and $textureReport.textures[2].mipCount -eq 4) $textureReportPath
        $textureJob=Invoke-MeshQueueContract $textureRecipe 'mesh-template-contracts' $textureInputs
        Assert-MeshStages $textureJob.report 'mesh-texture-contracts' 2
        Add-Check 'mesh-texture-contracts-succeeded' ($textureJob.report.success -and @($textureJob.report.meshValidationContracts).Count -eq 10 -and
            @($textureJob.report.meshValidationContracts|Where-Object {!$_.applicable -or !$_.rejected}).Count -eq 0) $textureJob.path
        foreach($kind in @('texture-name','texture-payload','texture-format','texture-mips','texture-import-type','texture-import-srgb','texture-height-conversion','texture-import-mips','texture-compression','texture-readable')) {
            $mutation=@($textureJob.report.textureValidationContracts|Where-Object kind -eq $kind)
            Add-Check ('mesh-texture-validator-reject-'+$kind) ($mutation.Count -eq 1 -and $mutation[0].rejected -and $mutation[0].rejection) $textureJob.path
        }
        foreach($kind in @('unknown-field','path','hash','name','duplicate-name','duplicate-path','normal-type','srgb-type','mipmap-type','normal-srgb','missing-import-field','null-list')) {
            $bad=Get-Content -LiteralPath $texturePath -Raw|ConvertFrom-Json
            switch($kind) {
                'unknown-field' {$bad.textures[0]|Add-Member -NotePropertyName unsupported -NotePropertyValue $true}
                'path' {$bad.textures[0].path='../NormalControl.png'}
                'hash' {$bad.textures[0].sha256='0'*64}
                'name' {$bad.textures[0].name='WrongBasename'}
                'duplicate-name' {$bad.textures[1].name=$bad.textures[0].name}
                'duplicate-path' {$bad.textures[1].path=$bad.textures[0].path}
                'normal-type' {$bad.textures[0].normalMap='true'}
                'srgb-type' {$bad.textures[0].sRGB=0}
                'mipmap-type' {$bad.textures[0].mipmaps='false'}
                'normal-srgb' {$bad.textures[0].sRGB=$true}
                'missing-import-field' {$bad.textures[0].PSObject.Properties.Remove('mipmaps')}
                'null-list' {$bad.textures=$null}
            }
            $input=Join-Path $fixture ('texture-'+$kind+'.json');[IO.File]::WriteAllText($input,($bad|ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))
            $before=MeshJobIds;$rejected=$false;try{& $launcher @arguments -MeshRecipePath $input}catch{$rejected=$true}
            Add-Check ('mesh-texture-preflight-reject-'+$kind) ($rejected -and @(MeshJobIds|Where-Object {$_ -notin $before}).Count -eq 0) $input
            $negative=Invoke-MeshQueueContract $bad 'mesh-templates' $textureInputs
            Add-Check ('mesh-texture-worker-reject-'+$kind) (!$negative.report.success -and $negative.report.error -and
                !(Test-Path -LiteralPath (Join-Path $project ('Assets/AuthoringJobs/'+$negative.id+'/Generated'))) -and
                !(Test-Path -LiteralPath (Join-Path $negative.job ('Output/'+$bad.bundleName)))) $negative.path
        }
        Add-Check 'mesh-texture-source-unchanged' (@($textureInputs|Where-Object {(Get-FileHash -LiteralPath $_.source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $_.sha256}).Count -eq 0) $textureDirectory
        foreach($kind in @('bind-length','bind-nonfinite','weight-length','weight-sum','weight-order','weight-negative','bone-range','bone-fraction','shape-control-name','frame-order','frame-nonfinite','frame-empty','frame-null','delta-length','delta-nonfinite')) {
            $bad=Get-Content -LiteralPath $skinPath -Raw|ConvertFrom-Json;$mesh=$bad.meshes[1]
            switch($kind) {
                'bind-length' {$mesh.skin.bindPoses=@(1,0,0)}
                'bind-nonfinite' {$mesh.skin.bindPoses[0]=[double]::NaN}
                'weight-length' {$mesh.skin.boneWeights=@(1,0,0,0)}
                'weight-sum' {$mesh.skin.boneWeights[0]=0.5}
                'weight-order' {$mesh.skin.boneWeights[0]=0.25;$mesh.skin.boneWeights[1]=0.75}
                'weight-negative' {$mesh.skin.boneWeights[0]=1.25;$mesh.skin.boneWeights[1]=-0.25}
                'bone-range' {$mesh.skin.boneIndices[0]=2}
                'bone-fraction' {$mesh.skin.boneIndices[0]=0.5}
                'shape-control-name' {$mesh.blendShapes[1].name="Invalid`nName"}
                'frame-order' {$mesh.blendShapes[0].frames[1].weight=25}
                'frame-nonfinite' {$mesh.blendShapes[0].frames[0].weight=[double]::NaN}
                'frame-empty' {$mesh.blendShapes[0].frames=@()}
                'frame-null' {$mesh.blendShapes[0].frames[0]=$null}
                'delta-length' {$mesh.blendShapes[0].frames[0].vertices=@(0,0,0)}
                'delta-nonfinite' {$mesh.blendShapes[0].frames[0].normals[0]=[double]::PositiveInfinity}
            }
            $input=Join-Path $fixture ($kind+'.json')
            [IO.File]::WriteAllText($input,($bad|ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))
            $before=MeshJobIds;$rejected=$false
            try { & $launcher @arguments -MeshRecipePath $input } catch { $rejected=$true }
            Add-Check ('mesh-skin-preflight-reject-'+$kind) ($rejected -and @(MeshJobIds|Where-Object {$_ -notin $before}).Count -eq 0) $input
            if($kind -in @('bone-range','weight-sum','frame-order','delta-length')) {
                $negative=Invoke-MeshQueueContract $bad
                Add-Check ('mesh-skin-worker-reject-'+$kind) (!$negative.report.success -and $negative.report.error -and
                    !(Test-Path -LiteralPath (Join-Path $project ('Assets/AuthoringJobs/'+$negative.id+'/Generated'))) -and
                    !(Test-Path -LiteralPath (Join-Path $negative.job ('Output/'+$bad.bundleName)))) $negative.path
            }
        }
        foreach ($kind in @('nonfinite','channel-length','indices','bounds','duplicate-name','path-name')) {
            $bad=Get-Content -LiteralPath $recipePath -Raw|ConvertFrom-Json
            switch($kind) {
                'nonfinite' {$bad.meshes[0].vertices[0]=[double]::NaN}
                'channel-length' {$bad.meshes[0].normals=@(1,0,0)}
                'indices' {$bad.meshes[0].subMeshes[0].indices[0]=2147483647}
                'bounds' {$bad.meshes[0].bounds.size=@(0,0,0)}
                'duplicate-name' {$bad.meshes=@($bad.meshes)+@($bad.meshes[0])}
                'path-name' {$bad.meshes[0].name='../outside'}
            }
            $input=Join-Path $fixture ($kind+'.json')
            [IO.File]::WriteAllText($input,($bad|ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
            $before=MeshJobIds;$rejected=$false
            try { & $launcher @arguments -MeshRecipePath $input } catch { $rejected=$true }
            Add-Check ('mesh-preflight-reject-'+$kind) ($rejected -and @(MeshJobIds|Where-Object {$_ -notin $before}).Count -eq 0) $input
        }
        # Existing input contracts remain before worker dispatch; no extra rig/material build.
        foreach($legacy in @('rig-extension','material-schema')) {
            $before=MeshJobIds;$rejected=$false
            try {
                if ($legacy -eq 'rig-extension') { & $launcher @arguments -FbxPath $recipePath }
                else { & $launcher @arguments -MaterialRecipePath $recipePath }
            } catch { $rejected=$true }
            Add-Check ('legacy-preflight-'+$legacy) ($rejected -and @(MeshJobIds|Where-Object {$_ -notin $before}).Count -eq 0) $recipePath
        }
        # A direct owned queue fault proves the worker independently checks before asset creation.
        $id=[guid]::NewGuid().ToString('N');$job=Join-Path $jobs $id;$input=Join-Path $project ('Assets/AuthoringJobs/'+$id+'/Input')
        [void][IO.Directory]::CreateDirectory($job);[void][IO.Directory]::CreateDirectory($input)
        [IO.File]::WriteAllText((Join-Path $job '.solasta-asset-authoring-job'),'SolastaUBAssetAuthoring:UnityJob:1')
        $bad=Get-Content -LiteralPath $recipePath -Raw|ConvertFrom-Json;$bad.meshes[0].subMeshes[0].indices[0]=-1
        $copied=Join-Path $input 'mesh-recipe.json';[IO.File]::WriteAllText($copied,($bad|ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
        $request=@{schemaVersion=1;jobId=$id;kind='mesh-templates';inputSha256=(Get-FileHash -LiteralPath $copied -Algorithm SHA256).Hash.ToLowerInvariant()}
        [IO.File]::WriteAllText((Join-Path $job 'request.writing'),($request|ConvertTo-Json));[IO.File]::Move((Join-Path $job 'request.writing'),(Join-Path $job 'request.json'))
        $failedReport=Join-Path $job 'Output/unity-report.json';$deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while (!(Test-Path -LiteralPath $failedReport) -and [DateTime]::UtcNow -lt $deadline) {Start-Sleep -Milliseconds 250}
        if (!(Test-Path -LiteralPath $failedReport)) {throw 'Rejected mesh input did not return a worker report.'}
        $failed=Get-Content -LiteralPath $failedReport -Raw|ConvertFrom-Json
        Add-Check 'mesh-worker-reject-invalid-index' (!$failed.success -and $failed.error -like '*index range*' -and
            !(Test-Path -LiteralPath (Join-Path $project ('Assets/AuthoringJobs/'+$id+'/Generated'))) -and
            !(Test-Path -LiteralPath (Join-Path $job ('Output/'+$recipe.bundleName)))) $failedReport
        Add-Check 'mesh-source-unchanged' ((Get-FileHash -LiteralPath $recipePath -Algorithm SHA256).Hash -eq $recipeHash) $recipeHash
        Add-Check 'mesh-borrowed-worker-still-owned' ($null -ne (Get-AssetOwnedProcess $ownedSession.Ready.editor)) $ownedSession.Ready.editor
        $completed=$true
    } catch {$failure=$_.Exception.ToString();throw}
    finally {
        if($ownedSession) {
            Stop-AssetBackgroundEditor $ownedSession
            $state=Get-Content -LiteralPath (Join-Path $ownedSession.RunDirectory 'session.json') -Raw|ConvertFrom-Json
            Add-Check 'mesh-normal-owned-shutdown' ($state.status -eq 'complete' -and $state.editorReleased -and !$state.forcedExit -and
                !(Get-AssetOwnedProcess $state.editor)) $ownedSession.RunDirectory
        }
        [IO.File]::WriteAllText($resultPath,(@{passed=$completed;error=$failure;scope='mesh-templates-static-and-skinned-multiframe-readback-input-and-validator-contracts';checks=$checks}|ConvertTo-Json -Depth 12))
        Write-Output "Mesh template contracts: $resultPath"
    }
    return
}
if ($CallerStopOnly) {
    $helper=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../UnityAssetBuild/BackgroundEditor.ps1')).ProviderPath
    . $helper
    $fixtureRoot=Join-Path $jobs ('CallerStopContracts/'+[guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($fixtureRoot)
    $fakeCli=Join-Path $fixtureRoot 'ReceiptOnlyCli.exe'
    $source=Join-Path $fixtureRoot 'ReceiptOnlyCli.cs'
    [IO.File]::WriteAllText($source,@'
using System;
class ReceiptOnlyCli {
    static string E(string s) { return s.Replace("\\","\\\\").Replace("\"","\\\""); }
    static void Main(string[] a) { Console.WriteLine("{\"success\":true,\"data\":{\"editorPath\":\""+E(a[5])+"\",\"projectPath\":\""+E(a[1])+"\"}}"); }
}
'@)
    & (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /target:exe ('/out:'+$fakeCli) $source
    if ($LASTEXITCODE -ne 0) { throw 'The receipt-only CLI fixture did not compile.' }
    $sourceHash=(Get-FileHash -LiteralPath (Join-Path $project 'Assets/Editor/AssetBundleBuilder.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
    $editor=(Resolve-Path -LiteralPath $UnityPath).ProviderPath
    $sessions=Join-Path $jobs 'BackgroundSessions'
    function Wait-CallerFixture([string[]]$Before) {
        $deadline=[DateTime]::UtcNow.AddSeconds(15)
        while ([DateTime]::UtcNow -lt $deadline) {
            $created=@(Get-ChildItem -LiteralPath $sessions -Directory | Where-Object Name -NotIn $Before)
            if ($created.Count -eq 1) {
                $statePath=Join-Path $created[0].FullName 'session.json'
                if (Test-Path -LiteralPath $statePath) {
                    $state=Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
                    if ($state.PSObject.Properties['cli'] -and $state.cli.PSObject.Properties['exitCode']) { return $statePath }
                }
            }
            Start-Sleep -Milliseconds 100
        }
        throw 'The actual background host did not reach its startup wait.'
    }
    function Assert-CallerFixtureReleased([string]$StatePath,[string]$Name,[DateTime]$Stopped) {
        $deadline=[DateTime]::UtcNow.AddSeconds(10)
        do {
            $state=Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
            if ($state.PSObject.Properties['finishedUtc']) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        Add-Check $Name ($state.status -eq 'failed' -and $state.cliReleased -and $state.editorReleased -and
            !$state.forcedExit -and $state.error -match 'caller (cancelled|ended)' -and
            ([DateTime]::UtcNow-$Stopped).TotalSeconds -lt 10 -and !(Get-AssetOwnedProcess $state.cli)) $StatePath
    }
    $before=@(Get-ChildItem -LiteralPath $sessions -Directory | ForEach-Object Name)
    $pipeline=[PowerShell]::Create()
    try {
        [void]$pipeline.AddScript('param($helper,$project,$editor,$cli,$hash); . $helper; Start-AssetBackgroundEditor $project $editor $cli $hash 60').AddArgument($helper).AddArgument($project).AddArgument($editor).AddArgument($fakeCli).AddArgument($sourceHash)
        $pending=$pipeline.BeginInvoke()
        $statePath=Wait-CallerFixture $before
        $stopped=[DateTime]::UtcNow
        $pipeline.Stop()
        Assert-CallerFixtureReleased $statePath 'cancelled-pipeline-releases-starting-host' $stopped
    }
    finally { if ($pipeline.InvocationStateInfo.State -eq 'Running') { $pipeline.Stop() }; $pipeline.Dispose() }
    $before=@(Get-ChildItem -LiteralPath $sessions -Directory | ForEach-Object Name)
    $caller=Join-Path $fixtureRoot 'Caller.ps1'
    [IO.File]::WriteAllText($caller,'param($helper,$project,$editor,$cli,$hash); . $helper; Start-AssetBackgroundEditor $project $editor $cli $hash 60')
    $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=(Get-Process -Id $PID).Path;$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    foreach ($arg in @('-NoProfile','-File',$caller,$helper,$project,$editor,$fakeCli,$sourceHash)) { $start.ArgumentList.Add($arg) }
    $ownedCaller=[Diagnostics.Process]::Start($start)
    try {
        $statePath=Wait-CallerFixture $before
        $stopped=[DateTime]::UtcNow
        $ownedCaller.Kill();[void]$ownedCaller.WaitForExit(5000)
        Assert-CallerFixtureReleased $statePath 'ended-caller-releases-starting-host' $stopped
    }
    finally { if (!$ownedCaller.HasExited) { $ownedCaller.Kill();[void]$ownedCaller.WaitForExit(5000) }; $ownedCaller.Dispose() }
    $reportPath=Join-Path $fixtureRoot 'contracts.json'
    [IO.File]::WriteAllText($reportPath,(@{success=$true;scope='actual-background-host-startup-caller-lifecycle';checks=$checks}|ConvertTo-Json -Depth 12))
    Write-Output "Caller stop contracts passed: $reportPath"
    return
}
if ($CliTimeoutOnly) {
    . (Join-Path $PSScriptRoot '../UnityAssetBuild/BackgroundEditor.ps1')
    $fixtureRoot=Join-Path $jobs ('CliTimeoutContracts/'+[guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $source=Join-Path $fixtureRoot 'DelayedCli.cs'
    $fakeCli=Join-Path $fixtureRoot 'DelayedCli.exe'
    [IO.File]::WriteAllText($source, 'using System; using System.IO; using System.Threading; class DelayedCli { static void Main() { Thread.Sleep(60000); File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"late-launch.txt"),"unexpected late launcher action"); } }')
    $compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    & $compiler /nologo /target:exe ('/out:'+$fakeCli) $source
    if ($LASTEXITCODE -ne 0) { throw 'The owned delayed CLI fixture did not compile.' }
    $sessions=Join-Path $jobs 'BackgroundSessions'
    $before=@(Get-ChildItem -LiteralPath $sessions -Directory | ForEach-Object Name)
    $sourceHash=(Get-FileHash -LiteralPath (Join-Path $project 'Assets/Editor/AssetBundleBuilder.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
    $failed=$false
    try { $unexpected=Start-AssetBackgroundEditor $project (Resolve-Path -LiteralPath $UnityPath).ProviderPath $fakeCli $sourceHash 30 }
    catch { $failed=$true }
    $created=@(Get-ChildItem -LiteralPath $sessions -Directory | Where-Object Name -NotIn $before)
    Add-Check 'one-timeout-session' ($failed -and $created.Count -eq 1) @($created.Name)
    $statePath=Join-Path $created[0].FullName 'session.json'
    $state=Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    Add-Check 'owned-cli-stopped-before-release' ($state.status -eq 'failed' -and $state.cliReleased -and $state.cliTerminatedAfterFailure -and
        $state.cli.path -eq $fakeCli -and !(Get-AssetOwnedProcess $state.cli) -and $state.editorReleased -and
        !(Test-Path -LiteralPath (Join-Path $fixtureRoot 'late-launch.txt'))) $statePath
    $reportPath=Join-Path $fixtureRoot 'contracts.json'
    [IO.File]::WriteAllText($reportPath, (@{success=$true;scope='owned-cli-timeout-cleanup';checks=$checks} | ConvertTo-Json -Depth 12))
    Write-Output "CLI timeout contracts passed: $reportPath"
    return
}
if ($BackgroundEditorOnly) {
    $sessions = Join-Path $jobs 'BackgroundSessions'
    if ($InstallGuiWorker) {
        & $launcher -UnityPath $UnityPath -FbxPath $fbx -GuiProjectPath $project -StartBackgroundEditor -UnityCliPath $UnityCliPath -InstallGuiWorker -TimeoutSeconds $TimeoutSeconds
    }
    . (Join-Path $PSScriptRoot '../UnityAssetBuild/BackgroundEditor.ps1')
    $sourceHash=(Get-FileHash -LiteralPath (Join-Path $project 'Assets/Editor/AssetBundleBuilder.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
    $cli=if ($UnityCliPath) { $UnityCliPath } else { Join-Path $env:LOCALAPPDATA 'Unity/bin/unity.exe' }
    $ownedSession=Start-AssetBackgroundEditor $project (Resolve-Path -LiteralPath $UnityPath).ProviderPath $cli $sourceHash $TimeoutSeconds
    try {
        $environmentScript=Join-Path $PSScriptRoot '../Test-AssetEnvironment.ps1'
        $environmentHash=(Get-FileHash -LiteralPath $environmentScript -Algorithm SHA256).Hash
        $environmentText=& $environmentScript -UnityPath $UnityPath -GuiProjectPath $project -RequireReadyEditor
        $environment=$environmentText | ConvertFrom-Json
        $environmentPath=Join-Path $ownedSession.RunDirectory 'environment.json'
        [IO.File]::WriteAllText($environmentPath,($environment | ConvertTo-Json -Depth 12))
        Add-Check 'environment-live-isolated-worker' ($environment.editor_worker_ready -and $environment.editor_non_input_worker_ready -and
            @($environment.editor_workers | Where-Object { $_.ready -and $_.isolated -and $_.process_id -eq $ownedSession.Ready.editor.pid }).Count -eq 1 -and
            (Get-FileHash -LiteralPath $environmentScript -Algorithm SHA256).Hash -eq $environmentHash) $environmentPath
        $rejected=$false
        try { $unexpected=Start-AssetBackgroundEditor $project (Resolve-Path -LiteralPath $UnityPath).ProviderPath $cli $sourceHash $TimeoutSeconds }
        catch { $rejected=$_.Exception.Message -like 'The selected project*open*' }
        Add-Check 'reject-second-owner' $rejected $ownedSession.RunDirectory
        $readyPath=Join-Path $jobs 'worker.json'
        [IO.File]::Delete($readyPath)
        $rejectedWithoutReceipt=$false
        try { $unexpected=Start-AssetBackgroundEditor $project (Resolve-Path -LiteralPath $UnityPath).ProviderPath $cli $sourceHash $TimeoutSeconds }
        catch { $rejectedWithoutReceipt=$_.Exception.Message -like '*native UnityLockfile is locked*' }
        Add-Check 'native-lock-without-worker-receipt' $rejectedWithoutReceipt $ownedSession.RunDirectory
        & $launcher -UnityPath $UnityPath -FbxPath $fbx -GuiProjectPath $project -TimeoutSeconds $TimeoutSeconds
        Add-Check 'borrowed-worker-remains-running' ($null -ne (Get-AssetOwnedProcess $ownedSession.Ready.editor)) $ownedSession.Ready.editor
    } finally { Stop-AssetBackgroundEditor $ownedSession }
    $previousOwner = $null
    foreach ($cycle in @('first', 'repeat', 'rejected-input')) {
        $before = @(if (Test-Path -LiteralPath $sessions) { Get-ChildItem -LiteralPath $sessions -Directory | ForEach-Object Name })
        $input = $fbx
        if ($cycle -eq 'rejected-input') {
            $input = Join-Path $jobs ('invalid-' + [guid]::NewGuid().ToString('N') + '.fbx')
            [IO.File]::WriteAllText($input, 'invalid FBX fixture')
        }
        $failed = $false
        try {
            & $launcher -UnityPath $UnityPath -FbxPath $input -GuiProjectPath $project -StartBackgroundEditor -UnityCliPath $UnityCliPath -InstallGuiWorker:$InstallGuiWorker -TimeoutSeconds $TimeoutSeconds
        } catch { $failed = $true; if ($cycle -ne 'rejected-input') { throw } }
        finally { if ($cycle -eq 'rejected-input') { Remove-Item -LiteralPath $input -Force } }
        Add-Check ('background-result-' + $cycle) ($failed -eq ($cycle -eq 'rejected-input')) $failed
        $created = @(Get-ChildItem -LiteralPath $sessions -Directory | Where-Object Name -NotIn $before)
        Add-Check ('one-owned-session-' + $cycle) ($created.Count -eq 1) @($created.Name)
        $statePath = Join-Path $created[0].FullName 'session.json'
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        Add-Check ('isolated-main-thread-' + $cycle) ($state.worker.mainThread -gt 0 -and $state.worker.nativeMainThread -gt 0 -and
            $state.worker.desktopQuerySucceeded -and !$state.worker.desktopReceivesInput -and $state.worker.desktop -eq $state.desktop -and
            $state.worker.desktop -ne $state.worker.inputDesktop -and $state.worker.ownerId -eq $state.ownerId -and
            $state.worker.processId -eq $state.editor.pid -and $state.cli.exitCode -eq 0 -and !$state.cli.batchMode -and !$state.cli.waitRequested) $statePath
        Add-Check ('normal-owned-shutdown-' + $cycle) ($state.status -eq 'complete' -and $state.editorReleased -and !$state.forcedExit -and
            !(Get-Process -Id $state.editor.pid -ErrorAction SilentlyContinue)) $statePath
        if ($previousOwner) { Add-Check ('distinct-owner-' + $cycle) ($state.ownerId -ne $previousOwner) $state.ownerId }
        $previousOwner = $state.ownerId
    }
    $reportPath = Join-Path $jobs ('contracts-background-' + [guid]::NewGuid().ToString('N') + '.json')
    [IO.File]::WriteAllText($reportPath, (@{success=$true;scope='native-background-editor-lifecycle';checks=$checks} | ConvertTo-Json -Depth 12))
    Write-Output "Background Editor contracts passed: $reportPath"
    return
}
$ready = Get-Content -LiteralPath (Join-Path $jobs 'worker.json') -Raw | ConvertFrom-Json
if ($ready.schemaVersion -ne 2) { throw 'Refresh the current GUI worker before running its contracts.' }
$originalProcess = Get-Process -Id $ready.processId
$originalStart = $originalProcess.StartTime.ToUniversalTime()
$inputHash = if ($fbx) { (Get-FileHash -LiteralPath $fbx -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }

function Get-JobIds {
    @(Get-ChildItem -LiteralPath $jobs -Directory | Where-Object Name -Match '^[a-f0-9]{32}$' | ForEach-Object Name)
}

function Invoke-NativeBuild([string] $Label) {
    $before = Get-JobIds
    & $launcher -UnityPath $UnityPath -FbxPath $fbx -GuiProjectPath $project -TimeoutSeconds $TimeoutSeconds -InstallGuiWorker:$InstallGuiWorker
    $created = @(Get-JobIds | Where-Object { $_ -notin $before })
    Add-Check ('unique-job-' + $Label) ($created.Count -eq 1) $created
    $reportPath = Join-Path $jobs ($created[0] + '\Output\unity-report.json')
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    Add-Check ('native-build-reload-' + $Label) ($report.success -and $report.stage -eq 'complete' -and
        $report.reloaded.sampledFrames -eq 25 -and $report.reloaded.deformedFrames -gt 0 -and
        $report.reloaded.boneCount -eq $report.imported.boneCount -and $report.reloaded.vertexCount -eq $report.imported.vertexCount -and
        $report.activeScenePathBefore -eq $report.activeScenePathAfter -and
        $report.activeSceneDirtyBefore -eq $report.activeSceneDirtyAfter -and $report.sceneCountBefore -eq $report.sceneCountAfter) $reportPath
}

function Wait-ReloadedSource([string] $Hash, [DateTime] $PreviousAssemblyTime) {
    $readyPath = Join-Path $jobs 'worker.json'
    $assemblyPath = Join-Path $project 'Library\ScriptAssemblies\Assembly-CSharp-Editor.dll'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $receipt = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
        $process = Get-Process -Id $ready.processId
        if ($process.StartTime.ToUniversalTime() -ne $originalStart) { throw 'The original GUI worker process ended.' }
        $compiledAt = (Get-Item -LiteralPath $assemblyPath).LastWriteTimeUtc
        if ($receipt.schemaVersion -eq 2 -and $receipt.sourceSha256 -eq $Hash -and $receipt.mainThread -eq $ready.mainThread -and
            $compiledAt -gt $PreviousAssemblyTime -and
            ([DateTime]::UtcNow - ([DateTime]$receipt.updatedUtc).ToUniversalTime()).TotalSeconds -lt 5) { return $compiledAt }
        Start-Sleep -Milliseconds 250
    }
    throw 'The modified fixed worker source did not self-import and recompile.'
}

function Submit-RejectedJob([string] $Kind) {
    $id = [guid]::NewGuid().ToString('N')
    $jobRoot = Join-Path $jobs $id
    $input = Join-Path $project ('Assets\AuthoringJobs\' + $id + '\Input')
    [IO.Directory]::CreateDirectory($jobRoot) | Out-Null
    [IO.Directory]::CreateDirectory($input) | Out-Null
    [IO.File]::WriteAllText((Join-Path $jobRoot '.solasta-asset-authoring-job'), 'SolastaUBAssetAuthoring:UnityJob:1')
    [IO.File]::Copy($fbx, (Join-Path $input 'SampleRig.fbx'), $false)
    $request = @{ schemaVersion = 1; jobId = $id; inputSha256 = $inputHash }
    switch ($Kind) {
        'changed-input' { $request.inputSha256 = '0' * 64 }
        'schema' { $request.schemaVersion = 2 }
        'identity' { $request.jobId = [guid]::NewGuid().ToString('N') }
        'interrupted' { [IO.File]::WriteAllText((Join-Path $jobRoot 'started.json'), [DateTime]::UtcNow.ToString('o')) }
        default { throw "Unknown contract: $Kind" }
    }
    [IO.File]::WriteAllText((Join-Path $jobRoot 'request.writing'), ($request | ConvertTo-Json))
    [IO.File]::Move((Join-Path $jobRoot 'request.writing'), (Join-Path $jobRoot 'request.json'))
    $reportPath = Join-Path $jobRoot 'Output\unity-report.json'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (!(Test-Path -LiteralPath $reportPath -PathType Leaf) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (!(Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw "Rejected job did not report its failure: $jobRoot" }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    Add-Check ('reject-' + $Kind) (!$report.success -and $report.error -and
        $report.jobId -eq $id -and $report.processId -eq $ready.processId -and $report.mainThread -eq $ready.mainThread -and
        !(Test-Path -LiteralPath (Join-Path $jobRoot 'Output\ub_authoring_sample'))) $reportPath
}

function Test-MaterialJobs {
    $recipePath = (Resolve-Path -LiteralPath $MaterialRecipePath).ProviderPath
    $before = Get-JobIds
    & $launcher -UnityPath $UnityPath -MaterialRecipePath $recipePath -GuiProjectPath $project -TimeoutSeconds $TimeoutSeconds -InstallGuiWorker:$InstallGuiWorker
    $created = @(Get-JobIds | Where-Object { $_ -notin $before })
    Add-Check 'unique-material-job' ($created.Count -eq 1) $created
    $reportPath = Join-Path $jobs ($created[0] + '\Output\unity-report.json')
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    Add-Check 'native-material-reload' ($report.success -and $report.validationScope -eq 'materials' -and
        @($report.materials).Count -gt 0 -and @($report.materials | Where-Object { !$_.shaderSupported -or !$_.passes }).Count -eq 0) $reportPath
    $fixture = Join-Path $jobs ('MaterialContracts\' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($fixture) | Out-Null
    $originalRecipe = Get-Content -LiteralPath $recipePath -Raw | ConvertFrom-Json
    foreach ($asset in $originalRecipe.assets) {
        $destination = Join-Path $fixture $asset.path
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy((Join-Path ([IO.Path]::GetDirectoryName($recipePath)) $asset.path), $destination, $false)
    }
    foreach ($kind in @('required-pass', 'property-type', 'asset-hash', 'unknown-field', 'numeric-type', 'asset-path')) {
        $recipe = Get-Content -LiteralPath $recipePath -Raw | ConvertFrom-Json
        switch ($kind) {
            'required-pass' { $recipe.materials[0].requiredPasses = @('__MissingValidationPass__') }
            'property-type' {
                $color = @($recipe.materials[0].properties | Where-Object kind -eq 'color')[0]
                $recipe.materials[0].properties = @([pscustomobject]@{ name = $color.name; kind = 'float'; value = 0.5 })
            }
            'asset-hash' { $recipe.assets[0].sha256 = '0' * 64 }
            'unknown-field' { $recipe | Add-Member -NotePropertyName unsupported -NotePropertyValue $true }
            'numeric-type' { @($recipe.materials[0].properties | Where-Object kind -eq 'float')[0].value = 'invalid' }
            'asset-path' { $recipe.assets[0].path = '../Outside.shader' }
        }
        $input = Join-Path $fixture ($kind + '.json')
        [IO.File]::WriteAllText($input, ($recipe | ConvertTo-Json -Depth 20))
        $before = Get-JobIds
        $rejected = $false
        try { & $launcher -UnityPath $UnityPath -MaterialRecipePath $input -GuiProjectPath $project -TimeoutSeconds $TimeoutSeconds }
        catch { $rejected = $true }
        $created = @(Get-JobIds | Where-Object { $_ -notin $before })
        if ($kind -in @('required-pass', 'property-type')) {
            Add-Check ('material-native-reject-' + $kind) ($rejected -and $created.Count -eq 1) $created
            $failedPath = Join-Path $jobs ($created[0] + '\Output\unity-report.json')
            $failed = Get-Content -LiteralPath $failedPath -Raw | ConvertFrom-Json
            Add-Check ('material-failed-report-' + $kind) (!$failed.success -and $failed.error -and $failed.validationScope -eq 'materials' -and
                !(Test-Path -LiteralPath (Join-Path $jobs ($created[0] + '\Output\' + $originalRecipe.bundleName)))) $failedPath
        }
        else { Add-Check ('material-preflight-reject-' + $kind) ($rejected -and $created.Count -eq 0) $input }
    }
}

function Test-MaterialIdentity {
    $id = [guid]::NewGuid().ToString('N')
    $jobRoot = Join-Path $jobs $id
    $input = Join-Path $project ('Assets\AuthoringJobs\' + $id + '\Input')
    [IO.Directory]::CreateDirectory($jobRoot) | Out-Null
    [IO.File]::WriteAllText((Join-Path $jobRoot '.solasta-asset-authoring-job'), 'SolastaUBAssetAuthoring:UnityJob:1')
    $assets = @()
    Add-Type -AssemblyName System.Drawing
    foreach ($side in @('A', 'B')) {
        $directory = Join-Path $input $side
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        $tint = if ($side -eq 'A') { 'fixed3(1, 0.5, 0.5)' } else { 'fixed3(0.5, 0.5, 1)' }
        $shader = @"
Shader "AuthoringContracts/SharedIdentity" {
 Properties { _MainTex ("Albedo", 2D) = "white" {} }
 SubShader { Tags { "RenderType" = "Opaque" }
 CGPROGRAM
 #pragma target 3.0
 #pragma surface surf Standard
 sampler2D _MainTex;
 struct Input { float2 uv_MainTex; };
 void surf(Input input, inout SurfaceOutputStandard output) {
  output.Albedo = tex2D(_MainTex, input.uv_MainTex).rgb * $tint;
  output.Alpha = 1;
 }
 ENDCG
 }
 FallBack Off
}
"@
        [IO.File]::WriteAllText((Join-Path $directory 'Shared.shader'), $shader)
        $bitmap = [Drawing.Bitmap]::new(2, 2)
        try {
            $color = if ($side -eq 'A') { [Drawing.Color]::Red } else { [Drawing.Color]::Blue }
            foreach ($x in 0..1) { foreach ($y in 0..1) { $bitmap.SetPixel($x, $y, $color) } }
            $bitmap.Save((Join-Path $directory 'Shared.png'), [Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $bitmap.Dispose() }
        foreach ($name in @('Shared.shader', 'Shared.png')) {
            $assets += @{path="$side/$name"; sha256=(Get-FileHash -LiteralPath (Join-Path $directory $name) -Algorithm SHA256).Hash.ToLowerInvariant()}
        }
    }
    $recipe = @{schemaVersion=1; bundleName='material_identity_contract'; assets=$assets; materials=@(
        @{name='Expected';shader='A/Shared.shader';requiredPasses=@('FORWARD');properties=@(@{name='_MainTex';kind='texture';texture='A/Shared.png'})},
        @{name='Alternative';shader='B/Shared.shader';requiredPasses=@('FORWARD');properties=@(@{name='_MainTex';kind='texture';texture='B/Shared.png'})}
    )}
    $recipePath = Join-Path $input 'recipe.json'
    [IO.File]::WriteAllText($recipePath, ($recipe | ConvertTo-Json -Depth 10))
    $request = @{schemaVersion=1;jobId=$id;kind='material-identity-contracts';inputSha256=(Get-FileHash -LiteralPath $recipePath -Algorithm SHA256).Hash.ToLowerInvariant()}
    [IO.File]::WriteAllText((Join-Path $jobRoot 'request.writing'), ($request | ConvertTo-Json))
    [IO.File]::Move((Join-Path $jobRoot 'request.writing'), (Join-Path $jobRoot 'request.json'))
    $reportPath = Join-Path $jobRoot 'Output\unity-report.json'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (!(Test-Path -LiteralPath $reportPath -PathType Leaf) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (!(Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw "The material identity job did not complete: $jobRoot" }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    Add-Check 'material-identity-native-reload' ($report.success -and $report.validationScope -eq 'material_identity_contracts' -and
        $report.jobId -eq $id -and $report.processId -eq $ready.processId -and $report.mainThread -eq $ready.mainThread -and
        @($report.identityContracts).Count -eq 2 -and $report.activeScenePathBefore -eq $report.activeScenePathAfter -and
        $report.activeSceneDirtyBefore -eq $report.activeSceneDirtyAfter -and $report.sceneCountBefore -eq $report.sceneCountAfter) $reportPath
    foreach ($kind in @('shader', 'texture')) {
        $contract = @($report.identityContracts | Where-Object kind -eq $kind)
        Add-Check ('material-reject-same-name-' + $kind) ($contract.Count -eq 1 -and $contract[0].rejected -and
            $contract[0].expectedName -eq $contract[0].alternativeName -and $contract[0].expectedAsset -ne $contract[0].alternativeAsset -and
            $contract[0].expectedInstanceId -ne $contract[0].alternativeInstanceId -and $contract[0].rejection) $reportPath
    }
}

$resultPath = Join-Path $jobs ('contracts-' + [guid]::NewGuid().ToString('N') + '.json')
$completed = $false
$failure = $null
try {
    if (!$MaterialsOnly) {
        foreach ($iteration in 1..2) { Invoke-NativeBuild $iteration }
        foreach ($kind in @('changed-input', 'schema', 'identity', 'interrupted')) { Submit-RejectedJob $kind }
        $source = Join-Path $project 'Assets\Editor\AssetBundleBuilder.cs'
        $originalBytes = [IO.File]::ReadAllBytes($source)
        $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
        $assemblyTime = (Get-Item -LiteralPath (Join-Path $project 'Library\ScriptAssemblies\Assembly-CSharp-Editor.dll')).LastWriteTimeUtc
        try {
            [IO.File]::AppendAllText($source, ([Environment]::NewLine + '// Reload contract ' + [guid]::NewGuid().ToString('N') + [Environment]::NewLine))
            $changedHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
            $assemblyTime = Wait-ReloadedSource $changedHash $assemblyTime
            Add-Check 'changed-worker-self-reloaded' $true $changedHash
        }
        finally {
            [IO.File]::WriteAllBytes($source, $originalBytes)
            $null = Wait-ReloadedSource $sourceHash $assemblyTime
            Add-Check 'original-worker-self-reloaded' $true $sourceHash
        }
        Invoke-NativeBuild 'after-reload'
    }
    if (![string]::IsNullOrWhiteSpace($MaterialRecipePath)) { Test-MaterialJobs; Test-MaterialIdentity }
    $process = Get-Process -Id $ready.processId
    Add-Check 'existing-editor-kept-running' ($process.StartTime.ToUniversalTime() -eq $originalStart) $process.Id
    if ($fbx) { Add-Check 'source-fbx-unchanged' ((Get-FileHash -LiteralPath $fbx -Algorithm SHA256).Hash.ToLowerInvariant() -eq $inputHash) $inputHash }
    $completed = $true
}
catch {
    $failure = $_.Exception.ToString()
    throw
}
finally {
    [IO.File]::WriteAllText($resultPath, (@{ schemaVersion = 1; checks = @($checks.ToArray()); passed = $completed -and @($checks | Where-Object { !$_.passed }).Count -eq 0; error = $failure;
        unityVersion = $ready.unityVersion; processId = $ready.processId; completedUtc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json -Depth 8))
    Write-Output "Unity worker contract results: $resultPath"
}
