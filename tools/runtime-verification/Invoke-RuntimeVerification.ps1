[CmdletBinding(DefaultParameterSetName = 'Probe')]
param(
    [Parameter(Mandatory = $true)] [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]*$')] [string] $RunName,
    [Parameter(Mandatory = $true, ParameterSetName = 'Probe')] [string[]] $ProbeSource = @(),
    [Parameter(Mandatory = $true, ParameterSetName = 'Probe')] [string[]] $ProbeType = @(),
    [Parameter(Mandatory = $true, ParameterSetName = 'Startup')] [switch] $StartupOnly,
    [string[]] $ReferencePath = @(),
    [string] $GameExePath,
    [string] $EvidenceRoot,
    [string] $PlayerLogPath,
    [switch] $DedicatedPlayerLog,
    [switch] $ShowGameWindow,
    [switch] $RunInBackground,
    [switch] $RequireOtherAppForeground,
    [switch] $RequireNativeActionProof,
    [ValidatePattern('^[a-fA-F0-9]{64}$')] [string] $ExpectedUbSha256,
    [ValidateRange(15, 1800)] [int] $TimeoutSeconds = 120,
    [switch] $SkipBuild,
    [switch] $Sync,
    [Parameter(ParameterSetName = 'Probe')] [switch] $ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'NativeDisplaySettings.ps1')
if ($RunInBackground -and $StartupOnly) { throw 'RunInBackground requires the temporary probe harness and cannot be combined with StartupOnly.' }
if ($RequireOtherAppForeground -and $StartupOnly) { throw 'RequireOtherAppForeground requires the temporary probe harness and cannot be combined with StartupOnly.' }
if ($RequireOtherAppForeground -and $ShowGameWindow) { throw 'RequireOtherAppForeground cannot be combined with ShowGameWindow.' }
if ($RequireNativeActionProof -and (-not $RequireOtherAppForeground -or $StartupOnly)) { throw 'Native action proof requires the background probe harness.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$probeId = 'UBRuntimeVerification'

function Get-FullPath([string] $Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Test-IsWithin([string] $Path, [string] $Parent) {
    $normalized = Get-FullPath $Path
    $normalizedParent = Get-FullPath $Parent
    return $normalized.Equals($normalizedParent, [StringComparison]::OrdinalIgnoreCase) -or
        $normalized.StartsWith($normalizedParent + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-File([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing file: $Path" }
}

function Read-SharedText([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader = [IO.StreamReader]::new($stream)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose(); $stream.Dispose() }
}

function Get-CurrentRunLog {
    $content = Read-SharedText $PlayerLogPath
    # Unity normally replaces Player.log; also handle a host which appends instead.
    if ($originalLogContent.Length -gt 0 -and
        $content.StartsWith($originalLogContent, [StringComparison]::Ordinal)) {
        return $content.Substring($originalLogContent.Length)
    }
    return $content
}

function Test-StartupMarkers([string] $Content) {
    $ubStarted = $Content -match '\[SolastaUnfinishedBusiness\]\s+Version .*Loading\.' -and
        $Content -match '\[SolastaUnfinishedBusiness\]\s+Active\.' -and
        $Content -match '\[SolastaUnfinishedBusiness\]\s+enabled in '
    $managerMatch = [regex]::Match($Content, 'SUCCESSFUL LOADED (\d+)/(\d+) MODS')
    return $ubStarted -and $managerMatch.Success -and
        [int] $managerMatch.Groups[1].Value -gt 0 -and
        $managerMatch.Groups[1].Value -eq $managerMatch.Groups[2].Value
}

function Get-RuntimeErrors([string] $Content) {
    $errors = @($Content -split '\r?\n' | Where-Object {
        $_ -match '^\[[^\]]+\].*\[(Error|Exception)\]' -or
        $_ -match '^(?:[\w.]+)?(?:Exception|Error):|^Uploading Crash Report|HarmonyException'
    })
    $managerMatch = [regex]::Match($Content, 'SUCCESSFUL LOADED (\d+)/(\d+) MODS')
    if ($managerMatch.Success -and ([int] $managerMatch.Groups[2].Value -eq 0 -or
        $managerMatch.Groups[1].Value -ne $managerMatch.Groups[2].Value)) {
        $errors += "Unity Mod Manager loaded $($managerMatch.Groups[1].Value)/$($managerMatch.Groups[2].Value) mods."
    }
    return $errors
}

function Write-Json([string] $Path, [object] $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

function Remove-OwnedDirectory([string] $Path, [string] $Parent) {
    $resolved = Get-FullPath $Path
    if (-not (Test-IsWithin $resolved $Parent) -or $resolved -eq (Get-FullPath $Parent)) {
        throw "Refusing to remove a path outside its owned parent: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        $item = Get-Item -LiteralPath $resolved
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing recursive removal of a reparse point: $resolved"
        }
        if (Get-ChildItem -LiteralPath $resolved -Recurse -Force |
                Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }) {
            throw "Refusing recursive removal through a reparse point: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

function Assert-NoGameProcess {
    # A concurrent instance can overwrite the same Player.log and user settings.
    $processState=Get-VerificationProcessState -Name Solasta
    $processDiagnostic=$processState | ConvertTo-Json -Depth 6 -Compress
    if (-not $processState.querySucceeded) { throw ('Cannot verify that Solasta is stopped before runtime verification. Process state: ' + $processDiagnostic) }
    if ($processState.matches.Count) { throw ('Solasta is already running. Close it before runtime verification. Process state: ' + $processDiagnostic) }
}

if (-not $GameExePath -and $env:SolastaInstallDir) {
    $GameExePath = Join-Path $env:SolastaInstallDir 'Solasta.exe'
}
if (-not $GameExePath) { throw 'Specify -GameExePath or SolastaInstallDir.' }
$GameExePath = Get-FullPath $GameExePath
$gameDirectory = Split-Path -Parent $GameExePath
if (Test-IsWithin $gameDirectory (Join-Path $repoRoot 'SolastaFiles')) {
    throw 'The repository reference snapshot is not an installed game.'
}
if (-not $EvidenceRoot) { $EvidenceRoot = Join-Path $repoRoot 'tools\game-test\artifacts\runtime' }
$EvidenceRoot = Get-FullPath $EvidenceRoot
$allowedEvidenceRoot = Join-Path $repoRoot 'tools\game-test\artifacts'
if (-not (Test-IsWithin $EvidenceRoot $allowedEvidenceRoot)) {
    throw "EvidenceRoot must be within $allowedEvidenceRoot."
}
$runDirectory = Join-Path $EvidenceRoot ((Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + $RunName)
if ($DedicatedPlayerLog) {
    if ($PlayerLogPath) { throw 'DedicatedPlayerLog and PlayerLogPath cannot be combined.' }
    # Unity writes only this new run log; the existing user Player.log is never read or copied.
    $PlayerLogPath = Join-Path $runDirectory 'Native-Player.log'
}
if (-not $PlayerLogPath) {
    $PlayerLogPath = Join-Path $env:USERPROFILE 'AppData\LocalLow\Tactical Adventures\Solasta\Player.log'
}
$PlayerLogPath = Get-FullPath $PlayerLogPath
New-Item -ItemType Directory -Path $runDirectory | Out-Null
Write-Host "Verification evidence: $runDirectory"
$workDirectory = Join-Path $runDirectory 'Build'
$modsDirectory = Join-Path $gameDirectory 'Mods'
$probeDirectory = Join-Path $modsDirectory $probeId
$installedUbDirectory = Join-Path $modsDirectory 'SolastaUnfinishedBusiness'
$managedDirectory = Join-Path $gameDirectory 'Solasta_Data\Managed'
$outputDirectory = Join-Path $repoRoot 'tools\CustomBuildLocal\Output\SolastaUnfinishedBusiness'
$settingsPath = Join-Path $installedUbDirectory 'Settings.xml'
$paramsPath = Join-Path $managedDirectory 'UnityModManager\Params.xml'
$nativePreferencesDirectory = Join-Path $env:USERPROFILE 'AppData\LocalLow\Tactical Adventures\Solasta'
$backupDirectory = Join-Path $runDirectory 'SettingsBackup'
$settingsBackups = @()
$nativeDisplayBackup = $null
$probeCreated = $false
$process = $null
$backgroundLease = $null
$expectedDesktopName=$(if ($RequireOtherAppForeground) { 'UBGameTest_' + [guid]::NewGuid().ToString('N') } else { '' })
$processStartTime = $null
$failure = $null
$foregroundUnavailable = $false
$script:lastForegroundPid = $null
$script:lastForegroundWriteUtc = [datetime]::MinValue
$originalLogContent = ''
$result = [ordered]@{
    status = 'incomplete'; reason = 'Verification has not completed.'; runName = $RunName
    evidenceDirectory = $runDirectory; startedUtc = [datetime]::UtcNow.ToString('O')
    validationOnly = [bool] $ValidateOnly; startupOnly = [bool] $StartupOnly; compilationSucceeded = $false
    dedicatedPlayerLog = [bool] $DedicatedPlayerLog
    showGameWindow = [bool] $ShowGameWindow; runInBackground = [bool] $RunInBackground
    requireOtherAppForeground = [bool] $RequireOtherAppForeground
    requireNativeActionProof = [bool] $RequireNativeActionProof
    batchModeRequested = $false
    desktop = @{name=$expectedDesktopName;before=$null;after=$null;receipt=$null;history='Runtime-Desktop.jsonl';released=$false}
    foreground = @{ before = $null; after = $null; history = 'Runtime-Foreground.jsonl'; primaryThreadId = $null; released = $false }
    probes = $ProbeType; sources = @(); hashes = @{}; assemblyVersions = @{}; processId = $null
    settingsRestored = $false; displaySettingsRestored = $false; probeRemoved = $false; passed = 0; failed = 0; incomplete = 0
}

function Save-OwnedProcessCleanupEvidence {
    try { Write-Json (Join-Path $runDirectory 'Runtime-ProcessCleanup.json') $result.processCleanup }
    catch {
        $result.processCleanup.observationErrors+= $_.Exception.Message
        $result.status='failed'
        $message=' Process cleanup evidence failed: ' + $_.Exception.Message
        if (-not $result.reason.Contains($message)) { $result.reason+= $message }
    }
}

function Save-OwnedProcessObservation([string] $Phase) {
    $sample=[ordered]@{utc=[datetime]::UtcNow.ToString('O');phase=$Phase;processId=$null;startTimeUtc=$null;path=$null;hasExited=$null;errors=@{}}
    try { $sample.processId=$process.Id } catch { $sample.errors['processId']=$_.Exception.Message }
    try { $process.Refresh(); $sample.hasExited=$process.HasExited } catch { $sample.errors['hasExited']=$_.Exception.Message }
    try { $sample.startTimeUtc=$process.StartTime.ToUniversalTime().ToString('O') } catch { $sample.errors['startTimeUtc']=$_.Exception.Message }
    if ($sample.hasExited -is [bool] -and -not $sample.hasExited) {
        try { $sample.path=$process.MainModule.FileName } catch { $sample.errors['path']=$_.Exception.Message }
    } else { $sample.errors['path']='Not queried after exit or when exit state is unknown.' }
    $result.processCleanup.observations+= $sample
    Save-OwnedProcessCleanupEvidence
}

function Wait-OwnedProcessExit([string] $Kind, [int] $TimeoutMilliseconds) {
    $wait=[ordered]@{kind=$Kind;startedUtc=[datetime]::UtcNow.ToString('O');timeoutMilliseconds=$TimeoutMilliseconds;result=$null;error=$null;elapsedMilliseconds=$null}
    $result.processCleanup.waits+= $wait
    Save-OwnedProcessCleanupEvidence
    $timer=[Diagnostics.Stopwatch]::StartNew()
    try { $wait.result=$process.WaitForExit($TimeoutMilliseconds); return $wait.result }
    catch { $wait.error=$_.Exception.Message; throw }
    finally {
        $timer.Stop(); $wait.elapsedMilliseconds=$timer.Elapsed.TotalMilliseconds
        Save-OwnedProcessCleanupEvidence
    }
}

function Save-ForegroundObservation([string] $Phase, [switch] $Force) {
    try { $sample=Get-VerificationForeground }
    catch { $script:foregroundUnavailable=$true; throw }
    $now=[datetime]::UtcNow
    $row=[ordered]@{utc=$sample.capturedUtc;observationSource='host-runner';phase=$Phase;
        processId=$sample.processId;nativeThreadId=$sample.nativeThreadId;windowHandle=$sample.windowHandle}
    if ($Force -or $sample.processId -ne $script:lastForegroundPid -or $now -ge $script:lastForegroundWriteUtc.AddSeconds(1)) {
        $row['desktop']=Get-VerificationDesktop
        [IO.File]::AppendAllText((Join-Path $runDirectory 'Runtime-Foreground.jsonl'), ($row | ConvertTo-Json -Depth 8 -Compress) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        $script:lastForegroundWriteUtc=$now
        if (-not $row.desktop.complete -or $row.desktop.runnerReceivesInput -ne $true -or
            $row.desktop.runnerDesktop -ne $row.desktop.inputDesktop) {
            $script:foregroundUnavailable=$true
            throw 'The host input desktop could not be verified as active.'
        }
    }
    $script:lastForegroundPid=$sample.processId
    if ($sample.processId -eq 0) { $script:foregroundUnavailable=$true; throw 'The foreground application could not be identified; background verification stopped.' }
    if ($null -ne $process -and $sample.processId -eq $process.Id) { $script:foregroundUnavailable=$true; throw 'The game became the foreground application; background verification stopped.' }
    return $row
}

try {
    if ($RequireOtherAppForeground -and -not $ValidateOnly) {
        try {
            . (Join-Path $PSScriptRoot 'WindowsDesktop.ps1')
            $result.desktop.before=Get-VerificationDesktop
            [void](Get-Command Start-VerificationBackgroundProcess -ErrorAction Stop)
            $result.foreground.before=Save-ForegroundObservation 'before-launch' -Force
        } catch { $foregroundUnavailable = $true; throw }
    }
    Assert-File $GameExePath
    Assert-File (Join-Path $gameDirectory 'winhttp.dll')
    Assert-File (Join-Path $gameDirectory 'doorstop_config.ini')
    Assert-File $paramsPath
    $sourceInfoPath = Join-Path $repoRoot 'SolastaUnfinishedBusiness\Info.json'
    Assert-File $sourceInfoPath
    $sourceInfo = [IO.File]::ReadAllText($sourceInfoPath) | ConvertFrom-Json
    $minimumManagerVersion = [version] $sourceInfo.ManagerVersion
    $result['minimumManagerVersion'] = $minimumManagerVersion.ToString()
    foreach ($assemblyName in @('UnityModManager', '0Harmony', 'dnlib')) {
        $assemblyPath = Join-Path $managedDirectory "UnityModManager\$assemblyName.dll"
        Assert-File $assemblyPath
        $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($assemblyPath).Version
        $result.assemblyVersions[$assemblyName] = $assemblyVersion.ToString()
        $result.hashes[$assemblyName] = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash
    }
    $managerVersion = [version] $result.assemblyVersions['UnityModManager']
    Write-Host "Unity Mod Manager runtime: $managerVersion (minimum $minimumManagerVersion)"
    if ($managerVersion -lt $minimumManagerVersion) {
        throw "Unity Mod Manager $minimumManagerVersion or newer is required; installed runtime is $managerVersion."
    }
    if (-not $ValidateOnly) {
        Assert-NoGameProcess
        if (Test-Path -LiteralPath $probeDirectory) {
            throw "A verification mod already exists. Inspect it before proceeding: $probeDirectory"
        }
    }
    if (-not $StartupOnly) {
        if ($ProbeSource.Count -eq 0 -or $ProbeType.Count -eq 0) { throw 'At least one probe is required.' }
        $sources = @($ProbeSource | ForEach-Object {
            $source = (Resolve-Path -LiteralPath $_).Path
            Assert-File $source
            if ([IO.Path]::GetExtension($source) -ne '.cs') { throw "Not a C# probe source: $source" }
            $source
        })
        $sourceArchiveDirectory = Join-Path $runDirectory 'InputSources'
        New-Item -ItemType Directory -Path $sourceArchiveDirectory | Out-Null
        $archivedSources = @()
        $sourceIndex = 0
        $result.sources = @($sources | ForEach-Object {
            $source = $_
            $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
            $sourceIndex++
            $archivePath = Join-Path $sourceArchiveDirectory (
                '{0:D3}-{1}' -f $sourceIndex, [IO.Path]::GetFileName($source))
            Copy-Item -LiteralPath $source -Destination $archivePath
            $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
            if ($archiveHash -ne $sourceHash -or
                (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $sourceHash) {
                throw "Probe source changed while being archived: $source"
            }
            $archivedSources += $archivePath
            @{ path = $source; sha256 = $sourceHash; archivePath = $archivePath }
        })
        $sources = $archivedSources
        $result['runnerSources']=@('RuntimeVerificationHarness.cs','WindowsDesktop.cs','WindowsDesktop.ps1','Invoke-RuntimeVerification.ps1','NativeDisplaySettings.ps1' | ForEach-Object {
            $source=Join-Path $PSScriptRoot $_
            $sourceHash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
            $archivePath=Join-Path $sourceArchiveDirectory ('runtime-' + $_)
            Copy-Item -LiteralPath $source -Destination $archivePath
            if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $sourceHash -or
                (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $sourceHash) {
                throw "Runner source changed while being archived: $source"
            }
            @{path=$source;sha256=$sourceHash;archivePath=$archivePath}
        })
    }
    if (-not $SkipBuild -and -not $ValidateOnly) {
        & (Join-Path $repoRoot 'tools\CustomBuildLocal\BuildCustomLocalPackage.cmd') *> (Join-Path $runDirectory 'Build.log')
        if ($LASTEXITCODE -ne 0) { throw "Package build failed with exit code $LASTEXITCODE." }
    }
    Assert-File (Join-Path $outputDirectory 'SolastaUnfinishedBusiness.dll')
    Assert-File (Join-Path $outputDirectory 'Info.json')
    $result.hashes['builtUb'] = (Get-FileHash (Join-Path $outputDirectory 'SolastaUnfinishedBusiness.dll') -Algorithm SHA256).Hash
    if (-not $ExpectedUbSha256) { $ExpectedUbSha256=$result.hashes['builtUb'] }
    if ($ExpectedUbSha256 -ne $result.hashes['builtUb']) { throw 'ExpectedUbSha256 does not match the formal build output.' }
    $result['expectedUb']=[ordered]@{sha256=$ExpectedUbSha256;location=(Join-Path $installedUbDirectory 'SolastaUnfinishedBusiness.dll');moduleVersionId=$null}
    [void] [Reflection.Assembly]::LoadFrom((Join-Path $repoRoot 'lib\Mono.Cecil.dll'))
    $ubMetadata=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $outputDirectory 'SolastaUnfinishedBusiness.dll'))
    try { $result.expectedUb.moduleVersionId=$ubMetadata.MainModule.Mvid.ToString('D') }
    finally { $ubMetadata.Dispose() }
    $result.hashes['gameAssembly'] = (Get-FileHash (Join-Path $managedDirectory 'Assembly-CSharp.dll') -Algorithm SHA256).Hash

    if (-not $StartupOnly) {
        Assert-File (Join-Path $repoRoot 'lib\Assembly-CSharp_public.dll')
        $result.hashes['publicizedGameAssembly'] = (Get-FileHash (Join-Path $repoRoot 'lib\Assembly-CSharp_public.dll') -Algorithm SHA256).Hash
        # Resolve an installed compiler instead of pinning a developer's SDK patch version.
        $sdks = @(& dotnet --list-sdks | ForEach-Object {
            if ($_ -match '^(\d+\.\d+\.\d+)[^ ]* \[(.+)\]$') {
                $sdkVersion = [version] $Matches[1]
                $sdkFolder = ($_ -split ' ')[0]
                if ($sdkVersion.Major -ge 9) {
                    [pscustomobject]@{ Version = $sdkVersion; Path = Join-Path $Matches[2] "$sdkFolder\Roslyn\bincore\csc.dll" }
                }
            }
        } | Sort-Object Version -Descending)
        if ($sdks.Count -eq 0) { throw 'An installed .NET SDK 9 or newer is required.' }
        $compiler = $sdks[0].Path
        Assert-File $compiler
        $result['compiler'] = $compiler
        New-Item -ItemType Directory -Path $workDirectory | Out-Null
        $configurationPath = Join-Path $workDirectory 'RuntimeVerificationConfiguration.cs'
        $configuration = @"
    internal static class RuntimeVerificationConfiguration
    {
        internal const string EvidenceDirectory = $($runDirectory | ConvertTo-Json -Compress);
        internal const int TimeoutSeconds = $TimeoutSeconds;
        internal static readonly bool RunInBackground = $($RunInBackground.ToString().ToLowerInvariant());
        internal static readonly bool RequireOtherAppForeground = $($RequireOtherAppForeground.ToString().ToLowerInvariant());
        internal const string ExpectedDesktopName = $($expectedDesktopName | ConvertTo-Json -Compress);
        internal const string ExpectedUbLocation = $($result.expectedUb.location | ConvertTo-Json -Compress);
        internal const string ExpectedUbSha256 = $($ExpectedUbSha256 | ConvertTo-Json -Compress);
        internal const string ExpectedUbModuleVersionId = $($result.expectedUb.moduleVersionId | ConvertTo-Json -Compress);
        internal const string ExpectedManagerSha256 = $($result.hashes['UnityModManager'] | ConvertTo-Json -Compress);
        internal static readonly string[] ProbeTypes = { $(($ProbeType | ForEach-Object { $_ | ConvertTo-Json -Compress }) -join ', ') };
    }
"@
        [IO.File]::WriteAllText($configurationPath, $configuration, [Text.UTF8Encoding]::new($false))
        $frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
        $references = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Runtime.Serialization.dll' |
            ForEach-Object { Join-Path $frameworkDirectory $_ })
        $references += Join-Path $repoRoot 'lib\Assembly-CSharp_public.dll'
        $references += Join-Path $outputDirectory 'SolastaUnfinishedBusiness.dll'
        $references += Join-Path $managedDirectory 'UnityModManager\UnityModManager.dll'
        $references += @(Get-ChildItem -LiteralPath $managedDirectory -Filter 'Unity*.dll' | Select-Object -ExpandProperty FullName)
        $references += @($ReferencePath | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
        $references = @($references | Sort-Object -Unique)
        foreach ($reference in $references) { Assert-File $reference }
        $probeDll = Join-Path $workDirectory "$probeId.dll"
        $arguments = @('/nologo', '/target:library', '/nostdlib', '/langversion:latest', "/out:$probeDll")
        $arguments += @($references | ForEach-Object { "/reference:$_" })
        $arguments += Join-Path $sourceArchiveDirectory 'runtime-RuntimeVerificationHarness.cs'
        $arguments += Join-Path $sourceArchiveDirectory 'runtime-WindowsDesktop.cs'
        $arguments += $configurationPath
        $arguments += $sources
        foreach ($source in @($result.sources) + @($result.runnerSources)) {
            if ((Get-FileHash -LiteralPath $source.archivePath -Algorithm SHA256).Hash -ne $source.sha256) {
                throw "Archived probe source changed before compilation: $($source.archivePath)"
            }
        }
        & dotnet $compiler @arguments *> (Join-Path $runDirectory 'Compile.log')
        if ($LASTEXITCODE -ne 0) { throw "Probe compilation failed with exit code $LASTEXITCODE. See Compile.log." }
        # Validate the public contract without loading Unity/game assemblies into PowerShell.
        $metadata = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($probeDll)
        try {
            foreach ($typeName in $ProbeType) {
                $probeMetadata = $metadata.MainModule.GetType($typeName)
                if ($null -eq $probeMetadata) { throw "Probe type was not compiled: $typeName" }
                $entryMethods = @($probeMetadata.Methods | Where-Object {
                    $_.Name -eq 'Run' -and $_.IsStatic -and $_.Parameters.Count -eq 1 -and
                    $_.Parameters[0].ParameterType.FullName -eq 'System.Action`1<System.String>' -and
                    $_.ReturnType.FullName -in @('System.Void', 'System.Collections.IEnumerator')
                })
                if ($entryMethods.Count -ne 1) {
                    throw "Probe $typeName must define static void/IEnumerator Run(Action<string> log)."
                }
            }
        }
        finally { $metadata.Dispose() }
        $result.compilationSucceeded = $true
        $result.hashes['probe'] = (Get-FileHash -LiteralPath $probeDll -Algorithm SHA256).Hash
    }
    if ($ValidateOnly) {
        $result.reason = 'Probe compiled. Runtime checks were not requested (-ValidateOnly).'
    }
    else {
        Assert-NoGameProcess
        New-Item -ItemType Directory -Path $backupDirectory | Out-Null
        $nativeDisplayBackup=Get-NativeDisplaySettingsSnapshot
        Assert-NativeDisplaySettingsSnapshot $nativeDisplayBackup
        Write-Json (Join-Path $backupDirectory 'Native-DisplaySettings.json') $nativeDisplayBackup
        foreach ($entry in @(
            @{ Path = $settingsPath; Name = 'UB-Settings.xml' },
            @{ Path = $paramsPath; Name = 'UMM-Params.xml' },
            @{ Path = (Join-Path $nativePreferencesDirectory 'UserPreferences.xml'); Name = 'Native-UserPreferences.xml' },
            @{ Path = (Join-Path $nativePreferencesDirectory 'PlatformPreferences.xml'); Name = 'Native-PlatformPreferences.xml' }
        )) {
            $exists = Test-Path -LiteralPath $entry.Path
            $backup = @{ Path = $entry.Path; Exists = $exists; Bytes = $null; LastWriteTimeUtc = $null }
            if ($exists) {
                $backup.LastWriteTimeUtc = [IO.File]::GetLastWriteTimeUtc($entry.Path)
                $backup.Bytes = [IO.File]::ReadAllBytes($entry.Path)
                [IO.File]::WriteAllBytes((Join-Path $backupDirectory $entry.Name), $backup.Bytes)
            }
            $settingsBackups += $backup
        }
        $result['settingsBackups'] = @($settingsBackups | ForEach-Object {
            @{ path = $_.Path; exists = $_.Exists; lastWriteUtc = $_.LastWriteTimeUtc
                sha256 = $(if ($_.Exists) { (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash } else { $null }) }
        })
        if (Test-Path -LiteralPath $PlayerLogPath) {
            $originalLogContent = Read-SharedText $PlayerLogPath
            Copy-Item -LiteralPath $PlayerLogPath -Destination (Join-Path $runDirectory 'Original-Player.log')
        }
        if ($Sync) {
            & (Join-Path $repoRoot 'tools\runtime-verification\Invoke-SolastaSemiAutoTest.ps1') -SkipBuild -Sync -GameExePath $GameExePath *> (Join-Path $runDirectory 'Sync.log')
        }
        Assert-File (Join-Path $installedUbDirectory 'SolastaUnfinishedBusiness.dll')
        $result.hashes['installedUb'] = (Get-FileHash (Join-Path $installedUbDirectory 'SolastaUnfinishedBusiness.dll') -Algorithm SHA256).Hash
        if ($result.hashes['builtUb'] -ne $result.hashes['installedUb']) {
            throw 'The installed UB DLL differs from the build output. Use -Sync after reviewing the build.'
        }
        [xml] $ummParams = [IO.File]::ReadAllText($paramsPath)
        $ummParams.Param.ShowOnStart = '0'
        $ummParams.Save($paramsPath)
        if (-not $StartupOnly) {
            New-Item -ItemType Directory -Path $probeDirectory | Out-Null
            $probeCreated = $true
            Copy-Item -LiteralPath $probeDll -Destination $probeDirectory
            Write-Json (Join-Path $probeDirectory 'Info.json') @{
                Id = $probeId; DisplayName = 'Temporary UB Runtime Verification'; Version = '1.0.0'
                ManagerVersion = $sourceInfo.ManagerVersion; AssemblyName = "$probeId.dll"
                EntryMethod = 'RuntimeVerificationHarness.Load'; Requirements = @('SolastaUnfinishedBusiness')
            }
        }
        Assert-NoGameProcess
        $launchParameters = @{
            FilePath = $GameExePath; WorkingDirectory = $gameDirectory
            WindowStyle = $(if ($ShowGameWindow) { 'Normal' } else { 'Hidden' }); PassThru = $true
        }
        if ($DedicatedPlayerLog) {
            $launchParameters.ArgumentList = @('-logFile', ('"{0}"' -f $PlayerLogPath))
        }
        if ($RequireOtherAppForeground) {
            try {
                $backgroundArguments=@()
                if ($DedicatedPlayerLog) { $backgroundArguments+=@('-logFile',$PlayerLogPath) }
                $backgroundLease=Start-VerificationBackgroundProcess -FilePath $GameExePath -WorkingDirectory $gameDirectory -ArgumentList $backgroundArguments -DesktopName $expectedDesktopName
                $process=$backgroundLease.Process
                $result.foreground.primaryThreadId=$backgroundLease.PrimaryThreadId
            } catch { $foregroundUnavailable=$true; throw }
        } else { $process = Start-Process @launchParameters }
        $processStartTime = $process.StartTime
        $result.processId = $process.Id
        $result['processStartedUtc']=$processStartTime.ToUniversalTime().ToString('O')
        if ($RequireOtherAppForeground) { [void](Save-ForegroundObservation 'launched' -Force) }
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        $harnessResultPath = Join-Path $runDirectory 'HarnessResult.json'
        $startupStableSince = $null
        $completed = $false
        while ((Get-Date) -lt $deadline) {
            $process.Refresh()
            if ($process.HasExited) {
                $exitObservation=[ordered]@{utc=[datetime]::UtcNow.ToString('O');processId=$process.Id;hasExited=$true;exitSignalObserved=$null;exitCode=$null;errors=@{}}
                try { $exitObservation.exitSignalObserved=$process.WaitForExit(0) }
                catch { $exitObservation.errors['exitSignalObserved']=$_.Exception.Message }
                try { $exitObservation.exitCode=$process.ExitCode }
                catch { $exitObservation.errors['exitCode']=$_.Exception.Message }
                $result['earlyExitObservation']=$exitObservation
                throw "Game exited before verification completed (exit code $($exitObservation.exitCode))."
            }
            if ($RequireOtherAppForeground) { [void](Save-ForegroundObservation 'running') }
            if ($StartupOnly) {
                $currentLog = Get-CurrentRunLog
                if (@(Get-RuntimeErrors $currentLog).Count -gt 0) {
                    throw 'An error was reported during startup; see Runtime-Player.log.'
                }
                if (Test-StartupMarkers $currentLog) {
                    if ($null -eq $startupStableSince) { $startupStableSince = Get-Date }
                    elseif ((Get-Date) -ge $startupStableSince.AddSeconds(5)) {
                        $result.status = 'success'
                        $result.passed = 1
                        $result.reason = 'Normal startup remained stable for 5 seconds; gameplay behavior was not tested.'
                        $completed = $true
                        break
                    }
                }
                else { $startupStableSince = $null }
            }
            elseif (Test-Path -LiteralPath $harnessResultPath) {
                # Keep the game alive briefly to catch deferred UI callbacks and late errors.
                for ($settleIndex=0; $settleIndex -lt 4; $settleIndex++) {
                    Start-Sleep -Milliseconds 500
                    if ($RequireOtherAppForeground) { [void](Save-ForegroundObservation 'settling') }
                }
                # Unity may update the result after a deferred error. Retry a locked
                # or partially written result within the existing overall deadline.
                try {
                    $harnessResult = Read-SharedText $harnessResultPath | ConvertFrom-Json
                }
                catch [IO.IOException] { continue }
                catch [ArgumentException] { continue }
                if ($null -eq $harnessResult -or
                    @('status', 'passed', 'failed', 'incomplete' | Where-Object {
                        $_ -notin $harnessResult.PSObject.Properties.Name
                    }).Count -gt 0) { continue }
                $result.status = $harnessResult.status
                $result.passed = $harnessResult.passed
                $result.failed = $harnessResult.failed
                $result.incomplete = $harnessResult.incomplete
                $result.reason = 'Native verification harness completed; see Probe.log.'
                $completed = $true
                break
            }
            Start-Sleep -Milliseconds 500
        }
        if (-not $completed) {
            $result.reason = "Timed out after $TimeoutSeconds seconds; runtime verification is incomplete."
        }
    }
}
catch {
    $failure = $_
    $result.status = $(if ($foregroundUnavailable) { 'incomplete' } else { 'failed' })
    $result.reason = $_.Exception.Message
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $runDirectory 'Runner-error.log') -Encoding UTF8
}
finally {
    $gameStopped = $true
    if ($null -ne $process) {
        $result['processCleanup']=[ordered]@{expectedProcessId=$result.processId;expectedStartTimeUtc=$(if($null -ne $processStartTime){$processStartTime.ToUniversalTime().ToString('O')}else{$null});expectedPath=$GameExePath;observations=@();waits=@();observationErrors=@();alreadyExited=$false;closeMainWindowRequested=$false;closeMainWindowResult=$null;forcedTerminationRequested=$false;completed=$false;error=$null}
        if ($RequireOtherAppForeground) {
            try { [void](Save-ForegroundObservation 'before-exit' -Force) }
            catch {
                if ($result.status -ne 'failed') { $result.status='incomplete' }
                if (-not $result.reason.Contains($_.Exception.Message)) { $result.reason+=' ' + $_.Exception.Message }
            }
        }
        try {
            Save-OwnedProcessObservation 'cleanup-started'
            $process.Refresh()
            if (-not $process.HasExited) {
                if ($process.StartTime -ne $processStartTime -or
                    (Get-FullPath $process.MainModule.FileName) -ne $GameExePath) {
                    throw 'The launched process identity changed. Refusing to stop another process.'
                }
                $result.processCleanup.closeMainWindowRequested=$true
                $result.processCleanup.closeMainWindowResult=$process.CloseMainWindow()
                Save-OwnedProcessObservation 'close-requested'
                if (-not (Wait-OwnedProcessExit 'graceful' 5000)) {
                    $result.processCleanup.forcedTerminationRequested=$true
                    Stop-Process -InputObject $process -Force
                    if (-not (Wait-OwnedProcessExit 'after-force' 5000)) { throw 'The launched game did not exit.' }
                }
            } else {
                $result.processCleanup.alreadyExited=$true
                if (-not (Wait-OwnedProcessExit 'already-exited' 5000)) { throw 'The exited game process did not signal termination.' }
            }
            $result.processCleanup.completed=$true
            Save-OwnedProcessObservation 'cleanup-completed'
        }
        catch {
            $gameStopped = $false
            $result.status = 'failed'
            $result.processCleanup.error=$_.Exception.Message
            Save-OwnedProcessObservation 'cleanup-failed'
            $result.reason += " Process cleanup failed: $($_.Exception.Message)"
        }
        try {
            $runtimeLog = Read-SharedText $PlayerLogPath
            [IO.File]::WriteAllText((Join-Path $runDirectory 'Runtime-Player.log'), $runtimeLog)
            $currentRunLog = Get-CurrentRunLog
            $errors = @(Get-RuntimeErrors $currentRunLog)
            if ($errors.Count -gt 0) {
                $result.status = 'failed'
                $result.reason += ' Runtime errors detected; see Runtime-errors.log.'
                $errors | Set-Content (Join-Path $runDirectory 'Runtime-errors.log') -Encoding UTF8
            }
            $startupSucceeded = Test-StartupMarkers $currentRunLog
            $result['startupSucceeded'] = [bool] $startupSucceeded
            if ($result.status -eq 'success' -and -not $startupSucceeded) {
                $result.status = 'failed'
                $result.reason += ' Required UMM/UB startup markers were missing.'
            }
        }
        catch {
            $result.status = 'failed'
            $result.reason += " Runtime evidence capture failed: $($_.Exception.Message)"
        }
    }
    if ($RequireOtherAppForeground -and $null -ne $process -and $gameStopped) {
        try {
            $result.foreground.after=Save-ForegroundObservation 'after-exit' -Force
            $result.desktop.after=Get-VerificationDesktop
            if ($result.desktop.before.inputDesktop -ne $result.desktop.after.inputDesktop -or $result.desktop.before.runnerDesktop -ne $result.desktop.after.runnerDesktop) { throw 'The parent input or runner desktop changed during verification.' }
            $receiptPath=Join-Path $runDirectory 'Runtime-Desktop.json'
            if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw 'The game did not provide a main-thread desktop receipt.' }
            $receipt=Read-SharedText $receiptPath | ConvertFrom-Json
            $result.desktop.receipt=$receipt
            if ($receipt.capturedUtc -is [datetime]) { $receiptUtc=$receipt.capturedUtc.ToUniversalTime() }
            else { $receiptUtc=[datetime]::Parse($receipt.capturedUtc,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime() }
            $desktopChecks=[ordered]@{
                observationSource=($receipt.observationSource -eq 'game-main-thread')
                acquisitionComplete=($receipt.complete -is [bool] -and $receipt.complete)
                processId=($receipt.processId -eq $result.processId)
                mainThread=($receipt.nativeThreadId -eq $result.foreground.primaryThreadId)
                expectedDesktop=($receipt.expectedDesktop -eq $expectedDesktopName)
                actualDesktop=($receipt.runnerDesktop -eq $expectedDesktopName)
                windowStation=($receipt.windowStation -eq $result.desktop.before.windowStation)
                inputDesktopKnown=($receipt.inputDesktopQuerySucceeded -is [bool] -and $receipt.inputDesktopQuerySucceeded -and
                    $receipt.inputDesktop -eq $result.desktop.before.inputDesktop)
                inputQuerySucceeded=($receipt.runnerInputQuery.succeeded -is [bool] -and $receipt.runnerInputQuery.succeeded -and
                    $receipt.runnerInputQuery.errorCode -eq 0 -and $receipt.runnerInputQuery.bytesNeeded -eq 4)
                inputQueryValue=($receipt.runnerInputQuery.rawValue -eq 0 -and $receipt.runnerInputQuery.value -is [bool] -and
                    -not $receipt.runnerInputQuery.value)
                inactiveInput=($receipt.runnerReceivesInput -is [bool] -and -not $receipt.runnerReceivesInput)
                isolated=($receipt.isolated -is [bool] -and $receipt.isolated)
                capturedAfterLaunch=($receiptUtc -ge $processStartTime.ToUniversalTime())
                capturedBeforeCheck=($receiptUtc -le [datetime]::UtcNow)
            }
            $result.desktop['checks']=$desktopChecks
            $mismatches=@($desktopChecks.Keys | Where-Object { -not $desktopChecks[$_] })
            if ($mismatches.Count) { throw ('The main-thread desktop receipt does not match the launch: ' + ($mismatches -join ', ')) }
            $desktopSamples=@(Get-Content -LiteralPath (Join-Path $runDirectory 'Runtime-Desktop.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
            $unverifiedSamples=@($desktopSamples | Where-Object {
                $_.observationSource -ne 'game-main-thread' -or $_.processId -ne $result.processId -or
                $_.nativeThreadId -ne $result.foreground.primaryThreadId -or $_.complete -ne $true -or
                $_.runnerInputQuery.succeeded -ne $true -or $_.runnerInputQuery.bytesNeeded -ne 4 -or
                $_.runnerInputQuery.rawValue -ne 0 -or $_.runnerReceivesInput -ne $false -or
                $_.runnerDesktop -ne $expectedDesktopName -or $_.inputDesktopQuerySucceeded -ne $true -or
                $_.inputDesktop -ne $result.desktop.before.inputDesktop -or $_.isolated -ne $true
            })
            $result.desktop['samples']=$desktopSamples.Count
            $result.desktop['unverifiedSamples']=$unverifiedSamples.Count
            if ($desktopSamples.Count -eq 0 -or $unverifiedSamples.Count -gt 0) { throw 'The game desktop history contains missing or unsuccessful acquisition.' }
        }
        catch {
            if ($result.status -ne 'failed') { $result.status='incomplete' }
            if (-not $result.reason.Contains($_.Exception.Message)) { $result.reason+=' Foreground observation failed: ' + $_.Exception.Message }
        }
    }
    if ($null -ne $backgroundLease -and $gameStopped) {
        try { $backgroundLease.Dispose(); $result.foreground.released=$true; $result.desktop.released=$true }
        catch { $result.status='failed'; $result.reason+=" Process handle cleanup failed: $($_.Exception.Message)" }
    }
    if (-not $StartupOnly -and -not $ValidateOnly -and $null -ne $process) {
        try {
            $assemblyReceipt=Read-SharedText (Join-Path $runDirectory 'Runtime-Assembly.json') | ConvertFrom-Json
            $result['loadedUb']=$assemblyReceipt
            $loaded=@($assemblyReceipt.assemblies)
            if ($assemblyReceipt.observationSource -ne 'game-main-thread' -or $assemblyReceipt.processId -ne $result.processId -or
                $loaded.Count -ne 1 -or $assemblyReceipt.matches -ne $true -or
                $assemblyReceipt.originLocation -ne $result.expectedUb.location -or $assemblyReceipt.originSha256 -ne $ExpectedUbSha256 -or
                $assemblyReceipt.modEntryAssemblyMatches -ne $true -or $assemblyReceipt.managerSha256 -ne $result.hashes['UnityModManager'] -or
                $loaded[0].location -notin @($result.expectedUb.location,$assemblyReceipt.expectedCacheLocation) -or
                $loaded[0].sha256 -ne $ExpectedUbSha256 -or
                $loaded[0].moduleVersionId -ne $result.expectedUb.moduleVersionId -or
                [string]::IsNullOrWhiteSpace($loaded[0].fullName)) { throw 'The loaded UB assembly receipt does not match the expected build.' }
            if ($RequireOtherAppForeground -and $assemblyReceipt.nativeThreadId -ne $result.foreground.primaryThreadId) { throw 'The loaded UB assembly receipt is not from the game main thread.' }
        } catch {
            if ($result.status -ne 'failed') { $result.status='incomplete' }
            $result.reason+=' Loaded assembly observation failed: ' + $_.Exception.Message
        }
    }
    if ($RequireNativeActionProof -and $result.status -eq 'success') {
        try {
            $samples=@(Get-Content -LiteralPath (Join-Path $runDirectory 'Runtime-Execution.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
            $proofCase=Read-SharedText ([Environment]::GetEnvironmentVariable('UB_GAME_TEST_CASE_PATH','Process')) | ConvertFrom-Json
            $verifyFocusLoss=$false
            if ('verifyFocusLoss' -in $proofCase.operation.PSObject.Properties.Name) {
                if ($proofCase.operation.verifyFocusLoss -isnot [bool]) { throw 'verifyFocusLoss must be a Boolean.' }
                $verifyFocusLoss=$proofCase.operation.verifyFocusLoss
            }
            $action=@($samples | Where-Object { $_.processId -eq $result.processId -and $_.eventName -eq 'operation' -and $_.phase -eq 'action' })
            $end=@($samples | Where-Object { $_.processId -eq $result.processId -and $_.eventName -eq 'operation' -and $_.phase -eq 'action-completed' })
            if ($action.Count -ne 1 -or $end.Count -ne 1) { throw 'The native action interval has no unique main-thread observations.' }
            $actionIndex=[Array]::IndexOf($samples,$action[0])
            $endIndex=[Array]::IndexOf($samples,$end[0])
            if ($endIndex -le $actionIndex) { throw 'The native action observations are out of order.' }
            $interval=@($samples[$actionIndex..$endIndex] | Where-Object { $_.processId -eq $result.processId })
            $actionUtc=[datetime]$action[0].utc
            $endUtc=[datetime]$end[0].utc
            if ($action[0].screenWidth -le 0 -or $action[0].screenHeight -le 0 -or
                $end[0].screenWidth -ne $action[0].screenWidth -or $end[0].screenHeight -ne $action[0].screenHeight) {
                throw 'The native action has no stable, positive main-thread Screen dimensions.'
            }
            $actionDesktops=@($desktopSamples | Where-Object {
                ([datetime]$_.capturedUtc) -ge $actionUtc -and ([datetime]$_.capturedUtc) -le $endUtc.AddMilliseconds(250)
            })
            $unmapped=@($actionDesktops | Where-Object {
                @($_.ownedWindows | Where-Object { $_.processId -eq $result.processId -and $_.windowHandle -ne 0 -and
                    $_.nativeThreadId -eq $result.foreground.primaryThreadId -and $_.desktopQuerySucceeded -eq $true -and
                    $_.desktop -eq $expectedDesktopName -and $_.inputQuery.succeeded -eq $true -and
                    $_.inputQuery.rawValue -eq 0 -and $_.visible -eq $true -and $_.clientRectQuerySucceeded -eq $true -and
                    $_.clientWidth -eq $action[0].screenWidth -and $_.clientHeight -eq $action[0].screenHeight }).Count -ne 1
            })
            $proof=[ordered]@{
                actionObserved=$true; startFrame=$action[0].frame; endFrame=$end[0].frame
                framesAdvanced=($end[0].frame -gt $action[0].frame)
                remainedUnfocused=(@($interval | Where-Object { $_.focused -isnot [bool] -or $_.focused }).Count -eq 0)
                backgroundEnabled=($action[0].runInBackground -and $end[0].runInBackground)
                batchMode=($action[0].batchMode -is [bool] -and $action[0].batchMode -and $end[0].batchMode -is [bool] -and $end[0].batchMode)
                graphicsDeviceAvailable=($action[0].graphicsDeviceAvailable -is [bool] -and $action[0].graphicsDeviceAvailable -and $end[0].graphicsDeviceAvailable -is [bool] -and $end[0].graphicsDeviceAvailable)
                graphicsDeviceType=$action[0].graphicsDeviceType; graphicsDeviceName=$action[0].graphicsDeviceName
                samples=$interval.Count; hostInputUsed=$false; uiAcceptanceVerified=$false
                desktopSamples=$actionDesktops.Count; ownedRenderWindowsMapped=($actionDesktops.Count -gt 0 -and $unmapped.Count -eq 0)
                renderWidth=$action[0].screenWidth; renderHeight=$action[0].screenHeight
                unityFocusLossRequested=$verifyFocusLoss; unityFocusTransitionVerified=$false
            }
            if ($verifyFocusLoss -and $proof.remainedUnfocused -and $proof.framesAdvanced) {
                $proof.unityFocusTransitionVerified=(@($samples | Where-Object { $_.processId -eq $result.processId -and $_.frame -lt $action[0].frame -and $_.focused -is [bool] -and $_.focused }).Count -gt 0)
            }
            $result['backgroundActionProof']=$proof
            if (-not $proof.framesAdvanced -or -not $proof.backgroundEnabled) { throw 'The native action did not demonstrate background frame progression.' }
            if (-not $proof.graphicsDeviceAvailable) { throw 'The native action has no active graphics device.' }
            if (-not $proof.ownedRenderWindowsMapped) { throw 'The native action has no verified game PID/window/thread/desktop correspondence.' }
            if ($verifyFocusLoss -and -not $proof.remainedUnfocused) { throw 'The requested native action did not remain unfocused.' }
            if ($verifyFocusLoss -and -not $proof.unityFocusTransitionVerified) { throw 'The requested native action did not demonstrate an actual Unity focus transition.' }
        } catch { $result.status='incomplete'; $result.reason+=' Background proof unavailable: ' + $_.Exception.Message }
    }
    if ($gameStopped) {
        $settingsCleanupErrors=@()
        foreach ($backup in $settingsBackups) {
            try {
                if ($backup.Exists) {
                    [IO.File]::WriteAllBytes($backup.Path, $backup.Bytes)
                    $restored = [IO.File]::ReadAllBytes($backup.Path)
                    if ([Convert]::ToBase64String($restored) -ne [Convert]::ToBase64String($backup.Bytes)) {
                        throw "Byte-for-byte settings restoration failed: $($backup.Path)"
                    }
                    [IO.File]::SetLastWriteTimeUtc($backup.Path, $backup.LastWriteTimeUtc)
                    if ([IO.File]::GetLastWriteTimeUtc($backup.Path) -ne $backup.LastWriteTimeUtc) {
                        throw "Settings modification-time restoration failed: $($backup.Path)"
                    }
                }
                elseif (Test-Path -LiteralPath $backup.Path) {
                    Remove-Item -LiteralPath $backup.Path -Force
                }
            } catch {
                $settingsCleanupErrors += "$($backup.Path): $($_.Exception.Message)"
            }
        }
        $result.settingsRestored=($settingsCleanupErrors.Count -eq 0)
        if ($settingsCleanupErrors.Count) {
            $result.status='failed'
            $result['settingsCleanupErrors']=$settingsCleanupErrors
            $result.reason += ' Settings cleanup failed: ' + ($settingsCleanupErrors -join '; ')
        }
        try {
            if ($null -ne $nativeDisplayBackup) {
                $result['displayRestoration']=Restore-NativeDisplaySettings $nativeDisplayBackup
                $result.displaySettingsRestored=$result.displayRestoration.succeeded
            }
        }
        catch {
            $result.status='failed'
            $result.reason += " Display settings cleanup failed: $($_.Exception.Message)"
        }
        try {
            if ($probeCreated) { Remove-OwnedDirectory $probeDirectory $modsDirectory }
            $result.probeRemoved = -not $probeCreated -or -not (Test-Path -LiteralPath $probeDirectory)
        }
        catch {
            $result.status = 'failed'
            $result.reason += " Probe cleanup failed: $($_.Exception.Message)"
        }
    }
    else {
        $result.reason += " Settings and the temporary mod were retained for recovery after the game exits: $backupDirectory"
    }
    try { Remove-OwnedDirectory $workDirectory $runDirectory }
    catch { $result.status = 'failed'; $result.reason += " Build cleanup failed: $($_.Exception.Message)" }
    $result['completedUtc'] = [datetime]::UtcNow.ToString('O')
    Write-Json (Join-Path $runDirectory 'Result.json') $result
    Write-Host "Status: $($result.status); passed=$($result.passed), failed=$($result.failed), incomplete=$($result.incomplete)"
    Write-Host $result.reason
    Write-Host "Evidence: $runDirectory"
}

if ($failure) { throw $failure }
if ($result.status -eq 'failed') { throw "Runtime verification failed. See $runDirectory" }
if (-not $ValidateOnly -and $result.status -eq 'incomplete') {
    throw "Runtime verification incomplete. See $runDirectory"
}
