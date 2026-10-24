[CmdletBinding()]
param(
    [string]$BlenderPath,
    [string]$UnityPath,
    [string]$GameDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
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
    $hubEditors = Join-Path $env:ProgramFiles 'Unity\Hub\Editor'
    if (Test-Path -LiteralPath $hubEditors -PathType Container) {
        $unityCandidates += @(Get-ChildItem -LiteralPath $hubEditors -Directory |
            ForEach-Object { Join-Path $_.FullName 'Editor\Unity.exe' })
    }
    $unityCandidates += (Join-Path $env:ProgramFiles 'Unity\Editor\Unity.exe')
    $command = Get-Command Unity.exe -ErrorAction SilentlyContinue
    if ($command) { $unityCandidates += $command.Source }
}
$editors = @($unityCandidates | Select-Object -Unique | Where-Object {
    Test-Path -LiteralPath $_ -PathType Leaf
} | ForEach-Object {
    $item = Get-Item -LiteralPath $_
    [ordered]@{ path = $item.FullName; product_version = $item.VersionInfo.ProductVersion }
})
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
    game = $game
    system_python_path = $pythonPath
    python_requirement = 'Use the Python bundled with Blender; system bpy is not required.'
    mutation = 'none'
} | ConvertTo-Json -Depth 6
