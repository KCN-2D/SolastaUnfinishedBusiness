[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$BaseCommit = 'HEAD',
    [switch]$Staged,
    [string]$JapaneseGlossaryPath,
    [string]$TerminologyReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
if (-not $RepositoryRoot) { $RepositoryRoot = Join-Path $PSScriptRoot '../..' }
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$locales = @('en','de','es','fr','it','ja','ko','pt-BR','ru','zh-CN')
$translationRoot = 'SolastaUnfinishedBusiness/Translations'
$unofficialRoot = 'SolastaUnfinishedBusiness/UnofficialTranslations'

function Read-Git([string[]]$Arguments, [switch]$AllowMissing, [switch]$PreserveFinalNewline) {
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& git -C $root -c core.quotepath=false @Arguments 2>$null)
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previousPreference }
    $missing = $AllowMissing -and (($Arguments[0] -eq 'grep' -and $code -eq 1) -or
        ($Arguments[0] -eq 'show' -and $code -eq 128) -or ($Arguments[0] -eq 'rev-parse' -and $code -eq 1))
    if ($code -ne 0 -and -not $missing) { throw "Git の読み取りに失敗しました: $($Arguments[0])" }
    $text = $output -join "`n"
    if ($PreserveFinalNewline -and $output.Count -gt 0) { $text += "`n" }
    return $text
}

function Read-TranslationMap([string]$Content, [string]$Path) {
    $terms = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($line in ($Content.TrimStart([char]0xFEFF) -split '\r?\n')) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line -match '^\s*(#|//)') { continue }
        if ($line -notmatch '^([^=]+/&[^=]+)=(.*)$') { throw "翻訳行の形式が不正です: $Path" }
        $key = $Matches[1]
        if ($terms.ContainsKey($key)) { throw "翻訳キーが重複しています: $Path : $key" }
        $terms.Add($key, $Matches[2])
    }
    return ,$terms
}

function Get-CurrentContent([string]$Path) {
    if ($Staged) { return Read-Git @('show',(':' + $Path)) -AllowMissing -PreserveFinalNewline }
    $fullPath = Join-Path $root $Path
    if ([IO.File]::Exists($fullPath)) { return [IO.File]::ReadAllText($fullPath) }
    return ''
}

function Get-TokenSignature([string]$Value) {
    # Formatting order may change in translation; token identity and count may not.
    $tokens = @([regex]::Matches($Value, '(?<!\{)\{\d+(?:,[^}:]+)?(?::[^}]+)?\}(?!\})|%[^%\r\n]+%|<[^>\r\n]+>|\\[nrt]') |
        ForEach-Object { $_.Value } | Sort-Object -CaseSensitive)
    return $tokens -join "`n"
}

function Assert-Text([string]$Value, [string]$Context) {
    if ([string]::IsNullOrWhiteSpace($Value)) { throw "翻訳が空です: $Context" }
    if ($Value -match '(?<!\{)\{\d+[^}]*$') { throw "プレースホルダーが閉じていません: $Context" }
    $stack = [Collections.Generic.Stack[string]]::new()
    foreach ($tag in [regex]::Matches($Value, '<(?<close>/)?(?<name>b|i|u|s|color|size|align|alpha|font|mark|nobr|sub|sup|voffset|cspace|mspace|indent|line-height|margin|width|rotate|link|style|#[a-fA-F0-9]{6}(?:[a-fA-F0-9]{2})?)(?:=[^>]*)?>')) {
        $name = $tag.Groups['name'].Value.ToLowerInvariant()
        if ($name.StartsWith('#')) { $name = 'color' }
        if ($tag.Groups['close'].Success) {
            if ($stack.Count -eq 0 -or $stack.Pop() -cne $name) { throw "タグの対応が不正です: $Context" }
        } else { $stack.Push($name) }
    }
    if ($stack.Count -ne 0) { throw "タグが閉じていません: $Context" }
}

function Assert-JapaneseToggleStyle {
    $grepArguments = @('grep','--full-name','-l','-I')
    if ($Staged) { $grepArguments += '--cached' } else { $grepArguments += '--untracked' }
    $grepArguments += @('UI.Toggle','--','SolastaUnfinishedBusiness/*.cs')
    $toggleKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    # Inspect checkbox call sites; buttons and headings have different writing conventions.
    $pattern = '(?s)UI\.Toggle\s*\(\s*(?:"[^"\r\n]*"\s*\+\s*)?Gui\.(?:Localize|Format)\(\s*"(?<key>[^"\r\n]+)"'
    foreach ($path in ((Read-Git $grepArguments -AllowMissing) -split '\r?\n')) {
        if (-not $path) { continue }
        foreach ($match in [regex]::Matches((Get-CurrentContent $path),$pattern)) {
            [void]$toggleKeys.Add($match.Groups['key'].Value)
        }
    }
    if ($toggleKeys.Count -eq 0) { return 0 }

    $grepArguments = @('grep','--full-name','-n','-I','-E')
    if ($Staged) { $grepArguments += '--cached' } else { $grepArguments += '--untracked' }
    $grepArguments += @('^[^=]+/&[^=]+=','--',($translationRoot + '/ja'),($unofficialRoot + '/ja'))
    $checked = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in ((Read-Git $grepArguments -AllowMissing) -split '\r?\n')) {
        if ($line -notmatch '^(?<path>[^:]+):\d+:(?<key>[^=]+)=(?<value>.*)$') { continue }
        $path = $Matches['path']; $key = $Matches['key']; $value = $Matches['value']
        if (-not $toggleKeys.Contains($key)) { continue }
        $label = ([regex]::Replace($value,'<[^>]+>','') -split '\\n')[0].Trim()
        do {
            $before = $label
            $label = [regex]::Replace($label,'\s*[（(][^（）()]*[）)]\s*$','').Trim()
        } while ($before -cne $label)
        if ($label -notmatch '(ます|ません|です)[。]?$') {
            throw "日本語チェックボックスの文体を敬体に揃えてください: $path : $key = $value"
        }
        [void]$checked.Add($key)
    }
    foreach ($key in $toggleKeys) {
        if (-not $checked.Contains($key)) { throw "チェックボックスの日本語訳がありません: $key" }
    }
    return $checked.Count
}

function Assert-TranslationFileLayout([string]$Content, [string]$Path) {
    $text = $Content.TrimStart([char]0xFEFF)
    $blank = [regex]::Match($text,'(?m)^(?:[^\S\r\n]|\uFEFF)*\r?\n|^(?:[^\S\r\n]|\uFEFF)+\z')
    if (-not $blank.Success) { return }
    $number = ($text.Substring(0,$blank.Index) -split '\n').Count
    throw "翻訳ファイルに空白行があります: $Path : $number"
}

function Assert-JapaneseText([string]$Value, [string]$Context) {
    $text = [regex]::Replace($Value,'<[^>]+>|\{[^}]+\}|https?://\S+','')
    if ($text -match '[【】《》]') { throw "日本語訳に禁止された括弧があります: $Context" }
    if ($text -match '[^\x00-\x7F]:|:[^\x00-\x7F]') {
        throw "日本語のコロンは全角にしてください: $Context"
    }
    if ($text -match '[（(][^）)]*[（(〈]') { throw "日本語訳の括弧が入れ子になっています: $Context" }
}

function Read-JapaneseGlossary([string]$Path) {
    if (-not $Path) { return @() }
    $content = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $Path).Path,[Text.Encoding]::GetEncoding(932))
    $terms = @(foreach ($line in ($content -split '\r?\n')) {
        $columns = $line -split "`t"
        if ($columns.Count -lt 3 -or -not $columns[0] -or -not $columns[1]) { continue }
        $alternatives = @($columns[1] -split '[／/]')
        $preferred = @($alternatives | Where-Object { $_ -match '[一-鿿]' } | Select-Object -First 1)
        $japanese = if ($preferred.Count) { $preferred[0] } else { $alternatives[0] }
        $japanese = [regex]::Replace($japanese,'[（(][^）)]*[）)]','') -replace '[【】《》「」]',''
        $japanese = [regex]::Replace($japanese.Trim(),'^［([^［］]+)］$','$1')
        [pscustomobject]@{english=$columns[0];japanese=$japanese;category=$columns[2];reference=($columns | Select-Object -Skip 3) -join ' '}
    })
    if ($terms.Count -eq 0) { throw "用語集に有効な対訳がありません: $Path" }
    return $terms
}

function Get-EnglishTermPattern([string]$Term) {
    # Common inflections retain the same term; source context resolves homonyms.
    return [regex]::Replace([regex]::Escape($Term),'[a-zA-Z]{3,}',[Text.RegularExpressions.MatchEvaluator]{
        param($word)
        $value = $word.Value
        if ($value -match '[^aeiou]y$') { return $value.Substring(0,$value.Length-1) + '(?:y|ies|ied|ying)' }
        if ($value -match 'e$') { return $value.Substring(0,$value.Length-1) + '(?:e|es|ed|ing)' }
        if ($value -match '(ss|sh|ch|x|z)$') { return $value + '(?:es)?' }
        return $value + '(?:s|ed|ing)?'
    })
}

function Get-TerminologyIndex([object[]]$Terms) {
    $index = @{}
    foreach ($group in @($Terms | Group-Object english)) {
        $word = [regex]::Match($group.Name,'[a-zA-Z]+').Value.ToLowerInvariant()
        $prefix = $word.Substring(0,[Math]::Min(2,$word.Length))
        if (-not $index.ContainsKey($prefix)) { $index[$prefix] = [Collections.Generic.List[object]]::new() }
        $index[$prefix].Add([pscustomobject]@{Name=$group.Name;Group=$group.Group;Matcher=$null})
    }
    return $index
}

function Get-TerminologyMatch([string]$English, [hashtable]$Glossary) {
    $text = [regex]::Replace($English,'<[^>]+>|\{[^}]+\}','').Trim()
    $prefixes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void]$prefixes.Add('')
    foreach ($word in [regex]::Matches($text,'[a-zA-Z]+')) {
        [void]$prefixes.Add($word.Value.Substring(0,[Math]::Min(2,$word.Length)).ToLowerInvariant())
    }
    $candidates = @(foreach ($prefix in $prefixes) {
        if ($Glossary.ContainsKey($prefix)) { $Glossary[$prefix].ToArray() }
    })
    # Prefer the longest source term when a compound contains another glossary entry.
    $termMatches = [Collections.Generic.List[object]]::new()
    $occupied = [Collections.Generic.HashSet[int]]::new()
    $spans = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($group in @($candidates | Sort-Object { $_.Name.Length } -Descending)) {
        if ($null -eq $group.Matcher) {
            $pattern = '(?<![\p{L}\p{N}])' + (Get-EnglishTermPattern $group.Name) + '(?![\p{L}\p{N}])'
            $group.Matcher = [regex]::new($pattern,[Text.RegularExpressions.RegexOptions]::IgnoreCase)
        }
        foreach ($hit in $group.Matcher.Matches($text)) {
            $positions = @($hit.Index..($hit.Index + $hit.Length - 1))
            $span = "$($hit.Index):$($hit.Length)"
            if (-not $spans.Contains($span) -and @($positions | Where-Object { $occupied.Contains($_) }).Count) { continue }
            [void]$spans.Add($span)
            foreach ($position in $positions) { [void]$occupied.Add($position) }
            foreach ($term in $group.Group) {
                $termMatches.Add([pscustomobject]@{english=$term.english;japanese=$term.japanese;category=$term.category;
                    reference=$term.reference;wholeText=($hit.Index -eq 0 -and $hit.Length -eq $text.Length)})
            }
        }
    }
    return @($termMatches.ToArray())
}

try {
    if (-not $Staged) {
        $paths = Read-Git @('ls-files','--cached','--others','--exclude-standard','--',$translationRoot,$unofficialRoot)
        foreach ($path in @($paths -split '\r?\n' | Where-Object { $_ -like '*.txt' } | Sort-Object -Unique)) {
            Assert-TranslationFileLayout (Get-CurrentContent $path) $path
        }
    }
    $glossary = Get-TerminologyIndex @(Read-JapaneseGlossary $JapaneseGlossaryPath)
    $terminology = [Collections.Generic.List[object]]::new()
    $toggleCount = Assert-JapaneseToggleStyle
    $baseline = Read-Git @('rev-parse','--verify','-q',($BaseCommit + '^{commit}')) -AllowMissing
    if (-not $baseline -and $BaseCommit -ne 'HEAD') { throw "比較元のコミットがありません: $BaseCommit" }
    $diffArguments = @('diff','--name-only','--no-renames')
    if ($Staged) { $diffArguments += '--cached' }
    if ($baseline) { $diffArguments += $baseline }
    $diffArguments += @('--',$translationRoot,$unofficialRoot)
    $changed = @(Read-Git $diffArguments) -split '\r?\n'
    if (-not $Staged) {
        $changed += (Read-Git @('ls-files','--others','--exclude-standard','--',$translationRoot,$unofficialRoot)) -split '\r?\n'
        if (-not $baseline) { $changed += (Read-Git @('ls-files','--',$translationRoot,$unofficialRoot)) -split '\r?\n' }
    }
    $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $removedEnglish = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in @($changed | Where-Object { $_ -like '*.txt' } | Sort-Object -Unique)) {
        $before = if ($baseline) { Read-Git @('show',($baseline + ':' + $path)) -AllowMissing } else { '' }
        $old = Read-TranslationMap $before $path
        $content = Get-CurrentContent $path
        Assert-TranslationFileLayout $content $path
        $current = Read-TranslationMap $content $path
        foreach ($key in @($old.Keys) + @($current.Keys) | Sort-Object -Unique) {
            if (-not $old.ContainsKey($key) -or -not $current.ContainsKey($key) -or $old[$key] -cne $current[$key]) {
                [void]$keys.Add($key)
                if ($path.StartsWith($translationRoot + '/en/') -and $old.ContainsKey($key)) { [void]$removedEnglish.Add($key) }
            }
        }
    }
    if ($keys.Count -eq 0) {
        if ($TerminologyReportPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($TerminologyReportPath),'[]',[Text.UTF8Encoding]::new($false)) }
        Write-Output "翻訳: 変更キーなし、日本語チェックボックス$($toggleCount)件の文体を確認"; exit 0
    }

    $pattern = '^(' + ((@($keys) | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')='
    $grepArguments = @('grep','--full-name','-n','-I','-E')
    if ($Staged) { $grepArguments += '--cached' } else { $grepArguments += '--untracked' }
    $grepArguments += @($pattern,'--',$translationRoot,$unofficialRoot)
    $records = @(foreach ($line in ((Read-Git $grepArguments -AllowMissing) -split '\r?\n')) {
        if ($line -match '^(?<path>[^:]+):(?<line>\d+):(?<key>[^=]+)=(?<value>.*)$') {
            $path = $Matches['path']; $key = $Matches['key']; $value = $Matches['value']
            $locale = if ($path -match '^SolastaUnfinishedBusiness/Translations/([^/]+)/') { $Matches[1] } else { $null }
            [pscustomobject]@{path=$path;key=$key;value=$value;locale=$locale}
        }
    })
    $checked = 0
    foreach ($key in $keys) {
        $definitions = @($records | Where-Object key -CEQ $key)
        foreach ($definition in $definitions) {
            $context = $definition.path + ' : ' + $key
            Assert-Text $definition.value $context
            if ($definition.locale -eq 'ja' -or $definition.path -like ($unofficialRoot + '/ja/*')) {
                Assert-JapaneseText $definition.value $context
            }
        }
        $english = @($definitions | Where-Object locale -EQ 'en')
        if ($english.Count -eq 0) {
            if ($removedEnglish.Contains($key) -and $definitions.Count -gt 0) { throw "削除した英語キーの翻訳が残っています: $key" }
            # Locale-only overrides of native game terms have no mod-owned English source.
            continue
        }
        if ($glossary.Count) {
            $japanese = @($definitions | Where-Object locale -EQ 'ja')
            if ($japanese.Count -eq 1) {
                $sourceTerms = @(Get-TerminologyMatch $english[0].value $glossary)
                $text = [regex]::Replace($japanese[0].value,'<[^>]+>','')
                $titleTerms = @($sourceTerms | Where-Object wholeText)
                if ($key -match 'Title$' -and $titleTerms.Count -gt 0 -and
                    @($titleTerms | Where-Object { $text -ceq $_.japanese }).Count -eq 0) {
                    throw "公式用語集と表示名が一致しません: $key : $($english[0].value) = $(($titleTerms.japanese | Sort-Object -Unique) -join ' / ')"
                }
                foreach ($term in $sourceTerms) {
                    $present = $text.Contains($term.japanese)
                    $terminology.Add([pscustomobject]@{key=$key;english=$english[0].value;japanese=$japanese[0].value;
                        sourceTerm=$term.english;preferredTerm=$term.japanese;category=$term.category;reference=$term.reference;matched=$present})
                }
            }
        }
        $signature = Get-TokenSignature $english[0].value
        foreach ($locale in $locales) {
            $localized = @($definitions | Where-Object locale -EQ $locale)
            if ($localized.Count -ne 1) { throw "翻訳は各言語に1件必要です: $locale : $key ($($localized.Count)件)" }
            if ((Get-TokenSignature $localized[0].value) -cne $signature) { throw "プレースホルダー・タグ・改行が一致しません: $locale : $key" }
            $checked++
        }
        foreach ($override in @($definitions | Where-Object { -not $_.locale })) {
            if ((Get-TokenSignature $override.value) -cne $signature) { throw "非公式訳のプレースホルダー・タグ・改行が一致しません: $($override.path) : $key" }
        }
    }
    if ($TerminologyReportPath) {
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($TerminologyReportPath),
            (ConvertTo-Json -InputObject @($terminology.ToArray()) -Depth 5),[Text.UTF8Encoding]::new($false))
    }
    Write-Output "翻訳: $($keys.Count)変更キー、$($checked)件の言語整合性、日本語チェックボックス$($toggleCount)件の文体を確認"
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
