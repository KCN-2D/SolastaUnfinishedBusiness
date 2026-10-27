Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$gitExecutable = (Get-Command git.exe -ErrorAction Stop).Source
$gitRoot = Split-Path (Split-Path $gitExecutable -Parent) -Parent
$shellCandidates = @(
    (Join-Path $gitRoot 'bin\sh.exe'),
    (Join-Path $gitRoot 'usr\bin\sh.exe')
)
$shellPath = $shellCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $shellPath) {
    throw 'Git for Windowsのsh.exeが見つかりません。'
}

$cases = @(
    @{ Name = 'Japanese subject and body'; Pass = $true; Text = "処理を修正`n`n入力の検証を追加した。" },
    @{ Name = 'English subject'; Pass = $false; Text = "Update processing`n`n入力の検証を追加した。" },
    @{ Name = 'English body'; Pass = $false; Text = "処理を修正`n`nValidate the input." },
    @{ Name = 'Single English word is prose'; Pass = $false; Text = "処理を修正`n`nImprove" },
    @{ Name = 'Later English paragraph'; Pass = $false; Text = "処理を修正`n`n入力の検証を追加した。`n`nCheck the returned value." },
    @{ Name = 'English list item'; Pass = $false; Text = "処理を修正`n`n- 入力の検証を追加した。`n- Check the returned value." },
    @{ Name = 'Fixed values and trailers'; Pass = $true; Text = "処理を修正`n`n入力と結果を照合した。`n`nmodule.cs`nv1.2.3`nhttps://example.invalid/reference`n`nCo-authored-by: Example <example@example.invalid>`nSigned-off-by: Example <example@example.invalid>" },
    @{ Name = 'Code block'; Pass = $true; Text = "処理を修正`n`n検証コマンドを記録した。`n`n``````text`ngit diff --check`n``````" },
    @{ Name = 'Longer code fence with an inner short fence'; Pass = $true; Text = "処理を修正`n`n~~~~text`n``` example`n~~~~" },
    @{ Name = 'Wrapped Japanese paragraph'; Pass = $true; Text = "処理を修正`n`n入力の検証を追加し、`n返された結果も確認した。" },
    @{ Name = 'Comments do not supply Japanese'; Pass = $false; Text = "# 日本語の説明`nUpdate processing`n`nValidate the input." },
    @{ Name = 'Merge has no exemption'; Pass = $false; Text = "Merge branch 'example'`n`n変更を統合した。" },
    @{ Name = 'Revert English body has no exemption'; Pass = $false; Text = "Revert 日本語の変更`n`nThis reverts commit 0123456789abcdef." },
    @{ Name = 'CRLF and BOM'; Pass = $true; Bom = $true; Text = "処理を修正`r`n`r`n結果を確認した。`r`n" },
    @{ Name = 'Invalid UTF8'; Pass = $false; Bytes = [byte[]] @(0xFF, 0xFE, 0xFF) }
)

$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$temporaryDirectory = [System.IO.Path]::GetFullPath((Join-Path $temporaryRoot ('solasta commit message ' + [guid]::NewGuid().ToString('N'))))
if (-not $temporaryDirectory.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [System.IO.Path]::GetDirectoryName($temporaryDirectory) -ne $temporaryRoot.TrimEnd('\')) {
    throw '契約テストの一時フォルダーが指定した保存先の直下ではありません。'
}
[System.IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null
$failures = New-Object 'System.Collections.Generic.List[string]'
function Invoke-TestGit([string[]] $Arguments) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $gitExecutable
    $info.Arguments = (@($Arguments | ForEach-Object {'"' + $_.Replace('"','\"') + '"'}) -join ' ')
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    $process = [Diagnostics.Process]::Start($info)
    try {
        $output = $process.StandardOutput.ReadToEnd()
        $errorOutput = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        [pscustomobject]@{exitCode=$process.ExitCode;output=$output;errorOutput=$errorOutput}
    } finally { $process.Dispose() }
}
try {
    for ($index = 0; $index -lt $cases.Count; $index++) {
        $case = $cases[$index]
        $messagePath = Join-Path $temporaryDirectory ("case-$index.txt")
        if ($case.ContainsKey('Bytes')) {
            [System.IO.File]::WriteAllBytes($messagePath, $case.Bytes)
        } else {
            $writeBom = $case.ContainsKey('Bom') -and $case.Bom
            $encoding = New-Object System.Text.UTF8Encoding($writeBom)
            [System.IO.File]::WriteAllText($messagePath, $case.Text, $encoding)
        }
        $processInfo = New-Object System.Diagnostics.ProcessStartInfo
        $processInfo.FileName = $shellPath
        $hookPath = (Join-Path $PSScriptRoot 'commit-msg').Replace('\', '/')
        $processInfo.Arguments = '"' + $hookPath + '" "' + $messagePath.Replace('\', '/') + '"'
        $processInfo.UseShellExecute = $false
        $processInfo.CreateNoWindow = $true
        $processInfo.RedirectStandardOutput = $true
        $processInfo.RedirectStandardError = $true
        $processInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
        $processInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8
        $process = [System.Diagnostics.Process]::Start($processInfo)
        $output = $process.StandardOutput.ReadToEnd()
        $errorOutput = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $actualPass = $process.ExitCode -eq 0
        if ($actualPass -ne $case.Pass) {
            $failures.Add("$($case.Name): exit=$($process.ExitCode); $output$errorOutput")
        }
        $process.Dispose()
    }
    # Exercise Git itself, including its working directory, comment cleanup,
    # commit rejection and a fast-forward importing an existing English commit.
    $fixture = Join-Path $temporaryDirectory 'repository with spaces'
    [void][IO.Directory]::CreateDirectory($fixture)
    $hookDirectory = $PSScriptRoot.Replace('\','/')
    & $gitExecutable init --quiet $fixture
    if ($LASTEXITCODE) { throw '契約テスト用リポジトリを作成できません。' }
    & $gitExecutable -C $fixture config user.name 'Fixture Example'
    & $gitExecutable -C $fixture config user.email 'fixture@example.invalid'
    & $gitExecutable -C $fixture config core.hooksPath $hookDirectory
    & $gitExecutable -C $fixture config core.commentChar ';'
    $gitCases = @(
        @{Name='Git accepts Japanese and strips the configured comment';Pass=$true;Text="処理を修正`n`n; An English Git comment."},
        @{Name='Git does not use Japanese in the configured comment';Pass=$false;Text="; 日本語コメント`nEnglish subject"},
        @{Name='Git rejects an English body without creating a commit';Pass=$false;Text="処理を修正`n`nValidate the input."},
        @{Name='Git accepts technical data in a Japanese explanation';Pass=$true;Text="処理を修正`n`n入力の検証を追加した。`n`nExample.Type`nv1.2.3"}
    )
    $expectedCount = 0
    foreach ($case in $gitCases) {
        $messagePath = Join-Path $temporaryDirectory 'git-message.txt'
        [IO.File]::WriteAllText($messagePath,$case.Text,[Text.UTF8Encoding]::new($false))
        # The hook must use the invoking repository's Git configuration, not the
        # repository where a shared hooksPath happens to be stored.
        $commit = Invoke-TestGit @('-C',$fixture,'-c','commit.gpgsign=false','commit','--quiet','--allow-empty','--cleanup=strip',('--file='+$messagePath))
        $accepted = $commit.exitCode -eq 0
        if ($case.Pass) { $expectedCount++ }
        $count = [int](& $gitExecutable -C $fixture rev-list --count HEAD)
        if ($accepted -ne $case.Pass -or $count -ne $expectedCount) {
            $failures.Add("$($case.Name): accepted=$accepted; count=$count; $($commit.output)$($commit.errorOutput)")
        }
    }
    & $gitExecutable -C $fixture branch imported
    & $gitExecutable -C $fixture checkout --quiet imported
    & $gitExecutable -C $fixture -c core.hooksPath= -c commit.gpgsign=false commit --quiet --allow-empty --message='Imported upstream change'
    $imported = & $gitExecutable -C $fixture rev-parse HEAD
    & $gitExecutable -C $fixture checkout --quiet -
    $mergeOutput = & $gitExecutable -C $fixture merge --quiet --ff-only imported 2>&1
    if ($LASTEXITCODE -ne 0 -or (& $gitExecutable -C $fixture rev-parse HEAD) -cne $imported) {
        $failures.Add("Fast-forward import changed or rejected existing history: $mergeOutput")
    }
} finally {
    # This exact directory was created above; do not discover deletion targets.
    # Git object files are read-only on Windows.
    foreach ($item in Get-ChildItem -LiteralPath $temporaryDirectory -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "契約テストの後始末でreparse pointを検出しました: $($item.FullName)"
        }
        if (-not $item.PSIsContainer) { $item.IsReadOnly = $false }
    }
    [System.IO.Directory]::Delete($temporaryDirectory, $true)
}
if ($failures.Count) {
    throw ($failures -join [Environment]::NewLine)
}
Write-Output "PASS: $($cases.Count) hook-entry cases and $($gitCases.Count + 1) actual Git commit/import contracts."
