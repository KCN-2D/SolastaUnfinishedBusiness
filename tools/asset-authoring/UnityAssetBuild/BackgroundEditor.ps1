param([string]$Configuration)

function Write-AssetWorkerJson([string]$Path, $Value) {
    $temporary=$Path+'.writing'
    [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary,$Path,$true)
}

function Get-AssetOwnedProcess($Identity) {
    if (!$Identity) { return $null }
    try { $process = Get-Process -Id $Identity.pid -ErrorAction Stop }
    catch {
        if ($_.FullyQualifiedErrorId.Split(',')[0] -eq 'NoProcessFoundForGivenId' -and
            $_.CategoryInfo.Category -eq [Management.Automation.ErrorCategory]::ObjectNotFound) { return $null }
        throw "The owned process identity cannot be queried: PID $($Identity.pid). $($_.Exception.Message)"
    }
    $path=$process.Path
    if ([string]::IsNullOrEmpty($path)) { throw "The executable path of PID $($Identity.pid) is unavailable; its ownership is unknown." }
    $started=$process.StartTime.ToUniversalTime()
    if ($path -eq $Identity.path -and
        [Math]::Abs(($started - ([DateTime]$Identity.startTimeUtc).ToUniversalTime()).TotalSeconds) -lt 1) { return $process }
    return $null
}

function Get-AssetProjectLock([string]$Project) {
    $path=Join-Path $Project 'Temp/UnityLockfile'
    $stream=$null
    try {
        # Open only an existing native lock. Never create, truncate or write it.
        $stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
        return [ordered]@{path=$path;state='unlocked';observedUtc=[DateTime]::UtcNow.ToString('O')}
    }
    catch [IO.FileNotFoundException] { return [ordered]@{path=$path;state='absent';observedUtc=[DateTime]::UtcNow.ToString('O')} }
    catch [IO.DirectoryNotFoundException] { return [ordered]@{path=$path;state='absent';observedUtc=[DateTime]::UtcNow.ToString('O')} }
    catch [IO.IOException] {
        if (($_.Exception.HResult -band 65535) -in @(32,33)) {
            throw 'The selected project is already open (native UnityLockfile is locked). No CLI request was sent.'
        }
        throw "The native Unity project lock state is unknown. No CLI request was sent. $($_.Exception.Message)"
    }
    catch { throw "The native Unity project lock cannot be read. No CLI request was sent. $($_.Exception.Message)" }
    finally { if ($stream) { $stream.Dispose() } }
}

function Find-AssetEditorChild([int]$CliPid, [string]$Editor, [DateTime]$LaunchedUtc) {
    $children=@(Get-CimInstance Win32_Process -Filter "ParentProcessId = $CliPid AND Name = 'Unity.exe'" -Property ProcessId,ParentProcessId,ExecutablePath,CreationDate)
    $child=$children | Where-Object { $_.ExecutablePath -eq $Editor -and $_.CreationDate.ToUniversalTime() -ge $LaunchedUtc } | Select-Object -First 1
    if ($child) {
        try { $process=Get-Process -Id $child.ProcessId -ErrorAction Stop }
        catch { if ($_.FullyQualifiedErrorId.Split(',')[0] -eq 'NoProcessFoundForGivenId') { return $null }; throw }
        return [ordered]@{pid=$process.Id;path=$process.Path;startTimeUtc=$process.StartTime.ToUniversalTime().ToString('O');parentPid=$CliPid}
    }
    return $null
}

function Find-AssetCliChildren([int]$CliPid, [string]$Cli, [DateTime]$LaunchedUtc) {
    foreach ($child in @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $CliPid AND Name = 'unity.exe'" -Property ProcessId,ExecutablePath,CreationDate)) {
        if ($child.ExecutablePath -ne $Cli -or $child.CreationDate.ToUniversalTime() -lt $LaunchedUtc) { continue }
        try { $process=Get-Process -Id $child.ProcessId -ErrorAction Stop }
        catch { if ($_.FullyQualifiedErrorId.Split(',')[0] -eq 'NoProcessFoundForGivenId') { continue }; throw }
        [ordered]@{pid=$process.Id;path=$process.Path;startTimeUtc=$process.StartTime.ToUniversalTime().ToString('O');parentPid=$CliPid}
    }
}

function Invoke-AssetBackgroundHost([string]$ConfigPath) {
    $ErrorActionPreference = 'Stop'
    $cfg = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    . (Join-Path $cfg.repoRoot 'tools/runtime-verification/WindowsDesktop.ps1')
    $desktop = Get-VerificationDesktop
    $statePath = Join-Path $cfg.run 'session.json'
    $state = [ordered]@{status='starting';ownerId=$cfg.ownerId;startedUtc=[DateTime]::UtcNow.ToString('O');desktop=$cfg.desktop;
        wrapperPid=$PID;wrapperDesktop=$desktop;worker=$null;editor=$null;editorReleased=$false;cliReleased=$false;cliChildren=@();cliChildrenReleased=$false;forcedExit=$false}
    $cli = $null
    try {
        # A console host can own no windows. Its desktop/input queries still
        # establish isolation independently of the empty window enumeration.
        if (!$desktop.currentDesktopQuerySucceeded -or !$desktop.inputDesktopQuerySucceeded -or
            !$desktop.runnerInputQuery.succeeded -or $desktop.runnerInputQuery.bytesNeeded -ne 4 -or
            $desktop.runnerInputQuery.rawValue -ne 0 -or $desktop.runnerDesktop -ne $cfg.desktop -or
            $desktop.runnerDesktop -eq $desktop.inputDesktop) { throw 'The background host is not on the owned non-input desktop.' }
        $extraArgs = '-noUpm -ubAuthoringOwner ' + $cfg.ownerId + ' -logFile "' + (Join-Path $cfg.run 'Editor.log') + '"'
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName=$cfg.cli; $start.WorkingDirectory=$cfg.project; $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        foreach ($arg in @('open',$cfg.project,'--editor-version','2019.4.37f1','--editor-path',$cfg.editor,'--json','--args',$extraArgs)) {
            $start.ArgumentList.Add($arg)
        }
        [void]$start.Environment.Remove('ELECTRON_RUN_AS_NODE')
        $launchedUtc=[DateTime]::UtcNow
        $cli=[Diagnostics.Process]::Start($start)
        $output=$cli.StandardOutput.ReadToEndAsync(); $errors=$cli.StandardError.ReadToEndAsync()
        $state.cli=[ordered]@{pid=$cli.Id;path=$cfg.cli;startTimeUtc=$cli.StartTime.ToUniversalTime().ToString('O');waitRequested=$false;batchMode=$false}
        Write-AssetWorkerJson $statePath $state
        if (!$cli.WaitForExit(20000)) { throw 'The Unity CLI did not return a launch receipt within 20 seconds.' }
        [IO.File]::WriteAllText((Join-Path $cfg.run 'CLI.json'),$output.Result,[Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $cfg.run 'CLI.stderr.txt'),$errors.Result,[Text.UTF8Encoding]::new($false))
        $state.cli.exitCode=$cli.ExitCode
        $state.cliChildren=@(Find-AssetCliChildren $cli.Id $cfg.cli $launchedUtc)
        if ($cli.ExitCode -ne 0) { throw 'The Unity CLI rejected the owned project launch. Inspect CLI.json.' }
        $launchReceipt=$output.Result | ConvertFrom-Json
        if (!$launchReceipt.success -or $launchReceipt.data.editorPath -ne $cfg.editor -or $launchReceipt.data.projectPath -ne $cfg.project) {
            throw 'The Unity CLI launch receipt identifies a different project or Editor.'
        }
        $deadline=[DateTime]::UtcNow.AddSeconds($cfg.timeoutSeconds)
        $firstUpdate=$null
        while ([DateTime]::UtcNow -lt $deadline) {
            if (!(Get-AssetOwnedProcess $cfg.parent)) { throw 'The authoring caller ended before its Editor became ready.' }
            if (Test-Path -LiteralPath (Join-Path $cfg.run 'release.request')) { throw 'The authoring caller cancelled before its Editor became ready.' }
            # Query only identity fields, never a process command line: Hub may
            # pass existing credentials internally to the Editor.
            if (!$state.editor) {
                $state.editor=Find-AssetEditorChild $cli.Id $cfg.editor $launchedUtc
            }
            $readyPath=Join-Path $cfg.project '.asset-authoring-jobs/worker.json'
            if ($state.editor -and (Test-Path -LiteralPath $readyPath)) {
                try { $ready=Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json }
                catch { Start-Sleep -Milliseconds 250; continue }
                if ($ready.processId -eq $state.editor.pid -and $ready.ownerId -eq $cfg.ownerId -and
                    $ready.sourceSha256 -eq $cfg.sourceSha256 -and $ready.projectPath -eq $cfg.project -and
                    $ready.unityVersion -eq '2019.4.37f1' -and $ready.mainThread -gt 0 -and $ready.nativeMainThread -gt 0 -and $ready.desktopQuerySucceeded -and
                    !$ready.desktopReceivesInput -and $ready.desktop -eq $cfg.desktop -and $ready.desktop -ne $ready.inputDesktop -and
                    ([DateTime]$ready.updatedUtc).ToUniversalTime() -ge $launchedUtc) {
                    if ($firstUpdate -and $firstUpdate -ne $ready.updatedUtc) { $state.worker=$ready; break }
                    $firstUpdate=$ready.updatedUtc
                }
            }
            if ($state.editor -and !(Get-AssetOwnedProcess $state.editor)) { throw 'The owned Editor ended before its worker became ready.' }
            Write-AssetWorkerJson $statePath $state
            Start-Sleep -Milliseconds 250
        }
        if (!$state.worker) { throw 'The owned Editor did not establish an isolated main-thread worker before timeout.' }
        $state.status='ready'; Write-AssetWorkerJson $statePath $state
        while (!(Test-Path -LiteralPath (Join-Path $cfg.run 'release.request'))) {
            if (!(Get-AssetOwnedProcess $cfg.parent)) { throw 'The authoring caller ended before releasing its owned Editor.' }
            if (!(Get-AssetOwnedProcess $state.editor)) { throw 'The owned Editor ended before release.' }
            Start-Sleep -Milliseconds 250
        }
        $state.status='complete'
    }
    catch { $state.status='failed'; $state.error=$_.Exception.Message }
    finally {
        # A timed-out launcher can spawn the Editor later. Stop the exact CLI
        # we created before looking for its final set of child processes.
        if ($cli -and !$cli.HasExited) {
            $ownedCli=Get-AssetOwnedProcess $state.cli
            if (!$ownedCli) { throw 'The running CLI ownership could not be verified for cleanup.' }
            $ownedCli.Kill()
            if (!$ownedCli.WaitForExit(10000)) { throw 'The owned CLI did not exit during cleanup.' }
            $state.cliTerminatedAfterFailure=$true
        }
        $state.cliReleased=(!$cli -or $cli.HasExited)
        if (!$state.editor -and $cli) { $state.editor=Find-AssetEditorChild $cli.Id $cfg.editor $launchedUtc }
        $owned=Get-AssetOwnedProcess $state.editor
        if ($owned) {
            if ($state.worker) {
                $request=[ordered]@{ownerId=$cfg.ownerId;processId=$state.worker.processId;
                    processStartedUtc=([DateTimeOffset]$state.worker.processStartedUtc).ToUniversalTime().ToString('o')}
                Write-AssetWorkerJson (Join-Path $cfg.project '.asset-authoring-jobs/shutdown-request.json') $request
            }
            if (!$owned.WaitForExit(15000)) { $owned.Kill(); [void]$owned.WaitForExit(10000); $state.forcedExit=$true }
        }
        $state.editorReleased=!(Get-AssetOwnedProcess $state.editor)
        # Unity CLI can leave a per-launch companion after its Editor has exited.
        # Release only same-executable children captured from our exact launcher.
        if ($cli) {
            $state.cliChildren=@(@($state.cliChildren)+@(Find-AssetCliChildren $cli.Id $cfg.cli $launchedUtc) | Sort-Object pid -Unique)
            foreach ($identity in $state.cliChildren) {
                $companion=Get-AssetOwnedProcess $identity
                if ($companion -and !$companion.WaitForExit(3000)) { $companion.Kill(); if (!$companion.WaitForExit(10000)) { throw 'The owned CLI companion did not exit during cleanup.' } }
            }
        }
        $state.cliChildrenReleased=@($state.cliChildren | Where-Object { Get-AssetOwnedProcess $_ }).Count -eq 0
        $state.finishedUtc=[DateTime]::UtcNow.ToString('O')
        Write-AssetWorkerJson $statePath $state
        if ($cli) { $cli.Dispose() }
    }
}

function Start-AssetBackgroundEditor([string]$Project, [string]$Editor, [string]$Cli, [string]$SourceSha256, [int]$TimeoutSeconds) {
    if ($PSVersionTable.PSEdition -ne 'Core' -or $PSVersionTable.PSVersion.Major -lt 7) { throw 'Background Editor startup requires PowerShell 7 (pwsh). Existing GUI and batch modes are unchanged.' }
    if (!(Test-Path -LiteralPath $Cli -PathType Leaf)) { throw 'Unity CLI is missing. Supply -UnityCliPath; this tool does not install it.' }
    $lockObservation=Get-AssetProjectLock $Project
    $existing=Join-Path $Project '.asset-authoring-jobs/worker.json'
    if (Test-Path -LiteralPath $existing) {
        $ready=Get-Content -LiteralPath $existing -Raw | ConvertFrom-Json
        $identity=[pscustomobject]@{pid=$ready.processId;path=$Editor;startTimeUtc=$ready.processStartedUtc}
        if (Get-AssetOwnedProcess $identity) { throw 'The selected project already has a running Editor. Reuse its worker without -StartBackgroundEditor.' }
    }
    $repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
    . (Join-Path $repo 'tools/runtime-verification/WindowsDesktop.ps1')
    $owner=[guid]::NewGuid().ToString('N')
    $run=Join-Path $Project ('.asset-authoring-jobs/BackgroundSessions/'+$owner)
    [IO.Directory]::CreateDirectory($run) | Out-Null
    $parent=Get-Process -Id $PID
    $cfg=[ordered]@{repoRoot=$repo;run=$run;ownerId=$owner;desktop=('UBGameTest_'+[guid]::NewGuid().ToString('N'));
        project=$Project;editor=$Editor;cli=$Cli;sourceSha256=$SourceSha256;timeoutSeconds=$TimeoutSeconds;projectLockBefore=$lockObservation;
        parent=[ordered]@{pid=$PID;path=$parent.Path;startTimeUtc=$parent.StartTime.ToUniversalTime().ToString('O')}}
    $config=Join-Path $run 'configuration.json'; Write-AssetWorkerJson $config $cfg
    $session=[pscustomobject]@{RunDirectory=$run;OwnerId=$owner;Lease=$null;Ready=$null;ForegroundBefore=(Get-VerificationForeground);ForegroundAfter=$null}
    $returnedReady=$false
    try {
        $hostScript=(Get-Command Invoke-AssetBackgroundHost).ScriptBlock.File
        $session.Lease=Start-VerificationBackgroundProcess -FilePath $parent.Path -WorkingDirectory $repo -DesktopName $cfg.desktop -ArgumentList @('-NoProfile','-File',$hostScript,'-Configuration',$config)
        $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds+25)
        while ([DateTime]::UtcNow -lt $deadline -and !$session.Lease.Process.HasExited) {
            $path=Join-Path $run 'session.json'
            if (Test-Path -LiteralPath $path) {
                $state=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                if ($state.status -eq 'ready') { $session.Ready=$state; $returnedReady=$true; return $session }
                if ($state.status -eq 'failed') { throw "Background Editor startup failed. Inspect $path" }
            }
            Start-Sleep -Milliseconds 250
        }
        throw "Background Editor startup did not complete. Inspect $run"
    }
    finally { if (!$returnedReady) { Stop-AssetBackgroundEditor $session } }
}

function Stop-AssetBackgroundEditor($Session) {
    if (!$Session -or !$Session.Lease) { return }
    [IO.File]::WriteAllText((Join-Path $Session.RunDirectory 'release.request'),'release owned authoring worker')
    if (!$Session.Lease.Process.WaitForExit(30000)) {
        $path=Join-Path $Session.RunDirectory 'session.json'
        if (Test-Path -LiteralPath $path) {
            $state=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            $ownedCli=Get-AssetOwnedProcess $state.cli
            if ($ownedCli) { $ownedCli.Kill(); [void]$ownedCli.WaitForExit(10000) }
            if (!$state.editor -and $state.cli) { $state.editor=Find-AssetEditorChild $state.cli.pid (Get-Content -LiteralPath (Join-Path $Session.RunDirectory 'configuration.json') -Raw | ConvertFrom-Json).editor ([DateTime]$state.cli.startTimeUtc) }
            $owned=Get-AssetOwnedProcess $state.editor
            if ($owned) { $owned.Kill(); [void]$owned.WaitForExit(10000) }
            foreach ($identity in $state.cliChildren) { $companion=Get-AssetOwnedProcess $identity; if ($companion) { $companion.Kill();[void]$companion.WaitForExit(10000) } }
        }
        $Session.Lease.Process.Kill(); [void]$Session.Lease.Process.WaitForExit(10000)
        $Session.Lease.Dispose()
        throw "The background host required forced cleanup. Inspect $($Session.RunDirectory)"
    }
    $Session.ForegroundAfter=[UBVerification.DesktopReader]::ReadForeground()
    Write-AssetWorkerJson (Join-Path $Session.RunDirectory 'foreground.json') ([ordered]@{before=$Session.ForegroundBefore;after=$Session.ForegroundAfter})
    $Session.Lease.Dispose()
    $state=Get-Content -LiteralPath (Join-Path $Session.RunDirectory 'session.json') -Raw | ConvertFrom-Json
    if (!$state.editorReleased -or !$state.cliReleased -or !$state.cliChildrenReleased -or $state.forcedExit -or $state.status -ne 'complete') { throw "Background Editor did not finish a normal owned shutdown. Inspect $($Session.RunDirectory)" }
}

if ($Configuration) { Invoke-AssetBackgroundHost $Configuration }
