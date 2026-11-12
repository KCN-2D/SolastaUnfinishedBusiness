[CmdletBinding()]
param(
    [string]$ArtifactRoot,
    [string[]]$PreserveDirectory=@(),
    [string[]]$ReferencePath=@(),
    [ValidateRange(0,100)][int]$KeepLatestPerCase=2,
    [ValidateRange(0,36500)][double]$MaxAgeDays=0,
    [ValidateRange(0,9223372036854775807)][long]$MaxRetainedBytes=0,
    [switch]$PruneSuperseded,
    [switch]$ReleaseNativeUserData,
    [switch]$AuditInputValidation,
    [string]$InputProducerPath,
    [string]$GeneratedPayloadPlanPath,
    [switch]$PruneReports,
    [switch]$PruneLooseFiles,
    [ValidateRange(1,100)][int]$KeepCleanupReports=3,
    [switch]$Apply
)
. (Join-Path $PSScriptRoot 'GameTest.Common.ps1')
if(($PruneReports -or $PruneLooseFiles) -and ($PruneSuperseded -or $ReleaseNativeUserData -or $AuditInputValidation -or $GeneratedPayloadPlanPath -or $MaxAgeDays -gt 0 -or $MaxRetainedBytes -gt 0)) {
    throw 'Workspace metadata retention runs separately from payload cleanup.'
}
if ($ReleaseNativeUserData -and ($PruneSuperseded -or $MaxAgeDays -gt 0 -or $MaxRetainedBytes -gt 0)) {
    throw 'Native user-data release is separate from run/build pruning.'
}
if($AuditInputValidation -and ($ReleaseNativeUserData -or $PruneSuperseded -or $MaxAgeDays -gt 0 -or $MaxRetainedBytes -gt 0 -or -not $InputProducerPath)) {
    throw 'Input-validation audit requires its producer source and runs separately from payload cleanup.'
}
if($InputProducerPath -and -not $AuditInputValidation){throw 'InputProducerPath is only used by an input-validation audit.'}
if($GeneratedPayloadPlanPath -and ($ReleaseNativeUserData -or $AuditInputValidation -or $PruneSuperseded -or $MaxAgeDays -gt 0 -or $MaxRetainedBytes -gt 0)) {
    throw 'Generated payload retirement runs separately from run/build cleanup.'
}
function Test-ArtifactEnvironment([string]$Path) {
    return [IO.File]::Exists([IO.Path]::Combine($Path,'tools','game-test','GameTest.Common.ps1'))
}
if (-not $ArtifactRoot) { $ArtifactRoot=$script:GameTestArtifactRoot }
$root=Get-GtPath $ArtifactRoot
if (-not (Test-GtWithin $root $script:GameTestArtifactRoot)) { throw 'Cleanup is limited to tools/game-test/artifacts.' }
$ancestor=$root
while ($ancestor -ne $script:GameTestArtifactRoot) {
    if (Test-ArtifactEnvironment $ancestor) { throw 'Copied tool environments must be cleaned by their own tools.' }
    $ancestor=Split-Path -Parent $ancestor
}
if (Test-GtWithin $root (Join-Path $script:GameTestArtifactRoot '.native')) {
    throw 'Native user-data folders must be cleaned with their owning evidence run.'
}
if($PruneLooseFiles -and $root -ne $script:GameTestArtifactRoot){throw 'Loose files are only retired at the workspace artifact root.'}
if($PruneReports -or $PruneLooseFiles -or $GeneratedPayloadPlanPath){Assert-GtPlainPath $root}else{Assert-GtTree $root}
$preserved=@($PreserveDirectory | ForEach-Object { Get-GtPath $_ })
foreach ($path in $preserved) {
    if (-not (Test-GtWithin $path $script:GameTestArtifactRoot)) { throw 'Preserved paths must be artifact directories.' }
}
$storageReferences=Read-GtStorageReferences $ReferencePath
$referenced=$storageReferences.paths
$referenceInputs=$storageReferences.inputs
$referenceAncestors=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $referenced) {
    $cursor=$entry
    while ($cursor) {
        if (-not $referenceAncestors.Add($cursor)) { break }
        $cursor=[IO.Path]::GetDirectoryName($cursor)
    }
}
function Test-Referenced([string]$Path) {
    $cursor=Get-GtPath $Path
    if ($referenceAncestors.Contains($cursor)) { return $true }
    while ($cursor) {
        if ($referenced.Contains($cursor)) { return $true }
        $cursor=[IO.Path]::GetDirectoryName($cursor)
    }
    return $false
}
function Assert-ArtifactGameStopped {
    $state=Get-VerificationProcessState -Name Solasta
    if (-not $state.querySucceeded -or $state.matches.Count) { throw 'Artifact deletion requires a verified stopped game.' }
}
function Assert-ReferencesCurrent { Assert-GtStorageReferences $storageReferences }


function Test-Preserved([string]$Path) {
    foreach ($item in $preserved) {
        if ((Test-GtWithin $Path $item) -or (Test-GtWithin $item $Path)) { return $true }
    }
    return $false
}
function Get-ArtifactBoundary([string]$Path,[switch]$IncludeEmpty) {
    $pending=[Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $pending.Push([IO.DirectoryInfo]::new($Path))
    while ($pending.Count) {
        $directory=$pending.Pop()
        if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point in artifact boundary: $($directory.FullName)" }
        # Re-enumerate every time this function is called. A new consumer or
        # a late Build.json in an existing boundary must remain observable.
        if (Test-ArtifactEnvironment $directory.FullName) { continue }
        $boundary=$false
        foreach ($name in @('Directory.json','Transaction.json','Build.json','Result.json','ToolsAudit.json')) {
            if ([IO.File]::Exists([IO.Path]::Combine($directory.FullName,$name))) { $boundary=$true; break }
        }
        if ($boundary) { $directory; continue }
        if($IncludeEmpty -and -not @($directory.EnumerateFileSystemInfos()).Count) { $directory;continue }
        foreach ($child in $directory.EnumerateDirectories()) {
            if ($child.Name -eq '.native') { continue }
            $pending.Push($child)
        }
    }
}

$lock=Enter-GtLock
$actions=[Collections.Generic.List[object]]::new()
$retained=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$retentionReasons=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
$retired=[Collections.Generic.List[object]]::new()
$policy=[ordered]@{keepLatestPerCase=$KeepLatestPerCase;maxAgeDays=$MaxAgeDays;maxRetainedBytes=$MaxRetainedBytes;
    budgetScope='Recognized restored run/build payloads and their owned native user-data companions. Explicit references, unknown/incomplete ownership and required builds remain protected.';
    retainedBytes=$null;budgetSatisfied=$null;keepCleanupReports=$KeepCleanupReports}
$retainedReports=[Collections.Generic.List[object]]::new()
$retainedLooseFiles=[Collections.Generic.List[object]]::new()
$cleanupCompleted=$false
$cleanupError=$null
$report=Join-Path $root ('Storage-Cleanup-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8) + '.json')
$journalPath=[IO.Path]::ChangeExtension($report,'.jsonl')
$journal=$null
$payloadInputLeases=[Collections.Generic.List[IO.FileStream]]::new()
$journalState=[ordered]@{actions=0;retired=0;completed=[Collections.Generic.HashSet[int]]::new();events=0;bytes=[long]0;flushes=0;summaryWrites=0}
function Write-CleanupEvent($Value) {
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Value|ConvertTo-Json -Depth 20 -Compress)+[Environment]::NewLine)
    $journal.Write($bytes,0,$bytes.Length);$journalState.bytes+=$bytes.Length;$journalState.events++
}
function Get-CleanupResultSummary($Result) {
    $summary=[ordered]@{}
    foreach($name in @('status','reason','runName','caseId','startedUtc','completedUtc','passed','failed','incomplete','validationOnly','startupOnly','compilationSucceeded',
        'settingsRestored','displaySettingsRestored','probeRemoved','originalNativeFilesUnchanged','restored','originalSavePreserved','nativeVerificationStatus',
        'hashes','expectedUb','loadedUb','sources','adapterSources','toolSources','execution')) {
        if($name -in $Result.PSObject.Properties.Name){$summary[$name]=$Result.$name}
    }
    if('processCleanup' -in $Result.PSObject.Properties.Name){
        $cleanup=$Result.processCleanup;$identity=[ordered]@{}
        foreach($name in @('expectedProcessId','expectedStartTimeUtc','expectedPath','completed','error')){if($name -in $cleanup.PSObject.Properties.Name){$identity[$name]=$cleanup.$name}}
        $summary['processCleanup']=$identity
    }
    if('environment' -in $Result.PSObject.Properties.Name -and $Result.environment -and 'source' -in $Result.environment.PSObject.Properties.Name){
        $source=$Result.environment.source;$identity=[ordered]@{}
        foreach($name in @('digest','targetCommit','headCommit')){if($name -in $source.PSObject.Properties.Name){$identity[$name]=$source.$name}}
        $summary['source']=$identity
    }
    return $summary
}
function Save-CleanupReport($Action,[switch]$Final) {
    $before=$journalState.events
    while($journalState.retired -lt $retired.Count){$index=$journalState.retired;Write-CleanupEvent ([ordered]@{event='retire-diagnostic';index=$index;record=$retired[$index]});$journalState.retired++}
    while($journalState.actions -lt $actions.Count){$index=$journalState.actions;Write-CleanupEvent ([ordered]@{event='action-planned';index=$index;action=$actions[$index]});$journalState.actions++}
    if($null -ne $Action){
        $index=$actions.IndexOf($Action)
        if($index -ge 0 -and $Action.applied -and $journalState.completed.Add($index)){Write-CleanupEvent ([ordered]@{event='action-complete';index=$index})}
    }
    if($Final){Write-CleanupEvent ([ordered]@{event=$(if($cleanupCompleted){'cleanup-complete'}else{'cleanup-error'});utc=[datetime]::UtcNow.ToString('O');error=$cleanupError})}
    if($journalState.events -gt $before){$journal.Flush($true);$journalState.flushes++}
    if(-not $Final){return}
    $summary=[ordered]@{schemaVersion=1;utc=[datetime]::UtcNow.ToString('O');applied=[bool]$Apply;completed=$cleanupCompleted;error=$cleanupError;
        bytes=[long]($actions | Measure-Object bytes -Sum | ForEach-Object { $_.Sum });policy=$policy;referenceInputs=$referenceInputs;
        actions=@($actions.ToArray());retiredRuns=@($retired.ToArray());retainedDirectories=@(foreach ($path in $retentionReasons.Keys | Sort-Object) { [ordered]@{path=$path;reason=$retentionReasons[$path]} });
        retainedCleanupReports=@($retainedReports.ToArray());retainedLooseFiles=@($retainedLooseFiles.ToArray());journal=[ordered]@{path=[IO.Path]::GetFileName($journalPath);events=$journalState.events;bytes=$journalState.bytes;flushes=$journalState.flushes};summaryWrites=1}
    Write-GtJson $report $summary
    $journalState.summaryWrites++
}
function Invoke-CleanupReportRetention {
    $closed=[Collections.Generic.List[object]]::new()
    foreach($file in [IO.Directory]::EnumerateFiles($root,'Storage-Cleanup-*.json',[IO.SearchOption]::TopDirectoryOnly)) {
        if($file -eq $report -or [IO.Path]::GetFileName($file) -notmatch '^Storage-Cleanup-\d{8}-\d{6}-\d{3}(?:-[a-f0-9]{8})?\.json$'){continue}
        $leases=[Collections.Generic.List[IO.FileStream]]::new()
        try {
            if((Test-Preserved $file) -or (Test-Referenced $file)){$retainedReports.Add(@{path=$file;reason='referenced'});continue}
            Assert-GtFile $file
            $leases.Add([IO.File]::Open($file,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read))
            $value=Read-GtObject $file
            $names=$value.PSObject.Properties.Name
            if('schemaVersion' -notin $names -or $value.schemaVersion -ne 1 -or 'actions' -notin $names -or $value.actions -isnot [array] -or
                'utc' -notin $names -or 'applied' -notin $names -or $value.applied -isnot [bool]){throw 'Unrecognized cleanup report.'}
            $completed=[datetime]::Parse($value.utc,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind)
            $absentTargets=@()
            if('completed' -in $names -and ($value.completed -isnot [bool] -or -not $value.completed)){throw 'Cleanup report has unfinished actions.'}
            if($value.applied){
                foreach($pending in @($value.actions|Where-Object { $_.kind -ne 'retained' -and -not $_.applied })){
                    # Old summaries did not record overall completion. A later
                    # run can have retired every target left pending there.
                    if('completed' -in $names -or $pending.kind -cnotin @('native-user-data','superseded-run','restored-backup','empty-directory','generated-payload') -or
                        $pending.path -isnot [string] -or -not [IO.Path]::IsPathRooted($pending.path)){throw 'Cleanup report has unfinished actions.'}
                    $target=Get-GtPath $pending.path
                    if($target -eq $script:GameTestArtifactRoot -or -not(Test-GtWithin $target $script:GameTestArtifactRoot) -or
                        (Test-Path -LiteralPath $target) -or (Test-Preserved $target) -or (Test-Referenced $target)){throw 'Cleanup report has unfinished actions.'}
                    $absentTargets+= $target
                }
            }
            $files=@($file);$paired=[IO.Path]::ChangeExtension($file,'.jsonl')
            if([IO.File]::Exists($paired)){
                if('journal' -notin $names -or $value.journal.path -cne [IO.Path]::GetFileName($paired)){throw 'Cleanup journal has no matching summary.'}
                Assert-GtFile $paired
                if((Test-Preserved $paired) -or (Test-Referenced $paired)){throw 'Cleanup journal is referenced.'}
                $leases.Add([IO.File]::Open($paired,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read))
                if([IO.FileInfo]::new($paired).Length -ne $value.journal.bytes){throw 'Cleanup journal differs from its final summary.'}
                $files+= $paired
            }elseif('journal' -in $names){throw 'Cleanup report is missing its journal.'}
            $snapshot=@(foreach($path in $files){$info=[IO.FileInfo]::new($path);[pscustomobject]@{path=$path;sha256=Get-GtHash $path;bytes=$info.Length;lastWriteTicks=$info.LastWriteTimeUtc.Ticks}})
            $closed.Add([pscustomobject]@{path=$file;completed=$completed.ToUniversalTime();files=$snapshot;absentTargets=$absentTargets})
        }catch{$retainedReports.Add(@{path=$file;reason=$_.Exception.Message})}
        finally{foreach($handle in $leases){$handle.Dispose()}}
    }
    foreach($file in [IO.Directory]::EnumerateFiles($root,'Storage-Cleanup-*.jsonl',[IO.SearchOption]::TopDirectoryOnly)){
        if($file -eq $journalPath -or [IO.File]::Exists([IO.Path]::ChangeExtension($file,'.json')) -or
            [IO.Path]::GetFileName($file) -notmatch '^Storage-Cleanup-\d{8}-\d{6}-\d{3}(?:-[a-f0-9]{8})?\.jsonl$'){continue}
        $handle=$null
        try{
            Assert-GtFile $file
            if((Test-Preserved $file) -or (Test-Referenced $file)){throw 'Cleanup journal is referenced.'}
            $handle=[IO.File]::Open($file,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            $events=@([IO.File]::ReadLines($file)|Where-Object {-not [string]::IsNullOrWhiteSpace($_)}|ForEach-Object {$_|ConvertFrom-Json})
            if(-not $events.Count -or $events[0].event -cne 'cleanup-start' -or $events[0].schemaVersion -ne 1 -or $events[0].applied -isnot [bool]){throw 'Unrecognized cleanup journal.'}
            $planned=@{};$finished=[Collections.Generic.HashSet[int]]::new()
            foreach($journalEvent in $events){
                switch -CaseSensitive ($journalEvent.event){
                    'cleanup-start' {if($journalEvent -ne $events[0]){throw 'Repeated cleanup journal header.'}}
                    'retire-diagnostic' {}
                    'action-planned' {if($planned.ContainsKey([int]$journalEvent.index)){throw 'Repeated cleanup action.'};$planned[[int]$journalEvent.index]=$journalEvent.action}
                    'action-complete' {[void]$finished.Add([int]$journalEvent.index)}
                    'cleanup-complete' {}
                    'cleanup-error' {throw 'Cleanup journal records an interrupted operation.'}
                    default {throw 'Unrecognized cleanup journal event.'}
                }
            }
            if(@($finished|Where-Object {-not $planned.ContainsKey($_)}).Count -or ($events[0].applied -and @($planned.Keys|Where-Object {$planned[$_].kind -cne 'retained' -and -not $finished.Contains($_)}).Count)){
                throw 'Cleanup journal has unfinished actions.'
            }
            $info=[IO.FileInfo]::new($file)
            $closed.Add([pscustomobject]@{path=$file;completed=([datetime]$events[0].utc).ToUniversalTime();files=@([pscustomobject]@{path=$file;sha256=Get-GtHash $file;bytes=$info.Length;lastWriteTicks=$info.LastWriteTimeUtc.Ticks})})
        }catch{$retainedReports.Add(@{path=$file;reason=$_.Exception.Message})}
        finally{if($handle){$handle.Dispose()}}
    }
    $ordered=@($closed|Sort-Object completed -Descending)
    for($index=0;$index -lt $ordered.Count;$index++){
        $entry=$ordered[$index]
        if($index -lt $KeepCleanupReports-1){$retainedReports.Add(@{path=$entry.path;reason='latest-cleanup-report'});continue}
        $absentTargets=@(if('absentTargets' -in $entry.PSObject.Properties.Name){$entry.absentTargets})
        $action=[pscustomobject]@{kind='cleanup-report';path=$entry.path;files=$entry.files;absentTargets=@($absentTargets);bytes=[long]($entry.files|Measure-Object bytes -Sum|ForEach-Object Sum);applied=$false}
        Invoke-ClosedArtifactRetirement $action
    }
}
function Invoke-ClosedArtifactRetirement($Action) {
    $actions.Add($Action);Save-CleanupReport $Action
    if(-not $Apply){return}
    Assert-ArtifactGameStopped;Assert-ReferencesCurrent
    $script:GtValidatedDirectories.Clear();Assert-GtPlainPath $root
    foreach($property in @('migrationDestinations','absentTargets')){
        if($property -notin $Action.PSObject.Properties.Name){continue}
        foreach($destination in $Action.$property){
            if((Test-Path -LiteralPath $destination) -or (Test-Referenced $destination) -or (Test-Preserved $destination)){throw 'A retired metadata target acquired a consumer.'}
        }
    }
    $leases=[Collections.Generic.List[IO.FileStream]]::new()
    try {
        foreach($file in $Action.files){
            if([IO.Path]::GetDirectoryName($file.path) -ne $root){throw 'Metadata retirement escaped its artifact root.'}
            Assert-GtFile $file.path
            $leases.Add([IO.File]::Open($file.path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read))
            $info=[IO.FileInfo]::new($file.path)
            if((Test-Preserved $file.path) -or (Test-Referenced $file.path) -or $info.Length -ne $file.bytes -or
                $info.LastWriteTimeUtc.Ticks -ne $file.lastWriteTicks -or (Get-GtHash $file.path) -cne $file.sha256){throw 'Artifact metadata changed before retirement.'}
        }
    } finally {foreach($handle in $leases){$handle.Dispose()}}
    foreach($file in $Action.files){Remove-Item -LiteralPath $file.path -Force}
    $Action.applied=$true;Save-CleanupReport $Action
}
function Invoke-LooseFileRetention {
    # Output logs belong inside their run/build. Only the legacy loose output
    # at the workspace root is eligible, after a full day without writes.
    if($root -ne $script:GameTestArtifactRoot){return}
    $cutoff=[datetime]::UtcNow.AddDays(-1)
    $sourceReferences=Get-GtStorageReferenceText
    foreach($file in [IO.DirectoryInfo]::new($root).EnumerateFiles()){
        if($file.Extension -ine '.log' -and $file.Extension -ine '.txt' -and $file.Name -cne 'Migration.json'){continue}
        $handle=$null;$snapshot=$null;$migrationDestinations=@()
        try {
            Assert-GtFile $file.FullName
            if($file.LastWriteTimeUtc -ge $cutoff){throw 'recent-output'}
            if((Test-Preserved $file.FullName) -or (Test-Referenced $file.FullName) -or
                $sourceReferences.IndexOf($file.Name,[StringComparison]::OrdinalIgnoreCase) -ge 0){throw 'referenced'}
            $handle=[IO.File]::Open($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            if($file.Extension -ieq '.txt'){
                # A legacy current-run pointer contains one absolute artifact
                # path. Notes and other text inputs have no output ownership.
                if($file.Length -gt 4096){throw 'unrecognized-text-input'}
                $pointer=[IO.File]::ReadAllText($file.FullName).Trim()
                if($pointer.Contains("`n") -or $pointer.Contains("`r") -or -not [IO.Path]::IsPathRooted($pointer) -or
                    -not (Test-GtWithin $pointer $root) -or (Get-GtPath $pointer) -eq $root){throw 'unrecognized-text-input'}
            } elseif($file.Name -ceq 'Migration.json') {
                $migration=Read-GtObject $file.FullName
                Assert-GtProperties $migration @('schemaVersion','mappings') @('schemaVersion','mappings')
                if($migration.schemaVersion -ne 1 -or $migration.mappings -isnot [array] -or -not $migration.mappings.Count){throw 'unrecognized-migration'}
                foreach($mapping in $migration.mappings){
                    Assert-GtProperties $mapping @('originalRoot','currentRoot') @('originalRoot','currentRoot')
                    if($mapping.originalRoot -isnot [string] -or -not [IO.Path]::IsPathRooted($mapping.originalRoot) -or
                        $mapping.currentRoot -isnot [string] -or -not [IO.Path]::IsPathRooted($mapping.currentRoot)){throw 'unrecognized-migration'}
                    $destination=Get-GtPath $mapping.currentRoot
                    if($destination -eq $root -or -not(Test-GtWithin $destination $root)){throw 'unrecognized-migration'}
                    if((Test-Path -LiteralPath $destination) -or (Test-Referenced $destination) -or (Test-Preserved $destination)){throw 'live-migration'}
                    $migrationDestinations+= $destination
                }
            }
            $snapshot=[pscustomobject]@{path=$file.FullName;sha256=Get-GtHash $file.FullName;bytes=$file.Length;lastWriteTicks=$file.LastWriteTimeUtc.Ticks}
        } catch {$retainedLooseFiles.Add(@{path=$file.FullName;reason=$_.Exception.Message})}
        finally {if($handle){$handle.Dispose()}}
        if($snapshot){Invoke-ClosedArtifactRetirement ([pscustomobject]@{kind='loose-artifact';path=$file.FullName;files=@($snapshot);bytes=$snapshot.bytes;migrationDestinations=$migrationDestinations;applied=$false})}
    }
}
try {
    $journal=[IO.File]::Open($journalPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    Write-CleanupEvent ([ordered]@{event='cleanup-start';schemaVersion=1;utc=[DateTime]::UtcNow.ToString('O');applied=[bool]$Apply;policy=$policy;referenceInputs=$referenceInputs})
    $journal.Flush($true);$journalState.flushes++
    if($PruneReports -or $PruneLooseFiles){$cleanupCompleted=$true;return}
    if ($GeneratedPayloadPlanPath) {
        $planPath=Get-GtPath $GeneratedPayloadPlanPath
        Assert-GtFile $planPath
        $planHash=Get-GtHash $planPath
        $plan=Read-GtObject $planPath
        Assert-GtProperties $plan @('schemaVersion','artifactRoot','scope','utc','reference','roots','files','logicalBytes','estimatedReclaimableFileBytes','retainedReferencedFiles') @('schemaVersion','artifactRoot','reference','roots','files')
        if ($plan.schemaVersion -ne 1 -or (Get-GtPath $plan.artifactRoot) -ne $root -or $plan.roots -isnot [array] -or $plan.files -isnot [array]) {
            throw 'Generated payload plan does not identify this artifact root.'
        }
        Assert-GtProperties $plan.reference @('path','sha256') @('path','sha256')
        $reference=@($referenceInputs | Where-Object { $_.path -eq (Get-GtPath $plan.reference.path) -and $_.sha256 -ieq $plan.reference.sha256 })
        if ($reference.Count -ne 1) { throw 'The generated payload plan requires its unchanged current-input reference manifest.' }
        # Read leases prevent source-plan/reference replacement during the batch.
        foreach ($inputPath in @($planPath)+@($referenceInputs | ForEach-Object path)) {
            $payloadInputLeases.Add([IO.File]::Open($inputPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read))
        }
        Assert-ReferencesCurrent
        if ((Get-GtHash $planPath) -ne $planHash) { throw 'Generated payload plan changed before validation.' }
        Write-CleanupEvent ([ordered]@{event='generated-payload-plan';path=$planPath;sha256=$planHash})
        $payloadRoots=@(foreach ($entry in $plan.roots) {
            if ($entry -isnot [string] -or [IO.Path]::IsPathRooted($entry)) { throw 'Generated payload roots must be relative artifact directories.' }
            $candidate=Get-GtPath (Join-Path $root $entry)
            if ($candidate -eq $root -or -not (Test-GtWithin $candidate $root) -or
                (Test-GtWithin $candidate (Join-Path $script:GameTestArtifactRoot '.native'))) { throw 'Generated payload root escaped its artifact scope.' }
            Assert-GtTree $candidate
            $candidate
        })
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $checkedPayloadParents=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $extensions=@('.bundle','.bin','.bytes','.gz','.rgba8','.cpu-rgba8','.bc3','.bc7','.mips','.u8','.encoded','.png','.zip')
        foreach ($row in $plan.files) {
            Assert-GtProperties $row @('path','bytes','sha256','lastWriteUtcTicks') @('path','bytes','sha256','lastWriteUtcTicks')
            $path=Get-GtPath $row.path
            $numericPacket=[IO.Path]::GetFileName($path) -cmatch '^[a-f0-9]{64}\.json$'
            if ($path -eq $planPath -or -not $seen.Add($path) -or -not @($payloadRoots | Where-Object { Test-GtWithin $path $_ }).Count -or
                ($extensions -cnotcontains [IO.Path]::GetExtension($path) -and -not $numericPacket) -or
                ($row.bytes -isnot [long] -and $row.bytes -isnot [int]) -or $row.bytes -lt 0 -or
                $row.sha256 -isnot [string] -or $row.sha256 -cnotmatch '^[a-f0-9]{64}$' -or
                ($row.lastWriteUtcTicks -isnot [long] -and $row.lastWriteUtcTicks -isnot [int]) -or
                (Test-Preserved $path) -or (Test-Referenced $path)) { throw "Generated payload is outside the unreferenced declared selection: $path" }
            $ancestor=Split-Path -Parent $path
            while ($ancestor -and $ancestor -ne $root) {
                if ($checkedPayloadParents.Contains($ancestor)) { break }
                if (Test-ArtifactEnvironment $ancestor) { throw 'Copied tool environments must be cleaned by their own tools.' }
                $transactionPath=Join-Path $ancestor 'Transaction.json'
                if (Test-Path -LiteralPath $transactionPath -PathType Leaf) {
                    if ((Read-GtObject $transactionPath).state -cne 'restored') { throw 'Generated payload retirement encountered an unresolved transaction.' }
                }
                [void]$checkedPayloadParents.Add($ancestor)
                $ancestor=Split-Path -Parent $ancestor
            }
            $actions.Add([pscustomobject]@{kind='generated-payload';path=$path;bytes=[long]$row.bytes;sha256=$row.sha256;lastWriteUtcTicks=$row.lastWriteUtcTicks;applied=$false})
        }
        Assert-GtGeneratedPayloadBatch $actions.ToArray()
        Assert-ArtifactGameStopped
        Assert-ReferencesCurrent
        if ((Get-GtHash $planPath) -ne $planHash) { throw 'Generated payload plan changed during validation.' }
        Save-CleanupReport
        if ($Apply) {
            $removedPayloads=0
            foreach ($action in $actions) {
                if ($removedPayloads % 256 -eq 0) { Assert-ArtifactGameStopped }
                Assert-GtFile $action.path
                $item=Get-Item -LiteralPath $action.path -Force
                if ($item.Length -ne $action.bytes -or $item.LastWriteTimeUtc.Ticks -ne $action.lastWriteUtcTicks) { throw 'Generated payload changed before retirement.' }
                Remove-Item -LiteralPath $action.path -Force
                $action.applied=$true
                Save-CleanupReport $action
                $removedPayloads++
            }
        }
        $cleanupCompleted=$true
        return
    }
    $directories=@(Get-ArtifactBoundary $root -IncludeEmpty)
    if($AuditInputValidation) {
        foreach($directory in $directories) {
            $run=$directory.FullName
            if((Test-Preserved $run) -or (Test-Referenced $run) -or
                -not [IO.File]::Exists((Join-Path $run 'Directory.json')) -or
                -not [IO.File]::Exists((Join-Path $run 'Result.json'))) {continue}
            try {
                $identity=Read-GtObject (Join-Path $run 'Directory.json')
                if($identity.layout -cnotin @('GameTest','Runtime')){continue}
                if($identity.layout -ceq 'GameTest' -and -not [IO.File]::Exists((Join-Path $run 'Build.json'))){continue}
                $existing=Read-GtObject (Join-Path $run 'Result.json')
                if($identity.layout -ceq 'Runtime' -and ('validationOnly' -notin $existing.PSObject.Properties.Name -or $existing.validationOnly -isnot [bool] -or -not $existing.validationOnly)){continue}
                if('execution' -in $existing.PSObject.Properties.Name){continue}
                if(Test-Path -LiteralPath (Join-Path $run 'InputValidationAudit.json')) {
                    [void](Get-GtInputValidationRun $run $existing)
                    $retentionReasons[$run]='completed-input-audit';continue
                }
                if($Apply){Assert-ArtifactGameStopped;Assert-ReferencesCurrent}
                $audit=New-GtInputValidationAudit $run $InputProducerPath -Preview:(-not $Apply)
                $action=[pscustomobject]@{kind='input-validation-audit';path=$run;bytes=0;applied=[bool]$Apply;producerSha256=$audit.producer.sha256}
                $actions.Add($action);Save-CleanupReport $action
            } catch {
                $retentionReasons[$run]='input-validation-not-proven'
                $actions.Add([pscustomobject]@{kind='retained';path=$run;bytes=0;applied=$false;reason=$_.Exception.Message})
            }
        }
        $cleanupCompleted=$true
        return
    }
    $backupDirectories=if ($ReleaseNativeUserData) { @() } else { $directories }
    foreach ($directory in $backupDirectories) {
        $run=$directory.FullName
        $manifest=Join-Path $run 'Transaction.json'
        if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { continue }
        if ((Test-Preserved $run) -or (Test-Referenced $run)) { continue }
        try { $transaction=Read-GtObject $manifest }
        catch {
            $retentionReasons[$run]='unrecognized-transaction'
            $actions.Add([pscustomobject]@{kind='retained';path=$manifest;bytes=0;applied=$false;reason=$_.Exception.Message})
            continue
        }
        if ('schemaVersion' -notin $transaction.PSObject.Properties.Name -or $transaction.schemaVersion -ne 1 -or
            'state' -notin $transaction.PSObject.Properties.Name -or $transaction.state -ne 'restored') {
            $retentionReasons[$run]='incomplete-or-unrecognized-transaction'
            continue
        }
        $backup=Join-Path $run 'Backup'
        if (-not (Test-Path -LiteralPath $backup -PathType Container)) { continue }
        try {
            if ($Apply) { Assert-ArtifactGameStopped; Assert-ReferencesCurrent }
            $receipt=Clear-GtRestoredBackup $run -Preview
            $action=[pscustomobject]@{kind='restored-backup';path=$backup;bytes=[long]$receipt.bytes;applied=$false}
            $actions.Add($action);Save-CleanupReport $action
            if($Apply){Assert-ArtifactGameStopped;Assert-ReferencesCurrent;[void](Clear-GtRestoredBackup $run);$action.applied=$true;Save-CleanupReport $action}
        } catch {
            [void]$retained.Add($run)
            $actions.Add([pscustomobject]@{ kind='retained'; path=$backup; bytes=0; applied=$false; reason=$_.Exception.Message })
        }
    }
    if ($ReleaseNativeUserData) {
        foreach ($directory in $directories) {
            if (-not [IO.File]::Exists((Join-Path $directory.FullName 'Result.json'))) { continue }
            $ownsNativeData=$false
            foreach ($location in [IO.Directory]::EnumerateFiles($directory.FullName,'Native-UserDataLocation.json',[IO.SearchOption]::AllDirectories)) { $ownsNativeData=$true;break }
            if (-not $ownsNativeData) { continue }
            $released=Clear-GtNativeUserData -RunDirectory $directory.FullName -ReferencePath $ReferencePath -PreserveDirectory $PreserveDirectory -Preview:(-not $Apply)
            foreach ($entry in $released.actions) {
                $action=[pscustomobject]@{kind='native-user-data';path=$entry.path;run=$directory.FullName;bytes=$entry.bytes;applied=$entry.applied;reason=$entry.reason}
                $actions.Add($action);Save-CleanupReport $action
            }
        }
    }
    if ($PruneSuperseded -or $MaxAgeDays -gt 0 -or $MaxRetainedBytes -gt 0) {
        $referenceText=Get-GtStorageReferenceText
        $runs=@(foreach ($directory in $directories) {
            try {
                $resultPath=Join-Path $directory.FullName 'Result.json'
                $transactionPath=Join-Path $directory.FullName 'Transaction.json'
                $result=if([IO.File]::Exists($resultPath)){Read-GtObject $resultPath}else{$null}
                $toolRun=Get-GtToolRun $directory.FullName $result
                if($null -ne $toolRun){$toolRun;continue}
                if ((Test-Path -LiteralPath $resultPath -PathType Leaf) -and -not (Test-Path -LiteralPath $transactionPath -PathType Leaf)) {
                    $inputValidation=Get-GtInputValidationRun $directory.FullName $result
                    if($null -ne $inputValidation){$inputValidation;continue}
                    if ('status' -in $result.PSObject.Properties.Name -and $result.status -cin @('success','failed','incomplete')) {
                        Get-GtRuntimeRun $directory.FullName $result
                        continue
                    }
                }
                if (-not (Test-Path -LiteralPath $resultPath) -or -not (Test-Path -LiteralPath $transactionPath)) {
                    $buildPath=Join-Path $directory.FullName 'Build.json'
                    $buildOwner=Get-GtCompletedBuild $directory
                    if ($null -eq $buildOwner) { continue }
                    if ($buildOwner.error) { throw $buildOwner.error }
                    $build=$buildOwner.receipt
                    foreach ($field in @('schemaVersion','status','stage','completedUtc','environment')) {
                        if ($field -notin $build.PSObject.Properties.Name) { throw "Missing build receipt field: $field" }
                    }
                    if ($build.schemaVersion -ne 1 -or $build.status -ne 'PASS' -or $build.stage -ne 'Stage/SolastaUnfinishedBusiness') { continue }
                    if (-not $build.environment -or 'gameExe' -notin $build.environment.PSObject.Properties.Name -or
                        $build.environment.gameExe -isnot [string] -or -not [IO.Path]::IsPathRooted($build.environment.gameExe)) {
                        throw 'Invalid build game executable receipt.'
                    }
                    [pscustomobject]@{ path=$directory.FullName; name=$directory.Name; caseId='@Build'; completed=[datetime]$build.completedUtc; gameDirectory=(Split-Path -Parent $build.environment.gameExe); buildHash=Get-GtHash $buildPath;kind='build';result=$build }
                    continue
                }
                $transaction=Read-GtObject $transactionPath
                if ('schemaVersion' -notin $transaction.PSObject.Properties.Name -or $transaction.schemaVersion -ne 1 -or
                    'schemaVersion' -notin $result.PSObject.Properties.Name -or $result.schemaVersion -ne 1 -or
                    'state' -notin $transaction.PSObject.Properties.Name -or $transaction.state -ne 'restored' -or 'caseId' -notin $result.PSObject.Properties.Name -or
                    -not $result.caseId -or 'restored' -notin $result.PSObject.Properties.Name -or -not $result.restored -or
                    'completedUtc' -notin $result.PSObject.Properties.Name) { continue }
                [pscustomobject]@{ path=$directory.FullName; name=$directory.Name; caseId=$result.caseId; completed=[datetime]$result.completedUtc; gameDirectory=$transaction.gameDirectory; buildHash=$null;kind='game-test';result=$result }
            } catch {
                if (-not $retentionReasons.ContainsKey($directory.FullName)) {
                    $retentionReasons[$directory.FullName]='unrecognized-receipt'
                    $actions.Add([pscustomobject]@{kind='retained';path=$directory.FullName;bytes=0;applied=$false;reason=$_.Exception.Message})
                }
            }
        })
        foreach ($run in $runs) {
            $identityPath=if($run.kind -eq 'tool'){$run.identityPath}else{Join-Path $run.path $(if ($run.kind -eq 'build') { 'Build.json' } else { 'Result.json' })}
            $run | Add-Member -NotePropertyName identityHash -NotePropertyValue (Get-GtHash $identityPath)
            if($run.kind -ne 'tool'){$run | Add-Member -NotePropertyName identityPath -NotePropertyValue $identityPath}
        }
        foreach ($group in $runs | Group-Object kind,caseId) {
            foreach ($run in @($group.Group | Sort-Object completed -Descending | Select-Object -First $KeepLatestPerCase)) {
                $retentionReasons[$run.path]='latest-per-case'
            }
        }
        foreach ($run in $runs) {
            if ($run.path -eq $root) { $retentionReasons[$run.path]='cleanup-report-directory' }
            elseif (Test-Preserved $run.path) { $retentionReasons[$run.path]='explicit-preservation' }
            elseif (Test-Referenced $run.path) { $retentionReasons[$run.path]='declared-input-reference' }
            elseif ($retained.Contains($run.path)) { $retentionReasons[$run.path]='unrecognized-backup' }
            elseif ($referenceText.IndexOf($run.name,[StringComparison]::OrdinalIgnoreCase) -ge 0) { $retentionReasons[$run.path]='case-or-scenario-reference' }
            elseif ($run.gameDirectory -and (Test-Path -LiteralPath (Join-Path $run.gameDirectory 'Mods\.UBGameTestTransaction.json'))) { $retentionReasons[$run.path]='unresolved-game-transaction' }
        }
        if ($MaxAgeDays -gt 0) {
            $cutoff=[datetime]::UtcNow.AddDays(-$MaxAgeDays)
            foreach ($run in $runs) {
                if ($retentionReasons.ContainsKey($run.path) -and $retentionReasons[$run.path] -eq 'latest-per-case' -and $run.completed.ToUniversalTime() -lt $cutoff) {
                    [void]$retentionReasons.Remove($run.path)
                }
            }
        }
        $companionsByRun=@{}
        foreach ($run in $runs) {
            if ($run.kind -in @('build','tool') -or ($MaxRetainedBytes -eq 0 -and $retentionReasons.ContainsKey($run.path))) { continue }
            try {
                $companions=@(Get-GtNativeUserDataCompanions $run.path)
                $companionsByRun[$run.path]=$companions
                if (@($companions | Where-Object { (Test-Preserved $_.path) -or (Test-Referenced $_.path) }).Count) { $retentionReasons[$run.path]='preserved-native-companion' }
            } catch {
                $retentionReasons[$run.path]='unrecognized-native-companion'
                $actions.Add([pscustomobject]@{kind='retained';path=$run.path;bytes=0;applied=$false;reason=$_.Exception.Message})
            }
        }
        # Recompute build dependencies after each budget decision: retiring a
        # run can release its build, but a surviving consumer always keeps it.
        $buildDirectories=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $runByPath=@{}
        foreach ($run in $runs) {
            $runByPath[$run.path]=$run
            if ($run.caseId -eq '@Build') { [void]$buildDirectories.Add($run.path) }
        }
        $buildHashes=@{}
        # A consumer outside the selected cleanup subtree still owns its build.
        $dependencyDirectories=if ($buildDirectories.Count -eq 0) { @() }
            elseif ($root -eq $script:GameTestArtifactRoot) { $directories }
            else { @(Get-ArtifactBoundary $script:GameTestArtifactRoot) }
        foreach ($directory in $dependencyDirectories) {
            if ($buildDirectories.Contains($directory.FullName)) { continue }
            if (-not (Test-Path -LiteralPath (Join-Path $directory.FullName 'Transaction.json') -PathType Leaf) -and
                -not (Test-Path -LiteralPath (Join-Path $directory.FullName 'Result.json') -PathType Leaf)) { continue }
            $buildPath=Join-Path $directory.FullName 'Build.json'
            if (Test-Path -LiteralPath $buildPath -PathType Leaf) { $buildHashes[$directory.FullName]=Get-GtHash $buildPath }
        }
        function Get-RequiredBuild {
            $hashes=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($path in $buildHashes.Keys) {
                if (-not $runByPath.ContainsKey($path) -or $retentionReasons.ContainsKey($path)) { [void]$hashes.Add($buildHashes[$path]) }
            }
            return ,$hashes
        }
        function Get-RemainingRequiredBuild {
            $hashes=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($directory in @(Get-ArtifactBoundary $script:GameTestArtifactRoot)) {
                if ($buildDirectories.Contains($directory.FullName)) { continue }
                if (-not $Apply -and $plannedConsumers.Contains($directory.FullName)) { continue }
                if (-not (Test-Path -LiteralPath (Join-Path $directory.FullName 'Result.json') -PathType Leaf) -and
                    -not (Test-Path -LiteralPath (Join-Path $directory.FullName 'Transaction.json') -PathType Leaf)) { continue }
                $path=Join-Path $directory.FullName 'Build.json'
                if (Test-Path -LiteralPath $path -PathType Leaf) { [void]$hashes.Add((Get-GtHash $path)) }
            }
            return ,$hashes
        }
        function Assert-RunDeletionReady($Run) {
            Assert-ArtifactGameStopped
            Assert-ReferencesCurrent
            if ($Run.path -eq $root -or -not (Test-GtWithin $Run.path $root)) { throw 'The cleanup report root cannot be deleted.' }
            if ($Run.gameDirectory -and (Test-Path -LiteralPath (Join-Path $Run.gameDirectory 'Mods\.UBGameTestTransaction.json'))) { throw 'A recovery transaction appeared during cleanup.' }
            if ($Run.kind -eq 'tool') { if($null -eq (Get-GtToolRun $Run.path (Read-GtObject $Run.identityPath))){throw 'Tool ownership disappeared before deletion.'} }
            if ($Run.kind -eq 'runtime') { [void](Get-GtRuntimeRun $Run.path (Read-GtObject $Run.identityPath)) }
            elseif ($Run.kind -eq 'input-validation') { $current=Get-GtInputValidationRun $Run.path (Read-GtObject $Run.identityPath);if($null -eq $current -or $current.kind -cne 'input-validation'){throw 'Input-validation ownership disappeared before cleanup.'} }
            elseif ($Run.kind -eq 'game-test') {
                $transaction=Read-GtObject (Join-Path $Run.path 'Transaction.json')
                if ($transaction.schemaVersion -ne 1 -or $transaction.state -ne 'restored' -or
                    (Get-GtPath $transaction.gameDirectory) -ne (Get-GtPath $Run.gameDirectory)) { throw 'Cleanup transaction ownership changed.' }
            }
            elseif ($Run.kind -eq 'build') {
                $owner=Get-GtCompletedBuild ([IO.DirectoryInfo]::new($Run.path))
                if ($null -eq $owner -or $owner.error -or $owner.hash -ne $Run.identityHash) { throw 'Build ownership changed before deletion.' }
            }
            if ((Get-GtHash $Run.identityPath) -ne $Run.identityHash) { throw 'A cleanup result changed before deletion.' }
            Assert-GtTree $Run.path
            Assert-ReferencesCurrent
        }
        $runBytes=@{}
        if ($MaxRetainedBytes -gt 0) {
            foreach ($run in $runs) {
                $bytes=[long](Get-ChildItem -LiteralPath $run.path -File -Recurse -Force | Measure-Object Length -Sum | ForEach-Object { $_.Sum })
                if (-not $Apply) { $bytes-=[long]($actions | Where-Object { $_.kind -eq 'restored-backup' -and (Test-GtWithin $_.path $run.path) } | Measure-Object bytes -Sum | ForEach-Object { $_.Sum }) }
                foreach ($companion in @($companionsByRun[$run.path])) { if ($null -ne $companion) { $bytes+=$companion.bytes } }
                $runBytes[$run.path]=$bytes
            }
            while ($true) {
                $requiredBuilds=Get-RequiredBuild
                $kept=@($runs | Where-Object { $retentionReasons.ContainsKey($_.path) -or ($_.caseId -eq '@Build' -and $requiredBuilds.Contains($_.buildHash)) })
                $total=[long]($kept | ForEach-Object { $runBytes[$_.path] } | Measure-Object -Sum | ForEach-Object { $_.Sum })
                $policy.retainedBytes=$total
                $policy.budgetSatisfied=($total -le $MaxRetainedBytes)
                if ($policy.budgetSatisfied) { break }
                $next=@($kept | Where-Object { $retentionReasons.ContainsKey($_.path) -and $retentionReasons[$_.path] -eq 'latest-per-case' -and
                    ($_.caseId -ne '@Build' -or -not $requiredBuilds.Contains($_.buildHash)) } | Sort-Object completed,path | Select-Object -First 1)
                if (-not $next.Count) { break }
                [void]$retentionReasons.Remove($next[0].path)
            }
        }
        $requiredBuilds=Get-RequiredBuild
        foreach ($run in $runs) {
            if ($run.caseId -eq '@Build' -and $requiredBuilds.Contains($run.buildHash)) { $retentionReasons[$run.path]='retained-run-build' }
        }
        $plannedConsumers=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($phase in @('consumers','builds')) {
            if ($phase -eq 'builds' -and $buildDirectories.Count -gt 0) {
                # Only successful consumer removals can release a build. A
                # skipped, failed or newly present consumer still owns it.
                $remainingBuilds=Get-RemainingRequiredBuild
                foreach ($build in @($runs | Where-Object kind -eq 'build')) {
                    if ($remainingBuilds.Contains($build.buildHash)) { $retentionReasons[$build.path]='retained-run-build' }
                }
            }
            foreach ($run in @($runs | Where-Object { ($_.kind -eq 'build') -eq ($phase -eq 'builds') })) {
                if ($retentionReasons.ContainsKey($run.path)) { continue }
                # Any unresolved deployment for this game can still refer to older evidence.
                if ($run.gameDirectory -and (Test-Path -LiteralPath (Join-Path $run.gameDirectory 'Mods\.UBGameTestTransaction.json'))) { $retentionReasons[$run.path]='unresolved-game-transaction'; continue }
                Assert-GtTree $run.path
                $companions=@(if ($companionsByRun.ContainsKey($run.path)) { $companionsByRun[$run.path] })
                if ((Get-GtHash $run.identityPath) -ne $run.identityHash) { throw 'A cleanup result changed during planning.' }
                if ($run.kind -eq 'tool') { if($null -eq (Get-GtToolRun $run.path (Read-GtObject $run.identityPath))){throw 'Tool ownership disappeared during cleanup planning.'} }
                if ($run.kind -eq 'runtime') { [void](Get-GtRuntimeRun $run.path (Read-GtObject $run.identityPath)) }
                elseif ($run.kind -eq 'input-validation') { $current=Get-GtInputValidationRun $run.path (Read-GtObject $run.identityPath);if($null -eq $current -or $current.kind -cne 'input-validation'){throw 'Input-validation ownership disappeared during cleanup planning.'} }
                elseif ($run.kind -eq 'game-test') {
                    $transaction=Read-GtObject (Join-Path $run.path 'Transaction.json')
                    if ($transaction.schemaVersion -ne 1 -or $transaction.state -ne 'restored' -or
                        (Get-GtPath $transaction.gameDirectory) -ne (Get-GtPath $run.gameDirectory)) { throw 'Cleanup transaction ownership changed.' }
                }
                if ($Apply) {
                    Assert-ArtifactGameStopped
                    if ($run.kind -notin @('build','tool')) {
                        $verified=@(Get-GtNativeUserDataCompanions $run.path)
                        if ($companions.Count -ne $verified.Count -or ($companions.Count -gt 0 -and
                            @(Compare-Object @($companions | ForEach-Object path) @($verified | ForEach-Object path)).Count)) { throw 'Native user-data ownership changed during planning.' }
                    }
                }
                $diagnostic=[ordered]@{path=$run.path;kind=$run.kind;caseId=$run.caseId;completedUtc=$run.completed.ToUniversalTime().ToString('O');
                    identitySha256=$run.identityHash;result=(Get-CleanupResultSummary $run.result);runtimeResult=$null;receiptHashes=@()}
                foreach ($name in @('Directory.json','Transaction.json','BackupRelease.json','Build.json','InputValidationAudit.json','InputValidationProducer.ps1','ToolsAudit.json')) {
                    $path=Join-Path $run.path $name
                    if (Test-Path -LiteralPath $path -PathType Leaf) { $diagnostic.receiptHashes+= [ordered]@{name=$name;sha256=Get-GtHash $path} }
                }
                if ($run.kind -eq 'game-test' -and 'runtimeResult' -in $run.result.PSObject.Properties.Name -and $run.result.runtimeResult -is [string]) {
                    $runtimePath=Get-GtPath $run.result.runtimeResult
                    if ((Test-GtWithin $runtimePath $run.path) -and (Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
                        $diagnostic.runtimeResult=Get-CleanupResultSummary (Read-GtObject $runtimePath)
                        $diagnostic.receiptHashes+= [ordered]@{name=$runtimePath.Substring($run.path.Length+1);sha256=Get-GtHash $runtimePath}
                    }
                }
                $retired.Add($diagnostic)
                $bytes=[long](Get-ChildItem -LiteralPath $run.path -File -Recurse -Force | Measure-Object Length -Sum | ForEach-Object { $_.Sum })
                if (-not $Apply) {
                    # A preview has not removed backups yet; count each byte only once.
                    $bytes-=[long]($actions | Where-Object { $_.kind -eq 'restored-backup' -and (Test-GtWithin $_.path $run.path) } | Measure-Object bytes -Sum | ForEach-Object { $_.Sum })
                }
                foreach ($companion in $companions) {
                    # The exact short-root child, ownership markers, and complete tree were checked above.
                    $action=[pscustomobject]@{kind='native-user-data';path=$companion.path;run=$run.path;bytes=$companion.bytes;applied=$false}
                    $actions.Add($action)
                    Save-CleanupReport $action
                    if ($Apply) {
                        Assert-RunDeletionReady $run
                        $confirmed=@(Get-GtNativeUserDataCompanions $run.path)
                        if (@($confirmed | Where-Object path -eq $companion.path).Count -ne 1) { throw 'Native user-data ownership changed before deletion.' }
                        Assert-ReferencesCurrent
                        Remove-Item -LiteralPath $companion.path -Recurse -Force;$action.applied=$true;Save-CleanupReport $action
                    }
                }
                $action=[pscustomobject]@{kind='superseded-run';path=$run.path;bytes=$bytes;applied=$false}
                $actions.Add($action)
                Save-CleanupReport $action
                if ($Apply) {
                    Assert-RunDeletionReady $run
                    if ($run.kind -eq 'build' -and (Get-RemainingRequiredBuild).Contains($run.buildHash)) {
                        $retentionReasons[$run.path]='retained-run-build';$action.kind='retained';$action.bytes=0
                        Write-CleanupEvent ([ordered]@{event='action-cancelled';index=$actions.IndexOf($action);reason='retained-run-build'});$journal.Flush($true);$journalState.flushes++;continue
                    }
                    Remove-Item -LiteralPath $run.path -Recurse -Force;$action.applied=$true;Save-CleanupReport $action
                }
                if ($run.kind -ne 'build') { [void]$plannedConsumers.Add($run.path) }
            }
        }
        $emptyCutoff=[datetime]::UtcNow.AddDays(-[Math]::Max(1,$MaxAgeDays))
        $emptyGameChecked=$false
        foreach($directory in $directories) {
            $path=$directory.FullName
            if($path -eq $root -or -not [IO.Directory]::Exists($path) -or $directory.LastWriteTimeUtc -ge $emptyCutoff -or
                (Test-Preserved $path) -or (Test-Referenced $path) -or @([IO.Directory]::EnumerateFileSystemEntries($path)).Count) {continue}
            $action=[pscustomobject]@{kind='empty-directory';path=$path;bytes=0;applied=$false}
            $actions.Add($action);Save-CleanupReport $action
            if($Apply) {
                if(-not $emptyGameChecked){Assert-ArtifactGameStopped;$emptyGameChecked=$true}
                Assert-ReferencesCurrent;Assert-GtTree $path
                if((Test-Preserved $path) -or (Test-Referenced $path)){throw 'An empty directory acquired a consumer.'}
                # Nonrecursive deletion refuses a file created after enumeration.
                [IO.Directory]::Delete($path,$false);$action.applied=$true;Save-CleanupReport $action
            }
        }
    }
    $cleanupCompleted=$true
} catch {$cleanupError=$_.Exception.Message;throw} finally {
    try {
        if($cleanupCompleted){
            try{
                if(-not $PruneReports -or $PruneLooseFiles){Invoke-LooseFileRetention}
                Invoke-CleanupReportRetention
            }catch{$cleanupCompleted=$false;$cleanupError=$_.Exception.Message;throw}
        }
    } finally {
        try { if($journal){Save-CleanupReport -Final} } finally {
            foreach($lease in $payloadInputLeases){$lease.Dispose()}
            if($journal){$journal.Dispose()};$lock.ReleaseMutex();$lock.Dispose()
        }
    }
    if($cleanupCompleted){
        $totalBytes=[long]($actions | Measure-Object bytes -Sum | ForEach-Object { $_.Sum })
        Write-Information ('{0} entries; {1:N0} bytes; applied={2}' -f $actions.Count,$totalBytes,[bool]$Apply) -InformationAction Continue
        Write-Information "Report: $report" -InformationAction Continue
    }
}
