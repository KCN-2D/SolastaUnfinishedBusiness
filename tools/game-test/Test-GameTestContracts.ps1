[CmdletBinding()]
param(
    [string]$EvidenceRoot,
    [switch]$SkipProcessLifecycle
)
. (Join-Path $PSScriptRoot 'GameTest.Common.ps1')
if (-not $EvidenceRoot) { $EvidenceRoot=$script:GameTestArtifactRoot }
$run=New-GtDirectory $EvidenceRoot 'Contracts'
$result=[ordered]@{
    schemaVersion=1; status='ERROR'; reason='Contract checks have not completed.'
    startedUtc=[datetime]::UtcNow.ToString('O'); sources=@(); processGuards=@()
    processGuardsScope='Actual guard function AST with mocked process lookup and settings boundaries.'
    processLifecycle=[ordered]@{status='NOT_RUN';reason='Process lifecycle checks have not run.'}
    nativeActionProof=[ordered]@{status='NOT_RUN';reason='Native action proof contracts have not run.'}
}

function Read-ContractSource([string]$Path) {
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile($Path,[ref]$tokens,[ref]$errors)
    if ($errors) { throw ($errors | Out-String) }
    return $ast
}

function Read-ContractFunction([object]$Ast,[string]$Name) {
    $functions=@($Ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name
    },$true))
    if ($functions.Count -ne 1) { throw "The contract requires one function named $Name." }
    return $functions[0].Extent.Text
}

function Invoke-ProcessGuardContract([string]$Definitions,[string]$Guard,[string]$Mode) {
    $record=& {
        param($Definitions,$Guard,$Mode)
        . ([scriptblock]::Create($Definitions))
        $counts=[pscustomobject]@{processQueries=0;settingsReadBoundaryCalls=0;restorationChecks=0}

        function New-ContractProcess([int]$ProcessId) {
            [pscustomobject]@{
                Id=$ProcessId; StartTime=[datetime]'2000-01-01T00:00:00Z'
                Path='C:\ContractFixture\Solasta.exe'; HasExited=$false
            }
        }
        function Get-Process {
            [CmdletBinding()]
            param([string]$Name)
            $counts.processQueries++
            switch ($Mode) {
                'one' { New-ContractProcess 41001 }
                'multiple' { New-ContractProcess 41001; New-ContractProcess 41002 }
                'terminating-error' { throw 'Contract process enumeration failed.' }
                'permission-error' { Write-Error 'Contract process access denied.' -Category PermissionDenied }
                'unrelated-not-found' { Write-Error 'Contract unrelated failure.' -ErrorId OtherProcessQueryFailure -Category ObjectNotFound }
                'absent' { }
                'absent-error' { Write-Error 'Contract process absent.' -ErrorId NoProcessFoundForGivenName -Category ObjectNotFound }
                'metadata-unreadable' {
                    $process=[pscustomobject]@{Id=41003}
                    $process | Add-Member ScriptProperty StartTime { throw 'Contract StartTime denied.' }
                    $process | Add-Member ScriptProperty Path { throw 'Contract Path denied.' }
                    $process
                }
                'exited-reported' { $process=New-ContractProcess 41004; $process.HasExited=$true; $process }
                'partial-error' { New-ContractProcess 41005; Write-Error 'Contract later enumeration failed.' -Category PermissionDenied }
                default { throw 'Unknown process contract mode.' }
            }
        }
        function Get-NativeDisplaySettingsSnapshot {
            $counts.settingsReadBoundaryCalls++
            [ordered]@{
                schemaVersion=1; keyPath=$script:NativeDisplayRegistryPath
                readSucceeded=$true; keyExisted=$false; values=@()
            }
        }
        function Assert-NativeDisplaySettingsRestored { param($Snapshot) $counts.restorationChecks++ }
        function Stop-Process { throw 'Contract guard checks must not stop a process.' }
        function Start-Process { throw 'Contract guard checks must not start a process.' }

        $snapshot=[pscustomobject]@{
            schemaVersion=1; keyPath=$script:NativeDisplayRegistryPath
            readSucceeded=$true; keyExisted=$false; values=@()
        }
        $thrown=$null; $continued=$false; $returned=$null
        try {
            if ($Guard -eq 'Restore-NativeDisplaySettings') { $returned=Restore-NativeDisplaySettings $snapshot }
            else { & $Guard }
            $continued=$true
        } catch { $thrown=$_.Exception.Message }
        $diagnostic=$null
        if ($thrown -and $thrown.Contains('Process state: ')) {
            $diagnostic=$thrown.Substring($thrown.IndexOf('Process state: ')+15) | ConvertFrom-Json
        }
        [ordered]@{
            guard=$Guard; mode=$Mode; thrown=$thrown; continued=$continued
            returned=$returned; counts=$counts; diagnostic=$diagnostic
        }
    } $Definitions $Guard $Mode

    $reject=$Mode -notin @('absent','absent-error')
    $checks=[Collections.Generic.List[object]]::new()
    $checks.Add([ordered]@{
        name='continuation'; expected=(-not $reject); actual=$record.continued
        passed=($record.continued -eq (-not $reject))
    })
    if ($reject) {
        $checks.Add([ordered]@{
            name='rejection before settings access'
            passed=([bool]$record.thrown -and $record.counts.settingsReadBoundaryCalls -eq 0 -and $record.counts.restorationChecks -eq 0)
        })
        $checks.Add([ordered]@{name='structured process diagnostic';passed=($null -ne $record.diagnostic)})
    } else {
        $checks.Add([ordered]@{name='no rejection';passed=(-not $record.thrown)})
        if ($Guard -eq 'Restore-NativeDisplaySettings') {
            $checks.Add([ordered]@{
                name='unchanged settings use only mocked read boundaries'
                passed=($record.returned.succeeded -and $record.counts.settingsReadBoundaryCalls -eq 1 -and $record.counts.restorationChecks -eq 1)
            })
        }
    }
    if ($reject -and $null -ne $record.diagnostic) {
        if ($Mode -in @('one','multiple','metadata-unreadable','exited-reported','partial-error')) {
            $expectedPid=switch ($Mode) {
                'metadata-unreadable' { 41003 }
                'exited-reported' { 41004 }
                'partial-error' { 41005 }
                default { 41001 }
            }
            $checks.Add([ordered]@{name='matching PID retained';passed=($expectedPid -in @($record.diagnostic.matches.pid))})
            if ($Mode -eq 'multiple') {
                $checks.Add([ordered]@{name='second matching PID retained';passed=(41002 -in @($record.diagnostic.matches.pid))})
            }
            if ($Mode -eq 'metadata-unreadable') {
                $checks.Add([ordered]@{
                    name='unreadable metadata retains the process and failure reasons'
                    passed=($record.diagnostic.matches.Count -eq 1 -and
                        'startTimeUtc' -in $record.diagnostic.matches[0].readErrors.PSObject.Properties.Name -and
                        'path' -in $record.diagnostic.matches[0].readErrors.PSObject.Properties.Name)
                })
            } else {
                $checks.Add([ordered]@{
                    name='matching process start time and path recorded'
                    passed=((([datetime]$record.diagnostic.matches[0].startTimeUtc).ToUniversalTime().ToString('O') -eq '2000-01-01T00:00:00.0000000Z') -and
                        $record.diagnostic.matches[0].path -eq 'C:\ContractFixture\Solasta.exe')
                })
            }
        }
        if ($Mode -in @('terminating-error','permission-error','unrelated-not-found','partial-error')) {
            $checks.Add([ordered]@{
                name='process query failure stays distinct from absence'
                passed=($record.diagnostic.querySucceeded -eq $false -and $null -ne $record.diagnostic.queryError)
            })
        }
    }
    $record['checks']=$checks.ToArray()
    $record['status']=if (@($checks | Where-Object passed -eq $false).Count) { 'FAIL' } else { 'PASS' }
    return $record
}

function Invoke-OwnedProcessContract([string]$Definitions,[string]$Initialize,[string]$Cleanup,[string]$Mode,[string]$Directory,[string]$Shell) {
    & {
        param($Definitions,$Initialize,$Cleanup,$Mode,$Directory,$Shell)
        . ([scriptblock]::Create($Definitions))
        [void][IO.Directory]::CreateDirectory($Directory)
        $runDirectory=$Directory
        $process=$null; $ownedStart=$null; $ownedPath=$null
        $record=[ordered]@{mode=$Mode;status='ERROR';checks=@();cleanup=$null;aliveBeforeTeardown=$null;fixtureProcessRemoved=$false;error=$null;teardownError=$null}
        try {
            $milliseconds=if ($Mode -like 'already-exited*') { 250 } elseif ($Mode -in @('natural-exit','diagnostic-write-failure')) { 1500 } else { 15000 }
            $ownedPath=Get-FullPath $Shell
            $process=Start-Process -FilePath $Shell -ArgumentList @('-NoProfile','-NonInteractive','-Command',"[Threading.Thread]::Sleep($milliseconds)") -WindowStyle Hidden -PassThru
            $ownedStart=$process.StartTime
            $startup=[Diagnostics.Stopwatch]::StartNew()
            do {
                $process.Refresh()
                $modulePath=$null
                if (-not $process.HasExited) {
                    $module=$process.MainModule
                    if ($null -ne $module) { $modulePath=$module.FileName }
                }
                if ($modulePath -and (Get-FullPath $modulePath) -eq $ownedPath) { break }
                Start-Sleep -Milliseconds 10
            } while ($startup.ElapsedMilliseconds -lt 2000 -and -not $process.HasExited)
            $startup.Stop()
            if (-not $modulePath -or (Get-FullPath $modulePath) -ne $ownedPath) { throw 'The fixture executable did not become observable.' }
            $processStartTime=$ownedStart; $GameExePath=$ownedPath
            if ($Mode -like 'already-exited*' -and -not $process.WaitForExit(5000)) { throw 'The short-lived fixture process did not exit.' }
            if ($Mode -eq 'identity-mismatch') { $GameExePath=Join-Path $Directory 'WrongExecutable.exe' }
            if ($Mode -eq 'unknown-start') { $processStartTime=$null }
            if ($Mode -eq 'diagnostic-write-failure') {
                function Write-Json([string]$Path,[object]$Value) { throw 'Contract diagnostic write failure.' }
            }
            if ($Mode -in @('already-exited-wait-timeout','already-exited-wait-error')) {
                function Wait-OwnedProcessExit([string]$Kind,[int]$TimeoutMilliseconds) {
                    $wait=[ordered]@{kind=$Kind;timeoutMilliseconds=$TimeoutMilliseconds;result=$null;error=$null;fixture='termination wait failure injection'}
                    $result.processCleanup.waits+=$wait
                    try {
                        if ($Mode -eq 'already-exited-wait-error') { throw 'Contract exit wait failure.' }
                        $wait.result=$false
                        return $false
                    } catch { $wait.error=$_.Exception.Message; throw }
                    finally { Save-OwnedProcessCleanupEvidence }
                }
            }
            $result=[ordered]@{processId=$process.Id;status='success';reason='Owned process contract.'}
            $gameStopped=$true
            . ([scriptblock]::Create($Initialize))
            . ([scriptblock]::Create($Cleanup))
            $process.Refresh()
            $record.aliveBeforeTeardown=-not $process.HasExited
            $record.cleanup=$result
            $state=$result.processCleanup
            $identityRefused=$Mode -in @('identity-mismatch','unknown-start')
            $waitRefused=$Mode -in @('already-exited-wait-timeout','already-exited-wait-error')
            $refused=$identityRefused -or $waitRefused
            $record.checks+=@(
                [ordered]@{name='cleanup completion';expected=(-not $refused);actual=$state.completed;passed=($state.completed -eq (-not $refused))},
                [ordered]@{name='restoration continuation';expected=(-not $refused);actual=$gameStopped;passed=($gameStopped -eq (-not $refused))},
                [ordered]@{name='process state before fixture teardown';expected=$identityRefused;actual=$record.aliveBeforeTeardown;passed=($record.aliveBeforeTeardown -eq $identityRefused)},
                [ordered]@{name='expected identity recorded';passed=($state.expectedProcessId -eq $process.Id -and $state.expectedPath -eq $GameExePath)}
            )
            if ($identityRefused) {
                $record.checks+=[ordered]@{name='identity refusal precedes close and stop';passed=($result.status -eq 'failed' -and $state.error -like '*identity changed*' -and -not $state.closeMainWindowRequested -and -not $state.forcedTerminationRequested -and $state.waits.Count -eq 0)}
                if ($Mode -eq 'unknown-start') {
                    $record.checks+=[ordered]@{name='unknown start time stays null';passed=($null -eq $state.expectedStartTimeUtc)}
                }
            } elseif ($Mode -like 'already-exited*') {
                $record.checks+=[ordered]@{name='already exited requires termination signal without stop';passed=($state.alreadyExited -and $state.waits.Count -eq 1 -and $state.waits[0].kind -eq 'already-exited' -and $state.waits[0].timeoutMilliseconds -eq 5000 -and -not $state.closeMainWindowRequested -and -not $state.forcedTerminationRequested)}
                if ($waitRefused) {
                    $record.checks+=[ordered]@{name='termination wait failure prevents restoration';passed=($result.status -eq 'failed' -and -not $gameStopped -and -not $state.completed)}
                    $expectedWaitError=if ($Mode -eq 'already-exited-wait-error') { 'Contract exit wait failure.' } else { 'The exited game process did not signal termination.' }
                    $record.checks+=[ordered]@{name='termination wait failure reason retained';passed=($state.error -eq $expectedWaitError)}
                } else {
                    $record.checks+=[ordered]@{name='real exited process signals termination';passed=($state.waits[0].result -eq $true -and $null -eq $state.waits[0].error)}
                }
            } elseif ($Mode -eq 'forced-exit') {
                $record.checks+=[ordered]@{name='original graceful and forced exit waits';passed=($state.closeMainWindowRequested -and $state.forcedTerminationRequested -and $state.waits.Count -eq 2 -and $state.waits[0].kind -eq 'graceful' -and $state.waits[0].timeoutMilliseconds -eq 5000 -and $state.waits[0].result -eq $false -and $state.waits[1].kind -eq 'after-force' -and $state.waits[1].timeoutMilliseconds -eq 5000 -and $state.waits[1].result -eq $true)}
            } else {
                $record.checks+=[ordered]@{name='natural exit uses the graceful wait';passed=($state.closeMainWindowRequested -and -not $state.forcedTerminationRequested -and $state.waits.Count -eq 1 -and $state.waits[0].kind -eq 'graceful' -and $state.waits[0].result -eq $true)}
            }
            if ($Mode -eq 'diagnostic-write-failure') {
                $record.checks+=[ordered]@{name='failed diagnostic does not skip owned process cleanup';passed=($result.status -eq 'failed' -and $state.observationErrors.Count -gt 0 -and -not (Test-Path -LiteralPath (Join-Path $Directory 'Runtime-ProcessCleanup.json')))}
            } else {
                $record.checks+=[ordered]@{name='diagnostic file saved';passed=((Test-Path -LiteralPath (Join-Path $Directory 'Runtime-ProcessCleanup.json')) -and $state.observationErrors.Count -eq 0)}
            }
            $record.status=if (@($record.checks | Where-Object passed -eq $false).Count) { 'FAIL' } else { 'PASS' }
        } catch { $record.status='ERROR'; $record.error=$_.Exception.Message }
        finally {
            if ($null -ne $process) {
                try {
                    $process.Refresh()
                    if (-not $process.HasExited) {
                        if ($null -eq $ownedStart -or $null -eq $ownedPath -or $process.StartTime -ne $ownedStart -or (Get-FullPath $process.MainModule.FileName) -ne $ownedPath) {
                            throw 'The fixture process identity cannot be verified. Refusing to stop it.'
                        }
                        Stop-Process -InputObject $process -Force
                        if (-not $process.WaitForExit(5000)) { throw 'The owned fixture process did not exit.' }
                    }
                    $record.fixtureProcessRemoved=$true
                } catch { $record.status='ERROR'; $record.teardownError=$_.Exception.Message }
                $process.Dispose()
            }
        }
        $record
    } $Definitions $Initialize $Cleanup $Mode $Directory $Shell
}

function Invoke-NativeActionProofContract([string]$Statements,[string]$Mode) {
    $variants=if ($Mode -eq 'interval-focus-invalid') { @('regained','nonboolean') } else { @('default') }
    $trials=@(foreach ($variant in $variants) {
        & {
            param($Statements,$Mode,$Variant)
            $processId=42001
            $expectedDesktopName='Contract_NonInput'
            $verifyFocusLoss=$Mode -ne 'native-focus-not-required'
            $result=[ordered]@{processId=$processId;foreground=[ordered]@{primaryThreadId=43001}}
            $start=[datetime]'2000-01-01T00:00:00Z'
            $samples=@(foreach ($index in 0..3) {
                [pscustomobject]@{
                    processId=$processId; nativeThreadId=43001
                    eventName=$(if ($index -in @(1,3)) { 'operation' } else { 'heartbeat' })
                    phase=$(if ($index -eq 1) { 'action' } elseif ($index -eq 3) { 'action-completed' } else { 'ready' })
                    frame=(9+$index); utc=$start.AddMilliseconds(100*$index).ToString('O')
                    focused=($index -eq 0); runInBackground=$true; batchMode=$false
                    graphicsDeviceAvailable=$true; graphicsDeviceType='Direct3D11'; graphicsDeviceName='Contract fixture'
                    screenWidth=3840; screenHeight=2160
                }
            })
            if ($Mode -eq 'native-focus-not-required') { foreach ($sample in $samples) { $sample.focused=$true } }
            if ($Mode -eq 'initially-unfocused') { $samples[0].focused=$false }
            if ($Mode -eq 'interval-focus-invalid') { $samples[2].focused=if ($Variant -eq 'regained') { $true } else { 'false' } }
            $desktopSamples=@([pscustomobject]@{
                capturedUtc=$start.AddMilliseconds(200).ToString('O')
                ownedWindows=@([pscustomobject]@{
                    processId=$processId; nativeThreadId=43001; windowHandle=44001
                    desktopQuerySucceeded=$true; desktop=$expectedDesktopName
                    inputQuery=[pscustomobject]@{succeeded=$true;rawValue=0}
                    visible=$true;clientRectQuerySucceeded=$true
                    clientWidth=$(if ($Mode -eq 'render-window-mismatch') { 1280 } else { 3840 });clientHeight=2160
                })
            })
            # The extracted statements, including the actual rejection branches, own the decision.
            $continued=$false; $thrown=$null
            try { . ([scriptblock]::Create($Statements)); $continued=$true }
            catch { $thrown=$_.Exception.Message }
            $expectedContinuation=$Mode -in @('native-focus-not-required','observed-focus-transition')
            $expectedReason=switch ($Mode) {
                'initially-unfocused' { 'The requested native action did not demonstrate an actual Unity focus transition.' }
                'interval-focus-invalid' { 'The requested native action did not remain unfocused.' }
                'render-window-mismatch' { 'The native action has no verified game PID/window/thread/desktop correspondence.' }
                default { $null }
            }
            if (-not $result.Contains('backgroundActionProof')) { throw ('The proof fixture did not reach the actual proof receipt: ' + $thrown) }
            $proof=$result.backgroundActionProof
            $checks=@(
                [ordered]@{name='continuation';expected=$expectedContinuation;actual=$continued;passed=($continued -eq $expectedContinuation)},
                [ordered]@{name='rejection reason';expected=$expectedReason;actual=$thrown;passed=($thrown -ceq $expectedReason)},
                [ordered]@{name='actual proof retained';passed=($null -ne $proof)},
                [ordered]@{name='focus requirement follows the case';passed=($proof.unityFocusLossRequested -eq $verifyFocusLoss)},
                [ordered]@{name='frame and graphics requirements preserved';passed=($proof.framesAdvanced -and $proof.backgroundEnabled -and $proof.graphicsDeviceAvailable)},
                [ordered]@{name='render correspondence follows the supplied window';passed=($proof.ownedRenderWindowsMapped -eq ($Mode -ne 'render-window-mismatch'))},
                [ordered]@{name='transition requires observed focus and an unfocused interval';passed=($proof.unityFocusTransitionVerified -eq ($Mode -in @('observed-focus-transition','render-window-mismatch')))}
            )
            [ordered]@{variant=$Variant;status=$(if (@($checks | Where-Object passed -eq $false).Count) { 'FAIL' } else { 'PASS' });checks=$checks;proof=$proof}
        } $Statements $Mode $variant
    })
    [ordered]@{mode=$Mode;status=$(if (@($trials | Where-Object status -ne 'PASS').Count) { 'FAIL' } else { 'PASS' });trials=$trials}
}

try {
    $paths=@(
        'tools/runtime-verification/WindowsDesktop.ps1',
        'tools/runtime-verification/NativeDisplaySettings.ps1',
        'tools/runtime-verification/Invoke-RuntimeVerification.ps1',
        'tools/game-test/GameTest.Common.ps1',
        'tools/game-test/Test-GameTestContracts.ps1'
    )
    $archived=@{}
    foreach ($relative in $paths) {
        $source=Join-Path $script:GameTestRepo $relative
        $hash=Get-GtHash $source
        $archive=Join-Path $run ('InputSources/' + [IO.Path]::GetFileName($source))
        Copy-GtFile $source $archive -ExpectedSha256 $hash
        $archived[$relative]=$archive
        $result.sources+=[ordered]@{path=$relative;sha256=$hash;archivePath=$archive}
    }
    $desktop=Read-ContractSource $archived['tools/runtime-verification/WindowsDesktop.ps1']
    $display=Read-ContractSource $archived['tools/runtime-verification/NativeDisplaySettings.ps1']
    $runtime=Read-ContractSource $archived['tools/runtime-verification/Invoke-RuntimeVerification.ps1']
    $common=Read-ContractSource $archived['tools/game-test/GameTest.Common.ps1']
    $registrySetting=@($display.FindAll({
        param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $node.Left.VariablePath.UserPath -eq 'script:NativeDisplayRegistryPath'
    },$true))
    if ($registrySetting.Count -ne 1) { throw 'The display registry setting is not unique.' }
    $definitions=@(
        $registrySetting[0].Extent.Text
        Read-ContractFunction $desktop 'Get-VerificationProcessState'
        Read-ContractFunction $display 'Test-NativeDisplayValueName'
        Read-ContractFunction $display 'Assert-NativeDisplaySettingsSnapshot'
        Read-ContractFunction $display 'Restore-NativeDisplaySettings'
        Read-ContractFunction $common 'Assert-GtGameStopped'
        Read-ContractFunction $runtime 'Assert-NoGameProcess'
    ) -join [Environment]::NewLine
    $modes=@('one','multiple','terminating-error','permission-error','unrelated-not-found','absent','absent-error','metadata-unreadable','exited-reported','partial-error')
    foreach ($guard in @('Restore-NativeDisplaySettings','Assert-GtGameStopped','Assert-NoGameProcess')) {
        foreach ($mode in $modes) {
            $result.processGuards+=Invoke-ProcessGuardContract $definitions $guard $mode
        }
    }
    $proofBlocks=@($runtime.FindAll({
        param($node)
        $node -is [Management.Automation.Language.TryStatementAst] -and
        @($node.Body.Statements | Where-Object {
            $_ -is [Management.Automation.Language.AssignmentStatementAst] -and
            $_.Left.Extent.Text -ceq '$result[''backgroundActionProof'']'
        }).Count -eq 1
    },$true))
    if ($proofBlocks.Count -ne 1) { throw 'The native action proof source is not unique.' }
    $proofStatements=@($proofBlocks[0].Body.Statements)
    $actionStatements=@($proofStatements | Where-Object {
        $_ -is [Management.Automation.Language.AssignmentStatementAst] -and
        $_.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $_.Left.VariablePath.UserPath -ceq 'action'
    })
    if ($actionStatements.Count -ne 1) { throw 'The native action interval source is not unique.' }
    $proofStart=[Array]::IndexOf($proofStatements,$actionStatements[0])
    $proofText=($proofStatements[$proofStart..($proofStatements.Count-1)] | ForEach-Object { $_.Extent.Text }) -join [Environment]::NewLine
    $proofCases=@(foreach ($mode in @('native-focus-not-required','observed-focus-transition','initially-unfocused','interval-focus-invalid','render-window-mismatch')) {
        Invoke-NativeActionProofContract $proofText $mode
    })
    $result.nativeActionProof=[ordered]@{
        status=$(if (@($proofCases | Where-Object status -ne 'PASS').Count) { 'FAIL' } else { 'PASS' })
        scope='Actual native action proof AST with synthetic execution and desktop samples. This does not verify real Unity focus, rendering or gameplay.'
        cases=$proofCases
    }
    if (-not $SkipProcessLifecycle) {
        $initialize=@($runtime.FindAll({
            param($node)
            $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq '$result[''processCleanup'']'
        },$true))
        $cleanup=@($runtime.FindAll({
            param($node)
            $node -is [Management.Automation.Language.TryStatementAst] -and $node.Body.Extent.Text.Contains("Save-OwnedProcessObservation 'cleanup-started'")
        },$true))
        if ($initialize.Count -ne 1 -or $cleanup.Count -ne 1) { throw 'The owned process cleanup source is not unique.' }
        $lifecycleDefinitions=@(
            Read-ContractFunction $runtime 'Write-Json'
            Read-ContractFunction $runtime 'Get-FullPath'
            Read-ContractFunction $runtime 'Save-OwnedProcessCleanupEvidence'
            Read-ContractFunction $runtime 'Save-OwnedProcessObservation'
            Read-ContractFunction $runtime 'Wait-OwnedProcessExit'
        ) -join [Environment]::NewLine
        $shell=(Get-Process -Id $PID).MainModule.FileName
        $cases=@(foreach ($mode in @('already-exited','already-exited-wait-timeout','already-exited-wait-error','natural-exit','forced-exit','identity-mismatch','unknown-start','diagnostic-write-failure')) {
            Invoke-OwnedProcessContract $lifecycleDefinitions $initialize[0].Extent.Text $cleanup[0].Extent.Text $mode (Join-Path $run ('ProcessLifecycle/' + $mode)) $shell
        })
        $result.processLifecycle=[ordered]@{
            status=$(if (@($cases | Where-Object status -eq 'ERROR').Count) { 'ERROR' } elseif (@($cases | Where-Object status -ne 'PASS').Count) { 'FAIL' } else { 'PASS' })
            scope='Actual cleanup AST with owned hidden PowerShell child processes. The two termination-wait refusal cases inject a false return or exception at the wait boundary; no game or settings access.'
            cases=$cases
        }
    }
    if ($result.processLifecycle.status -eq 'ERROR') {
        $result.status='ERROR'; $result.reason='A process lifecycle fixture could not complete.'
    } elseif (@($result.processGuards | Where-Object status -ne 'PASS').Count -or $result.processLifecycle.status -eq 'FAIL' -or $result.nativeActionProof.status -eq 'FAIL') {
        $result.status='FAIL'; $result.reason='A process guard, lifecycle or native action proof contract failed.'
    } elseif ($SkipProcessLifecycle) {
        $result.processLifecycle=[ordered]@{status='SKIPPED';reason='Skipped by -SkipProcessLifecycle.'}
        $result.status='PASS'; $result.reason="All 30 process guard contracts and $($proofCases.Count) native action proof contracts passed. Process lifecycle checks were skipped."
    } else {
        $result.status='PASS'; $result.reason="All 30 process guard contracts, $($cases.Count) owned process lifecycle cases and $($proofCases.Count) native action proof contracts passed."
    }
} catch {
    $result.status='ERROR'; $result.reason=$_.Exception.Message
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $run 'Error.log') -Encoding UTF8
} finally {
    $result['completedUtc']=[datetime]::UtcNow.ToString('O')
    Write-GtJson (Join-Path $run 'Result.json') $result
}
Write-Host ($result.status + ': ' + $result.reason)
Write-Host "Evidence: $run"
if ($result.status -ne 'PASS') { throw "Contract verification $($result.status). See $run" }
$run
