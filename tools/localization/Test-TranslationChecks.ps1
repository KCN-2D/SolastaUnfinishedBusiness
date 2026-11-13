[CmdletBinding()]
param([string]$PythonPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$validator = Join-Path $PSScriptRoot 'Test-Translations.ps1'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('ub-translation-check-' + [guid]::NewGuid().ToString('N'))
$locales = @('en','de','es','fr','it','ja','ko','pt-BR','ru','zh-CN')
$results = [Collections.Generic.List[object]]::new()
$seed = 'UI/&Seed=Seed' + "`n"
$valid = $seed + 'UI/&Probe=Text {0} <b>{1}</b>\nNext' + "`n"
if (-not $PythonPath) { $PythonPath = Join-Path $PSScriptRoot '../asset-authoring/workspace/Python/Scripts/python.exe' }

function Invoke-FixtureGit([string[]]$Arguments) {
    $output = & git -C $temporary @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($output -join "`n") }
    return $output
}

function Write-FixtureText([string]$Locale, [string]$Content) {
    $path = Join-Path $temporary "SolastaUnfinishedBusiness/Translations/$Locale/UI-$Locale.txt"
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $path))
    [IO.File]::WriteAllText($path,$Content,[Text.UTF8Encoding]::new($false))
}

function Initialize-Fixture {
    [void](Invoke-FixtureGit @('reset','--quiet',$script:baseline))
    foreach ($locale in $locales) { Write-FixtureText $locale $valid }
    $duplicate = Join-Path $temporary 'SolastaUnfinishedBusiness/Translations/de/Duplicate-de.txt'
    if ([IO.File]::Exists($duplicate)) { Remove-Item -LiteralPath $duplicate }
    foreach ($path in @('SolastaUnfinishedBusiness/Displays/Probe.cs','SolastaUnfinishedBusiness/UnofficialTranslations/ja/Probe-ja.txt')) {
        $fullPath = Join-Path $temporary $path
        if ([IO.File]::Exists($fullPath)) { Remove-Item -LiteralPath $fullPath }
    }
}

function Assert-Validation([string]$Name, [bool]$Pass, [switch]$Staged, [string]$ErrorPattern) {
    $arguments = @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$validator,'-RepositoryRoot',$temporary,'-BaseCommit',$script:baseline,
        '-JapaneseGlossaryPath',(Join-Path $temporary 'Glossary.txt'))
    if ($Staged) { $arguments += '-Staged' }
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& powershell.exe @arguments 2>&1 | ForEach-Object { $_.ToString() }) -join "`n"
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previousPreference }
    if (($code -eq 0) -ne $Pass -or ($ErrorPattern -and $output -notmatch $ErrorPattern)) { throw "$Name failed: $output" }
    $results.Add([pscustomobject]@{name=$Name;passed=$true})
}

try {
    [void][IO.Directory]::CreateDirectory($temporary)
    [IO.File]::WriteAllText((Join-Path $temporary 'Glossary.txt'),
        ('Source Feature' + "`t" + 'ソース・フィーチャー／出典特徴' + "`t" + 'クラス特徴' + "`n" +
         'Source Spell' + "`t" + 'ソース・スペル／出典呪文' + "`t" + '呪文' + "`n" +
         'Fortify Spell' + "`t" + '呪文補強' + "`t" + 'クラス特徴' + "`n" +
         'Source Feature Bonus' + "`t" + '出典特徴ボーナス' + "`t" + 'ルール' + "`n" +
         'Force' + "`t" + '［力場］' + "`t" + 'ダメージ種別' + "`n" +
         'Source Feature [Wisdom]' + "`t" + '出典特徴［判断力］' + "`t" + 'クラス特徴'),[Text.Encoding]::GetEncoding(932))
    [void](Invoke-FixtureGit @('init','--quiet'))
    [void](Invoke-FixtureGit @('config','core.autocrlf','false'))
    foreach ($locale in $locales) { Write-FixtureText $locale $seed }
    [void](Invoke-FixtureGit @('add','.'))
    [void](Invoke-FixtureGit @('-c','user.name=Translation Test','-c','user.email=translation-test@example.invalid','commit','--quiet','-m','Seed'))
    $script:baseline = [string](Invoke-FixtureGit @('rev-parse','HEAD'))

    Initialize-Fixture
    Assert-Validation 'all locales' $true
    Write-FixtureText 'de' $seed
    Assert-Validation 'missing locale' $false -ErrorPattern 'de'
    Initialize-Fixture
    Write-FixtureText 'de' ($valid.Replace('{1}','{2}'))
    Assert-Validation 'placeholder mismatch' $false -ErrorPattern 'de'
    Initialize-Fixture
    Write-FixtureText 'en' ($valid.Replace('</b>',''))
    Assert-Validation 'unclosed tag' $false -ErrorPattern 'en'
    Initialize-Fixture
    [IO.File]::WriteAllText((Join-Path $temporary 'SolastaUnfinishedBusiness/Translations/de/Duplicate-de.txt'),'UI/&Probe=Duplicate',[Text.UTF8Encoding]::new($false))
    Assert-Validation 'duplicate across files' $false -ErrorPattern 'de'
    Initialize-Fixture
    foreach ($locale in $locales) { Write-FixtureText $locale $seed }
    Write-FixtureText 'ja' ($seed + 'Feature/&NativeProbe=Localized')
    Assert-Validation 'native locale override' $true
    Initialize-Fixture
    [void](Invoke-FixtureGit @('add','.'))
    Write-FixtureText 'de' ($valid.Replace('{1}','{2}'))
    Assert-Validation 'valid index with invalid working copy' $true -Staged
    Assert-Validation 'invalid working copy with valid index' $false -ErrorPattern 'de'
    Initialize-Fixture
    Write-FixtureText 'en' ''
    Assert-Validation 'orphaned translations' $false -ErrorPattern 'UI/&Seed'

    foreach ($content in @(("`n" + $valid),($valid + "`n"),($valid + " `t`n"),([string][char]0xFEFF + "`n" + $valid))) {
        Initialize-Fixture
        Write-FixtureText 'ja' $content
        Assert-Validation 'blank translation row' $false -ErrorPattern '空白行'
    }
    Initialize-Fixture
    foreach ($locale in $locales) { Write-FixtureText $locale $seed }
    Write-FixtureText 'ja' ($seed + "`n")
    Assert-Validation 'layout-only change' $false -ErrorPattern '空白行'
    Initialize-Fixture
    [void](Invoke-FixtureGit @('add','.'))
    Write-FixtureText 'ja' ($valid + "`n")
    Assert-Validation 'blank working copy with valid index' $true -Staged
    Assert-Validation 'blank working copy is rejected' $false -ErrorPattern '空白行'
    [void](Invoke-FixtureGit @('add','.'))
    Assert-Validation 'blank indexed row' $false -Staged -ErrorPattern '空白行'

    foreach ($value in @('【能力】です。','《能力》です。','条件:成功します。','条件（判定（知力））です。','対象（〈呪文〉）です。')) {
        Initialize-Fixture
        Write-FixtureText 'ja' ($valid + 'Feature/&NativeProbe=' + $value)
        Assert-Validation ('Japanese text rejected: ' + $value) $false -ErrorPattern 'NativeProbe'
    }
    Initialize-Fixture
    Write-FixtureText 'ja' ($valid + 'Feature/&NativeProbe=セーヴ難易度とセーヴィング・スローを確認します： https://example.invalid/a:b')
    Assert-Validation 'official compound term and URL preserved' $true

    foreach ($pair in @(@('Source Feature','出典特徴'),@('Source Spell','出典呪文'),@('Source Features','出典特徴'),@('Fortified Spell','呪文補強'),@('Source Feature Bonus','出典特徴ボーナス'),@('Force','力場'),@('Source Feature [Wisdom]','出典特徴［判断力］'))) {
        Initialize-Fixture
        foreach ($locale in $locales) { Write-FixtureText $locale ($valid + 'Feature/&ReferenceProbeTitle=' + $pair[0]) }
        Write-FixtureText 'ja' ($valid + 'Feature/&ReferenceProbeTitle=' + $pair[1])
        Assert-Validation ('glossary preferred title: ' + $pair[0]) $true
        Write-FixtureText 'ja' ($valid + 'Feature/&ReferenceProbeTitle=独自の訳語')
        Assert-Validation ('glossary rejects title: ' + $pair[0]) $false -ErrorPattern 'ReferenceProbeTitle'
    }
    Initialize-Fixture

    $sourcePath = Join-Path $temporary 'SolastaUnfinishedBusiness/Displays/Probe.cs'
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $sourcePath))
    $source = 'UI.Toggle(Gui.Localize("ModUi/&ToggleProbe"), ref toggle); UI.ActionButton(Gui.Localize("ModUi/&ButtonProbe"), action); UI.Label(Gui.Localize("ModUi/&HeadingProbe"));'
    $labels = $valid + 'ModUi/&ToggleProbe=<color=#D89555>範囲</color>を広げます（再起動が必要）' + "`n" +
        'ModUi/&ButtonProbe=範囲を広げる' + "`n" + 'ModUi/&HeadingProbe=範囲の設定' + "`n"
    [IO.File]::WriteAllText($sourcePath,$source,[Text.UTF8Encoding]::new($false))
    Write-FixtureText 'ja' $labels
    Assert-Validation 'checkbox, button and heading roles' $true
    Write-FixtureText 'ja' ($labels.Replace('広げます','広げる'))
    Assert-Validation 'plain checkbox label' $false -ErrorPattern 'ToggleProbe'
    Write-FixtureText 'ja' ($labels.Replace('広げます','拡張'))
    Assert-Validation 'noun checkbox label' $false -ErrorPattern 'ToggleProbe'
    Write-FixtureText 'ja' $labels
    [void](Invoke-FixtureGit @('add','.'))
    Write-FixtureText 'ja' ($labels.Replace('広げます','広げる'))
    Assert-Validation 'checkbox valid index' $true -Staged
    Assert-Validation 'checkbox invalid working copy' $false -ErrorPattern 'ToggleProbe'
    Write-FixtureText 'ja' $labels
    [IO.File]::WriteAllText($sourcePath,$source.Replace('UI.Toggle','UI.ActionButton'),[Text.UTF8Encoding]::new($false))
    Write-FixtureText 'ja' ($labels.Replace('広げます','広げる'))
    Assert-Validation 'working call site determines role' $true
    Assert-Validation 'staged call site determines role' $true -Staged
    Write-FixtureText 'ja' $labels
    [IO.File]::WriteAllText($sourcePath,$source,[Text.UTF8Encoding]::new($false))
    $overridePath = Join-Path $temporary 'SolastaUnfinishedBusiness/UnofficialTranslations/ja/Probe-ja.txt'
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $overridePath))
    [IO.File]::WriteAllText($overridePath,'ModUi/&ToggleProbe=範囲を広げる',[Text.UTF8Encoding]::new($false))
    Assert-Validation 'unofficial override style' $false -ErrorPattern 'ToggleProbe'

    $readerPath = Join-Path $temporary 'Readers.py'
    $readerFixture = @'
import sys
sys.dont_write_bytecode = True
import contextlib
import importlib.util
import io
import json
from pathlib import Path

source, fixture = map(Path, sys.argv[1:])
sample = fixture / 'Records.txt'
sample.write_text('\ufeff\nUI/&First=Alpha {0} <b>Beta</b>\\nTail\n \t\nUI/&Second=Gamma=Delta\n\n', encoding='utf-8')
expected = {'UI/&First': 'Alpha {0} <b>Beta</b>\\nTail', 'UI/&Second': 'Gamma=Delta'}
checks = []
def load(name):
    spec = importlib.util.spec_from_file_location(name, source / (name + '.py'))
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module

for name, reader in [('translate', 'get_records'), ('translateAutoSync', 'readRecord'),
                     ('translateUnofficialAutoSync', 'readRecord'), ('translateLLM', 'read_localization_file')]:
    module = load(name)
    with contextlib.redirect_stdout(io.StringIO()):
        records = dict(getattr(module, reader)(str(sample)))
    assert records == expected, name
    checks.append({'name': 'blank record handling: ' + name, 'passed': True})

normalizer = load('cleanseTerms')
folder = fixture / 'Normalize'
folder.mkdir()
target = folder / 'Records.txt'
target.write_bytes(sample.read_bytes())
sys.argv = ['cleanseTerms.py', str(folder)]
with contextlib.redirect_stdout(io.StringIO()):
    normalizer.main()
assert target.read_text(encoding='utf-8').splitlines() == [key + '=' + value for key, value in expected.items()]
before = target.read_bytes()
with contextlib.redirect_stdout(io.StringIO()):
    normalizer.main()
assert target.read_bytes() == before
checks.append({'name': 'normalization removes blank rows and preserves tokens', 'passed': True})
checks.append({'name': 'normalization is idempotent', 'passed': True})
target.write_text('UI/&Last=Gamma=Delta\nUI/&First=Alpha {0} <b>Beta</b>\\nTail', encoding='utf-8')
with contextlib.redirect_stdout(io.StringIO()):
    normalizer.main()
assert target.read_text(encoding='utf-8').splitlines() == [
    'UI/&First=Alpha {0} <b>Beta</b>\\nTail', 'UI/&Last=Gamma=Delta']
checks.append({'name': 'normalization preserves unterminated records when sorting', 'passed': True})
print(json.dumps(checks))
'@
    [IO.File]::WriteAllText($readerPath,$readerFixture,[Text.UTF8Encoding]::new($false))
    if (-not [IO.File]::Exists($PythonPath)) { throw "共通Python環境がありません: $PythonPath" }
    $readerOutput = & $PythonPath $readerPath $PSScriptRoot $temporary
    if ($LASTEXITCODE -ne 0) { throw '翻訳読み書きの検証に失敗しました。' }
    foreach ($result in @($readerOutput | ConvertFrom-Json)) { $results.Add($result) }
    Write-Output "PASS: $($results.Count) translation validation cases."
} finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $expectedBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\ub-translation-check-'
    if (-not $resolved.StartsWith($expectedBase,[StringComparison]::OrdinalIgnoreCase)) { throw 'Temporary path escaped its owner.' }
    if ([IO.Directory]::Exists($resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
