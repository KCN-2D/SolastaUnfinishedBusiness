[CmdletBinding()]
param(
    [string]$BlenderPath,
    [string]$OutputRoot,
    [string]$PipelinePath,
    [ValidateNotNullOrEmpty()]
    [string[]]$Stages = @('generate', 'verify-static', 'verify-rig'),
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_-]*$')]
    [string]$ResultName,
    [ValidateRange(1, 64)]
    [int]$Threads = 2
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$workbench = Join-Path $PSScriptRoot 'workspace'
if (-not $BlenderPath) {
    $BlenderPath = Join-Path $workbench 'Tools\blender-4.5.9-windows-x64\blender.exe'
}
if (-not (Test-Path -LiteralPath $BlenderPath -PathType Leaf)) {
    throw "Blender is missing. Supply -BlenderPath; this script does not install software: $BlenderPath"
}
$BlenderPath = (Resolve-Path -LiteralPath $BlenderPath).Path
$versionLines = @(& $BlenderPath --version)
if ($LASTEXITCODE -ne 0 -or $versionLines.Count -eq 0 -or $versionLines[0] -ne 'Blender 4.5.9 LTS') {
    throw 'This workflow requires the verified Blender 4.5.9 LTS. See README.md.'
}
if (-not $OutputRoot) { $OutputRoot = Join-Path $workbench 'Output' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
# Keep all generated files in the disposable authoring workspace. Neither an
# installed game nor the mod source tree can be selected as an output target.
$workbenchPrefix = $workbench.TrimEnd('\') + '\'
if (-not $OutputRoot.StartsWith($workbenchPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must be below the authoring workspace: $workbench"
}
$relativeOutput = $OutputRoot.Substring($workbenchPrefix.Length)
if ($relativeOutput -match '(^|[\\/])(Tools|UnityWorkspace|Mods|Solasta_Data)([\\/]|$)') {
    throw 'OutputRoot overlaps software, Unity workspace, or game files.'
}
for ($ancestor = $OutputRoot; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
    if (Test-Path -LiteralPath $ancestor) {
        $item = Get-Item -LiteralPath $ancestor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "OutputRoot cannot pass through a junction or symlink: $ancestor"
        }
    }
}
if (-not $PipelinePath) { $PipelinePath = Join-Path $PSScriptRoot 'blender_pipeline.py' }
$pipeline = (Resolve-Path -LiteralPath $PipelinePath).Path
if (-not (Test-Path -LiteralPath $pipeline -PathType Leaf)) { throw "Missing pipeline: $pipeline" }
if ([IO.Path]::GetExtension($pipeline) -ne '.py') { throw 'PipelinePath must name a Python script.' }
if ($Stages.Count -eq 0 -or @($Stages | Where-Object { $_ -notmatch '^[a-z][a-z0-9_-]*$' }).Count -gt 0 -or
    @($Stages | Select-Object -Unique).Count -ne $Stages.Count) {
    throw 'Stages must contain distinct stage names using lowercase letters, numbers, underscores, or hyphens.'
}
$successFile = if ($ResultName) { "last-$ResultName-success.json" } else { 'last-success.json' }
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$runName = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$runDirectory = Join-Path $OutputRoot $runName
New-Item -ItemType Directory -Path $runDirectory | Out-Null
$run = [ordered]@{
    status = 'running'
    pipeline = $pipeline
    stages = $Stages
    blender = $versionLines[0]
    blender_executable = $BlenderPath
    pipeline_sha256 = (Get-FileHash -LiteralPath $pipeline -Algorithm SHA256).Hash
    validation_sha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'blender_pipeline.py') -Algorithm SHA256).Hash
    threads = $Threads
    output = $runDirectory
    completed_stages = @()
    stage_results = @()
    unity = 'not_run'
    game = 'not_run'
}
$runReport = Join-Path $runDirectory 'run.json'
$inputHashes = @{}
$inputHashes[$pipeline] = $run.pipeline_sha256
$inputHashes[(Join-Path $PSScriptRoot 'blender_pipeline.py')] = $run.validation_sha256
$archiveDirectory = Join-Path $runDirectory 'InputSources'
New-Item -ItemType Directory -Path $archiveDirectory | Out-Null
$run['input_archive'] = @()
$sourceIndex = 0
foreach ($inputPath in $inputHashes.Keys) {
    $sourceIndex++
    $archive = Join-Path $archiveDirectory ('{0:D2}-{1}' -f $sourceIndex, [IO.Path]::GetFileName($inputPath))
    Copy-Item -LiteralPath $inputPath -Destination $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $inputHashes[$inputPath]) { throw 'Archived authoring source hash mismatch.' }
    $run.input_archive += [ordered]@{source=$inputPath;archive=$archive;sha256=$inputHashes[$inputPath]}
}
function Assert-InputsUnchanged {
    foreach ($inputPath in $inputHashes.Keys) {
        if ((Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash -ne $inputHashes[$inputPath]) {
            throw "Authoring input changed during this run: $inputPath"
        }
    }
}
try {
    foreach ($stage in $stages) {
        $stageTimer = [Diagnostics.Stopwatch]::StartNew()
        Assert-InputsUnchanged
        Write-Host "Running $stage ($($versionLines[0]))"
        $blenderArgs = @(
            '--background', '--factory-startup', '--disable-autoexec', '--offline-mode',
            '--threads', [string]$Threads, '--python-exit-code', '1',
            '--python-expr', 'import sys; sys.dont_write_bytecode = True', '--python', $pipeline,
            '--', '--stage', $stage, '--output', $runDirectory
        )
        $stageLog = Join-Path $runDirectory ("reports\$stage.log")
        New-Item -ItemType Directory -Path (Split-Path -Parent $stageLog) -Force | Out-Null
        & $BlenderPath @blenderArgs 2>&1 | Tee-Object -FilePath $stageLog
        if ($LASTEXITCODE -ne 0) { throw "Blender stage '$stage' failed with exit code $LASTEXITCODE. Output: $runDirectory" }
        Assert-InputsUnchanged
        $stageReportPath = Join-Path $runDirectory "reports\$stage.json"
        if (-not (Test-Path -LiteralPath $stageReportPath -PathType Leaf)) {
            throw "Stage '$stage' produced no validation report."
        }
        $stageReport = Get-Content -LiteralPath $stageReportPath -Raw | ConvertFrom-Json
        if ($stageReport.status -ne 'passed' -or $stageReport.stage -ne $stage) {
            throw "Stage '$stage' did not report success."
        }
        $run.completed_stages += $stage
        $stageTimer.Stop()
        $run.stage_results += [ordered]@{stage=$stage;status=$stageReport.status;report=$stageReportPath;
            report_sha256=(Get-FileHash -LiteralPath $stageReportPath -Algorithm SHA256).Hash;
            log=$stageLog;elapsed_seconds=$stageTimer.Elapsed.TotalSeconds;measurements=$stageReport}
    }
    $run.status = 'passed'
}
catch {
    $run.status = 'failed'
    $run.error = $_.Exception.Message
    if ($stage -and $stage -notin $run.completed_stages) {
        $failedReport = Join-Path $runDirectory "reports\$stage.json"
        $measurements = $null
        if (Test-Path -LiteralPath $failedReport -PathType Leaf) {
            try { $measurements = Get-Content -LiteralPath $failedReport -Raw | ConvertFrom-Json } catch { }
        }
        $stageTimer.Stop()
        $run.stage_results += [ordered]@{stage=$stage;status='failed';report=$failedReport;
            log=$stageLog;elapsed_seconds=$stageTimer.Elapsed.TotalSeconds;measurements=$measurements}
    }
    throw
}
finally {
    $run['artifacts_sha256'] = @(Get-ChildItem -LiteralPath $runDirectory -File -Recurse |
        Where-Object { $_.FullName -ne $runReport } | ForEach-Object {
            [ordered]@{path=$_.FullName.Substring($runDirectory.Length + 1).Replace('\','/');
                sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
        })
    $run | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $runReport -Encoding UTF8
}
# Only successful runs become the current result. A failed rerun cannot make
# an old report or an old render look like a newly completed validation.
$run | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputRoot $successFile) -Encoding UTF8
Write-Host "Validated output: $runDirectory"
