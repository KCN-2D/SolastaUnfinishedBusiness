[CmdletBinding(DefaultParameterSetName='Build')]
param(
    [string]$BlenderPath,
    [string]$OutputRoot,
    [string]$PipelinePath,
    [string]$QualityProfilePath,
    [Parameter(Mandatory=$true,ParameterSetName='Cleanup')][switch]$Cleanup,
    [string[]]$DependencyPath=@(),
    [string[]]$SourcePath=@(),
    [string[]]$ReferencePath=@(),
    [string[]]$PreserveDirectory=@(),
    [ValidateRange(1,100)][int]$KeepLatest=2,
    [ValidateRange(0,36500)][double]$MaxAgeDays=0,
    [ValidateRange(0,9223372036854775807)][long]$MaxRetainedBytes=0,
    [switch]$Apply,
    [ValidateNotNullOrEmpty()]
    [string[]]$Stages = @('generate', 'verify-static', 'verify-rig'),
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_-]*$')]
    [string]$ResultName,
    [ValidateRange(1, 64)]
    [int]$Threads = 2
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$workbench = Join-Path $PSScriptRoot 'workspace'
. (Join-Path $PSScriptRoot 'UnityAssetBuild/BackgroundEditor.ps1')
function Clear-BlenderAuthoringOutputs {
    [CmdletBinding()]
    param([string]$Root,[string[]]$ReferenceFiles,[string[]]$Preserve,[int]$Latest,[double]$Age,[long]$Budget,[switch]$Delete)
    $storageOperation=Enter-AssetStorageOperation $Root
    try{

    $root=Get-AssetStoragePath $Root
    $base=Get-AssetStoragePath $workbench
    if($root -eq $base -or -not(Test-AssetStorageWithin $root $base)-or $root.Substring($base.Length) -match '(^|[\\/])(Tools|UnityWorkspace|Mods|Solasta_Data)([\\/]|$)'){throw 'Blender cleanup requires an output directory below the authoring workspace.'}
    $references=Get-AssetStorageReferences $ReferenceFiles
    foreach($path in $Preserve){[void]$references.paths.Add((Get-AssetStoragePath $path))}
    $pointers=[Collections.Generic.List[object]]::new()
    foreach($pointer in @(Get-ChildItem -LiteralPath $root -Filter 'last*success.json' -File)){
        $snapshot=Read-AssetStorageJson $pointer.FullName;$current=$snapshot.value
        if($current.status -cne 'passed' -or -not $current.output){throw 'A current Blender success pointer is invalid.'}
        $target=Get-AssetStoragePath $current.output
        if($target -eq $root -or -not(Test-AssetStorageWithin $target $root)){throw 'A Blender success pointer is outside its output scope.'}
        if(@($references.paths|Where-Object{(Test-AssetStorageWithin $pointer.FullName $_)-or(Test-AssetStorageWithin $_ $pointer.FullName)}).Count){[void]$references.paths.Add($target)}
        $pointers.Add([pscustomobject]@{path=$pointer.FullName;target=$target;sha256=$snapshot.sha256})
    }
    $processes=@();$processError=$null
    try{$processes=@(Get-CimInstance Win32_Process -Filter "Name = 'blender.exe'" -Property ProcessId,ExecutablePath,CommandLine,CreationDate)}catch{$processError=$_.Exception.Message}
    $directory=Join-Path $base ('StorageCleanup/Blender/'+[guid]::NewGuid().ToString('N'));[void][IO.Directory]::CreateDirectory($directory)
    $journal=Join-Path $directory 'Journal.jsonl';$reportPath=Join-Path $directory 'Result.json'
    $units=[Collections.Generic.List[object]]::new();$retained=[Collections.Generic.List[object]]::new();$actions=[Collections.Generic.List[object]]::new()
    $report=[ordered]@{schemaVersion=1;status='planning';scope='Completed Blender run.json output directories only; software and undeclared workspace data remain outside cleanup.';
        outputRoot=$root;applied=[bool]$Delete;referenceInputs=$references.inputs;processQueryError=$processError;retainedBytes=$null;budgetSatisfied=$null;actions=@();retained=@();journal='Journal.jsonl'}
    try{
        foreach($child in Get-ChildItem -LiteralPath $root -Directory){
            $manifest=Join-Path $child.FullName 'run.json'
            if(-not [IO.File]::Exists($manifest)){continue}
            $data=$null
            try{
                [void](Get-AssetStoragePath $manifest)
                $snapshot=Read-AssetStorageJson $manifest;$data=$snapshot.value
                if($data.output -ne $child.FullName -or $data.input_archive -isnot [array] -or $data.artifacts_sha256 -isnot [array] -or
                    $data.pipeline_sha256 -cnotmatch '^[a-fA-F0-9]{64}$'){throw 'Unknown Blender output ownership.'}
                $dependencies=@($data.input_archive|ForEach-Object source)
                if($data.PSObject.Properties['dependencies']){$dependencies+=@($data.dependencies)}
                $hard=if($data.status -cnotin @('passed','failed')){'incomplete'}elseif($processError){'unknown-process-query'}else{$null}
                foreach($process in $processes){
                    if(-not $process.CommandLine -or -not $process.ExecutablePath){$hard='unknown-process-identity';break}
                    if($process.CommandLine.IndexOf($child.FullName,[StringComparison]::OrdinalIgnoreCase) -ge 0){$hard='active-output-process';break}
                }
                $completed=if($data.PSObject.Properties['completedUtc'] -and $data.completedUtc){$data.completedUtc}else{$child.LastWriteTimeUtc.ToString('O')}
                $units.Add([pscustomobject]@{path=$manifest;identityHash=$snapshot.sha256;roots=@($child.FullName);dependencies=$dependencies;
                    completedUtc=$completed;bytes=[long]((Get-ChildItem -LiteralPath $child.FullName -File -Recurse|Measure-Object Length -Sum).Sum);hardReason=$hard;data=$data})
            }catch{
                $failure=$_.Exception.Message
                if($null -ne (Get-Variable -Name data -ErrorAction SilentlyContinue)-and $data -and $data.PSObject.Properties['dependencies']){
                    foreach($dependency in @($data.dependencies)){try{[void]$references.paths.Add((Get-AssetStoragePath $dependency -ReadOnlyReference))}catch{}}
                }
                $retained.Add([ordered]@{path=$manifest;reason=$failure})
            }
        }
        $selection=Select-AssetStorageUnits $units.ToArray() $references $Latest $Age $Budget
        $report.retainedBytes=$selection.retainedBytes;$report.budgetSatisfied=$selection.budgetSatisfied
        foreach($unit in $units){if($selection.reasons.ContainsKey($unit.path)){$retained.Add([ordered]@{path=$unit.path;reason=$selection.reasons[$unit.path]})}}
        $pending=[Collections.Generic.List[object]]::new();foreach($unit in @($units|Where-Object{-not $selection.reasons.ContainsKey($_.path)}|Sort-Object completedUtc,path)){$pending.Add($unit)}
        $consumers=Get-AssetStorageConsumers $units.ToArray();$planned=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        while($pending.Count){
            $next=@($pending|Where-Object{$candidate=$_;-not @($consumers[$candidate.path]|Where-Object{[IO.File]::Exists($_)-and ($Delete -or -not $planned.Contains($_))}).Count}|Select-Object -First 1)
            if(-not $next.Count){foreach($unit in $pending){$retained.Add([ordered]@{path=$unit.path;reason='remaining-input-consumer'})};break}
            $unit=$next[0];[void]$pending.Remove($unit)
            $data=$unit.data
            try{
                Assert-AssetStorageReferences $references
                if((Get-FileHash -LiteralPath (Get-AssetStoragePath $unit.path) -Algorithm SHA256).Hash -cne $unit.identityHash){throw 'The Blender output receipt changed.'}
                $expected=@{}
                foreach($entry in $data.artifacts_sha256){
                    if([IO.Path]::IsPathRooted($entry.path)){throw 'Blender output hashes must use relative paths.'}
                    $path=Get-AssetStoragePath (Join-Path $data.output $entry.path)
                    if(-not(Test-AssetStorageWithin $path $data.output)-or $path -eq $unit.path -or $expected.ContainsKey($path)){throw 'Invalid Blender output hash ownership.'}
                    $expected[$path]=$entry.sha256
                }
                foreach($entry in $data.input_archive){
                    $path=Get-AssetStoragePath $entry.archive
                    if(-not(Test-AssetStorageWithin $path $data.output)-or -not $expected.ContainsKey($path)-or $expected[$path] -cne $entry.sha256){throw 'The archived input is outside its signed output.'}
                }
                $actual=@(Get-ChildItem -LiteralPath $data.output -Recurse -Force)
                if(@($actual|Where-Object{$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Blender output contains a reparse point.'}
                $files=@($actual|Where-Object{-not $_.PSIsContainer -and $_.FullName -ne $unit.path})
                if($files.Count -ne $expected.Count){throw 'The Blender output file set changed.'}
                foreach($file in $files){if(-not $expected.ContainsKey($file.FullName)-or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -cne $expected[$file.FullName]){throw 'A Blender output changed after completion.'}}
                $action=[ordered]@{path=$data.output;bytes=$unit.bytes;applied=$false};$actions.Add($action)
                Write-AssetStorageEvent $journal ([ordered]@{event='retire-output';path=$data.output;receiptSha256=$unit.identityHash;status=$data.status;error=$(if($data.PSObject.Properties['error']){$data.error}else{$null});pipelineSha256=$data.pipeline_sha256;inputArchive=$data.input_archive;action=$action})
                foreach($pointer in @($pointers|Where-Object target -eq $data.output)){
                    if((Get-FileHash -LiteralPath (Get-AssetStoragePath $pointer.path) -Algorithm SHA256).Hash -cne $pointer.sha256){throw 'A Blender success pointer changed during cleanup.'}
                    Write-AssetStorageEvent $journal @{event='retire-pointer';path=$pointer.path;target=$pointer.target;sha256=$pointer.sha256}
                    if($Delete){Assert-AssetStorageReferences $references;Remove-Item -LiteralPath $pointer.path -Force;Write-AssetStorageEvent $journal @{event='pointer-retired';path=$pointer.path}}
                }
                if($Delete){Assert-AssetStorageReferences $references;Write-AssetStorageEvent $journal @{event='delete-planned';path=$data.output};Remove-Item -LiteralPath (Get-AssetStoragePath $data.output) -Recurse -Force;$action.applied=$true;Write-AssetStorageEvent $journal @{event='delete-complete';path=$data.output}}
                [void]$planned.Add($unit.path)
            }catch{$retained.Add([ordered]@{path=$unit.path;reason=$_.Exception.Message});if($Delete){throw}}
        }
        $report.status='complete'
    }catch{$report.status='failed';$report['error']=$_.Exception.Message;throw}
    finally{
        if($null -ne (Get-Variable -Name planned -ErrorAction SilentlyContinue)){
            $report.retainedBytes=Get-AssetStorageRemainingBytes $units.ToArray() $planned ([bool]$Delete)
            $report.budgetSatisfied=if($Budget -gt 0){$report.retainedBytes -le $Budget}else{$null}
        }
        $report.actions=@($actions.ToArray());$report.retained=@($retained.ToArray());Write-AssetWorkerJson $reportPath $report
    }
    return $reportPath

    }finally{$storageOperation.ReleaseMutex();$storageOperation.Dispose()}
}
if($Cleanup){
    if($SourcePath.Count){throw '-SourcePath requires a build operation.'}
    if(-not $OutputRoot){$OutputRoot=Join-Path $workbench 'Output'}
    Clear-BlenderAuthoringOutputs $OutputRoot $ReferencePath $PreserveDirectory $KeepLatest $MaxAgeDays $MaxRetainedBytes -Delete:$Apply
    return
}
if($Apply -or $ReferencePath.Count -or $PreserveDirectory.Count -or $MaxAgeDays -gt 0 -or $MaxRetainedBytes -gt 0){throw 'Storage policy arguments require -Cleanup.'}
$declaredSources = @($SourcePath | ForEach-Object {
    $source = Get-AssetStoragePath $_ -ReadOnlyReference
    if (-not [IO.File]::Exists($source)) { throw "SourcePath must name an existing file: $source" }
    $source
} | Select-Object -Unique)
if (-not $BlenderPath) {
    $BlenderPath = Join-Path $workbench 'Tools\blender-4.5.9-windows-x64\blender.exe'
}
if (-not (Test-Path -LiteralPath $BlenderPath -PathType Leaf)) {
    throw "Blender is missing. Supply -BlenderPath; this script does not install software: $BlenderPath"
}
$BlenderPath = (Resolve-Path -LiteralPath $BlenderPath).Path
$versionLines = @(& $BlenderPath --version)
if ($LASTEXITCODE -ne 0 -or $versionLines.Count -eq 0 -or $versionLines[0] -ne 'Blender 4.5.9 LTS') {
    throw 'This workflow requires the verified Blender 4.5.9 LTS. See README.md.'
}
if (-not $OutputRoot) { $OutputRoot = Join-Path $workbench 'Output' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
# Keep all generated files in the disposable authoring workspace. Neither an
# installed game nor the mod source tree can be selected as an output target.
$workbenchPrefix = $workbench.TrimEnd('\') + '\'
if (-not $OutputRoot.StartsWith($workbenchPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must be below the authoring workspace: $workbench"
}
$relativeOutput = $OutputRoot.Substring($workbenchPrefix.Length)
if ($relativeOutput -match '(^|[\\/])(Tools|UnityWorkspace|Mods|Solasta_Data)([\\/]|$)') {
    throw 'OutputRoot overlaps software, Unity workspace, or game files.'
}
for ($ancestor = $OutputRoot; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
    if (Test-Path -LiteralPath $ancestor) {
        $item = Get-Item -LiteralPath $ancestor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "OutputRoot cannot pass through a junction or symlink: $ancestor"
        }
    }
}
$storageOperation=Enter-AssetStorageOperation $OutputRoot
try {
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$runName = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$runDirectory = Join-Path $OutputRoot $runName
New-Item -ItemType Directory -Path $runDirectory | Out-Null
$registration=[ordered]@{schemaVersion=1;status='running';output=$runDirectory;dependencies=@($DependencyPath)+@($declaredSources)+@($PipelinePath,$QualityProfilePath)|Where-Object{$_};input_archive=@();artifacts_sha256=@();pipeline_sha256=$null}
Write-AssetWorkerJson (Join-Path $runDirectory 'run.json') $registration
if (-not $PipelinePath) { $PipelinePath = Join-Path $PSScriptRoot 'blender_pipeline.py' }
$pipeline = (Resolve-Path -LiteralPath $PipelinePath).Path
if (-not (Test-Path -LiteralPath $pipeline -PathType Leaf)) { throw "Missing pipeline: $pipeline" }
if ([IO.Path]::GetExtension($pipeline) -ne '.py') { throw 'PipelinePath must name a Python script.' }
if ($Stages.Count -eq 0 -or @($Stages | Where-Object { $_ -notmatch '^[a-z][a-z0-9_-]*$' }).Count -gt 0 -or
    @($Stages | Select-Object -Unique).Count -ne $Stages.Count) {
    throw 'Stages must contain distinct stage names using lowercase letters, numbers, underscores, or hyphens.'
}
$validationPipeline = Join-Path $PSScriptRoot 'blender_pipeline.py'
$executionPlan = @($Stages | ForEach-Object { [pscustomobject]@{stage=$_;pipeline=$pipeline;arguments=@()} })
if ($QualityProfilePath) {
    $QualityProfilePath = (Resolve-Path -LiteralPath $QualityProfilePath).Path
    if (-not (Test-Path -LiteralPath $QualityProfilePath -PathType Leaf) -or
        [IO.Path]::GetExtension($QualityProfilePath) -ne '.json') { throw 'QualityProfilePath must name a JSON file.' }
    $qualityProfile = Get-Content -LiteralPath $QualityProfilePath -Raw | ConvertFrom-Json
    if ($qualityProfile.schema_version -ne 1 -or -not $qualityProfile.required_stages -or
        'validate-quality' -in $Stages) { throw 'Invalid quality profile version, required stages, or reserved stage.' }
    $previousIndex = -1
    foreach ($requiredStage in $qualityProfile.required_stages) {
        $index = [array]::IndexOf($Stages, [string]$requiredStage)
        if ($index -le $previousIndex) { throw "Quality profile requires stage '$requiredStage' in the declared order." }
        $previousIndex = $index
    }
    $executionPlan += [pscustomobject]@{stage='validate-quality';pipeline=$validationPipeline;
        arguments=@('--quality-profile',$QualityProfilePath)}
}
$successFile = if ($ResultName) { "last-$ResultName-success.json" } else { 'last-success.json' }

$run = [ordered]@{
    schemaVersion = 1
    startedUtc = [DateTime]::UtcNow.ToString('O')
    completedUtc = $null
    artifacts_sha256 = @()
    creator = @{pid=$PID;path=(Get-Process -Id $PID).Path;startTimeUtc=(Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('O')}
    status = 'running'
    pipeline = $pipeline
    requested_stages = $Stages
    stages = @($executionPlan | ForEach-Object stage)
    blender = $versionLines[0]
    blender_executable = $BlenderPath
    pipeline_sha256 = (Get-FileHash -LiteralPath $pipeline -Algorithm SHA256).Hash
    validation_sha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'blender_pipeline.py') -Algorithm SHA256).Hash
    threads = $Threads
    output = $runDirectory
    completed_stages = @()
    stage_results = @()
    unity = 'not_run'
    game = 'not_run'
    validation_scope = if ($QualityProfilePath) { 'blender_quality_profile' } else { 'declared_pipeline_stages' }
    quality = [ordered]@{status=if ($QualityProfilePath) { 'pending' } else { 'not_requested' };
        profile=$QualityProfilePath;report=$null}
}
$runReport = Join-Path $runDirectory 'run.json'
$run['dependencies']=@(@($DependencyPath)+@($declaredSources)|ForEach-Object{Get-AssetStoragePath $_ -ReadOnlyReference}|Select-Object -Unique)
$inputHashes = @{}
$inputHashes[$pipeline] = $run.pipeline_sha256
$inputHashes[(Join-Path $PSScriptRoot 'blender_pipeline.py')] = $run.validation_sha256
if ($QualityProfilePath) { $inputHashes[$QualityProfilePath] = (Get-FileHash -LiteralPath $QualityProfilePath -Algorithm SHA256).Hash }
foreach ($source in $declaredSources) { $inputHashes[$source] = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash }
$archiveDirectory = Join-Path $runDirectory 'InputSources'
New-Item -ItemType Directory -Path $archiveDirectory | Out-Null
$run['input_archive'] = @()
$sourceIndex = 0
foreach ($inputPath in $inputHashes.Keys) {
    $sourceIndex++
    $archive = Join-Path $archiveDirectory ('{0:D2}-{1}' -f $sourceIndex, [IO.Path]::GetFileName($inputPath))
    Copy-Item -LiteralPath $inputPath -Destination $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $inputHashes[$inputPath]) { throw 'Archived authoring source hash mismatch.' }
    $run.input_archive += [ordered]@{source=$inputPath;archive=$archive;sha256=$inputHashes[$inputPath]}
}
function Assert-InputsUnchanged {
    foreach ($inputPath in $inputHashes.Keys) {
        if ((Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash -ne $inputHashes[$inputPath]) {
            throw "Authoring input changed during this run: $inputPath"
        }
    }
}
Write-AssetWorkerJson $runReport $run
try {
    foreach ($step in $executionPlan) {
        $stage = $step.stage
        $stageTimer = [Diagnostics.Stopwatch]::StartNew()
        Assert-InputsUnchanged
        Write-Host "Running $stage ($($versionLines[0]))"
        $blenderArgs = @(
            '--background', '--factory-startup', '--disable-autoexec', '--offline-mode',
            '--threads', [string]$Threads, '--python-exit-code', '1',
            '--python-expr', 'import sys; sys.dont_write_bytecode = True', '--python', $step.pipeline,
            '--', '--stage', $stage, '--output', $runDirectory
        )
        $blenderArgs += $step.arguments
        $stageLog = Join-Path $runDirectory ("reports\$stage.log")
        New-Item -ItemType Directory -Path (Split-Path -Parent $stageLog) -Force | Out-Null
        & $BlenderPath @blenderArgs 2>&1 | Tee-Object -FilePath $stageLog
        if ($LASTEXITCODE -ne 0) { throw "Blender stage '$stage' failed with exit code $LASTEXITCODE. Output: $runDirectory" }
        Assert-InputsUnchanged
        $stageReportPath = Join-Path $runDirectory "reports\$stage.json"
        if (-not (Test-Path -LiteralPath $stageReportPath -PathType Leaf)) {
            throw "Stage '$stage' produced no validation report."
        }
        $stageReport = Get-Content -LiteralPath $stageReportPath -Raw | ConvertFrom-Json
        if ($stageReport.status -ne 'passed' -or $stageReport.stage -ne $stage) {
            throw "Stage '$stage' did not report success."
        }
        $run.completed_stages += $stage
        if ($stage -eq 'validate-quality') { $run.quality.status = 'passed'; $run.quality.report = $stageReportPath }
        $stageTimer.Stop()
        $run.stage_results += [ordered]@{stage=$stage;status=$stageReport.status;report=$stageReportPath;
            report_sha256=(Get-FileHash -LiteralPath $stageReportPath -Algorithm SHA256).Hash;
            log=$stageLog;elapsed_seconds=$stageTimer.Elapsed.TotalSeconds;measurements=$stageReport}
    }
    $run.status = 'passed'
}
catch {
    $run.status = 'failed'
    $run.error = $_.Exception.Message
    if ($QualityProfilePath) { $run.quality.status = if ($stage -eq 'validate-quality') { 'failed' } else { 'not_run' } }
    if ($stage -and $stage -notin $run.completed_stages) {
        $failedReport = Join-Path $runDirectory "reports\$stage.json"
        $measurements = $null
        if (Test-Path -LiteralPath $failedReport -PathType Leaf) {
            try { $measurements = Get-Content -LiteralPath $failedReport -Raw | ConvertFrom-Json } catch { }
        }
        $stageTimer.Stop()
        $run.stage_results += [ordered]@{stage=$stage;status='failed';report=$failedReport;
            log=$stageLog;elapsed_seconds=$stageTimer.Elapsed.TotalSeconds;measurements=$measurements}
    }
    throw
}
finally {
    $run.completedUtc=[DateTime]::UtcNow.ToString('O')
    $run['artifacts_sha256'] = @(Get-ChildItem -LiteralPath $runDirectory -File -Recurse |
        Where-Object { $_.FullName -ne $runReport } | ForEach-Object {
            [ordered]@{path=$_.FullName.Substring($runDirectory.Length + 1).Replace('\','/');
                sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
        })
    Write-AssetWorkerJson $runReport $run
}
# Only successful runs become the current result. A failed rerun cannot make
# an old report or an old render look like a newly completed validation.
Write-AssetWorkerJson (Join-Path $OutputRoot $successFile) $run
Write-Host "Validated output: $runDirectory"

}finally{$storageOperation.ReleaseMutex();$storageOperation.Dispose()}
