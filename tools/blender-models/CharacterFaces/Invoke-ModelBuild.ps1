# Resumable character-model recipe closure; Unity contracts stay in the shared worker.
[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string[]]$PlanPath,
 [Parameter(Mandatory=$true)][string]$OutputDirectory,
 [Parameter(Mandatory=$true)][string]$GuiProjectPath,
 [Parameter(Mandatory=$true)][string]$UnityPath,
 [ValidateRange(2,16)][int]$ChunkSize=8,
 [switch]$ValidateOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repository=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../..')).Path
$planPaths=@($PlanPath|ForEach-Object{(Resolve-Path -LiteralPath $_).Path})
$plans=@($planPaths|ForEach-Object{Get-Content -LiteralPath $_ -Raw|ConvertFrom-Json})
$planIdentities=@($planPaths|ForEach-Object{[pscustomobject]@{path=$_;sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()}})
$jobs=@($plans|ForEach-Object{$_.jobs})
if(-not $jobs.Count -or @($jobs|Group-Object outputFile|Where-Object Count -NE 1).Count){throw 'Every model bundle needs one recipe.'}
$root=[IO.Path]::GetFullPath($OutputDirectory)
$toolsPrefix=(Join-Path $repository 'tools')+[IO.Path]::DirectorySeparatorChar
if(-not $root.StartsWith($toolsPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Model build output must stay inside tools.'}
$progress=Join-Path $root 'Actual.json';$stop=Join-Path $root 'STOP'
$entry=Join-Path $repository 'tools/asset-authoring/Invoke-UnityAssetBuild.ps1'
$project=(Resolve-Path -LiteralPath $GuiProjectPath).Path
$editor=(Resolve-Path -LiteralPath $UnityPath).Path
$worker=Join-Path $repository 'tools/asset-authoring/UnityAssetBuild/Assets/Editor/AssetBundleBuilder.cs'
$workerHash=(Get-FileHash -LiteralPath $worker -Algorithm SHA256).Hash.ToLowerInvariant()
[void][IO.Directory]::CreateDirectory((Join-Path $root 'Reports'));[void][IO.Directory]::CreateDirectory((Join-Path $root 'Built'))
$results=[Collections.Generic.List[object]]::new();$chunks=[Collections.Generic.List[object]]::new()
$session=$null;$shutdown=$false;$batchError=$null;$paused=$false
function Save-Progress([string]$Status){
 $record=[ordered]@{status=$Status;plans=$planPaths;planInputs=$planIdentities;workerSha256=$workerHash;editorNormallyReleased=$shutdown;
  session=$(if($session){$session.RunDirectory}else{$null});expectedJobs=$jobs.Count;completedJobs=$results.Count;
  chunkSize=$ChunkSize;chunks=@($chunks.ToArray());results=@($results.ToArray());error=$batchError;updatedUtc=[DateTime]::UtcNow.ToString('O')}
 $temporary=$progress+'.writing'
 [IO.File]::WriteAllText($temporary,($record|ConvertTo-Json -Depth 15),[Text.UTF8Encoding]::new($false))
 Move-Item -LiteralPath $temporary -Destination $progress -Force
}
foreach($plan in $plans){if((Get-FileHash -LiteralPath $plan.baseManifest.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $plan.baseManifest.sha256){throw 'The admitted original public catalog changed.'}}
foreach($job in $jobs){if((Get-FileHash -LiteralPath $job.recipe.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $job.recipe.sha256){throw 'A frozen numeric recipe changed.'}}
if($ValidateOnly){Write-Output ('PASS frozen inputs '+$jobs.Count);return}
if(Test-Path -LiteralPath $progress){
 $old=Get-Content -LiteralPath $progress -Raw|ConvertFrom-Json
 if((ConvertTo-Json -InputObject @($old.planInputs) -Compress) -cne (ConvertTo-Json -InputObject $planIdentities -Compress)){throw 'The resumed recipe plans differ.'}
 if(-not $old.editorNormallyReleased){throw 'The previous editor session is not normally released.'}
 foreach($row in $old.results){$results.Add($row)}
 foreach($row in $old.chunks){$chunks.Add($row)}
}
foreach($row in $results){
 if($row.file -CNotIn @($jobs.outputFile)){throw 'A completed bundle is outside the recipe closure.'}
 if((Get-FileHash -LiteralPath $row.bundlePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $row.sha256){throw 'A completed bundle changed before resume.'}
 $report=Get-Content -LiteralPath $row.report -Raw|ConvertFrom-Json
 if(-not $report.success){throw 'A completed member report did not pass.'}
}
if($results.Count -eq $jobs.Count){$shutdown=$true;Save-Progress 'COMPLETE_HUMANOID_ENGINE_BATCH';return}
if(Test-Path -LiteralPath $stop){$shutdown=$true;Save-Progress 'PAUSED_AT_COMPLETED_CHUNK';return}
. (Join-Path $repository 'tools/asset-authoring/UnityAssetBuild/BackgroundEditor.ps1')
try{
 $session=Start-AssetBackgroundEditor -Project $project -Editor $editor -Cli (Join-Path $env:LOCALAPPDATA 'Unity/bin/unity.exe') -SourceSha256 $workerHash -TimeoutSeconds 300
 while($results.Count -lt $jobs.Count){
  if(Test-Path -LiteralPath $stop){$paused=$true;break}
  $completedNames=@($results|ForEach-Object{$_.file})
  $remaining=@($jobs|Where-Object outputFile -CNotIn $completedNames)
  $selected=@($remaining|Select-Object -First $ChunkSize)
  Save-Progress 'RUNNING_HUMANOID_ENGINE_BATCH'
  $jobsRoot=Join-Path $project '.asset-authoring-jobs'
  $filter=$(if($selected.Count -gt 1){'*-mesh-batch'}else{'*-mesh'})
  $before=@(Get-ChildItem -LiteralPath $jobsRoot -Directory -Filter $filter|Select-Object -ExpandProperty Name)
  $timer=[Diagnostics.Stopwatch]::StartNew()
  & $entry -UnityPath $editor -MeshRecipePath ([string[]]@($selected|ForEach-Object{$_.recipe.path})) -GuiProjectPath $project -TimeoutSeconds 1200|Out-Null
  $timer.Stop()
  $created=@(Get-ChildItem -LiteralPath $jobsRoot -Directory -Filter $filter|Where-Object Name -NotIn $before)
  if($created.Count -ne 1){throw 'A chunk did not produce exactly one owned job.'}
  $rootReport=Join-Path $created[0].FullName 'Output/unity-report.json'
  $report=Get-Content -LiteralPath $rootReport -Raw|ConvertFrom-Json
  if(-not $report.success){throw 'The common worker did not pass.'}
  $children=$(if($selected.Count -gt 1){@($report.bundles)}else{@($report)})
  if($children.Count -ne $selected.Count -or ($selected.Count -gt 1 -and $report.buildPipelineInvocations -ne 1)){throw 'A chunk failed its bundle closure.'}
  $archive=Join-Path $root ('Built/'+$created[0].Name)
  $proofRoot=Join-Path $root ('Reports/'+$created[0].Name)
  [void][IO.Directory]::CreateDirectory($archive);[void][IO.Directory]::CreateDirectory($proofRoot)
  for($index=0;$index -lt $selected.Count;$index++){
   $job=$selected[$index];$child=$children[$index]
   if(-not $child.success -or @($child.meshes).Count -ne $job.meshes -or @($child.sourceAssets).Count -ne $job.sourceAssets -or $child.sourceBundlesLoaded -ne $job.sourceBundles -or $child.sourceBundlesReleased -ne $job.sourceBundles){throw ('The member source closure failed: '+$job.outputFile)}
   $target=Join-Path $archive $job.outputFile;$proof=Join-Path $proofRoot ($job.outputFile+'.json')
   if(Test-Path -LiteralPath $target){throw 'The completed output already exists.'}
   New-Item -ItemType HardLink -Path $target -Target $child.bundlePath|Out-Null
   if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -cne $child.bundleSha256){throw 'The archived candidate differs from its report.'}
   $child|ConvertTo-Json -Depth 90|Set-Content -LiteralPath $proof -Encoding UTF8
   $results.Add([pscustomobject]@{file=$job.outputFile;bundlePath=$target;sha256=$child.bundleSha256;bytes=(Get-Item -LiteralPath $target).Length;report=$proof;originalJob=$created[0].FullName;memberIndex=$index})
   Save-Progress 'RUNNING_HUMANOID_ENGINE_BATCH'
  }
  $chunks.Add([pscustomobject]@{job=$created[0].FullName;report=$rootReport;members=@($selected.outputFile);wrapperSeconds=$timer.Elapsed.TotalSeconds;buildPipelineInvocations=1})
  Save-Progress 'RUNNING_HUMANOID_ENGINE_BATCH'
  Write-Output ('Completed '+$results.Count+'/'+$jobs.Count+'; chunk '+$chunks.Count+' in '+[math]::Round($timer.Elapsed.TotalSeconds,2)+' seconds')
 }
}catch{$batchError=$_.Exception.ToString();throw}
finally{
 try{if($session){Stop-AssetBackgroundEditor $session;$shutdown=$true}}
 catch{if(-not $batchError){$batchError=$_.Exception.ToString()};throw}
 finally{Save-Progress $(if($batchError){'FAIL'}elseif($paused){'PAUSED_AT_COMPLETED_CHUNK'}elseif($shutdown -and $results.Count -eq $jobs.Count){'COMPLETE_HUMANOID_ENGINE_BATCH'}else{'FAIL'})}
}
