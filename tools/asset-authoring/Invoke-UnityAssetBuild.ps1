[CmdletBinding(DefaultParameterSetName = 'Rig')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Rig')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Materials')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Meshes')]
    [Parameter(Mandatory = $true, ParameterSetName = 'MaterialFields')] [string] $UnityPath,
    [Parameter(ParameterSetName = 'Rig')]
    [Parameter(ParameterSetName = 'Materials')]
    [Parameter(ParameterSetName = 'Meshes')]
    [ValidateSet('Gamma', 'Linear')] [string] $ColorSpace = 'Linear',
    [Parameter(Mandatory = $true, ParameterSetName = 'Rig')] [string] $FbxPath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Materials')] [string] $MaterialRecipePath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Meshes')]
    [ValidateCount(1, 16)] [string[]] $MeshRecipePath,
    [Parameter(Mandatory = $true, ParameterSetName = 'MaterialFields')] [string] $MaterialFieldInputPath,
    [Parameter(ParameterSetName = 'Rig')]
    [Parameter(ParameterSetName = 'Materials')]
    [Parameter(ParameterSetName = 'Meshes')]
    [Parameter(ParameterSetName = 'MaterialFields')] [switch] $ValidateOnly,
    [Parameter(Mandatory = $true, ParameterSetName = 'Cleanup')] [switch] $Cleanup,
    [string] $WorkspaceRoot,
    [string] $GuiProjectPath,
    [switch] $InstallGuiWorker,
    [switch] $StartBackgroundEditor,
    [string] $UnityCliPath,
    [string[]] $ReferencePath=@(),
    [string[]] $PreserveDirectory=@(),
    [ValidateRange(0,100)] [int] $KeepLatest=2,
    [ValidateRange(0,36500)] [double] $MaxAgeDays=0,
    [ValidateRange(0,9223372036854775807)] [long] $MaxRetainedBytes=0,
    [switch] $Apply,
    [ValidateRange(5, 3600)] [int] $TimeoutSeconds = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ColorSpace = if ($ColorSpace -ieq 'Linear') { 'Linear' } else { 'Gamma' }
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

function Assert-LocalPath([string] $Path, [switch] $UnityImport) {
    if ($UnityImport -and ($Path + '.meta').Length -gt 259) {
        throw "Unity 2019 import path is too long ($($Path.Length + 5) characters including .meta; maximum 259). Shorten the project/workspace path or asset relative names: $Path"
    }
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
        $Recipe.meshes -isnot [array] -or $Recipe.meshes.Count -gt 128) { throw 'Invalid mesh recipe version, bundle name or mesh list.' }
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
            Assert-RecipeFields $texture @('name', 'path', 'sha256', 'normalMap', 'sRGB', 'mipmaps') @('crunchedCompression', 'compressionQuality')
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
            if (($texture.PSObject.Properties['crunchedCompression'] -and $texture.crunchedCompression -isnot [bool]) -or
                ($texture.PSObject.Properties['compressionQuality'] -and
                    ($texture.compressionQuality -isnot [int] -and $texture.compressionQuality -isnot [long] -or
                     $texture.compressionQuality -lt 0 -or $texture.compressionQuality -gt 100))) {
                throw 'Texture compressionQuality must be an integer from 0 to 100 and crunchedCompression must be boolean.'
            }
            $textureNames[$texture.name] = $true; $texturePaths[$texture.path] = $true
        }
    }
    $textureCount = if ($Recipe.PSObject.Properties['textures']) { $Recipe.textures.Count } else { 0 }
    if ($Recipe.meshes.Count -eq 0 -and $textureCount -eq 0) { throw 'The mesh recipe must contain a mesh or texture.' }
}

function Read-ExternalMeshRecipe([string] $Path, [string] $InputOwnerPath) {
    $source = if ($InputOwnerPath) { $InputOwnerPath } else { Join-Path $PSScriptRoot 'UnityAssetBuild/Assets/Editor/AssetBundleBuilder.cs' }
    $bytes = [IO.File]::ReadAllBytes($source)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $identity = ([BitConverter]::ToString($hash.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose() }
    $namespace = 'SolastaAssetInput_' + $identity
    $typeName = $namespace + '.MeshRecipeInput'
    $codec = $typeName -as [type]
    if ($null -eq $codec) {
        $text = [Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xfeff).Replace('namespace SolastaAssetAuthoring', ('namespace ' + $namespace))
        $arguments = @{TypeDefinition=('#define MESH_INPUT_ONLY' + [Environment]::NewLine + $text); Language='CSharp'; ErrorAction='Stop'}
        if ($PSVersionTable.PSVersion.Major -lt 6) { $arguments.ReferencedAssemblies = @('System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll') }
        Add-Type @arguments
        $codec = $typeName -as [type]
    }
    return $codec::ReadFile($Path)
}

function Install-AssetGuiWorker {
    param([string]$Project,[string]$Source,[string]$SourceHash,[switch]$AllowInstall)
    Assert-LocalPath $Source
    $identitySource=Get-AssetWorkerIdentitySource $SourceHash
    if (!(Test-Path -LiteralPath $Source -PathType Leaf) -or
        (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash.ToLowerInvariant() -cne $SourceHash) {
        throw 'The desired GUI worker changed before installation.'
    }
    $destination=Join-Path $Project 'Assets\Editor\AssetBundleBuilder.cs'
    $identityDestination=Join-Path $Project 'Assets\Editor\AssetBundleBuilder.Identity.cs'
    Assert-LocalPath $destination
    Assert-LocalPath $identityDestination -UnityImport
    if ((Test-Path -LiteralPath $destination -PathType Leaf) -and
        (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $SourceHash -and
        (Test-Path -LiteralPath $identityDestination -PathType Leaf) -and
        [IO.File]::ReadAllText($identityDestination) -ceq $identitySource) { return }
    if (!$AllowInstall) { throw 'The GUI worker differs from the template. Use -InstallGuiWorker once, then refresh the project in the Editor.' }
    $backup=Join-Path $Project ('.asset-authoring-jobs\WorkerBackups\'+[guid]::NewGuid().ToString('N'))
    Assert-LocalPath $backup
    [void][IO.Directory]::CreateDirectory($backup)
    if (Test-Path -LiteralPath $destination -PathType Leaf) { [IO.File]::Copy($destination,(Join-Path $backup 'AssetBundleBuilder.cs'),$false) }
    if (Test-Path -LiteralPath $identityDestination -PathType Leaf) { [IO.File]::Copy($identityDestination,(Join-Path $backup 'AssetBundleBuilder.Identity.cs'),$false) }
    [IO.File]::Copy($Source,$destination,$true)
    [IO.File]::WriteAllText($identityDestination,$identitySource,[Text.UTF8Encoding]::new($false))
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -cne $SourceHash -or
        [IO.File]::ReadAllText($identityDestination) -cne $identitySource) { throw 'The installed GUI worker or compiled identity differs from its desired source.' }
    Write-Output 'GUI worker updated. If it is not loaded yet, select Assets > Refresh in the open Editor once.'
}

function Assert-MaterialFieldPin($Pin) {
    Assert-RecipeFields $Pin @('path','sha256')
    if ($Pin.path -isnot [string] -or $Pin.path -notmatch '^[A-Za-z]:[\\/]' -or $Pin.sha256 -isnot [string] -or $Pin.sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid material-field file pin.' }
    $path=[IO.Path]::GetFullPath($Pin.path)
    Assert-LocalPath $path
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Pin.sha256) { throw "Pinned material-field input changed: $path" }
    return $path
}

function Read-ValidatedMaterialFieldInput([string]$Path) {
    $path=(Resolve-Path -LiteralPath $Path).ProviderPath
    Assert-LocalPath $path
    if ([IO.Path]::GetFileName($path) -cne 'Input.json' -or (Get-Item -LiteralPath $path).Length -gt 67108864) { throw 'Material-field input must be Input.json of at most 64 MiB.' }
    $folder=[IO.Path]::GetDirectoryName($path)
    if (!(Test-WithinPath $folder $authoringRoot) -and !(Test-WithinPath $folder (Join-Path $repoRoot 'tools/game-test/artifacts'))) { throw 'Material-field output must use the existing authoring workspace or game-test artifacts owner.' }
    $inputHash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $materialFieldInput=Get-Content -Raw -LiteralPath $path|ConvertFrom-Json
    Assert-RecipeFields $materialFieldInput @('status','tasks','ageScalars','scalarAuthority','scalarAuthorityProof','privateSource','ownerSource','codecResource','sourceInput','nativeAssetAuthority','sourcePins','generatedAssets','bundleName','holdFile')
    if ($materialFieldInput.status -cne 'PRIVATE_PERSISTENT_NATIVE_CONTROL_FIELD_BC7_PAIR_INPUT' -or $materialFieldInput.tasks -isnot [array] -or $materialFieldInput.tasks.Count -lt 1 -or $materialFieldInput.tasks.Count -gt 32) { throw 'Material-field input requires 1..32 explicit Main tasks.' }
    if ($materialFieldInput.ageScalars -isnot [array] -or $materialFieldInput.ageScalars.Count -ne 3 -or @($materialFieldInput.ageScalars|Where-Object {!(Test-FiniteNumber $_) -or $_ -le 0}).Count -or $materialFieldInput.scalarAuthority -isnot [string] -or [string]::IsNullOrWhiteSpace($materialFieldInput.scalarAuthority)) { throw 'Material-field age scalar authority is missing or invalid.' }
    if ($materialFieldInput.generatedAssets -isnot [string] -or $materialFieldInput.generatedAssets -cnotmatch '^Assets/MaterialFields_([A-Za-z0-9_]{1,64})$') { throw 'Material-field generatedAssets must be a single owned Assets/MaterialFields_<tag> namespace.' }
    $tag=$Matches[1]
    if ($materialFieldInput.bundleName -cne ('material_fields_'+$tag.ToLowerInvariant()+'.bundle')) { throw 'Material-field bundle and generated namespace tags differ.' }
    if ($materialFieldInput.holdFile -isnot [string] -or $materialFieldInput.holdFile -notmatch '^[A-Za-z]:[\\/]') { throw 'Material-field absolute HOLD path is required.' }
    $hold=[IO.Path]::GetFullPath($materialFieldInput.holdFile);Assert-LocalPath $hold
    if (!(Test-WithinPath $hold $authoringRoot) -and !(Test-WithinPath $hold (Join-Path $repoRoot 'tools/game-test/artifacts'))) { throw 'Material-field HOLD path is outside its existing owners.' }
    if (Test-Path -LiteralPath $hold) { throw 'Material-field HOLD requested before the next pack; no Editor was started.' }
    foreach($name in @('Result.json','Result.json.new','request.marker','OuterFailure.json')) { if (Test-Path -LiteralPath (Join-Path $folder $name)) { throw "Immutable material-field result or marker exists: $name" } }
    $pins=@($materialFieldInput.privateSource,$materialFieldInput.ownerSource,$materialFieldInput.codecResource,$materialFieldInput.sourceInput,$materialFieldInput.nativeAssetAuthority,$materialFieldInput.scalarAuthorityProof)
    if ($materialFieldInput.sourcePins -isnot [array] -or $materialFieldInput.sourcePins.Count -eq 0) { throw 'Material-field sourcePins must explicitly pin all data and evidence files.' }
    $pins+=@($materialFieldInput.sourcePins)
    $byPath=@{}
    foreach($pin in $pins) {
        $full=Assert-MaterialFieldPin $pin
        if ($byPath.ContainsKey($full) -and $byPath[$full] -cne $pin.sha256) { throw 'Material-field input has conflicting SHA pins for one path.' }
        $byPath[$full]=$pin.sha256
    }
    $dataPins=@{}
    foreach($pin in $materialFieldInput.sourcePins) {$dataPins[[IO.Path]::GetFullPath($pin.path)]=$pin.sha256}
    $scalarProofPath=[IO.Path]::GetFullPath($materialFieldInput.scalarAuthorityProof.path)
    if(!$dataPins.ContainsKey($scalarProofPath) -or $dataPins[$scalarProofPath] -cne $materialFieldInput.scalarAuthorityProof.sha256){throw 'Material-field scalar authority proof is missing from sourcePins.'}
    $ids=@{}
    foreach($task in $materialFieldInput.tasks) {
        foreach($name in @('id','name','role','raw','rawSha256','nativeRaw','nativeRawSha256','ownedMask','ownedMaskSha256','protectedMask','protectedMaskSha256','sourceBundle','sourceBundleSha256','asset','sourceEncodedSha256','width','height','mipCount','sRGB','mips')) { if (!$task.PSObject.Properties[$name]) { throw "Material-field task is missing $name" } }
        if ($task.id -isnot [string] -or $task.id -cnotmatch '^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$' -or $ids.ContainsKey($task.id) -or $task.name -cne $task.id -or $task.role -cnotin @('Main','Normal')) { throw 'Material-field task identity or skin texture role differs.' }
        $ids[$task.id]=$true
        foreach($name in @('width','height','mipCount')) { if (!(Test-FiniteNumber $task.$name) -or $task.$name -ne [Math]::Truncate($task.$name) -or $task.$name -le 0) { throw 'Material-field dimensions must be positive integers.' } }
        if ($task.width -gt 16384 -or $task.height -gt 16384 -or $task.width -ne $task.height -or $task.sRGB -isnot [bool] -or $task.sRGB -ne ($task.role -ceq 'Main') -or $task.mips -isnot [array] -or $task.mips.Count -ne $task.mipCount) { throw 'Material-field skin texture layout or colour space differs.' }
        foreach($name in @('precompressedField','precompressedFieldSha256')) { if($task.PSObject.Properties[$name] -and ![string]::IsNullOrWhiteSpace([string]$task.$name)){throw 'A changed-field checkpoint needs a separate explicit prior-codec proof; implicit field precompression is not accepted.'} }
        $fullMips=1;for($size=[Math]::Max($task.width,$task.height);$size -gt 1;$size=[int][Math]::Floor($size/2)){$fullMips++}
        if ($task.mipCount -ne $fullMips) { throw 'Material-field Main must include every physical mip.' }
        foreach($name in @('raw','nativeRaw','ownedMask','protectedMask','sourceBundle','precompressedNative')) {
            if (!$task.PSObject.Properties[$name]) { if($name -eq 'precompressedNative'){continue};throw 'A required material-field file is absent.' }
            $hashName=$name+'Sha256'
            if (!$task.PSObject.Properties[$hashName]) { throw "Material-field file has no SHA: $name" }
            $file=[IO.Path]::GetFullPath($task.$name)
            if (!$dataPins.ContainsKey($file) -or $dataPins[$file] -cne $task.$hashName) { throw "Material-field task file is not in sourcePins with the exact SHA: $name" }
        }
    }
    $resource=[IO.Path]::GetFullPath((Join-Path $repoRoot 'tools/asset-authoring/UnityAssetBuild/MaterialFieldBc7.cs.in'))
    if ([IO.Path]::GetFullPath($materialFieldInput.codecResource.path) -ine $resource) { throw 'Material-field codec resource is outside its canonical source owner.' }
    $render=[IO.File]::ReadAllText($resource)
    $tokens=@{__MATERIAL_FIELD_FOLDER__=$folder.Replace('\','/');__MATERIAL_FIELD_GENERATED__=[string]$materialFieldInput.generatedAssets;__MATERIAL_FIELD_BUNDLE__=[string]$materialFieldInput.bundleName;__MATERIAL_FIELD_TASK_COUNT__=[string]$materialFieldInput.tasks.Count}
    foreach($token in $tokens.GetEnumerator()) { if ([regex]::Matches($render,[regex]::Escape($token.Key)).Count -ne 1) { throw 'Material-field resource token cardinality differs.' };$render=$render.Replace($token.Key,$token.Value) }
    if ([IO.File]::ReadAllText($materialFieldInput.privateSource.path) -cne $render) { throw 'Material-field private source changed outside its four resource tokens.' }
    if ([string]::IsNullOrWhiteSpace($GuiProjectPath)) { throw 'Material-field input requires the marked GUI project for its installed owner authority.' }
    $project=(Resolve-Path -LiteralPath $GuiProjectPath).ProviderPath;Assert-LocalPath $project
    $workspace=[IO.Path]::GetFullPath($WorkspaceRoot);Assert-WorkspacePath $workspace
    if (!(Test-WithinPath $project $workspace) -or $project -ieq $workspace) { throw 'Material-field project is outside the marked WorkspaceRoot.' }
    $projectMarkerPath=Join-Path $project '.solasta-asset-authoring-project'
    if (!(Test-Path -LiteralPath $projectMarkerPath -PathType Leaf) -or [IO.File]::ReadAllText($projectMarkerPath).Trim() -cne $projectMarker) { throw 'Material-field GUI project is unmarked.' }
    $versionFile=Join-Path $project 'ProjectSettings/ProjectVersion.txt'
    if ([IO.File]::ReadAllText($versionFile) -notmatch ('(?m)^m_EditorVersion: '+[regex]::Escape($requiredVersion)+'\s*$')) { throw 'Material-field project uses a different Editor version.' }
    $installedOwner=Join-Path $project 'Assets/Editor/AssetBundleBuilder.cs';Assert-LocalPath $installedOwner
    $canonicalOwner=[IO.Path]::GetFullPath((Join-Path $repoRoot 'tools/asset-authoring/UnityAssetBuild/Assets/Editor/AssetBundleBuilder.cs'))
    if ([IO.Path]::GetFullPath($materialFieldInput.ownerSource.path) -ine $canonicalOwner) { throw 'Material-field worker is outside its canonical source owner.' }
    if (!$InstallGuiWorker -and (!(Test-Path -LiteralPath $installedOwner -PathType Leaf) -or
        (Get-FileHash -LiteralPath $installedOwner -Algorithm SHA256).Hash.ToLowerInvariant() -cne $materialFieldInput.ownerSource.sha256)) { throw 'Material-field installed worker differs from its pinned owner.' }
    $destination=Join-Path $project 'Assets/Editor/BoundedSkinBc7PersistentPair.cs'
    $generated=[IO.Path]::GetFullPath((Join-Path $project $materialFieldInput.generatedAssets))
    foreach($candidate in @($destination,($destination+'.meta'),$generated,($generated+'.meta'))) {Assert-LocalPath $candidate -UnityImport;if(Test-Path -LiteralPath $candidate){throw 'Material-field source or generated namespace already exists.'}}
    foreach($task in $materialFieldInput.tasks){foreach($variant in @('NativeBC7Control','FieldBC7')){Assert-LocalPath (Join-Path $generated ($task.id+'_'+$variant+'.asset')) -UnityImport}}
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $inputHash) { throw 'Material-field Input.json changed during validation.' }
    return [pscustomobject]@{path=$path;folder=$folder;sha256=$inputHash;input=$materialFieldInput;pins=$pins;project=$project;sourceDestination=$destination;generated=$generated;installedOwner=$installedOwner;hold=$hold;dependencyPaths=@($path)+@($byPath.Keys)}
}

function Invoke-MaterialFieldBuild($Validated) {
    if (Test-Path -LiteralPath $Validated.hold) { throw 'Material-field HOLD requested before pack; no Editor was started.' }
    if (!$StartBackgroundEditor) { throw 'Material fields require -StartBackgroundEditor.' }
    $editor=(Resolve-Path -LiteralPath $UnityPath).ProviderPath;Assert-LocalPath $editor
    if ([IO.Path]::GetFileName($editor) -ine 'Unity.exe') { throw '-UnityPath must identify an installed Unity.exe.' }
    Assert-UnityVersion $editor
    if (!$UnityCliPath) { $UnityCliPath=Join-Path $env:LOCALAPPDATA 'Unity/bin/unity.exe' }
    $cli=(Resolve-Path -LiteralPath $UnityCliPath).ProviderPath;Assert-LocalPath $cli
    $project=$Validated.project;$materialFieldInput=$Validated.input
    $null=Get-AssetProjectLock $project
    if (Test-Path -LiteralPath $Validated.hold) { throw 'Material-field HOLD requested before pack; no Editor was started.' }
    Install-AssetGuiWorker -Project $project -Source $materialFieldInput.ownerSource.path -SourceHash $materialFieldInput.ownerSource.sha256 -AllowInstall:$InstallGuiWorker
    $runRoot=Join-Path $project ('.asset-authoring-jobs/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff',[Globalization.CultureInfo]::InvariantCulture)+'-material-fields')
    Assert-LocalPath $runRoot
    if(Test-Path -LiteralPath $runRoot){throw 'The unique material-field job already exists.'}
    $resultPath=Join-Path $Validated.folder 'Result.json';$marker=Join-Path $Validated.folder 'request.marker'
    $wrapper=Join-Path $runRoot 'material-field-result.json'
    $session=$null;$ownership=$null;$primary=$null;$cleanup=[Collections.Generic.List[Exception]]::new();$success=$false;$result=$null;$sourceCopied=$false;$sessionClosedNormally=$false;$startedUtc=[DateTime]::UtcNow
    try {
        [void][IO.Directory]::CreateDirectory($runRoot)
        [IO.File]::WriteAllText((Join-Path $runRoot '.solasta-asset-authoring-job'),'SolastaUBAssetAuthoring:UnityJob:1')
        [IO.File]::Copy($materialFieldInput.privateSource.path,$Validated.sourceDestination,$false);$sourceCopied=$true
        if((Get-FileHash -LiteralPath $Validated.sourceDestination).Hash.ToLowerInvariant() -cne $materialFieldInput.privateSource.sha256){throw 'The copied material-field source changed.'}
        $session=Start-AssetBackgroundEditor -Project $project -Editor $editor -Cli $cli -SourceSha256 $materialFieldInput.ownerSource.sha256 -TimeoutSeconds $TimeoutSeconds
        $ownership=Register-AssetAuthoringJob -Project $project -JobDirectory $runRoot -AssetDirectory $Validated.generated -ResultPath $wrapper -DependencyPath $Validated.dependencyPaths -Session $session
        if(Test-Path -LiteralPath $Validated.hold){throw 'Material-field HOLD arrived before request; the Editor will close normally without encoding.'}
        [IO.File]::WriteAllText($marker,$Validated.sha256,[Text.UTF8Encoding]::new($false))
        $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds);$lastReadError=$null
        while([DateTime]::UtcNow -lt $deadline){
            if(Test-Path -LiteralPath $resultPath){
                try{
                    $stream=[IO.File]::Open($resultPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite-bor[IO.FileShare]::Delete))
                    try{$reader=[IO.StreamReader]::new($stream);$text=$reader.ReadToEnd()}finally{$stream.Dispose()}
                    $observed=$text|ConvertFrom-Json
                    if($observed.status -cne 'RUNNING'){$result=$observed;break}
                }catch{$lastReadError=$_.Exception.Message}
            }
            if($session.Ready -and $session.Ready.editor -and !(Get-AssetOwnedProcess $session.Ready.editor)){throw 'The owned material-field Editor ended before its final receipt.'}
            Start-Sleep -Milliseconds 250
        }
        if($null -eq $result){throw "Material-field codec returned no final receipt. Last read: $lastReadError"}
        if((Get-Item -LiteralPath $resultPath).LastWriteTimeUtc -lt $startedUtc -or $result.inputSha256 -cne $Validated.sha256){throw 'Material-field result has stale or different input authority.'}
        if(!$result.completed -or !$result.savedReloadAccepted -or !$result.production.allMipGpuReloadExact -or !$result.production.noRecompression -or !$result.production.fieldOnlyTextureSet -or @($result.production.fields).Count -ne @($materialFieldInput.tasks).Count){throw "Material-field codec/save/reload did not close: $($result.error)"}
        foreach($pin in $Validated.pins){[void](Assert-MaterialFieldPin $pin)}
        if((Get-FileHash -LiteralPath $Validated.path).Hash.ToLowerInvariant() -cne $Validated.sha256){throw 'Material-field Input.json changed during execution.'}
        $success=$true
    }catch{$primary=$_}
    finally {
        try{if($session){Stop-AssetBackgroundEditor $session;$sessionClosedNormally=$true}}catch{$cleanup.Add($_.Exception)}
        try{
            if($session -and !$sessionClosedNormally){throw 'The material-field session has no normal release receipt; owned project artifacts were retained for review.'}
            $null=Get-AssetProjectLock $project
            $expected=[IO.Path]::GetFullPath((Join-Path $project $materialFieldInput.generatedAssets))
            if($Validated.generated -cne $expected -or !(Test-WithinPath $expected (Join-Path $project 'Assets'))){throw 'Material-field cleanup namespace differs from its validated owner.'}
            Assert-LocalPath $expected
            if(Test-Path -LiteralPath $expected){Remove-Item -LiteralPath $expected -Recurse -Force}
            if(Test-Path -LiteralPath ($expected+'.meta')){Remove-Item -LiteralPath ($expected+'.meta') -Force}
            if($sourceCopied -and (Test-Path -LiteralPath $Validated.sourceDestination)){
                if((Get-FileHash -LiteralPath $Validated.sourceDestination).Hash.ToLowerInvariant() -cne $materialFieldInput.privateSource.sha256){throw 'The owned material-field source changed before cleanup; it was retained.'}
                Remove-Item -LiteralPath $Validated.sourceDestination -Force
                if(Test-Path -LiteralPath ($Validated.sourceDestination+'.meta')){Remove-Item -LiteralPath ($Validated.sourceDestination+'.meta') -Force}
            }
            if(Test-Path -LiteralPath $marker){Remove-Item -LiteralPath $marker -Force}
        }catch{$cleanup.Add($_.Exception)}
        try{
            $summary=[ordered]@{status=$(if($success -and !$cleanup.Count){'PASS_MATERIAL_FIELD_CODEC'}else{'ERROR'});input=@{path=$Validated.path;sha256=$Validated.sha256};ownerSource=$materialFieldInput.ownerSource;codecResource=$materialFieldInput.codecResource;actualResult=$(if(Test-Path -LiteralPath $resultPath){@{path=$resultPath;sha256=(Get-FileHash -LiteralPath $resultPath).Hash.ToLowerInvariant()}}else{$null});primaryError=$(if($primary){$primary.Exception.ToString()}else{$null});cleanupErrors=@($cleanup|ForEach-Object {$_.ToString()});session=$(if($session){$session.RunDirectory}else{$null});editorClosedNormally=$sessionClosedNormally}
            Write-AssetWorkerJson $wrapper $summary
        }catch{$cleanup.Add($_.Exception)}
        try{if($ownership){Complete-AssetAuthoringJob -Ownership $ownership -Succeeded:($success -and !$cleanup.Count) -Failure:$(if($primary){$primary.Exception.Message}elseif($cleanup.Count){$cleanup[0].Message}else{$null})}}catch{$cleanup.Add($_.Exception)}
    }
    if($primary){if($cleanup.Count){$primary.Exception.Data['MaterialFieldCleanupErrors']=@($cleanup|ForEach-Object {$_.ToString()})};throw $primary}
    if($cleanup.Count){throw [AggregateException]::new('Material-field cleanup failed.',$cleanup.ToArray())}
    [pscustomobject]@{status='PASS_MATERIAL_FIELD_CODEC';result=$resultPath;wrapper=$wrapper;completed=$true;savedReloadAccepted=$true;fields=@($materialFieldInput.tasks).Count;session=$session.RunDirectory}
}

$materialFieldJob=$PSCmdlet.ParameterSetName -eq 'MaterialFields'
$validatedMaterialField=$null
if($materialFieldJob){
    # Same repository/allowed workspace owner when this source candidate is tested privately.
    $probe=Get-Item -LiteralPath $PSScriptRoot
    while($probe -and !(Test-Path -LiteralPath (Join-Path $probe.FullName 'SolastaUnfinishedBusiness.sln'))){$probe=$probe.Parent}
    if(!$probe){throw 'Material-field entry has no repository owner.'}
    $repoRoot=$probe.FullName;$authoringRoot=Join-Path $repoRoot 'tools/asset-authoring/workspace'
    if([string]::IsNullOrWhiteSpace($WorkspaceRoot)){$WorkspaceRoot=Join-Path $authoringRoot 'UnityWorkspace'}
    $unityExe=(Resolve-Path -LiteralPath $UnityPath).ProviderPath
    Assert-LocalPath $unityExe
    if([IO.Path]::GetFileName($unityExe) -ine 'Unity.exe'){throw '-UnityPath must identify an installed Unity.exe.'}
    $validatedMaterialField=Read-ValidatedMaterialFieldInput $MaterialFieldInputPath
    if($ValidateOnly){
        [pscustomobject]@{status='PASS_MATERIAL_FIELD_INPUT';validationScope='material-field-pins-and-exact-resource';inputPath=$validatedMaterialField.path;sha256=$validatedMaterialField.sha256;kind='material-fields';fields=@($validatedMaterialField.input.tasks).Count;bundleName=$validatedMaterialField.input.bundleName;dependencyCount=@($validatedMaterialField.dependencyPaths).Count;appsStarted=0;outputsCreated=0}
        return
    }
}

if ($Cleanup) {
    if ([string]::IsNullOrWhiteSpace($GuiProjectPath)) { throw 'Cleanup requires -GuiProjectPath.' }
    . (Join-Path $PSScriptRoot 'UnityAssetBuild/BackgroundEditor.ps1')
    Clear-AssetAuthoringJobs -Project $GuiProjectPath -ReferencePath $ReferencePath -PreserveDirectory $PreserveDirectory -KeepLatest $KeepLatest -MaxAgeDays $MaxAgeDays -MaxRetainedBytes $MaxRetainedBytes -Apply:$Apply
    return
}
if ($Apply -or $ReferencePath.Count -or $PreserveDirectory.Count -or $MaxAgeDays -gt 0 -or $MaxRetainedBytes -gt 0) { throw 'Storage policy arguments require -Cleanup.' }
. $(if($materialFieldJob){Join-Path $repoRoot 'tools/asset-authoring/UnityAssetBuild/BackgroundEditor.ps1'}else{Join-Path $PSScriptRoot 'UnityAssetBuild/BackgroundEditor.ps1'})
$storageOperation=if($GuiProjectPath -and -not $ValidateOnly){Enter-AssetStorageOperation $GuiProjectPath}else{$null}
try {
if($materialFieldJob){Invoke-MaterialFieldBuild $validatedMaterialField;return}
$unityExe = (Resolve-Path -LiteralPath $UnityPath).ProviderPath
$materialJob = $PSCmdlet.ParameterSetName -eq 'Materials'
$meshJob = $PSCmdlet.ParameterSetName -eq 'Meshes'
$meshBatch = $meshJob -and $MeshRecipePath.Count -gt 1
$sourceFiles = @($(if ($meshJob) { $MeshRecipePath } elseif ($materialJob) { $MaterialRecipePath } else { $FbxPath }) | ForEach-Object {
    (Resolve-Path -LiteralPath $_).ProviderPath
})
if (@($sourceFiles | Sort-Object -Unique).Count -ne $sourceFiles.Count) { throw 'Duplicate mesh recipe inputs are not accepted.' }
$sourceFile = $sourceFiles[0]
if (!(Test-Path -LiteralPath $unityExe -PathType Leaf) -or [IO.Path]::GetFileName($unityExe) -ine 'Unity.exe') {
    throw '-UnityPath must identify an installed Unity.exe; this script does not install Unity.'
}
if (!$materialJob -and !$meshJob -and (!(Test-Path -LiteralPath $sourceFile -PathType Leaf) -or [IO.Path]::GetExtension($sourceFile) -ine '.fbx')) {
    throw '-FbxPath must identify an existing FBX file.'
}
if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    $WorkspaceRoot = Join-Path $authoringRoot 'UnityWorkspace'
}
$workspace = [IO.Path]::GetFullPath($WorkspaceRoot)
Assert-WorkspacePath $workspace
foreach ($inputSource in $sourceFiles) { Assert-LocalPath $inputSource }
$inputName = if ($meshBatch) { 'mesh-batch.json' } elseif ($meshJob) { 'mesh-recipe.json' } elseif ($materialJob) { 'recipe.json' } else { 'SampleRig.fbx' }
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

function Read-ValidatedMeshInput([string] $Path) {
    if ([IO.Path]::GetExtension($Path) -ine '.json' -or (Get-Item -LiteralPath $Path).Length -gt 67108864) { throw '-MeshRecipePath must identify a JSON recipe of at most 64 MiB.' }
    $bytes = [IO.File]::ReadAllBytes($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    $value = [Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xfeff) | ConvertFrom-Json
    $dependencies = @()
    if ($value.PSObject.Properties['numericPayload'] -or $value.PSObject.Properties['sourceBundles']) {
        $value = Read-ExternalMeshRecipe $Path
        if ($null -ne $value.numericPayload) {
            $assetSource = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($Path)) $value.numericPayload.path))
            Assert-LocalPath $assetSource
            $dependencies += [pscustomobject]@{path=$value.numericPayload.path;source=$assetSource;sha256=$value.numericPayload.sha256}
        }
    }
    else { Assert-MeshRecipe $value }
    if ($value.PSObject.Properties['textures']) {
        foreach ($texture in $value.textures) {
            $assetSource = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($Path)) $texture.path))
            Assert-LocalPath $assetSource
            if ((Get-FileHash -LiteralPath $assetSource -Algorithm SHA256).Hash.ToLowerInvariant() -ne $texture.sha256) {
                throw "The mesh texture source hash differs: $($texture.path)"
            }
            $dependencies += [pscustomobject]@{ path = $texture.path; source = $assetSource; sha256 = $texture.sha256 }
        }
    }
    if ($value.PSObject.Properties['sourceBundles']) {
        foreach ($sourceBundle in $value.sourceBundles) {
            $assetSource = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($Path)) $sourceBundle.path))
            Assert-LocalPath $assetSource
            if ((Get-FileHash -LiteralPath $assetSource -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sourceBundle.sha256) {
                throw "The source bundle hash differs: $($sourceBundle.path)"
            }
            $dependencies += [pscustomobject]@{path=$sourceBundle.path;source=$assetSource;sha256=$sourceBundle.sha256}
            foreach ($asset in $sourceBundle.assets) {
                if ($asset.PSObject.Properties['encodedTexturePatch'] -and $null -ne $asset.encodedTexturePatch) {
                    $encodedPayloads = @($asset.encodedTexturePatch.sourceMipPayload,$asset.encodedTexturePatch.patchedMipPayload)
                    if ($asset.encodedTexturePatch.PSObject.Properties['sourceEncodedStorage'] -and $null -ne $asset.encodedTexturePatch.sourceEncodedStorage) {
                        $encodedPayloads += $asset.encodedTexturePatch.sourceEncodedStorage.streamPayload
                    }
                    foreach ($payload in $encodedPayloads) {
                        $payloadSource = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($Path)) $payload.path))
                        Assert-LocalPath $payloadSource
                        $dependencies += [pscustomobject]@{path=$payload.path;source=$payloadSource;sha256=$payload.sha256}
                    }
                }
            }
        }
    }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'The mesh recipe changed while its input was validated.' }
    return [pscustomobject]@{source=$Path;bytes=$bytes;sha256=$hash;recipe=$value;dependencies=$dependencies}
}

$meshInputBytes = $null
$meshInputHash = $null
$meshInputs = @()
if ($meshJob) {
    $bundleNames = @{}
    foreach ($inputSource in $sourceFiles) {
        $meshInput = Read-ValidatedMeshInput $inputSource
        if ($bundleNames.ContainsKey($meshInput.recipe.bundleName)) { throw 'Duplicate mesh bundle names are not accepted in one batch.' }
        $bundleNames[$meshInput.recipe.bundleName] = $true
        $meshInputs += $meshInput
    }
    $outputNames = @{'unity-report.json'=$true}
    $rootManifest = if ($GuiProjectPath) { 'Output' } else { 'BuildOutput' }
    $outputNames[$rootManifest]=$true
    $outputNames[$rootManifest+'.manifest']=$true
    foreach ($meshInput in $meshInputs) {
        foreach ($name in @($meshInput.recipe.bundleName, ($meshInput.recipe.bundleName+'.manifest'))) {
            if ($outputNames.ContainsKey($name)) { throw 'A mesh bundle output collides with another bundle, its manifest or the authoring receipt.' }
            $outputNames[$name]=$true
        }
    }
    $recipe = $meshInputs[0].recipe
    $bundleName = $recipe.bundleName
    if ($meshBatch) {
        $batchRecipes = @()
        for ($index = 0; $index -lt $meshInputs.Count; $index++) {
            $meshInput = $meshInputs[$index]
            $prefix = 'Batch{0:D3}' -f $index
            $relativeRecipe = $prefix + '/mesh-recipe.json'
            $batchRecipes += [ordered]@{path=$relativeRecipe;sha256=$meshInput.sha256}
            $materialInputs += [pscustomobject]@{path=$relativeRecipe;source=$meshInput.source;sha256=$meshInput.sha256;bytes=$meshInput.bytes}
            foreach ($dependency in $meshInput.dependencies) {
                $materialInputs += [pscustomobject]@{path=($prefix+'/'+$dependency.path);source=$dependency.source;sha256=$dependency.sha256}
            }
        }
        $batchIndex = [ordered]@{schemaVersion=1;recipes=$batchRecipes} | ConvertTo-Json -Depth 5
        $meshInputBytes = [Text.UTF8Encoding]::new($false).GetBytes($batchIndex)
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $meshInputHash = ([BitConverter]::ToString($sha.ComputeHash($meshInputBytes))).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
    }
    else {
        $meshInputBytes = $meshInputs[0].bytes
        $meshInputHash = $meshInputs[0].sha256
        $materialInputs += $meshInputs[0].dependencies
    }
}

function Copy-AuthoringInput([string] $Destination) {
    if ($meshJob) {
        foreach ($inputSource in $meshInputs) {
            if ((Get-FileHash -LiteralPath $inputSource.source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inputSource.sha256) { throw 'The mesh recipe changed after its validated snapshot.' }
        }
        [IO.File]::WriteAllBytes($Destination, $meshInputBytes)
    }
    else { [IO.File]::Copy($sourceFile, $Destination, $false) }
}

function Copy-MaterialInputs([string] $InputDirectory, [switch] $ValidateOnly) {
    foreach ($asset in $materialInputs) {
        $destination = [IO.Path]::Combine($InputDirectory, $asset.path.Replace('/', '\'))
        Assert-LocalPath $destination -UnityImport
        if ($ValidateOnly) { continue }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        if ($asset.PSObject.Properties['bytes']) { [IO.File]::WriteAllBytes($destination, $asset.bytes) }
        else { [IO.File]::Copy($asset.source, $destination, $false) }
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) {
            throw 'A copied material asset differs from its source.'
        }
    }
}
$dependencyPaths = @(@($sourceFiles) + @($materialInputs | ForEach-Object source) | Sort-Object -Unique)
if ($ValidateOnly) {
    [pscustomobject]@{
        status='PASS_AUTHORING_INPUT';validationScope='input_declaration';inputPath=$sourceFile
        sha256=$(if($meshJob){$meshInputHash}else{(Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash.ToLowerInvariant()})
        kind=$(if($meshBatch){'mesh-batch-templates'}elseif($meshJob){'mesh-templates'}elseif($materialJob){'materials'}else{'rig'})
        bundleName=$bundleName;colorSpace=$ColorSpace;dependencyCount=$materialInputs.Count;appsStarted=0;outputsCreated=0
        inputs=@($meshInputs | ForEach-Object { [pscustomobject]@{path=$_.source;sha256=$_.sha256;bundleName=$_.recipe.bundleName} })
    }
    return
}
Assert-UnityVersion $unityExe
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
$sourceHash = (Get-FileHash -LiteralPath (Join-Path $template 'Assets\Editor\AssetBundleBuilder.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
$identitySource = Get-AssetWorkerIdentitySource $sourceHash
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
    $jobKind = if ($meshBatch) { 'mesh-batch' } elseif ($meshJob) { 'mesh' } elseif ($materialJob) { 'material' } else { 'rig' }
    $jobId = [datetime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff', [Globalization.CultureInfo]::InvariantCulture) + '-' + $jobKind
    $runRoot = Join-Path $project ('.asset-authoring-jobs\' + $jobId)
    $inputDirectory = [IO.Path]::Combine($project, 'Assets\AuthoringJobs\' + $jobId + '\Input')
    Assert-LocalPath ([IO.Path]::Combine($inputDirectory, $inputName)) -UnityImport
    Copy-MaterialInputs $inputDirectory -ValidateOnly
    Install-AssetGuiWorker -Project $project -Source (Join-Path $template 'Assets\Editor\AssetBundleBuilder.cs') -SourceHash $sourceHash -AllowInstall:$InstallGuiWorker
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
                    (Test-AssetWorkerIdentity $candidate $sourceHash) -and $candidate.projectPath -eq $project -and
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
    Assert-LocalPath $runRoot
    Assert-LocalPath $inputDirectory
    if ((Test-Path -LiteralPath $runRoot) -or (Test-Path -LiteralPath $inputDirectory)) { throw 'The unique GUI job already exists.' }
    [IO.Directory]::CreateDirectory($runRoot) | Out-Null
    [IO.Directory]::CreateDirectory($inputDirectory) | Out-Null
    [IO.File]::WriteAllText((Join-Path $runRoot '.solasta-asset-authoring-job'), 'SolastaUBAssetAuthoring:UnityJob:1')
    $copiedInput = Join-Path $inputDirectory $inputName
    Copy-AuthoringInput $copiedInput
    Copy-MaterialInputs $inputDirectory
    $inputHash = if ($meshJob) { $meshInputHash } else { (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash.ToLowerInvariant() }
    if ((Get-FileHash -LiteralPath $copiedInput -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inputHash) { throw 'The copied authoring input differs from the source.' }
    $bundleOutput = Join-Path $runRoot 'Output'
    $reportPath = Join-Path $bundleOutput 'unity-report.json'
    $logPath = $reportPath
    $startedUtc = [DateTime]::UtcNow
    $jobOwnership=Register-AssetAuthoringJob -Project $project -JobDirectory $runRoot -AssetDirectory (Split-Path -Parent $inputDirectory) -ResultPath $reportPath -DependencyPath $dependencyPaths -Session $backgroundSession
    $request = @{ schemaVersion = 1; jobId = $jobId; inputSha256 = $inputHash; colorSpace = $ColorSpace; kind = $(if ($meshBatch) { 'mesh-batch-templates' } elseif ($meshJob) { 'mesh-templates' } elseif ($materialJob) { 'materials' } else { 'rig' }) } | ConvertTo-Json
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

    $runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $runRoot = Join-Path $workspace $runId
    if (Test-Path -LiteralPath $runRoot) { throw 'The unique run directory already exists.' }
    $project = Join-Path $runRoot 'Project'
    $inputDirectory = [IO.Path]::Combine($project, 'Assets\Input')
    Assert-LocalPath ([IO.Path]::Combine($inputDirectory, $inputName)) -UnityImport
    Copy-MaterialInputs $inputDirectory -ValidateOnly
    [IO.Directory]::CreateDirectory($workspace) | Out-Null
    [IO.File]::WriteAllText($markerFile, $workspaceMarker + [Environment]::NewLine)
    [IO.Directory]::CreateDirectory($project) | Out-Null
    foreach ($relative in $templateFiles) {
        $destination = Join-Path $project $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy((Join-Path $template $relative), $destination, $false)
    }
    [IO.File]::WriteAllText((Join-Path $project 'Assets\Editor\AssetBundleBuilder.Identity.cs'), $identitySource, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $project '.solasta-asset-authoring-project'), $projectMarker)
    $storageOperation=Enter-AssetStorageOperation $project
    [IO.Directory]::CreateDirectory($inputDirectory) | Out-Null
    $copiedInput = Join-Path $inputDirectory $inputName
    Copy-AuthoringInput $copiedInput
    Copy-MaterialInputs $inputDirectory
    $inputHash = if ($meshJob) { $meshInputHash } else { (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash.ToLowerInvariant() }
    if ((Get-FileHash -LiteralPath $copiedInput -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inputHash) {
        throw 'The copied authoring input does not match its source.'
    }
    $bundleOutput = Join-Path $project 'BuildOutput'
    $reportPath = Join-Path $bundleOutput 'unity-report.json'
    $logPath = Join-Path $runRoot 'Editor.log'
    $startedUtc = [DateTime]::UtcNow
    $jobOwnership=Register-AssetAuthoringJob -Project $project -JobDirectory $runRoot -AssetDirectory (Join-Path $project 'Assets') -ResultPath $reportPath -DependencyPath $dependencyPaths -DisposableProject
    $arguments = @(
        '-batchmode', '-nographics', '-quit', '-noUpm',
        '-projectPath', $project,
        '-executeMethod', 'SolastaAssetAuthoring.AssetBundleBuilder.Build',
        $(if ($meshBatch) { '-meshBatchRecipe' } elseif ($meshJob) { '-meshRecipe' } elseif ($materialJob) { '-materialRecipe' } else { '-sampleFbx' }), $copiedInput,
        '-bundleOutput', $bundleOutput,
        '-colorSpace', $ColorSpace,
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
if (!$report.PSObject.Properties['compiledSourceSha256'] -or $report.compiledSourceSha256 -cne $sourceHash) {
    throw 'The report was produced by a different compiled authoring worker.'
}
if (!$report.PSObject.Properties['requestedColorSpace'] -or !$report.PSObject.Properties['actualColorSpace'] -or
    $report.requestedColorSpace -cne $ColorSpace -or $report.actualColorSpace -cne $ColorSpace) {
    throw 'The compiled assets use a different color space from the declared authoring target.'
}
if (![string]::IsNullOrWhiteSpace($GuiProjectPath) -and
    ($report.transport -ne 'gui_worker' -or $report.jobId -ne $jobId -or $report.processId -ne $ready.processId -or $report.mainThread -ne $ready.mainThread)) {
    throw 'The report does not identify the selected GUI worker job and main thread.'
}
if ((Get-FileHash -LiteralPath $copiedInput -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inputHash) {
    throw 'The source or copied authoring input changed during the build.'
}
if ($meshJob) {
    foreach ($inputSource in $meshInputs) {
        if ((Get-FileHash -LiteralPath $inputSource.source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inputSource.sha256) {
            throw 'The source mesh recipe changed during the build.'
        }
    }
}
elseif ((Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inputHash) {
    throw 'The source authoring input changed during the build.'
}
foreach ($asset in $materialInputs) {
    if ((Get-FileHash -LiteralPath $asset.source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256 -or
        (Get-FileHash -LiteralPath (Join-Path $inputDirectory $asset.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) {
        throw 'A source or copied material asset changed during the build.'
    }
}
function Assert-CompiledBundle($BuildReport, [string]$Name, [string]$Hash) {
    $expectedBundle = Join-Path $bundleOutput $Name
    if (!$BuildReport.success -or $BuildReport.stage -ne 'complete' -or $BuildReport.inputSha256 -cne $Hash -or
        !$BuildReport.PSObject.Properties['compiledSourceSha256'] -or $BuildReport.compiledSourceSha256 -cne $sourceHash -or
        $BuildReport.unityVersion -cne $requiredVersion -or $BuildReport.requestedColorSpace -cne $ColorSpace -or
        $BuildReport.actualColorSpace -cne $ColorSpace) { throw 'The bundle report does not match its validated input or target.' }
    if ($BuildReport.bundlePath -cne $expectedBundle -or !(Test-Path -LiteralPath $expectedBundle -PathType Leaf)) {
        throw 'The reported AssetBundle path is not the expected isolated output.'
    }
    if ((Get-FileHash -LiteralPath $expectedBundle -Algorithm SHA256).Hash.ToLowerInvariant() -cne $BuildReport.bundleSha256) {
        throw 'The AssetBundle does not match the success report.'
    }
}

function Assert-CompiledMeshReport($report, $recipe) {
if ($report.validationScope -ne 'mesh_templates' -or @($report.meshes).Count -ne @($recipe.meshes).Count -or
    @($report.meshes | Where-Object { !$_.exact }).Count -ne 0) { throw 'The report does not contain the requested exact mesh validation.' }
if ($recipe.PSObject.Properties['textures'] -and $recipe.textures.Count -gt 0) {
    foreach ($stage in @('createdTextures', 'savedTextures', 'textures')) {
        if (!$report.PSObject.Properties[$stage] -or @($report.$stage).Count -ne $recipe.textures.Count -or
            @($report.$stage | Where-Object { !$_.exact -or !$_.importSettingsExact }).Count -ne 0) {
            throw 'The report does not contain the requested exact texture validation.'
        }
        for ($index = 0; $index -lt $recipe.textures.Count; $index++) {
            $row = $report.$stage[$index]; $spec = $recipe.textures[$index]; $created = $report.createdTextures[$index]
            $expectedCrunch = $spec.PSObject.Properties['crunchedCompression'] -and $spec.crunchedCompression
            $expectedQuality = if ($spec.PSObject.Properties['compressionQuality'] -and $null -ne $spec.compressionQuality) { $spec.compressionQuality } else { 50 }
            if ($row.name -cne $spec.name -or $row.path -cne $spec.path -or $row.inputSha256 -cne $spec.sha256 -or
                $row.normalMap -ne $spec.normalMap -or $row.sRGB -ne $spec.sRGB -or $row.mipmaps -ne $spec.mipmaps -or
                $row.crunched -ne $expectedCrunch -or $row.compressionQuality -ne $expectedQuality -or
                $row.rawSha256 -cne $created.rawSha256 -or $row.graphicsFormat -cne $created.graphicsFormat -or
                $row.textureFormat -cne $created.textureFormat -or $row.rawBytes -ne $created.rawBytes -or
                $row.width -ne $created.width -or $row.height -ne $created.height -or $row.mipCount -ne $created.mipCount) {
                throw 'The texture report identity, import settings or raw payload changed.'
            }
        }
    }
}
if ($recipe.PSObject.Properties['sourceBundles']) {
    if ($report.sourceBundlesLoaded -ne $recipe.sourceBundles.Count -or $report.sourceBundlesReleased -ne $recipe.sourceBundles.Count) {
        throw 'The source bundle ownership was not released.'
    }
    $selected = @($recipe.sourceBundles | ForEach-Object { $sourceBundle = $_; $_.assets | ForEach-Object { [pscustomobject]@{bundle=$sourceBundle;asset=$_} } })
    foreach ($stage in @('createdSourceAssets','savedSourceAssets','sourceAssets')) {
        if (!$report.PSObject.Properties[$stage] -or @($report.$stage).Count -ne $selected.Count) { throw 'The source clone report count differs.' }
        for ($index = 0; $index -lt $selected.Count; $index++) {
            $row = $report.$stage[$index]; $spec = $selected[$index]; $created = $report.createdSourceAssets[$index]
            $dataExact = $row.rawBitsVerified -and $row.dataSha256 -ceq $created.dataSha256
            if ($stage -ceq 'sourceAssets' -and $spec.asset.PSObject.Properties['encodedTexturePatch'] -and
                $null -ne $spec.asset.encodedTexturePatch -and !$row.encodedTexturePatch.readable) {
                $dataExact = !$row.rawBitsVerified -and !$row.dataSha256 -and $row.savedDataSha256 -ceq $created.dataSha256 -and
                    $row.savedAssetSha256 -match '^[a-f0-9]{64}$' -and $row.dataValidationScope -ceq 'saved-encoded-bits-and-reloaded-gpu' -and $row.encodedTextureGpuExact
                $dataExact = $dataExact -and $row.metadataValidationScope -ceq 'stored-all-fields-plus-actual-gpu-dimensions'
            }
            if (!$row.exact -or !$row.physicalLayoutExact -or @($row.physicalFields).Count -eq 0 -or
                $row.sourcePath -cne $spec.bundle.path -or $row.sourceSha256 -cne $spec.bundle.sha256 -or
                $row.asset -cne $spec.asset.asset -or $row.name -cne $spec.asset.name -or $row.type -cne $spec.asset.type -or
                $row.sourceName -cne $created.sourceName -or !$dataExact -or $row.physicalSha256 -cne $created.physicalSha256) {
                throw 'The source clone physical data, identity or numeric fingerprint changed.'
            }
            if ($spec.asset.PSObject.Properties['encodedTexturePatch'] -and $null -ne $spec.asset.encodedTexturePatch) {
                $expected = $spec.asset.encodedTexturePatch; $patch = $row.encodedTexturePatch
                $expectedBlockBytes = if ($expected.outputFormat -ceq 'DXT1') { 8 } else { 16 }
                $expectedReadable = if ($expected.PSObject.Properties['readable'] -and $null -ne $expected.readable) { $expected.readable } else { $patch.sourceReadable }
                if (!$row.encodedTextureGpuExact -or !$patch.sourceOriginalUnchanged -or !$patch.nativeOriginalGpuUnchanged -or !$patch.outsideAllowedBlocksExact -or !$patch.allNativeMipsGpuExact -or !$patch.metadataExact -or
                    $patch.sourceFormat -cne $expected.sourceFormat -or $patch.outputFormat -cne $expected.outputFormat -or
                    $patch.encodedBlockBytes -ne $expectedBlockBytes -or
                    $patch.sourceRawSha256 -cne $expected.sourceRawSha256 -or $patch.sourceMipSha256 -cne $expected.sourceMipPayload.sha256 -or
                    $patch.patchedMipSha256 -cne $expected.patchedMipPayload.sha256 -or $patch.outputRawBytes -ne $expected.patchedMipPayload.byteLength -or
                    $patch.width -ne $expected.width -or $patch.height -ne $expected.height -or $patch.mipCount -ne $expected.mipCount -or
                    $patch.readable -ne $expectedReadable -or $patch.outputCompleteImageSize -ne $expected.patchedMipPayload.byteLength -or
                    @($patch.mips).Count -ne $expected.mipCount -or @($patch.mips | Where-Object { !$_.nativeGpuExact -or $_.nativeGpuSha256 -cne $_.decodedGpuSha256 }).Count -ne 0 -or
                    ($patch | ConvertTo-Json -Depth 12 -Compress) -cne ($created.encodedTexturePatch | ConvertTo-Json -Depth 12 -Compress)) {
                    throw 'The encoded texture patch source, preserved blocks, GPU mip authority or persisted proof changed.'
                }
            }
        }
    }
}
}
if ($meshBatch) {
    if ($report.validationScope -cne 'mesh_templates_batch' -or $report.buildPipelineInvocations -ne 1 -or
        @($report.bundles).Count -ne $meshInputs.Count) { throw 'The report does not contain every requested mesh bundle in one build.' }
    for ($index = 0; $index -lt $meshInputs.Count; $index++) {
        $inputSource = $meshInputs[$index]
        Assert-CompiledBundle $report.bundles[$index] $inputSource.recipe.bundleName $inputSource.sha256
        Assert-CompiledMeshReport $report.bundles[$index] $inputSource.recipe
    }
}
else {
    Assert-CompiledBundle $report $bundleName $inputHash
    if ($meshJob) { Assert-CompiledMeshReport $report $recipe }
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
