function Get-PackagedBlenderModelPath([string]$ProjectDirectory) {
    $document = [xml](Get-Content -LiteralPath (Join-Path $ProjectDirectory 'BlenderModels.Packaging.props') -Raw)
    $indexPath = Join-Path $ProjectDirectory 'BlenderModels/CharacterModels/CharacterModels.packs.json'
    $distribution = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
    $distributedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $distributedHashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $distribution.files) {
        [void]$distributedNames.Add($entry.file)
        [void]$distributedHashes.Add($entry.sha256)
    }
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $document.SelectNodes('/Project/ItemGroup/PackagedBlenderModel')) {
        $path = $entry.GetAttribute('Include')
        if ($path -cnotmatch '^BlenderModels/(?:[A-Za-z0-9_.-]+/)*[A-Za-z0-9_.-]+$' -or
            ($path -split '/') -contains '..' -or ($path -split '/') -contains '.' -or -not $paths.Add($path)) {
            throw "Invalid main package model entry: $path"
        }
        if ($path.StartsWith('BlenderModels/CharacterModels/', [StringComparison]::OrdinalIgnoreCase) -and
            $path -cnotin @('BlenderModels/CharacterModels/CharacterModels.manifest.json', 'BlenderModels/CharacterModels/CharacterModels.packs.json')) {
            throw "Character model payload belongs in its resource release: $path"
        }
        if ($path -like '*.bundle' -and ($distributedNames.Contains([IO.Path]::GetFileName($path)) -or
                $distributedHashes.Contains((Get-FileHash -LiteralPath (Join-Path $ProjectDirectory $path) -Algorithm SHA256).Hash))) {
            throw "A distributed character model bundle cannot also be declared for the main package: $path"
        }
        $path
    }
    if ($paths.Count -eq 0) { throw 'The main package has no declared model inputs.' }
}

function Assert-PackagedBlenderModel([string[]]$Paths, [string[]]$ExpectedPaths, [switch]$Partial) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $Paths) {
        if ($path -notlike 'BlenderModels/*' -and $path -notlike '*.bundle') { continue }
        if ($path -cnotin $ExpectedPaths -or -not $seen.Add($path)) {
            throw "Model asset is not declared for the main package: $path"
        }
    }
    if (-not $Partial -and -not $seen.SetEquals($ExpectedPaths)) {
        throw 'The main package omits declared model assets.'
    }
}
