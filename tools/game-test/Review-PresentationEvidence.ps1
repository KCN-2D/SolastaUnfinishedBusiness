[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string[]]$EvidencePath,
    [string]$OutputDirectory,
    [string[]]$ReferenceImagePath=@()
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Field($Value,[string]$Name) {
    if($null -eq $Value){return}
    $property=$Value.PSObject.Properties[$Name]
    if($null -ne $property -and $null -ne $property.Value){return $property.Value}
}
function Text-Field($Value,[string]$Name,[bool]$Optional=$false) {
    $text=Field $Value $Name
    if($null -eq $text -and $Optional){return ''}
    if($text -isnot [string] -or (-not $Optional -and [string]::IsNullOrWhiteSpace($text))){throw "Manifest string required: $Name"}
    return $text
}
function Array-Field($Value,[string]$Name) {
    $property=$Value.PSObject.Properties[$Name]
    if($null -eq $property -or $property.Value -isnot [Array]){throw "Manifest array required: $Name"}
    return $property.Value
}
function Hash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function Read-Json([string]$Path){Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json}
function Escape($Value){if($null -eq $Value){return '未記録'};[Net.WebUtility]::HtmlEncode([string]$Value)}
function Resolve-ArtifactPath([string]$Manifest,[string]$Path) {
    if([IO.Path]::IsPathRooted($Path)){return [IO.Path]::GetFullPath($Path)}
    [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($Manifest)) $Path))
}
function Artifact([string]$Path,[string]$ExpectedSha='') {
    $exists=Test-Path -LiteralPath $Path -PathType Leaf
    $actual=if($exists){Hash $Path}else{$null}
    [ordered]@{path=$Path;exists=$exists;sha256=$actual;expectedSha256=$ExpectedSha;hashMatches=$(if($ExpectedSha -and $exists){[string]::Equals($actual,$ExpectedSha,[StringComparison]::OrdinalIgnoreCase)}else{$null})}
}
function Check-Status($Checks) {
    $statuses=@($Checks | ForEach-Object {$_.status})
    if($statuses -ccontains 'FAIL'){'FAIL'}elseif($statuses -ccontains 'NOT_RECORDED' -or $statuses.Count -eq 0){'NOT_RECORDED'}elseif($statuses -ccontains 'REVIEW'){'REVIEW'}else{'PASS'}
}
$paths=[Collections.Generic.List[string]]::new()
foreach($requested in $EvidencePath) {
    if(-not (Test-Path -LiteralPath $requested)){continue}
    $resolved=(Resolve-Path -LiteralPath $requested).ProviderPath
    if(Test-Path -LiteralPath $resolved -PathType Container) {
        foreach($candidate in @(Get-ChildItem -LiteralPath $resolved -Filter 'PresentationEvidence*.json' -File)){$paths.Add($candidate.FullName)}
    } else {$paths.Add($resolved)}
}
$orderedPaths=[string[]]@($paths.ToArray() | Select-Object -Unique)
[Array]::Sort($orderedPaths,[StringComparer]::Ordinal)
$inputs=[Collections.Generic.List[object]]::new()
$documents=[Collections.Generic.List[object]]::new()
foreach($path in $orderedPaths) {
    $data=Read-Json $path
    if($null -eq $data -or $null -eq $data.PSObject.Properties['frames'] -or $null -eq $data.PSObject.Properties['checks'] -or $null -eq $data.PSObject.Properties['sources']) {
        if([IO.Path]::GetFileName($path) -like 'PresentationEvidence*.json'){throw "Incomplete presentation manifest: $path"}
        continue
    }
    if((Field $data 'schemaVersion') -ne 1){throw "Unsupported presentation manifest schema: $path"}
    $input=[ordered]@{path=$path;sha256=(Hash $path)}
    $inputs.Add($input)
    $title=Text-Field $data 'title'
    $frameIds=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $frames=@(foreach($frame in @(Array-Field $data 'frames')) {
        $id=Text-Field $frame 'id'
        if(-not $frameIds.Add($id)){throw "Duplicate frame id: $id"}
        $artifact=Artifact (Resolve-ArtifactPath $path (Text-Field $frame 'path')) (Text-Field $frame 'sha256' $true)
        [ordered]@{id=$id;path=$artifact.path;label=(Text-Field $frame 'label' $true);group=(Text-Field $frame 'group' $true);phase=(Text-Field $frame 'phase' $true);exists=$artifact.exists;sha256=$artifact.sha256;expectedSha256=$artifact.expectedSha256;hashMatches=$artifact.hashMatches}
    })
    $checkIds=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $checks=@(foreach($check in @(Array-Field $data 'checks')) {
        $id=Text-Field $check 'id';$state=Text-Field $check 'status'
        if(-not $checkIds.Add($id)){throw "Duplicate check id: $id"}
        if(@('PASS','FAIL','REVIEW','NOT_RECORDED') -cnotcontains $state){throw "Invalid manifest check status: $state"}
        $evidence=@(foreach($frameId in @(Array-Field $check 'evidence')) {
            if($frameId -isnot [string] -or -not $frameIds.Contains($frameId)){throw "Unknown evidence frame id: $frameId"}
            $frameId
        })
        $missingEvidence=@($frames | Where-Object {$evidence -ccontains $_.id -and -not $_.exists})
        $changedEvidence=@($frames | Where-Object {$evidence -ccontains $_.id -and $_.hashMatches -eq $false})
        $evidenceState=if($changedEvidence.Count){'FAIL'}elseif($missingEvidence.Count){'NOT_RECORDED'}else{'PASS'}
        $effectiveState=Check-Status @([ordered]@{status=$state},[ordered]@{status=$evidenceState})
        [ordered]@{id=$id;title=(Text-Field $check 'title');status=$effectiveState;declaredStatus=$state;expected=(Text-Field $check 'expected' $true);actual=(Text-Field $check 'actual' $true);evidence=$evidence;missingEvidence=@($missingEvidence | ForEach-Object {$_.id})}
    })
    $sources=@(foreach($source in @(Array-Field $data 'sources')) {
        $artifact=Artifact (Resolve-ArtifactPath $path (Text-Field $source 'path')) (Text-Field $source 'sha256' $true)
        [ordered]@{path=$artifact.path;role=(Text-Field $source 'role' $true);expectedSha256=$artifact.expectedSha256;exists=$artifact.exists;sha256=$artifact.sha256;hashMatches=$artifact.hashMatches}
    })
    $frameStatus=if(@($frames | Where-Object {$_.hashMatches -eq $false}).Count){'FAIL'}elseif(@($frames | Where-Object {-not $_.exists}).Count){'NOT_RECORDED'}else{'PASS'}
    $sourceStatus=if(@($sources | Where-Object {$_.hashMatches -eq $false}).Count){'FAIL'}elseif(@($sources | Where-Object {-not $_.exists}).Count){'NOT_RECORDED'}else{'PASS'}
    $documentStatus=Check-Status @([ordered]@{status=(Check-Status $checks)},[ordered]@{status=$sourceStatus},[ordered]@{status=$frameStatus})
    $documents.Add([ordered]@{manifest=$path;manifestSha256=$input.sha256;title=$title;status=$documentStatus;sourceIntegrity=$sourceStatus;frameIntegrity=$frameStatus;frames=$frames;checks=$checks;sources=$sources})
}
if($documents.Count -eq 0){[pscustomobject]@{status='NOT_APPLICABLE';visualAcceptance='NOT_REVIEWED';reason='No supported presentation evidence manifest.'};return}
if(-not $OutputDirectory) {
    $algorithm=[Security.Cryptography.SHA256]::Create()
    try{$key=([BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes(($inputs | ConvertTo-Json -Compress))))).Replace('-','').Substring(0,16).ToLowerInvariant()}
    finally{$algorithm.Dispose()}
    $OutputDirectory=Join-Path $PSScriptRoot ('artifacts\PresentationReview\'+$key)
}
$output=[IO.Path]::GetFullPath($OutputDirectory)
foreach($input in $inputs) {
    $sourceDirectory=[IO.Path]::GetDirectoryName($input.path).TrimEnd('\','/')
    if($output -eq $sourceDirectory -or $output.StartsWith($sourceDirectory+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Review output must be outside the source evidence directory.'}
}
$cursor=$output
while($cursor) {
    if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Review output cannot traverse a reparse point.'}
    $cursor=[IO.Path]::GetDirectoryName($cursor)
}
foreach($name in @('Summary.json','Review.html')) {
    $target=Join-Path $output $name
    if((Test-Path -LiteralPath $target) -and ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Review output cannot overwrite a reparse point.'}
}
$contexts=[Collections.Generic.List[object]]::new()
$contextPaths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($input in $inputs) {
    $directory=[IO.Path]::GetDirectoryName($input.path)
    for($level=0;$level -lt 4 -and $directory;$level++) {
        $resultPath=Join-Path $directory 'Result.json'
        if((Test-Path -LiteralPath $resultPath -PathType Leaf) -and $contextPaths.Add($resultPath)) {
            $result=Read-Json $resultPath
            $sources=@(foreach($entry in @(Field $result 'sources')) {
                $archive=Field $entry 'archivePath';$exists=$archive -and (Test-Path -LiteralPath $archive -PathType Leaf)
                [ordered]@{path=(Field $entry 'path');recordedSha256=(Field $entry 'sha256');archivePath=$archive;archiveExists=[bool]$exists;archiveHashMatches=$(if($exists){(Hash $archive) -eq (Field $entry 'sha256')}else{$null})}
            })
            $contexts.Add([ordered]@{path=$resultPath;sha256=(Hash $resultPath);status=(Field $result 'status');nativeVerificationStatus=(Field $result 'nativeVerificationStatus');restored=(Field $result 'restored');settingsRestored=(Field $result 'settingsRestored');dllHashes=(Field $result 'hashes');expectedUb=(Field $result 'expectedUb');loadedUbMatches=(Field (Field $result 'loadedUb') 'matches');sources=$sources})
        }
        $directory=[IO.Path]::GetDirectoryName($directory)
    }
}
$references=@(foreach($reference in $ReferenceImagePath){Artifact ((Resolve-Path -LiteralPath $reference).ProviderPath)})
$allChecks=@(foreach($document in $documents){$document.checks})
$status=Check-Status $documents
$summary=[ordered]@{schemaVersion=1;status=$status;visualAcceptance='NOT_REVIEWED';generatorSha256=(Hash $PSCommandPath);inputs=@($inputs.ToArray());documents=@($documents.ToArray());references=$references;runContext=@($contexts.ToArray())}
[void][IO.Directory]::CreateDirectory($output)
$utf8=[Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $output 'Summary.json'),($summary | ConvertTo-Json -Depth 40)+[Environment]::NewLine,$utf8)
$baseUri=[Uri]::new($output.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar)
function Link([string]$Path){Escape ($baseUri.MakeRelativeUri([Uri]::new($Path)).ToString())}
$html=[Collections.Generic.List[string]]::new()
$html.Add('<!doctype html><html lang="ja"><meta charset="utf-8"><title>Presentation review</title><style>body{font:16px system-ui;margin:24px;color:#222}figure{margin:0;border:1px solid #bbb;padding:8px}img{max-width:100%;height:auto}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(360px,1fr));gap:12px}table{border-collapse:collapse}td,th{border:1px solid #bbb;padding:6px}.FAIL{color:#b00020}.PASS{color:#14652c}</style>')
$html.Add('<h1>Presentation review: <span class="'+$status+'">'+$status+'</span></h1><p>manifestの判定を集約 / 目視判定: 未確認</p><p><a href="Summary.json">Summary.json</a></p>')
$documentIndex=0
foreach($document in $documents) {
    $documentIndex++;$prefix='document-'+$documentIndex+'-'
    $html.Add('<h2>'+(Escape $document.title)+' / '+$document.status+'</h2><p><a href="'+(Link $document.manifest)+'">manifest</a> SHA256 '+$document.manifestSha256+'</p><table><tr><th>項目</th><th>判定</th><th>期待値</th><th>実測値</th><th>画像</th></tr>')
    foreach($check in $document.checks) {
        $links=@(foreach($id in $check.evidence){'<a href="#'+(Escape ($prefix+$id))+'">'+(Escape $id)+'</a>'})
        $html.Add('<tr><td>'+(Escape $check.title)+'</td><td class="'+$check.status+'">'+$check.status+'</td><td>'+(Escape $check.expected)+'</td><td>'+(Escape $check.actual)+'</td><td>'+($links -join ' ')+'</td></tr>')
    }
    $html.Add('</table><div class="grid">')
    foreach($frame in $document.frames) {
        $html.Add('<figure id="'+(Escape ($prefix+$frame.id))+'"><figcaption>'+(Escape ($frame.group+' / '+$frame.label+' / '+$frame.phase))+'</figcaption>')
        if($frame.exists){$url=Link $frame.path;$html.Add('<a href="'+$url+'"><img loading="lazy" src="'+$url+'" alt="'+(Escape $frame.label)+'"></a><p>SHA256 '+$frame.sha256+'</p>')}else{$html.Add('<p>画像未取得: '+(Escape $frame.path)+'</p>')}
        if($frame.expectedSha256){$html.Add('<p>記録SHA256 '+(Escape $frame.expectedSha256)+' / hash一致 '+(Escape $frame.hashMatches)+'</p>')}
        $html.Add('</figure>')
    }
    $html.Add('</div><details><summary>出典</summary><table><tr><th>role / path</th><th>実在</th><th>期待SHA256 / 実SHA256</th><th>hash一致</th></tr>')
    foreach($source in $document.sources){$html.Add('<tr><td>'+(Escape $source.role)+' / '+(Escape $source.path)+'</td><td>'+(Escape $source.exists)+'</td><td>'+(Escape $source.expectedSha256)+'<br>'+(Escape $source.sha256)+'</td><td>'+(Escape $source.hashMatches)+'</td></tr>')}
    $html.Add('</table></details>')
}
$html.Add('<h2>実行証跡</h2><table><tr><th>Result</th><th>記録された状態 / native検証</th><th>restored / settingsRestored</th><th>built / installed SHA256</th><th>loaded matches</th></tr>')
foreach($entry in $contexts){$html.Add('<tr><td><a href="'+(Link $entry.path)+'">'+(Escape $entry.path)+'</a></td><td>'+(Escape $entry.status)+' / '+(Escape $entry.nativeVerificationStatus)+'</td><td>'+(Escape $entry.restored)+' / '+(Escape $entry.settingsRestored)+'</td><td>'+(Escape (Field $entry.dllHashes 'builtUb'))+'<br>'+(Escape (Field $entry.dllHashes 'installedUb'))+'</td><td>'+(Escape $entry.loadedUbMatches)+'</td></tr>')}
$html.Add('</table>')
if($references.Count){$html.Add('<h2>参照画像</h2><div class="grid">');foreach($entry in $references){$html.Add('<figure><img src="'+(Link $entry.path)+'" alt="参照画像"><figcaption>'+(Escape ([IO.Path]::GetFileName($entry.path)))+'</figcaption></figure>')};$html.Add('</div>')}
$html.Add('</html>')
[IO.File]::WriteAllText((Join-Path $output 'Review.html'),($html -join [Environment]::NewLine)+[Environment]::NewLine,$utf8)
foreach($input in $inputs){if((Hash $input.path) -cne $input.sha256){throw 'Source evidence changed during review.'}}
[pscustomobject]@{status=$status;checks=$allChecks.Count;images=@(foreach($document in $documents){$document.frames}).Count;summary=(Join-Path $output 'Summary.json');html=(Join-Path $output 'Review.html')}
