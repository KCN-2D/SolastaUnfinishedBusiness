[CmdletBinding()]
param([string]$PlanPath, [string]$HookPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Invoke-RequiredChecks.ps1'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('ub-codex-hook-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporary)
$missing = Join-Path $temporary 'missing.json'
$cases = @(
    @{name='read-only command';event='PreToolUse';command='git status --short';expected='allow'},
    @{name='Japanese read-only command';event='PreToolUse';command=('Write-Output "'+[char]0x65e5+[char]0x672c+[char]0x8a9e+'"');expected='allow'},
    @{name='quoted prose';event='PreToolUse';command='Write-Output "git commit --amend"';expected='allow'},
    @{name='commit';event='PreToolUse';command='git commit -m "test"';expected='deny'},
    @{name='push with global options';event='PreToolUse';command='git -C "E:\sample project" -c core.quotepath=false push';expected='deny'},
    @{name='explicit executable';event='PreToolUse';command='& "C:\Program Files\Git\cmd\git.exe" commit --amend';expected='deny'},
    @{name='stop';event='Stop';expected='block'},
    @{name='continued stop';event='Stop';active=$true;expected='block'}
)
if ($PlanPath) { $cases += @{name='verified project';event='Stop';expected='allow';plan=$PlanPath} }
$results = [Collections.Generic.List[object]]::new()
try {
    . (Join-Path $PSScriptRoot '../game-test/GameTest.Common.ps1')
    $reportPath = Join-Path $temporary 'Checks.json'
    $requirements = [pscustomobject]@{reportFile='Checks.json';checks=@([pscustomobject]@{name='resource recovery';minimumCount=1},[pscustomobject]@{name='localized display';minimumCount=2})}
    $validChecks = @([pscustomobject]@{name='resource recovery';passed=$true},[pscustomobject]@{name='localized display';passed=$true},[pscustomobject]@{name='localized display';passed=$true})
    foreach ($mode in @('valid','missing-report','missing-requirement','insufficient-count','failed-check','string-status','duplicate-report','path-escape')) {
        $rows = @($validChecks | ForEach-Object { [pscustomobject]@{name=$_.name;passed=$_.passed} })
        if ($mode -eq 'missing-requirement') { $rows = @($rows | Where-Object name -NE 'resource recovery') }
        if ($mode -eq 'insufficient-count') { $rows = @($rows | Select-Object -First 2) }
        if ($mode -eq 'failed-check') { $rows[0].passed = $false }
        if ($mode -eq 'string-status') { $rows[0].passed = 'true' }
        Write-GtJson $reportPath ([ordered]@{checks=$rows})
        if ($mode -eq 'missing-report') { Remove-Item -LiteralPath $reportPath }
        if ($mode -eq 'duplicate-report') { [void][IO.Directory]::CreateDirectory((Join-Path $temporary 'Duplicate')); Copy-Item -LiteralPath $reportPath -Destination (Join-Path $temporary 'Duplicate/Checks.json') }
        $requirements.reportFile = if ($mode -eq 'path-escape') { '../Checks.json' } else { 'Checks.json' }
        $passed = $false
        try { Assert-GtRequiredEvidence $temporary $requirements; $passed = $true } catch { if ($mode -eq 'valid') { throw } }
        if ($passed -ne ($mode -eq 'valid')) { throw "Evidence requirement contract failed: $mode" }
        $results.Add([pscustomobject]@{name=('evidence requirements: ' + $mode);passed=$true})
        $duplicatePath = Join-Path $temporary 'Duplicate/Checks.json'
        if ([IO.File]::Exists($duplicatePath)) { Remove-Item -LiteralPath $duplicatePath }
    }
    foreach ($case in $cases) {
        $inputObject = @{hook_event_name=$case.event;stop_hook_active=($case.ContainsKey('active') -and $case.active);tool_input=@{command=$(if($case.ContainsKey('command')){$case.command}else{''})}}
        $casePlan = if ($case.ContainsKey('plan')) { $case.plan } else { $missing }
        $start = [Diagnostics.ProcessStartInfo]::new('powershell.exe',('-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $scriptPath + '" -CodexHook -PlanPath "' + $casePlan + '"'))
        $start.UseShellExecute=$false;$start.CreateNoWindow=$true
        $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
        $start.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
        $process=[Diagnostics.Process]::Start($start)
        $inputBytes=[Text.UTF8Encoding]::new($false).GetBytes(($inputObject|ConvertTo-Json -Depth 5 -Compress)+[Environment]::NewLine)
        # Keep the JSON bytes independent of the parent console's code page.
        $process.StandardInput.BaseStream.Write($inputBytes,0,$inputBytes.Length);$process.StandardInput.BaseStream.Close()
        $output=$process.StandardOutput.ReadToEnd();$errorText=$process.StandardError.ReadToEnd();$process.WaitForExit()
        if($process.ExitCode-ne0){throw "Hook protocol failed: $($case.name) : $errorText"}
        $response=$output|ConvertFrom-Json
        $actual=if($response.PSObject.Properties['decision']){$response.decision}elseif($response.PSObject.Properties['hookSpecificOutput']){$response.hookSpecificOutput.permissionDecision}else{'allow'}
        if($actual-cne$case.expected){throw "Hook decision failed: $($case.name) : $actual; $output"}
        $results.Add([pscustomobject]@{name=$case.name;passed=$true})
        $process.Dispose()
    }
    if ($HookPath) {
        $configuration = Get-Content -LiteralPath $HookPath -Raw | ConvertFrom-Json
        foreach ($hookEvent in @('PreToolUse','Stop')) {
            if (@($configuration.hooks.$hookEvent).Count -ne 1 -or $configuration.hooks.$hookEvent[0].hooks[0].type -ne 'command') { throw 'Hook configuration failed.' }
        }
    }
    Write-Output "PASS: $($results.Count) Codex hook protocol cases."
} finally {
    $resolved=[IO.Path]::GetFullPath($temporary)
    $expectedBase=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\ub-codex-hook-'
    if(-not$resolved.StartsWith($expectedBase,[StringComparison]::OrdinalIgnoreCase)){throw 'Temporary path escaped its owner.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
