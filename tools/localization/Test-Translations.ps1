[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$BaseCommit = 'HEAD',
    [switch]$Staged
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
if (-not $RepositoryRoot) { $RepositoryRoot = Join-Path $PSScriptRoot '../..' }
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$locales = @('en','de','es','fr','it','ja','ko','pt-BR','ru','zh-CN')
$translationRoot = 'SolastaUnfinishedBusiness/Translations'
$unofficialRoot = 'SolastaUnfinishedBusiness/UnofficialTranslations'

function Read-Git([string[]]$Arguments, [switch]$AllowMissing) {
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& git -C $root -c core.quotepath=false @Arguments 2>$null)
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previousPreference }
    $missing = $AllowMissing -and (($Arguments[0] -eq 'grep' -and $code -eq 1) -or
        ($Arguments[0] -eq 'show' -and $code -eq 128) -or ($Arguments[0] -eq 'rev-parse' -and $code -eq 1))
    if ($code -ne 0 -and -not $missing) { throw "Git の読み取りに失敗しました: $($Arguments[0])" }
    return $output -join "`n"
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
    if ($Staged) { return Read-Git @('show',(':' + $Path)) -AllowMissing }
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

try {
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
        $current = Read-TranslationMap (Get-CurrentContent $path) $path
        foreach ($key in @($old.Keys) + @($current.Keys) | Sort-Object -Unique) {
            if (-not $old.ContainsKey($key) -or -not $current.ContainsKey($key) -or $old[$key] -cne $current[$key]) {
                [void]$keys.Add($key)
                if ($path.StartsWith($translationRoot + '/en/') -and $old.ContainsKey($key)) { [void]$removedEnglish.Add($key) }
            }
        }
    }
    if ($keys.Count -eq 0) { Write-Output '翻訳: 変更キーなし'; exit 0 }

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
        foreach ($definition in $definitions) { Assert-Text $definition.value ($definition.path + ' : ' + $key) }
        $english = @($definitions | Where-Object locale -EQ 'en')
        if ($english.Count -eq 0) {
            if ($removedEnglish.Contains($key) -and $definitions.Count -gt 0) { throw "削除した英語キーの翻訳が残っています: $key" }
            # Locale-only overrides of native game terms have no mod-owned English source.
            continue
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
    Write-Output "翻訳: $($keys.Count)変更キー、$($checked)件の言語整合性を確認"
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
