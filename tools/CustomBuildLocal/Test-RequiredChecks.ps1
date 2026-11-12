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
