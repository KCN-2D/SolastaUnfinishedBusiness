param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $MessageFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

function Stop-CommitMessage([string] $Reason, [int] $Code = 1) {
    [Console]::Error.WriteLine("commit-msg: $Reason")
    exit $Code
}

function Test-JapaneseText([string] $Text) {
    return $Text -match '[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}\uFF66-\uFF9F]'
}

function Test-FixedLine([string] $Text) {
    # Trailers contain contributor names, addresses and referenced commit identities.
    if ($Text -match '^(?i:Signed-off-by|Co-authored-by|Reviewed-by|Acked-by|Tested-by|Reported-by|Suggested-by|Helped-by|See-also|Cc|Fixes):\s+\S') {
        return $true
    }

    if ($Text -match '^`[^`]+`$' -or $Text -match '^(?:https?://|ssh://|git@)\S+$') {
        return $true
    }

    # Standalone words are prose. Fixed identifiers have visible structural
    # evidence (a path, dotted name, digit, underscore or mixed-case type name).
    return $Text -cmatch '^[A-Za-z0-9_./\\:@+~^{}()\[\]<>#%=-]+$' -and
        ($Text -match '[0-9_./\\:@+~^{}()\[\]<>#%=-]' -or $Text -cmatch '^[A-Z][a-z]+[A-Z][A-Za-z]*$')
}

try {
    $messagePath = (Resolve-Path -LiteralPath $MessageFile -ErrorAction Stop).ProviderPath
    $strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
    $message = $strictUtf8.GetString([System.IO.File]::ReadAllBytes($messagePath)).TrimStart([char] 0xFEFF)
} catch {
    Stop-CommitMessage 'メッセージファイルをUTF-8で読み取れません。' 2
}

$lines = @($message -split '\r?\n')
$commentCharacter = '#'
$configuredComment = & git config --get core.commentChar 2>$null
if ($LASTEXITCODE -eq 0 -and $configuredComment -and $configuredComment -ne 'auto') {
    $commentCharacter = [string] $configuredComment
}
$commentPattern = '^' + [regex]::Escape($commentCharacter)
$content = @($lines | Where-Object { $_ -notmatch $commentPattern })
$subjectIndex = 0
while ($subjectIndex -lt $content.Count -and [string]::IsNullOrWhiteSpace($content[$subjectIndex])) {
    $subjectIndex++
}
if ($subjectIndex -eq $content.Count -or -not (Test-JapaneseText $content[$subjectIndex])) {
    Stop-CommitMessage '件名を日本語で記述してください。'
}

$paragraphs = New-Object 'System.Collections.Generic.List[string]'
$paragraph = New-Object 'System.Collections.Generic.List[string]'
$fenceCharacter = ''
$fenceLength = 0
for ($lineIndex = $subjectIndex + 1; $lineIndex -lt $content.Count; $lineIndex++) {
    $line = $content[$lineIndex].Trim()
    if ($line -match '^(?<fence>`{3,}|~{3,})') {
        $fence = $Matches['fence']
        if (-not $fenceCharacter) {
            if ($paragraph.Count) {
                $paragraphs.Add(($paragraph -join ' '))
                $paragraph.Clear()
            }
            $fenceCharacter = $fence.Substring(0, 1)
            $fenceLength = $fence.Length
        } elseif ($fence.Substring(0, 1) -eq $fenceCharacter -and $fence.Length -ge $fenceLength -and $line -match '^[`~]+\s*$') {
            $fenceCharacter = ''
            $fenceLength = 0
        }
        continue
    }
    if ($fenceCharacter) {
        continue
    }

    $isListItem = $line -match '^(?:[-*+]\s+|\d+[.)]\s+)'
    if ([string]::IsNullOrWhiteSpace($line) -or $isListItem) {
        if ($paragraph.Count) {
            $paragraphs.Add(($paragraph -join ' '))
            $paragraph.Clear()
        }
    }
    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }
    if ($isListItem) {
        $line = $line -replace '^(?:[-*+]\s+|\d+[.)]\s+)', ''
    }
    if (Test-FixedLine $line) {
        continue
    }
    $paragraph.Add($line)
}
if ($paragraph.Count) {
    $paragraphs.Add(($paragraph -join ' '))
}
for ($index = 0; $index -lt $paragraphs.Count; $index++) {
    if (-not (Test-JapaneseText $paragraphs[$index])) {
        Stop-CommitMessage "本文の文章段落$($index + 1)を日本語で記述してください。"
    }
}
exit 0
