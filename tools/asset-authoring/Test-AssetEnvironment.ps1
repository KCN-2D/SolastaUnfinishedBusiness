[CmdletBinding()]
param(
    [string]$BlenderPath,
    [string]$UnityPath,
    [string]$GameDirectory,
    [string]$GuiProjectPath,
    [switch]$RequireReadyEditor
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$requiredEditor = '2019.4.37f1'
$warnings = [Collections.Generic.List[string]]::new()
if (-not $BlenderPath) {
    $BlenderPath = Join-Path $PSScriptRoot 'workspace\Tools\blender-4.5.9-windows-x64\blender.exe'
}
$blenderVersion = $null
if (Test-Path -LiteralPath $BlenderPath -PathType Leaf) {
    $versionLines = @(& $BlenderPath --version)
    if ($LASTEXITCODE -ne 0) { throw 'Blender version probe failed.' }
    $blenderVersion = $versionLines[0]
}
$unityCandidates = @()
if ($UnityPath) {
    $unityCandidates = @($UnityPath)
}
else {
    $unityCandidates += (Join-Path $PSScriptRoot 'workspace\Tools\Unity-2019.4.37f1\Editor\Unity.exe')
    $hubRegistry = Join-Path $env:APPDATA 'UnityHub\editors-v2.json'
    if (Test-Path -LiteralPath $hubRegistry -PathType Leaf) {
        try {
            $registered = [IO.File]::ReadAllText($hubRegistry) | ConvertFrom-Json
            if ($registered.schema_version -ne '2') { throw 'Unsupported Hub editor registry schema.' }
            foreach ($editor in $registered.data) { $unityCandidates += @($editor.location) }
        }
        catch { $warnings.Add('The Hub editor registry could not be read: ' + $_.Exception.Message) }
    }
    $hubEditors = Join-Path $env:ProgramFiles 'Unity\Hub\Editor'
    if (Test-Path -LiteralPath $hubEditors -PathType Container) {
        $unityCandidates += @(Get-ChildItem -LiteralPath $hubEditors -Directory |
            ForEach-Object { Join-Path $_.FullName 'Editor\Unity.exe' })
    }
    $unityCandidates += (Join-Path $env:ProgramFiles 'Unity\Editor\Unity.exe')
}
$unityCli = Get-Command Unity.exe -ErrorAction SilentlyContinue
$editors = @($unityCandidates | Select-Object -Unique | Where-Object {
    Test-Path -LiteralPath $_ -PathType Leaf
} | ForEach-Object {
    $item = Get-Item -LiteralPath $_
    [ordered]@{
        path = $item.FullName
        product_version = $item.VersionInfo.ProductVersion
        matches_required_file_version = ($item.VersionInfo.ProductVersion -match
            ('^' + [regex]::Escape($requiredEditor) + '(?:_|$)'))
    }
})

# Installation metadata and an active Hub account do not prove that an Editor can run jobs.
# Use a fresh receipt emitted on the Editor's own main thread and bind it to the live process.
$projects = @()
if ($GuiProjectPath) {
    $projects = @([IO.Path]::GetFullPath($GuiProjectPath))
}
else {
    $workspace = Join-Path $PSScriptRoot 'workspace\UnityWorkspace'
    if (Test-Path -LiteralPath $workspace -PathType Container) {
        $projects = @(Get-ChildItem -LiteralPath $workspace -Directory | ForEach-Object {
            $candidate = Join-Path $_.FullName 'Project'
            if (Test-Path -LiteralPath (Join-Path $candidate '.solasta-asset-authoring-project') -PathType Leaf) { $candidate }
        })
    }
}
$template = Join-Path $PSScriptRoot 'UnityAssetBuild\Assets\Editor\AssetBundleBuilder.cs'
$sourceHash = (Get-FileHash -LiteralPath $template -Algorithm SHA256).Hash.ToLowerInvariant()
$workers = @(foreach ($project in $projects) {
    $entry = [ordered]@{
        project = $project; ready = $false; reason = 'No worker receipt.'
        process_id = $null; heartbeat_age_seconds = $null
        desktop = $null; receives_input = $null; isolated = $false
    }
    $receiptPath = Join-Path $project '.asset-authoring-jobs\worker.json'
    if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
        try {
            $receipt = [IO.File]::ReadAllText($receiptPath) | ConvertFrom-Json
            $process = Get-Process -Id $receipt.processId -ErrorAction Stop
            $entry.process_id = $process.Id
            $entry.heartbeat_age_seconds = [Math]::Round(([datetime]::UtcNow - ([datetime]$receipt.updatedUtc).ToUniversalTime()).TotalSeconds, 2)
            $processMatches = @($editors | Where-Object { $_.matches_required_file_version -and $_.path -eq $process.Path }).Count -gt 0 -and
                [Math]::Abs(($process.StartTime.ToUniversalTime() - ([datetime]$receipt.processStartedUtc).ToUniversalTime()).TotalSeconds) -lt 1
            if ($receipt.schemaVersion -ne 2 -or $receipt.unityVersion -ne $requiredEditor) { $entry.reason = 'Worker version does not match.' }
            elseif ($receipt.projectPath -ne $project -or $receipt.sourceSha256 -ne $sourceHash) { $entry.reason = 'Worker project or loaded source does not match.' }
            elseif (!$processMatches) { $entry.reason = 'Worker process identity does not match an installed Editor.' }
            elseif ($receipt.mainThread -lt 1) { $entry.reason = 'The worker callback thread is missing.' }
            elseif ($entry.heartbeat_age_seconds -lt 0 -or $entry.heartbeat_age_seconds -ge 10) { $entry.reason = 'Worker heartbeat is stale.' }
            else {
                $entry.ready = $true
                $entry.reason = 'Live Editor main-thread worker verified.'
                if ($receipt.PSObject.Properties['desktopQuerySucceeded'] -and $receipt.desktopQuerySucceeded) {
                    $entry.desktop = $receipt.desktop
                    $entry.receives_input = $receipt.desktopReceivesInput
                    $entry.isolated = $receipt.nativeMainThread -gt 0 -and
                        -not $receipt.desktopReceivesInput -and
                        -not [string]::IsNullOrEmpty($receipt.desktop) -and $receipt.desktop -ne $receipt.inputDesktop
                }
            }
        }
        catch { $entry.reason = 'Worker verification failed: ' + $_.Exception.Message }
    }
    [pscustomobject]$entry
})
$editorReady = @($workers | Where-Object ready).Count -gt 0
$game = $null
if ($GameDirectory) {
    $GameDirectory = (Resolve-Path -LiteralPath $GameDirectory).Path
    $exe = Get-Item -LiteralPath (Join-Path $GameDirectory 'Solasta.exe')
    $player = Get-Item -LiteralPath (Join-Path $GameDirectory 'UnityPlayer.dll')
    $managerFile = Join-Path $GameDirectory 'Solasta_Data\globalgamemanagers'
    $stream = [IO.File]::OpenRead($managerFile)
    try {
        $header = New-Object byte[] 4096
        $count = $stream.Read($header, 0, $header.Length)
        $headerText = [Text.Encoding]::UTF8.GetString($header, 0, $count)
        $match = [regex]::Match($headerText, '20\d{2}\.\d+\.\d+[abfp]\d+')
        $engineVersion = if ($match.Success) { $match.Value } else { $null }
    }
    finally { $stream.Dispose() }
    $game = [ordered]@{
        directory = $GameDirectory
        executable_product_version = $exe.VersionInfo.ProductVersion
        player_product_version = $player.VersionInfo.ProductVersion
        serialized_engine_version = $engineVersion
        engine_version_source = $managerFile
        required_asset_editor = '2019.4.37f1'
        matches_asset_editor = ($engineVersion -eq '2019.4.37f1')
    }
}
$python = Get-Command python.exe -ErrorAction SilentlyContinue
$pythonPath = if ($python) { $python.Source } else { $null }
[ordered]@{
    checked_at = [DateTimeOffset]::Now.ToString('o')
    blender_path = $BlenderPath
    blender_version = $blenderVersion
    unity_editors = $editors
    unity_cli_path = $(if ($unityCli) { $unityCli.Source } else { $null })
    required_asset_editor = $requiredEditor
    editor_worker_ready = $editorReady
    editor_non_input_worker_ready = @($workers | Where-Object { $_.ready -and $_.isolated }).Count -gt 0
    editor_workers = $workers
    editor_execution_scope = 'A live worker receipt verifies job readiness; installed Editor metadata and CLI account state alone do not. Desktop isolation must also be verified by the authoring launcher.'
    game = $game
    system_python_path = $pythonPath
    python_requirement = 'Use the Python bundled with Blender; system bpy is not required.'
    mutation = 'none'
    warnings = @($warnings)
} | ConvertTo-Json -Depth 6
if ($RequireReadyEditor -and !$editorReady) { throw 'No live matching Editor worker is ready. Start the non-input authoring Editor before submitting a job.' }
