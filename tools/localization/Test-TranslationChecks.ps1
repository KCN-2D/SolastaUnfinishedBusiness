[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$validator = Join-Path $PSScriptRoot 'Test-Translations.ps1'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('ub-translation-check-' + [guid]::NewGuid().ToString('N'))
$locales = @('en','de','es','fr','it','ja','ko','pt-BR','ru','zh-CN')
$results = [Collections.Generic.List[object]]::new()
$seed = 'UI/&Seed=Seed' + "`n"
$valid = $seed + 'UI/&Probe=Text {0} <b>{1}</b>\nNext' + "`n"

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
}

function Assert-Validation([string]$Name, [bool]$Pass, [switch]$Staged, [string]$ErrorPattern) {
    $arguments = @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$validator,'-RepositoryRoot',$temporary,'-BaseCommit',$script:baseline)
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
    Write-Output "PASS: $($results.Count) translation validation cases."
} finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $expectedBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\ub-translation-check-'
    if (-not $resolved.StartsWith($expectedBase,[StringComparison]::OrdinalIgnoreCase)) { throw 'Temporary path escaped its owner.' }
    if ([IO.Directory]::Exists($resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
