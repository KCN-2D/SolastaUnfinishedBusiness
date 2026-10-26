[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RunDirectory, [Parameter(Mandatory=$true)][string]$AuthorizedGameDirectory)
. (Join-Path $PSScriptRoot 'GameTest.Common.ps1')
. (Join-Path $script:GameTestRepo 'tools\runtime-verification\NativeDisplaySettings.ps1')
$run = Resolve-GtArtifactPath $RunDirectory
$manifestPath = Join-Path $run 'Transaction.json'
$transaction = Read-GtObject $manifestPath
if ($transaction.schemaVersion -ne 1 -or $transaction.state -notin @('prepared','deployed','restored')) { throw 'Unknown recovery transaction schema or state.' }
$operationLock=Enter-GtLock
try {
$authorized = Get-GtPath $AuthorizedGameDirectory
if ($transaction.gameDirectory -ne $authorized) { throw 'AuthorizedGameDirectory does not match this transaction.' }
if ((Test-GtWithin $authorized $script:GameTestRepo) -or -not (Test-GtWithin $run $script:GameTestArtifactRoot)) { throw 'Recovery requires an installed game and an artifact directory under tools/game-test/artifacts.' }
Assert-GtGameStopped
Assert-GtTree $run
if ($transaction.state -eq 'restored') {
    [void](Clear-GtRestoredBackup $run)
    Write-Host 'Transaction is already restored.'
    return
}
$markerPath=Join-Path $authorized 'Mods\.UBGameTestTransaction.json'
if (Test-Path -LiteralPath $markerPath) {
    $marker=Read-GtObject $markerPath
    if ((Resolve-GtArtifactPath $marker.transactionPath) -ne (Get-GtPath $manifestPath)) { throw 'Another transaction owns the game-test marker.' }
}
elseif ($transaction.state -eq 'deployed') { throw 'Deployment ownership marker is missing. Inspect the transaction before recovery.' }
$mods = Join-Path $authorized 'Mods'
$installed = Join-Path $mods 'SolastaUnfinishedBusiness'
Assert-GtTree $installed
$native = Join-Path $env:USERPROFILE 'AppData\LocalLow\Tactical Adventures\Solasta'
$allowedExact = @((Join-Path $authorized 'Solasta_Data\Managed\UnityModManager\Params.xml'),(Join-Path $native 'UserPreferences.xml'),(Join-Path $native 'PlatformPreferences.xml'))
foreach ($entry in $transaction.files) {
    $target = Get-GtPath $entry.path
    if (-not (Test-GtWithin $target $installed) -and $target -notin $allowedExact) { throw "Recovery target is outside the transaction scope: $target" }
    if ($target -eq $installed) { throw 'Recovery file must not be the mod directory.' }
    Assert-GtPlainPath $target
    if ($entry.existed) {
        $backup = Get-GtPath (Join-Path $run $entry.backup)
        if (-not (Test-GtWithin $backup (Join-Path $run 'Backup'))) { throw 'Backup path escapes the owned backup directory.' }
        if ((Get-GtHash $backup) -ne $entry.sha256) { throw "Backup hash mismatch: $backup" }
    }
}
# Validate every backup before the first mutation, so an incomplete backup never erases installed files.
if ('nativeDisplaySettings' -in $transaction.PSObject.Properties.Name) {
    Assert-NativeDisplaySettingsSnapshot $transaction.nativeDisplaySettings
    [void](Get-NativeDisplaySettingsSnapshot)
}
$createdCaches=@()
if ('assemblyCache' -in $transaction.PSObject.Properties.Name) {
    if ($transaction.assemblyCache.expectedSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid deployed assembly cache hash.' }
    $createdCaches=@(Get-GtAssemblyCaches $installed 'SolastaUnfinishedBusiness.dll' | Where-Object { $_.Name -notin @($transaction.assemblyCache.originalNames) })
    foreach ($cacheFile in $createdCaches) {
        $expectedCacheHash=$transaction.assemblyCache.expectedSha256
        if ($cacheFile.Name.EndsWith('.pdb',[StringComparison]::OrdinalIgnoreCase)) {
            if ('expectedPdbSha256' -notin $transaction.assemblyCache.PSObject.Properties.Name -or $transaction.assemblyCache.expectedPdbSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'The new cache PDB has no recorded source hash; retained for inspection.' }
            $expectedCacheHash=$transaction.assemblyCache.expectedPdbSha256
        }
        if ((Get-GtHash $cacheFile.FullName) -ne $expectedCacheHash) { throw 'An unexpected assembly cache was retained for inspection.' }
    }
}
foreach ($entry in $transaction.files) {
    if ($entry.existed) {
        Copy-GtFile (Join-Path $run $entry.backup) $entry.path -ExpectedSha256 $entry.sha256 -SkipIfIdentical
        [IO.File]::SetLastWriteTimeUtc($entry.path, (ConvertTo-GtUtcDate $entry.lastWriteUtc))
        [IO.File]::SetAttributes($entry.path, [IO.FileAttributes][int]$entry.attributes)
    } elseif (Test-Path -LiteralPath $entry.path) {
        if (Test-Path -LiteralPath $entry.path -PathType Container) { throw "Expected a file, found a directory: $($entry.path)" }
        Remove-Item -LiteralPath $entry.path -Force
    }
}
foreach ($cacheFile in $createdCaches) { Remove-Item -LiteralPath $cacheFile.FullName -Force }
if ('assemblyCache' -in $transaction.PSObject.Properties.Name) {
    $remaining=@(Get-GtAssemblyCaches $installed 'SolastaUnfinishedBusiness.dll' | ForEach-Object Name | Sort-Object)
    $expected=@($transaction.assemblyCache.originalNames | Sort-Object)
    if (($remaining | ConvertTo-Json -Compress) -cne ($expected | ConvertTo-Json -Compress)) { throw 'Assembly cache names were not restored.' }
    $transaction | Add-Member -NotePropertyName assemblyCacheRestored -NotePropertyValue $true -Force
}
foreach ($entry in $transaction.files) {
    if ($entry.existed) {
        $expectedTicks=if ('lastWriteUtcTicks' -in $entry.PSObject.Properties.Name -and $null -ne $entry.lastWriteUtcTicks) { [long]$entry.lastWriteUtcTicks } else { (ConvertTo-GtUtcDate $entry.lastWriteUtc).Ticks }
        if ((Get-GtHash $entry.path) -ne $entry.sha256 -or [IO.File]::GetLastWriteTimeUtc($entry.path).Ticks -ne $expectedTicks -or [int][IO.File]::GetAttributes($entry.path) -ne [int]$entry.attributes) { throw "Restoration check failed: $($entry.path)" }
    } elseif (Test-Path -LiteralPath $entry.path) { throw "Created file was not removed: $($entry.path)" }
}
$probe = Join-Path $mods 'UBRuntimeVerification'
if ($transaction.temporaryProbeOwned -and (Test-Path -LiteralPath $probe)) {
    Assert-GtTree $probe
    $info = Read-GtObject (Join-Path $probe 'Info.json')
    if ($info.Id -ne 'UBRuntimeVerification' -or $info.EntryMethod -ne 'RuntimeVerificationHarness.Load') { throw 'Unexpected verification mod contents. Inspect the directory before removal.' }
    if ((Get-GtPath $probe) -ne (Join-Path (Get-GtPath $mods) 'UBRuntimeVerification')) { throw 'Unexpected probe path.' }
    Remove-Item -LiteralPath $probe -Recurse -Force
}
foreach ($directory in @($transaction.createdDirectories | Sort-Object { $_.Length } -Descending)) {
    if ((Test-GtWithin $directory $installed) -and (Test-Path -LiteralPath $directory -PathType Container)) {
        Assert-GtPlainPath $directory
        if (@(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) { Remove-Item -LiteralPath $directory -Force }
    }
}
if ('settingsProfileNames' -in $transaction.PSObject.Properties.Name) {
    $profiles=Join-Path $installed 'Settings'
    $currentNames=@()
    if (Test-Path -LiteralPath $profiles -PathType Container) { $currentNames=@(Get-GtManifest $profiles | ForEach-Object { $_.path }) }
    $unexpected=@($currentNames | Where-Object { $_ -notin @($transaction.settingsProfileNames) })
    if ($unexpected.Count) { throw "Unexpected settings profiles were retained for inspection: $($unexpected -join ', ')" }
}
if ('nativeDisplaySettings' -in $transaction.PSObject.Properties.Name) {
    $displayRestoration=Restore-NativeDisplaySettings $transaction.nativeDisplaySettings
    $transaction | Add-Member -NotePropertyName nativeDisplayRestoration -NotePropertyValue $displayRestoration -Force
}
$transaction.state='restored'
$transaction | Add-Member -NotePropertyName restoredUtc -NotePropertyValue ([datetime]::UtcNow.ToString('O')) -Force
Write-GtJson $manifestPath $transaction
if (Test-Path -LiteralPath $markerPath) { Remove-Item -LiteralPath $markerPath -Force }
[void](Clear-GtRestoredBackup $run)
Write-Host 'Installed files and settings restored with verified hashes and timestamps.'
} finally { $operationLock.ReleaseMutex(); $operationLock.Dispose() }
