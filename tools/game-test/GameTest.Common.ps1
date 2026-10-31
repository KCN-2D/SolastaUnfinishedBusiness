Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:GameTestRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script:GameTestBuildRoot = Join-Path $script:GameTestRepo 'tools\CustomBuildLocal'
$script:GameTestArtifactRoot = Join-Path $PSScriptRoot 'artifacts'
. (Join-Path $script:GameTestRepo 'tools\runtime-verification\WindowsDesktop.ps1')
. (Join-Path $script:GameTestRepo 'tools\runtime-verification\EvidencePaths.ps1')
$script:GtValidatedDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

function Get-GtPath([string] $Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Test-GtWithin([string] $Path, [string] $Parent) {
    $a = Get-GtPath $Path; $b = Get-GtPath $Parent
    return $a.Equals($b, [StringComparison]::OrdinalIgnoreCase) -or
        $a.StartsWith($b + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-GtPlainPath([string] $Path) {
    $cursor = Get-GtPath $Path
    while ($cursor) {
        if ($script:GtValidatedDirectories.Contains($cursor)) { break }
        try {
            $attributes=[IO.File]::GetAttributes($cursor)
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse points are not accepted: $cursor" }
            if ($attributes -band [IO.FileAttributes]::Directory) { [void]$script:GtValidatedDirectories.Add($cursor) }
        } catch {
            $cause=$_.Exception.InnerException
            if ($cause -isnot [IO.FileNotFoundException] -and $cause -isnot [IO.DirectoryNotFoundException]) { throw }
        }
        $parent = Split-Path -Parent $cursor
        if ($parent -eq $cursor) { break }; $cursor = $parent
    }
}

function Assert-GtTree([string] $Path) {
    $script:GtValidatedDirectories.Clear()
    Assert-GtPlainPath $Path
    $root=[IO.DirectoryInfo]::new($Path)
    if ($root.Exists) {
        $pending=[Collections.Generic.Stack[IO.DirectoryInfo]]::new()
        $pending.Push($root)
        while ($pending.Count) {
            foreach ($item in $pending.Pop().EnumerateFileSystemInfos()) {
                $attributes=$item.Attributes
                if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point in tree: $($item.FullName)" }
                if ($attributes -band [IO.FileAttributes]::Directory) {
                    [void]$script:GtValidatedDirectories.Add($item.FullName)
                    $pending.Push([IO.DirectoryInfo]$item)
                }
            }
        }
    }
}

function Assert-GtFile([string] $Path) {
    Assert-GtPlainPath $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing file: $Path" }
}

function Write-GtJson([string] $Path, [object] $Value) {
    Assert-GtPlainPath $Path
    $temporary=$Path + '.tmp-' + [guid]::NewGuid().ToString('N')
    $previous=$Path + '.old-' + [guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllText($temporary, (ConvertTo-Json -InputObject $Value -Depth 20), [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary,$Path,$previous) }
        else { [IO.File]::Move($temporary,$Path) }
    } finally {
        foreach ($ownedFile in @($temporary,$previous)) { if (Test-Path -LiteralPath $ownedFile -PathType Leaf) { Remove-Item -LiteralPath $ownedFile -Force } }
    }
}

function Get-GtHash([string] $Path) { Assert-GtFile $Path; return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

function Clear-GtRestoredBackup([string] $RunDirectory, [switch] $Preview) {
    $run = Get-GtPath $RunDirectory
    if ($run -eq (Get-GtPath $script:GameTestArtifactRoot) -or -not (Test-GtWithin $run $script:GameTestArtifactRoot)) {
        throw 'Backup cleanup requires an owned run under tools/game-test/artifacts.'
    }
    $transaction = Read-GtObject (Join-Path $run 'Transaction.json')
    if ($transaction.schemaVersion -ne 1 -or $transaction.state -ne 'restored') {
        throw 'Unrestored transactions must retain their backups.'
    }
    $markerPath = Join-Path $transaction.gameDirectory 'Mods\.UBGameTestTransaction.json'
    if (Test-Path -LiteralPath $markerPath) {
        throw 'An unresolved recovery transaction for this game must retain its backups.'
    }
    $backup = Join-Path $run 'Backup'
    $receiptPath = Join-Path $run 'BackupRelease.json'
    if (-not (Test-Path -LiteralPath $backup)) {
        if (Test-Path -LiteralPath $receiptPath) { return Read-GtObject $receiptPath }
        return [pscustomobject]@{ files=0; bytes=0; released=$true }
    }
    Assert-GtTree $backup
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $transaction.files) {
        if (-not $entry.existed) { continue }
        if ($entry.backup -notmatch '^Backup/\d{6}\.bin$') { throw 'Unexpected backup entry in restored transaction.' }
        [void]$expected.Add((Get-GtPath (Join-Path $run $entry.backup)))
    }
    $files = @(Get-ChildItem -LiteralPath $backup -File -Recurse -Force)
    if (@($files | Where-Object { -not $expected.Contains($_.FullName) }).Count) {
        throw 'Unrecognized files in a restored backup were retained.'
    }
    $receipt = [ordered]@{ schemaVersion=1; released=$false; files=$files.Count; bytes=[long]($files | Measure-Object Length -Sum | ForEach-Object { $_.Sum }); utc=[datetime]::UtcNow.ToString('O') }
    if ($Preview) { return [pscustomobject]$receipt }
    Write-GtJson $receiptPath $receipt
    # The fixed child path was checked for traversal and reparse points before removal.
    Remove-Item -LiteralPath $backup -Recurse -Force
    $receipt.released=$true
    Write-GtJson $receiptPath $receipt
    return [pscustomobject]$receipt
}

function New-GtDirectory([string] $Root, [string] $Name) {
    $rootPath = Get-GtPath $Root
    if (-not (Test-GtWithin $rootPath $script:GameTestArtifactRoot)) {
        throw 'EvidenceRoot must be tools/game-test/artifacts or one of its subdirectories.'
    }
    $plan = Get-VerificationEvidencePath -Root $rootPath -Name $Name -ArtifactRoot $script:GameTestArtifactRoot -Layout GameTest
    $path = $plan.directory
    Assert-GtPlainPath $rootPath
    New-Item -ItemType Directory -Path $path | Out-Null
    Write-GtJson (Join-Path $path 'Directory.json') $plan
    return $path
}

function ConvertTo-GtUtcDate([object] $Value) {
    if ($Value -is [datetime]) { return $Value.ToUniversalTime() }
    return [datetime]::Parse([string]$Value,[Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
}

function Resolve-GtArtifactPath([string] $Path) {
    $resolved=Get-GtPath $Path
    $mappingPath=Join-Path $script:GameTestArtifactRoot 'Migration.json'
    if (Test-Path -LiteralPath $mappingPath -PathType Leaf) {
        $migration=Read-GtObject $mappingPath
        if ($migration.schemaVersion -ne 1) { throw 'Unknown artifact migration schema.' }
        foreach ($mapping in @($migration.mappings | Sort-Object { $_.originalRoot.Length } -Descending)) {
            $original=Get-GtPath $mapping.originalRoot
            $current=Get-GtPath $mapping.currentRoot
            if (-not (Test-GtWithin $current $script:GameTestArtifactRoot)) { throw 'Migration destinations must remain within game-test/artifacts.' }
            if (Test-GtWithin $resolved $original) {
                $resolved=$current + $resolved.Substring($original.Length)
                break
            }
        }
    }
    return $resolved
}

function Assert-GtGameStopped {
    Assert-VerificationGameStopped
}

function Get-GtAssemblyCaches([string] $Directory, [string] $AssemblyName) {
    Assert-GtTree $Directory
    $pattern='^' + [regex]::Escape($AssemblyName) + '\.([0-9]{1,5})\.cache(?:\.pdb)?$'
    return @(Get-ChildItem -LiteralPath $Directory -File | Where-Object {
        $_.Name -cmatch $pattern -and [int]$Matches[1] -le [uint16]::MaxValue
    })
}

function Enter-GtLock {
    $mutex=[Threading.Mutex]::new($false,'Local\SolastaUBGameTest')
    try { $acquired=$mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $acquired=$true }
    if (-not $acquired) { $mutex.Dispose(); throw 'Another game-test operation owns the deployment lock.' }
    return $mutex
}

function Invoke-GtGit([string[]] $Arguments) {
    $result = @(& git -c ('safe.directory=' + $script:GameTestRepo.Replace('\','/')) -c core.quotepath=false -C $script:GameTestRepo @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "Git failed: $($Arguments -join ' ')" }
    return $result
}

function Get-GtSourceIdentity([string] $TargetCommit = 'HEAD') {
    $headCommit = [string](Invoke-GtGit @('rev-parse','--verify','HEAD^{commit}'))
    $target = [string](Invoke-GtGit @('rev-parse','--verify', ($TargetCommit + '^{commit}')))
    if ($target -ne $headCommit) { throw 'The wrapper builds HEAD plus the working tree. TargetCommit must resolve to the current HEAD.' }
    $files = @()
    $sourcePaths=@('--','.',':(exclude)tools')
    $overlay = @(Invoke-GtGit (@('diff','HEAD','--name-only','--diff-filter=ACDMRTUXB') + $sourcePaths))
    $overlay += @(Invoke-GtGit @('ls-files','--others','--exclude-standard'))
    $overlay += @('SolastaUnfinishedBusiness/Assembly-CSharp.dll','lib/Assembly-CSharp_public.dll')
    $overlayPaths=[Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($relative in $overlay) {
        if ($relative -notmatch '^tools/') { [void]$overlayPaths.Add($relative) }
    }
    foreach ($relative in $overlayPaths) {
        $path = Join-Path $script:GameTestRepo $relative
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $files += [ordered]@{ path = $relative; sha256 = Get-GtHash $path }
        } else { $files += [ordered]@{ path = $relative; sha256 = $null } }
    }
    $state = [ordered]@{
        head = $headCommit; targetCommit = $target
        branch = [string](Invoke-GtGit @('branch','--show-current'))
        branches = @(Invoke-GtGit @('branch','--all','--no-color'))
        status = @(Invoke-GtGit (@('status','--porcelain=v1') + $sourcePaths))
        index = @(Invoke-GtGit (@('diff','--cached','--raw','--full-index') + $sourcePaths)); overlayFiles = $files
    }
    $jsonArguments=@{Depth=10;Compress=$true}
    if ((Get-Command ConvertTo-Json).Parameters.ContainsKey('EscapeHandling')) {
        # Match Windows PowerShell's JSON escaping on PowerShell 7 as well.
        $jsonArguments.EscapeHandling='EscapeHtml'
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($state | ConvertTo-Json @jsonArguments))
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-','') } finally { $algorithm.Dispose() }
    return [ordered]@{ digest = $digest; state = $state }
}

function Get-GtManifest([string] $Directory) {
    Assert-GtTree $Directory
    $root = Get-GtPath $Directory
    [string[]]$paths = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force | ForEach-Object FullName)
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    return @($paths | ForEach-Object {
        [ordered]@{ path = $_.Substring($root.Length + 1).Replace('\','/'); size = (Get-Item -LiteralPath $_).Length; sha256 = Get-GtHash $_ }
    })
}

function Assert-GtManifest([string] $Directory, [object[]] $Manifest) {
    $actual = @(Get-GtManifest $Directory)
    if ($actual.Count -ne $Manifest.Count) { throw "Manifest file count differs: $Directory" }
    $expectedByPath = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($entry in $Manifest) {
        if ($expectedByPath.ContainsKey($entry.path)) { throw "Duplicate manifest path: $($entry.path)" }
        $expectedByPath.Add($entry.path, $entry)
    }
    foreach ($entry in $actual) {
        if (-not $expectedByPath.ContainsKey($entry.path) -or
            $entry.sha256 -ne $expectedByPath[$entry.path].sha256 -or
            $entry.size -ne $expectedByPath[$entry.path].size) {
            throw "Manifest mismatch: $($entry.path)"
        }
    }
}

function Copy-GtFile([string] $Source, [string] $Destination, [string] $ExpectedSha256, [switch] $SkipIfIdentical) {
    Assert-GtFile $Source; Assert-GtPlainPath $Destination
    if ($ExpectedSha256 -and $ExpectedSha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'ExpectedSha256 must be a SHA-256 digest.' }
    if ($SkipIfIdentical -and -not $ExpectedSha256) { throw 'SkipIfIdentical requires an independently verified ExpectedSha256.' }
    if ($SkipIfIdentical -and (Test-Path -LiteralPath $Destination -PathType Leaf) -and (Get-GtHash $Destination) -eq $ExpectedSha256) { return }
    New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
    $expected=if ($ExpectedSha256) { $ExpectedSha256 } else { Get-GtHash $Source }
    if ($expected -ne (Get-GtHash $Destination)) { throw "Copy verification failed: $Destination" }
}

function Read-GtObject([string] $Path) { Assert-GtFile $Path; return [IO.File]::ReadAllText($Path) | ConvertFrom-Json }

function Assert-GtProperties([object] $Object, [string[]] $Allowed, [string[]] $Required) {
    foreach ($property in $Object.PSObject.Properties.Name) { if ($property -notin $Allowed) { throw "Unknown JSON property: $property" } }
    foreach ($property in $Required) { if ($property -notin $Object.PSObject.Properties.Name) { throw "Missing JSON property: $property" } }
}

function Get-GtAdapter(
    [string] $Name,
    [string[]] $ScenarioSource=@(),
    [string] $ScenarioType,
    [string] $ScenarioValidator,
    [Nullable[bool]] $ScenarioObservation=$null,
    [Nullable[bool]] $ScenarioSaveScenario=$null,
    [string[]] $ScenarioReferencePath=@()
) {
    $builtIn=@('startup','save-smoke')
    $localRequested=@($ScenarioSource).Count -gt 0 -or $ScenarioType -or $ScenarioValidator -or $null -ne $ScenarioObservation -or $null -ne $ScenarioSaveScenario -or @($ScenarioReferencePath).Count -gt 0
    if ($localRequested) {
        if ($Name -in $builtIn) { throw 'Local scenario arguments cannot override a built-in adapter.' }
        if ($Name -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$' -or @($ScenarioSource).Count -eq 0 -or
            $ScenarioType -notmatch '^[A-Za-z_][A-Za-z0-9_.+]*$' -or -not $ScenarioValidator -or $null -eq $ScenarioObservation) {
            throw 'A local scenario requires an adapter name, source files, probe type, validator and explicit observation selection.'
        }
        $sources=@($ScenarioSource | ForEach-Object {
            $path=Get-GtPath $_; Assert-GtFile $path
            if ([IO.Path]::GetExtension($path) -ne '.cs') { throw 'ScenarioSource must contain C# files.' }
            $path
        } | Select-Object -Unique)
        $validator=Get-GtPath $ScenarioValidator; Assert-GtFile $validator
        if ([IO.Path]::GetExtension($validator) -ne '.ps1') { throw 'ScenarioValidator must be a PowerShell script.' }
        $references=@($ScenarioReferencePath | ForEach-Object {
            $path=Get-GtPath $_; Assert-GtFile $path
            if ([IO.Path]::GetExtension($path) -ne '.dll') { throw 'ScenarioReferencePath must contain DLL files.' }
            $path
        } | Select-Object -Unique)
        $saveScenario=if ($null -eq $ScenarioSaveScenario) { $true } else { [bool]$ScenarioSaveScenario }
        return [pscustomobject]@{ name=$Name; saveScenario=$saveScenario; observation=[bool]$ScenarioObservation; sources=$sources; probeType=$ScenarioType; caseValidator=$validator; referencePaths=$references }
    }
    switch ($Name) {
        'startup' { return [pscustomobject]@{ name=$Name; saveScenario=$false; observation=$false; sources=@(); probeType=$null; caseValidator=$null; referencePaths=@() } }
        'save-smoke' { return [pscustomobject]@{ name=$Name; saveScenario=$true; observation=$false; sources=@((Join-Path $PSScriptRoot 'Probes\SaveSmokeProbe.cs')); probeType='GameTestSaveSmokeProbe'; caseValidator=$null; referencePaths=@() } }
        default { throw 'Unknown adapter. Local scenarios require explicit CLI source, type, validator and observation arguments.' }
    }
}

function Assert-GtInitialSetup([object] $Case, [bool] $SaveScenario) {
    if ('setup' -notin $Case.initialState.PSObject.Properties.Name -or $null -eq $Case.initialState.setup) { return }
    if (-not $SaveScenario) { throw 'Initial setup requires a saved-game adapter.' }
    $setup=$Case.initialState.setup
    Assert-GtProperties $setup @('kind','actorSource','actorIndex','powerName','positionOffset','expectedGuestCount') @('kind','actorIndex','powerName','positionOffset','expectedGuestCount')
    if ($setup.kind -ne 'use-power') { throw 'Initial setup kind must be use-power.' }
    if ('actorSource' -notin $setup.PSObject.Properties.Name) { $setup | Add-Member -NotePropertyName actorSource -NotePropertyValue 'party' }
    elseif ($null -eq $setup.actorSource) { $setup.actorSource='party' }
    if ($setup.actorSource -notin @('party','guest')) { throw 'Setup actorSource must be party or guest.' }
    if (($setup.actorIndex -isnot [int] -and $setup.actorIndex -isnot [long]) -or $setup.actorIndex -lt 0 -or $setup.actorIndex -gt [int]::MaxValue) { throw 'Setup actorIndex must be a nonnegative Int32.' }
    if ($setup.powerName -isnot [string] -or [string]::IsNullOrWhiteSpace($setup.powerName) -or $setup.powerName.Length -gt 160) { throw 'Setup powerName must be the complete name of an owned usable power, within 160 characters.' }
    if (@($setup.positionOffset).Count -ne 3) { throw 'Setup positionOffset must contain three integers.' }
    foreach ($offset in $setup.positionOffset) {
        if (($offset -isnot [int] -and $offset -isnot [long]) -or $offset -lt -3 -or $offset -gt 3) { throw 'Setup positionOffset values must be integers within -3..3.' }
    }
    if (@($setup.positionOffset | Where-Object { $_ -ne 0 }).Count -eq 0) { throw 'Setup positionOffset must change position.' }
    if (($setup.expectedGuestCount -isnot [int] -and $setup.expectedGuestCount -isnot [long]) -or $setup.expectedGuestCount -lt 0 -or $setup.expectedGuestCount -gt 32) { throw 'Setup expectedGuestCount must be an integer within 0..32.' }
}

function Read-GtCase([string] $Path, [object] $AdapterDefinition=$null) {
    $case = Read-GtObject $Path
    Assert-GtProperties $case @('schemaVersion','id','adapter','initialState','operation','expected','observation','timeoutSeconds') @('schemaVersion','id','adapter','initialState','operation','expected','observation','timeoutSeconds')
    if (($case.schemaVersion -isnot [int] -and $case.schemaVersion -isnot [long]) -or $case.schemaVersion -ne 1 -or $case.id -isnot [string] -or $case.id -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$') { throw 'Invalid case schemaVersion or id.' }
    $adapter=if ($null -eq $AdapterDefinition) { Get-GtAdapter $case.adapter } else { $AdapterDefinition }
    if ($case.adapter -ne $adapter.name) { throw 'Case adapter does not match the explicitly selected scenario.' }
    if ($case.timeoutSeconds -isnot [long] -and $case.timeoutSeconds -isnot [int]) { throw 'timeoutSeconds must be an integer.' }
    if ($case.timeoutSeconds -lt 15 -or $case.timeoutSeconds -gt 1800) { throw 'timeoutSeconds must be 15..1800.' }
    if ($adapter.saveScenario) {
        if ($case.timeoutSeconds -lt 30) { throw 'Saved-game timeoutSeconds must be 30..1800.' }
        if ('verifyFocusLoss' -notin $case.operation.PSObject.Properties.Name) { $case.operation | Add-Member -NotePropertyName verifyFocusLoss -NotePropertyValue $false }
        if ($case.operation.verifyFocusLoss -isnot [bool]) { throw 'verifyFocusLoss must be a Boolean.' }
        foreach ($key in @('manualGate','manualGateTimeoutSeconds')) { if ($key -notin $case.operation.PSObject.Properties.Name) { throw "Missing operation property: $key" } }
        if ($case.operation.manualGate -isnot [bool] -or ($case.operation.manualGateTimeoutSeconds -isnot [int] -and $case.operation.manualGateTimeoutSeconds -isnot [long])) { throw 'Manual gate must contain a Boolean and integer seconds.' }
        if ($case.operation.manualGate -and ($case.operation.manualGateTimeoutSeconds -lt 1 -or $case.operation.manualGateTimeoutSeconds -gt $case.timeoutSeconds)) { throw 'manualGateTimeoutSeconds must be 1..timeoutSeconds when manualGate is enabled.' }
        $moveOperation=$case.operation.kind -eq 'move'
        if ($moveOperation -or -not $adapter.caseValidator) {
            Assert-GtProperties $case.operation @('kind','actorSource','actorIndex','offset','manualGate','manualGateTimeoutSeconds','verifyFocusLoss') @('kind','actorIndex','offset','manualGate','manualGateTimeoutSeconds')
        } elseif ($case.operation.kind -isnot [string] -or $case.operation.kind -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$') {
            throw 'A local operation must have a valid kind and an explicit scenario validator.'
        }
        if ('actorSource' -notin $case.operation.PSObject.Properties.Name) { $case.operation | Add-Member -NotePropertyName actorSource -NotePropertyValue 'party' }
        elseif ($null -eq $case.operation.actorSource) { $case.operation.actorSource='party' }
        if ($case.operation.actorSource -notin @('party','guest')) { throw 'actorSource must be party or guest.' }
        if (-not $adapter.caseValidator) { Assert-GtProperties $case.expected @('loadedPartyCount','requireUserLocation','requireExploration') @('loadedPartyCount','requireUserLocation','requireExploration') }
        if ($case.operation.actorIndex -isnot [int] -and $case.operation.actorIndex -isnot [long]) { throw 'actorIndex must be an integer.' }
        if ($case.operation.actorIndex -lt 0 -or $case.operation.actorIndex -gt [int]::MaxValue) { throw 'actorIndex must be a nonnegative Int32.' }
        if ($moveOperation) {
            if (@($case.operation.offset).Count -ne 3) { throw 'Move offsets must contain three integers.' }
            foreach ($offset in $case.operation.offset) { if ($offset -isnot [int] -and $offset -isnot [long]) { throw 'Move offsets must be integers.' }; if ([Math]::Abs($offset) -gt 3) { throw 'Smoke movement is limited to three cells per axis.' } }
            if (@($case.operation.offset | Where-Object { $_ -ne 0 }).Count -eq 0) { throw 'A smoke move must change position.' }
        } elseif (-not $adapter.caseValidator) { throw 'Non-move operations require an explicit local scenario validator.' }
        if ($case.expected.loadedPartyCount -isnot [int] -and $case.expected.loadedPartyCount -isnot [long]) { throw 'loadedPartyCount must be an integer.' }
        if ($case.expected.loadedPartyCount -lt 1 -or $case.expected.loadedPartyCount -gt 6 -or $case.expected.requireUserLocation -isnot [bool] -or $case.expected.requireExploration -isnot [bool]) { throw 'Invalid expected initial state.' }
    }
    Assert-GtProperties $case.observation @('enabled') @('enabled')
    if ($case.observation.enabled -isnot [bool]) { throw 'observation.enabled must be a Boolean.' }
    if ($case.observation.enabled -ne $adapter.observation) { throw 'Observation selection must match the fixed adapter.' }
    if ($adapter.caseValidator) { & $adapter.caseValidator $case }
    Assert-GtInitialSetup $case $adapter.saveScenario
    return $case
}

function Get-GtEnvironment([string] $GameExePath, [string] $TargetCommit = 'HEAD') {
    $gameExe = Get-GtPath $GameExePath
    Assert-GtFile $gameExe
    $game = Split-Path -Parent $gameExe
    if ((Test-GtWithin $game $script:GameTestRepo) -or [IO.Path]::GetFileName($gameExe) -ne 'Solasta.exe') { throw 'Specify an installed Solasta.exe outside the repository.' }
    $source = Get-GtSourceIdentity $TargetCommit
    $minimum = [version](Read-GtObject (Join-Path $script:GameTestRepo 'SolastaUnfinishedBusiness\Info.json')).ManagerVersion
    $assemblies = @()
    foreach ($relative in @('Solasta_Data/Managed/Assembly-CSharp.dll','Solasta_Data/Managed/UnityEngine.CoreModule.dll','Solasta_Data/Managed/UnityModManager/UnityModManager.dll','Solasta_Data/Managed/UnityModManager/0Harmony.dll','Solasta_Data/Managed/UnityModManager/dnlib.dll')) {
        $file = Join-Path $game $relative; Assert-GtFile $file
        $version = [Reflection.AssemblyName]::GetAssemblyName($file).Version.ToString()
        if ($relative -like '*/UnityModManager.dll' -and [version]$version -lt $minimum) { throw "Unity Mod Manager $minimum or newer is required; found $version." }
        $snapshot = Join-Path (Join-Path $script:GameTestRepo 'SolastaFiles') $relative
        $assemblies += [ordered]@{ path = $relative; version = $version; sha256 = Get-GtHash $file; snapshotSha256 = $(if (Test-Path -LiteralPath $snapshot) { Get-GtHash $snapshot } else { $null }) }
    }
    foreach ($relative in @('winhttp.dll','doorstop_config.ini','Solasta_Data/Managed/UnityModManager/Params.xml')) { Assert-GtFile (Join-Path $game $relative) }
    $commands = @('git','dotnet','tar','robocopy' | ForEach-Object { $command = Get-Command $_ -ErrorAction Stop; [ordered]@{ name=$_; path=$command.Source } })
    $sdks = @(& dotnet --list-sdks)
    if (-not ($sdks | Where-Object { $_ -match '^(?:9|[1-9]\d)\.' })) { throw '.NET SDK 9 or newer is required.' }
    $project = Join-Path $script:GameTestRepo 'SolastaUnfinishedBusiness\SolastaUnfinishedBusiness.csproj'
    [xml]$xml = [IO.File]::ReadAllText($project)
    $outputPaths = @($xml.SelectNodes('//OutputPath') | ForEach-Object { $_.InnerText })
    $buildInputs=@('BuildCustomLocalPackage.cmd','internal/BuildCustomLocalPackage.ps1','internal/CustomBuildRuntime.patch' | ForEach-Object {
        $path=Join-Path $script:GameTestBuildRoot $_
        [ordered]@{path=$path;sha256=Get-GtHash $path}
    })
    $processState=Get-VerificationProcessState -Name Solasta
    return [ordered]@{ gameExe=$gameExe; gameVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($gameExe).FileVersion; gameSha256=Get-GtHash $gameExe; minimumManagerVersion=$minimum.ToString(); assemblies=$assemblies; commands=$commands; sdks=$sdks; source=$source; projectOutputPaths=$outputPaths; buildInputs=$buildInputs; gameStopped=($processState.querySucceeded -and $processState.matches.Count -eq 0); gameProcessState=$processState }
}


function Assert-GtRuntimeOwnerStopped($Cleanup) {
    if ($null -eq $Cleanup -or $Cleanup.completed -isnot [bool] -or -not $Cleanup.completed -or
        ($Cleanup.expectedProcessId -isnot [int] -and $Cleanup.expectedProcessId -isnot [long]) -or
        $Cleanup.expectedProcessId -le 0 -or $Cleanup.expectedPath -isnot [string] -or
        -not [IO.Path]::IsPathRooted($Cleanup.expectedPath)) { throw 'Runtime process ownership is incomplete.' }
    $started=([datetime]$Cleanup.expectedStartTimeUtc).ToUniversalTime()
    $state=Get-VerificationProcessState -Name ([IO.Path]::GetFileNameWithoutExtension($Cleanup.expectedPath))
    if (-not $state.querySucceeded) { throw 'Runtime process ownership could not be queried.' }
    foreach ($match in $state.matches) {
        if ($null -eq $match.pid) { throw 'A process identity could not be read.' }
        if ($match.pid -ne $Cleanup.expectedProcessId) { continue }
        $errors=$match.readErrors
        $errorCount=if ($errors -is [Collections.IDictionary]) { $errors.Count } else { @($errors.PSObject.Properties).Count }
        if ($errorCount) { throw 'The runtime process identity could not be read.' }
        if ([Math]::Abs((([datetime]$match.startTimeUtc).ToUniversalTime()-$started).TotalSeconds) -ge 1) { continue }
        if ((Get-GtPath $match.path) -ne (Get-GtPath $Cleanup.expectedPath)) { throw 'The runtime process identity is inconsistent.' }
        throw 'The runtime owner process is still active.'
    }
}

function Get-GtRuntimeRun([string]$Path,$Result) {
    $ownership=Read-GtObject (Join-Path $Path 'Directory.json')
    if ($ownership.schemaVersion -ne 1 -or $ownership.layout -cne 'Runtime' -or
        (Get-GtPath $ownership.directory) -ne (Get-GtPath $Path) -or
        (Get-GtPath $Result.evidenceDirectory) -ne (Get-GtPath $Path)) { throw 'Runtime directory ownership does not match.' }
    foreach ($field in @('settingsRestored','displaySettingsRestored','probeRemoved','originalNativeFilesUnchanged')) {
        if ($Result.$field -isnot [bool] -or -not $Result.$field) { throw 'Runtime restoration is incomplete.' }
    }
    if ($Result.status -cnotin @('success','failed','incomplete') -or [string]::IsNullOrWhiteSpace($Result.runName) -or
        $Result.processId -ne $Result.processCleanup.expectedProcessId) { throw 'Runtime restoration is incomplete.' }
    Assert-GtRuntimeOwnerStopped $Result.processCleanup
    $completed=([datetime]$Result.completedUtc).ToUniversalTime()
    if ($completed -lt ([datetime]$Result.startedUtc).ToUniversalTime()) { throw 'Runtime completion precedes its start.' }
    [pscustomobject]@{path=$Path;name=(Split-Path -Leaf $Path);caseId=('@Runtime:'+$Result.runName);completed=$completed;
        gameDirectory=(Split-Path -Parent $Result.processCleanup.expectedPath);buildHash=$null;kind='runtime';result=$Result}
}

function Get-GtNativeUserDataCompanions([string]$Run) {
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
        [pscustomobject]@{path=$path;owner=$location.owner;evidence=$evidence;locationPath=$manifest.FullName;locationSha256=Get-GtHash $manifest.FullName;bytes=[long]((Get-ChildItem -LiteralPath $path -File -Recurse -Force | Measure-Object Length -Sum).Sum)}
    }
}

function Read-GtStorageReferences([string[]]$ReferencePath) {
    $paths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $inputs=@(foreach($path in $ReferencePath){
        $manifestPath=Get-GtPath $path
        Assert-GtFile $manifestPath
        if(Test-GtWithin $manifestPath $script:GameTestArtifactRoot){[void]$paths.Add($manifestPath)}
        $manifest=Read-GtObject $manifestPath
        Assert-GtProperties $manifest @('schemaVersion','paths') @('schemaVersion','paths')
        if($manifest.schemaVersion -ne 1 -or $manifest.paths -isnot [array]){throw 'Invalid storage reference manifest.'}
        foreach($entry in $manifest.paths){
            if($entry -isnot [string] -or [string]::IsNullOrWhiteSpace($entry)){throw 'Storage references must be paths.'}
            $resolved=if([IO.Path]::IsPathRooted($entry)){Get-GtPath $entry}else{Get-GtPath (Join-Path (Split-Path -Parent $manifestPath) $entry)}
            if(-not(Test-GtWithin $resolved $script:GameTestArtifactRoot)){throw 'Storage references must identify artifact paths.'}
            [void]$paths.Add($resolved)
        }
        [ordered]@{path=$manifestPath;sha256=Get-GtHash $manifestPath}
    })
    return [pscustomobject]@{paths=$paths;inputs=$inputs}
}

function Assert-GtStorageReferences($References) {
    foreach($reference in $References.inputs){
        if((Get-GtHash $reference.path) -ne $reference.sha256){throw 'A storage reference manifest changed during cleanup.'}
    }
}

function Get-GtStorageReferenceText {
    $references=[Text.StringBuilder]::new()
    foreach($folder in @('Cases','Scenarios')){
        $root=Join-Path $PSScriptRoot $folder
        if(-not [IO.Directory]::Exists($root)){continue}
        foreach($file in Get-ChildItem -LiteralPath $root -Recurse -File|Where-Object{$_.Extension -in @('.json','.ps1','.cs','.mjs')}){
            [void]$references.AppendLine([IO.File]::ReadAllText($file.FullName))
        }
    }
    return $references.ToString()
}

function Clear-GtNativeUserData {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][string]$RunDirectory,[string[]]$ReferencePath=@(),[string[]]$PreserveDirectory=@(),[switch]$Keep,[switch]$Preview)
    $run=Get-GtPath $RunDirectory
    if(-not(Test-GtWithin $run $script:GameTestArtifactRoot) -or $run -eq $script:GameTestArtifactRoot -or (Test-GtWithin $run (Join-Path $script:GameTestArtifactRoot '.native'))){throw 'Native data release requires its owning evidence run.'}
    if(-not [IO.Directory]::Exists($run)){throw 'The owning evidence run does not exist.'}
    Assert-GtTree $run
    $lock=Enter-GtLock
    $journal=$null
    $receiptPath=Join-Path $run 'NativeUserDataRelease.json'
    $journalPath=Join-Path $run 'NativeUserDataRelease.jsonl'
    $actions=[Collections.Generic.List[object]]::new()
    $receipt=[ordered]@{schemaVersion=1;utc=[DateTime]::UtcNow.ToString('O');status='RETAINED';runDirectory=$run;
        preview=[bool]$Preview;resultSha256=$null;runtimeResultSha256=$null;referenceInputs=@();actions=@();reason=$null;journal='NativeUserDataRelease.jsonl'}
    function Write-NativeReleaseEvent([string]$Event,$Detail){
        if($Preview){return}
        $bytes=[Text.UTF8Encoding]::new($false).GetBytes((([ordered]@{utc=[DateTime]::UtcNow.ToString('O');event=$Event;detail=$Detail}|ConvertTo-Json -Depth 15 -Compress)+[Environment]::NewLine))
        $journal.Write($bytes,0,$bytes.Length);$journal.Flush($true)
    }
    function Assert-NativeReleaseOwner {
        Assert-GtGameStopped
        Assert-GtStorageReferences $references
        if((Get-GtHash $resultPath) -ne $receipt.resultSha256 -or (Get-GtHash $runtimePath) -ne $runtimeIdentityHash){throw 'A native release result changed during cleanup.'}
        if($transactionPath){
            if((Get-GtHash $transactionPath) -ne $transactionHash){throw 'The native release transaction changed during cleanup.'}
            $transaction=Read-GtObject $transactionPath
            if($transaction.schemaVersion -ne 1 -or $transaction.state -cne 'restored'){throw 'Native release requires complete outer restoration.'}
        }
        if([IO.File]::Exists((Join-Path $runtime.gameDirectory 'Mods/.UBGameTestTransaction.json'))){throw 'A recovery transaction still owns this game.'}
        [void](Get-GtRuntimeRun (Split-Path -Parent $runtimePath) (Read-GtObject $runtimePath))
    }
    function Test-NativeReleaseReference($Companion){
        foreach($path in @($references.paths)+@($PreserveDirectory)){
            $resolved=Get-GtPath $path
            if((Test-GtWithin $resolved $Companion.path) -or (Test-GtWithin $Companion.path $resolved)){return $true}
        }
        # Re-read the same case/scenario consumers used by general artifact cleanup.
        return (Get-GtStorageReferenceText).IndexOf($Companion.owner,[StringComparison]::OrdinalIgnoreCase) -ge 0
    }
    function Test-NativeReleaseOutput($Companion){
        foreach($name in @('Saves','Characters')){
            $folder=Join-Path $Companion.path $name
            if([IO.Directory]::Exists($folder)){
                foreach($file in [IO.Directory]::EnumerateFiles($folder,'*',[IO.SearchOption]::AllDirectories)){return $true}
            }
        }
        return $false
    }
    try{
        $references=Read-GtStorageReferences $ReferencePath
        $receipt.referenceInputs=$references.inputs
        $resultPath=Join-Path $run 'Result.json'
        $result=Read-GtObject $resultPath
        $receipt.resultSha256=Get-GtHash $resultPath
        $previousRelease=if([IO.File]::Exists($receiptPath)){Read-GtObject $receiptPath}else{$null}
        $transactionPath=$null;$transactionHash=$null
        if([IO.File]::Exists((Join-Path $run 'Transaction.json'))){
            $transactionPath=Join-Path $run 'Transaction.json'
            $transactionHash=Get-GtHash $transactionPath
            $transaction=Read-GtObject $transactionPath
            if($transaction.schemaVersion -ne 1 -or $transaction.state -cne 'restored' -or $result.schemaVersion -ne 1 -or $result.restored -isnot [bool] -or -not $result.restored -or $result.displaySettingsRestored -isnot [bool] -or -not $result.displaySettingsRestored){throw 'Native release requires complete outer restoration.'}
            if((Get-GtPath $result.evidenceDirectory) -ne $run){throw 'Unknown outer evidence ownership.'}
            if('originalSavePreserved' -in $result.PSObject.Properties.Name -and ($result.originalSavePreserved -isnot [bool] -or -not $result.originalSavePreserved)){throw 'The original save was not preserved.'}
            if($result.runtimeResult -isnot [string] -or -not(Test-GtWithin $result.runtimeResult $run)){throw 'Unknown runtime result ownership.'}
            $runtimePath=Get-GtPath $result.runtimeResult
        }else{$runtimePath=$resultPath}
        $runtime=Get-GtRuntimeRun (Split-Path -Parent $runtimePath) (Read-GtObject $runtimePath)
        if($transactionPath -and (Get-GtPath $transaction.gameDirectory) -ne (Get-GtPath $runtime.gameDirectory)){throw 'Outer and runtime game ownership differ.'}
        $receipt.runtimeResultSha256=Get-GtHash $runtimePath
        $runtimeIdentityHash=$receipt.runtimeResultSha256
        $companions=@(Get-GtNativeUserDataCompanions $run)
        if(-not $Preview){
            Assert-GtPlainPath $receiptPath;Assert-GtPlainPath $journalPath
            $journal=[IO.File]::Open($journalPath,[IO.FileMode]::Append,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        }
        Write-NativeReleaseEvent 'release-started' ([ordered]@{resultSha256=$receipt.resultSha256;runtimeResultSha256=$receipt.runtimeResultSha256;referenceInputs=$references.inputs})
        foreach($companion in $companions){
            $companionRuntimePath=Join-Path $companion.evidence 'Result.json'
            if($companionRuntimePath -ne $runtimePath){
                $runtimePath=$companionRuntimePath
                $runtime=Get-GtRuntimeRun $companion.evidence (Read-GtObject $runtimePath)
                $runtimeIdentityHash=Get-GtHash $runtimePath
                if($transactionPath -and (Get-GtPath $transaction.gameDirectory) -ne (Get-GtPath $runtime.gameDirectory)){throw 'Outer and runtime game ownership differ.'}
            }
            $action=[ordered]@{kind='native-user-data';path=$companion.path;owner=$companion.owner;bytes=$companion.bytes;applied=$false;reason=$null;
                locationSha256=$companion.locationSha256;runtimeResult=$runtimePath;runtimeResultSha256=$runtimeIdentityHash}
            $actions.Add($action)
            if($Keep -or (Test-NativeReleaseReference $companion)){$action.reason='explicit-native-data-reference';continue}
            if(Test-NativeReleaseOutput $companion){$action.reason='unexported-native-output';continue}
            # Process checks precede the final fresh reference, marker and output reads.
            Assert-NativeReleaseOwner
            if(Test-NativeReleaseReference $companion){$action.reason='late-native-data-reference';continue}
            Assert-GtStorageReferences $references
            $confirmed=@(Get-GtNativeUserDataCompanions $run|Where-Object path -eq $companion.path)
            if($confirmed.Count -ne 1 -or $confirmed[0].locationSha256 -ne $companion.locationSha256){throw 'Native user-data ownership changed before deletion.'}
            if(Test-NativeReleaseOutput $companion){$action.reason='late-unexported-native-output';continue}
            Write-NativeReleaseEvent 'action-planned' $action
            if(-not $Preview){
                Remove-Item -LiteralPath $companion.path -Recurse -Force
                if(Test-Path -LiteralPath $companion.path){throw 'Native user data still exists after release.'}
                $action.applied=$true
                Write-NativeReleaseEvent 'action-complete' $action
            }
        }
        $receipt.status=if(-not $companions.Count){
            if($previousRelease -and $previousRelease.schemaVersion -eq 1 -and $previousRelease.runDirectory -eq $run -and
                $previousRelease.resultSha256 -eq $receipt.resultSha256 -and $previousRelease.status -in @('RELEASED','ALREADY_RELEASED')){'ALREADY_RELEASED'}else{'NO_NATIVE_DATA'}
        }elseif(@($actions|Where-Object{$_.reason}).Count){'RETAINED'}elseif($Preview){'PREVIEW'}else{'RELEASED'}
        $receipt.reason=if($receipt.status -eq 'RETAINED'){'Native data is still owned by a dependency or produced output.'}else{$null}
        Write-NativeReleaseEvent 'release-completed' ([ordered]@{status=$receipt.status;actions=$actions.ToArray()})
    }catch{
        $receipt.status='RETAINED';$receipt.reason=$_.Exception.Message
        if($null -ne $journal){Write-NativeReleaseEvent 'release-retained' @{reason=$receipt.reason}}
    }finally{
        $receipt.actions=$actions.ToArray()
        try{if(-not $Preview){Write-GtJson $receiptPath $receipt}}finally{if($null -ne $journal){$journal.Dispose()};$lock.ReleaseMutex();$lock.Dispose()}
    }
    return [pscustomobject]$receipt
}
