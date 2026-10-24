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
    caseValidation=[ordered]@{status='NOT_RUN';reason='Saved-game case validation contracts have not run.'}
    compilationBoundary=[ordered]@{status='NOT_RUN';reason='Compilation preflight contracts have not run.'}
    desktopFocusBoundary=[ordered]@{status='NOT_RUN';reason='Desktop focus safety contracts have not run.'}
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

function Invoke-DesktopFocusBoundaryContract([string]$Source,[string]$Directory) {
    if ('UBVerification.DesktopReader' -as [type]) {
        throw 'Desktop focus contracts require a fresh PowerShell process to compile the selected source.'
    }
    [void][IO.Directory]::CreateDirectory($Directory)
    $assembly=Join-Path $Directory 'WindowsDesktop.Contract.dll'
    $types=@(Add-Type -Path $Source -OutputAssembly $assembly -PassThru)
    $reader=@($types | Where-Object FullName -ceq 'UBVerification.DesktopReader')
    if ($reader.Count -ne 1) { throw 'The selected desktop source did not compile its reader.' }
    $private=[Reflection.BindingFlags]'NonPublic,Static'
    $readInput=$reader[0].GetMethod('ReadInputState',$private)
    $requireDesktop=$reader[0].GetMethod('RequireFocusDesktop',$private)
    $peer=$reader[0].GetField('focusPeer',$private)
    if (-not $readInput -or -not $requireDesktop -or -not $peer) {
        throw 'The desktop focus safety boundaries were not found in the compiled source.'
    }

    $before=[UBVerification.DesktopReader]::ReadCurrent($null,'contract-host/before')
    $foregroundBefore=[UBVerification.DesktopReader]::ReadForeground()
    if (-not $before.complete -or -not $before.runnerInputQuery.succeeded -or
        $before.runnerInputQuery.bytesNeeded -ne 4 -or $before.runnerInputQuery.rawValue -ne 1 -or
        $before.runnerReceivesInput -ne $true -or $before.isolated -or
        $before.runnerDesktop -cne $before.inputDesktop -or $foregroundBefore.windowHandle -eq 0) {
        throw 'The desktop focus contract must run on the observed input desktop with a foreground window.'
    }
    $rejection=$null
    try { [UBVerification.DesktopReader]::DeactivateCurrentWindow(1,1) }
    catch { $rejection=$_.Exception.GetBaseException() }
    $foregroundAfter=[UBVerification.DesktopReader]::ReadForeground()
    $after=[UBVerification.DesktopReader]::ReadCurrent($null,'contract-host/after')
    $receipt=$after.focusActivation
    $checks=[Collections.Generic.List[object]]::new()
    $checks.Add([ordered]@{name='actual input-desktop entry rejected before activation';passed=(
        $null -ne $rejection -and $rejection -is [InvalidOperationException] -and
        $rejection.Message -ceq 'Focus verification requires the owned non-input desktop.' -and
        $null -ne $receipt -and -not $receipt.attempted -and -not $receipt.completed -and
        $null -eq $receipt.after -and [bool]$receipt.error)})
    $checks.Add([ordered]@{name='rejection retains actual host UOI acquisition';passed=(
        $null -ne $receipt -and $null -ne $receipt.before -and $receipt.before.complete -and
        -not $receipt.before.isolated -and $receipt.before.runnerInputQuery.succeeded -and
        $receipt.before.runnerInputQuery.bytesNeeded -eq 4 -and $receipt.before.runnerInputQuery.rawValue -eq 1 -and
        $receipt.before.runnerReceivesInput -eq $true -and $receipt.before.runnerDesktop -ceq $before.runnerDesktop)})
    $checks.Add([ordered]@{name='host foreground PID thread and window unchanged';passed=(
        $foregroundBefore.windowHandle -eq $foregroundAfter.windowHandle -and
        $foregroundBefore.processId -eq $foregroundAfter.processId -and
        $foregroundBefore.nativeThreadId -eq $foregroundAfter.nativeThreadId)})
    $checks.Add([ordered]@{name='host remains on the observed input desktop without a created peer';passed=(
        $after.complete -and -not $after.isolated -and $after.runnerReceivesInput -eq $true -and
        $after.runnerInputQuery.succeeded -and $after.runnerInputQuery.bytesNeeded -eq 4 -and
        $after.runnerInputQuery.rawValue -eq 1 -and $after.runnerDesktop -ceq $before.runnerDesktop -and
        $after.inputDesktop -ceq $before.inputDesktop -and $peer.GetValue($null) -eq [IntPtr]::Zero)})

    # An invalid handle exercises the real Win32 failure, without creating or switching a desktop.
    $failedInput=$readInput.Invoke($null,[object[]]@([IntPtr]::Zero))
    $checks.Add([ordered]@{name='actual failed UOI query retains unknown BOOL';passed=(
        -not $failedInput.succeeded -and
        $null -eq $failedInput.rawValue -and $null -eq $failedInput.value)})
    $trials=@(foreach ($mode in @('declared-valid-control','actual-invalid-handle','failed-with-false-output','short-bool-output','missing-query','incomplete-snapshot')) {
        $snapshot=[UBVerification.DesktopSnapshot]::new()
        $snapshot.complete=$true; $snapshot.isolated=$true
        $snapshot.runnerDesktop='UBGameTest_' + ('a' * 32)
        $snapshot.guiThread=[UBVerification.GuiThreadSnapshot]::new()
        $snapshot.guiThread.querySucceeded=$true
        $snapshot.runnerInputQuery=[UBVerification.BooleanQuery]::new()
        $snapshot.runnerInputQuery.succeeded=$true; $snapshot.runnerInputQuery.bytesNeeded=4
        $snapshot.runnerInputQuery.rawValue=0; $snapshot.runnerInputQuery.value=$false
        switch ($mode) {
            'actual-invalid-handle' { $snapshot.runnerInputQuery=$failedInput }
            'failed-with-false-output' { $snapshot.runnerInputQuery.succeeded=$false; $snapshot.runnerInputQuery.errorCode=6 }
            'short-bool-output' { $snapshot.runnerInputQuery.bytesNeeded=0 }
            'missing-query' { $snapshot.runnerInputQuery=$null }
            'incomplete-snapshot' { $snapshot.complete=$false }
        }
        $accepted=$false; $failure=$null
        try { [void]$requireDesktop.Invoke($null,[object[]]@($snapshot)); $accepted=$true }
        catch { $failure=$_.Exception.GetBaseException().Message }
        $expectedAcceptance=$mode -ceq 'declared-valid-control'
        [ordered]@{mode=$mode;accepted=$accepted;failure=$failure;passed=(
            $accepted -eq $expectedAcceptance -and ($expectedAcceptance -or
                $failure -ceq 'Focus verification requires the owned non-input desktop.'))}
    })
    [ordered]@{
        status=$(if (@($checks | Where-Object passed -eq $false).Count -or @($trials | Where-Object passed -eq $false).Count) {'FAIL'}else{'PASS'})
        scope='Compiled actual desktop source: real input-desktop refusal, foreground readback and invalid-handle UOI failure. Declared snapshots separately exercise the guard; no desktop creation, activation, input or game launch.'
        assembly=$assembly;checks=@($checks.ToArray());guardTrials=$trials
        hostBefore=$before;hostAfter=$after;foregroundBefore=$foregroundBefore;foregroundAfter=$foregroundAfter
        actualInvalidHandleQuery=$failedInput;rejection=$(if ($rejection) {$rejection.ToString()}else{$null})
    }
}

function Invoke-CaseValidationContracts([string]$Definitions,[string]$Directory) {
    & {
        param($Definitions,$Directory)
        . ([scriptblock]::Create($Definitions))
        [void][IO.Directory]::CreateDirectory($Directory)
        $source=Join-Path $Directory 'ContractFixtureOperation.cs'
        $validator=Join-Path $Directory 'Assert-ContractFixture.ps1'
        $reference=Join-Path $Directory 'ContractReference.dll'
        [IO.File]::Copy([object].Assembly.Location,$reference)
        [IO.File]::WriteAllText($source,'public sealed class ContractFixtureOperation { }',[Text.UTF8Encoding]::new($false))
        $validatorText=@'
param([object]$Case)
Assert-GtProperties $Case.operation @('kind','actorSource','actorIndex','actionValue','manualGate','manualGateTimeoutSeconds','verifyFocusLoss') @('kind','actionValue')
if ($Case.operation.kind -ne 'fixture-action') { throw 'The fixture validator only accepts its compiled action kind.' }
if (($Case.operation.actionValue -isnot [int] -and $Case.operation.actionValue -isnot [long]) -or $Case.operation.actionValue -ne 17) { throw 'The fixture actionValue must equal 17.' }
Assert-GtProperties $Case.expected @('loadedPartyCount','requireUserLocation','requireExploration','fixtureObserved') @()
if ('fixtureObserved' -in $Case.expected.PSObject.Properties.Name -and $Case.expected.fixtureObserved -ne $true) { throw 'The fixture observation must be expected.' }
'@
        [IO.File]::WriteAllText($validator,$validatorText,[Text.UTF8Encoding]::new($false))
        $records=@(foreach ($mode in @('legacy-move','built-in-non-move','explicit-local-non-move','local-validator-rejection',
                'invalid-actor-source','invalid-actor-index','invalid-manual-gate','invalid-gate-timeout',
                'json-api-name','json-local-without-cli','built-in-cli-override',
                'explicit-local-no-save','local-no-save-validator-rejection','local-no-save-setup',
                'built-in-save-flag-override','local-save-flag-only',
                'explicit-local-reference','local-invalid-reference','local-missing-reference',
                'built-in-reference-only-override','local-reference-only')) {
            $local=$mode -notin @('legacy-move','built-in-non-move','built-in-cli-override','built-in-save-flag-override','built-in-reference-only-override')
            $noSave=$mode -in @('explicit-local-no-save','local-no-save-validator-rejection','local-no-save-setup')
            $operation=[ordered]@{kind=$(if ($mode -eq 'legacy-move') {'move'}else{'fixture-action'});actorIndex=0;manualGate=$false;manualGateTimeoutSeconds=0}
            if ($local) { $operation['actionValue']=17 } else { $operation['offset']=@(1,0,0) }
            switch ($mode) {
                'local-validator-rejection' {$operation.actionValue=18}
                'invalid-actor-source' {$operation['actorSource']='unrecognized'}
                'invalid-actor-index' {$operation.actorIndex=-1}
                'invalid-manual-gate' {$operation.manualGate='true'}
                'invalid-gate-timeout' {$operation.manualGate=$true;$operation.manualGateTimeoutSeconds=31}
                'json-api-name' {$operation['methodName']='UnregisteredMethod'}
            }
            $case=[ordered]@{
                schemaVersion=1;id=('contract-' + $mode);adapter=$(if ($local) {'fixture-local'}else{'save-smoke'})
                initialState=[ordered]@{savePath='Fixture.sav';originalSavePath='FixtureOriginal.sav';saveSha256=('0' * 64)};operation=$operation
                expected=[ordered]@{loadedPartyCount=2;requireUserLocation=$true;requireExploration=$true}
                observation=[ordered]@{enabled=$false};timeoutSeconds=30
            }
            if ($noSave) {
                $case.initialState=[ordered]@{settingsFile='FixtureSettings.xml'}
                $case.expected=[ordered]@{fixtureObserved=($mode -ne 'local-no-save-validator-rejection')}
                $case.timeoutSeconds=15
                [void]$operation.Remove('actorIndex')
                [void]$operation.Remove('manualGate')
                [void]$operation.Remove('manualGateTimeoutSeconds')
            }
            if ($mode -eq 'local-no-save-setup') { $case.initialState['setup']=[ordered]@{kind='use-power'} }
            $path=Join-Path $Directory ($mode + '.json')
            Write-GtJson $path $case
            $returned=$null;$failure=$null;$continued=$false;$adapter=$null
            try {
                if ($mode -in @('built-in-save-flag-override','local-save-flag-only')) {
                    $adapter=Get-GtAdapter -Name $case.adapter -ScenarioSaveScenario $false
                    $returned=Read-GtCase $path -AdapterDefinition $adapter
                } elseif ($mode -in @('built-in-reference-only-override','local-reference-only')) {
                    $adapter=Get-GtAdapter -Name $case.adapter -ScenarioReferencePath @($reference)
                    $returned=Read-GtCase $path -AdapterDefinition $adapter
                } elseif ($mode -eq 'built-in-cli-override' -or $local -and $mode -ne 'json-local-without-cli') {
                    $parameters=@{Name=$case.adapter;ScenarioSource=@($source);ScenarioType='ContractFixtureOperation';ScenarioValidator=$validator;ScenarioObservation=$false}
                    if ($noSave) { $parameters['ScenarioSaveScenario']=$false }
                    if ($mode -eq 'explicit-local-reference') { $parameters['ScenarioReferencePath']=@($reference,$reference) }
                    if ($mode -eq 'local-invalid-reference') { $parameters['ScenarioReferencePath']=@($source) }
                    if ($mode -eq 'local-missing-reference') { $parameters['ScenarioReferencePath']=@((Join-Path $Directory 'MissingReference.dll')) }
                    $adapter=Get-GtAdapter @parameters
                    $returned=Read-GtCase $path -AdapterDefinition $adapter
                } else { $adapter=Get-GtAdapter $case.adapter; $returned=Read-GtCase $path }
                $continued=$true
            } catch { $failure=$_.Exception.Message }
            $expectedContinuation=$mode -in @('legacy-move','explicit-local-non-move','explicit-local-no-save','explicit-local-reference')
            $reason=switch ($mode) {
                'built-in-non-move' {'Non-move operations require an explicit local scenario validator.'}
                'local-validator-rejection' {'The fixture actionValue must equal 17.'}
                'invalid-actor-source' {'actorSource must be party or guest.'}
                'invalid-actor-index' {'actorIndex must be a nonnegative Int32.'}
                'invalid-manual-gate' {'Manual gate must contain a Boolean and integer seconds.'}
                'invalid-gate-timeout' {'manualGateTimeoutSeconds must be 1..timeoutSeconds when manualGate is enabled.'}
                'json-api-name' {'Unknown JSON property: methodName'}
                'json-local-without-cli' {'Unknown adapter. Local scenarios require explicit CLI source, type, validator and observation arguments.'}
                'built-in-cli-override' {'Local scenario arguments cannot override a built-in adapter.'}
                'built-in-save-flag-override' {'Local scenario arguments cannot override a built-in adapter.'}
                'local-save-flag-only' {'A local scenario requires an adapter name, source files, probe type, validator and explicit observation selection.'}
                'local-no-save-validator-rejection' {'The fixture observation must be expected.'}
                'local-no-save-setup' {'Initial setup requires a saved-game adapter.'}
                'built-in-reference-only-override' {'Local scenario arguments cannot override a built-in adapter.'}
                'local-reference-only' {'A local scenario requires an adapter name, source files, probe type, validator and explicit observation selection.'}
                'local-invalid-reference' {'ScenarioReferencePath must contain DLL files.'}
                'local-missing-reference' { 'Missing file: ' + (Join-Path $Directory 'MissingReference.dll') }
                default {$null}
            }
            $checks=@(
                [ordered]@{name='acceptance';expected=$expectedContinuation;actual=$continued;passed=($continued -eq $expectedContinuation)},
                [ordered]@{name='rejection boundary';expected=$reason;actual=$failure;passed=($failure -ceq $reason)}
            )
            if ($expectedContinuation -and $continued -and -not $noSave) {
                $checks+= [ordered]@{name='legacy defaults preserved';passed=($returned.operation.actorSource -ceq 'party' -and $returned.operation.verifyFocusLoss -eq $false)}
                $checks+= [ordered]@{name='validated operation retained';passed=($returned.operation.kind -ceq $operation.kind)}
            }
            if ($expectedContinuation -and $continued) {
                $checks+= [ordered]@{name='save selection preserved';passed=($adapter.saveScenario -eq (-not $noSave))}
            }
            if ($mode -eq 'explicit-local-reference' -and $continued) {
                $checks+= [ordered]@{name='reference path resolved and deduplicated';passed=(@($adapter.referencePaths).Count -eq 1 -and $adapter.referencePaths[0] -ceq $reference)}
            }
            [ordered]@{mode=$mode;status=$(if (@($checks | Where-Object passed -eq $false).Count) {'FAIL'}else{'PASS'});casePath=$path;checks=$checks}
        })
        [ordered]@{
            status=$(if (@($records | Where-Object status -ne 'PASS').Count) {'FAIL'}else{'PASS'})
            scope='Actual adapter and case-validation functions with owned JSON, CLI source and validator fixtures. No game launch, JSON API dispatch or operation execution.'
            cases=$records
        }
    } $Definitions $Directory
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
        'tools/runtime-verification/WindowsDesktop.cs',
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
    $result.desktopFocusBoundary=Invoke-DesktopFocusBoundaryContract $archived['tools/runtime-verification/WindowsDesktop.cs'] (Join-Path $run 'DesktopFocusBoundary')
    $preflight=@($runtime.FindAll({
        param($node)
        $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Extent.Text.Contains('A verification mod already exists.') -and
        @($node.Clauses[0].Item2.Statements | Where-Object { $_.Extent.Text -ceq 'Assert-NoGameProcess' }).Count -eq 1
    },$true))
    if ($preflight.Count -ne 1) { throw 'The compilation/execution preflight source is not unique.' }
    $compileCases=@(foreach ($compileOnly in @($true,$false)) {
        foreach ($state in @('stopped','running','existing-probe')) {
            & {
                param($source,$compileOnly,$state)
                $ValidateOnly=$compileOnly; $probeDirectory='OwnedContractProbe'; $calls=@{game=0;mod=0}
                function Assert-NoGameProcess { $calls.game++; if ($state -eq 'running') { throw 'Synthetic running game.' } }
                function Test-Path { param($LiteralPath) $calls.mod++; return $state -eq 'existing-probe' }
                $rejected=$false
                try { & ([scriptblock]::Create($source)) } catch { $rejected=$true }
                $expectedRejection=-not $compileOnly -and $state -ne 'stopped'
                $matched=$rejected -eq $expectedRejection -and
                    (($compileOnly -and $calls.game -eq 0 -and $calls.mod -eq 0) -or
                     (-not $compileOnly -and $calls.game -eq 1))
                [pscustomobject]@{compileOnly=$compileOnly;state=$state;rejected=$rejected;gameQueries=$calls.game;modQueries=$calls.mod;status=$(if($matched){'PASS'}else{'FAIL'})}
            } $preflight[0].Extent.Text $compileOnly $state
        }
    })
    $result.compilationBoundary=[ordered]@{status=$(if(@($compileCases | Where-Object status -ne 'PASS').Count){'FAIL'}else{'PASS'});scope='Actual compilation/execution preflight AST with synthetic running-process and existing-mod states.';cases=$compileCases}
    $caseDefinitions=@(
        Read-ContractFunction $common 'Assert-GtProperties'
        Read-ContractFunction $common 'Assert-GtInitialSetup'
        Read-ContractFunction $common 'Read-GtCase'
    ) -join [Environment]::NewLine
    $result.caseValidation=Invoke-CaseValidationContracts $caseDefinitions (Join-Path $run 'CaseValidation')
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
    } elseif (@($result.processGuards | Where-Object status -ne 'PASS').Count -or $result.processLifecycle.status -eq 'FAIL' -or $result.nativeActionProof.status -eq 'FAIL' -or $result.caseValidation.status -eq 'FAIL' -or $result.compilationBoundary.status -eq 'FAIL' -or $result.desktopFocusBoundary.status -ne 'PASS') {
        $result.status='FAIL'; $result.reason='A process guard, lifecycle, native action proof, case validation or desktop focus safety contract failed.'
    } elseif ($SkipProcessLifecycle) {
        $result.processLifecycle=[ordered]@{status='SKIPPED';reason='Skipped by -SkipProcessLifecycle.'}
        $result.status='PASS'; $result.reason="All 30 process guard contracts, $($compileCases.Count) compilation boundary contracts, $($proofCases.Count) native action proof contracts, $($result.caseValidation.cases.Count) case validation contracts and desktop focus safety contracts passed. Process lifecycle checks were skipped."
    } else {
        $result.status='PASS'; $result.reason="All 30 process guard contracts, $($compileCases.Count) compilation boundary contracts, $($cases.Count) owned process lifecycle cases, $($proofCases.Count) native action proof contracts, $($result.caseValidation.cases.Count) case validation contracts and desktop focus safety contracts passed."
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
