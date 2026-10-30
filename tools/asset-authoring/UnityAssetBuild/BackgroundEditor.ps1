param([string]$Configuration)

function ConvertTo-AssetWorkerJson($Value, [switch]$Compress) {
    # Windows PowerShell 5 can silently stringify values beyond ConvertTo-Json's depth.
    $pending=[Collections.Generic.Stack[object]]::new()
    $pending.Push([pscustomobject]@{value=$Value;depth=0})
    while($pending.Count){
        $entry=$pending.Pop();$item=$entry.value
        if($null -eq $item){continue}
        $scalar=[Convert]::GetTypeCode($item) -ne [TypeCode]::Object -or
            $item -is [DateTimeOffset] -or $item -is [guid] -or $item -is [uri] -or
            $item -is [System.Numerics.BigInteger] -or $item -is [Management.Automation.SwitchParameter]
        $properties=$false;$dictionary=$false
        $extensions=$item.PSObject.Properties.Match('*',[Management.Automation.PSMemberTypes]::NoteProperty -bor
            [Management.Automation.PSMemberTypes]::AliasProperty -bor [Management.Automation.PSMemberTypes]::ScriptProperty -bor
            [Management.Automation.PSMemberTypes]::CodeProperty)
        if($scalar){$children=@();if(-not $extensions.Count){continue}}
        elseif($item -is [Collections.IDictionary]){$children=$item.GetEnumerator();$dictionary=$true}
        elseif($item -is [Management.Automation.PSCustomObject]){$children=$item.PSObject.Properties;$properties=$true}
        elseif($item -is [Collections.IEnumerable] -and $item -isnot [string]){$children=$item}
        else{$children=$item.PSObject.Properties;$properties=$true}
        if($entry.depth -gt 100){throw [FormatException]::new('Authoring JSON exceeds the supported depth of 100.')}
        foreach($childEntry in $children){
            $child=$childEntry
            if($properties -or $dictionary){$child=$childEntry.Value}
            if($null -ne $child){
                $pending.Push([pscustomobject]@{value=$child;depth=$entry.depth+1})
            }
        }
        if($scalar -or $dictionary -or ($item -is [Collections.IEnumerable] -and -not $properties)){
            foreach($property in $extensions){
                $child=$property.Value
                if($null -ne $child){$pending.Push([pscustomobject]@{value=$child;depth=$entry.depth+1})}
            }
        }
    }
    $Value|ConvertTo-Json -Depth 100 -Compress:$Compress -WarningAction Stop
}

function Write-AssetWorkerJson([string]$Path, $Value) {
    $temporary=$Path+'.writing-'+[guid]::NewGuid().ToString('N')
    $previous=$Path+'.previous-'+[guid]::NewGuid().ToString('N')
    try{
        [IO.File]::WriteAllText($temporary, (ConvertTo-AssetWorkerJson $Value), [Text.UTF8Encoding]::new($false))
        for($attempt=0;;$attempt++){
            try{
                if([IO.File]::Exists($Path)){[IO.File]::Replace($temporary,$Path,$previous)}else{[IO.File]::Move($temporary,$Path)}
                break
            }catch{
                $cause=$_.Exception.GetBaseException()
                $code=$cause.HResult -band 0xffff
                if($cause -isnot [IO.IOException] -or $code -notin @(32,33) -or $attempt -ge 40){
                    throw [IO.IOException]::new("Unable to publish authoring JSON '$Path'.",$cause)
                }
                Start-Sleep -Milliseconds 25
            }
        }
    }finally{foreach($owned in @($temporary,$previous)){if([IO.File]::Exists($owned)){[IO.File]::Delete($owned)}}}
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
    $LaunchedUtc=$LaunchedUtc.ToUniversalTime()
    $children=@(Get-CimInstance Win32_Process -Filter "ParentProcessId = $CliPid AND Name = 'Unity.exe'" -Property ProcessId,ParentProcessId,ExecutablePath,CreationDate)
    $child=$children | Where-Object { $_.ExecutablePath -eq $Editor -and $_.CreationDate.ToUniversalTime() -ge $LaunchedUtc } | Select-Object -First 1
    if ($child) {
        try { $process=Get-Process -Id $child.ProcessId -ErrorAction Stop }
        catch { if ($_.FullyQualifiedErrorId.Split(',')[0] -eq 'NoProcessFoundForGivenId') { return $null }; throw }
        $started=$process.StartTime.ToUniversalTime()
        if($process.Path -ne $Editor -or $started -lt $LaunchedUtc -or
            [Math]::Abs(($started-$child.CreationDate.ToUniversalTime()).TotalSeconds) -ge 1){return $null}
        return [ordered]@{pid=$process.Id;path=$process.Path;startTimeUtc=$started.ToString('O');parentPid=$CliPid}
    }
    return $null
}

function Find-AssetCliChildren([int]$CliPid, [string]$Cli, [DateTime]$LaunchedUtc, [array]$KnownIdentities=@()) {
    $LaunchedUtc=$LaunchedUtc.ToUniversalTime()
    $identities=[Collections.Generic.List[object]]::new()
    foreach($identity in $KnownIdentities){if($identity){$identities.Add($identity)}}
    foreach ($child in @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $CliPid AND Name = 'unity.exe'" -Property ProcessId,ExecutablePath,CreationDate)) {
        if ($child.ExecutablePath -ne $Cli -or $child.CreationDate.ToUniversalTime() -lt $LaunchedUtc) { continue }
        try { $process=Get-Process -Id $child.ProcessId -ErrorAction Stop }
        catch { if ($_.FullyQualifiedErrorId.Split(',')[0] -eq 'NoProcessFoundForGivenId') { continue }; throw }
        $started=$process.StartTime.ToUniversalTime()
        if($process.Path -ne $Cli -or $started -lt $LaunchedUtc -or
            [Math]::Abs(($started-$child.CreationDate.ToUniversalTime()).TotalSeconds) -ge 1){continue}
        $identities.Add([ordered]@{pid=$process.Id;path=$process.Path;startTimeUtc=$started.ToString('O');parentPid=$CliPid})
    }
    # OrderedDictionary keys are not PSObject properties. Use their values and
    # the complete identity so a later companion or reused PID is not lost.
    $identities | Sort-Object { [int]$_.pid },{ [string]$_.path },{ ([DateTime]$_.startTimeUtc).ToUniversalTime().Ticks } -Unique
}

function Invoke-AssetBackgroundHost([string]$ConfigPath) {
    $ErrorActionPreference = 'Stop'
    $cfg = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    . (Join-Path $cfg.repoRoot 'tools/runtime-verification/WindowsDesktop.ps1')
    $desktop = Get-VerificationDesktop
    $statePath = Join-Path $cfg.run 'session.json'
    $state = [ordered]@{status='starting';ownerId=$cfg.ownerId;startedUtc=[DateTime]::UtcNow.ToString('O');desktop=$cfg.desktop;
        wrapperPid=$PID;wrapperDesktop=$desktop;worker=$null;editor=$null;editorReleased=$false;cliReleased=$false;cliChildren=@();cliChildrenReleased=$false;forcedExit=$false;
        shutdownRequestedUtc=$null;shutdownExitObservedUtc=$null;shutdownForcedUtc=$null;editorExitCode=$null}
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
                $state.shutdownRequestedUtc=[DateTime]::UtcNow.ToString('O')
                Write-AssetWorkerJson $statePath $state
            }
            if (!$owned.WaitForExit(30000)) {
                $state.shutdownForcedUtc=[DateTime]::UtcNow.ToString('O');$state.forcedExit=$true
                $owned.Kill();[void]$owned.WaitForExit(10000)
            }
            if($owned.HasExited){$state.shutdownExitObservedUtc=[DateTime]::UtcNow.ToString('O');$state.editorExitCode=$owned.ExitCode}
        }
        $state.editorReleased=!(Get-AssetOwnedProcess $state.editor)
        # Unity CLI can leave a per-launch companion after its Editor has exited.
        # Release only same-executable children captured from our exact launcher.
        if ($cli) {
            $state.cliChildren=@(Find-AssetCliChildren $cli.Id $cfg.cli $launchedUtc $state.cliChildren)
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
    $storageOperation=Enter-AssetStorageOperation $Project;$session=$null
    try {
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
    $session=[pscustomobject]@{RunDirectory=$run;OwnerId=$owner;Lease=$null;Ready=$null;ForegroundBefore=(Get-VerificationForeground);ForegroundAfter=$null;StorageOperation=$storageOperation}
    $returnedReady=$false;$startupError=$null
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
    catch { $startupError=$_; throw }
    finally {
        if (!$returnedReady) {
            try { Stop-AssetBackgroundEditor $session }
            catch {
                if (!$startupError) { throw }
                $failure=[AggregateException]::new(
                    "Background Editor startup failed: $($startupError.Exception.Message) Cleanup also failed: $($_.Exception.Message)",
                    [Exception[]]@($startupError.Exception,$_.Exception))
                $failure.Data['StartupErrorRecord']=$startupError
                $failure.Data['CleanupErrorRecord']=$_
                throw $failure
            }
        }
    }
    }catch{if(-not $session){$storageOperation.ReleaseMutex();$storageOperation.Dispose()};throw}
}

function Stop-AssetBackgroundEditor($Session) {
    try {
    if (!$Session -or !$Session.Lease) { return }
    [IO.File]::WriteAllText((Join-Path $Session.RunDirectory 'release.request'),'release owned authoring worker')
    if (!$Session.Lease.Process.WaitForExit(60000)) {
        $path=Join-Path $Session.RunDirectory 'session.json'
        if (Test-Path -LiteralPath $path) {
            $state=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            $cfg=Get-Content -LiteralPath (Join-Path $Session.RunDirectory 'configuration.json') -Raw | ConvertFrom-Json
            $ownedCli=Get-AssetOwnedProcess $state.cli
            if ($ownedCli) { $ownedCli.Kill(); [void]$ownedCli.WaitForExit(10000) }
            if (!$state.editor -and $state.cli) { $state.editor=Find-AssetEditorChild $state.cli.pid $cfg.editor ([DateTime]$state.cli.startTimeUtc) }
            $owned=Get-AssetOwnedProcess $state.editor
            if ($owned) {
                $state.shutdownForcedUtc=[DateTime]::UtcNow.ToString('O');$state.forcedExit=$true
                $owned.Kill();[void]$owned.WaitForExit(10000)
                if($owned.HasExited){$state.shutdownExitObservedUtc=[DateTime]::UtcNow.ToString('O');$state.editorExitCode=$owned.ExitCode}
            }
            if($state.cli){$state.cliChildren=@(Find-AssetCliChildren $state.cli.pid $state.cli.path ([DateTime]$state.cli.startTimeUtc) $state.cliChildren)}
            foreach ($identity in $state.cliChildren) { $companion=Get-AssetOwnedProcess $identity; if ($companion) { $companion.Kill();[void]$companion.WaitForExit(10000) } }
            $state.editorReleased=!(Get-AssetOwnedProcess $state.editor)
            $state.cliReleased=!(Get-AssetOwnedProcess $state.cli)
            $state.cliChildrenReleased=@($state.cliChildren | Where-Object { Get-AssetOwnedProcess $_ }).Count -eq 0
            $state.forcedExit=$true;$state.status='failed';$state.finishedUtc=[DateTime]::UtcNow.ToString('O')
            Write-AssetWorkerJson $path $state
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
    }finally{if($Session -and $Session.PSObject.Properties['StorageOperation'] -and $Session.StorageOperation){$Session.StorageOperation.ReleaseMutex();$Session.StorageOperation.Dispose();$Session.StorageOperation=$null}}
}

function Get-AssetStoragePath([string]$Path,[switch]$ReadOnlyReference) {
    $full=[IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    if ($full.StartsWith('\\') -or $full -notmatch '^[A-Za-z]:\\' -or $full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')) { throw 'A local non-root storage path is required.' }
    for ($cursor=$full;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)) {
        if ([IO.File]::Exists($cursor) -or [IO.Directory]::Exists($cursor)) {
            if ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) { throw "Storage paths cannot contain reparse points: $cursor" }
        }
        if (-not $ReadOnlyReference -and [IO.File]::Exists((Join-Path $cursor 'Solasta.exe'))) { throw 'A game installation is not authoring storage.' }
    }
    return $full
}
function Enter-AssetStorageOperation([string]$Scope) {
    $path=Get-AssetStoragePath $Scope;$sha=[Security.Cryptography.SHA256]::Create()
    try{$key=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($path.ToLowerInvariant())))).Replace('-','')}finally{$sha.Dispose()}
    $mutex=[Threading.Mutex]::new($false,('Local\SolastaUBAuthoring_'+$key))
    try{$acquired=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$acquired=$true}
    if(-not $acquired){$mutex.Dispose();throw 'Another authoring operation owns this storage scope.'}
    return $mutex
}
function Test-AssetStorageWithin([string]$Path,[string]$Parent) {
    return $Path.Equals($Parent,[StringComparison]::OrdinalIgnoreCase) -or $Path.StartsWith($Parent.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)
}
function Read-AssetStorageJson([string]$Path,[switch]$ReadOnlyReference) {
    $resolved=Get-AssetStoragePath $Path -ReadOnlyReference:$ReadOnlyReference;$bytes=[IO.File]::ReadAllBytes($resolved)
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$hash=([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','')}finally{$sha.Dispose()}
    return [pscustomobject]@{value=([Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF)|ConvertFrom-Json);sha256=$hash}
}
function Get-AssetStorageProject([string]$Project) {
    $projectPath=Get-AssetStoragePath $Project
    $marker=Join-Path $projectPath '.solasta-asset-authoring-project'
    if (-not [IO.File]::Exists($marker) -or [IO.File]::ReadAllText($marker).Trim() -cne 'SolastaUBAssetAuthoring:UnityProject:1') { throw 'The authoring project is unmarked.' }
    $workspace=$null
    for ($cursor=[IO.Path]::GetDirectoryName($projectPath);$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)) {
        $marker=Join-Path $cursor '.solasta-asset-authoring-workspace'
        if ([IO.File]::Exists($marker)) {
            if ([IO.File]::ReadAllText($marker).Trim() -cne 'SolastaUBAssetAuthoring:UnityWorkspace:1') { throw 'The authoring workspace marker is invalid.' }
            $workspace=$cursor;break
        }
    }
    if (-not $workspace) { throw 'The project has no marked authoring workspace.' }
    return [pscustomobject]@{project=$projectPath;workspace=$workspace;jobs=(Join-Path $projectPath '.asset-authoring-jobs');assets=(Join-Path $projectPath 'Assets')}
}
function Get-AssetStorageReferences([string[]]$ReferencePath) {
    $paths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $inputs=@(foreach ($path in $ReferencePath) {
        $resolved=Get-AssetStoragePath $path -ReadOnlyReference
        $snapshot=Read-AssetStorageJson $resolved -ReadOnlyReference;$data=$snapshot.value
        if ($data.schemaVersion -ne 1 -or $data.paths -isnot [array] -or @($data.PSObject.Properties.Name|Where-Object{$_ -notin @('schemaVersion','paths')}).Count) { throw 'Invalid storage reference manifest.' }
        [void]$paths.Add($resolved)
        foreach ($entry in $data.paths) {
            if ($entry -isnot [string] -or [string]::IsNullOrWhiteSpace($entry)) { throw 'Storage references must be paths.' }
            $item=if ([IO.Path]::IsPathRooted($entry)) { Get-AssetStoragePath $entry -ReadOnlyReference } else { Get-AssetStoragePath (Join-Path (Split-Path -Parent $resolved) $entry) -ReadOnlyReference }
            [void]$paths.Add($item)
        }
        [ordered]@{path=$resolved;sha256=$snapshot.sha256}
    })
    return [pscustomobject]@{paths=$paths;inputs=$inputs}
}
function Assert-AssetStorageReferences($References) {
    foreach ($entry in $References.inputs) {
        if ((Get-FileHash -LiteralPath (Get-AssetStoragePath $entry.path -ReadOnlyReference) -Algorithm SHA256).Hash -cne $entry.sha256) { throw 'A storage reference manifest changed during cleanup.' }
    }
}
function Write-AssetStorageEvent([string]$Path,$Value) {
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-AssetWorkerJson $Value -Compress)+[Environment]::NewLine)
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Append,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try {$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
}
function Get-AssetStorageFiles([string[]]$Roots,[string]$Exclude) {
    $files=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($root in $Roots) {
        $path=Get-AssetStoragePath $root
        $items=if ([IO.File]::Exists($path)) {@(Get-Item -LiteralPath $path -Force)} elseif ([IO.Directory]::Exists($path)) {@(Get-ChildItem -LiteralPath $path -Recurse -Force)} else {@()}
        foreach ($item in $items) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Authoring output contains a reparse point.' }
            if ($item.PSIsContainer -or $item.FullName -eq $Exclude) { continue }
            $files[$item.FullName]=[pscustomobject][ordered]@{path=$item.FullName;bytes=[long]$item.Length;sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash}
        }
    }
    return @($files.Values|Sort-Object path)
}
function Test-AssetStorageFiles([string[]]$Roots,[string]$Exclude,[array]$Recorded) {
    $current=@(Get-AssetStorageFiles $Roots $Exclude)
    $expected=@($Recorded|Where-Object{$expectedFile=$_;@($Roots|Where-Object{([IO.Directory]::Exists($_)-or [IO.File]::Exists($_))-and(Test-AssetStorageWithin $expectedFile.path $_)}).Count}|Sort-Object path)
    return (ConvertTo-AssetWorkerJson $current -Compress)-ceq(ConvertTo-AssetWorkerJson $expected -Compress)
}
function Assert-AssetStorageOwner($Data,[string]$Path) {
    $context=Get-AssetStorageProject $Data.project
    if($Data.schemaVersion -ne 1 -or $Data.ownerId -cnotmatch '^[a-f0-9]{32}$' -or $Data.disposableProject -isnot [bool] -or
        $Data.state -cnotin @('running','completed') -or $Data.dependencies -isnot [array] -or $Data.files -isnot [array]){throw 'Unknown job ownership.'}
    $job=Get-AssetStoragePath $Data.jobDirectory;$assets=Get-AssetStoragePath $Data.assetDirectory;$result=Get-AssetStoragePath $Data.resultPath
    if($Data.disposableProject){
        if($context.project -ne (Join-Path $job 'Project') -or $job -eq $context.workspace -or -not(Test-AssetStorageWithin $job $context.workspace) -or
            $Path -ne (Join-Path $context.jobs ('StorageOwners/'+$Data.ownerId+'/storage-owner.json'))){throw 'Invalid disposable project ownership.'}
    }elseif($job -eq $context.jobs -or -not(Test-AssetStorageWithin $job $context.jobs) -or $Path -ne (Join-Path $job 'storage-owner.json')){throw 'Invalid job directory ownership.'}
    if(-not(Test-AssetStorageWithin $assets $context.assets)-or ($assets -eq $context.assets -and -not $Data.disposableProject)-or
        (Test-AssetStorageWithin $assets (Join-Path $context.assets 'Editor')) -or -not(Test-AssetStorageWithin $result $job)){throw 'Invalid generated asset ownership.'}
    foreach($dependency in $Data.dependencies){if($dependency -isnot [string]){throw 'Invalid job dependency.'};[void](Get-AssetStoragePath $dependency -ReadOnlyReference)}
    return $context
}
function Register-AssetAuthoringJob {
    param([Parameter(Mandatory)][string]$Project,[Parameter(Mandatory)][string]$JobDirectory,
        [Parameter(Mandatory)][string]$AssetDirectory,[Parameter(Mandatory)][string]$ResultPath,
        [string[]]$DependencyPath=@(),$Session,[switch]$DisposableProject)
    $storageOperation=Enter-AssetStorageOperation $Project
    try{

    $context=Get-AssetStorageProject $Project
    $job=Get-AssetStoragePath $JobDirectory;$assets=Get-AssetStoragePath $AssetDirectory;$result=Get-AssetStoragePath $ResultPath
    if ($DisposableProject) {
        if ($context.project -ne (Join-Path $job 'Project') -or $job -eq $context.workspace -or -not (Test-AssetStorageWithin $job $context.workspace)) { throw 'Disposable project ownership does not match its generated job directory.' }
    } elseif ($job -eq $context.jobs -or -not (Test-AssetStorageWithin $job $context.jobs)) { throw 'The job is outside its authoring project.' }
    if (-not (Test-AssetStorageWithin $assets $context.assets) -or ($assets -eq $context.assets -and -not $DisposableProject) -or
        (Test-AssetStorageWithin $assets (Join-Path $context.assets 'Editor')) -or -not (Test-AssetStorageWithin $result $job)) { throw 'The declared output directories do not belong to this job.' }
    $owner=[guid]::NewGuid().ToString('N')
    $directory=if ($DisposableProject) {Join-Path $context.jobs ('StorageOwners/'+$owner)}else{$job}
    [void][IO.Directory]::CreateDirectory($directory)
    $path=Join-Path $directory 'storage-owner.json'
    if ([IO.File]::Exists($path)) { throw 'This authoring job is already registered.' }
    $process=Get-Process -Id $PID
    $dependencies=@($DependencyPath|ForEach-Object{Get-AssetStoragePath $_ -ReadOnlyReference}|Sort-Object -Unique)
    $data=[ordered]@{schemaVersion=1;ownerId=$owner;project=$context.project;jobDirectory=$job;assetDirectory=$assets;resultPath=$result;
        disposableProject=[bool]$DisposableProject;dependencies=$dependencies;registeredUtc=[DateTime]::UtcNow.ToString('O');completedUtc=$null;state='running';outcome=$null;
        creator=@{pid=$PID;path=$process.Path;startTimeUtc=$process.StartTime.ToUniversalTime().ToString('O')};
        sessionDirectory=$(if($Session){$Session.RunDirectory}else{$null});resultSha256=$null;failure=$null;files=@()}
    [void](Assert-AssetStorageOwner ([pscustomobject]$data) $path)
    if([IO.Directory]::Exists($context.jobs)){
        foreach($other in Get-ChildItem -LiteralPath $context.jobs -Filter storage-owner.json -File -Recurse){
            $claim=[IO.File]::ReadAllText($other.FullName)|ConvertFrom-Json
            [void](Assert-AssetStorageOwner $claim $other.FullName)
            if((Test-AssetStorageWithin $assets $claim.assetDirectory)-or(Test-AssetStorageWithin $claim.assetDirectory $assets)-or
                (Test-AssetStorageWithin $job $claim.jobDirectory)-or(Test-AssetStorageWithin $claim.jobDirectory $job)){throw 'Authoring output is already owned by another job.'}
        }
    }
    Write-AssetWorkerJson $path $data
    return [pscustomobject]@{path=$path;ownerId=$owner;project=$context.project}

    }finally{$storageOperation.ReleaseMutex();$storageOperation.Dispose()}
}
function Complete-AssetAuthoringJob {
    param([Parameter(Mandatory)]$Ownership,[bool]$Succeeded=$false,[string]$Failure)
    $storageOperation=Enter-AssetStorageOperation $Ownership.project
    try{

    $path=Get-AssetStoragePath $Ownership.path
    $data=[IO.File]::ReadAllText($path)|ConvertFrom-Json
    if ($data.schemaVersion -ne 1 -or $data.ownerId -cne $Ownership.ownerId -or $data.state -cne 'running') { throw 'Authoring ownership does not match its completion.' }
    $context=Assert-AssetStorageOwner $data $path
    if ($data.sessionDirectory) {
        $sessionDirectory=Get-AssetStoragePath $data.sessionDirectory
        if(-not(Test-AssetStorageWithin $sessionDirectory (Join-Path $context.jobs 'BackgroundSessions'))){throw 'The session is outside the authoring project.'}
        $sessionPath=Join-Path $sessionDirectory 'session.json'
        $session=[IO.File]::ReadAllText($sessionPath)|ConvertFrom-Json
        foreach ($identity in @($session.editor,$session.cli)+@($session.cliChildren)) {
            if ($identity -and (Get-AssetOwnedProcess $identity)) { throw 'The authoring session still owns a live process.' }
        }
        if (-not $session.editorReleased -or -not $session.cliReleased -or -not $session.cliChildrenReleased) { throw 'The authoring session has not verified process release.' }
    }
    $data.files=@(Get-AssetStorageFiles @($data.jobDirectory,$data.assetDirectory,($data.assetDirectory+'.meta')) $path)
    $data.resultSha256=if ([IO.File]::Exists($data.resultPath)) {(Get-FileHash -LiteralPath $data.resultPath -Algorithm SHA256).Hash}else{$null}
    $data.state='completed';$data.outcome=if($Succeeded){'passed'}else{'failed'};$data.failure=$Failure;$data.completedUtc=[DateTime]::UtcNow.ToString('O')
    Write-AssetWorkerJson $path $data

    }finally{$storageOperation.ReleaseMutex();$storageOperation.Dispose()}
}
function Select-AssetStorageUnits {
    param([array]$Units,$References,[int]$KeepLatest=2,[double]$MaxAgeDays=0,[long]$MaxRetainedBytes=0)
    $reasons=@{};$owners=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($unit in $Units) {
        $unit.completedUtc=if($unit.completedUtc){([DateTimeOffset]$unit.completedUtc).UtcDateTime}
            else{[DateTime]::SpecifyKind([DateTime]::MinValue,[DateTimeKind]::Utc)}
        foreach ($root in $unit.roots) {$owners[$root]=$unit}
        if ($unit.hardReason) {$reasons[$unit.path]=$unit.hardReason}
        foreach ($reference in $References.paths) {
            if (@($unit.roots|Where-Object{(Test-AssetStorageWithin $reference $_)-or(Test-AssetStorageWithin $_ $reference)}).Count) {$reasons[$unit.path]='explicit-reference';break}
        }
    }
    foreach ($group in @($Units|Where-Object{-not $_.PSObject.Properties['kind'] -or $_.kind -ne 'session'}|Group-Object {if($_.PSObject.Properties['kind']){$_.kind}else{'output'}})) {
        foreach ($unit in @($group.Group|Sort-Object completedUtc -Descending|Select-Object -First $KeepLatest)) {
            if (-not $reasons.ContainsKey($unit.path) -and ($MaxAgeDays -eq 0 -or ([DateTime]$unit.completedUtc).ToUniversalTime() -ge [DateTime]::UtcNow.AddDays(-$MaxAgeDays))) {$reasons[$unit.path]='latest'}
        }
    }
    function Get-DependencyClosure {
        $kept=@{};foreach($key in $reasons.Keys){$kept[$key]=$reasons[$key]}
        $pending=[Collections.Generic.Queue[object]]::new();foreach($unit in $Units){if($kept.ContainsKey($unit.path)){$pending.Enqueue($unit)}}
        while($pending.Count){
            $unit=$pending.Dequeue()
            foreach($dependency in $unit.dependencies){
                for($cursor=$dependency;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
                    if($owners.ContainsKey($cursor)){
                        $owner=$owners[$cursor]
                        if(-not $kept.ContainsKey($owner.path)){$kept[$owner.path]='retained-input';$pending.Enqueue($owner)}
                        break
                    }
                }
            }
        }
        return ,$kept
    }
    while($true){
        $kept=Get-DependencyClosure
        $bytes=[long]($Units|Where-Object{$kept.ContainsKey($_.path)}|Measure-Object bytes -Sum|ForEach-Object{$_.Sum})
        if($MaxRetainedBytes -eq 0 -or $bytes -le $MaxRetainedBytes){break}
        $next=@($Units|Where-Object{$reasons.ContainsKey($_.path)-and $reasons[$_.path] -eq 'latest'}|Sort-Object completedUtc,path|Select-Object -First 1)
        if(-not $next.Count){break};[void]$reasons.Remove($next[0].path)
    }
    return [pscustomobject]@{reasons=$kept;retainedBytes=$bytes;budgetSatisfied=$(if($MaxRetainedBytes -gt 0){$bytes -le $MaxRetainedBytes}else{$null})}
}
function Get-AssetStorageConsumers([array]$Units) {
    $owners=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $consumers=@{}
    foreach($unit in $Units){foreach($root in $unit.roots){$owners[$root]=$unit.path};$consumers[$unit.path]=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)}
    foreach($unit in $Units){foreach($dependency in $unit.dependencies){
        for($cursor=$dependency;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
            if(-not $owners.ContainsKey($cursor)){continue}
            $owner=$owners[$cursor];if($owner -ne $unit.path){[void]$consumers[$owner].Add($unit.path)};break
        }
    }}
    return ,$consumers
}
function Get-AssetStorageRemainingBytes([array]$Units,$Planned,[bool]$Apply) {
    $remaining=[long]0
    foreach($unit in $Units){if(([IO.File]::Exists($unit.path)-or[IO.Directory]::Exists($unit.path))-and ($Apply -or -not $Planned.Contains($unit.path))){$remaining+=$unit.bytes}}
    return $remaining
}
function Test-AssetStorageProcess($Identity,$Snapshot) {
    if(-not $Identity){return $false}
    if(-not $Snapshot.ContainsKey([int]$Identity.pid)){return $false}
    $process=$Snapshot[[int]$Identity.pid]
    if($process.HasExited){return $false}
    $path=$process.Path;$started=$process.StartTime.ToUniversalTime()
    if([string]::IsNullOrEmpty($path)){throw 'A live authoring process identity is unavailable.'}
    return $path -eq $Identity.path -and [Math]::Abs(($started-([DateTime]$Identity.startTimeUtc).ToUniversalTime()).TotalSeconds) -lt 1
}
function Clear-AssetAuthoringJobs {
    param([Parameter(Mandatory)][string]$Project,[string[]]$ReferencePath=@(),[string[]]$PreserveDirectory=@(),
        [ValidateRange(0,100)][int]$KeepLatest=2,[ValidateRange(0,36500)][double]$MaxAgeDays=0,
        [ValidateRange(0,9223372036854775807)][long]$MaxRetainedBytes=0,[switch]$Apply)
    $storageOperation=Enter-AssetStorageOperation $Project
    try{

    $context=Get-AssetStorageProject $Project
    [void](Get-AssetProjectLock $context.project)
    $processSnapshot=@{};foreach($process in [Diagnostics.Process]::GetProcesses()){$processSnapshot[$process.Id]=$process}
    $references=Get-AssetStorageReferences $ReferencePath
    foreach($path in $PreserveDirectory){[void]$references.paths.Add((Get-AssetStoragePath $path))}
    $directory=Join-Path $context.workspace ('StorageCleanup/'+[guid]::NewGuid().ToString('N'));[void][IO.Directory]::CreateDirectory($directory)
    $journal=Join-Path $directory 'Journal.jsonl';$reportPath=Join-Path $directory 'Result.json'
    $units=[Collections.Generic.List[object]]::new();$retained=[Collections.Generic.List[object]]::new();$actions=[Collections.Generic.List[object]]::new()
    $report=[ordered]@{schemaVersion=1;status='planning';scope='Registered completed Unity outputs and imported working copies, with separate ownership roles. Unregistered project data is retained.';
        project=$context.project;applied=[bool]$Apply;referenceInputs=$references.inputs;retainedBytes=$null;budgetSatisfied=$null;actions=@();retained=@();journal='Journal.jsonl'}
    try {
        if([IO.Directory]::Exists($context.jobs)){
            foreach($file in Get-ChildItem -LiteralPath $context.jobs -Filter storage-owner.json -Recurse -File){
                $data=$null
                try{
                    [void](Get-AssetStoragePath $file.FullName)
                    $snapshot=Read-AssetStorageJson $file.FullName;$data=$snapshot.value
                    [void](Assert-AssetStorageOwner $data $file.FullName)
                    if($data.project -ne $context.project){throw 'Unknown job ownership.'}
                    $job=Get-AssetStoragePath $data.jobDirectory;$assets=Get-AssetStoragePath $data.assetDirectory
                    if($data.disposableProject){if($context.project -ne (Join-Path $job 'Project') -or $job -eq $context.workspace -or -not(Test-AssetStorageWithin $job $context.workspace)){throw 'Invalid disposable project ownership.'}}
                    elseif($job -eq $context.jobs -or -not(Test-AssetStorageWithin $job $context.jobs) -or $file.DirectoryName -ne $job){throw 'Invalid job directory ownership.'}
                    if(-not(Test-AssetStorageWithin $assets $context.assets)-or ($assets -eq $context.assets -and -not $data.disposableProject)-or(Test-AssetStorageWithin $assets (Join-Path $context.assets 'Editor'))){throw 'Invalid generated asset ownership.'}
                    $hard=if($data.state -cne 'completed' -or -not $data.completedUtc){'incomplete'}else{$null}
                    if($data.sessionDirectory){$state=[IO.File]::ReadAllText((Join-Path (Get-AssetStoragePath $data.sessionDirectory) 'session.json'))|ConvertFrom-Json;foreach($identity in @($state.editor,$state.cli)+@($state.cliChildren)){if($identity-and(Test-AssetStorageProcess $identity $processSnapshot)){throw 'The authoring worker remains active.'}}}
                    $dependencies=@($data.dependencies);if($data.sessionDirectory){$dependencies+= $data.sessionDirectory}
                    $roles=@([pscustomobject]@{kind='job';path=$file.FullName;roots=@($job);dependencies=$dependencies})
                    if(-not $data.disposableProject -and ([IO.Directory]::Exists($assets)-or[IO.File]::Exists($assets+'.meta'))){
                        $importIdentity=if([IO.Directory]::Exists($assets)){$assets}else{$assets+'.meta'}
                        $roles+= [pscustomobject]@{kind='import';path=$importIdentity;roots=@($assets,($assets+'.meta'));dependencies=@($job)}
                    }
                    $verificationRoots=if($data.disposableProject){@($job)}else{@($assets,($assets+'.meta'),$job)}
                    $ownerUnits=[Collections.Generic.List[object]]::new()
                    foreach($role in $roles){
                        $bytes=[long]0
                        foreach($root in $role.roots){if([IO.Directory]::Exists($root)){$bytes+=[long](Get-ChildItem -LiteralPath $root -Recurse -Force -File|Measure-Object Length -Sum|ForEach-Object{$_.Sum})}elseif([IO.File]::Exists($root)){$bytes+=(Get-Item -LiteralPath $root).Length}}
                        $unit=[pscustomobject]@{kind=$role.kind;path=$role.path;identityPath=$file.FullName;identityHash=$snapshot.sha256;
                            roots=$role.roots;verificationRoots=$verificationRoots;dependencies=$role.dependencies;completedUtc=$data.completedUtc;
                            bytes=$bytes;hardReason=$hard;data=$data}
                        $ownerUnits.Add($unit)
                    }
                    foreach($unit in $ownerUnits){$units.Add($unit)}
                }catch{
                    $failure=$_.Exception.Message
                    if($null -ne (Get-Variable -Name data -ErrorAction SilentlyContinue)-and $data -and $data.PSObject.Properties['dependencies']){
                        foreach($dependency in @($data.dependencies)){try{[void]$references.paths.Add((Get-AssetStoragePath $dependency))}catch{}}
                    }
                    $retained.Add([ordered]@{path=$file.FullName;reason=$failure})
                }
            }
        }
        $sessionRoot=Join-Path $context.jobs 'BackgroundSessions'
        if([IO.Directory]::Exists($sessionRoot)){
            foreach($sessionDirectory in Get-ChildItem -LiteralPath $sessionRoot -Directory){
                $statePath=Join-Path $sessionDirectory.FullName 'session.json'
                try{
                    [void](Get-AssetStoragePath $statePath)
                    $state=[IO.File]::ReadAllText($statePath)|ConvertFrom-Json
                    $configuration=[IO.File]::ReadAllText((Join-Path $sessionDirectory.FullName 'configuration.json'))|ConvertFrom-Json
                    if($sessionDirectory.Name -cnotmatch '^[a-f0-9]{32}$' -or $state.ownerId -cne $sessionDirectory.Name -or
                        $configuration.ownerId -cne $state.ownerId -or $configuration.project -ne $context.project){throw 'Unknown background session ownership.'}
                    $hard=if(-not $state.editorReleased -or -not $state.cliReleased -or -not $state.cliChildrenReleased){'incomplete-session'}else{$null}
                    foreach($identity in @($state.editor,$state.cli)+@($state.cliChildren)){if($identity-and(Test-AssetStorageProcess $identity $processSnapshot)){$hard='active-session'}}
                    $files=@(Get-AssetStorageFiles @($sessionDirectory.FullName) $statePath)
                    $data=[pscustomobject]@{ownerId=$state.ownerId;jobDirectory=$sessionDirectory.FullName;assetDirectory=$null;files=$files;
                        outcome=$state.status;failure=$(if($state.PSObject.Properties['error']){$state.error}else{$null});resultSha256=(Get-FileHash -LiteralPath $statePath -Algorithm SHA256).Hash}
                    $units.Add([pscustomobject]@{kind='session';path=$statePath;identityPath=$statePath;identityHash=$data.resultSha256;roots=@($sessionDirectory.FullName);verificationRoots=@($sessionDirectory.FullName);dependencies=@();
                        completedUtc=(Get-Item -LiteralPath $statePath).LastWriteTimeUtc.ToString('O');bytes=[long](($files|Measure-Object bytes -Sum).Sum)+(Get-Item -LiteralPath $statePath).Length;hardReason=$hard;data=$data})
                }catch{$retained.Add([ordered]@{path=$statePath;reason=$_.Exception.Message})}
            }
        }
        $protected=Select-AssetStorageUnits $units.ToArray() $references 0
        $checkedOwners=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($unit in $units){
            if($unit.kind -eq 'session' -or $protected.reasons.ContainsKey($unit.path) -or -not $checkedOwners.Add($unit.identityPath)){continue}
            # Acquisition failures still go through the deletion guard below.
            try{$unchanged=Test-AssetStorageFiles $unit.verificationRoots $unit.identityPath $unit.data.files}catch{continue}
            if(-not $unchanged){
                foreach($owned in $units){if($owned.identityPath -eq $unit.identityPath){$owned.hardReason='Authoring outputs changed after completion.'}}
            }
        }
        $selection=Select-AssetStorageUnits $units.ToArray() $references $KeepLatest $MaxAgeDays $MaxRetainedBytes
        $report.retainedBytes=$selection.retainedBytes;$report.budgetSatisfied=$selection.budgetSatisfied
        foreach($unit in $units){if($selection.reasons.ContainsKey($unit.path)){$retained.Add([ordered]@{path=$unit.path;reason=$selection.reasons[$unit.path]})}}
        $pending=[Collections.Generic.List[object]]::new();foreach($unit in @($units|Where-Object{-not $selection.reasons.ContainsKey($_.path)}|Sort-Object completedUtc,path)){$pending.Add($unit)}
        $consumers=Get-AssetStorageConsumers $units.ToArray();$planned=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        while($pending.Count){
            $next=@($pending|Where-Object{
                $candidate=$_
                -not @($consumers[$candidate.path]|Where-Object{([IO.File]::Exists($_)-or[IO.Directory]::Exists($_))-and ($Apply -or -not $planned.Contains($_))}).Count
            }|Select-Object -First 1)
            if(-not $next.Count){foreach($unit in $pending){$retained.Add([ordered]@{path=$unit.path;reason='remaining-input-consumer'})};break}
            $unit=$next[0];[void]$pending.Remove($unit)
            $data=$unit.data
            $action=[ordered]@{kind=$unit.kind;ownerId=$data.ownerId;jobDirectory=$data.jobDirectory;assetDirectory=$(if($unit.kind -eq 'import'){$data.assetDirectory}else{$null});roots=$unit.roots;bytes=$unit.bytes;applied=$false}
            try {
                [void](Get-AssetProjectLock $context.project);Assert-AssetStorageReferences $references
                if((Get-FileHash -LiteralPath (Get-AssetStoragePath $unit.identityPath) -Algorithm SHA256).Hash -cne $unit.identityHash){throw 'Job ownership changed during cleanup.'}
                if(-not(Test-AssetStorageFiles $unit.verificationRoots $unit.identityPath $data.files)){throw 'Authoring outputs changed after completion.'}
                Write-AssetStorageEvent $journal ([ordered]@{event='retire-job';kind=$unit.kind;ownerId=$data.ownerId;ownershipSha256=$unit.identityHash;resultSha256=$data.resultSha256;outcome=$data.outcome;failure=$data.failure;roots=$unit.roots;action=$action})
                $actions.Add($action)
                if($Apply){foreach($root in $unit.roots){
                    [void](Get-AssetProjectLock $context.project);Assert-AssetStorageReferences $references
                    if((Get-FileHash -LiteralPath (Get-AssetStoragePath $unit.identityPath) -Algorithm SHA256).Hash -cne $unit.identityHash){throw 'Job ownership changed before deletion.'}
                    $path=Get-AssetStoragePath $root
                    if($path -eq $context.workspace -or $path -eq $directory){throw 'The cleanup receipt root cannot be deleted.'}
                    Write-AssetStorageEvent $journal ([ordered]@{event='delete-planned';ownerId=$data.ownerId;path=$path})
                    if([IO.Directory]::Exists($path)){Remove-Item -LiteralPath $path -Recurse -Force}elseif([IO.File]::Exists($path)){Remove-Item -LiteralPath $path -Force}
                    Write-AssetStorageEvent $journal ([ordered]@{event='delete-complete';ownerId=$data.ownerId;path=$path})
                };$action.applied=$true}
                [void]$planned.Add($unit.path)
            }catch{$retained.Add([ordered]@{path=$unit.path;reason=$_.Exception.Message});if($Apply){throw}}
        }
        $report.status='complete'
    }catch{$report.status='failed';$report['error']=$_.Exception.Message;throw}
    finally{
        if($null -ne (Get-Variable -Name planned -ErrorAction SilentlyContinue)){
            $report.retainedBytes=Get-AssetStorageRemainingBytes $units.ToArray() $planned ([bool]$Apply)
            $report.budgetSatisfied=if($MaxRetainedBytes -gt 0){$report.retainedBytes -le $MaxRetainedBytes}else{$null}
        }
        $report.actions=@($actions.ToArray());$report.retained=@($retained.ToArray());Write-AssetWorkerJson $reportPath $report
        foreach($process in $processSnapshot.Values){$process.Dispose()}
    }
    return $reportPath

    }finally{$storageOperation.ReleaseMutex();$storageOperation.Dispose()}
}

if ($Configuration) { Invoke-AssetBackgroundHost $Configuration }
