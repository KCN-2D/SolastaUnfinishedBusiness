# Plan evidence names before any native .NET Framework / Mono file operation.
function Get-VerificationEvidencePath {
    param(
        [Parameter(Mandatory=$true)][string]$Root,
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][string]$ArtifactRoot,
        [Parameter(Mandatory=$true)][ValidateSet('GameTest','Runtime')][string]$Layout,
        [string]$Stamp = (Get-Date -Format 'yyyyMMdd-HHmmss-fff'),
        [string]$UniqueToken = ([guid]::NewGuid().ToString('N').Substring(0,16))
    )
    $requestedRoot=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    $allowed=[IO.Path]::GetFullPath($ArtifactRoot).TrimEnd('\','/')
    if (-not ($requestedRoot.Equals($allowed,[StringComparison]::OrdinalIgnoreCase) -or
        $requestedRoot.StartsWith($allowed+'\',[StringComparison]::OrdinalIgnoreCase))) {
        throw 'EvidenceRoot must remain inside the allowed artifact root.'
    }
    if ($Name -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_-]*$' -or
        $Stamp -notmatch '^\d{8}-\d{6}-\d{3}$' -or $UniqueToken -notmatch '^[a-fA-F0-9]{16}$') {
        throw 'Invalid evidence directory identifier.'
    }
    # Native net4 paths must be <260 for files and <248 for directories.
    # Keep 64 UTF-16 code units for a normal character filename, including suffix.
    $maximumFilePath=259; $maximumDirectoryPath=247; $characterFileNameBudget=64
    $isolationChild='OwnedSaveSmoke\TemporaryCharacters'
    $runtimeLimit=[Math]::Min($maximumDirectoryPath-1-$isolationChild.Length,
        $maximumFilePath-2-$isolationChild.Length-$characterFileNameBudget)
    $minimumComponent='r-'+$UniqueToken
    $limit=$runtimeLimit
    if ($Layout -eq 'GameTest') { $limit-='\Runtime\'.Length+$minimumComponent.Length }
    $componentBudget=$limit-$requestedRoot.Length-1
    if ($componentBudget -lt $minimumComponent.Length) {
        throw "shorter EvidenceRoot required: root length $($requestedRoot.Length), minimum run path length $($requestedRoot.Length+1+$minimumComponent.Length), $Layout limit $limit (native file <260 / directory <248, character filename reserve $characterFileNameBudget). Root: $requestedRoot"
    }
    $preferred=$Stamp+'-'+$Name
    if ($Layout -eq 'GameTest') { $preferred+='-'+$UniqueToken.Substring(0,8) }
    $component=$preferred
    if ($component.Length -gt $componentBudget) {
        $labelBudget=$componentBudget-$Stamp.Length-2-$UniqueToken.Length
        if ($labelBudget -gt 0) { $component=$Stamp+'-'+$Name.Substring(0,[Math]::Min($Name.Length,$labelBudget))+'-'+$UniqueToken }
        else { $component=$minimumComponent }
    }
    $directory=Join-Path $requestedRoot $component
    [pscustomobject][ordered]@{
        schemaVersion=1; layout=$Layout; requestedName=$Name; requestedEvidenceRoot=$requestedRoot
        directory=$directory; shortened=($component -cne $preferred)
        maximumThisDirectoryLength=$limit; maximumRuntimeDirectoryLength=$runtimeLimit
        maximumNativeFilePath=$maximumFilePath; maximumNativeDirectoryPath=$maximumDirectoryPath
        nativeIsolationRelativePath=$isolationChild; nativeCharacterFileNameBudget=$characterFileNameBudget
    }
}
