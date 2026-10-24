[CmdletBinding()]
param([switch]$Preflight)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$buildRoot = Split-Path -Parent $PSScriptRoot

function Assert-Command {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,
        [Parameter(Mandatory = $true)]
        [string] $InstallHint
    )

    if (-not (Get-Command -Name $Name -ErrorAction SilentlyContinue)) {
        throw "エラー: $Name が PATH に見つかりません。$InstallHint"
    }
}

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Description,
        [Parameter(Mandatory = $true)]
        [scriptblock] $Action
    )

    Write-Host $Description
    & $Action
}

function Convert-ToGitPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    return ((Resolve-Path -LiteralPath $Path).Path -replace "\\", "/")
}

function Import-CompressionAssemblies {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
}

function Write-BuildCompleted {
    param(
        [Parameter(Mandatory = $true)]
        [string] $OutputPath,
        [string] $NextStep = ""
    )

    Write-Host ""
    Write-Host "完了しました。"
    Write-Host "作成されたファイル:"
    Write-Host "  $OutputPath"

    if (-not [string]::IsNullOrWhiteSpace($NextStep)) {
        Write-Host $NextStep
    }
}

function Invoke-NativeCommandCapture {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock] $Command
    )

    $previousErrorActionPreference = $ErrorActionPreference

    try {
        $Global:ErrorActionPreference = "Continue"
        $outputLines = @(& $Command 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $Global:ErrorActionPreference = $previousErrorActionPreference
    }

    return @{
        ExitCode = $exitCode
        Output = @($outputLines | ForEach-Object { "$_" })
    }
}

function Invoke-Robocopy {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Source,
        [Parameter(Mandatory = $true)]
        [string] $Destination,
        [string[]] $ExtraArguments = @(),
        [switch] $SuppressExtraFileOutput
    )

    $arguments = @($Source, $Destination) + $ExtraArguments + @("/R:1", "/W:1", "/NFL", "/NDL", "/NJH", "/NJS", "/NP")

    $outputLines = @(& robocopy @arguments)

    foreach ($line in $outputLines) {
        if ($SuppressExtraFileOutput -and $line -match "^\s*\*EXTRA File\s+") {
            continue
        }

        Write-Host $line
    }

    if ($LASTEXITCODE -gt 7) {
        throw "エラー: robocopy が失敗しました。終了コード: $LASTEXITCODE"
    }
}

function Get-CustomBuildSuffix {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string] $RepositoryGitPath
    )

    $suffix = "custom.local"
    $sha = & git -C $RepositoryRoot -c "safe.directory=$RepositoryGitPath" rev-parse --short HEAD 2>$null

    if ($LASTEXITCODE -eq 0 -and $sha) {
        return "custom.$(($sha | Select-Object -Last 1).Trim())"
    }

    return $suffix
}

function Add-TemporaryWorktree {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string] $RepositoryGitPath,
        [Parameter(Mandatory = $true)]
        [string] $WorktreePath,
        [Parameter(Mandatory = $true)]
        [string] $BaseRef
    )

    $commandResult = Invoke-NativeCommandCapture -Command {
        git -C $RepositoryRoot -c "safe.directory=$RepositoryGitPath" worktree add --detach $WorktreePath $BaseRef
    }

    foreach ($line in $commandResult.Output) {
        Write-Host $line
    }

    return @{
        Success = $commandResult.ExitCode -eq 0
        ExitCode = $commandResult.ExitCode
        Output = $commandResult.Output
    }
}

function Add-TemporaryRepositoryClone {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string] $WorkspacePath,
        [Parameter(Mandatory = $true)]
        [string] $BaseRef
    )

    New-Item -ItemType Directory -Path $WorkspacePath -Force | Out-Null

    $sourceGitDirectory = Join-Path $RepositoryRoot ".git"
    $destinationGitDirectory = Join-Path $WorkspacePath ".git"

    if (-not (Test-Path -LiteralPath $sourceGitDirectory)) {
        throw "エラー: 元リポジトリの .git フォルダが見つかりません: $sourceGitDirectory"
    }

    Invoke-Robocopy -Source $sourceGitDirectory -Destination $destinationGitDirectory -ExtraArguments @(
        "/MIR",
        "/XD", "worktrees"
    )

    $workFolderGitPath = Convert-ToGitPath -Path $WorkspacePath
    $checkoutResult = Invoke-NativeCommandCapture -Command {
        git -C $WorkspacePath -c "safe.directory=$workFolderGitPath" checkout --force --detach $BaseRef
    }

    foreach ($line in $checkoutResult.Output) {
        if (-not [string]::IsNullOrWhiteSpace("$line")) {
            Write-Host $line
        }
    }

    if ($checkoutResult.ExitCode -ne 0) {
        throw "エラー: 一時 clone fallback で '$BaseRef' を checkout できませんでした。"
    }
}

function Initialize-TemporaryWorkspace {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string] $RepositoryGitPath,
        [Parameter(Mandatory = $true)]
        [string] $WorkspacePath,
        [Parameter(Mandatory = $true)]
        [string] $BaseRef
    )

    $worktreeResult = Add-TemporaryWorktree `
        -RepositoryRoot $RepositoryRoot `
        -RepositoryGitPath $RepositoryGitPath `
        -WorktreePath $WorkspacePath `
        -BaseRef $BaseRef

    if ($worktreeResult.Success) {
        return "worktree"
    }

    $worktreeOutput = [string]::Join("`n", $worktreeResult.Output)

    if ($worktreeOutput -match "Permission denied" -or
        $worktreeOutput -match "could not create directory of '.git/worktrees") {
        Write-Host "警告: 作業用フォルダを通常の方法で作れませんでした。別の方法で続けます。" -ForegroundColor Yellow

        Add-TemporaryRepositoryClone `
            -RepositoryRoot $RepositoryRoot `
            -WorkspacePath $WorkspacePath `
            -BaseRef $BaseRef

        return "clone"
    }

    throw "エラー: 作業用フォルダを作成できませんでした。"
}

function Remove-TemporaryWorktree {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string] $RepositoryGitPath,
        [Parameter(Mandatory = $true)]
        [string] $WorktreePath
    )

    $fullWorktreePath = [System.IO.Path]::GetFullPath($WorktreePath)
    $worktreeDirectory = [System.IO.DirectoryInfo]::new($fullWorktreePath)
    $buildDirectory = $worktreeDirectory.Parent
    $tempDirectory = if ($buildDirectory) { $buildDirectory.Parent } else { $null }
    $expectedTempDirectory = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::GetTempPath()).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)

    if ($worktreeDirectory.Name -ne "repo" -or
        -not $buildDirectory -or
        $buildDirectory.Name -notmatch "^SolastaUB-CustomBuild-[0-9a-f]{32}$" -or
        -not $tempDirectory -or
        $tempDirectory.FullName.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar) -ne $expectedTempDirectory) {
        throw "エラー: 安全に削除できない一時 worktree パスです: $fullWorktreePath"
    }

    if (Test-Path -LiteralPath $fullWorktreePath) {
        $removeResult = Invoke-NativeCommandCapture -Command {
            git -C $RepositoryRoot -c "safe.directory=$RepositoryGitPath" worktree remove --force $fullWorktreePath
        }

        if ($removeResult.ExitCode -ne 0) {
            Write-Host "警告: 一時 worktree の通常削除に失敗したため、安全確認後に登録を整理します。" -ForegroundColor Yellow

            foreach ($line in $removeResult.Output) {
                if (-not [string]::IsNullOrWhiteSpace("$line")) {
                    Write-Host "  $line"
                }
            }

            Remove-Item -LiteralPath $fullWorktreePath -Recurse -Force
        }
    }

    $pruneResult = Invoke-NativeCommandCapture -Command {
        git -C $RepositoryRoot -c "safe.directory=$RepositoryGitPath" worktree prune --expire now --verbose
    }

    foreach ($line in $pruneResult.Output) {
        if (-not [string]::IsNullOrWhiteSpace("$line")) {
            Write-Host $line
        }
    }

    if ($pruneResult.ExitCode -ne 0) {
        throw "エラー: 一時 worktree の古い登録を整理できませんでした。"
    }

    $normalizedWorktreePath = $fullWorktreePath -replace "\\", "/"
    $registeredPaths = @(& git -C $RepositoryRoot -c "safe.directory=$RepositoryGitPath" worktree list --porcelain)

    if ($LASTEXITCODE -ne 0) {
        throw "エラー: 一時 worktree の登録解除を確認できませんでした。"
    }

    if ($registeredPaths -contains "worktree $normalizedWorktreePath") {
        throw "エラー: 一時 worktree の登録が残っています: $fullWorktreePath"
    }
}

function Test-WorkspaceOverlayPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RelativePath
    )

    $normalizedPath = $RelativePath -replace "\\", "/"

    if ([string]::IsNullOrWhiteSpace($normalizedPath)) {
        return $false
    }

    if ($normalizedPath.StartsWith("tools/", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    $pathSegments = @($normalizedPath.Split("/"))

    foreach ($excludedDirectoryName in @(".git", ".vs", "artifacts", "SolastaFiles", "CustomBuildLocal", "bin", "obj")) {
        if ($pathSegments -contains $excludedDirectoryName) {
            return $false
        }
    }

    $fileName = [System.IO.Path]::GetFileName($normalizedPath)

    return $fileName -notin @("BuildCustomLocalPackage.cmd", "SolastaUnfinishedBusiness.zip")
}

function Add-AffectedWorkspacePathsFromGitDiff {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.HashSet[string]] $AffectedPaths,
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string] $RepositoryGitPath,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]] $DiffArguments,
        [Parameter(Mandatory = $true)]
        [string] $ErrorContext
    )

    $statusLines = @(& git -C $RepositoryRoot -c "safe.directory=$RepositoryGitPath" diff --name-status --find-renames @DiffArguments --)

    if ($LASTEXITCODE -ne 0) {
        throw "エラー: $ErrorContext を列挙できませんでした。"
    }

    foreach ($statusLine in $statusLines) {
        if ([string]::IsNullOrWhiteSpace("$statusLine")) {
            continue
        }

        $parts = "$statusLine".Split("`t")

        if ($parts.Count -lt 2) {
            continue
        }

        $status = $parts[0]

        $candidatePaths = switch -Regex ($status) {
            "^R" { @($parts[1], $parts[2]) }
            "^C" { @($parts[2]) }
            default { @($parts[$parts.Count - 1]) }
        }

        foreach ($candidatePath in $candidatePaths) {
            if ([string]::IsNullOrWhiteSpace($candidatePath)) {
                continue
            }

            $AffectedPaths.Add($candidatePath.Trim()) | Out-Null
        }
    }
}

function Get-WorkspaceOverlayPaths {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string] $RepositoryGitPath,
        [Parameter(Mandatory = $true)]
        [string] $BaseRef,
        [string[]] $AlwaysIncludePaths = @()
    )

    $affectedPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    Add-AffectedWorkspacePathsFromGitDiff `
        -AffectedPaths $affectedPaths `
        -RepositoryRoot $RepositoryRoot `
        -RepositoryGitPath $RepositoryGitPath `
        -DiffArguments @("$BaseRef...HEAD") `
        -ErrorContext "'$BaseRef' との差分"

    Add-AffectedWorkspacePathsFromGitDiff `
        -AffectedPaths $affectedPaths `
        -RepositoryRoot $RepositoryRoot `
        -RepositoryGitPath $RepositoryGitPath `
        -DiffArguments @("--cached") `
        -ErrorContext "staged された作業ツリー差分"

    Add-AffectedWorkspacePathsFromGitDiff `
        -AffectedPaths $affectedPaths `
        -RepositoryRoot $RepositoryRoot `
        -RepositoryGitPath $RepositoryGitPath `
        -DiffArguments @() `
        -ErrorContext "unstaged の作業ツリー差分"

    $untrackedPaths = @(& git -C $RepositoryRoot -c "safe.directory=$RepositoryGitPath" ls-files --others --exclude-standard --)

    if ($LASTEXITCODE -ne 0) {
        throw "エラー: 未追跡ファイルの一覧を取得できませんでした。"
    }

    foreach ($untrackedPath in $untrackedPaths) {
        if ([string]::IsNullOrWhiteSpace("$untrackedPath")) {
            continue
        }

        $affectedPaths.Add(("$untrackedPath").Trim()) | Out-Null
    }

    foreach ($alwaysIncludePath in $AlwaysIncludePaths) {
        if ([string]::IsNullOrWhiteSpace($alwaysIncludePath)) {
            continue
        }

        $sourcePath = Join-Path $RepositoryRoot $alwaysIncludePath

        if (Test-Path -LiteralPath $sourcePath -PathType Leaf) {
            $affectedPaths.Add($alwaysIncludePath.Trim()) | Out-Null
        }
    }

    return @($affectedPaths | Where-Object { Test-WorkspaceOverlayPath $_ } | Sort-Object)
}

function Apply-WorkspaceOverlay {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string] $RepositoryGitPath,
        [Parameter(Mandatory = $true)]
        [string] $WorkspaceRoot,
        [Parameter(Mandatory = $true)]
        [string] $BaseRef,
        [string[]] $AlwaysIncludePaths = @()
    )

    $overlayPaths = Get-WorkspaceOverlayPaths `
        -RepositoryRoot $RepositoryRoot `
        -RepositoryGitPath $RepositoryGitPath `
        -BaseRef $BaseRef `
        -AlwaysIncludePaths $AlwaysIncludePaths

    $copiedCount = 0
    $removedCount = 0

    foreach ($relativePath in $overlayPaths) {
        $sourcePath = Join-Path $RepositoryRoot $relativePath
        $destinationPath = Join-Path $WorkspaceRoot $relativePath

        if (Test-Path -LiteralPath $sourcePath -PathType Leaf) {
            $destinationDirectory = Split-Path -Parent $destinationPath

            if (-not [string]::IsNullOrWhiteSpace($destinationDirectory) -and -not (Test-Path -LiteralPath $destinationDirectory)) {
                New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
            }

            Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
            $copiedCount++
        }
        elseif (Test-Path -LiteralPath $destinationPath) {
            Remove-Item -LiteralPath $destinationPath -Recurse -Force
            $removedCount++
        }
    }

    Write-Host ("  変更確認: 対象 {0}; コピー {1}; 削除 {2}" -f $overlayPaths.Count, $copiedCount, $removedCount)
}

function Apply-LocalRuntimePatch {
    param(
        [Parameter(Mandatory = $true)]
        [string] $WorkspaceRoot,
        [Parameter(Mandatory = $true)]
        [string] $WorkspaceGitPath,
        [Parameter(Mandatory = $true)]
        [string] $PatchPath
    )

    if (-not (Test-Path -LiteralPath $PatchPath -PathType Leaf)) {
        throw "エラー: コミュニティ用の調整ファイルが見つかりません: $PatchPath"
    }

    & git -C $WorkspaceRoot -c "safe.directory=$WorkspaceGitPath" apply --whitespace=nowarn $PatchPath | Out-Host

    if ($LASTEXITCODE -ne 0) {
        throw "エラー: コミュニティ用の調整を反映できませんでした: $PatchPath"
    }
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $buildRoot)
$repoGitPath = Convert-ToGitPath -Path $repoRoot
$copyBaseRef = "HEAD"
$solutionPath = Join-Path $repoRoot "SolastaUnfinishedBusiness.sln"
$solastaInstallDir = Join-Path $repoRoot "SolastaFiles"
$managedSourceDirectory = Join-Path $solastaInstallDir "Solasta_Data\Managed"
$sourceInfoJson = Join-Path $repoRoot "SolastaUnfinishedBusiness\Info.json"
$localOutputRoot = Join-Path $buildRoot "Output"
$localOutputFolder = Join-Path $localOutputRoot "SolastaUnfinishedBusiness"
$outputZip = Join-Path $buildRoot "SolastaUnfinishedBusiness.zip"
$requiredAssembly = Join-Path $solastaInstallDir "Solasta_Data\Managed\Assembly-CSharp.dll"
$requiredHarmony = Join-Path $solastaInstallDir "Solasta_Data\Managed\UnityModManager\0Harmony.dll"
$requiredUmm = Join-Path $solastaInstallDir "Solasta_Data\Managed\UnityModManager\UnityModManager.dll"
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("SolastaUB-CustomBuild-" + [guid]::NewGuid().ToString("N"))
$tempRepo = Join-Path $tempRoot "repo"
$tempRepoGitPath = $null
$tempSolutionPath = Join-Path $tempRepo "SolastaUnfinishedBusiness.sln"
$tempTranslationsZip = Join-Path $tempRepo "SolastaUnfinishedBusiness\Resources\Translations.zip"
$tempSolastaInstallDir = Join-Path $tempRoot "SolastaFiles"
$tempManagedDirectory = Join-Path $tempSolastaInstallDir "Solasta_Data\Managed"
$tempBuildOutputFolder = Join-Path $tempRoot "staging\SolastaUnfinishedBusiness"
$outputInfoJson = Join-Path $localOutputFolder "Info.json"
$customRuntimePatchPath = Join-Path $PSScriptRoot "CustomBuildRuntime.patch"
$overlayDependencyFiles = @(
    "SolastaUnfinishedBusiness/Assembly-CSharp.dll",
    "lib/Assembly-CSharp_public.dll"
)
$customBuildSuffix = $null
$dotnetVersion = $null
$originalSolastaInstallDir = $env:SolastaInstallDir
$originalUnityModManagerDir = $env:UnityModManagerDir
$temporaryWorktreeCreated = $false
$temporaryWorkspaceMode = ""

Assert-Command -Name "dotnet" -InstallHint ".NET SDK 9 以上をインストールしてから再実行してください。"
Assert-Command -Name "tar" -InstallHint "Windows 10/11 の tar.exe を利用できる状態にしてから再実行してください。"
Assert-Command -Name "robocopy" -InstallHint "robocopy.exe が必要です。通常は Windows に同梱されています。"
Assert-Command -Name "git" -InstallHint "ZIP 作成に Git for Windows が必要です。"

$dotnetVersion = (& dotnet --version).Trim()
$dotnetVersionParts = $dotnetVersion.Split(".")

if (-not $dotnetVersionParts[0] -or [int]$dotnetVersionParts[0] -lt 9) {
    throw "エラー: .NET SDK 9 以上が必要です。検出されたバージョン: $dotnetVersion"
}

foreach ($requiredPath in @($requiredAssembly, $requiredHarmony, $requiredUmm, $sourceInfoJson)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "エラー: 必須ファイルが見つかりません: $requiredPath"
    }
}

$minimumManagerVersion = [version](Get-Content -LiteralPath $sourceInfoJson -Raw | ConvertFrom-Json).ManagerVersion
$referenceManagerVersion = [Reflection.AssemblyName]::GetAssemblyName($requiredUmm).Version
if ($referenceManagerVersion -lt $minimumManagerVersion) {
    throw "エラー: UMM の参照 DLL は $minimumManagerVersion 以降が必要です。検出: $referenceManagerVersion"
}
Write-Host "Unity Mod Manager: $referenceManagerVersion"

if (-not (Test-Path -LiteralPath $solutionPath)) {
    throw "エラー: ビルド用の設定ファイルが見つかりません: $solutionPath"
}

$customBuildSuffix = Get-CustomBuildSuffix -RepositoryRoot $repoRoot -RepositoryGitPath $repoGitPath

if (-not (Test-Path -LiteralPath $customRuntimePatchPath -PathType Leaf)) {
    throw "エラー: コミュニティ用の調整ファイルが見つかりません: $customRuntimePatchPath"
}

$head = & git -C $repoRoot -c "safe.directory=$repoGitPath" rev-parse --verify "HEAD^{commit}"
if ($LASTEXITCODE -ne 0) {
    throw "エラー: ビルド元の HEAD コミットを確認できませんでした。"
}

if ($Preflight) {
    [pscustomobject]@{
        Status = "PASS"
        RepositoryRoot = $repoRoot
        BuildRoot = $buildRoot
        Commit = "$head".Trim()
        Dotnet = $dotnetVersion
        ManagerVersion = "$referenceManagerVersion"
        OutputDirectory = $localOutputFolder
        PackagePath = $outputZip
        TemporaryBuildOutput = $tempBuildOutputFolder
        RuntimePatch = $customRuntimePatchPath
    } | ConvertTo-Json
    return
}

Write-Host "SolastaUnfinishedBusiness.zip を作成します..."

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    $temporaryWorkspaceMode = Invoke-Step -Description "作業用フォルダを準備しています..." -Action {
        Initialize-TemporaryWorkspace `
            -RepositoryRoot $repoRoot `
            -RepositoryGitPath $repoGitPath `
            -WorkspacePath $tempRepo `
            -BaseRef $copyBaseRef
    }
    $temporaryWorktreeCreated = $temporaryWorkspaceMode -eq "worktree"

    $tempRepoGitPath = Convert-ToGitPath -Path $tempRepo

    Invoke-Step -Description "今の変更内容を作業用フォルダへコピーしています..." -Action {
        Apply-WorkspaceOverlay `
            -RepositoryRoot $repoRoot `
            -RepositoryGitPath $repoGitPath `
            -WorkspaceRoot $tempRepo `
            -BaseRef $copyBaseRef `
            -AlwaysIncludePaths $overlayDependencyFiles
    }

    Invoke-Step -Description "このコミュニティ用の調整を反映しています..." -Action {
        Apply-LocalRuntimePatch -WorkspaceRoot $tempRepo -WorkspaceGitPath $tempRepoGitPath -PatchPath $customRuntimePatchPath
    }

    Invoke-Step -Description "Solasta の必要なファイルを準備しています..." -Action {
        New-Item -ItemType Directory -Path $tempManagedDirectory -Force | Out-Null
        Invoke-Robocopy -Source $managedSourceDirectory -Destination $tempManagedDirectory -ExtraArguments @("/MIR")
    }

    if (Test-Path -LiteralPath $tempTranslationsZip) {
        Remove-Item -LiteralPath $tempTranslationsZip -Force
    }

    $env:SolastaInstallDir = $tempSolastaInstallDir
    $env:UnityModManagerDir = Join-Path $tempManagedDirectory "UnityModManager"

    Push-Location $tempRepo
    try {
        Invoke-Step -Description "必要な部品を準備しています..." -Action {
            & dotnet restore $tempSolutionPath
            if ($LASTEXITCODE -ne 0) {
                throw "エラー: dotnet restore が失敗しました。終了コード: $LASTEXITCODE"
            }
        }

        Invoke-Step -Description "本体ファイルを作成しています..." -Action {
            & dotnet build $tempSolutionPath --no-restore -c "Release Workflow" "-p:OutputPath=$tempBuildOutputFolder/"
            if ($LASTEXITCODE -ne 0) {
                throw "エラー: dotnet build が失敗しました。終了コード: $LASTEXITCODE"
            }
        }
    }
    finally {
        Pop-Location
    }

    if (-not (Test-Path -LiteralPath (Join-Path $tempBuildOutputFolder "SolastaUnfinishedBusiness.dll"))) {
        throw "エラー: ビルドは完了しましたが、期待する出力が見つかりません: $(Join-Path $tempBuildOutputFolder "SolastaUnfinishedBusiness.dll")"
    }

    if (-not (Test-Path -LiteralPath (Join-Path $tempBuildOutputFolder "Info.json"))) {
        throw "エラー: ビルドは完了しましたが、期待する出力が見つかりません: $(Join-Path $tempBuildOutputFolder "Info.json")"
    }

    Invoke-Step -Description "出力フォルダを更新しています..." -Action {
        New-Item -ItemType Directory -Path $localOutputRoot -Force | Out-Null
        Invoke-Robocopy -Source $tempBuildOutputFolder -Destination $localOutputFolder -ExtraArguments @("/MIR")
    }

    Invoke-Step -Description "この ZIP 用の情報を付けています..." -Action {
        $info = Get-Content -LiteralPath $outputInfoJson -Raw | ConvertFrom-Json
        $sourceInfo = Get-Content -LiteralPath $sourceInfoJson -Raw | ConvertFrom-Json
        $baseVersion = [string] $info.Version

        $info.Version = "$baseVersion+$customBuildSuffix"
        $info | Add-Member -NotePropertyName CustomBuild -NotePropertyValue $true -Force
        $info | Add-Member -NotePropertyName CustomBuildBaseVersion -NotePropertyValue $baseVersion -Force
        $json = $info | ConvertTo-Json -Depth 8
        [System.IO.File]::WriteAllText($outputInfoJson, $json + [Environment]::NewLine)

        if ($info.DisplayName -ne $sourceInfo.DisplayName) {
            throw "エラー: この ZIP 用の情報を付ける途中で DisplayName が想定外に変わりました。"
        }
    }

    if (Test-Path -LiteralPath $outputZip) {
        Remove-Item -LiteralPath $outputZip -Force
    }

    Invoke-Step -Description "SolastaUnfinishedBusiness.zip を作成しています..." -Action {
        Push-Location $localOutputRoot
        try {
            & tar -acf $outputZip "SolastaUnfinishedBusiness"
        }
        finally {
            Pop-Location
        }

        if ($LASTEXITCODE -ne 0) {
            throw "エラー: tar が失敗しました。終了コード: $LASTEXITCODE"
        }
    }

    Invoke-Step -Description "ZIP の中身を確認しています..." -Action {
        Import-CompressionAssemblies
        $sourceInfo = Get-Content -LiteralPath $sourceInfoJson -Raw | ConvertFrom-Json
        $zip = [System.IO.Compression.ZipFile]::OpenRead($outputZip)

        try {
            $entry = $zip.GetEntry("SolastaUnfinishedBusiness/Info.json")

            if (-not $entry) {
                throw "ZIP に SolastaUnfinishedBusiness/Info.json が含まれていません。"
            }

            $badEntries = $zip.Entries | Where-Object { $_.FullName.Contains("\") }

            if ($badEntries) {
                throw "ZIP に UMM で扱えないファイル名が含まれています: $((($badEntries | Select-Object -First 5 -ExpandProperty FullName) -join ", "))"
            }

            $reader = [System.IO.StreamReader]::new($entry.Open())

            try {
                $info = $reader.ReadToEnd() | ConvertFrom-Json
            }
            finally {
                $reader.Dispose()
            }

            if ($info.DisplayName -ne $sourceInfo.DisplayName) {
                throw "ZIP 内の Info.json で DisplayName が想定外に変わっています。"
            }

            if (-not $info.Version.Contains("+custom.")) {
                throw "ZIP 内の Info.json に、この ZIP 用のバージョン情報がありません。"
            }

            if (-not $info.CustomBuild) {
                throw "ZIP 内の Info.json に CustomBuild=true がありません。"
            }

            if ([string]::IsNullOrWhiteSpace([string] $info.CustomBuildBaseVersion)) {
                throw "ZIP 内の Info.json に CustomBuildBaseVersion がありません。"
            }

        }
        finally {
            $zip.Dispose()
        }
    }

    Write-BuildCompleted -OutputPath $outputZip -NextStep "作成された ZIP を UMM に入れて、ゲームを起動してください。"
}
finally {
    if ($null -eq $originalSolastaInstallDir) {
        Remove-Item Env:SolastaInstallDir -ErrorAction SilentlyContinue
    }
    else {
        $env:SolastaInstallDir = $originalSolastaInstallDir
    }

    if ($null -eq $originalUnityModManagerDir) {
        Remove-Item Env:UnityModManagerDir -ErrorAction SilentlyContinue
    }
    else {
        $env:UnityModManagerDir = $originalUnityModManagerDir
    }

    if ($temporaryWorktreeCreated) {
        Remove-TemporaryWorktree -RepositoryRoot $repoRoot -RepositoryGitPath $repoGitPath -WorktreePath $tempRepo
    }

    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
