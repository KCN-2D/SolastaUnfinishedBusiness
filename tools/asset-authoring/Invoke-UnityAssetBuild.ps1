[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $UnityPath,
    [Parameter(Mandatory = $true)] [string] $FbxPath,
    [string] $WorkspaceRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$requiredVersion = '2019.4.37f1'
$workspaceMarker = 'SolastaUBAssetAuthoring:UnityWorkspace:1'
$projectMarker = 'SolastaUBAssetAuthoring:UnityProject:1'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$authoringRoot = Join-Path $PSScriptRoot 'workspace'

function Test-WithinPath([string] $Child, [string] $Parent) {
    $fullChild = [IO.Path]::GetFullPath($Child).TrimEnd('\')
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\')
    return $fullChild.Equals($fullParent, [StringComparison]::OrdinalIgnoreCase) -or
        $fullChild.StartsWith($fullParent + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-LocalPath([string] $Path) {
    if ($Path.StartsWith('\\') -or $Path -notmatch '^[A-Za-z]:\\') {
        throw "A local Windows drive path is required: $Path"
    }
    $current = $Path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse-point paths are not accepted: $current"
            }
        }
        if (Test-Path -LiteralPath (Join-Path $current 'Solasta.exe') -PathType Leaf) {
            throw "A game installation cannot be used as an authoring workspace: $current"
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Assert-WorkspacePath([string] $Path) {
    Assert-LocalPath $Path
    if ($Path.TrimEnd('\') -eq [IO.Path]::GetPathRoot($Path).TrimEnd('\')) {
        throw 'A drive root cannot be used as a workspace.'
    }
    if ($Path -match '(?i)(^|[\\/])(Mods|Solasta_Data|Managed|StreamingAssets|\.git|\.codex|\.agents)([\\/]|$)') {
        throw 'Game, mod, managed-assembly and repository metadata directories cannot be used as output paths.'
    }
    if ((Test-WithinPath $Path $repoRoot) -and !(Test-WithinPath $Path $authoringRoot)) {
        throw "Inside this repository, the workspace must be within $authoringRoot."
    }
    foreach ($protected in @($env:WINDIR, $env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($protected -and (Test-WithinPath $Path $protected)) { throw "Protected installation directory: $protected" }
    }
    if (Test-Path -LiteralPath $Path) {
        if (!(Test-Path -LiteralPath $Path -PathType Container)) { throw 'WorkspaceRoot is not a directory.' }
        $marker = Join-Path $Path '.solasta-asset-authoring-workspace'
        if (Test-Path -LiteralPath $marker -PathType Leaf) {
            if ([IO.File]::ReadAllText($marker).Trim() -ne $workspaceMarker) { throw 'Unrecognized workspace marker.' }
        }
        elseif (@(Get-ChildItem -LiteralPath $Path -Force).Count -gt 0) {
            throw 'The existing workspace is unmarked and nonempty. Choose a new or empty directory.'
        }
    }
}

function Assert-UnityVersion([string] $Executable) {
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($Executable)
    if ($versionInfo.FileMajorPart -ne 2019 -or $versionInfo.FileMinorPart -ne 4 -or $versionInfo.FileBuildPart -ne 37) {
        throw "The executable file version is not 2019.4.37: '$($versionInfo.FileVersion)'. No Editor was started."
    }
    # Unity 2019.4 documents -version as printing the exact release without opening the Editor.
    # PE version resources may use four numeric fields and cannot establish the f1 release alone.
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $Executable
    $startInfo.Arguments = '-version'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $versionProcess = New-Object Diagnostics.Process
    $versionProcess.StartInfo = $startInfo
    try {
        if (!$versionProcess.Start()) { throw 'The Unity version query did not start.' }
        $standardOutput = $versionProcess.StandardOutput.ReadToEndAsync()
        $standardError = $versionProcess.StandardError.ReadToEndAsync()
        if (!$versionProcess.WaitForExit(15000)) {
            $versionProcess.Kill()
            throw 'The Unity -version query timed out; no project was opened.'
        }
        $exactVersion = $standardOutput.Result.Trim()
        $versionPattern = '^' + [regex]::Escape($requiredVersion) + '(?:\s*\([0-9a-fA-F]+\)|_[0-9a-fA-F]+)?$'
        if ($versionProcess.ExitCode -ne 0 -or $exactVersion -notmatch $versionPattern) {
            throw "The Unity -version query did not confirm $requiredVersion (output='$exactVersion'). No project was opened."
        }
        $null = $standardError.Result
    }
    finally {
        $versionProcess.Dispose()
    }
}

$unityExe = (Resolve-Path -LiteralPath $UnityPath).ProviderPath
$fbxFile = (Resolve-Path -LiteralPath $FbxPath).ProviderPath
if (!(Test-Path -LiteralPath $unityExe -PathType Leaf) -or [IO.Path]::GetFileName($unityExe) -ine 'Unity.exe') {
    throw '-UnityPath must identify an installed Unity.exe; this script does not install Unity.'
}
if (!(Test-Path -LiteralPath $fbxFile -PathType Leaf) -or [IO.Path]::GetExtension($fbxFile) -ine '.fbx') {
    throw '-FbxPath must identify an existing FBX file.'
}
Assert-UnityVersion $unityExe
if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    $WorkspaceRoot = Join-Path $authoringRoot 'UnityWorkspace'
}
$workspace = [IO.Path]::GetFullPath($WorkspaceRoot)
Assert-WorkspacePath $workspace
Assert-LocalPath $fbxFile
$markerFile = Join-Path $workspace '.solasta-asset-authoring-workspace'
$template = Join-Path $PSScriptRoot 'UnityAssetBuild'
$templateFiles = @(
    'ProjectSettings\ProjectVersion.txt',
    'Packages\manifest.json',
    'Assets\Editor\AssetBundleBuilder.cs'
)
foreach ($relative in $templateFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $template $relative) -PathType Leaf)) { throw "Missing template file: $relative" }
}

[IO.Directory]::CreateDirectory($workspace) | Out-Null
[IO.File]::WriteAllText($markerFile, $workspaceMarker + [Environment]::NewLine)
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$runRoot = Join-Path $workspace $runId
if (Test-Path -LiteralPath $runRoot) { throw 'The unique run directory already exists.' }
$project = Join-Path $runRoot 'Project'
[IO.Directory]::CreateDirectory($project) | Out-Null
foreach ($relative in $templateFiles) {
    $destination = Join-Path $project $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::Copy((Join-Path $template $relative), $destination, $false)
}
[IO.File]::WriteAllText((Join-Path $project '.solasta-asset-authoring-project'), $projectMarker)
$inputDirectory = Join-Path $project 'Assets\Input'
[IO.Directory]::CreateDirectory($inputDirectory) | Out-Null
$copiedFbx = Join-Path $inputDirectory 'SampleRig.fbx'
[IO.File]::Copy($fbxFile, $copiedFbx, $false)
$inputHash = (Get-FileHash -LiteralPath $fbxFile -Algorithm SHA256).Hash.ToLowerInvariant()
if ((Get-FileHash -LiteralPath $copiedFbx -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inputHash) {
    throw 'The copied FBX does not match its source.'
}
$bundleOutput = Join-Path $project 'BuildOutput'
$reportPath = Join-Path $bundleOutput 'unity-report.json'
$logPath = Join-Path $runRoot 'Editor.log'
$startedUtc = [DateTime]::UtcNow
$arguments = @(
    '-batchmode', '-nographics', '-quit', '-noUpm',
    '-projectPath', $project,
    '-executeMethod', 'SolastaAssetAuthoring.AssetBundleBuilder.Build',
    '-sampleFbx', $copiedFbx,
    '-bundleOutput', $bundleOutput,
    '-logFile', $logPath
)
# All paths are resolved Windows paths without quotes; none of these arguments ends in a backslash.
# Start-Process needs explicit quoting for spaces and waits even for the GUI Editor executable.
$argumentLine = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
# Calling an existing, licensed Editor is explicit. -noUpm disables package-manager activity.
$editorProcess = Start-Process -FilePath $unityExe -ArgumentList $argumentLine -Wait -PassThru -WindowStyle Hidden
$editorExitCode = $editorProcess.ExitCode
if ($editorExitCode -ne 0) { throw "Unity exited with code $editorExitCode. Inspect $logPath" }
if (!(Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw "Unity did not produce a success report. Inspect $logPath" }
if ((Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -lt $startedUtc) { throw 'The Unity report is stale.' }
$report = [IO.File]::ReadAllText($reportPath) | ConvertFrom-Json
if (!$report.success -or $report.stage -ne 'complete' -or $report.unityVersion -ne $requiredVersion) {
    throw "Unity validation did not complete successfully. Inspect $reportPath and $logPath"
}
if ($report.inputSha256 -ne $inputHash) { throw 'The report refers to a different input FBX.' }
$expectedBundle = Join-Path $bundleOutput 'ub_authoring_sample'
if ($report.bundlePath -ne $expectedBundle -or !(Test-Path -LiteralPath $expectedBundle -PathType Leaf)) {
    throw 'The reported AssetBundle path is not the expected isolated output.'
}
if ((Get-FileHash -LiteralPath $expectedBundle -Algorithm SHA256).Hash.ToLowerInvariant() -ne $report.bundleSha256) {
    throw 'The AssetBundle does not match the success report.'
}
Write-Output "Unity AssetBundle and reload validation succeeded: $reportPath"
Write-Output 'Game loading, animation events, avatar compatibility and multiplayer behavior remain unverified.'
