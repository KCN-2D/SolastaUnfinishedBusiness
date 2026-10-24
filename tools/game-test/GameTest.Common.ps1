Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:GameTestRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script:GameTestBuildRoot = Join-Path $script:GameTestRepo 'tools\CustomBuildLocal'
$script:GameTestArtifactRoot = Join-Path $PSScriptRoot 'artifacts'
. (Join-Path $script:GameTestRepo 'tools\runtime-verification\WindowsDesktop.ps1')
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
    if (Test-Path -LiteralPath $Path -PathType Container) {
        foreach ($item in Get-ChildItem -LiteralPath $Path -Recurse -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point in tree: $($item.FullName)" }
            if ($item.PSIsContainer) { [void]$script:GtValidatedDirectories.Add($item.FullName) }
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

function New-GtDirectory([string] $Root, [string] $Name) {
    $rootPath = Get-GtPath $Root
    if (-not (Test-GtWithin $rootPath $script:GameTestArtifactRoot)) {
        throw 'EvidenceRoot must be tools/game-test/artifacts or one of its subdirectories.'
    }
    Assert-GtPlainPath $rootPath
    $path = Join-Path $rootPath ((Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + $Name + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
    New-Item -ItemType Directory -Path $path | Out-Null
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
    $processState=Get-VerificationProcessState -Name Solasta
    $processDiagnostic=$processState | ConvertTo-Json -Depth 6 -Compress
    if (-not $processState.querySucceeded) { throw ('Cannot verify that Solasta is stopped before deployment or restoration. Process state: ' + $processDiagnostic) }
    if ($processState.matches.Count) { throw ('Solasta is running. Close it before deployment or restoration. Process state: ' + $processDiagnostic) }
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
    foreach ($relative in ($overlay | Where-Object { $_ -notmatch '^tools/' } | Sort-Object -Unique)) {
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
    $bytes = [Text.Encoding]::UTF8.GetBytes(($state | ConvertTo-Json -Depth 10 -Compress))
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-','') } finally { $algorithm.Dispose() }
    return [ordered]@{ digest = $digest; state = $state }
}

function Get-GtManifest([string] $Directory) {
    Assert-GtTree $Directory
    $root = Get-GtPath $Directory
    return @(Get-ChildItem -LiteralPath $root -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($root.Length + 1).Replace('\','/'); size = $_.Length; sha256 = Get-GtHash $_.FullName }
    })
}

function Assert-GtManifest([string] $Directory, [object[]] $Manifest) {
    $actual = @(Get-GtManifest $Directory)
    if ($actual.Count -ne $Manifest.Count) { throw "Manifest file count differs: $Directory" }
    for ($index=0; $index -lt $actual.Count; $index++) {
        if ($actual[$index].path -ne $Manifest[$index].path -or $actual[$index].sha256 -ne $Manifest[$index].sha256) {
            throw "Manifest mismatch: $($actual[$index].path)"
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
    [Nullable[bool]] $ScenarioObservation=$null
) {
    $builtIn=@('startup','save-smoke','native-summon-icons')
    $localRequested=@($ScenarioSource).Count -gt 0 -or $ScenarioType -or $ScenarioValidator -or $null -ne $ScenarioObservation
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
        return [pscustomobject]@{ name=$Name; saveScenario=$true; observation=[bool]$ScenarioObservation; sources=$sources; probeType=$ScenarioType; caseValidator=$validator }
    }
    switch ($Name) {
        'startup' { return [pscustomobject]@{ name=$Name; saveScenario=$false; observation=$false; sources=@(); probeType=$null; caseValidator=$null } }
        'save-smoke' { return [pscustomobject]@{ name=$Name; saveScenario=$true; observation=$false; sources=@((Join-Path $PSScriptRoot 'Probes\SaveSmokeProbe.cs')); probeType='GameTestSaveSmokeProbe'; caseValidator=$null } }
        'native-summon-icons' { return [pscustomobject]@{ name=$Name; saveScenario=$false; observation=$false; sources=@((Join-Path $script:GameTestRepo 'tools\runtime-verification\Probes\NativeSummonIconInventoryProbe.cs')); probeType='NativeSummonIconInventoryProbe'; caseValidator=$null } }
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
        Assert-GtProperties $case.operation @('kind','actorSource','actorIndex','offset','manualGate','manualGateTimeoutSeconds','verifyFocusLoss') @('kind','actorIndex','offset','manualGate','manualGateTimeoutSeconds')
        if ('actorSource' -notin $case.operation.PSObject.Properties.Name) { $case.operation | Add-Member -NotePropertyName actorSource -NotePropertyValue 'party' }
        elseif ($null -eq $case.operation.actorSource) { $case.operation.actorSource='party' }
        if ($case.operation.actorSource -notin @('party','guest')) { throw 'actorSource must be party or guest.' }
        if (-not $adapter.caseValidator) { Assert-GtProperties $case.expected @('loadedPartyCount','requireUserLocation','requireExploration') @('loadedPartyCount','requireUserLocation','requireExploration') }
        if ($case.operation.actorIndex -isnot [int] -and $case.operation.actorIndex -isnot [long]) { throw 'actorIndex must be an integer.' }
        if ($case.operation.kind -ne 'move' -or $case.operation.actorIndex -lt 0 -or $case.operation.actorIndex -gt [int]::MaxValue -or @($case.operation.offset).Count -ne 3) { throw 'Invalid move operation.' }
        foreach ($offset in $case.operation.offset) { if ($offset -isnot [int] -and $offset -isnot [long]) { throw 'Move offsets must be integers.' }; if ([Math]::Abs($offset) -gt 3) { throw 'Smoke movement is limited to three cells per axis.' } }
        if (@($case.operation.offset | Where-Object { $_ -ne 0 }).Count -eq 0) { throw 'A smoke move must change position.' }
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
