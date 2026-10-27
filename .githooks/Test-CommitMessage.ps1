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
    @{ Name = 'Japanese subject and body'; Pass = $true; Text = "装備表示を改善`n`n形状の所有元を使って再計算を抑制した。" },
    @{ Name = 'English subject'; Pass = $false; Text = "Fix equipment presentation`n`n不要な再計算を抑制した。" },
    @{ Name = 'English body'; Pass = $false; Text = "装備表示を改善`n`nAvoid repeated mesh recalculation." },
    @{ Name = 'Later English paragraph'; Pass = $false; Text = "装備表示を改善`n`n不要な再計算を抑制した。`n`nKeep the original material ownership." },
    @{ Name = 'English list item'; Pass = $false; Text = "装備表示を改善`n`n- 不要な再計算を抑制した。`n- Keep the original material ownership." },
    @{ Name = 'Short Japanese subject'; Pass = $true; Text = '装備表示を改善' },
    @{ Name = 'Technical data only'; Pass = $true; Text = "装備表示を改善`n`nCustomModels.cs`nhttps://example.invalid/reference`nSigned-off-by: Example <example@example.invalid>" },
    @{ Name = 'Fixed values and trailers'; Pass = $true; Text = "装備表示を改善`n`n共通の所有元から形状を取得する。`n`nCustomModels.cs`nnet481`nhttps://example.invalid/reference`n`nCo-authored-by: Example <example@example.invalid>`nSigned-off-by: Example <example@example.invalid>" },
    @{ Name = 'Code block'; Pass = $true; Text = "装備表示を改善`n`n検証コマンドを記録した。`n`n``````text`ngit diff --check`n``````" },
    @{ Name = 'Wrapped Japanese paragraph'; Pass = $true; Text = "装備表示を改善`n`n測定結果から再計算の条件を絞り、`nCustomModels.csの所有関係を維持した。" },
    @{ Name = 'Comments do not supply Japanese'; Pass = $false; Text = "# 日本語の説明`nFix equipment presentation`n`nAvoid repeated mesh recalculation." },
    @{ Name = 'Merge has no exemption'; Pass = $false; Text = "Merge branch 'custom/work-playtest'`n`n検証した変更を統合した。" },
    @{ Name = 'Revert English body has no exemption'; Pass = $false; Text = "Revert 日本語の変更`n`nThis reverts commit 0123456789abcdef." },
    @{ Name = 'CRLF and BOM'; Pass = $true; Bom = $true; Text = "装備表示を改善`r`n`r`n実環境で表示を確認した。`r`n" },
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
} finally {
    # This exact directory was created above; do not discover deletion targets.
    [System.IO.Directory]::Delete($temporaryDirectory, $true)
}
if ($failures.Count) {
    throw ($failures -join [Environment]::NewLine)
}
Write-Output "PASS: $($cases.Count) commit-message cases through the Git hook entry."
