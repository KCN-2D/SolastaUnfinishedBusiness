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
    [switch]$Apply
)
. (Join-Path $PSScriptRoot 'GameTest.Common.ps1')
if ($ReleaseNativeUserData -and ($PruneSuperseded -or $MaxAgeDays -gt 0 -or $MaxRetainedBytes -gt 0)) {
    throw 'Native user-data release is separate from run/build pruning.'
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
Assert-GtTree $root
$preserved=@($PreserveDirectory | ForEach-Object { Get-GtPath $_ })
foreach ($path in $preserved) {
    if (-not (Test-GtWithin $path $script:GameTestArtifactRoot)) { throw 'Preserved paths must be artifact directories.' }
}
$storageReferences=Read-GtStorageReferences $ReferencePath
$referenced=$storageReferences.paths
$referenceInputs=$storageReferences.inputs
function Test-Referenced([string]$Path) {
    foreach ($entry in $referenced) {
        if ((Test-GtWithin $entry $Path) -or (Test-GtWithin $Path $entry)) { return $true }
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
function Get-ArtifactBoundaries([string]$Path) {
    $pending=[Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $pending.Push([IO.DirectoryInfo]::new($Path))
    while ($pending.Count) {
        $directory=$pending.Pop()
        if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point in artifact boundary: $($directory.FullName)" }
        # Re-enumerate every time this function is called. A new consumer or
        # a late Build.json in an existing boundary must remain observable.
        if (Test-ArtifactEnvironment $directory.FullName) { continue }
        $boundary=$false
        foreach ($name in @('Directory.json','Transaction.json','Build.json','Result.json')) {
            if ([IO.File]::Exists([IO.Path]::Combine($directory.FullName,$name))) { $boundary=$true; break }
        }
        if ($boundary) { $directory; continue }
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
    retainedBytes=$null;budgetSatisfied=$null}
$report=Join-Path $root ('Storage-Cleanup-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8) + '.json')
$journalPath=[IO.Path]::ChangeExtension($report,'.jsonl')
$journal=$null
$journalState=[ordered]@{actions=0;retired=0;completed=[Collections.Generic.HashSet[int]]::new();events=0;bytes=[long]0;flushes=0;summaryWrites=0}
function Write-CleanupEvent($Value) {
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Value|ConvertTo-Json -Depth 20 -Compress)+[Environment]::NewLine)
    $journal.Write($bytes,0,$bytes.Length);$journalState.bytes+=$bytes.Length;$journalState.events++
}
function Get-CleanupResultSummary($Result) {
    $summary=[ordered]@{}
    foreach($name in @('status','reason','runName','caseId','startedUtc','completedUtc','passed','failed','incomplete','validationOnly','startupOnly','compilationSucceeded',
        'settingsRestored','displaySettingsRestored','probeRemoved','originalNativeFilesUnchanged','restored','originalSavePreserved','nativeVerificationStatus',
        'hashes','expectedUb','loadedUb','sources','adapterSources','toolSources')) {
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
    if($journalState.events -gt $before){$journal.Flush($true);$journalState.flushes++}
    if(-not $Final){return}
    $summary=[ordered]@{schemaVersion=1;utc=[datetime]::UtcNow.ToString('O');applied=[bool]$Apply;
        bytes=[long]($actions | Measure-Object bytes -Sum | ForEach-Object { $_.Sum });policy=$policy;referenceInputs=$referenceInputs;
        actions=@($actions.ToArray());retiredRuns=@($retired.ToArray());retainedDirectories=@(foreach ($path in $retentionReasons.Keys | Sort-Object) { [ordered]@{path=$path;reason=$retentionReasons[$path]} });
        journal=[ordered]@{path=[IO.Path]::GetFileName($journalPath);events=$journalState.events;bytes=$journalState.bytes;flushes=$journalState.flushes};summaryWrites=1}
    Write-GtJson $report $summary
    $journalState.summaryWrites++
}
try {
    $journal=[IO.File]::Open($journalPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    Write-CleanupEvent ([ordered]@{event='cleanup-start';schemaVersion=1;utc=[DateTime]::UtcNow.ToString('O');applied=[bool]$Apply;policy=$policy;referenceInputs=$referenceInputs})
    $journal.Flush($true);$journalState.flushes++
    $directories=@(Get-ArtifactBoundaries $root)
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
                if ((Test-Path -LiteralPath $resultPath -PathType Leaf) -and -not (Test-Path -LiteralPath $transactionPath -PathType Leaf)) {
                    $runtime=Read-GtObject $resultPath
                    if ('status' -in $runtime.PSObject.Properties.Name -and $runtime.status -cin @('success','failed','incomplete')) {
                        Get-GtRuntimeRun $directory.FullName $runtime
                        continue
                    }
                }
                if (-not (Test-Path -LiteralPath $resultPath) -or -not (Test-Path -LiteralPath $transactionPath)) {
                    $buildPath=Join-Path $directory.FullName 'Build.json'
                    if ($directory.Name -notmatch '^\d{8}-\d{6}-\d{3}-Build-[a-fA-F0-9]+$' -or -not (Test-Path -LiteralPath $buildPath)) { continue }
                    $build=Read-GtObject $buildPath
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
                $result=Read-GtObject $resultPath
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
            $identityPath=Join-Path $run.path $(if ($run.kind -eq 'build') { 'Build.json' } else { 'Result.json' })
            $run | Add-Member -NotePropertyName identityHash -NotePropertyValue (Get-GtHash $identityPath)
            $run | Add-Member -NotePropertyName identityPath -NotePropertyValue $identityPath
        }
        foreach ($group in $runs | Group-Object caseId) {
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
            elseif (Test-Path -LiteralPath (Join-Path $run.gameDirectory 'Mods\.UBGameTestTransaction.json')) { $retentionReasons[$run.path]='unresolved-game-transaction' }
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
            if ($run.caseId -eq '@Build' -or ($MaxRetainedBytes -eq 0 -and $retentionReasons.ContainsKey($run.path))) { continue }
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
        $dependencyDirectories=if ($root -eq $script:GameTestArtifactRoot) { $directories }
            else { @(Get-ArtifactBoundaries $script:GameTestArtifactRoot) }
        foreach ($directory in $dependencyDirectories) {
            if ($buildDirectories.Contains($directory.FullName)) { continue }
            if (-not (Test-Path -LiteralPath (Join-Path $directory.FullName 'Transaction.json') -PathType Leaf) -and
                -not (Test-Path -LiteralPath (Join-Path $directory.FullName 'Result.json') -PathType Leaf)) { continue }
            $buildPath=Join-Path $directory.FullName 'Build.json'
            if (Test-Path -LiteralPath $buildPath -PathType Leaf) { $buildHashes[$directory.FullName]=Get-GtHash $buildPath }
        }
        function Get-RequiredBuilds {
            $hashes=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($path in $buildHashes.Keys) {
                if (-not $runByPath.ContainsKey($path) -or $retentionReasons.ContainsKey($path)) { [void]$hashes.Add($buildHashes[$path]) }
            }
            return ,$hashes
        }
        function Get-RemainingRequiredBuilds {
            $hashes=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($directory in @(Get-ArtifactBoundaries $script:GameTestArtifactRoot)) {
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
            if (Test-Path -LiteralPath (Join-Path $Run.gameDirectory 'Mods\.UBGameTestTransaction.json')) { throw 'A recovery transaction appeared during cleanup.' }
            if ($Run.kind -eq 'runtime') { [void](Get-GtRuntimeRun $Run.path (Read-GtObject $Run.identityPath)) }
            elseif ($Run.kind -eq 'game-test') {
                $transaction=Read-GtObject (Join-Path $Run.path 'Transaction.json')
                if ($transaction.schemaVersion -ne 1 -or $transaction.state -ne 'restored' -or
                    (Get-GtPath $transaction.gameDirectory) -ne (Get-GtPath $Run.gameDirectory)) { throw 'Cleanup transaction ownership changed.' }
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
                $requiredBuilds=Get-RequiredBuilds
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
        $requiredBuilds=Get-RequiredBuilds
        foreach ($run in $runs) {
            if ($run.caseId -eq '@Build' -and $requiredBuilds.Contains($run.buildHash)) { $retentionReasons[$run.path]='retained-run-build' }
        }
        $plannedConsumers=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($phase in @('consumers','builds')) {
            if ($phase -eq 'builds') {
                # Only successful consumer removals can release a build. A
                # skipped, failed or newly present consumer still owns it.
                $remainingBuilds=Get-RemainingRequiredBuilds
                foreach ($build in @($runs | Where-Object kind -eq 'build')) {
                    if ($remainingBuilds.Contains($build.buildHash)) { $retentionReasons[$build.path]='retained-run-build' }
                }
            }
            foreach ($run in @($runs | Where-Object { ($_.kind -eq 'build') -eq ($phase -eq 'builds') })) {
                if ($retentionReasons.ContainsKey($run.path)) { continue }
                # Any unresolved deployment for this game can still refer to older evidence.
                if (Test-Path -LiteralPath (Join-Path $run.gameDirectory 'Mods\.UBGameTestTransaction.json')) { $retentionReasons[$run.path]='unresolved-game-transaction'; continue }
                Assert-GtTree $run.path
                $companions=@(if ($companionsByRun.ContainsKey($run.path)) { $companionsByRun[$run.path] })
                if ((Get-GtHash $run.identityPath) -ne $run.identityHash) { throw 'A cleanup result changed during planning.' }
                if ($run.kind -eq 'runtime') { [void](Get-GtRuntimeRun $run.path (Read-GtObject $run.identityPath)) }
                elseif ($run.kind -eq 'game-test') {
                    $transaction=Read-GtObject (Join-Path $run.path 'Transaction.json')
                    if ($transaction.schemaVersion -ne 1 -or $transaction.state -ne 'restored' -or
                        (Get-GtPath $transaction.gameDirectory) -ne (Get-GtPath $run.gameDirectory)) { throw 'Cleanup transaction ownership changed.' }
                }
                if ($Apply) {
                    Assert-ArtifactGameStopped
                    if ($run.kind -ne 'build') {
                        $verified=@(Get-GtNativeUserDataCompanions $run.path)
                        if ($companions.Count -ne $verified.Count -or ($companions.Count -gt 0 -and
                            @(Compare-Object @($companions | ForEach-Object path) @($verified | ForEach-Object path)).Count)) { throw 'Native user-data ownership changed during planning.' }
                    }
                }
                $diagnostic=[ordered]@{path=$run.path;kind=$run.kind;caseId=$run.caseId;completedUtc=$run.completed.ToUniversalTime().ToString('O');
                    identitySha256=$run.identityHash;result=(Get-CleanupResultSummary $run.result);runtimeResult=$null;receiptHashes=@()}
                foreach ($name in @('Directory.json','Transaction.json','BackupRelease.json','Build.json')) {
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
                    if ($run.kind -eq 'build' -and (Get-RemainingRequiredBuilds).Contains($run.buildHash)) {
                        $retentionReasons[$run.path]='retained-run-build';$action.kind='retained';$action.bytes=0
                        Write-CleanupEvent ([ordered]@{event='action-cancelled';index=$actions.IndexOf($action);reason='retained-run-build'});$journal.Flush($true);$journalState.flushes++;continue
                    }
                    Remove-Item -LiteralPath $run.path -Recurse -Force;$action.applied=$true;Save-CleanupReport $action
                }
                if ($run.kind -ne 'build') { [void]$plannedConsumers.Add($run.path) }
            }
        }
    }
    Save-CleanupReport
    $totalBytes=[long]($actions | Measure-Object bytes -Sum | ForEach-Object { $_.Sum })
    Write-Host ('{0} entries; {1:N0} bytes; applied={2}' -f $actions.Count,$totalBytes,[bool]$Apply)
    Write-Host "Report: $report"
} finally {
    try { if($journal){Save-CleanupReport -Final} } finally { if($journal){$journal.Dispose()};$lock.ReleaseMutex();$lock.Dispose() }
}
