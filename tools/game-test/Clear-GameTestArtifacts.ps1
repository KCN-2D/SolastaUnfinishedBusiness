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
try {
    foreach ($manifest in Get-ChildItem -LiteralPath $root -Filter 'Transaction.json' -File -Recurse) {
        $run=$manifest.DirectoryName
        if (Test-Preserved $run) { continue }
        $transaction=Read-GtObject $manifest.FullName
        if ('schemaVersion' -notin $transaction.PSObject.Properties.Name -or $transaction.schemaVersion -ne 1 -or $transaction.state -ne 'restored') { continue }
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
        $runs=@(foreach ($directory in Get-ChildItem -LiteralPath $root -Directory) {
            $resultPath=Join-Path $directory.FullName 'Result.json'
            $transactionPath=Join-Path $directory.FullName 'Transaction.json'
            if (-not (Test-Path -LiteralPath $resultPath) -or -not (Test-Path -LiteralPath $transactionPath)) {
                $buildPath=Join-Path $directory.FullName 'Build.json'
                if ($directory.Name -notmatch '^\d{8}-\d{6}-\d{3}-Build-[a-fA-F0-9]+$' -or -not (Test-Path -LiteralPath $buildPath)) { continue }
                $build=Read-GtObject $buildPath
                if ($build.schemaVersion -ne 1 -or $build.status -ne 'PASS' -or $build.stage -ne 'Stage/SolastaUnfinishedBusiness' -or
                    'completedUtc' -notin $build.PSObject.Properties.Name) { continue }
                [pscustomobject]@{ path=$directory.FullName; name=$directory.Name; caseId='@Build'; completed=[datetime]$build.completedUtc; gameDirectory=(Split-Path -Parent $build.environment.gameExe) }
                continue
            }
            $result=Read-GtObject $resultPath
            $transaction=Read-GtObject $transactionPath
            if ('schemaVersion' -notin $transaction.PSObject.Properties.Name -or $transaction.schemaVersion -ne 1 -or
                'schemaVersion' -notin $result.PSObject.Properties.Name -or $result.schemaVersion -ne 1 -or
                $transaction.state -ne 'restored' -or 'caseId' -notin $result.PSObject.Properties.Name -or
                -not $result.caseId -or 'restored' -notin $result.PSObject.Properties.Name -or -not $result.restored -or
                'completedUtc' -notin $result.PSObject.Properties.Name) { continue }
            [pscustomobject]@{ path=$directory.FullName; name=$directory.Name; caseId=$result.caseId; completed=[datetime]$result.completedUtc; gameDirectory=$transaction.gameDirectory }
        })
        foreach ($group in $runs | Group-Object caseId) {
            foreach ($run in @($group.Group | Sort-Object completed -Descending | Select-Object -Skip $KeepLatestPerCase)) {
                if ((Test-Preserved $run.path) -or $retained.Contains($run.path) -or $referenceText.IndexOf($run.name,[StringComparison]::OrdinalIgnoreCase) -ge 0) { continue }
                # Any unresolved deployment for this game can still refer to older evidence.
                if (Test-Path -LiteralPath (Join-Path $run.gameDirectory 'Mods\.UBGameTestTransaction.json')) { continue }
                Assert-GtTree $run.path
                try {
                    $companions=@(Get-NativeUserDataCompanions $run.path)
                    if (@($companions | Where-Object { Test-Preserved $_.path }).Count) { continue }
                } catch {
                    $actions.Add([pscustomobject]@{kind='retained';path=$run.path;bytes=0;applied=$false;reason=$_.Exception.Message})
                    continue
                }
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
    }
    $summary=[ordered]@{ schemaVersion=1; utc=[datetime]::UtcNow.ToString('O'); applied=[bool]$Apply; bytes=[long]($actions | Measure-Object bytes -Sum | ForEach-Object { $_.Sum }); actions=@($actions.ToArray()) }
    $report=Join-Path $root ('Storage-Cleanup-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.json')
    Write-GtJson $report $summary
    Write-Host ('{0} entries; {1:N0} bytes; applied={2}' -f $actions.Count,$summary.bytes,[bool]$Apply)
    Write-Host "Report: $report"
} finally { $lock.ReleaseMutex(); $lock.Dispose() }
