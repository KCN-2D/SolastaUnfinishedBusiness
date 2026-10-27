[CmdletBinding()]
param(
    [string] $BuildScriptPath,
    [string] $BuildOutputDirectory,
    [string] $ZipPath,
    [string] $GameExePath,
    [string] $GameModsDirectory,
    [string] $PlayerLogPath,
    [string] $UmmStagingDirectory,
    [int] $StartupWaitSeconds = 45,
    [switch] $SkipBuild,
    [switch] $Sync,
    [switch] $Launch,
    [switch] $PreflightOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'WindowsDesktop.ps1')

function Assert-SyncOrLaunchAvailable([string] $GameDirectory) {
    Assert-VerificationGameStopped
    $marker = Join-Path $GameDirectory 'Mods\.UBGameTestTransaction.json'
    if (Test-Path -LiteralPath $marker) {
        throw 'An unresolved game-test transaction exists. Restore it before synchronizing or launching.'
    }
}

function Resolve-DefaultPath {
    param(
        [string] $Preferred,
        [string[]] $Candidates
    )

    if ($Preferred) {
        return $Preferred
    }

    foreach ($candidate in $Candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    return $Candidates | Select-Object -First 1
}

function Assert-PathExists {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyString()]
        [string] $Path,
        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        throw "Missing ${Description}: $Path"
    }
}

function Assert-CommandExists {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if (-not (Get-Command -Name $Name -ErrorAction SilentlyContinue)) {
        throw "Missing command: $Name"
    }
}

function Get-JsonPropertyValue {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Object,
        [Parameter(Mandatory = $true)]
        [string] $Name,
        [object] $Default = ""
    )

    $property = $Object.PSObject.Properties[$Name]

    if ($null -eq $property) {
        return $Default
    }

    return $property.Value
}

function Invoke-RobocopyOverlay {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Source,
        [Parameter(Mandatory = $true)]
        [string] $Destination,
        [switch] $PreserveSettings
    )

    $sourceRoot = [IO.Path]::GetFullPath($Source).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $destinationRoot = [IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($sourceRoot.Equals($destinationRoot,[StringComparison]::OrdinalIgnoreCase) -or
        $destinationRoot.StartsWith($sourceRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or
        $sourceRoot.StartsWith($destinationRoot+'\',[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Build output and sync destination must not overlap.'
    }
    $arguments = @(
        $sourceRoot, $destinationRoot,
        "/E", "/R:1", "/W:1", "/NFL", "/NDL", "/NJH", "/NJS", "/NP"
    )

    if ($PreserveSettings) {
        $sourceSettingsFile = Join-Path $sourceRoot "Settings.xml"
        $sourceSettingsDirectory = Join-Path $sourceRoot "Settings"
        # Full source paths exclude only the persistent root settings, not nested documentation names.
        $arguments += @("/XF", $sourceSettingsFile, "/XD", $sourceSettingsDirectory)
    }

    $outputLines = @(& robocopy @arguments)

    foreach ($line in $outputLines) {
        if ($line -match "^\s*\*EXTRA (Dir|File)\s+") {
            continue
        }

        Write-Host $line
    }

    if ($LASTEXITCODE -gt 7) {
        throw "robocopy failed with exit code $LASTEXITCODE."
    }

    if ($PreserveSettings) {
        $defaults = @()
        if ([IO.File]::Exists($sourceSettingsFile)) {
            $defaults += Get-Item -LiteralPath $sourceSettingsFile -Force
        }
        if ([IO.Directory]::Exists($sourceSettingsDirectory)) {
            $defaults += Get-ChildItem -LiteralPath $sourceSettingsDirectory -File -Recurse -Force
        }
        foreach ($file in $defaults) {
            $relativePath = $file.FullName.Substring($sourceRoot.Length + 1)
            $targetPath = Join-Path $destinationRoot $relativePath
            if (-not [IO.File]::Exists($targetPath)) {
                [void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($targetPath))
                [IO.File]::Copy($file.FullName, $targetPath, $false)
            }
        }
    }

    # Robocopy can skip different bytes when size and mtime are identical.
    # Compare content once per explicit sync, excluding persistent settings.
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -File -Recurse -Force) {
        $relativePath = $file.FullName.Substring($sourceRoot.Length + 1)
        if ($PreserveSettings -and ($relativePath.Equals('Settings.xml',[StringComparison]::OrdinalIgnoreCase) -or
            $relativePath.StartsWith('Settings\',[StringComparison]::OrdinalIgnoreCase))) { continue }
        $targetPath = Join-Path $destinationRoot $relativePath
        $expectedHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        $actualHash = if ([IO.File]::Exists($targetPath)) { (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash } else { '' }
        if ($actualHash -ne $expectedHash) {
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($targetPath))
            [IO.File]::Copy($file.FullName,$targetPath,$true)
            if ((Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash -ne $expectedHash) {
                throw "Synchronized file differs from the build output: $relativePath"
            }
        }
    }
}

function Get-FileFingerprint {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return [PSCustomObject]@{
            Exists        = $false
            LastWriteTime = $null
            Attributes    = $null
            Hash          = ""
        }
    }

    $item = Get-Item -LiteralPath $Path -Force
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash

    return [PSCustomObject]@{
        Exists        = $true
        LastWriteTime = $item.LastWriteTimeUtc
        Attributes    = $item.Attributes
        Hash          = $hash
    }
}

function Get-PersistentSettingsSnapshot([string] $ModDirectory) {
    $paths = @()
    $rootSettings = Join-Path $ModDirectory 'Settings.xml'
    if ([IO.File]::Exists($rootSettings)) { $paths += $rootSettings }
    $settingsDirectory = Join-Path $ModDirectory 'Settings'
    if ([IO.Directory]::Exists($settingsDirectory)) {
        $paths += @(Get-ChildItem -LiteralPath $settingsDirectory -Recurse -File -Force | ForEach-Object { $_.FullName })
    }
    return @(foreach ($path in $paths) {
        [pscustomobject]@{path=$path;fingerprint=(Get-FileFingerprint -Path $path)}
    })
}

function Assert-PersistentSettingsPreserved([object[]] $Snapshot) {
    foreach ($entry in $Snapshot) {
        $actual = Get-FileFingerprint -Path $entry.path
        if (-not $actual.Exists -or $actual.Hash -ne $entry.fingerprint.Hash -or
            $actual.LastWriteTime -ne $entry.fingerprint.LastWriteTime -or
            $actual.Attributes -ne $entry.fingerprint.Attributes) {
            throw "Persistent settings changed or disappeared during sync: $($entry.path)"
        }
    }
}

function Get-StreamPrefixHash {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileStream] $Stream,
        [Parameter(Mandatory = $true)]
        [int] $Length
    )

    if ($Length -eq 0) {
        return ""
    }

    $originalPosition = $Stream.Position

    try {
        [void] $Stream.Seek(0, [System.IO.SeekOrigin]::Begin)
        $buffer = [byte[]]::new($Length)
        $bytesRead = 0

        while ($bytesRead -lt $Length) {
            $read = $Stream.Read($buffer, $bytesRead, $Length - $bytesRead)

            if ($read -eq 0) {
                return ""
            }

            $bytesRead += $read
        }

        $sha256 = [System.Security.Cryptography.SHA256]::Create()

        try {
            return [Convert]::ToBase64String($sha256.ComputeHash($buffer))
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        [void] $Stream.Seek($originalPosition, [System.IO.SeekOrigin]::Begin)
    }
}

function Get-LogCheckpoint {
    param(
        [Parameter(Mandatory = $true)]
        [string] $LogPath
    )

    if (-not (Test-Path -LiteralPath $LogPath)) {
        return [PSCustomObject]@{
            Exists          = $false
            Length          = [long] 0
            CreationTimeUtc = [datetime]::MinValue
            PrefixLength    = 0
            PrefixHash      = ""
        }
    }

    $item = Get-Item -LiteralPath $LogPath
    $prefixLength = [int] [Math]::Min([long] 4096, $item.Length)
    $stream = [System.IO.FileStream]::new(
        $LogPath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
    )

    try {
        $prefixHash = Get-StreamPrefixHash -Stream $stream -Length $prefixLength
    }
    finally {
        $stream.Dispose()
    }

    return [PSCustomObject]@{
        Exists          = $true
        Length          = [long] $item.Length
        CreationTimeUtc = $item.CreationTimeUtc
        PrefixLength    = $prefixLength
        PrefixHash      = $prefixHash
    }
}

function Get-NewLogContent {
    param(
        [Parameter(Mandatory = $true)]
        [string] $LogPath,
        [Parameter(Mandatory = $true)]
        [object] $Checkpoint
    )

    if (-not (Test-Path -LiteralPath $LogPath)) {
        return ""
    }

    $item = Get-Item -LiteralPath $LogPath
    $stream = [System.IO.FileStream]::new(
        $LogPath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
    )

    try {
        $startOffset = [long] 0

        if (
            $Checkpoint.Exists -and
            $item.CreationTimeUtc -eq $Checkpoint.CreationTimeUtc -and
            $stream.Length -ge $Checkpoint.Length
        ) {
            $currentPrefixHash = Get-StreamPrefixHash -Stream $stream -Length $Checkpoint.PrefixLength

            if ($currentPrefixHash -eq $Checkpoint.PrefixHash) {
                $startOffset = $Checkpoint.Length
            }
        }

        [void] $stream.Seek($startOffset, [System.IO.SeekOrigin]::Begin)
        $reader = [System.IO.StreamReader]::new(
            $stream,
            [System.Text.Encoding]::UTF8,
            $true,
            4096,
            $true
        )

        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-NewLogSummary {
    param(
        [Parameter(Mandatory = $true)]
        [string] $LogPath,
        [Parameter(Mandatory = $true)]
        [object] $Checkpoint
    )

    $content = Get-NewLogContent -LogPath $LogPath -Checkpoint $Checkpoint
    $managerMatches = [regex]::Matches(
        $content,
        "(?m)^\[Manager\]\s+FINISH\.\s+SUCCESSFUL LOADED\s+(?<loaded>\d+)/(?<total>\d+)\s+MODS\.\s*$"
    )
    $managerSeen = $managerMatches.Count -gt 0
    $managerLoaded = 0
    $managerTotal = 0

    if ($managerSeen) {
        $managerMatch = $managerMatches[$managerMatches.Count - 1]
        $managerLoaded = [int] $managerMatch.Groups["loaded"].Value
        $managerTotal = [int] $managerMatch.Groups["total"].Value
    }

    $errors = @(
        $content -split "\r?\n" | Where-Object {
            $_ -match "^\[SolastaUnfinishedBusiness\].*\[(Error|Exception)\]" -or
            $_ -match "^\[SolastaUnfinishedBusiness\]\s+Not loaded\.\s*$" -or
            $_ -match "HarmonyException" -or
            $_ -match "BootContext.*(Exception|NullReferenceException)"
        }
    )

    if (
        [regex]::IsMatch(
            $content,
            "(?m)(Exception|NullReferenceException)[^\r\n]*(\r?\n[^\r\n]*){0,20}BootContext|" +
            "BootContext[^\r\n]*(Exception|NullReferenceException)"
        )
    ) {
        $errors += "BootContext exception detected."
    }

    return [PSCustomObject]@{
        HasNewContent    = -not [string]::IsNullOrEmpty($content)
        ManagerSeen      = $managerSeen
        ManagerLoaded    = $managerLoaded
        ManagerTotal     = $managerTotal
        ManagerSucceeded = $managerSeen -and $managerTotal -gt 0 -and $managerLoaded -eq $managerTotal
        VersionSeen      = [regex]::IsMatch(
            $content,
            "(?m)^\[SolastaUnfinishedBusiness\]\s+Version\s+'[^']+'\.\s+Loading\.\s*$"
        )
        ActiveSeen       = [regex]::IsMatch(
            $content,
            "(?m)^\[SolastaUnfinishedBusiness\]\s+Active\.\s*$"
        )
        EnabledSeen      = [regex]::IsMatch(
            $content,
            "(?m)^\[SolastaUnfinishedBusiness\]\s+enabled in .+\.\s*$"
        )
        Errors           = $errors
    }
}

function Wait-ForSolastaStartupMarkers {
    param(
        [Parameter(Mandatory = $true)]
        [string] $LogPath,
        [Parameter(Mandatory = $true)]
        [object] $Checkpoint,
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process] $Process,
        [Parameter(Mandatory = $true)]
        [int] $TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $latestSummary = Get-NewLogSummary -LogPath $LogPath -Checkpoint $Checkpoint

    do {
        $latestSummary = Get-NewLogSummary -LogPath $LogPath -Checkpoint $Checkpoint

        if ($latestSummary.Errors.Count -gt 0) {
            $firstError = $latestSummary.Errors | Select-Object -First 1
            throw "SolastaUnfinishedBusiness reported an error during startup: $firstError"
        }

        if ($latestSummary.ManagerSeen -and -not $latestSummary.ManagerSucceeded) {
            throw "Unity Mod Manager loaded $($latestSummary.ManagerLoaded)/$($latestSummary.ManagerTotal) mods."
        }

        if (
            $latestSummary.ManagerSucceeded -and
            $latestSummary.VersionSeen -and
            $latestSummary.ActiveSeen -and
            $latestSummary.EnabledSeen
        ) {
            return $latestSummary
        }

        $Process.Refresh()

        if ($Process.HasExited) {
            throw "Solasta exited before all startup markers were observed (exit code $($Process.ExitCode))."
        }

        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)

    $missingMarkers = @()

    if (-not $latestSummary.ManagerSucceeded) {
        $missingMarkers += "UMM n/n MODS"
    }

    if (-not $latestSummary.VersionSeen) {
        $missingMarkers += "Version ... Loading."
    }

    if (-not $latestSummary.ActiveSeen) {
        $missingMarkers += "Active."
    }

    if (-not $latestSummary.EnabledSeen) {
        $missingMarkers += "enabled in ..."
    }

    throw "Timed out after $TimeoutSeconds seconds waiting for new Player.log markers: $($missingMarkers -join ', ')."
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$solastaInstallDirFromEnv = $env:SolastaInstallDir
$gameExeCandidates = @()

if ($solastaInstallDirFromEnv) {
    $gameExeCandidates += (Join-Path $solastaInstallDirFromEnv "Solasta.exe")
}

$BuildScriptPath = Resolve-DefaultPath -Preferred $BuildScriptPath -Candidates @(
    (Join-Path $repoRoot "tools\CustomBuildLocal\BuildCustomLocalPackage.cmd")
)

$BuildOutputDirectory = Resolve-DefaultPath -Preferred $BuildOutputDirectory -Candidates @(
    (Join-Path $repoRoot "tools\CustomBuildLocal\Output\SolastaUnfinishedBusiness")
)

$ZipPath = Resolve-DefaultPath -Preferred $ZipPath -Candidates @(
    (Join-Path $repoRoot "tools\CustomBuildLocal\SolastaUnfinishedBusiness.zip")
)

$GameExePath = Resolve-DefaultPath -Preferred $GameExePath -Candidates $gameExeCandidates

if (-not $GameModsDirectory) {
    if ($GameExePath) {
        $GameModsDirectory = Join-Path (Split-Path -Parent $GameExePath) "Mods\SolastaUnfinishedBusiness"
    }
    else {
        $GameModsDirectory = ""
    }
}

$PlayerLogPath = Resolve-DefaultPath -Preferred $PlayerLogPath -Candidates @(
    (Join-Path $env:USERPROFILE "AppData\LocalLow\Tactical Adventures\Solasta\Player.log")
)

Write-Host "Resolved paths:"
Write-Host "  Build        : $(-not $SkipBuild)"
Write-Host "  Sync         : $([bool] $Sync)"
Write-Host "  Launch       : $([bool] $Launch)"
Write-Host "  Build script : $BuildScriptPath"
Write-Host "  Build output : $BuildOutputDirectory"
Write-Host "  ZIP path     : $ZipPath"
Write-Host "  Game exe     : $GameExePath"
Write-Host "  Game mod dir : $GameModsDirectory"
Write-Host "  Player.log   : $PlayerLogPath"
Write-Host "  UMM staging  : $UmmStagingDirectory"

if (-not $SkipBuild -or $Sync) {
    Assert-CommandExists -Name "robocopy"
}

if (-not $SkipBuild) {
    Assert-PathExists -Path $BuildScriptPath -Description "build script"
}

if ($Sync -or $Launch) {
    Assert-PathExists -Path $GameExePath -Description "game executable"
    $resolvedGameExePath = [System.IO.Path]::GetFullPath($GameExePath)
    $referenceSnapshotDirectory = [System.IO.Path]::GetFullPath(
        (Join-Path $repoRoot "SolastaFiles")
    ).TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar

    if ($resolvedGameExePath.StartsWith(
            $referenceSnapshotDirectory,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The repository SolastaFiles snapshot cannot be used for -Sync or -Launch. " +
            "Specify the installed game with -GameExePath or SolastaInstallDir."
    }

    $gameDirectory = Split-Path -Parent $GameExePath
    Assert-SyncOrLaunchAvailable $gameDirectory
    Assert-PathExists -Path (Join-Path $gameDirectory "winhttp.dll") -Description "UMM runtime winhttp.dll"
    Assert-PathExists -Path (Join-Path $gameDirectory "doorstop_config.ini") -Description "UMM runtime doorstop_config.ini"
    $sourceInfoPath = Join-Path $repoRoot "SolastaUnfinishedBusiness\Info.json"
    Assert-PathExists -Path $sourceInfoPath -Description "source Info.json"
    $sourceInfo = Get-Content -LiteralPath $sourceInfoPath -Raw | ConvertFrom-Json
    $minimumManagerVersion = [version] $sourceInfo.ManagerVersion
    $managerAssemblyPath = Join-Path $gameDirectory "Solasta_Data\Managed\UnityModManager\UnityModManager.dll"
    Assert-PathExists -Path $managerAssemblyPath -Description "UMM runtime assembly"
    $managerVersion = [System.Reflection.AssemblyName]::GetAssemblyName($managerAssemblyPath).Version
    Write-Host "Unity Mod Manager runtime: $managerVersion (minimum $minimumManagerVersion)"
    if ($managerVersion -lt $minimumManagerVersion) {
        throw "Unity Mod Manager $minimumManagerVersion or newer is required; installed runtime is $managerVersion."
    }
}

if ($Sync) {
    if ([string]::IsNullOrWhiteSpace($GameModsDirectory)) {
        throw "Game Mods directory could not be resolved. Specify -GameModsDirectory or -GameExePath."
    }

    if ($UmmStagingDirectory) {
        Assert-PathExists -Path $UmmStagingDirectory -Description "UMM staging directory"
    }
}

if ($Launch -and $StartupWaitSeconds -le 0) {
    throw "StartupWaitSeconds must be greater than zero when -Launch is used."
}

if ($PreflightOnly) {
    if ($Sync -and $SkipBuild) {
        Assert-PathExists -Path $BuildOutputDirectory -Description "build output directory"
        Assert-PathExists -Path $ZipPath -Description "custom build zip"
    }

    Write-Host ""
    Write-Host "Preflight OK."
    return
}

if (-not $SkipBuild) {
    Write-Host ""
    Write-Host "Running build script: $BuildScriptPath"
    & cmd /c $BuildScriptPath

    if ($LASTEXITCODE -ne 0) {
        throw "Build script failed with exit code ${LASTEXITCODE}: $BuildScriptPath"
    }
}

if (-not $SkipBuild -or $Sync) {
    Assert-PathExists -Path $BuildOutputDirectory -Description "build output directory"
    Assert-PathExists -Path $ZipPath -Description "custom build zip"

    $outputInfoPath = Join-Path $BuildOutputDirectory "Info.json"
    Assert-PathExists -Path $outputInfoPath -Description "build output Info.json"
    $outputInfo = Get-Content -LiteralPath $outputInfoPath -Raw | ConvertFrom-Json

    Write-Host ""
    Write-Host "Build output version: $($outputInfo.Version)"
    Write-Host "CustomBuild: $(Get-JsonPropertyValue -Object $outputInfo -Name 'CustomBuild' -Default '<missing>')"
    Write-Host "CustomBuildBaseVersion: $(Get-JsonPropertyValue -Object $outputInfo -Name 'CustomBuildBaseVersion' -Default '<missing>')"
}

if ($Sync) {
    Assert-SyncOrLaunchAvailable $gameDirectory
    Write-Host ""
    Write-Host "Syncing build output to game Mods folder..."
    New-Item -ItemType Directory -Path $GameModsDirectory -Force | Out-Null
    $settingsBeforeSync = @(Get-PersistentSettingsSnapshot $GameModsDirectory)
    Invoke-RobocopyOverlay -Source $BuildOutputDirectory -Destination $GameModsDirectory -PreserveSettings
    Assert-PersistentSettingsPreserved $settingsBeforeSync
    Write-Host "Persistent settings preserved: $($settingsBeforeSync.Count) existing files."

    if ($UmmStagingDirectory) {
        Write-Host "Copying ZIP to UMM staging folder..."
        Copy-Item -LiteralPath $ZipPath -Destination $UmmStagingDirectory -Force
    }
}

if (-not $Launch) {
    Write-Host ""
    Write-Host "Launch not requested."
    return
}

$logCheckpoint = Get-LogCheckpoint -LogPath $PlayerLogPath
Assert-SyncOrLaunchAvailable $gameDirectory

Write-Host ""
Write-Host "Launching Solasta..."
$process = Start-Process -FilePath $GameExePath -WorkingDirectory (Split-Path -Parent $GameExePath) -PassThru

$summary = Wait-ForSolastaStartupMarkers `
    -LogPath $PlayerLogPath `
    -Checkpoint $logCheckpoint `
    -Process $process `
    -TimeoutSeconds $StartupWaitSeconds

Write-Host ""
Write-Host "Semi-auto test summary:"
Write-Host "  ProcessId  : $($process.Id)"
Write-Host "  UMM loaded : $($summary.ManagerLoaded)/$($summary.ManagerTotal)"
Write-Host "  Version    : $($summary.VersionSeen)"
Write-Host "  Active     : $($summary.ActiveSeen)"
Write-Host "  Enabled    : $($summary.EnabledSeen)"

Write-Host ""
Write-Host "Manual check:"
Write-Host "  1. In-game, press Ctrl-F10 to open UMM."
Write-Host "  2. Confirm SolastaUnfinishedBusiness is loaded and enabled."
Write-Host "  3. Reproduce the target feature and verify behavior."
