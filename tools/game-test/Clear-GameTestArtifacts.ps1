[CmdletBinding()]
param(
    [string]$ArtifactRoot,
    [string[]]$PreserveDirectory=@(),
    [ValidateRange(1,100)][int]$KeepLatestPerCase=2,
    [switch]$PruneSuperseded,
    [switch]$Apply
)
. (Join-Path $PSScriptRoot 'GameTest.Common.ps1')
if (-not $ArtifactRoot) { $ArtifactRoot=$script:GameTestArtifactRoot }
$root=Get-GtPath $ArtifactRoot
if (-not (Test-GtWithin $root $script:GameTestArtifactRoot)) { throw 'Cleanup is limited to tools/game-test/artifacts.' }
if (Test-GtWithin $root (Join-Path $script:GameTestArtifactRoot '.native')) {
    throw 'Native user-data folders must be cleaned with their owning evidence run.'
}
Assert-GtTree $root
$preserved=@($PreserveDirectory | ForEach-Object { Get-GtPath $_ })
foreach ($path in $preserved) {
    if (-not (Test-GtWithin $path $script:GameTestArtifactRoot)) { throw 'Preserved paths must be artifact directories.' }
}
function Test-Preserved([string]$Path) {
    foreach ($item in $preserved) {
        if ((Test-GtWithin $Path $item) -or (Test-GtWithin $item $Path)) { return $true }
    }
    return $false
}
function Get-ArtifactBoundaries([string]$Path) {
    $pending=[Collections.Generic.Stack[string]]::new()
    $pending.Push($Path)
    while ($pending.Count) {
        $directory=$pending.Pop()
        # Grouping folders may be nested. An owned run is a boundary: its
        # archived inputs, runtime evidence and contract fixtures are not runs.
        $boundary=$false
        foreach ($name in @('Directory.json','Transaction.json','Build.json','Result.json')) {
            if (Test-Path -LiteralPath (Join-Path $directory $name) -PathType Leaf) { $boundary=$true; break }
        }
        if ($boundary) { Get-Item -LiteralPath $directory; continue }
        foreach ($child in Get-ChildItem -LiteralPath $directory -Directory -Force) {
            if ($child.Name -eq '.native') { continue }
            $pending.Push($child.FullName)
        }
    }
}
function Get-NativeUserDataCompanions([string]$Run) {
    $nativeRoot=Get-GtPath (Join-Path $script:GameTestArtifactRoot '.native')
    foreach ($manifest in Get-ChildItem -LiteralPath $Run -Filter 'Native-UserDataLocation.json' -File -Recurse) {
        $location=Read-GtObject $manifest.FullName
        if ('owner' -notin $location.PSObject.Properties.Name -or $location.owner -cnotmatch '^[a-f0-9]{32}$' -or
            'path' -notin $location.PSObject.Properties.Name -or 'evidenceDirectory' -notin $location.PSObject.Properties.Name) {
            throw 'Unrecognized native user-data ownership receipt was retained.'
        }
        $path=Get-GtPath $location.path
        $evidence=Get-GtPath $location.evidenceDirectory
        if ($path -ne (Get-GtPath (Join-Path $nativeRoot $location.owner)) -or
            $evidence -ne (Get-GtPath $manifest.DirectoryName) -or -not (Test-GtWithin $evidence $Run)) {
            throw 'Native user-data receipt does not identify its owned artifact directories.'
        }
        # A previous interrupted cleanup may already have removed this companion.
        if (-not (Test-Path -LiteralPath $path)) { continue }
        Assert-GtTree $path
        $ownerMarker=Join-Path $path '.ub-runtime-userdata'
        $evidenceMarker=Join-Path $path '.ub-runtime-evidence'
        Assert-GtFile $ownerMarker
        Assert-GtFile $evidenceMarker
        if ([IO.File]::ReadAllText($ownerMarker).Trim() -cne $location.owner -or
            (Get-GtPath ([IO.File]::ReadAllText($evidenceMarker).Trim())) -ne $evidence) {
            throw 'Native user-data ownership markers do not match the evidence receipt.'
        }
        [pscustomobject]@{path=$path;bytes=[long]((Get-ChildItem -LiteralPath $path -File -Recurse -Force | Measure-Object Length -Sum).Sum)}
    }
}
$lock=Enter-GtLock
$actions=[Collections.Generic.List[object]]::new()
$retained=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$retentionReasons=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
try {
    $directories=@(Get-ArtifactBoundaries $root)
    foreach ($directory in $directories) {
        $run=$directory.FullName
        $manifest=Join-Path $run 'Transaction.json'
        if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { continue }
        if (Test-Preserved $run) { continue }
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
            $receipt=Clear-GtRestoredBackup $run -Preview:(-not $Apply)
            $actions.Add([pscustomobject]@{ kind='restored-backup'; path=$backup; bytes=[long]$receipt.bytes; applied=[bool]$Apply })
        } catch {
            [void]$retained.Add($run)
            $actions.Add([pscustomobject]@{ kind='retained'; path=$backup; bytes=0; applied=$false; reason=$_.Exception.Message })
        }
    }
    if ($PruneSuperseded) {
        $references=[Text.StringBuilder]::new()
        foreach ($folder in @('Cases','Scenarios')) {
            foreach ($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $folder) -Recurse -File | Where-Object { $_.Extension -in @('.json','.ps1','.cs','.mjs') }) {
                [void]$references.AppendLine([IO.File]::ReadAllText($file.FullName))
            }
        }
        $referenceText=$references.ToString()
        $runs=@(foreach ($directory in $directories) {
            try {
                $resultPath=Join-Path $directory.FullName 'Result.json'
                $transactionPath=Join-Path $directory.FullName 'Transaction.json'
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
                    [pscustomobject]@{ path=$directory.FullName; name=$directory.Name; caseId='@Build'; completed=[datetime]$build.completedUtc; gameDirectory=(Split-Path -Parent $build.environment.gameExe); buildHash=Get-GtHash $buildPath }
                    continue
                }
                $result=Read-GtObject $resultPath
                $transaction=Read-GtObject $transactionPath
                if ('schemaVersion' -notin $transaction.PSObject.Properties.Name -or $transaction.schemaVersion -ne 1 -or
                    'schemaVersion' -notin $result.PSObject.Properties.Name -or $result.schemaVersion -ne 1 -or
                    'state' -notin $transaction.PSObject.Properties.Name -or $transaction.state -ne 'restored' -or 'caseId' -notin $result.PSObject.Properties.Name -or
                    -not $result.caseId -or 'restored' -notin $result.PSObject.Properties.Name -or -not $result.restored -or
                    'completedUtc' -notin $result.PSObject.Properties.Name) { continue }
                [pscustomobject]@{ path=$directory.FullName; name=$directory.Name; caseId=$result.caseId; completed=[datetime]$result.completedUtc; gameDirectory=$transaction.gameDirectory; buildHash=$null }
            } catch {
                if (-not $retentionReasons.ContainsKey($directory.FullName)) {
                    $retentionReasons[$directory.FullName]='unrecognized-receipt'
                    $actions.Add([pscustomobject]@{kind='retained';path=$directory.FullName;bytes=0;applied=$false;reason=$_.Exception.Message})
                }
            }
        })
        foreach ($group in $runs | Group-Object caseId) {
            foreach ($run in @($group.Group | Sort-Object completed -Descending | Select-Object -First $KeepLatestPerCase)) {
                $retentionReasons[$run.path]='latest-per-case'
            }
        }
        foreach ($run in $runs) {
            if (Test-Preserved $run.path) { $retentionReasons[$run.path]='explicit-preservation' }
            elseif ($retained.Contains($run.path)) { $retentionReasons[$run.path]='unrecognized-backup' }
            elseif ($referenceText.IndexOf($run.name,[StringComparison]::OrdinalIgnoreCase) -ge 0) { $retentionReasons[$run.path]='case-or-scenario-reference' }
            elseif (Test-Path -LiteralPath (Join-Path $run.gameDirectory 'Mods\.UBGameTestTransaction.json')) { $retentionReasons[$run.path]='unresolved-game-transaction' }
        }
        $companionsByRun=@{}
        foreach ($run in $runs) {
            if ($run.caseId -eq '@Build' -or $retentionReasons.ContainsKey($run.path)) { continue }
            try {
                $companions=@(Get-NativeUserDataCompanions $run.path)
                $companionsByRun[$run.path]=$companions
                if (@($companions | Where-Object { Test-Preserved $_.path }).Count) { $retentionReasons[$run.path]='preserved-native-companion' }
            } catch {
                $retentionReasons[$run.path]='unrecognized-native-companion'
                $actions.Add([pscustomobject]@{kind='retained';path=$run.path;bytes=0;applied=$false;reason=$_.Exception.Message})
            }
        }
        # Invoke-GameTest archives the exact Build.json beside the result. Keep
        # the matching staged build whenever its latest/referenced run is kept.
        $requiredBuilds=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $discardedRuns=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $buildDirectories=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($run in $runs) {
            if ($run.caseId -eq '@Build') { [void]$buildDirectories.Add($run.path) }
            elseif (-not $retentionReasons.ContainsKey($run.path)) { [void]$discardedRuns.Add($run.path) }
        }
        foreach ($directory in $directories) {
            if ($buildDirectories.Contains($directory.FullName) -or $discardedRuns.Contains($directory.FullName)) { continue }
            if (-not (Test-Path -LiteralPath (Join-Path $directory.FullName 'Transaction.json') -PathType Leaf) -and
                -not (Test-Path -LiteralPath (Join-Path $directory.FullName 'Result.json') -PathType Leaf)) { continue }
            # Unknown or incomplete receipts remain boundaries too, and retain
            # a build they archived even when they cannot enter pruning policy.
            $buildPath=Join-Path $directory.FullName 'Build.json'
            if (Test-Path -LiteralPath $buildPath -PathType Leaf) { [void]$requiredBuilds.Add((Get-GtHash $buildPath)) }
        }
        foreach ($run in $runs) {
            if ($run.caseId -eq '@Build' -and $requiredBuilds.Contains($run.buildHash)) { $retentionReasons[$run.path]='retained-run-build' }
        }
        foreach ($run in $runs) {
            if ($retentionReasons.ContainsKey($run.path)) { continue }
            # Any unresolved deployment for this game can still refer to older evidence.
            if (Test-Path -LiteralPath (Join-Path $run.gameDirectory 'Mods\.UBGameTestTransaction.json')) { continue }
            Assert-GtTree $run.path
            $companions=if ($companionsByRun.ContainsKey($run.path)) { @($companionsByRun[$run.path]) } else { @() }
            $bytes=[long](Get-ChildItem -LiteralPath $run.path -File -Recurse -Force | Measure-Object Length -Sum | ForEach-Object { $_.Sum })
            if (-not $Apply) {
                # A preview has not removed backups yet; count each byte only once.
                $bytes-=[long]($actions | Where-Object { $_.kind -eq 'restored-backup' -and (Test-GtWithin $_.path $run.path) } | Measure-Object bytes -Sum | ForEach-Object { $_.Sum })
            }
            foreach ($companion in $companions) {
                # The exact short-root child, ownership markers, and complete tree were checked above.
                if ($Apply) { Remove-Item -LiteralPath $companion.path -Recurse -Force }
                $actions.Add([pscustomobject]@{kind='native-user-data';path=$companion.path;run=$run.path;bytes=$companion.bytes;applied=[bool]$Apply})
            }
            if ($Apply) { Remove-Item -LiteralPath $run.path -Recurse -Force }
            $actions.Add([pscustomobject]@{ kind='superseded-run'; path=$run.path; bytes=$bytes; applied=[bool]$Apply })
        }
    }
    $summary=[ordered]@{ schemaVersion=1; utc=[datetime]::UtcNow.ToString('O'); applied=[bool]$Apply; bytes=[long]($actions | Measure-Object bytes -Sum | ForEach-Object { $_.Sum }); actions=@($actions.ToArray()); retainedDirectories=@(foreach ($path in $retentionReasons.Keys | Sort-Object) { [ordered]@{path=$path;reason=$retentionReasons[$path]} }) }
    $report=Join-Path $root ('Storage-Cleanup-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.json')
    Write-GtJson $report $summary
    Write-Host ('{0} entries; {1:N0} bytes; applied={2}' -f $actions.Count,$summary.bytes,[bool]$Apply)
    Write-Host "Report: $report"
} finally { $lock.ReleaseMutex(); $lock.Dispose() }
