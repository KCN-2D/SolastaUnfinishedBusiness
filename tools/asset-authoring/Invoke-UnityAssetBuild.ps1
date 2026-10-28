[CmdletBinding(DefaultParameterSetName = 'Rig')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Rig')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Materials')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Meshes')] [string] $UnityPath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Rig')] [string] $FbxPath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Materials')] [string] $MaterialRecipePath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Meshes')] [string] $MeshRecipePath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Cleanup')] [switch] $Cleanup,
    [string] $WorkspaceRoot,
    [string] $GuiProjectPath,
    [switch] $InstallGuiWorker,
    [switch] $StartBackgroundEditor,
    [string] $UnityCliPath,
    [string[]] $ReferencePath=@(),
    [string[]] $PreserveDirectory=@(),
    [ValidateRange(1,100)] [int] $KeepLatest=2,
    [ValidateRange(0,36500)] [double] $MaxAgeDays=0,
    [ValidateRange(0,9223372036854775807)] [long] $MaxRetainedBytes=0,
    [switch] $Apply,
    [ValidateRange(5, 3600)] [int] $TimeoutSeconds = 300
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

function Assert-RecipeFields($Value, [string[]] $Required, [string[]] $Optional = @()) {
    if ($null -eq $Value -or $Value -isnot [pscustomobject]) { throw 'A recipe object is required.' }
    $names = @($Value.PSObject.Properties.Name)
    if (@($names | Where-Object { $_ -notin ($Required + $Optional) }).Count -gt 0 -or
        @($Required | Where-Object { $_ -notin $names }).Count -gt 0) { throw 'Unknown or missing recipe field.' }
}

function Test-FiniteNumber($Value) {
    return ($Value -is [int] -or $Value -is [long] -or $Value -is [double] -or $Value -is [decimal]) -and
        ![double]::IsNaN([double]$Value) -and ![double]::IsInfinity([double]$Value)
}

function Assert-MeshNumbers($Values, [int] $Count, [string] $Label) {
    if ($null -eq $Values -or $Values -isnot [array] -or $Values.Count -ne $Count) { throw "Invalid mesh $Label length." }
    foreach ($value in $Values) {
        if (!(Test-FiniteNumber $value) -or [Math]::Abs([double]$value) -gt [single]::MaxValue) { throw "A mesh $Label component is not finite float32 data." }
    }
}

function Assert-MeshRecipe($Recipe) {
    Assert-RecipeFields $Recipe @('schemaVersion', 'bundleName', 'meshes') @('textures')
    if ($Recipe.schemaVersion -ne 1 -or $Recipe.bundleName -cnotmatch '^[a-z0-9][a-z0-9_.-]{0,127}$' -or
        $Recipe.meshes -isnot [array] -or $Recipe.meshes.Count -lt 1 -or $Recipe.meshes.Count -gt 128) { throw 'Invalid mesh recipe version, bundle name or mesh list.' }
    $names = @{}
    foreach ($mesh in $Recipe.meshes) {
        Assert-RecipeFields $mesh @('name', 'vertices', 'normals', 'tangents', 'uvChannels', 'subMeshes', 'bounds', 'indexFormat') @('colors', 'skin', 'blendShapes')
        if ($mesh.name -cnotmatch '^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$' -or $names.ContainsKey($mesh.name)) { throw 'Invalid or duplicate mesh name.' }
        $names[$mesh.name] = $true
        if ($mesh.vertices -isnot [array] -or $mesh.vertices.Count -lt 9 -or $mesh.vertices.Count -gt 3000000 -or $mesh.vertices.Count % 3 -ne 0) { throw 'Invalid mesh vertex count.' }
        $count = [int]($mesh.vertices.Count / 3)
        Assert-MeshNumbers $mesh.vertices ($count * 3) 'vertices'
        foreach ($channel in @(@('normals', 3), @('tangents', 4))) {
            $values = $mesh.($channel[0])
            if ($null -eq $values -or $values -isnot [array] -or $values.Count -notin @(0, ($count * $channel[1]))) { throw 'Invalid mesh channel length.' }
            Assert-MeshNumbers $values $values.Count $channel[0]
        }
        if ($mesh.PSObject.Properties['colors']) {
            if ($mesh.colors -isnot [array] -or $mesh.colors.Count -notin @(0, ($count * 4))) { throw 'Invalid mesh colors length.' }
            Assert-MeshNumbers $mesh.colors $mesh.colors.Count 'colors'
        }
        if ($mesh.indexFormat -cnotin @('UInt16', 'UInt32') -or ($mesh.indexFormat -ceq 'UInt16' -and $count -gt 65535)) { throw 'Invalid mesh index format or vertex range.' }
        Assert-RecipeFields $mesh.bounds @('center', 'size')
        Assert-MeshNumbers $mesh.bounds.center 3 'bounds center'; Assert-MeshNumbers $mesh.bounds.size 3 'bounds size'
        if (@($mesh.bounds.size | Where-Object { $_ -lt 0 }).Count) { throw 'Mesh bounds size cannot be negative.' }
        for ($axis = 0; $axis -lt 3; $axis++) {
            $center = [double][single]$mesh.bounds.center[$axis]; $size = [double][single]$mesh.bounds.size[$axis]
            $margin = ([Math]::Abs($center) + [Math]::Abs($size)) * 2.384185791015625e-7 + 2.802596928649634e-45
            for ($vertex = 0; $vertex -lt $count; $vertex++) {
                if ([Math]::Abs([double][single]$mesh.vertices[$vertex * 3 + $axis] - $center) -gt $size / 2 + $margin) { throw 'Mesh bounds do not enclose the vertices within float32 rounding.' }
            }
        }
        if ($mesh.uvChannels -isnot [array] -or $mesh.uvChannels.Count -gt 8) { throw 'Invalid mesh UV list.' }
        $uvs = @{}
        foreach ($uv in $mesh.uvChannels) {
            Assert-RecipeFields $uv @('channel', 'dimension', 'values')
            if (!(Test-FiniteNumber $uv.channel) -or $uv.channel -ne [Math]::Truncate($uv.channel) -or $uv.channel -lt 0 -or $uv.channel -gt 7 -or
                !(Test-FiniteNumber $uv.dimension) -or $uv.dimension -cnotin @(2, 3, 4) -or $uvs.ContainsKey([int]$uv.channel)) { throw 'Invalid or duplicate mesh UV channel/dimension.' }
            $uvs[[int]$uv.channel] = $true
            Assert-MeshNumbers $uv.values ($count * $uv.dimension) 'UV'
        }
        if ($mesh.subMeshes -isnot [array] -or $mesh.subMeshes.Count -lt 1 -or $mesh.subMeshes.Count -gt 256) { throw 'Invalid mesh submesh list.' }
        foreach ($submesh in $mesh.subMeshes) {
            Assert-RecipeFields $submesh @('topology', 'indices')
            if ($submesh.topology -cne 'Triangles' -or $submesh.indices -isnot [array] -or $submesh.indices.Count -lt 3 -or
                $submesh.indices.Count -gt 6000000 -or $submesh.indices.Count % 3) { throw 'Invalid mesh triangle list.' }
            foreach ($index in $submesh.indices) {
                if (!(Test-FiniteNumber $index) -or $index -ne [Math]::Truncate($index) -or $index -lt 0 -or $index -ge $count) { throw 'A mesh index is outside its vertex range.' }
            }
        }
        if ($mesh.PSObject.Properties['skin'] -and $null -ne $mesh.skin) {
            $skin = $mesh.skin
            Assert-RecipeFields $skin @('bindPoses', 'boneWeights', 'boneIndices')
            if ($skin.bindPoses -isnot [array] -or $skin.bindPoses.Count -lt 16 -or $skin.bindPoses.Count % 16) { throw 'Invalid mesh bind pose count.' }
            Assert-MeshNumbers $skin.bindPoses $skin.bindPoses.Count 'bind poses'
            Assert-MeshNumbers $skin.boneWeights ($count * 4) 'bone weights'
            $bones = [int]($skin.bindPoses.Count / 16)
            if ($skin.boneIndices -isnot [array] -or $skin.boneIndices.Count -ne $count * 4) { throw 'Invalid mesh bone index count.' }
            foreach ($index in $skin.boneIndices) {
                if (!(Test-FiniteNumber $index) -or $index -ne [Math]::Truncate($index) -or $index -lt 0 -or $index -ge $bones) { throw 'A mesh bone index is outside its bind pose range.' }
            }
            for ($vertex = 0; $vertex -lt $count; $vertex++) {
                $total = 0.0
                for ($influence = 0; $influence -lt 4; $influence++) {
                    $weight = [single]$skin.boneWeights[$vertex * 4 + $influence]
                    if ($weight -lt 0 -or $weight -gt 1 -or ($influence -gt 0 -and [single]$skin.boneWeights[$vertex * 4 + $influence - 1] -lt $weight)) { throw 'Mesh bone weights must be nonnegative, at most one and descending.' }
                    $total += $weight
                }
                if ([Math]::Abs($total - 1.0) -gt 0.00001) { throw 'Mesh bone weights must sum to one.' }
            }
        }
        if ($mesh.PSObject.Properties['blendShapes'] -and $null -ne $mesh.blendShapes) {
            if ($mesh.blendShapes -isnot [array]) { throw 'Invalid mesh blend shape list.' }
            foreach ($shape in $mesh.blendShapes) {
                Assert-RecipeFields $shape @('name', 'frames')
                if ($shape.name -isnot [string] -or [string]::IsNullOrWhiteSpace($shape.name) -or $shape.name -match '\p{Cc}') { throw 'Invalid mesh blend shape name.' }
                if ($shape.frames -isnot [array] -or $shape.frames.Count -lt 1) { throw 'Missing mesh blend shape frames.' }
                $previous = [double]::NegativeInfinity
                foreach ($frame in $shape.frames) {
                    Assert-RecipeFields $frame @('weight', 'vertices', 'normals', 'tangents')
                    if (!(Test-FiniteNumber $frame.weight) -or [Math]::Abs([double]$frame.weight) -gt [single]::MaxValue -or [single]$frame.weight -le $previous) { throw 'Mesh blend shape frame weights must be finite and strictly increasing.' }
                    $previous = [single]$frame.weight
                    foreach ($channel in @('vertices', 'normals', 'tangents')) { Assert-MeshNumbers $frame.$channel ($count * 3) ('blend shape ' + $channel) }
                }
            }
        }
    }
    if ($Recipe.PSObject.Properties['textures']) {
        if ($Recipe.textures -isnot [array] -or $Recipe.textures.Count -gt 128) { throw 'Invalid mesh texture list.' }
        $textureNames = @{}; $texturePaths = @{}
        foreach ($texture in $Recipe.textures) {
            Assert-RecipeFields $texture @('name', 'path', 'sha256', 'normalMap', 'sRGB', 'mipmaps')
            if ($texture.name -isnot [string] -or $texture.name -cnotmatch '^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$' -or
                $texture.path -isnot [string] -or $texture.path -cnotmatch '^[A-Za-z0-9_./-]+$' -or $texture.path.StartsWith('/') -or
                @($texture.path.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0 -or
                [IO.Path]::GetExtension($texture.path) -cnotin @('.png', '.jpg', '.jpeg', '.tga') -or
                [IO.Path]::GetFileNameWithoutExtension($texture.path) -cne $texture.name -or
                $texture.sha256 -isnot [string] -or $texture.sha256 -cnotmatch '^[a-f0-9]{64}$' -or
                $texture.normalMap -isnot [bool] -or $texture.sRGB -isnot [bool] -or $texture.mipmaps -isnot [bool] -or
                ($texture.normalMap -and $texture.sRGB) -or $textureNames.ContainsKey($texture.name) -or $texturePaths.ContainsKey($texture.path)) {
                throw 'Invalid or duplicate mesh texture name, path, hash or import settings.'
            }
            $textureNames[$texture.name] = $true; $texturePaths[$texture.path] = $true
        }
    }
}

if ($Cleanup) {
    if ([string]::IsNullOrWhiteSpace($GuiProjectPath)) { throw 'Cleanup requires -GuiProjectPath.' }
    . (Join-Path $PSScriptRoot 'UnityAssetBuild/BackgroundEditor.ps1')
    Clear-AssetAuthoringJobs -Project $GuiProjectPath -ReferencePath $ReferencePath -PreserveDirectory $PreserveDirectory -KeepLatest $KeepLatest -MaxAgeDays $MaxAgeDays -MaxRetainedBytes $MaxRetainedBytes -Apply:$Apply
    return
}
if ($Apply -or $ReferencePath.Count -or $PreserveDirectory.Count -or $MaxAgeDays -gt 0 -or $MaxRetainedBytes -gt 0) { throw 'Storage policy arguments require -Cleanup.' }
. (Join-Path $PSScriptRoot 'UnityAssetBuild/BackgroundEditor.ps1')
$storageOperation=if($GuiProjectPath){Enter-AssetStorageOperation $GuiProjectPath}else{$null}
try {
$unityExe = (Resolve-Path -LiteralPath $UnityPath).ProviderPath
$materialJob = $PSCmdlet.ParameterSetName -eq 'Materials'
$meshJob = $PSCmdlet.ParameterSetName -eq 'Meshes'
$sourceFile = (Resolve-Path -LiteralPath $(if ($meshJob) { $MeshRecipePath } elseif ($materialJob) { $MaterialRecipePath } else { $FbxPath })).ProviderPath
if (!(Test-Path -LiteralPath $unityExe -PathType Leaf) -or [IO.Path]::GetFileName($unityExe) -ine 'Unity.exe') {
    throw '-UnityPath must identify an installed Unity.exe; this script does not install Unity.'
}
if (!$materialJob -and !$meshJob -and (!(Test-Path -LiteralPath $sourceFile -PathType Leaf) -or [IO.Path]::GetExtension($sourceFile) -ine '.fbx')) {
    throw '-FbxPath must identify an existing FBX file.'
}
Assert-UnityVersion $unityExe
if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    $WorkspaceRoot = Join-Path $authoringRoot 'UnityWorkspace'
}
$workspace = [IO.Path]::GetFullPath($WorkspaceRoot)
Assert-WorkspacePath $workspace
Assert-LocalPath $sourceFile
$inputName = if ($meshJob) { 'mesh-recipe.json' } elseif ($materialJob) { 'recipe.json' } else { 'SampleRig.fbx' }
$bundleName = 'ub_authoring_sample'
$materialInputs = @()
if ($materialJob) {
    if ([IO.Path]::GetExtension($sourceFile) -ine '.json') { throw '-MaterialRecipePath must identify a JSON recipe.' }
    $recipe = [IO.File]::ReadAllText($sourceFile) | ConvertFrom-Json
    Assert-RecipeFields $recipe @('schemaVersion', 'bundleName', 'assets', 'materials')
    if ($recipe.schemaVersion -ne 1 -or $recipe.bundleName -cnotmatch '^[a-z0-9][a-z0-9_.-]{0,127}$' -or
        !$recipe.assets -or !$recipe.materials) { throw 'Invalid material recipe version, bundle name or asset lists.' }
    $bundleName = $recipe.bundleName
    $seen = @{}
    foreach ($asset in $recipe.assets) {
        Assert-RecipeFields $asset @('path', 'sha256')
        if ($asset.path -notmatch '^[A-Za-z0-9_./-]+$' -or $asset.path.StartsWith('/') -or
            @($asset.path.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0 -or
            [IO.Path]::GetExtension($asset.path) -cnotin @('.shader', '.cginc', '.hlsl', '.png', '.jpg', '.jpeg', '.tga') -or
            $asset.sha256 -cnotmatch '^[a-f0-9]{64}$' -or $seen.ContainsKey($asset.path)) { throw 'Invalid or duplicate material asset path/hash.' }
        $seen[$asset.path] = $true
        $assetSource = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($sourceFile)) $asset.path))
        Assert-LocalPath $assetSource
        if ((Get-FileHash -LiteralPath $assetSource -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) {
            throw "The material source hash differs: $($asset.path)"
        }
        $materialInputs += [pscustomobject]@{ path = $asset.path; source = $assetSource; sha256 = $asset.sha256 }
    }
    foreach ($material in $recipe.materials) {
        Assert-RecipeFields $material @('name', 'shader', 'requiredPasses') @('renderQueue', 'properties')
        if ($material.PSObject.Properties['renderQueue'] -and (!(Test-FiniteNumber $material.renderQueue) -or
            $material.renderQueue -ne [Math]::Truncate($material.renderQueue) -or $material.renderQueue -lt -1 -or $material.renderQueue -gt 5000)) {
            throw 'The material render queue must be an integer from -1 through 5000.'
        }
        if ($material.PSObject.Properties['properties']) {
            foreach ($property in $material.properties) {
                if ($property.kind -eq 'float') {
                    Assert-RecipeFields $property @('name', 'kind', 'value')
                    if (!(Test-FiniteNumber $property.value)) { throw 'A material float must be a finite number.' }
                }
                elseif ($property.kind -eq 'color') {
                    Assert-RecipeFields $property @('name', 'kind', 'rgba')
                    if (@($property.rgba).Count -ne 4 -or @($property.rgba | Where-Object { !(Test-FiniteNumber $_) }).Count -ne 0) {
                        throw 'A material color must have four finite numeric components.'
                    }
                }
                elseif ($property.kind -eq 'texture') { Assert-RecipeFields $property @('name', 'kind', 'texture') }
                else { throw 'Unknown material property kind.' }
            }
        }
    }
}

$meshInputBytes = $null
$meshInputHash = $null
if ($meshJob) {
    if ([IO.Path]::GetExtension($sourceFile) -ine '.json' -or (Get-Item -LiteralPath $sourceFile).Length -gt 67108864) { throw '-MeshRecipePath must identify a JSON recipe of at most 64 MiB.' }
    $meshInputBytes = [IO.File]::ReadAllBytes($sourceFile)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $meshInputHash = ([BitConverter]::ToString($sha.ComputeHash($meshInputBytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    $recipe = [Text.Encoding]::UTF8.GetString($meshInputBytes).TrimStart([char]0xfeff) | ConvertFrom-Json
    Assert-MeshRecipe $recipe
    $bundleName = $recipe.bundleName
    if ($recipe.PSObject.Properties['textures']) {
        foreach ($texture in $recipe.textures) {
            $assetSource = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($sourceFile)) $texture.path))
            Assert-LocalPath $assetSource
            if ((Get-FileHash -LiteralPath $assetSource -Algorithm SHA256).Hash.ToLowerInvariant() -ne $texture.sha256) {
                throw "The mesh texture source hash differs: $($texture.path)"
            }
            $materialInputs += [pscustomobject]@{ path = $texture.path; source = $assetSource; sha256 = $texture.sha256 }
        }
    }
}

function Copy-AuthoringInput([string] $Destination) {
    if ($meshJob) {
        if ((Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $meshInputHash) { throw 'The mesh recipe changed after its validated snapshot.' }
        [IO.File]::WriteAllBytes($Destination, $meshInputBytes)
    }
    else { [IO.File]::Copy($sourceFile, $Destination, $false) }
}

function Copy-MaterialInputs([string] $InputDirectory) {
    foreach ($asset in $materialInputs) {
        $destination = Join-Path $InputDirectory $asset.path
        Assert-LocalPath $destination
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy($asset.source, $destination, $false)
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) {
            throw 'A copied material asset differs from its source.'
        }
    }
}
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

if ($InstallGuiWorker -and [string]::IsNullOrWhiteSpace($GuiProjectPath)) {
    throw '-InstallGuiWorker requires -GuiProjectPath.'
}
if ($StartBackgroundEditor -and [string]::IsNullOrWhiteSpace($GuiProjectPath)) {
    throw '-StartBackgroundEditor requires -GuiProjectPath.'
}
$backgroundSession = $null
. (Join-Path $template 'BackgroundEditor.ps1')
$jobOwnership=$null;$jobSucceeded=$false;$jobFailure=$null
try {

if (![string]::IsNullOrWhiteSpace($GuiProjectPath)) {
    $project = (Resolve-Path -LiteralPath $GuiProjectPath).ProviderPath
    Assert-LocalPath $project
    if (!(Test-WithinPath $project $workspace) -or $project -eq $workspace) {
        throw '-GuiProjectPath must identify a project inside the marked WorkspaceRoot.'
    }
    $projectMarkerPath = Join-Path $project '.solasta-asset-authoring-project'
    if (!(Test-Path -LiteralPath $projectMarkerPath -PathType Leaf) -or
        [IO.File]::ReadAllText($projectMarkerPath).Trim() -ne $projectMarker) { throw 'The GUI project is unmarked.' }
    $versionFile = Join-Path $project 'ProjectSettings\ProjectVersion.txt'
    if ([IO.File]::ReadAllText($versionFile) -notmatch ('(?m)^m_EditorVersion: ' + [regex]::Escape($requiredVersion) + '\s*$')) {
        throw 'The GUI project uses a different Editor version.'
    }
    $source = Join-Path $template 'Assets\Editor\AssetBundleBuilder.cs'
    $destination = Join-Path $project 'Assets\Editor\AssetBundleBuilder.cs'
    Assert-LocalPath $destination
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    if (!(Test-Path -LiteralPath $destination -PathType Leaf) -or
        (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sourceHash) {
        if (!$InstallGuiWorker) { throw 'The GUI worker differs from the template. Use -InstallGuiWorker once, then refresh the project in the Editor.' }
        $backup = Join-Path $project ('.asset-authoring-jobs\WorkerBackups\' + [guid]::NewGuid().ToString('N'))
        Assert-LocalPath $backup
        [IO.Directory]::CreateDirectory($backup) | Out-Null
        if (Test-Path -LiteralPath $destination -PathType Leaf) {
            [IO.File]::Copy($destination, (Join-Path $backup 'AssetBundleBuilder.cs'), $false)
        }
        [IO.File]::Copy($source, $destination, $true)
        Write-Output 'GUI worker updated. If it is not loaded yet, select Assets > Refresh in the open Editor once.'
    }
    if ($StartBackgroundEditor) {
        if (!$UnityCliPath) { $UnityCliPath = Join-Path $env:LOCALAPPDATA 'Unity\bin\unity.exe' }
        $cli = (Resolve-Path -LiteralPath $UnityCliPath).ProviderPath
        Assert-LocalPath $cli
        . (Join-Path $template 'BackgroundEditor.ps1')
        $backgroundSession = Start-AssetBackgroundEditor -Project $project -Editor $unityExe -Cli $cli -SourceSha256 $sourceHash -TimeoutSeconds $TimeoutSeconds
    }
    $readyPath = Join-Path $project '.asset-authoring-jobs\worker.json'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $ready = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $readyPath -PathType Leaf) {
            try {
                $candidate = [IO.File]::ReadAllText($readyPath) | ConvertFrom-Json
                $process = Get-Process -Id $candidate.processId -ErrorAction Stop
                if ($candidate.schemaVersion -eq 2 -and $candidate.unityVersion -eq $requiredVersion -and
                    $candidate.sourceSha256 -eq $sourceHash -and $candidate.projectPath -eq $project -and
                    [Math]::Abs(($process.StartTime.ToUniversalTime() - ([DateTime]$candidate.processStartedUtc).ToUniversalTime()).TotalSeconds) -lt 1 -and
                    $process.Path -eq $unityExe -and
                    ([DateTime]::UtcNow - ([DateTime]$candidate.updatedUtc).ToUniversalTime()).TotalSeconds -lt 10) {
                    $ready = $candidate
                    break
                }
            }
            catch { $ready = $null }
        }
        Start-Sleep -Milliseconds 500
    }
    if ($null -eq $ready) { throw "The GUI worker is not ready. Inspect the Editor log and the marked project: $project" }
    $jobId = [guid]::NewGuid().ToString('N')
    $runRoot = Join-Path $project ('.asset-authoring-jobs\' + $jobId)
    $inputDirectory = Join-Path $project ('Assets\AuthoringJobs\' + $jobId + '\Input')
    Assert-LocalPath $runRoot
    Assert-LocalPath $inputDirectory
    if ((Test-Path -LiteralPath $runRoot) -or (Test-Path -LiteralPath $inputDirectory)) { throw 'The unique GUI job already exists.' }
    [IO.Directory]::CreateDirectory($runRoot) | Out-Null
    [IO.Directory]::CreateDirectory($inputDirectory) | Out-Null
    [IO.File]::WriteAllText((Join-Path $runRoot '.solasta-asset-authoring-job'), 'SolastaUBAssetAuthoring:UnityJob:1')
    $copiedInput = Join-Path $inputDirectory $inputName
    Copy-AuthoringInput $copiedInput
    Copy-MaterialInputs $inputDirectory
    $inputHash = (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ((Get-FileHash -LiteralPath $copiedInput -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inputHash) { throw 'The copied authoring input differs from the source.' }
    $bundleOutput = Join-Path $runRoot 'Output'
    $reportPath = Join-Path $bundleOutput 'unity-report.json'
    $logPath = $reportPath
    $startedUtc = [DateTime]::UtcNow
    $jobOwnership=Register-AssetAuthoringJob -Project $project -JobDirectory $runRoot -AssetDirectory (Split-Path -Parent $inputDirectory) -ResultPath $reportPath -DependencyPath (@($sourceFile)+@($materialInputs|ForEach-Object source)) -Session $backgroundSession
    $request = @{ schemaVersion = 1; jobId = $jobId; inputSha256 = $inputHash; kind = $(if ($meshJob) { 'mesh-templates' } elseif ($materialJob) { 'materials' } else { 'rig' }) } | ConvertTo-Json
    $temporaryRequest = Join-Path $runRoot 'request.writing'
    [IO.File]::WriteAllText($temporaryRequest, $request)
    [IO.File]::Move($temporaryRequest, (Join-Path $runRoot 'request.json'))
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (!(Test-Path -LiteralPath $reportPath -PathType Leaf) -and [DateTime]::UtcNow -lt $deadline) {
        $process = Get-Process -Id $ready.processId -ErrorAction SilentlyContinue
        if ($null -eq $process -or
            [Math]::Abs(($process.StartTime.ToUniversalTime() - ([DateTime]$ready.processStartedUtc).ToUniversalTime()).TotalSeconds) -ge 1) {
            throw "The GUI worker process ended. Job retained: $runRoot"
        }
        Start-Sleep -Milliseconds 500
    }
    if (!(Test-Path -LiteralPath $reportPath -PathType Leaf)) {
        throw "The GUI job timed out. Inspect $runRoot"
    }
}
else {

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
    $storageOperation=Enter-AssetStorageOperation $project
    $inputDirectory = Join-Path $project 'Assets\Input'
    [IO.Directory]::CreateDirectory($inputDirectory) | Out-Null
    $copiedInput = Join-Path $inputDirectory $inputName
    Copy-AuthoringInput $copiedInput
    Copy-MaterialInputs $inputDirectory
    $inputHash = (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ((Get-FileHash -LiteralPath $copiedInput -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inputHash) {
        throw 'The copied authoring input does not match its source.'
    }
    $bundleOutput = Join-Path $project 'BuildOutput'
    $reportPath = Join-Path $bundleOutput 'unity-report.json'
    $logPath = Join-Path $runRoot 'Editor.log'
    $startedUtc = [DateTime]::UtcNow
    $jobOwnership=Register-AssetAuthoringJob -Project $project -JobDirectory $runRoot -AssetDirectory (Join-Path $project 'Assets') -ResultPath $reportPath -DependencyPath (@($sourceFile)+@($materialInputs|ForEach-Object source)) -DisposableProject
    $arguments = @(
        '-batchmode', '-nographics', '-quit', '-noUpm',
        '-projectPath', $project,
        '-executeMethod', 'SolastaAssetAuthoring.AssetBundleBuilder.Build',
        $(if ($meshJob) { '-meshRecipe' } elseif ($materialJob) { '-materialRecipe' } else { '-sampleFbx' }), $copiedInput,
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
}
if (!(Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw "Unity did not produce a success report. Inspect $logPath" }
if ((Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -lt $startedUtc) { throw 'The Unity report is stale.' }
$report = [IO.File]::ReadAllText($reportPath) | ConvertFrom-Json
if (!$report.success -or $report.stage -ne 'complete' -or $report.unityVersion -ne $requiredVersion) {
    throw "Unity validation did not complete successfully. Inspect $reportPath and $logPath"
}
if ($report.inputSha256 -ne $inputHash) { throw 'The report refers to a different authoring input.' }
if (![string]::IsNullOrWhiteSpace($GuiProjectPath) -and
    ($report.transport -ne 'gui_worker' -or $report.jobId -ne $jobId -or $report.processId -ne $ready.processId -or $report.mainThread -ne $ready.mainThread)) {
    throw 'The report does not identify the selected GUI worker job and main thread.'
}
$expectedBundle = Join-Path $bundleOutput $bundleName
if ($report.bundlePath -ne $expectedBundle -or !(Test-Path -LiteralPath $expectedBundle -PathType Leaf)) {
    throw 'The reported AssetBundle path is not the expected isolated output.'
}
if ((Get-FileHash -LiteralPath $expectedBundle -Algorithm SHA256).Hash.ToLowerInvariant() -ne $report.bundleSha256) {
    throw 'The AssetBundle does not match the success report.'
}
if ((Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inputHash -or
    (Get-FileHash -LiteralPath $copiedInput -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inputHash) {
    throw 'The source or copied authoring input changed during the build.'
}
foreach ($asset in $materialInputs) {
    if ((Get-FileHash -LiteralPath $asset.source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256 -or
        (Get-FileHash -LiteralPath (Join-Path $inputDirectory $asset.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) {
        throw 'A source or copied material asset changed during the build.'
    }
}
if ($meshJob -and ($report.validationScope -ne 'mesh_templates' -or @($report.meshes).Count -ne @($recipe.meshes).Count -or
    @($report.meshes | Where-Object { !$_.exact }).Count -ne 0)) { throw 'The report does not contain the requested exact mesh validation.' }
if ($meshJob -and $recipe.PSObject.Properties['textures'] -and $recipe.textures.Count -gt 0) {
    foreach ($stage in @('createdTextures', 'savedTextures', 'textures')) {
        if (!$report.PSObject.Properties[$stage] -or @($report.$stage).Count -ne $recipe.textures.Count -or
            @($report.$stage | Where-Object { !$_.exact -or !$_.importSettingsExact }).Count -ne 0) {
            throw 'The report does not contain the requested exact texture validation.'
        }
        for ($index = 0; $index -lt $recipe.textures.Count; $index++) {
            $row = $report.$stage[$index]; $spec = $recipe.textures[$index]; $created = $report.createdTextures[$index]
            if ($row.name -cne $spec.name -or $row.path -cne $spec.path -or $row.inputSha256 -cne $spec.sha256 -or
                $row.normalMap -ne $spec.normalMap -or $row.sRGB -ne $spec.sRGB -or $row.mipmaps -ne $spec.mipmaps -or
                $row.rawSha256 -cne $created.rawSha256 -or $row.graphicsFormat -cne $created.graphicsFormat -or
                $row.width -ne $created.width -or $row.height -ne $created.height -or $row.mipCount -ne $created.mipCount) {
                throw 'The texture report identity, import settings or raw payload changed.'
            }
        }
    }
}
if ($materialJob -and ($report.validationScope -ne 'materials' -or @($report.materials).Count -ne @($recipe.materials).Count)) {
    throw 'The report does not contain the requested material validation.'
}
$jobSucceeded=$true
}
catch {$jobFailure=$_.Exception.Message;throw}
finally {
    try {if ($backgroundSession) {Stop-AssetBackgroundEditor $backgroundSession}}
    catch {$jobSucceeded=$false;$jobFailure=$_.Exception.Message;throw}
    finally {if ($jobOwnership) {Complete-AssetAuthoringJob -Ownership $jobOwnership -Succeeded $jobSucceeded -Failure $jobFailure}}
}
Write-Output "Unity AssetBundle and reload validation succeeded: $reportPath"
if ($backgroundSession) { Write-Output "Background Editor isolation and normal shutdown verified: $($backgroundSession.RunDirectory)" }
Write-Output 'Game loading, animation events, avatar compatibility and multiplayer behavior remain unverified.'

}finally{if($storageOperation){$storageOperation.ReleaseMutex();$storageOperation.Dispose()}}
