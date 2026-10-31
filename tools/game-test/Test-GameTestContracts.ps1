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
    displayCleanup=[ordered]@{status='NOT_RUN';reason='Display cleanup contracts have not run.'}
    assemblyReferences=[ordered]@{status='NOT_RUN';reason='Assembly reference contracts have not run.'}
    desktopFocusBoundary=[ordered]@{status='NOT_RUN';reason='Desktop focus safety contracts have not run.'}
    storageCleanup=[ordered]@{status='NOT_RUN';reason='Artifact cleanup safety contracts have not run.'}
    packageManifest=[ordered]@{status='NOT_RUN';reason='Package manifest contracts have not run.'}
    buildDiagnostics=[ordered]@{status='NOT_RUN';reason='Native build diagnostic contracts have not run.'}
    sourceIdentity=[ordered]@{status='NOT_RUN';reason='Cross-edition source identity contracts have not run.'}
    modLoadPlan=[ordered]@{status='NOT_RUN';reason='Verification mod load-order contracts have not run.'}
    nativeUserData=[ordered]@{status='NOT_RUN';reason='Native user-data isolation contracts have not run.'}
    presentationReview=[ordered]@{status='NOT_RUN';reason='Presentation manifest contracts have not run.'}
    nativeEvidencePaths=[ordered]@{status='NOT_RUN';reason='Native evidence path contracts have not run.'}
    sourceTracking=[ordered]@{status='NOT_RUN';reason='Development source tracking contracts have not run.'}
    persistentSync=[ordered]@{status='NOT_RUN';reason='Persistent sync contracts have not run.'}
    presentationWriter=[ordered]@{status='NOT_RUN';reason='Presentation writer contracts have not run.'}
    foregroundObservation=[ordered]@{status='NOT_RUN';reason='Foreground observation contracts have not run.'}
}

function Invoke-DisplayCleanupContracts($RuntimeAst,[string]$Directory) {
    [void][IO.Directory]::CreateDirectory($Directory)
    $cleanup=@($RuntimeAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.TryStatementAst] -and
        $node.Body.Statements.Count -eq 1 -and
        $node.Body.Statements[0] -is [Management.Automation.Language.IfStatementAst] -and
        $node.Body.Extent.Text.Contains('Restore-NativeDisplaySettings $nativeDisplayBackup')
    },$true))
    if ($cleanup.Count -ne 1) { throw 'The native display cleanup boundary must be unique.' }
    $checks=@(foreach ($mode in @('no-snapshot','restore-succeeded','restore-failed','restore-threw')) {
        & {
            param($source,$mode)
            $nativeDisplayBackup=if($mode -eq 'no-snapshot'){$null}else{[pscustomobject]@{owned=$true}}
            $result=[ordered]@{status='incomplete';reason='Compilation failed.';displaySettingsRestored=$false}
            $calls=@{restore=0}
            function Restore-NativeDisplaySettings($Snapshot) {
                $calls.restore++
                if ($null -eq $Snapshot -or -not $Snapshot.owned) { throw 'Invalid synthetic display snapshot.' }
                if ($mode -eq 'restore-threw') { throw 'Synthetic display restoration failed.' }
                [pscustomobject]@{succeeded=($mode -eq 'restore-succeeded')}
            }
            & ([scriptblock]::Create($source))
            $expectedRestored=$mode -in @('no-snapshot','restore-succeeded')
            $expectedCalls=if($mode -eq 'no-snapshot'){0}else{1}
            $expectedStatus=if($mode -eq 'restore-threw'){'failed'}else{'incomplete'}
            $expectedReason=if($mode -eq 'restore-threw'){'Compilation failed. Display settings cleanup failed: Synthetic display restoration failed.'}else{'Compilation failed.'}
            $hasReceipt=$result.Contains('displayRestoration')
            $passed=$result.displaySettingsRestored -eq $expectedRestored -and
                $calls.restore -eq $expectedCalls -and $result.status -eq $expectedStatus -and
                $result.reason -eq $expectedReason -and
                $hasReceipt -eq ($mode -in @('restore-succeeded','restore-failed'))
            [ordered]@{mode=$mode;passed=$passed;restoreCalls=$calls.restore;displaySettingsRestored=$result.displaySettingsRestored;status=$result.status;reason=$result.reason;hasRestorationReceipt=$hasReceipt}
        } $cleanup[0].Extent.Text $mode
    })
    $receipt=[ordered]@{status=$(if(@($checks|Where-Object passed -ne $true).Count){'FAIL'}else{'PASS'});scope='Actual runtime cleanup AST with synthetic display snapshots; no display settings are read or changed.';cases=$checks}
    $receipt|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $Directory 'Result.json') -Encoding UTF8
    return $receipt
}

function Invoke-AssemblyReferenceContracts($RuntimeAst,[string]$Directory) {
    [void][IO.Directory]::CreateDirectory($Directory)
    foreach ($name in @('Get-FullPath','Assert-File','Resolve-VerificationAssemblyReferences')) {
        Invoke-Expression (Read-ContractFunction $RuntimeAst $name)
    }
    $compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'The installed .NET Framework compiler is required.' }
    $fixturePaths=@{}
    foreach ($fixture in @(
        @{id='default';name='ReferenceContract';version='1.0.0.0';value='original'},
        @{id='changed';name='ReferenceContract';version='1.0.0.0';value='changed'},
        @{id='version';name='ReferenceContract';version='2.0.0.0';value='original'},
        @{id='other';name='OtherReferenceContract';version='1.0.0.0';value='original'}
    )) {
        $folder=Join-Path $Directory $fixture.id
        [void][IO.Directory]::CreateDirectory($folder)
        $sourceFile=Join-Path $folder 'Reference.cs';$output=Join-Path $folder ($fixture.name+'.dll')
        $code='[assembly:System.Reflection.AssemblyVersion("'+$fixture.version+'")] public class ReferenceContract { public const string Data="'+$fixture.value+'"; }'
        [IO.File]::WriteAllText($sourceFile,$code,[Text.UTF8Encoding]::new($false))
        & $compiler /nologo /target:library ("/out:$output") $sourceFile *> (Join-Path $folder 'Compile.log')
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $output -PathType Leaf)) { throw 'Owned reference fixture compilation failed.' }
        $fixturePaths[$fixture.id]=$output
    }
    $copy=Join-Path $Directory 'SameBytes.dll'
    [IO.File]::Copy($fixturePaths.default,$copy)
    $rows=[Collections.Generic.List[object]]::new()
    function Add-ReferenceCheck([string]$Name,[bool]$Pass) {
        $rows.Add([ordered]@{name=$Name;status=$(if($Pass){'PASS'}else{'FAIL'})})
    }
    function Test-ReferenceConflict([string[]]$Paths) {
        try { $null=@(Resolve-VerificationAssemblyReferences $Paths) }
        catch { return $_.Exception.Message -like 'Conflicting compiler reference assembly*' }
        return $false
    }
    $native=@(Resolve-VerificationAssemblyReferences @($fixturePaths.default))
    Add-ReferenceCheck 'effective metadata and bytes are recorded' ($native.Count -eq 1 -and $native[0].path -ceq [IO.Path]::GetFullPath($fixturePaths.default) -and $native[0].fullIdentity -ceq [Reflection.AssemblyName]::GetAssemblyName($fixturePaths.default).FullName -and $native[0].sha256 -ceq (Get-FileHash -LiteralPath $fixturePaths.default -Algorithm SHA256).Hash)
    $same=@(Resolve-VerificationAssemblyReferences @($fixturePaths.default,$fixturePaths.default))
    Add-ReferenceCheck 'same path is represented once' ($same.Count -eq 1)
    $same=@(Resolve-VerificationAssemblyReferences @($fixturePaths.default,$copy))
    Add-ReferenceCheck 'identical bytes retain the default first path' ($same.Count -eq 1 -and $same[0].path -ceq [IO.Path]::GetFullPath($fixturePaths.default))
    $same=@(Resolve-VerificationAssemblyReferences @($copy,$fixturePaths.default))
    Add-ReferenceCheck 'identical bytes preserve supplied order' ($same.Count -eq 1 -and $same[0].path -ceq [IO.Path]::GetFullPath($copy))
    Add-ReferenceCheck 'same identity with different bytes rejects' (Test-ReferenceConflict @($fixturePaths.default,$fixturePaths.changed))
    Add-ReferenceCheck 'conflicting bytes reject in either order' (Test-ReferenceConflict @($fixturePaths.changed,$fixturePaths.default))
    Add-ReferenceCheck 'same simple name with a different version rejects' (Test-ReferenceConflict @($fixturePaths.default,$fixturePaths.version))
    $different=@(Resolve-VerificationAssemblyReferences @($fixturePaths.other,$fixturePaths.default))
    Add-ReferenceCheck 'distinct assembly names retain their order' ($different.Count -eq 2 -and $different[0].path -ceq [IO.Path]::GetFullPath($fixturePaths.other) -and $different[1].path -ceq [IO.Path]::GetFullPath($fixturePaths.default))
    $rejected=$false
    try { $null=@(Resolve-VerificationAssemblyReferences @((Join-Path $Directory 'Missing.dll'))) }
    catch { $rejected=$_.Exception.Message -like 'Missing file:*' }
    Add-ReferenceCheck 'missing metadata is rejected before compilation' $rejected
    [ordered]@{status=$(if(@($rows|Where-Object status -ne 'PASS').Count){'FAIL'}else{'PASS'});scope='Actual runtime reference normalization AST with owned compiled assemblies. Metadata inspection and hash equality only; no game or assembly code execution.';cases=$rows.ToArray()}
}

function Invoke-ForegroundObservationContract($RuntimeAst,[string]$Directory) {
    $definition=Read-ContractFunction $RuntimeAst 'Save-ForegroundObservation'
    $cases=@(
        @{name='identified';samples=@(7);success=$true;reads=1;waits=0;desktop=$true},
        @{name='transient-unknown';samples=@(0,7);success=$true;reads=2;waits=1;desktop=$true},
        @{name='last-bounded-retry';samples=@(0,0,0,0,0,7);success=$true;reads=6;waits=5;desktop=$true},
        @{name='persistent-unknown';samples=@(0,0,0,0,0,0);success=$false;reads=6;waits=5;desktop=$true},
        @{name='game-is-foreground';samples=@(99);success=$false;reads=1;waits=0;desktop=$true},
        @{name='unknown-then-game';samples=@(0,99);success=$false;reads=2;waits=1;desktop=$true},
        @{name='unknown-with-unverified-desktop';samples=@(0);success=$false;reads=1;waits=0;desktop=$false},
        @{name='foreground-query-failed';samples=@(-1);success=$false;reads=1;waits=0;desktop=$true},
        @{name='observation-only-unknown';samples=@(0);success=$true;reads=1;waits=0;desktop=$false;observeOnly=$true;known=$false},
        @{name='observation-only-known';samples=@(7);success=$true;reads=1;waits=0;desktop=$true;observeOnly=$true;known=$true},
        @{name='observation-only-game';samples=@(99);success=$false;reads=1;waits=0;desktop=$true;observeOnly=$true;known=$true},
        @{name='observation-only-query-failed';samples=@(-1);success=$true;reads=1;waits=0;desktop=$false;observeOnly=$true;known=$false},
        @{name='observation-only-desktop-failed';samples=@(0);success=$true;reads=1;waits=0;desktop=$false;observeOnly=$true;known=$false;desktopThrows=$true}
    )
    $rows=@(foreach($case in $cases) {
        & {
            param($case,$definition,$Directory)
            $observeOnly=$case.ContainsKey('observeOnly') -and $case.observeOnly
            $runDirectory=Join-Path $Directory $case.name
            [void][IO.Directory]::CreateDirectory($runDirectory)
            $process=[pscustomobject]@{Id=99}
            $script:foregroundUnavailable=$false;$script:lastForegroundPid=-2;$script:lastForegroundWriteUtc=[datetime]::MinValue
            $state=[pscustomobject]@{index=0;waits=[Collections.Generic.List[int]]::new()}
            function Get-VerificationForeground {
                if($state.index -ge $case.samples.Count){throw 'Unexpected extra foreground lookup.'}
                $value=$case.samples[$state.index];$state.index++
                if($value -eq -1){throw 'Injected lookup failure.'}
                [pscustomobject]@{capturedUtc=[datetime]::UtcNow.ToString('O');processId=$value;nativeThreadId=1;windowHandle=$value}
            }
            function Get-VerificationDesktop {
                if($case.ContainsKey('desktopThrows') -and $case.desktopThrows){throw 'Injected desktop lookup failure.'}
                [pscustomobject]@{complete=$case.desktop;runnerReceivesInput=$case.desktop;runnerDesktop='Input';inputDesktop=$(if($case.desktop){'Input'}else{$null})}
            }
            function Start-Sleep { param([int]$Milliseconds) $state.waits.Add($Milliseconds) }
            Invoke-Expression $definition
            $failure=$null;$value=$null
            try{$value=Save-ForegroundObservation 'contract' -Force -ObserveOnly:$observeOnly}catch{$failure=$_.Exception.Message}
            $file=Join-Path $runDirectory 'Runtime-Foreground.jsonl'
            $observed=@(if(Test-Path -LiteralPath $file){Get-Content -LiteralPath $file|ForEach-Object{$_|ConvertFrom-Json}})
            $expectedRecorded=@($case.samples|Where-Object{$observeOnly -or $_ -ne -1})
            $exact=$observed.Count -eq $expectedRecorded.Count
            for($i=0;$exact -and $i -lt $observed.Count;$i++){
                $expectedPid=if($expectedRecorded[$i] -eq -1){$null}else{$expectedRecorded[$i]}
                $exact=$observed[$i].processId -eq $expectedPid -and $observed[$i].foregroundAttempt -eq $i
                if($observeOnly){$exact=$exact -and $observed[$i].observationOnly -eq $true -and
                    $observed[$i].foregroundRequirement -ceq 'not-required' -and $observed[$i].foregroundObservationKnown -eq $case.known}
                if($observeOnly -and $expectedRecorded[$i] -eq -1){$exact=$exact -and $observed[$i].queryError -ceq 'Injected lookup failure.'}
            }
            $passed=($null -eq $failure) -eq $case.success -and $state.index -eq $case.reads -and $state.waits.Count -eq $case.waits -and @($state.waits|Where-Object{$_ -ne 50}).Count -eq 0 -and $exact
            if($case.success){$passed=$passed -and -not $script:foregroundUnavailable;if(-not $observeOnly){$passed=$passed -and $value.processId -eq 7}}
            else{$passed=$passed -and $script:foregroundUnavailable}
            [ordered]@{name=$case.name;status=$(if($passed){'PASS'}else{'FAIL'});reads=$state.index;waitMilliseconds=@($state.waits);rawSamplesPreserved=$exact;failure=$failure}
        } $case $definition $Directory
    })
    [ordered]@{status=$(if(@($rows|Where-Object status -ne 'PASS').Count){'FAIL'}else{'PASS'});scope='Actual foreground observation AST: existing strict bounded checks and explicit observation-only raw unknown/error preservation. No OS focus operations.';cases=$rows}
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

function Invoke-SourceTrackingContracts([string]$RootIgnore,[string]$GameTestIgnore,[string]$Directory) {
    [void][IO.Directory]::CreateDirectory((Join-Path $Directory 'tools/game-test'))
    Copy-GtFile $RootIgnore (Join-Path $Directory '.gitignore') -ExpectedSha256 (Get-GtHash $RootIgnore)
    Copy-GtFile $GameTestIgnore (Join-Path $Directory 'tools/game-test/.gitignore') -ExpectedSha256 (Get-GtHash $GameTestIgnore)
    & git init --quiet $Directory
    if($LASTEXITCODE){throw 'Cannot initialize the isolated source tracking fixture.'}
    $fixtures=@(
        @{path='tools/game-test/FutureEnvironmentCommand.ps1';ignored=$false},
        @{path='tools/game-test/Test-FutureEnvironmentContracts.ps1';ignored=$false},
        @{path='tools/game-test/Probes/SaveSmokeProbe.cs';ignored=$false},
        @{path='tools/runtime-verification/Invoke-SolastaSemiAutoTest.ps1';ignored=$false},
        @{path='tools/runtime-verification/Probes/PresentationCapture.cs';ignored=$false},
        @{path='tools/runtime-verification/Probes/DisplayStateProbe.cs';ignored=$false},
        @{path='tools/runtime-verification/Tests/PresentationCaptureProbe.cs';ignored=$false},
        @{path='tools/runtime-verification/Probes/FutureGameProbe.cs';ignored=$true},
        @{path='tools/game-test/Probes/FutureGameProbe.cs';ignored=$true},
        @{path='tools/game-test/Scenarios/FutureFamily/Probe.cs';ignored=$true},
        @{path='tools/game-test/Scenarios/FutureFamily/Review.ps1';ignored=$true},
        @{path='tools/game-test/Scenarios/FutureFamily/Review.py';ignored=$true},
        @{path='tools/game-test/Scenarios/FutureFamily/Future.expectations.json';ignored=$true},
        @{path='tools/game-test/Scenarios/FutureFamily/tests/fixtures/example.json';ignored=$true},
        @{path='tools/game-test/Cases/FutureCase/save.sav';ignored=$true},
        @{path='tools/game-test/Cases/FutureCase/Settings.xml';ignored=$true},
        @{path='tools/game-test/artifacts/Example/Result.json';ignored=$true},
        @{path='tools/asset-authoring/workspace/Example/source.py';ignored=$true}
    )
    $checks=@(foreach($fixture in $fixtures) {
        & git -C $Directory -c core.excludesFile= check-ignore --quiet --no-index -- $fixture.path
        $code=$LASTEXITCODE
        if($code -notin @(0,1)){throw "Git could not classify the fixture path: $($fixture.path)"}
        [ordered]@{path=$fixture.path;expectedIgnored=$fixture.ignored;actualIgnored=($code -eq 0);status=$(if(($code -eq 0) -eq $fixture.ignored){'PASS'}else{'FAIL'})}
    })
    [ordered]@{status=$(if(@($checks | Where-Object status -ne 'PASS').Count){'FAIL'}else{'PASS'});scope='Actual Git ignore matching in an isolated repository using archived policies; reusable environment logic, individual game scenarios and private inputs.';cases=$checks}
}

function Invoke-PersistentSyncContracts([object]$SyncAst,[object]$DesktopAst,[string]$Directory) {
    [void][IO.Directory]::CreateDirectory($Directory)
    $cases=[Collections.Generic.List[object]]::new()
    function Check-Sync([string]$Name,[bool]$Passed){$cases.Add([ordered]@{name=$Name;status=$(if($Passed){'PASS'}else{'FAIL'})})}
    $definitions=@(
        Read-ContractFunction $DesktopAst 'Assert-VerificationGameStopped'
        Read-ContractFunction $SyncAst 'Assert-SyncOrLaunchAvailable'
        Read-ContractFunction $SyncAst 'Get-FileFingerprint'
        Read-ContractFunction $SyncAst 'Get-PersistentSettingsSnapshot'
        Read-ContractFunction $SyncAst 'Assert-PersistentSettingsPreserved'
        Read-ContractFunction $SyncAst 'Invoke-RobocopyOverlay'
    ) -join [Environment]::NewLine
    . ([scriptblock]::Create($definitions))
    $state=[pscustomobject]@{querySucceeded=$true;matches=@()}
    function Get-VerificationProcessState {param($Name) $state}
    $game=Join-Path $Directory 'Game';[void][IO.Directory]::CreateDirectory((Join-Path $game 'Mods'))
    $marker=Join-Path $game 'Mods/.UBGameTestTransaction.json'
    foreach($mode in @('stopped','running','unknown-process-state','unrestored-transaction')) {
        $state=[pscustomobject]@{querySucceeded=($mode -ne 'unknown-process-state');matches=@(if($mode -eq 'running'){[pscustomobject]@{pid=12345}})}
        if($mode -eq 'unrestored-transaction'){[IO.File]::WriteAllText($marker,'{}')}
        $rejected=$false
        try{Assert-SyncOrLaunchAvailable $game}catch{$rejected=$true}
        Check-Sync ($mode+' at standalone sync/launch boundary') ($rejected -eq ($mode -ne 'stopped'))
        if(Test-Path -LiteralPath $marker){Remove-Item -LiteralPath $marker}
    }
    $syncBlock=@($SyncAst.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.IfStatementAst] -and $_.Clauses[0].Item1.Extent.Text -eq '$Sync' -and $_.Extent.Text.Contains('Invoke-RobocopyOverlay')})
    Check-Sync 'the actual sync branch checks availability before any write' ($syncBlock.Count -eq 1 -and $syncBlock[0].Clauses[0].Item2.Statements[0].Extent.Text -ceq 'Assert-SyncOrLaunchAvailable $gameDirectory')
    $launchStatements=@($SyncAst.EndBlock.Statements)
    $launchIndex=-1
    for($i=0;$i -lt $launchStatements.Count;$i++){if($launchStatements[$i].Extent.Text -like '$process = Start-Process*'){$launchIndex=$i}}
    $preceding=@(if($launchIndex -gt 0){$launchStatements[0..($launchIndex-1)] | Where-Object {$_.Extent.Text -ceq 'Assert-SyncOrLaunchAvailable $gameDirectory'}})
    Check-Sync 'the actual launch path checks availability after synchronization' ($launchIndex -ge 0 -and $preceding.Count -eq 1)

    $source=Join-Path $Directory 'Source';$target=Join-Path $Directory 'Target'
    foreach($path in @($source,$target)){[void][IO.Directory]::CreateDirectory((Join-Path $path 'Settings/nested'))}
    foreach($overlap in @($source,(Join-Path $source 'child'),$Directory)) {
        $rejected=$false;try{Invoke-RobocopyOverlay -Source $source -Destination $overlap -PreserveSettings}catch{$rejected=$true}
        Check-Sync ('overlapping copy destination is rejected: '+[IO.Path]::GetFileName($overlap)) $rejected
    }
    [IO.File]::WriteAllText((Join-Path $source 'payload.txt'),'new payload')
    [IO.File]::WriteAllText((Join-Path $target 'payload.txt'),'old payload')
    [IO.File]::WriteAllText((Join-Path $source 'Settings.xml'),'default settings')
    [IO.File]::WriteAllText((Join-Path $source 'Settings/nested/existing.xml'),'default profile')
    [IO.File]::WriteAllText((Join-Path $source 'Settings/nested/missing.xml'),'new profile')
    [IO.File]::WriteAllText((Join-Path $target 'Settings.xml'),'user settings')
    $profile=Join-Path $target 'Settings/nested/existing.xml'
    [IO.File]::WriteAllText($profile,'user profile')
    $fixedTime=[datetime]::SpecifyKind([datetime]'2001-02-03T04:05:06',[DateTimeKind]::Utc)
    [IO.File]::SetLastWriteTimeUtc($profile,$fixedTime)
    $profileBefore=Get-Item -LiteralPath $profile
    $expectedAttributes=[int]$profileBefore.Attributes
    $snapshot=@(Get-PersistentSettingsSnapshot $target)
    Invoke-RobocopyOverlay -Source $source -Destination $target -PreserveSettings
    $preserved=$true;try{Assert-PersistentSettingsPreserved $snapshot}catch{$preserved=$false}
    Check-Sync 'payload is updated while existing settings bytes are preserved' ($preserved -and [IO.File]::ReadAllText((Join-Path $target 'payload.txt')) -ceq 'new payload' -and [IO.File]::ReadAllText((Join-Path $target 'Settings.xml')) -ceq 'user settings' -and [IO.File]::ReadAllText($profile) -ceq 'user profile')
    $profileAfter=Get-Item -LiteralPath $profile
    Check-Sync 'nested settings retain their original timestamp and attributes' ($profileAfter.LastWriteTimeUtc -eq $fixedTime -and [int]$profileAfter.Attributes -eq $expectedAttributes)
    Check-Sync 'missing profile defaults are added' ([IO.File]::ReadAllText((Join-Path $target 'Settings/nested/missing.xml')) -ceq 'new profile')
    [IO.File]::WriteAllText($profile,'corrupt profile')
    $rejected=$false;try{Assert-PersistentSettingsPreserved $snapshot}catch{$rejected=$true}
    Check-Sync 'a changed nested settings file is independently rejected' $rejected
    [IO.File]::WriteAllText($profile,'user profile');[IO.File]::SetLastWriteTimeUtc($profile,$fixedTime.AddSeconds(1))
    $rejected=$false;try{Assert-PersistentSettingsPreserved $snapshot}catch{$rejected=$true}
    Check-Sync 'a changed settings timestamp is rejected even with identical bytes' $rejected
    [IO.File]::SetLastWriteTimeUtc($profile,$fixedTime);Remove-Item -LiteralPath $profile
    $rejected=$false;try{Assert-PersistentSettingsPreserved $snapshot}catch{$rejected=$true}
    Check-Sync 'a deleted nested settings file is rejected' $rejected
    [ordered]@{status=$(if(@($cases | Where-Object status -ne 'PASS').Count){'FAIL'}else{'PASS'});scope='Actual archived standalone sync guard and overlay functions, synthetic process states, a real owned recovery marker and bounded Robocopy/settings fixtures; no installed game or user settings access.';cases=@($cases.ToArray())}
}

function Invoke-NativeEvidencePathContracts([string]$Helper,[object]$CommonAst,[object]$RuntimeAst,[string]$Directory) {
    $tokens=$null; $parseErrors=$null
    $helperAst=[Management.Automation.Language.Parser]::ParseFile($Helper,[ref]$tokens,[ref]$parseErrors)
    if ($parseErrors) { throw ($parseErrors | Out-String) }
    $functions=@($helperAst.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-VerificationEvidencePath'},$true))
    if ($functions.Count -ne 1) { throw 'Native evidence path planner must have one source owner.' }
    . ([scriptblock]::Create($functions[0].Extent.Text))
    $checks=[Collections.Generic.List[object]]::new()
    function Check-Path([string]$Name,[bool]$Passed) { $checks.Add([ordered]@{name=$Name;status=$(if($Passed){'PASS'}else{'FAIL'})}) }
    $allowed=[IO.Path]::GetFullPath($script:GameTestArtifactRoot).TrimEnd('\','/')
    $stamp='20260102-030405-006'; $token='1234567890abcdef'
    $short=Get-VerificationEvidencePath -Root $allowed -Name 'Build' -ArtifactRoot $allowed -Layout GameTest -Stamp $stamp -UniqueToken $token
    Check-Path 'a fitting existing outer name is preserved' ($short.directory -ceq (Join-Path $allowed ($stamp+'-Build-'+$token.Substring(0,8))) -and -not $short.shortened)
    $plainRuntime=Get-VerificationEvidencePath -Root $allowed -Name 'Probe' -ArtifactRoot $allowed -Layout Runtime -Stamp $stamp -UniqueToken $token
    Check-Path 'a fitting existing runtime name is preserved' ($plainRuntime.directory -ceq (Join-Path $allowed ($stamp+'-Probe')) -and -not $plainRuntime.shortened)
    $label='presentation-consistency-authored'
    $outer=Get-VerificationEvidencePath -Root (Join-Path $allowed 'presentation-refactor-20261026') -Name $label -ArtifactRoot $allowed -Layout GameTest -Stamp $stamp -UniqueToken $token
    $runtimeRoot=Join-Path $outer.directory 'Runtime'
    $inner=Get-VerificationEvidencePath -Root $runtimeRoot -Name $label -ArtifactRoot $allowed -Layout Runtime -Stamp $stamp -UniqueToken $token
    $nativeDirectory=Join-Path $inner.directory $inner.nativeIsolationRelativePath
    $nativeFile=Join-Path $nativeDirectory (('c'*60)+'.chr')
    Check-Path 'repeated long labels are shortened without moving the requested root' ($outer.shortened -and $inner.shortened -and (Split-Path $outer.directory -Parent) -ceq $outer.requestedEvidenceRoot -and (Split-Path $inner.directory -Parent) -ceq $runtimeRoot)
    Check-Path 'nested run and native character paths fit legacy limits' ($inner.directory.Length -le $inner.maximumRuntimeDirectoryLength -and $nativeDirectory.Length -lt 248 -and $nativeFile.Length -lt 260)
    Check-Path 'full logical case and probe labels remain in metadata' ($outer.requestedName -ceq $label -and $inner.requestedName -ceq $label)
    $second=Get-VerificationEvidencePath -Root $runtimeRoot -Name $label -ArtifactRoot $allowed -Layout Runtime -Stamp $stamp -UniqueToken 'fedcba0987654321'
    Check-Path 'shortened names remain unique in the same timestamp and root' ($inner.directory -cne $second.directory)
    $minimumLength=('r-'+$token).Length
    foreach ($layout in @('GameTest','Runtime')) {
        $limit=if($layout -eq 'GameTest'){$outer.maximumThisDirectoryLength}else{$inner.maximumThisDirectoryLength}
        $rootLength=$limit-1-$minimumLength
        $boundaryRoot=Join-Path $allowed ('x'*($rootLength-$allowed.Length-1))
        $boundary=Get-VerificationEvidencePath -Root $boundaryRoot -Name ('case'*30) -ArtifactRoot $allowed -Layout $layout -Stamp $stamp -UniqueToken $token
        Check-Path ($layout+' exact minimum-name boundary accepted') ($boundary.directory.Length -eq $limit -and (Split-Path $boundary.directory -Leaf) -ceq ('r-'+$token))
        $rejected=$false; $message=''; $tooDeep=$boundaryRoot+'x'
        try { [void](Get-VerificationEvidencePath -Root $tooDeep -Name $label -ArtifactRoot $allowed -Layout $layout -Stamp $stamp -UniqueToken $token) } catch { $message=$_.Exception.Message; $rejected=$message -like 'shorter EvidenceRoot required:*' }
        Check-Path ($layout+' one character beyond minimum budget rejected before directory creation') ($rejected -and $message.Contains([string]$tooDeep.Length) -and -not (Test-Path -LiteralPath $tooDeep))
    }
    $invalid=$false
    try { [void](Get-VerificationEvidencePath -Root $allowed -Name '../escape' -ArtifactRoot $allowed -Layout Runtime) } catch { $invalid=$true }
    Check-Path 'path traversal cannot be supplied as a run label' $invalid
    $outside=$false
    try { [void](Get-VerificationEvidencePath -Root (Split-Path $allowed -Parent) -Name 'Probe' -ArtifactRoot $allowed -Layout Runtime) } catch { $outside=$true }
    Check-Path 'an outside root is rejected without relocation' $outside
    $newDirectoryFunction=@($CommonAst.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'New-GtDirectory'},$true))
    $outerCalls=@($newDirectoryFunction[0].FindAll({param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Get-VerificationEvidencePath'},$true))
    $runtimeCalls=@($RuntimeAst.FindAll({param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Get-VerificationEvidencePath'},$true))
    Check-Path 'both saved production entry points use the shared planner' ($outerCalls.Count -eq 1 -and $outerCalls[0].Extent.Text.Contains('GameTest') -and $runtimeCalls.Count -eq 1 -and $runtimeCalls[0].Extent.Text.Contains('Runtime'))
    # Real bounded filesystem operations use only a new unique owned directory.
    $ownedRoot=Join-Path $allowed ('path-contract-'+[guid]::NewGuid().ToString('N').Substring(0,16))
    $targetRootLength=$inner.maximumRuntimeDirectoryLength-1-$minimumLength
    $ownedRoot+=('x'*($targetRootLength-$ownedRoot.Length))
    $existed=Test-Path -LiteralPath $ownedRoot
    if ($existed) { throw 'The unique path contract directory already exists.' }
    try {
        $ioPlan=Get-VerificationEvidencePath -Root $ownedRoot -Name $label -ArtifactRoot $allowed -Layout Runtime -Stamp $stamp -UniqueToken $token
        $ioDirectory=Join-Path $ioPlan.directory $ioPlan.nativeIsolationRelativePath
        $ioFile=Join-Path $ioDirectory (('c'*60)+'.chr')
        [void][IO.Directory]::CreateDirectory($ioDirectory)
        [IO.File]::WriteAllText($ioFile,'owned native path boundary')
        Check-Path 'a 259-character native-layout file can be written and read' ($ioFile.Length -eq 259 -and [IO.File]::ReadAllText($ioFile) -ceq 'owned native path boundary')
    } finally {
        $resolved=[IO.Path]::GetFullPath($ownedRoot)
        if (-not $resolved.StartsWith($allowed+'\',[StringComparison]::OrdinalIgnoreCase) -or $resolved -eq $allowed) { throw 'Unsafe owned path-contract cleanup target.' }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
    [ordered]@{status=$(if(@($checks | Where-Object status -ne 'PASS').Count){'FAIL'}else{'PASS'});scope='Shared source planner and both saved caller ASTs; legacy path boundary, unchanged root, preserved labels and real bounded owned-file IO. No game/settings/deployment changes.';cases=@($checks.ToArray())}
}

function Invoke-PresentationReviewContracts([string]$Review,[object]$InvokeAst,[string]$Directory) {
    [void][IO.Directory]::CreateDirectory($Directory)
    $inputs=Join-Path $Directory 'Inputs';$outputs=Join-Path $Directory 'Outputs'
    $cases=[Collections.Generic.List[object]]::new()
    function Check-Review([string]$Name,[bool]$Passed) {
        $cases.Add([ordered]@{name=$Name;status=$(if($Passed){'PASS'}else{'FAIL'})})
    }
    $png=[Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jZQAAAABJRU5ErkJggg==')
    $states=@('PASS','REVIEW','FAIL','NOT_RECORDED')
    $directories=@(foreach($state in $states) {
        $folder=Join-Path $inputs $state;[void][IO.Directory]::CreateDirectory($folder)
        [IO.File]::WriteAllBytes((Join-Path $folder 'present.png'),$png)
        [IO.File]::WriteAllText((Join-Path $folder 'raw.txt'),'unchanged raw fixture data')
        Write-GtJson (Join-Path $folder 'PresentationEvidence-Sample.json') ([ordered]@{
            schemaVersion=1;title=('Independent manifest '+$state)
            frames=@([ordered]@{id='present';path='present.png';label='recorded';group='sample';phase='before'})
            checks=@([ordered]@{id='owner-check';title='Declared check';status=$state;expected='adapter expectation';actual='adapter observation';evidence=@('present')})
            sources=@([ordered]@{path='raw.txt';sha256=(Get-GtHash (Join-Path $folder 'raw.txt'));role='raw fixture'})
        })
        Write-GtJson (Join-Path $folder 'Result.json') ([ordered]@{status='success';settingsRestored=$true})
        $folder
    })
    $before=@{};foreach($file in Get-ChildItem -LiteralPath $inputs -Recurse -File){$before[$file.FullName]=Get-GtHash $file.FullName}
    foreach($index in 0..($states.Count-1)) {
        $state=$states[$index]
        $reviewResult=& $Review -EvidencePath $directories[$index] -OutputDirectory (Join-Path $outputs $state)
        $summary=Read-GtObject $reviewResult.summary
        Check-Review ($state+' status is preserved') ($reviewResult.status -ceq $state -and $summary.documents[0].checks[0].status -ceq $state)
        Check-Review ($state+' source hash is presented') ($summary.documents[0].sources[0].hashMatches -eq $true)
        Check-Review ($state+' legacy frame records observation without claiming hash verification') ($summary.documents[0].frames[0].exists -and $summary.documents[0].frames[0].expectedSha256 -ceq '' -and $null -eq $summary.documents[0].frames[0].hashMatches)
    }
    $combined=& $Review -EvidencePath $directories -OutputDirectory (Join-Path $outputs 'Combined')
    $summary=Read-GtObject $combined.summary
    Check-Review 'a failed check propagates across independent manifests' ($combined.status -ceq 'FAIL' -and @($summary.documents).Count -eq 4 -and $combined.checks -eq 4)
    $firstJson=Get-GtHash $combined.summary;$firstHtml=Get-GtHash $combined.html
    $repeated=& $Review -EvidencePath $directories -OutputDirectory (Join-Path $outputs 'Combined')
    Check-Review 'same manifests produce identical review artifacts' ((Get-GtHash $repeated.summary) -ceq $firstJson -and (Get-GtHash $repeated.html) -ceq $firstHtml)
    # Mutate only copied fixture inputs. A reader must independently reject
    # missing/corrupt evidence even when its producer declared PASS.
    $negativeDirectories=@{}
    foreach($variant in @('missing-required-frame','missing-unreferenced-frame','missing-source','changed-source','unknown-frame','duplicate-frame','invalid-status','matching-frame-hash','changed-frame','missing-frame-with-hash','review-with-missing-frame','review-with-missing-source')) {
        $folder=Join-Path $inputs $variant
        [void][IO.Directory]::CreateDirectory($folder)
        [IO.File]::WriteAllBytes((Join-Path $folder 'present.png'),$png)
        [IO.File]::WriteAllText((Join-Path $folder 'raw.txt'),'unchanged raw fixture data')
        $manifest=Read-GtObject (Join-Path $directories[0] 'PresentationEvidence-Sample.json')
        if($variant -in @('matching-frame-hash','changed-frame','missing-frame-with-hash')) {
            $manifest.frames[0] | Add-Member -NotePropertyName sha256 -NotePropertyValue (Get-GtHash (Join-Path $folder 'present.png'))
        }
        switch($variant) {
            'missing-required-frame' {$manifest.frames[0].path='missing.png'}
            'missing-unreferenced-frame' {$manifest.frames[0].path='missing.png';$manifest.checks[0].evidence=@()}
            'missing-source' {$manifest.sources[0].path='missing-source.txt'}
            'changed-source' {[IO.File]::WriteAllText((Join-Path $folder 'raw.txt'),'different bytes')}
            'unknown-frame' {$manifest.checks[0].evidence=@('unknown')}
            'duplicate-frame' {$manifest.frames+=@($manifest.frames[0])}
            'invalid-status' {$manifest.checks[0].status='success'}
            'changed-frame' {[IO.File]::WriteAllBytes((Join-Path $folder 'present.png'),[byte[]]@(1,2,3,4))}
            'missing-frame-with-hash' {$manifest.frames[0].path='missing.png'}
            'review-with-missing-frame' {$manifest.frames[0].path='missing.png';$manifest.checks[0].status='REVIEW'}
            'review-with-missing-source' {$manifest.sources[0].path='missing-source.txt';$manifest.checks[0].status='REVIEW'}
        }
        Write-GtJson (Join-Path $folder 'PresentationEvidence-Sample.json') $manifest
        $rejected=$false;$reviewResult=$null
        try {$reviewResult=& $Review -EvidencePath $folder -OutputDirectory (Join-Path $outputs $variant)}catch{$rejected=$true}
        if($variant -in @('unknown-frame','duplicate-frame','invalid-status')) {
            Check-Review ($variant+' is rejected before reporting success') $rejected
        } else {
            $expectedStatus=if($variant -in @('changed-source','changed-frame')){'FAIL'}elseif($variant -eq 'matching-frame-hash'){'PASS'}else{'NOT_RECORDED'}
            Check-Review ($variant+' has an independent expected status') (-not $rejected -and $reviewResult.status -ceq $expectedStatus)
            if($variant -in @('matching-frame-hash','changed-frame','missing-frame-with-hash') -and -not $rejected) {
                $frame=(Read-GtObject $reviewResult.summary).documents[0].frames[0]
                $hashState=if($variant -eq 'matching-frame-hash'){$frame.hashMatches -eq $true}elseif($variant -eq 'changed-frame'){$frame.hashMatches -eq $false}else{$null -eq $frame.hashMatches}
                Check-Review ($variant+' preserves expected hash and exact verification state') ($frame.expectedSha256 -ceq $manifest.frames[0].sha256 -and $hashState)
                Check-Review ($variant+' presents the recorded frame hash in HTML') ([IO.File]::ReadAllText($reviewResult.html).Contains($manifest.frames[0].sha256) -and [IO.File]::ReadAllText($reviewResult.html).Contains('記録SHA256'))
            }
            if($variant -eq 'missing-required-frame' -and -not $rejected) {
                $incomplete=Read-GtObject $reviewResult.summary
                Check-Review 'missing frame retains producer declaration for audit' ($incomplete.documents[0].checks[0].declaredStatus -ceq 'PASS' -and $incomplete.documents[0].checks[0].status -ceq 'NOT_RECORDED')
                Check-Review 'missing frame remains visible in the review' ([IO.File]::ReadAllText($reviewResult.html).Contains('missing.png') -and [IO.File]::ReadAllText($reviewResult.html).Contains('画像未取得'))
            }
        }
        $negativeDirectories[$variant]=$folder
    }
    foreach($kind in @('missing-required-frame','missing-source','changed-frame')) {
        $mixed=& $Review -EvidencePath @($directories[1],$negativeDirectories[$kind]) -OutputDirectory (Join-Path $outputs ('Mixed-'+$kind))
        $expectedStatus=if($kind -eq 'changed-frame'){'FAIL'}else{'NOT_RECORDED'}
        Check-Review ($kind+' is not hidden by REVIEW in another document') ($mixed.status -ceq $expectedStatus)
    }
    $hooks=@($InvokeAst.FindAll({param($node)
        $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Extent.Text.Contains("`$result['nativeVerificationStatus']") -and
        $node.Extent.Text.Contains('Review-PresentationEvidence.ps1')
    },$true))
    if($hooks.Count -ne 1){throw 'Presentation review integration hook is not unique.'}
    # Only the reader path is supplied by the fixture. The actual hook logic is retained.
    $hook=[scriptblock]::Create($hooks[0].Extent.Text.Replace("Join-Path `$PSScriptRoot 'Review-PresentationEvidence.ps1'",'$Review'))
    foreach($state in $states) {
        $folder=$directories[[Array]::IndexOf($states,$state)]
        $run=Join-Path $Directory ('Hook-'+$state);[void][IO.Directory]::CreateDirectory($run)
        $ValidateOnly=$false
        $result=[ordered]@{status='PASS';reason='native fixture succeeded';runtimeResult=(Join-Path $folder 'Result.json');restored=$true;toolSources=@([ordered]@{path=$Review;sha256=(Get-GtHash $Review)})}
        & $hook | Out-Null
        $expected=if($state -eq 'FAIL'){'FAIL'}elseif($state -eq 'NOT_RECORDED'){'BLOCKED'}else{'PASS'}
        Check-Review ($state+' hook preserves native status and propagates failure or missing evidence') ($result.status -ceq $expected -and $result.nativeVerificationStatus -ceq 'PASS' -and $result.presentationReview.status -ceq $state -and $result.restored)
    }
    foreach($variant in @('review-with-missing-frame','review-with-missing-source','changed-frame')) {
        $folder=$negativeDirectories[$variant]
        Write-GtJson (Join-Path $folder 'Result.json') ([ordered]@{status='success';settingsRestored=$true})
        $run=Join-Path $Directory ('Hook-'+$variant);[void][IO.Directory]::CreateDirectory($run)
        $ValidateOnly=$false
        $result=[ordered]@{status='PASS';reason='native fixture succeeded';runtimeResult=(Join-Path $folder 'Result.json');restored=$true;toolSources=@([ordered]@{path=$Review;sha256=(Get-GtHash $Review)})}
        & $hook | Out-Null
        $expected=if($variant -eq 'changed-frame'){'FAIL'}else{'BLOCKED'}
        Check-Review ($variant+' reaches the overall result without hiding behind REVIEW') ($result.status -ceq $expected -and $result.nativeVerificationStatus -ceq 'PASS')
    }
    Check-Review 'source bytes remain unchanged' (@($before.Keys | Where-Object {(Get-GtHash $_) -cne $before[$_]}).Count -eq 0)
    $record=[ordered]@{status=$(if(@($cases | Where-Object status -ne 'PASS').Count){'FAIL'}else{'PASS'});scope='Actual archived generic reader and integration hook with managed manifest/filesystem fixtures; no game or settings access.';cases=@($cases.ToArray())}
    Write-GtJson (Join-Path $Directory 'Result.json') $record
    return $record
}

function Invoke-PresentationWriterContracts([string]$Source,[string]$Review,[string]$Directory) {
    [void][IO.Directory]::CreateDirectory($Directory)
    $text=[IO.File]::ReadAllText($Source)
    $start=$text.IndexOf('[Serializable] public sealed class ReviewDocument',[StringComparison]::Ordinal)
    $end=$text.IndexOf('public sealed class Receipt',[StringComparison]::Ordinal)
    $end=$text.LastIndexOf('[Serializable]',$end,[StringComparison]::Ordinal)
    $importsEnd=$text.IndexOf('using UnityEngine;',[StringComparison]::Ordinal)
    if($start -lt 0 -or $end -le $start -or $importsEnd -lt 0){throw 'The presentation serializer source boundaries were not found.'}
    # Compile the exact serializer members without Unity rendering members.
    $excerpt=$text.Substring(0,$importsEnd)+'public static class PresentationCapture {'+$text.Substring($start,$end-$start)+'}' +
        'public static class RuntimeVerificationConfiguration { public static string EvidenceDirectory; }'
    $compiledSource=Join-Path $Directory 'PresentationWriter.cs'
    [IO.File]::WriteAllText($compiledSource,$excerpt,[Text.UTF8Encoding]::new($false))
    $assembly=Join-Path $Directory 'PresentationWriter.dll'
    $references=@('System.dll','System.Core.dll','System.Xml.dll','System.Runtime.Serialization.dll')
    if ($PSVersionTable.PSEdition -eq 'Core') {
        $references=@([IO.Directory]::EnumerateFiles((Join-Path $PSHOME 'ref'),'*.dll'))
    }
    Add-Type -TypeDefinition $excerpt -ReferencedAssemblies $references -OutputAssembly $assembly -PassThru | Out-Null
    $cases=[Collections.Generic.List[object]]::new()
    function Check-Writer([string]$Name,[bool]$Passed){$cases.Add([ordered]@{name=$Name;status=$(if($Passed){'PASS'}else{'FAIL'})})}
    [RuntimeVerificationConfiguration]::EvidenceDirectory=$Directory
    $png=[Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jZQAAAABJRU5ErkJggg==')
    $image=Join-Path $Directory 'image.png';[IO.File]::WriteAllBytes($image,$png)
    $expectedHash=Get-GtHash $image
    $document=New-Object 'PresentationCapture+ReviewDocument'
    $document.title='Writer contract'
    $frame=New-Object 'PresentationCapture+ReviewFrame';$frame.id='relative';$frame.path='image.png';$document.frames.Add($frame)
    $absolute=New-Object 'PresentationCapture+ReviewFrame';$absolute.id='absolute';$absolute.path=$image;$document.frames.Add($absolute)
    $supplied=New-Object 'PresentationCapture+ReviewFrame';$supplied.id='supplied';$supplied.path='image.png';$supplied.sha256=('0'*64);$document.frames.Add($supplied)
    $missing=New-Object 'PresentationCapture+ReviewFrame';$missing.id='missing';$missing.path='not-captured.png';$document.frames.Add($missing)
    [PresentationCapture]::WriteReviewEvidence('contract',$document)
    $serialized=Read-GtObject (Join-Path $Directory 'PresentationEvidence-contract.json')
    Check-Writer 'relative and absolute existing frames store their actual SHA256' ($serialized.frames[0].sha256 -ceq $expectedHash -and $serialized.frames[1].sha256 -ceq $expectedHash)
    Check-Writer 'a caller-supplied expected hash is not overwritten' ($serialized.frames[2].sha256 -ceq ('0'*64))
    Check-Writer 'a missing frame does not acquire a fabricated hash' ([string]::IsNullOrEmpty($serialized.frames[3].sha256))
    $reviewed=& $Review -EvidencePath (Join-Path $Directory 'PresentationEvidence-contract.json') -OutputDirectory (Join-Path (Split-Path $Directory -Parent) 'PresentationWriterReview')
    Check-Writer 'reader rejects a writer manifest with a supplied mismatching frame hash' ($reviewed.status -ceq 'FAIL')
    $document.frames.Remove($supplied)|Out-Null;$document.frames.Remove($missing)|Out-Null
    $check=New-Object 'PresentationCapture+ReviewCheck';$check.id='capture';$check.title='Capture';$check.status='PASS';$check.evidence=@('relative','absolute');$document.checks.Add($check)
    [PresentationCapture]::WriteReviewEvidence('complete',$document)
    $reviewed=& $Review -EvidencePath (Join-Path $Directory 'PresentationEvidence-complete.json') -OutputDirectory (Join-Path (Split-Path $Directory -Parent) 'PresentationWriterCompleteReview')
    Check-Writer 'complete writer output passes reader verification' ($reviewed.status -ceq 'PASS')
    [IO.File]::WriteAllBytes($image,[byte[]]@(1,2,3,4))
    [PresentationCapture]::WriteReviewEvidence('rewritten',$document)
    $rewritten=Read-GtObject (Join-Path $Directory 'PresentationEvidence-rewritten.json')
    Check-Writer 'rewriting a manifest retains the originally recorded hash' ($rewritten.frames[0].sha256 -ceq $expectedHash)
    $reviewed=& $Review -EvidencePath (Join-Path $Directory 'PresentationEvidence-rewritten.json') -OutputDirectory (Join-Path (Split-Path $Directory -Parent) 'PresentationWriterChangedReview')
    Check-Writer 'changed frame bytes are rejected after writer serialization' ($reviewed.status -ceq 'FAIL')
    [ordered]@{status=$(if(@($cases | Where-Object status -ne 'PASS').Count){'FAIL'}else{'PASS'});scope='Exact archived serializer types and method compiled without Unity rendering members; real files and the archived reader. No game launch.';assembly=$assembly;excerptSha256=(Get-GtHash $compiledSource);cases=@($cases.ToArray())}
}

function Invoke-ModLoadPlanContracts([object]$Ast,[string]$Directory) {
    [void][IO.Directory]::CreateDirectory($Directory)
    $probe=Join-Path $Directory 'Probe.ps1'
    $source=@'
param($report)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
'@ + [Environment]::NewLine + (Read-ContractFunction $Ast 'Get-VerificationModLoadPlan') + [Environment]::NewLine + @'
$checks=[Collections.Generic.List[object]]::new()
function Add-LoadPlanCheck([string]$Name,[bool]$Passed,[string]$Detail='') {
    $checks.Add([pscustomobject]@{name=$Name;status=$(if($Passed){'PASS'}else{'FAIL'});detail=$Detail})
}
function New-LoadPlanFixture {
    @(
        [pscustomobject][ordered]@{
            Id='SolastaUnfinishedBusiness';Version='1.5.97.127'
            Requirements=@('Library.Core-2.3.4-preview','Library-AddOn-1.2.3.4')
            LoadAfter=@('Optional.First','Optional.Second')
            EntryMethod='Example.Load';ManagerVersion='0.33.2'
            Unknown=[pscustomobject]@{label='preserve <>&';items=@('first',[pscustomobject]@{value='nested'})}
        },
        [pscustomobject]@{Id='UBRuntimeVerification';Version='1.0.0';Requirements=@();LoadAfter=@()},
        [pscustomobject]@{Id='Library.Core';Version='2.3.4';Requirements=@();LoadAfter=@()},
        [pscustomobject]@{Id='Library-AddOn';Version='1.2.3.4';Requirements=@();LoadAfter=@()},
        [pscustomobject]@{Id='Consumer';Version='1.0.0';Requirements=@('SolastaUnfinishedBusiness-1.5.97');LoadAfter=@()}
    )
}
function Test-LoadPlanResult([string]$Name,[object[]]$Infos,[string[]]$ExpectedRequirements,[string]$VerificationId='UBRuntimeVerification') {
    $before=ConvertTo-Json -InputObject $Infos -Depth 30 -Compress
    $expected=$Infos[0] | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    if ($expected.PSObject.Properties['Requirements']) { $expected.Requirements=$ExpectedRequirements }
    else { $expected | Add-Member -NotePropertyName Requirements -NotePropertyValue $ExpectedRequirements }
    $outputs=@(Get-VerificationModLoadPlan -ModInfos $Infos -VerificationId $VerificationId)
    Add-LoadPlanCheck ($Name+': one UB object, preserved fields and dependency order') ($outputs.Count -eq 1 -and
        ($outputs[0] | ConvertTo-Json -Depth 30 -Compress) -ceq ($expected | ConvertTo-Json -Depth 30 -Compress))
    Add-LoadPlanCheck ($Name+': input objects remain unchanged') ((ConvertTo-Json -InputObject $Infos -Depth 30 -Compress) -ceq $before)
    if ($outputs.Count -eq 1) {
        $outputs[0].Unknown.items[1].value='changed output only'
        $outputs[0].Requirements[0]='changed output only'
        Add-LoadPlanCheck ($Name+': output is an independent deep clone') ((ConvertTo-Json -InputObject $Infos -Depth 30 -Compress) -ceq $before)
    }
}
function Test-LoadPlanRejection([string]$Name,[object[]]$Infos) {
    $before=ConvertTo-Json -InputObject $Infos -Depth 30 -Compress
    $rejected=$false;$detail=''
    try { Get-VerificationModLoadPlan -ModInfos $Infos | Out-Null }
    catch { $rejected=$true;$detail=$_.Exception.Message }
    Add-LoadPlanCheck ($Name+': rejected') $rejected $detail
    Add-LoadPlanCheck ($Name+': rejected input remains unchanged') ((ConvertTo-Json -InputObject $Infos -Depth 30 -Compress) -ceq $before)
}
$infos=@(New-LoadPlanFixture)
Test-LoadPlanResult 'existing versioned requirements' $infos @('Library.Core-2.3.4-preview','Library-AddOn-1.2.3.4','UBRuntimeVerification')
$infos=@(New-LoadPlanFixture)
$before=ConvertTo-Json -InputObject $infos -Depth 30 -Compress
$planned=Get-VerificationModLoadPlan -ModInfos $infos
$reordered=@($infos[4],$infos[3],$infos[1],$infos[0],$infos[2])
$reorderedPlan=Get-VerificationModLoadPlan -ModInfos $reordered
Add-LoadPlanCheck 'input enumeration does not determine the verification-before-UB edge' (
    ($planned | ConvertTo-Json -Depth 30 -Compress) -ceq ($reorderedPlan | ConvertTo-Json -Depth 30 -Compress) -and
    @($planned.Requirements | Where-Object { $_ -ceq 'UBRuntimeVerification' }).Count -eq 1)
Add-LoadPlanCheck 'reordered planning preserves all inputs' ((ConvertTo-Json -InputObject $infos -Depth 30 -Compress) -ceq $before)
$infos=@(New-LoadPlanFixture);$infos[0].PSObject.Properties.Remove('Requirements')
Test-LoadPlanResult 'missing Requirements property' $infos @('UBRuntimeVerification')
$infos=@(New-LoadPlanFixture);$infos[0].Requirements+=@('UBRuntimeVerification-1.0.0')
Test-LoadPlanResult 'existing satisfied harness version remains exact and unique' $infos @('Library.Core-2.3.4-preview','Library-AddOn-1.2.3.4','UBRuntimeVerification-1.0.0')
$infos=@(New-LoadPlanFixture);$infos[1].Id='OwnedHarness'
Test-LoadPlanResult 'explicit verification Id' $infos @('Library.Core-2.3.4-preview','Library-AddOn-1.2.3.4','OwnedHarness') 'OwnedHarness'

foreach ($kind in @('missing UB','missing verification','duplicate UB Id','duplicate other Id','missing Id',
        'verification requires UB','verification LoadAfter UB','verification has another dependency',
        'unsatisfied existing verification version','versioned UB self dependency','other self LoadAfter',
        'requirements cycle','LoadAfter cycle','mixed dependency cycle','transitive UB cycle','literal version-shaped LoadAfter cycle')) {
    $infos=@(New-LoadPlanFixture)
    switch ($kind) {
        'missing UB' { $infos=@($infos | Where-Object Id -CNE 'SolastaUnfinishedBusiness') }
        'missing verification' { $infos=@($infos | Where-Object Id -CNE 'UBRuntimeVerification') }
        'duplicate UB Id' { $infos+=@($infos[0] | ConvertTo-Json -Depth 30 | ConvertFrom-Json) }
        'duplicate other Id' { $infos+=@($infos[2] | ConvertTo-Json -Depth 30 | ConvertFrom-Json) }
        'missing Id' { $infos+=@([pscustomobject]@{Id='';Version='1.0.0';Requirements=@();LoadAfter=@()}) }
        'verification requires UB' { $infos[1].Requirements=@('SolastaUnfinishedBusiness-1.5.97') }
        'verification LoadAfter UB' { $infos[1].LoadAfter=@('SolastaUnfinishedBusiness') }
        'verification has another dependency' { $infos[1].Requirements=@('Library.Core-2.3.4') }
        'unsatisfied existing verification version' { $infos[0].Requirements+=@('UBRuntimeVerification-2.0.0') }
        'versioned UB self dependency' { $infos[0].Requirements+=@('SolastaUnfinishedBusiness-1.5.97') }
        'other self LoadAfter' { $infos[2].LoadAfter=@('Library.Core') }
        'requirements cycle' { $infos[2].Requirements=@('Library-AddOn-1.2.3');$infos[3].Requirements=@('Library.Core-2.3.4') }
        'LoadAfter cycle' { $infos[2].LoadAfter=@('Library-AddOn');$infos[3].LoadAfter=@('Library.Core') }
        'mixed dependency cycle' { $infos[2].Requirements=@('Library-AddOn-1.2.3');$infos[3].LoadAfter=@('Library.Core') }
        'transitive UB cycle' { $infos[2].LoadAfter=@('Consumer') }
        'literal version-shaped LoadAfter cycle' {
            $infos[0].LoadAfter+=@('Literal-1.2.3')
            $infos+=@([pscustomobject]@{Id='Literal-1.2.3';Version='1.0.0';Requirements=@('SolastaUnfinishedBusiness');LoadAfter=@()})
        }
    }
    Test-LoadPlanRejection $kind $infos
}
$record=[ordered]@{
    status=$(if(@($checks | Where-Object status -ne 'PASS').Count){'FAIL'}else{'PASS'})
    checks=@($checks.ToArray())
}
$record | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $report -Encoding UTF8
'@
    [IO.File]::WriteAllText($probe,$source,[Text.UTF8Encoding]::new($true))
    $shells=@((Get-Process -Id $PID).MainModule.FileName,(Get-Command powershell.exe -ErrorAction Stop).Source | Select-Object -Unique)
    $trials=@(foreach ($shell in $shells) {
        $report=Join-Path $Directory ([guid]::NewGuid().ToString('N')+'.json')
        & $shell -NoProfile -ExecutionPolicy Bypass -File $probe -report $report
        if ($LASTEXITCODE -ne 0) { throw 'The verification mod load-plan fixture failed to execute.' }
        $record=Read-GtObject $report
        [ordered]@{shell=$shell;status=$record.status;checks=@($record.checks)}
    })
    return [ordered]@{
        status=$(if(@($trials | Where-Object status -ne 'PASS').Count){'FAIL'}else{'PASS'})
        scope='Actual load-plan function AST with synthetic mod metadata on both PowerShell editions; no installed files, game or Steam access.'
        cases=$trials
    }
}

function Invoke-NativeUserDataContracts([object]$Ast,[string]$Directory) {
    [void][IO.Directory]::CreateDirectory($Directory)
    $probe=Join-Path $Directory 'Probe.ps1'
    $definitions=@(foreach ($name in @('Get-FullPath','Test-IsWithin','New-VerificationNativeUserData','Get-VerificationNativeUserFiles')) {
        Read-ContractFunction $Ast $name
    }) -join [Environment]::NewLine
    $source=@'
param($fixture,$report)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
'@ + [Environment]::NewLine + $definitions + [Environment]::NewLine + @'
$checks=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[bool]$Passed) {
    $checks.Add([pscustomobject]@{name=$Name;status=$(if($Passed){'PASS'}else{'FAIL'})})
}
function Put([string]$Root,[string]$Relative,[string]$Text) {
    $path=Join-Path $Root $Relative
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $path))
    [IO.File]::WriteAllText($path,$Text,[Text.UTF8Encoding]::new($false))
}
function Snapshot([string]$Root) {
    ConvertTo-Json -InputObject @(Get-VerificationNativeUserFiles $Root) -Depth 8 -Compress
}
$native=Join-Path $fixture 'NativeOriginal';$prefs=Join-Path $fixture 'CasePreferences';$owned=Join-Path $fixture 'OwnedNativeData'
foreach ($entry in @(
    @('UserPreferences.xml','native-user'),@('PlatformPreferences.xml','native-platform'),
    @('Saves/original.sav','original-save'),@('Characters/original.chr','original-character'),
    @('UserContent/Locations/map.json','owned-map-input'),@('UserContent/Campaigns/campaign.json','owned-campaign-input'),
    @('TempSessionData/lore.json','owned-lore-input'),@('unrelated.txt','must-not-copy'),@('Marketing/news.txt','must-not-copy')
)) { Put $native $entry[0] $entry[1] }
Put $prefs 'UserPreferences.xml' 'case-user';Put $prefs 'PlatformPreferences.xml' 'case-platform'
$before=Snapshot $native
$copied=@(New-VerificationNativeUserData $native $owned $prefs 'contract-owner')
$expected=@('PlatformPreferences.xml','TempSessionData/lore.json','UserContent/Campaigns/campaign.json','UserContent/Locations/map.json','UserPreferences.xml')
$actual=@($copied | ForEach-Object { $_.relative.Replace('\','/') } | Sort-Object)
Check 'only selected read inputs are staged' (($actual -join '|') -ceq ($expected -join '|'))
Check 'owned marker identifies this run' ([IO.File]::ReadAllText((Join-Path $owned '.ub-runtime-userdata')) -ceq 'contract-owner')
Check 'case preferences override original native preferences' (
    [IO.File]::ReadAllText((Join-Path $owned 'UserPreferences.xml')) -ceq 'case-user' -and
    [IO.File]::ReadAllText((Join-Path $owned 'PlatformPreferences.xml')) -ceq 'case-platform')
Check 'custom content and campaign lore are available' (
    [IO.File]::ReadAllText((Join-Path $owned 'UserContent/Locations/map.json')) -ceq 'owned-map-input' -and
    [IO.File]::ReadAllText((Join-Path $owned 'UserContent/Campaigns/campaign.json')) -ceq 'owned-campaign-input' -and
    [IO.File]::ReadAllText((Join-Path $owned 'TempSessionData/lore.json')) -ceq 'owned-lore-input')
Check 'save, character and unrelated native files are not copied' (
    -not (Test-Path -LiteralPath (Join-Path $owned 'Saves')) -and
    -not (Test-Path -LiteralPath (Join-Path $owned 'Characters')) -and
    -not (Test-Path -LiteralPath (Join-Path $owned 'unrelated.txt')) -and
    -not (Test-Path -LiteralPath (Join-Path $owned 'Marketing')))
Check 'every staged input receipt matches source and destination hashes' (@($copied | Where-Object {
    $_.sha256 -cne (Get-FileHash -LiteralPath $_.source -Algorithm SHA256).Hash -or
    $_.sha256 -cne (Get-FileHash -LiteralPath (Join-Path $owned $_.relative) -Algorithm SHA256).Hash
}).Count -eq 0)
Check 'staging preserves original preference, save and character bytes, sizes and write times' ((Snapshot $native) -ceq $before)
$snapshot=@(Get-VerificationNativeUserFiles $native)
Check 'snapshot covers both preferences and all original save and character files' ($snapshot.Count -eq 4 -and
    @($snapshot | Where-Object { -not $_.exists -or !$_.sha256 -or !$_.size -or !$_.lastWriteUtcTicks }).Count -eq 0)
Put $owned 'UserPreferences.xml' 'verification-export';Put $owned 'Saves/new.sav' 'verification-save';Put $owned 'Characters/new.chr' 'verification-character'
Check 'owned preference exports, saves and characters do not alter original snapshot' ((Snapshot $native) -ceq $before)
$existingRejected=$false
try { New-VerificationNativeUserData $native $owned $prefs 'different-owner' | Out-Null } catch { $existingRejected=$true }
Check 'an existing destination is refused without overwriting its outputs' ($existingRejected -and
    [IO.File]::ReadAllText((Join-Path $owned '.ub-runtime-userdata')) -ceq 'contract-owner' -and
    [IO.File]::ReadAllText((Join-Path $owned 'UserPreferences.xml')) -ceq 'verification-export')
$prefPath=Join-Path $native 'UserPreferences.xml';$prefBefore=$snapshot | Where-Object path -EQ $prefPath
Put $native 'UserPreferences.xml' 'mutant-user'
[IO.File]::SetLastWriteTimeUtc($prefPath,[datetime]::new([long]$prefBefore.lastWriteUtcTicks,[DateTimeKind]::Utc))
$prefAfter=Get-VerificationNativeUserFiles $native | Where-Object path -EQ $prefPath
Check 'preference byte changes are detected even at equal size and write time' ($prefAfter.size -eq $prefBefore.size -and
    $prefAfter.lastWriteUtcTicks -eq $prefBefore.lastWriteUtcTicks -and $prefAfter.sha256 -cne $prefBefore.sha256)
$savePath=Join-Path $native 'Saves/original.sav';$saveBefore=$snapshot | Where-Object path -EQ $savePath
[IO.File]::AppendAllText($savePath,'changed')
$saveAfter=Get-VerificationNativeUserFiles $native | Where-Object path -EQ $savePath
Check 'original save changes are detected' ($saveAfter.size -gt $saveBefore.size -and $saveAfter.sha256 -cne $saveBefore.sha256)
$characterPath=Join-Path $native 'Characters/original.chr';$characterBefore=$snapshot | Where-Object path -EQ $characterPath
[IO.File]::SetLastWriteTimeUtc($characterPath,[datetime]::new([long]$characterBefore.lastWriteUtcTicks,[DateTimeKind]::Utc).AddSeconds(2))
$characterAfter=Get-VerificationNativeUserFiles $native | Where-Object path -EQ $characterPath
Check 'character write-time changes are detected without a content change' ($characterAfter.sha256 -ceq $characterBefore.sha256 -and
    $characterAfter.lastWriteUtcTicks -ne $characterBefore.lastWriteUtcTicks)
$record=[ordered]@{status=$(if(@($checks | Where-Object status -ne 'PASS').Count){'FAIL'}else{'PASS'});checks=@($checks.ToArray())}
$record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $report -Encoding UTF8
'@
    [IO.File]::WriteAllText($probe,$source,[Text.UTF8Encoding]::new($true))
    $shells=@((Get-Process -Id $PID).MainModule.FileName,(Get-Command powershell.exe -ErrorAction Stop).Source | Select-Object -Unique)
    $trials=@(foreach ($shell in $shells) {
        $fixture=Join-Path $Directory ([guid]::NewGuid().ToString('N'))
        $report=$fixture+'.json'
        & $shell -NoProfile -ExecutionPolicy Bypass -File $probe -fixture $fixture -report $report
        if ($LASTEXITCODE -ne 0) { throw 'The native user-data staging fixture failed to execute.' }
        $record=Read-GtObject $report
        [ordered]@{shell=$shell;status=$record.status;checks=@($record.checks)}
    })
    return [ordered]@{
        status=$(if(@($trials | Where-Object status -ne 'PASS').Count){'FAIL'}else{'PASS'})
        scope='Actual staging and snapshot function AST with owned dummy native files on both PowerShell editions; no installed game or user data access.'
        cases=$trials
    }
}

function Invoke-PackageManifestContracts([string]$Directory) {
    [void][IO.Directory]::CreateDirectory($Directory)
    foreach ($name in @('Bulette_Snow.png','Bulette_Young.png','Bulette.png')) {
        [IO.File]::WriteAllText((Join-Path $Directory $name), $name)
    }
    $manifest=@(Get-GtManifest $Directory)
    Assert-GtManifest $Directory $manifest
    Assert-GtManifest $Directory @($manifest | Sort-Object path -Descending)
    $checks=@('unchanged package','manifest order independence')
    foreach ($kind in @('duplicate path','wrong hash','wrong size','wrong case','missing file','unexpected file')) {
        $candidate=@($manifest | ConvertTo-Json | ConvertFrom-Json | ForEach-Object { $_ })
        switch ($kind) {
            'duplicate path' { $candidate[1].path=$candidate[0].path }
            'wrong hash' { $candidate[0].sha256='0' * 64 }
            'wrong size' { $candidate[0].size++ }
            'wrong case' { $candidate[0].path=$candidate[0].path.ToUpperInvariant() }
            'missing file' { $candidate=@($candidate | Select-Object -Skip 1) }
            'unexpected file' { $candidate+=@{path='unexpected.txt';size=0;sha256='0' * 64} }
        }
        $rejected=$false
        try { Assert-GtManifest $Directory $candidate } catch { $rejected=$true }
        if (-not $rejected) { throw "Package manifest accepted $kind." }
        $checks+=($kind + ' rejected')
    }
    return [ordered]@{status='PASS';scope='Actual manifest generation and comparison with native files and reordered or invalid manifests.';cases=$checks}
}

function Invoke-BuildDiagnosticContracts([object]$Ast,[string]$Directory) {
    $blocks=@($Ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.TryStatementAst] -and
        @($node.Body.Statements | Where-Object { $_.Extent.Text.TrimStart().StartsWith('& $wrapper *>') }).Count -eq 1
    },$true))
    if ($blocks.Count -ne 1) { throw 'The native package build invocation is not unique.' }
    [void][IO.Directory]::CreateDirectory($Directory)
    $probe=Join-Path $Directory 'Probe.ps1'
    $probeSource=@'
param($wrapper,$directory,$reportPath)
$ErrorActionPreference='Stop'
$previousErrorPreference=$ErrorActionPreference
'@ + [Environment]::NewLine + $blocks[0].Extent.Text + [Environment]::NewLine + @'
[ordered]@{exitCode=$buildExitCode;errorPreference=$ErrorActionPreference.ToString();log=[IO.File]::ReadAllText((Join-Path $directory 'Build.log'))} | ConvertTo-Json | Set-Content -LiteralPath $reportPath -Encoding UTF8
'@
    [IO.File]::WriteAllText($probe,$probeSource,[Text.UTF8Encoding]::new($false))
    $shells=@((Get-Process -Id $PID).MainModule.FileName,(Get-Command powershell.exe -ErrorAction Stop).Source | Select-Object -Unique)
    $checks=@()
    foreach ($shell in $shells) {
        foreach ($exitCode in @(0,7)) {
            $fixture=Join-Path $Directory ([guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($fixture)
            $wrapper=Join-Path $fixture 'Diagnostic.cmd'
            [IO.File]::WriteAllText($wrapper,"@echo off`r`necho example-warning 1>&2`r`nexit /b $exitCode`r`n",[Text.Encoding]::ASCII)
            $report=Join-Path $fixture 'Report.json'
            & $shell -NoProfile -ExecutionPolicy Bypass -File $probe -wrapper $wrapper -directory $fixture -reportPath $report
            if ($LASTEXITCODE -ne 0) { throw 'The native build diagnostic fixture failed to execute.' }
            $observed=Read-GtObject $report
            if ($observed.exitCode -ne $exitCode -or $observed.errorPreference -ne 'Stop' -or $observed.log -notmatch 'example-warning') {
                throw 'The native build lost its exit code, diagnostics, or caller error preference.'
            }
            $checks+=@{shell=$shell;exitCode=$exitCode;status='PASS'}
        }
    }
    return [ordered]@{status='PASS';scope='Actual build invocation AST with native stderr and successful or failing exit codes on the current shell and Windows PowerShell.';cases=$checks}
}

function Invoke-SourceIdentityContracts([object]$Ast,[string]$Directory) {
    [void][IO.Directory]::CreateDirectory($Directory)
    foreach ($relative in @('Bulette_Snow.cs','Bulette.cs','alpha.cs','SolastaUnfinishedBusiness/Assembly-CSharp.dll','lib/Assembly-CSharp_public.dll')) {
        $path=Join-Path $Directory $relative
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $path))
        [IO.File]::WriteAllText($path,$relative)
    }
    $probe=Join-Path $Directory 'Probe.ps1'
    $source=@'
param($fixture,$report)
$ErrorActionPreference='Stop'
$script:GameTestRepo=$fixture
function Get-GtHash($Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Invoke-GtGit([string[]]$Arguments) {
    switch ($Arguments[0]) {
        'rev-parse' { '0123456789abcdef0123456789abcdef01234567' }
        'branch' { $name='test/<>&'+[char]39+[char]0x65E5+[char]0x672C; if ($Arguments -contains '--all') { @('* '+$name,'  remotes/origin/HEAD -> test') } else { $name } }
        'diff' { if ($Arguments -contains '--name-only') { @('alpha.cs','Bulette_Snow.cs','Bulette.cs') } }
        'ls-files' { @('Bulette.cs','tools/ignored.cs') }
    }
}
'@ + [Environment]::NewLine + (Read-ContractFunction $Ast 'Get-GtSourceIdentity') + [Environment]::NewLine + @'
$before=Get-GtSourceIdentity
[IO.File]::AppendAllText((Join-Path $fixture 'alpha.cs'),'changed')
$after=Get-GtSourceIdentity
[IO.File]::WriteAllText((Join-Path $fixture 'alpha.cs'),'alpha.cs')
if ($before.digest -eq $after.digest) { throw 'A source change was not detected.' }
$before | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $report -Encoding UTF8
'@
    [IO.File]::WriteAllText($probe,$source,[Text.UTF8Encoding]::new($true))
    $shells=@((Get-Process -Id $PID).MainModule.FileName,(Get-Command powershell.exe -ErrorAction Stop).Source | Select-Object -Unique)
    $digests=@()
    foreach ($shell in $shells) {
        $report=Join-Path $Directory ([guid]::NewGuid().ToString('N')+'.json')
        & $shell -NoProfile -ExecutionPolicy Bypass -File $probe -fixture $Directory -report $report
        if ($LASTEXITCODE -ne 0) { throw 'The source identity fixture failed.' }
        $identity=Read-GtObject $report
        if (@($identity.state.overlayFiles).Count -ne 5 -or $identity.state.overlayFiles[0].path -cne 'Bulette.cs') {
            throw 'Source identity did not use ordinal paths, remove duplicates, or exclude tools.'
        }
        $digests+=@{shell=$shell;digest=$identity.digest;status='PASS'}
    }
    if (@($digests | ForEach-Object digest | Select-Object -Unique).Count -ne 1) { throw 'Source identity depends on the PowerShell edition.' }
    return [ordered]@{status='PASS';scope='Actual source identity AST with synthetic Git output, real source hashes, source mutation and both PowerShell editions.';cases=$digests}
}

function Invoke-StorageJournalContracts([hashtable]$Sources,[string]$OutputDirectory) {
    $savedJournalScope=@{}
    foreach($name in @('GameTestRepo','GameTestBuildRoot','GameTestArtifactRoot','GtValidatedDirectories')) {
        $savedJournalScope[$name]=Get-Variable -Name $name -Scope Script -ValueOnly
    }
    try {
    $source=$Sources['tools/game-test/Clear-GameTestArtifacts.ps1']
$sourceHash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$cases=[Collections.Generic.List[object]]::new()
foreach($mode in @('success','delete-failure')){
    $workspace=Join-Path $OutputDirectory $mode;$tool=Join-Path $workspace 'tools/game-test'
    foreach($relative in @('tools/game-test','tools/runtime-verification','tools/game-test/artifacts','tools/game-test/Cases','tools/game-test/Scenarios')){[void][IO.Directory]::CreateDirectory((Join-Path $workspace $relative))}
    foreach($relative in @('tools/game-test/GameTest.Common.ps1','tools/runtime-verification/WindowsDesktop.ps1','tools/runtime-verification/EvidencePaths.ps1')){Copy-Item -LiteralPath $Sources[$relative] -Destination (Join-Path $workspace $relative)}
    Copy-Item -LiteralPath $source -Destination (Join-Path $tool 'Clear-GameTestArtifacts.ps1')
    $common=Join-Path $tool 'GameTest.Common.ps1'
    $text=[IO.File]::ReadAllText($common).Replace('Local\SolastaUBGameTest',('Local\SolastaUBJournalDurability_'+[guid]::NewGuid().ToString('N')))
    $tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw 'Fixture common source did not parse.'}
    $writer=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]-and $n.Name -eq 'Write-GtJson'},$true))[0]
    $text=$text.Insert($writer.Body.Extent.EndOffset-1,@'

    if([IO.Path]::GetFileName($Path) -like 'Storage-Cleanup-*.json'){
        [IO.File]::AppendAllText((Join-Path $PSScriptRoot 'SummaryWrites.txt'),[IO.FileInfo]::new($Path).Length.ToString()+[Environment]::NewLine)
    }
'@)
    $text+=@'

function Remove-Item {
    [CmdletBinding()]param([string[]]$LiteralPath,[switch]$Recurse,[switch]$Force)
    foreach($target in $LiteralPath){
        if((Split-Path -Leaf $target) -ne 'owned'){throw 'The durability fixture intercepted an unexpected removal.'}
        $events=@(Get-Content -LiteralPath $journalPath | ForEach-Object {$_|ConvertFrom-Json})
        $plans=@($events|Where-Object {$_.event -eq 'action-planned' -and $_.action.path -eq $target})
        $diagnostics=@($events|Where-Object {$_.event -eq 'retire-diagnostic' -and $_.record.path -eq $target})
        if($plans.Count -ne 1 -or $diagnostics.Count -ne 1 -or $journalState.flushes -lt 2){throw 'Deletion preceded the flushed diagnostic or planned action.'}
        $original=Join-Path $target 'Result.json'
        if($diagnostics[0].record.identitySha256 -ne (Get-GtHash $original)){throw 'The journal did not preserve the original result hash.'}
        [IO.File]::AppendAllText((Join-Path $PSScriptRoot 'BeforeDelete.jsonl'),([ordered]@{path=$target;resultHash=Get-GtHash $original;flushedEvents=$events.Count;flushes=$journalState.flushes;diagnostic=$diagnostics[0].record.result}|ConvertTo-Json -Compress -Depth 10)+[Environment]::NewLine)
        if((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'FixtureMode.txt') -Raw) -eq 'delete-failure'){throw 'Synthetic delete boundary failure.'}
        Microsoft.PowerShell.Management\Remove-Item -LiteralPath $target -Recurse:$Recurse -Force:$Force
    }
}
'@
    [IO.File]::WriteAllText($common,$text,[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $tool 'FixtureMode.txt'),$mode)
    [IO.File]::AppendAllText((Join-Path $workspace 'tools/runtime-verification/WindowsDesktop.ps1'),@'

function Get-VerificationProcessState {param($Name) return [pscustomobject]@{querySucceeded=$true;matches=@()}}
'@)
    . $common
    $root=Join-Path $tool 'artifacts';$owned=Join-Path $root 'owned';[void][IO.Directory]::CreateDirectory($owned)
    $game=Join-Path $workspace 'DummyGame';[void][IO.Directory]::CreateDirectory((Join-Path $game 'Mods'))
    Write-GtJson (Join-Path $owned 'Directory.json') @{schemaVersion=1;layout='Runtime';directory=$owned}
    Write-GtJson (Join-Path $owned 'Result.json') @{status='failed';reason='synthetic original failure';runName='durability';evidenceDirectory=$owned;
        startedUtc='2020-01-01T00:00:00Z';completedUtc='2020-01-02T00:00:00Z';settingsRestored=$true;displaySettingsRestored=$true;probeRemoved=$true;originalNativeFilesUnchanged=$true;
        failed=1;diagnosticBulk=('x'*65536);processId=4318;processCleanup=@{completed=$true;expectedProcessId=4318;expectedStartTimeUtc='2020-01-01T00:00:00Z';expectedPath=(Join-Path $game 'Solasta.exe')}}
    $originalHash=Get-GtHash (Join-Path $owned 'Result.json');$caught=$null
    try { & (Join-Path $tool 'Clear-GameTestArtifacts.ps1') -ArtifactRoot $root -MaxAgeDays 1 -Apply | Out-Null } catch {$caught=$_.Exception.Message}
    $report=@(Get-ChildItem -LiteralPath $root -Filter 'Storage-Cleanup-*.json' -File)
    if($report.Count -ne 1){throw 'The final summary was missing or duplicated.'}
    $summary=Get-Content -LiteralPath $report[0].FullName -Raw|ConvertFrom-Json
    $events=@(Get-Content -LiteralPath (Join-Path $root $summary.journal.path)|ForEach-Object {$_|ConvertFrom-Json})
    $applied=@($summary.actions|Where-Object {$_.kind -eq 'superseded-run' -and $_.applied})
    $planned=@($events|Where-Object {$_.event -eq 'action-planned'})
    $completed=@($events|Where-Object {$_.event -eq 'action-complete'})
    $writes=@(Get-Content -LiteralPath (Join-Path $tool 'SummaryWrites.txt'))
    $before=@(Get-Content -LiteralPath (Join-Path $tool 'BeforeDelete.jsonl')|ForEach-Object {$_|ConvertFrom-Json})
    $checks=[ordered]@{
        originalFailureDiagnosticPreserved=($summary.retiredRuns[0].result.status -eq 'failed' -and $summary.retiredRuns[0].result.reason -eq 'synthetic original failure' -and $summary.retiredRuns[0].identitySha256 -eq $originalHash)
        omittedLargeDetailWithOriginalHash=(-not ($summary.retiredRuns[0].result.PSObject.Properties['diagnosticBulk']) -and $summary.retiredRuns[0].identitySha256 -eq $originalHash)
        plannedAndDiagnosticVisibleBeforeDelete=($before.Count -eq 1 -and $before[0].flushes -ge 2 -and $before[0].resultHash -eq $originalHash)
        summaryWrittenOnce=($writes.Count -eq 1 -and $summary.summaryWrites -eq 1)
        allAppliedActionsCompleted=($applied.Count -eq $completed.Count)
        failureStopsBeforeCompletion=$(if($mode -eq 'delete-failure'){$caught -eq 'Synthetic delete boundary failure.' -and (Test-Path -LiteralPath $owned) -and $completed.Count -eq 0 -and $applied.Count -eq 0}else{$null -eq $caught -and -not (Test-Path -LiteralPath $owned) -and $completed.Count -eq 1 -and $applied.Count -eq 1})
        journalCountsMatch=($events.Count -eq $summary.journal.events -and (Get-Item -LiteralPath (Join-Path $root $summary.journal.path)).Length -eq $summary.journal.bytes -and $planned.Count -eq 1)
    }
    foreach($entry in $checks.GetEnumerator()){$cases.Add([ordered]@{mode=$mode;name=$entry.Key;passed=[bool]$entry.Value})}
}
Microsoft.PowerShell.Management\Remove-Item -LiteralPath Function:\Remove-Item
$writerMeasurements=[Collections.Generic.List[object]]::new()
foreach($count in @(4,8)) {
    $workspace=Join-Path $OutputDirectory ('writer-'+$count);$tool=Join-Path $workspace 'tools/game-test'
    foreach($relative in @('tools/game-test','tools/runtime-verification','tools/game-test/artifacts','tools/game-test/Cases','tools/game-test/Scenarios')){[void][IO.Directory]::CreateDirectory((Join-Path $workspace $relative))}
    foreach($relative in @('tools/game-test/GameTest.Common.ps1','tools/game-test/Clear-GameTestArtifacts.ps1','tools/runtime-verification/WindowsDesktop.ps1','tools/runtime-verification/EvidencePaths.ps1')){[IO.File]::Copy($Sources[$relative],(Join-Path $workspace $relative))}
    $common=Join-Path $tool 'GameTest.Common.ps1'
    $text=[IO.File]::ReadAllText($common).Replace('Local\SolastaUBGameTest',('Local\SolastaUBStorageWriter_'+[guid]::NewGuid().ToString('N')))
    $tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw 'The writer fixture common source did not parse.'}
    $writer=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Write-GtJson'},$true))
    if($writer.Count -ne 1){throw 'The copied JSON writer is not unique.'}
    $text=$text.Insert($writer[0].Body.Extent.EndOffset-1,@'

    if([IO.Path]::GetFileName($Path) -like 'Storage-Cleanup-*.json') {
        [IO.File]::AppendAllText((Join-Path $PSScriptRoot 'SummaryWrites.txt'),([IO.FileInfo]$Path).Length.ToString()+[Environment]::NewLine)
    }
'@)
    [IO.File]::WriteAllText($common,$text,[Text.UTF8Encoding]::new($false))
    [IO.File]::AppendAllText((Join-Path $workspace 'tools/runtime-verification/WindowsDesktop.ps1'),@'

function Get-VerificationProcessState {param($Name)
    [IO.File]::AppendAllText((Join-Path $PSScriptRoot 'ProcessQueries.txt'),$Name+[Environment]::NewLine)
    return [pscustomobject]@{querySucceeded=$true;matches=@()}
}
'@)
    . $common
    $root=Join-Path $tool 'artifacts';$game=Join-Path $workspace 'DummyGame';[void][IO.Directory]::CreateDirectory((Join-Path $game 'Mods'))
    $expected=@{}
    for($index=0;$index -lt $count;$index++) {
        $owned=Join-Path $root ('owned-'+$index);[void][IO.Directory]::CreateDirectory($owned)
        Write-GtJson (Join-Path $owned 'Directory.json') @{schemaVersion=1;layout='Runtime';directory=$owned}
        Write-GtJson (Join-Path $owned 'Result.json') @{status='failed';reason='bounded writer fixture';runName=('unique-'+$index);evidenceDirectory=$owned;
            startedUtc='2020-01-01T00:00:00Z';completedUtc='2020-01-02T00:00:00Z';settingsRestored=$true;displaySettingsRestored=$true;probeRemoved=$true;originalNativeFilesUnchanged=$true;
            failed=1;diagnosticBulk=('x'*16384);processId=4318;processCleanup=@{completed=$true;expectedProcessId=4318;expectedStartTimeUtc='2020-01-01T00:00:00Z';expectedPath=(Join-Path $game 'Solasta.exe')}}
        $expected[$owned]=Get-GtHash (Join-Path $owned 'Result.json')
    }
    $timer=[Diagnostics.Stopwatch]::StartNew()
    & (Join-Path $tool 'Clear-GameTestArtifacts.ps1') -ArtifactRoot $root -MaxAgeDays 1 -Apply | Out-Null
    $timer.Stop()
    $report=@(Get-ChildItem -LiteralPath $root -Filter 'Storage-Cleanup-*.json' -File)
    if($report.Count -ne 1){throw 'The writer fixture final summary is not unique.'}
    $summary=Get-Content -LiteralPath $report[0].FullName -Raw|ConvertFrom-Json
    $journal=Join-Path $root $summary.journal.path
    $events=@(Get-Content -LiteralPath $journal|ForEach-Object{$_|ConvertFrom-Json})
    $writes=@(Get-Content -LiteralPath (Join-Path $tool 'SummaryWrites.txt'))
    $queries=@(Get-Content -LiteralPath (Join-Path $workspace 'tools/runtime-verification/ProcessQueries.txt'))
    $checks=[ordered]@{
        summarySerializedOnce=($writes.Count -eq 1 -and $summary.summaryWrites -eq 1)
        exactActionEventCounts=(@($events|Where-Object event -eq 'action-planned').Count -eq $count -and @($events|Where-Object event -eq 'action-complete').Count -eq $count)
        exactOriginalDiagnostics=($summary.retiredRuns.Count -eq $count -and @($summary.retiredRuns|Where-Object{$_.identitySha256 -ne $expected[$_.path] -or $_.result.status -ne 'failed' -or $_.result.reason -ne 'bounded writer fixture'}).Count -eq 0)
        unrelatedLargeDetailsOmitted=(@($summary.retiredRuns|Where-Object{$_.result.PSObject.Properties['diagnosticBulk']}).Count -eq 0)
        boundedWriterOutput=($report[0].Length+(Get-Item -LiteralPath $journal).Length -le 65536+$count*8192)
        ownedTargetsRemoved=(@($expected.Keys|Where-Object{Test-Path -LiteralPath $_}).Count -eq 0 -and @($summary.actions|Where-Object{$_.kind -eq 'superseded-run' -and $_.applied}).Count -eq $count)
        freshProcessBoundaryObserved=($queries.Count -ge $count)
    }
    foreach($entry in $checks.GetEnumerator()){$cases.Add([ordered]@{mode=('writer-'+$count);name=$entry.Key;passed=[bool]$entry.Value})}
    $writerMeasurements.Add([ordered]@{inputRuns=$count;summaryWrites=$writes.Count;summaryBytes=$report[0].Length;journalBytes=(Get-Item -LiteralPath $journal).Length;processQueries=$queries.Count;elapsedMilliseconds=$timer.Elapsed.TotalMilliseconds})
}

$receipt=[ordered]@{status=$(if(@($cases|Where-Object {-not $_.passed}).Count){'FAIL'}else{'PASS'});sourceSha256=$sourceHash;sourceUnchanged=((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -eq $sourceHash);checks=$cases.ToArray();writerMeasurements=$writerMeasurements.ToArray();scope='Synthetic owned Runtime fixtures only. Actual removal is intercepted immediately before the unchanged cmdlet. Independent file reads verify flushed journal content; the failure fixture throws before removal.'}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'Result.json'),($receipt|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
Write-Host ($receipt.status+' '+$cases.Count+' durability checks')
if($receipt.status -ne 'PASS' -or -not $receipt.sourceUnchanged){throw 'Journal durability validation failed.'}
return [pscustomobject]$receipt
    } finally {
        foreach($name in $savedJournalScope.Keys){Set-Variable -Name $name -Scope Script -Value $savedJournalScope[$name]}
    }
}

function Invoke-StorageCleanupContracts([hashtable]$Sources,[string]$Directory) {
    # Run the unchanged cleanup sources against a miniature workspace, never the real artifact tree.
    $workspace=Join-Path $Directory 'Workspace'
    $tool=Join-Path $workspace 'tools/game-test'
    foreach ($relative in @('tools/game-test/GameTest.Common.ps1','tools/game-test/Clear-GameTestArtifacts.ps1','tools/runtime-verification/WindowsDesktop.ps1','tools/runtime-verification/EvidencePaths.ps1')) {
        $destination=Join-Path $workspace $relative
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
        [IO.File]::Copy($Sources[$relative],$destination)
    }
    $commonPath=Join-Path $tool 'GameTest.Common.ps1'
    $commonText=[IO.File]::ReadAllText($commonPath)
    $mutexName='Local\SolastaUBGameTest_StorageContracts_'+[guid]::NewGuid().ToString('N')
    $mutexLiteral="'Local\SolastaUBGameTest'"
    if ([regex]::Matches($commonText,[regex]::Escape($mutexLiteral)).Count -ne 1) { throw 'Storage contracts could not isolate the copied cleanup mutex.' }
    $commonText=$commonText.Replace($mutexLiteral,("'"+$mutexName+"'"))
    [IO.File]::WriteAllText($commonPath,$commonText,[Text.UTF8Encoding]::new($false))
    # Only the external process-query boundary is controlled by these contracts.
    $processFixture=Join-Path $workspace 'tools/runtime-verification/ProcessFixture.json'
    [IO.File]::AppendAllText((Join-Path $workspace 'tools/runtime-verification/WindowsDesktop.ps1'),@'

function Get-VerificationProcessState {
    param([string]$Name)
    $mutation=Join-Path $PSScriptRoot 'QueryMutation.json'
    if(Test-Path -LiteralPath $mutation) {
        $change=Get-Content -LiteralPath $mutation -Raw|ConvertFrom-Json
        $counter=Join-Path $PSScriptRoot 'QueryCounter.txt'
        $count=if(Test-Path -LiteralPath $counter){[int][IO.File]::ReadAllText($counter)}else{0}
        $count++;[IO.File]::WriteAllText($counter,[string]$count)
        if($count -eq $change.atQuery){[IO.File]::WriteAllText($change.path,$change.content)}
    }
    $path=Join-Path $PSScriptRoot 'ProcessFixture.json'
    if(Test-Path -LiteralPath $path) {
        $state=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        return $state
    }
    return [pscustomobject]@{querySucceeded=$true;matches=@()}
}
'@)
    foreach ($child in @('artifacts','Cases','Scenarios')) { [void][IO.Directory]::CreateDirectory((Join-Path $tool $child)) }
    $saved=@{}
    foreach ($name in @('GameTestRepo','GameTestBuildRoot','GameTestArtifactRoot','GtValidatedDirectories')) {
        $saved[$name]=Get-Variable -Name $name -Scope Script -ValueOnly
    }
    try {
        . (Join-Path $tool 'GameTest.Common.ps1')
        $cleanup=Join-Path $tool 'Clear-GameTestArtifacts.ps1'
        $checks=[Collections.Generic.List[object]]::new()
        $game=Join-Path $workspace 'DummyGame'
        [void][IO.Directory]::CreateDirectory((Join-Path $game 'Mods'))
        function Add-StorageCheck([string]$Name,[bool]$Passed) {
            $checks.Add([pscustomobject]@{name=$Name;status=$(if($Passed){'PASS'}else{'FAIL'})})
        }
        function Test-StorageRejection([scriptblock]$Action) {
            try { & $Action | Out-Null; return $false } catch { return $true }
        }
        function New-StorageRun([string]$Name,[string]$State='restored',[string]$CaseId='fixture',[int]$Age=0) {
            $path=Join-Path $script:GameTestArtifactRoot $Name
            [void][IO.Directory]::CreateDirectory((Join-Path $path 'Backup'))
            [IO.File]::WriteAllBytes((Join-Path $path 'Backup/000001.bin'),[byte[]](1,2,3,4,5))
            Write-GtJson (Join-Path $path 'Transaction.json') ([ordered]@{
                schemaVersion=1;state=$State;gameDirectory=$game
                files=@([ordered]@{existed=$true;backup='Backup/000001.bin'})
            })
            Write-GtJson (Join-Path $path 'Result.json') ([ordered]@{
                schemaVersion=1;caseId=$CaseId;restored=($State -eq 'restored')
                completedUtc=[datetime]::UtcNow.AddDays(-$Age).ToString('O')
            })
            return $path
        }
        function Invoke-StorageCleanup([string]$Root,[switch]$Apply,[switch]$Prune,[string[]]$Preserve=@(),[hashtable]$Options=@{}) {
            $parameters=@{ArtifactRoot=$Root;Apply=[bool]$Apply;PruneSuperseded=[bool]$Prune;KeepLatestPerCase=1;PreserveDirectory=$Preserve}
            foreach ($key in $Options.Keys) { $parameters[$key]=$Options[$key] }
            & $cleanup @parameters | Out-Null
            $report=Get-ChildItem -LiteralPath $Root -Filter 'Storage-Cleanup-*.json' -File | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
            return Read-GtObject $report.FullName
        }

        $zeroArea=Join-Path $script:GameTestArtifactRoot 'zero-latest';[void][IO.Directory]::CreateDirectory($zeroArea)
        $obsolete=New-StorageRun 'zero-latest/obsolete' 'restored' 'zero-latest'
        $active=New-StorageRun 'zero-latest/active' 'restored' 'zero-latest'
        $unrestored=New-StorageRun 'zero-latest/unrestored' 'deployed' 'zero-latest'
        $zeroRef=Join-Path $Directory 'zero-latest-references.json';Write-GtJson $zeroRef @{schemaVersion=1;paths=@($active)}
        $r=Invoke-StorageCleanup $zeroArea -Prune -Options @{KeepLatestPerCase=0;ReferencePath=@($zeroRef)} -Apply
        Add-StorageCheck 'zero latest artifact policy retains referenced and unrestored owners' (-not(Test-Path -LiteralPath $obsolete) -and (Test-Path -LiteralPath $active) -and (Test-Path -LiteralPath $unrestored) -and @($r.actions|Where-Object {$_.kind -eq 'superseded-run' -and $_.applied}).Count -eq 1)
        Add-StorageCheck 'negative artifact latest count rejects before cleanup' ((Test-StorageRejection {Invoke-StorageCleanup $zeroArea -Prune -Options @{KeepLatestPerCase=-1}}) -and (Test-Path -LiteralPath $active))

        foreach ($state in @('prepared','deployed','unknown')) {
            $path=New-StorageRun ('state-' + $state) $state
            $before=Get-GtHash (Join-Path $path 'Backup/000001.bin')
            $rejected=Test-StorageRejection { Clear-GtRestoredBackup $path }
            Add-StorageCheck ($state + ' transaction retains backup') ($rejected -and (Get-GtHash (Join-Path $path 'Backup/000001.bin')) -eq $before)
        }
        $path=New-StorageRun 'unknown-schema'
        $transaction=Read-GtObject (Join-Path $path 'Transaction.json');$transaction.schemaVersion=2
        Write-GtJson (Join-Path $path 'Transaction.json') $transaction
        Add-StorageCheck 'unknown schema refuses backup release' (Test-StorageRejection { Clear-GtRestoredBackup $path })

        $path=New-StorageRun 'active-marker'
        $marker=Join-Path $game 'Mods/.UBGameTestTransaction.json'
        Write-GtJson $marker @{transactionPath=(Join-Path $workspace 'DifferentRun/Transaction.json')}
        foreach ($preview in @($true,$false)) {
            Add-StorageCheck ('different transaction marker refuses release; preview=' + $preview) (Test-StorageRejection { Clear-GtRestoredBackup $path -Preview:$preview })
        }
        Add-StorageCheck 'marker refusal preserves backup' (Test-Path -LiteralPath (Join-Path $path 'Backup/000001.bin'))
        Remove-Item -LiteralPath $marker -Force

        $path=New-StorageRun 'unknown-backup'
        [IO.File]::WriteAllText((Join-Path $path 'Backup/unowned.txt'),'retain')
        foreach ($preview in @($true,$false)) {
            Add-StorageCheck ('unknown file refuses release; preview=' + $preview) (Test-StorageRejection { Clear-GtRestoredBackup $path -Preview:$preview })
        }
        Add-StorageCheck 'unknown file is retained' (Test-Path -LiteralPath (Join-Path $path 'Backup/unowned.txt'))
        $path=New-StorageRun 'escaping-entry'
        $transaction=Read-GtObject (Join-Path $path 'Transaction.json');$transaction.files[0].backup='Backup/../outside.bin'
        Write-GtJson (Join-Path $path 'Transaction.json') $transaction
        Add-StorageCheck 'backup entry traversal is refused' (Test-StorageRejection { Clear-GtRestoredBackup $path })
        $outside=Join-Path $workspace 'artifacts-escape'
        [void][IO.Directory]::CreateDirectory($outside)
        Add-StorageCheck 'artifact root itself is refused' (Test-StorageRejection { Clear-GtRestoredBackup $script:GameTestArtifactRoot })
        Add-StorageCheck 'artifact prefix sibling is refused' (Test-StorageRejection { Clear-GtRestoredBackup $outside })
        Add-StorageCheck 'entry script refuses outside artifact root' (Test-StorageRejection { & $cleanup -ArtifactRoot $outside -Apply })

        $path=New-StorageRun 'reparse-backup'
        $target=Join-Path $workspace 'JunctionTarget'
        [void][IO.Directory]::CreateDirectory($target)
        $sentinel=Join-Path $target 'sentinel.txt';[IO.File]::WriteAllText($sentinel,'preserved')
        $link=Join-Path $path 'Backup/Link'
        try {
            [void](New-Item -ItemType Junction -Path $link -Target $target)
            Add-StorageCheck 'backup reparse point is refused' (Test-StorageRejection { Clear-GtRestoredBackup $path })
            Add-StorageCheck 'entry script refuses reparse tree before mutation' (Test-StorageRejection { & $cleanup -ArtifactRoot $script:GameTestArtifactRoot -Apply })
            Add-StorageCheck 'reparse target content is retained' ([IO.File]::ReadAllText($sentinel) -ceq 'preserved')
        } finally {
            # Remove only the junction itself, never recurse through its target.
            if (Test-Path -LiteralPath $link) { [IO.Directory]::Delete($link) }
        }

        $path=New-StorageRun 'release-success'
        $preview=Clear-GtRestoredBackup $path -Preview
        Add-StorageCheck 'helper preview counts bytes without a receipt or deletion' ($preview.files -eq 1 -and $preview.bytes -eq 5 -and -not $preview.released -and
            (Test-Path -LiteralPath (Join-Path $path 'Backup/000001.bin')) -and -not (Test-Path -LiteralPath (Join-Path $path 'BackupRelease.json')))
        $released=Clear-GtRestoredBackup $path
        $receiptHash=Get-GtHash (Join-Path $path 'BackupRelease.json')
        $again=Clear-GtRestoredBackup $path
        Add-StorageCheck 'restored release is successful and idempotent' ($released.released -and $released.bytes -eq 5 -and $again.released -and
            -not (Test-Path -LiteralPath (Join-Path $path 'Backup')) -and (Get-GtHash (Join-Path $path 'BackupRelease.json')) -eq $receiptHash)
        $path=New-StorageRun 'empty-backup'
        Remove-Item -LiteralPath (Join-Path $path 'Backup/000001.bin') -Force
        $transaction=Read-GtObject (Join-Path $path 'Transaction.json');$transaction.files=@()
        Write-GtJson (Join-Path $path 'Transaction.json') $transaction
        $emptyPreview=Clear-GtRestoredBackup $path -Preview
        $emptyRelease=Clear-GtRestoredBackup $path
        Add-StorageCheck 'empty backup previews and releases zero bytes' ($emptyPreview.files -eq 0 -and $emptyPreview.bytes -eq 0 -and $emptyRelease.released -and $emptyRelease.bytes -eq 0)
        $emptyRoot=Join-Path $script:GameTestArtifactRoot 'Empty'
        [void][IO.Directory]::CreateDirectory($emptyRoot)
        $emptyPlan=Invoke-StorageCleanup $emptyRoot -Prune
        Add-StorageCheck 'empty artifact root produces a zero action preview' ($emptyPlan.actions.Count -eq 0 -and $emptyPlan.bytes -eq 0)

        # A separate subtree makes the exact preview footprint independent of helper refusal fixtures.
        $pruneRoot=Join-Path $script:GameTestArtifactRoot 'Prune'
        [void][IO.Directory]::CreateDirectory($pruneRoot)
        $actualArtifactRoot=$script:GameTestArtifactRoot
        $script:GameTestArtifactRoot=$pruneRoot
        try {
            $latest=New-StorageRun 'case-latest' 'restored' 'case-a' 0
            $old=New-StorageRun 'case-old' 'restored' 'case-a' 4
            $preserve=New-StorageRun 'case-preserved' 'restored' 'case-a' 5
            $reference=New-StorageRun 'case-referenced' 'restored' 'case-a' 6
            [IO.File]::WriteAllText((Join-Path $tool 'Cases/Reference.json'),'{"artifact":"case-referenced"}')
            $other=New-StorageRun 'other-case-latest' 'restored' 'case-b' 7
            $unrestored=New-StorageRun 'case-unrestored' 'deployed' 'case-a' 8
            $unknown=New-StorageRun 'case-unknown-file' 'restored' 'case-a' 9
            [IO.File]::WriteAllText((Join-Path $unknown 'Backup/unowned.txt'),'retain')
            $schema=New-StorageRun 'case-unknown-schema' 'restored' 'case-a' 10
            $transaction=Read-GtObject (Join-Path $schema 'Transaction.json');$transaction.schemaVersion=2
            Write-GtJson (Join-Path $schema 'Transaction.json') $transaction
            $unknownResult=New-StorageRun 'case-unknown-result-schema' 'restored' 'case-a' 11
            $record=Read-GtObject (Join-Path $unknownResult 'Result.json');$record.schemaVersion=2
            Write-GtJson (Join-Path $unknownResult 'Result.json') $record
            $builds=@()
            foreach ($age in @(0,3)) {
                $build=Join-Path $pruneRoot ('20260101-000000-000-Build-' + $age.ToString('x8'))
                [void][IO.Directory]::CreateDirectory($build)
                Write-GtJson (Join-Path $build 'Build.json') @{schemaVersion=1;status='PASS';stage='Stage/SolastaUnfinishedBusiness';completedUtc=[datetime]::UtcNow.AddDays(-$age).ToString('O');environment=@{gameExe=(Join-Path $game 'Solasta.exe')}}
                $builds+=$build
            }
            $noncanonical=Join-Path $pruneRoot 'Other-Build-00000000'
            [void][IO.Directory]::CreateDirectory($noncanonical)
            [IO.File]::Copy((Join-Path $builds[1] 'Build.json'),(Join-Path $noncanonical 'Build.json'))
            $expectedBytes=[long]((Get-ChildItem -LiteralPath $old -File -Recurse | Measure-Object Length -Sum).Sum)+
                [long]((Get-ChildItem -LiteralPath $builds[1] -File -Recurse | Measure-Object Length -Sum).Sum)+20
            # Four retained runs release their five-byte backups; the old run is counted once.
            $plan=Invoke-StorageCleanup $pruneRoot -Prune -Preserve @($preserve)
            $planned=@($plan.actions | Where-Object kind -eq 'superseded-run' | ForEach-Object path)
            Add-StorageCheck 'preview prunes only superseded eligible run and canonical build' ($planned.Count -eq 2 -and $old -in $planned -and $builds[1] -in $planned)
            Add-StorageCheck 'preview bytes count backup and whole run only once' ($plan.bytes -eq $expectedBytes)
            Add-StorageCheck 'preview retains data and omits release receipts' ((Test-Path -LiteralPath (Join-Path $old 'Backup/000001.bin')) -and
                -not (Test-Path -LiteralPath (Join-Path $latest 'BackupRelease.json')))
            $applied=Invoke-StorageCleanup $pruneRoot -Apply -Prune -Preserve @($preserve)
            Add-StorageCheck 'apply deletes selected old run and old build' (-not (Test-Path -LiteralPath $old) -and -not (Test-Path -LiteralPath $builds[1]))
            Add-StorageCheck 'latest per case and build are retained' ((Test-Path -LiteralPath $latest) -and (Test-Path -LiteralPath $other) -and (Test-Path -LiteralPath $builds[0]))
            Add-StorageCheck 'preserved and referenced evidence directories are retained' ((Test-Path -LiteralPath (Join-Path $preserve 'Backup/000001.bin')) -and (Test-Path -LiteralPath $reference))
            Add-StorageCheck 'unrestored unknown file and unknown schemas are retained' ((Test-Path -LiteralPath (Join-Path $unrestored 'Backup/000001.bin')) -and
                (Test-Path -LiteralPath (Join-Path $unknown 'Backup/unowned.txt')) -and (Test-Path -LiteralPath $schema) -and (Test-Path -LiteralPath $unknownResult))
            Add-StorageCheck 'noncanonical build evidence is retained' (Test-Path -LiteralPath $noncanonical)
            Add-StorageCheck 'entry reports unknown backup as retained' (@($applied.actions | Where-Object { $_.kind -eq 'retained' -and $_.path -eq (Join-Path $unknown 'Backup') }).Count -eq 1)
            $again=Invoke-StorageCleanup $pruneRoot -Apply -Prune -Preserve @($preserve)
            Add-StorageCheck 'repeated apply performs no further destructive action' (@($again.actions | Where-Object kind -ne 'retained').Count -eq 0 -and $again.bytes -eq 0)
        } finally { $script:GameTestArtifactRoot=$actualArtifactRoot }
        $nestedRoot=Join-Path $script:GameTestArtifactRoot 'Nested'
        [void][IO.Directory]::CreateDirectory($nestedRoot)
        $script:GameTestArtifactRoot=$nestedRoot
        try {
            function New-NestedBuild([string]$Group,[int]$Age) {
                $path=Join-Path $nestedRoot ($Group+'/20260101-000000-000-Build-'+$Age.ToString('x8'))
                [void][IO.Directory]::CreateDirectory($path)
                Write-GtJson (Join-Path $path 'Build.json') @{schemaVersion=1;status='PASS';stage='Stage/SolastaUnfinishedBusiness';completedUtc=[datetime]::UtcNow.AddDays(-$Age).ToString('O');environment=@{gameExe=(Join-Path $game 'Solasta.exe')}}
                return $path
            }
            $newBuild=New-NestedBuild 'day-two' 0
            $requiredBuild=New-NestedBuild 'day-one' 5
            $preservedBuild=New-NestedBuild 'day-one' 6
            $incompleteBuild=New-NestedBuild 'day-one' 7
            $unusedBuild=New-NestedBuild 'day-one' 8
            $nestedLatest=New-StorageRun 'day-two/latest' 'restored' 'nested-case' 0
            $nestedOld=New-StorageRun 'day-one/old' 'restored' 'nested-case' 4
            $nestedPreserved=New-StorageRun 'day-one/preserved' 'restored' 'nested-case' 5
            $nestedIncomplete=New-StorageRun 'day-one/incomplete' 'deployed' 'nested-case' 8
            [IO.File]::Copy((Join-Path $requiredBuild 'Build.json'),(Join-Path $nestedLatest 'Build.json'))
            [IO.File]::Copy((Join-Path $unusedBuild 'Build.json'),(Join-Path $nestedOld 'Build.json'))
            [IO.File]::Copy((Join-Path $preservedBuild 'Build.json'),(Join-Path $nestedPreserved 'Build.json'))
            [IO.File]::Copy((Join-Path $incompleteBuild 'Build.json'),(Join-Path $nestedIncomplete 'Build.json'))
            # A contract archive contains intentionally realistic Result/Transaction
            # fixtures. Its Directory.json owns the whole archive, not these inputs.
            $contract=Join-Path $nestedRoot 'day-one/contracts'
            [void][IO.Directory]::CreateDirectory($contract)
            Write-GtJson (Join-Path $contract 'Directory.json') @{schemaVersion=1;layout='GameTest';directory=$contract}
            $fake=New-StorageRun 'day-one/contracts/Workspace/fake-old' 'restored' 'nested-case' 10
            $fakeBackup=Get-GtHash (Join-Path $fake 'Backup/000001.bin')
            # A copied environment can also be an input of an unmarked archive.
            # Its realistic receipts must neither be pruned nor retain our builds.
            $foreign=Join-Path $nestedRoot 'day-two/embedded-tools'
            foreach ($relative in @('tools/game-test/GameTest.Common.ps1','tools/game-test/Clear-GameTestArtifacts.ps1','tools/runtime-verification/WindowsDesktop.ps1','tools/runtime-verification/EvidencePaths.ps1')) {
                $destination=Join-Path $foreign $relative
                [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
                [IO.File]::Copy((Join-Path $workspace $relative),$destination)
            }
            foreach ($child in @('Cases','Scenarios')) { [void][IO.Directory]::CreateDirectory((Join-Path $foreign ('tools/game-test/'+$child))) }
            $foreignRun=New-StorageRun 'day-two/embedded-tools/tools/game-test/artifacts/old' 'restored' 'foreign-owner' 10
            [IO.File]::Copy((Join-Path $unusedBuild 'Build.json'),(Join-Path $foreignRun 'Build.json'))
            $foreignBackup=Get-GtHash (Join-Path $foreignRun 'Backup/000001.bin')
            $nestedPlan=Invoke-StorageCleanup $nestedRoot -Prune -Preserve @($nestedPreserved)
            $nestedPlanned=@($nestedPlan.actions | Where-Object kind -eq 'superseded-run' | ForEach-Object path)
            Add-StorageCheck 'recursive pruning applies one retention policy across grouping folders' ($nestedPlanned.Count -eq 2 -and $nestedOld -in $nestedPlanned -and $unusedBuild -in $nestedPlanned)
            Add-StorageCheck 'latest preserved and incomplete runs retain their archived staged builds' ($requiredBuild -notin $nestedPlanned -and $preservedBuild -notin $nestedPlanned -and $incompleteBuild -notin $nestedPlanned)
            Add-StorageCheck 'build dependency retention explains the retained directory' (@($nestedPlan.retainedDirectories | Where-Object { $_.path -eq $requiredBuild -and $_.reason -eq 'retained-run-build' }).Count -eq 1)
            Add-StorageCheck 'copied environment is excluded from backup and run discovery' (@($nestedPlan.actions | Where-Object { Test-GtWithin $_.path $foreign }).Count -eq 0)
            [void](Invoke-StorageCleanup $nestedRoot -Apply -Prune -Preserve @($nestedPreserved))
            Add-StorageCheck 'recursive apply removes only superseded nested runs and unused builds' (-not(Test-Path -LiteralPath $nestedOld) -and -not(Test-Path -LiteralPath $unusedBuild) -and (Test-Path -LiteralPath $nestedLatest) -and (Test-Path -LiteralPath $newBuild))
            Add-StorageCheck 'kept nested run build inputs survive apply' ((Test-Path -LiteralPath $requiredBuild) -and (Test-Path -LiteralPath $preservedBuild) -and (Test-Path -LiteralPath $incompleteBuild))
            Add-StorageCheck 'owned contract boundary protects nested fake runs and their backups' ((Get-GtHash (Join-Path $fake 'Backup/000001.bin')) -eq $fakeBackup -and -not(Test-Path -LiteralPath (Join-Path $fake 'BackupRelease.json')))
            Add-StorageCheck 'parent cleanup preserves copied environment payloads' ((Test-Path -LiteralPath (Join-Path $foreignRun 'Backup/000001.bin')) -and (Get-GtHash (Join-Path $foreignRun 'Backup/000001.bin')) -eq $foreignBackup -and -not(Test-Path -LiteralPath (Join-Path $foreignRun 'BackupRelease.json')))
            Add-StorageCheck 'parent tool rejects a copied environment artifact root' (Test-StorageRejection { Invoke-StorageCleanup (Join-Path $foreign 'tools/game-test/artifacts') -Apply })
            Add-StorageCheck 'parent tool rejects a scope inside copied environment payloads' (Test-StorageRejection { Invoke-StorageCleanup $foreignRun -Apply })
            Add-StorageCheck 'rejected foreign scopes preserve the copied backup' ((Test-Path -LiteralPath (Join-Path $foreignRun 'Backup/000001.bin')) -and (Get-GtHash (Join-Path $foreignRun 'Backup/000001.bin')) -eq $foreignBackup)
            & (Join-Path $foreign 'tools/game-test/Clear-GameTestArtifacts.ps1') -ArtifactRoot (Join-Path $foreign 'tools/game-test/artifacts') -Apply | Out-Null
            Add-StorageCheck 'copied environment can clean its own restored backup' (-not(Test-Path -LiteralPath (Join-Path $foreignRun 'Backup/000001.bin')) -and (Test-Path -LiteralPath (Join-Path $foreignRun 'BackupRelease.json')))
        } finally { $script:GameTestArtifactRoot=$actualArtifactRoot }
        $malformedRoot=Join-Path $script:GameTestArtifactRoot 'Malformed'
        [void][IO.Directory]::CreateDirectory($malformedRoot)
        $script:GameTestArtifactRoot=$malformedRoot
        try {
            $validLatest=New-StorageRun 'valid-latest' 'restored' 'malformed-case' 0
            $validOld=New-StorageRun 'valid-old' 'restored' 'malformed-case' 2
            $malformed=New-StorageRun 'malformed-json' 'restored' 'malformed-case' 3
            [IO.File]::WriteAllText((Join-Path $malformed 'Transaction.json'),'{invalid')
            $missingState=New-StorageRun 'missing-state' 'restored' 'malformed-case' 4
            $record=Read-GtObject (Join-Path $missingState 'Transaction.json')
            $record.PSObject.Properties.Remove('state')
            Write-GtJson (Join-Path $missingState 'Transaction.json') $record
            $badBuild=Join-Path $malformedRoot '20260101-000000-000-Build-deadbeef'
            [void][IO.Directory]::CreateDirectory($badBuild)
            Write-GtJson (Join-Path $badBuild 'Build.json') @{schemaVersion=1;status='PASS';stage='Stage/SolastaUnfinishedBusiness';completedUtc=[datetime]::UtcNow.AddDays(-5).ToString('O')}
            $plan=Invoke-StorageCleanup $malformedRoot -Prune -Apply
            Add-StorageCheck 'malformed receipt does not prevent eligible independent cleanup' (-not(Test-Path -LiteralPath $validOld) -and (Test-Path -LiteralPath $validLatest))
            Add-StorageCheck 'malformed transaction retains original backup' (Test-Path -LiteralPath (Join-Path $malformed 'Backup/000001.bin'))
            Add-StorageCheck 'missing transaction state retains original backup' (Test-Path -LiteralPath (Join-Path $missingState 'Backup/000001.bin'))
            Add-StorageCheck 'missing build field retains the unknown build directory' (Test-Path -LiteralPath $badBuild)
            Add-StorageCheck 'malformed receipt and missing field retention reasons are reported' (@($plan.retainedDirectories | Where-Object { $_.path -in @($malformed,$missingState,$badBuild) }).Count -eq 3)
        } finally { $script:GameTestArtifactRoot=$actualArtifactRoot }
        $markerRoot=Join-Path $script:GameTestArtifactRoot 'Marker'
        [void][IO.Directory]::CreateDirectory($markerRoot)
        $script:GameTestArtifactRoot=$markerRoot
        try {
            $markerLatest=New-StorageRun 'marker-latest' 'restored' 'marker-case' 0
            $markerOld=New-StorageRun 'marker-old' 'restored' 'marker-case' 2
            Write-GtJson $marker @{transactionPath=(Join-Path $workspace 'Unresolved/Transaction.json')}
            $markerPlan=Invoke-StorageCleanup $markerRoot -Prune
            $markerApply=Invoke-StorageCleanup $markerRoot -Apply -Prune
            Add-StorageCheck 'entry preview and apply retain all runs for an unresolved game marker' (@($markerPlan.actions | Where-Object kind -ne 'retained').Count -eq 0 -and
                @($markerApply.actions | Where-Object kind -ne 'retained').Count -eq 0 -and
                (Test-Path -LiteralPath (Join-Path $markerLatest 'Backup/000001.bin')) -and (Test-Path -LiteralPath (Join-Path $markerOld 'Backup/000001.bin')))
        } finally {
            if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker -Force }
            $script:GameTestArtifactRoot=$actualArtifactRoot
        }
        $nativeTestRoot=Join-Path $actualArtifactRoot 'NativeCompanions'
        [void][IO.Directory]::CreateDirectory($nativeTestRoot)
        $script:GameTestArtifactRoot=$nativeTestRoot
        try {
            $nativeLatest=New-StorageRun 'native-latest' 'restored' 'native-case' 0
            function New-StorageNativeCompanion([string]$Run,[int]$Bytes=5,[switch]$DirectRuntime) {
                $owner=[guid]::NewGuid().ToString('N')
                $path=Join-Path $actualArtifactRoot ('.native/' + $owner)
                $evidence=if ($DirectRuntime) { $Run } else { Join-Path $Run 'Runtime/owned-run' }
                [void][IO.Directory]::CreateDirectory($path)
                [void][IO.Directory]::CreateDirectory($evidence)
                [IO.File]::WriteAllText((Join-Path $path '.ub-runtime-userdata'),$owner)
                [IO.File]::WriteAllText((Join-Path $path '.ub-runtime-evidence'),$evidence)
                [IO.File]::WriteAllBytes((Join-Path $path 'UserPreferences.xml'),[byte[]]::new($Bytes))
                $receipt=Join-Path $evidence 'Native-UserDataLocation.json'
                Write-GtJson $receipt @{owner=$owner;path=$path;evidenceDirectory=$evidence}
                return [pscustomobject]@{path=$path;receipt=$receipt;owner=$owner}
            }
            $nativeOld=New-StorageRun 'native-old' 'restored' 'native-case' 2
            $native=New-StorageNativeCompanion $nativeOld
            Add-StorageCheck 'native user-data root and child reject standalone cleanup' ((Test-StorageRejection { & $cleanup -ArtifactRoot (Join-Path $actualArtifactRoot '.native') -Apply }) -and
                (Test-StorageRejection { & $cleanup -ArtifactRoot $native.path -Apply }) -and (Test-Path -LiteralPath $native.path))
            $nativeBytes=[long]((Get-ChildItem -LiteralPath $native.path -File -Recurse -Force | Measure-Object Length -Sum).Sum)
            $plan=Invoke-StorageCleanup $nativeTestRoot -Prune
            $nativeActions=@($plan.actions | Where-Object kind -eq 'native-user-data')
            Add-StorageCheck 'native companion preview counts owned bytes and retains all data' ($nativeActions.Count -eq 1 -and
                $nativeActions[0].bytes -eq $nativeBytes -and (Test-Path -LiteralPath $native.path) -and (Test-Path -LiteralPath $nativeOld))
            [void](Invoke-StorageCleanup $nativeTestRoot -Apply -Prune)
            Add-StorageCheck 'native companion and its superseded run are deleted together' (-not (Test-Path -LiteralPath $native.path) -and
                -not (Test-Path -LiteralPath $nativeOld) -and (Test-Path -LiteralPath $nativeLatest))
            $nativeOld=New-StorageRun 'native-preserved' 'restored' 'native-case' 3
            $native=New-StorageNativeCompanion $nativeOld
            [void](Invoke-StorageCleanup $nativeTestRoot -Apply -Prune -Preserve @($native.path))
            Add-StorageCheck 'preserving native companion retains its linked evidence run' ((Test-Path -LiteralPath $native.path) -and (Test-Path -LiteralPath $nativeOld))
            foreach ($fault in @('owner','outside','evidence','reparse')) {
                $badRun=New-StorageRun ('native-invalid-' + $fault) 'restored' 'native-case' 4
                $bad=New-StorageNativeCompanion $badRun
                $receipt=Read-GtObject $bad.receipt
                $junction=$null
                switch ($fault) {
                    'owner' { [IO.File]::WriteAllText((Join-Path $bad.path '.ub-runtime-userdata'),'different-owner') }
                    'outside' { $receipt.path=$outside; Write-GtJson $bad.receipt $receipt }
                    'evidence' { $receipt.evidenceDirectory=$nativeLatest; Write-GtJson $bad.receipt $receipt }
                    'reparse' {
                        $junction=Join-Path $bad.path 'Link'
                        [void](New-Item -ItemType Junction -Path $junction -Target $target)
                    }
                }
                try {
                    $plan=Invoke-StorageCleanup $nativeTestRoot -Apply -Prune -Preserve @($native.path)
                    Add-StorageCheck ('invalid native companion retains data and evidence: ' + $fault) ((Test-Path -LiteralPath $badRun) -and
                        (Test-Path -LiteralPath $bad.path) -and @($plan.actions | Where-Object { $_.kind -eq 'retained' -and $_.path -eq $badRun }).Count -eq 1)
                    Add-StorageCheck ('invalid native companion never mutates external target: ' + $fault) ([IO.File]::ReadAllText($sentinel) -ceq 'preserved' -and (Test-Path -LiteralPath $outside))
                } finally {
                    if ($junction -and (Test-Path -LiteralPath $junction)) { [IO.Directory]::Delete($junction) }
                }
            }
        } finally { $script:GameTestArtifactRoot=$actualArtifactRoot }
        # Age/budget policy and mutation-boundary controls use the same owned workspace.
        function New-StorageArea([string]$Name){$path=Join-Path $script:GameTestArtifactRoot $Name;[void][IO.Directory]::CreateDirectory($path);return $path}
        function New-StorageRuntime([string]$Parent,[string]$Name,[double]$Age=8,[string]$Status='success',[int]$Bytes=1024) {
            $path=Join-Path $Parent $Name;[void][IO.Directory]::CreateDirectory($path)
            Write-GtJson (Join-Path $path 'Directory.json') ([ordered]@{schemaVersion=1;layout='Runtime';directory=$path;requestedName=$Name})
            Write-GtJson (Join-Path $path 'Result.json') ([ordered]@{status=$Status;reason='owned diagnostic result';runName=$Name;evidenceDirectory=$path;
                startedUtc=[datetime]::UtcNow.AddDays(-$Age).AddMinutes(-1).ToString('O');completedUtc=[datetime]::UtcNow.AddDays(-$Age).ToString('O');
                settingsRestored=$true;displaySettingsRestored=$true;probeRemoved=$true;originalNativeFilesUnchanged=$true;
                processId=4318;processStartedUtc='2020-01-01T00:00:00Z';failed=$(if($Status -eq 'failed'){1}else{0});
                processCleanup=@{completed=$true;expectedProcessId=4318;expectedStartTimeUtc='2020-01-01T00:00:00Z';expectedPath=(Join-Path $game 'Solasta.exe')}})
            [IO.File]::WriteAllBytes((Join-Path $path 'payload.bin'),[byte[]]::new($Bytes))
            return $path
        }
        function New-StorageBuild([string]$Parent,[string]$Name,[double]$Age=8) {
            $path=Join-Path $Parent $Name;[void][IO.Directory]::CreateDirectory($path)
            Write-GtJson (Join-Path $path 'Build.json') @{schemaVersion=1;status='PASS';stage='Stage/SolastaUnfinishedBusiness';completedUtc=[datetime]::UtcNow.AddDays(-$Age).ToString('O');environment=@{gameExe=(Join-Path $game 'Solasta.exe')}}
            [IO.File]::WriteAllBytes((Join-Path $path 'payload.bin'),[byte[]]::new(8192))
            return $path
        }
        function New-StorageConsumer([string]$Parent,[string]$Name,[string]$Build,[double]$Age=8) {
            $path=Join-Path $Parent $Name;[void][IO.Directory]::CreateDirectory($path)
            Write-GtJson (Join-Path $path 'Transaction.json') @{schemaVersion=1;state='restored';gameDirectory=$game;files=@()}
            Write-GtJson (Join-Path $path 'Result.json') @{schemaVersion=1;caseId=$Name;restored=$true;completedUtc=[datetime]::UtcNow.AddDays(-$Age).ToString('O');status='FAIL';reason='diagnostic'}
            [IO.File]::Copy((Join-Path $Build 'Build.json'),(Join-Path $path 'Build.json'))
            return $path
        }
        function Set-StorageRuntimeResult([string]$Run,[scriptblock]$Action){$r=Get-Content -LiteralPath (Join-Path $Run 'Result.json') -Raw|ConvertFrom-Json;&$Action $r;Write-GtJson (Join-Path $Run 'Result.json') $r}
        function Set-StorageQueryMutation([string]$Path,$Value,[int]$At=1,[switch]$Raw) {
            $counter=Join-Path (Split-Path -Parent $processFixture) 'QueryCounter.txt'
            if(Test-Path -LiteralPath $counter){Remove-Item -LiteralPath $counter}
            $content=if($Raw){[string]$Value}else{$Value|ConvertTo-Json -Depth 30}
            Write-GtJson (Join-Path (Split-Path -Parent $processFixture) 'QueryMutation.json') @{atQuery=$At;path=$Path;content=$content}
        }
        function Clear-StorageQueryMutation {
            foreach($name in @('QueryCounter.txt','QueryMutation.json')){
                $path=Join-Path (Split-Path -Parent $processFixture) $name
                if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path}
            }
        }

        $area=New-StorageArea 'unique-default';$a=New-StorageRuntime $area 'uniqueA';$b=New-StorageRuntime $area 'uniqueB'
        $r=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2} -Apply
        Add-StorageCheck 'default count retention remains compatible for unique IDs' ((Test-Path -LiteralPath $a)-and(Test-Path -LiteralPath $b)-and $r.actions.Count -eq 0)
        foreach($status in @('success','failed','incomplete')) {
            $area=New-StorageArea ('age-'+$status);$run=New-StorageRuntime $area ('unique-'+$status) 8 $status;$native=(New-StorageNativeCompanion $run -Bytes 16384 -DirectRuntime).path
            $preview=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1}
            Add-StorageCheck ('direct runtime '+$status+' preview is non-destructive') ((Test-Path -LiteralPath $run)-and(Test-Path -LiteralPath $native)-and @($preview.actions|Where-Object kind -eq 'superseded-run').Count -eq 1)
            $r=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1} -Apply
            Add-StorageCheck ('direct runtime '+$status+' expires together with owned native user-data') (-not(Test-Path -LiteralPath $run)-and -not(Test-Path -LiteralPath $native))
            Add-StorageCheck ('diagnostic receipt survives retirement: '+$status) ($r.retiredRuns.Count -eq 1 -and $r.retiredRuns[0].result.status -ceq $status -and $r.retiredRuns[0].identitySha256 -match '^[A-F0-9]{64}$')
            $again=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1} -Apply
            Add-StorageCheck ('repeat age cleanup is idempotent: '+$status) ($again.actions.Count -eq 0)
        }
        foreach($field in @('settingsRestored','displaySettingsRestored','probeRemoved','originalNativeFilesUnchanged')) {
            $area=New-StorageArea ('incomplete-'+$field);$run=New-StorageRuntime $area 'incomplete'
            $r=Get-Content -LiteralPath (Join-Path $run 'Result.json') -Raw|ConvertFrom-Json;$r.$field=$false;Write-GtJson (Join-Path $run 'Result.json') $r
            $plan=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1} -Apply
            Add-StorageCheck ('incomplete restoration is retained: '+$field) ((Test-Path -LiteralPath $run)-and @($plan.retainedDirectories|Where-Object reason -eq 'unrecognized-receipt').Count -eq 1)
        }
        $area=New-StorageArea 'string-restoration';$run=New-StorageRuntime $area 'invalid';Set-StorageRuntimeResult $run {param($r)$r.settingsRestored='true'}
        $r=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1} -Apply
        Add-StorageCheck 'string true is not restoration proof' (Test-Path -LiteralPath $run)
        $area=New-StorageArea 'wrong-owner';$run=New-StorageRuntime $area 'invalid';$d=Get-Content -LiteralPath (Join-Path $run 'Directory.json') -Raw|ConvertFrom-Json;$d.directory=$area;Write-GtJson (Join-Path $run 'Directory.json') $d
        $r=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1} -Apply
        Add-StorageCheck 'mismatched direct runtime ownership stays intact' (Test-Path -LiteralPath $run)
        $area=New-StorageArea 'active-owner';$run=New-StorageRuntime $area 'active'
        Write-GtJson $processFixture @{querySucceeded=$true;matches=@(@{pid=4318;startTimeUtc='2020-01-01T00:00:00Z';path=(Join-Path $game 'Solasta.exe');readErrors=@{}})}
        $r=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1}
        Add-StorageCheck 'active exact PID/start/path is retained' ((Test-Path -LiteralPath $run)-and $r.retiredRuns.Count -eq 0)
        Write-GtJson $processFixture @{querySucceeded=$true;matches=@(@{pid=4318;startTimeUtc='2021-01-01T00:00:00Z';path=(Join-Path $game 'Solasta.exe');readErrors=@{}})}
        $r=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1}
        Add-StorageCheck 'a reused PID does not falsely own the retired runtime' (@($r.actions|Where-Object kind -eq 'superseded-run').Count -eq 1)
        Remove-Item -LiteralPath $processFixture
        $area=New-StorageArea 'active-game';$run=New-StorageRuntime $area 'old'
        Write-GtJson $processFixture @{querySucceeded=$true;matches=@(@{pid=9999;startTimeUtc='2022-01-01T00:00:00Z';path=(Join-Path $game 'Solasta.exe');readErrors=@{}})}
        Add-StorageCheck 'Apply refuses a running game before payload deletion' ((Test-StorageRejection {Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1} -Apply})-and(Test-Path -LiteralPath $run))
        Write-GtJson $processFixture @{querySucceeded=$false;matches=@()}
        $r=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1} -Apply
        Add-StorageCheck 'unknown process query is not absence' ((Test-Path -LiteralPath $run)-and $r.retiredRuns.Count -eq 0 -and @($r.retainedDirectories|Where-Object reason -eq 'unrecognized-receipt').Count -eq 1)
        Remove-Item -LiteralPath $processFixture

        $area=New-StorageArea 'budget-unique';$a=New-StorageRuntime $area 'oldUnique' 8 'success' 8192;$b=New-StorageRuntime $area 'newUnique' 2 'success' 8192
        $r=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxRetainedBytes=1000} -Apply
        Add-StorageCheck 'byte budget can retire obsolete unique latest IDs' (-not(Test-Path -LiteralPath $a)-and -not(Test-Path -LiteralPath $b)-and $r.policy.budgetSatisfied)
        $area=New-StorageArea 'hard-budget';$a=New-StorageRuntime $area 'hard' 8 'success' 8192;$b=New-StorageRuntime $area 'soft' 2 'success' 8192
        $r=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxRetainedBytes=1000;PreserveDirectory=@($a)} -Apply
        Add-StorageCheck 'hard preservation wins over byte budget and reports the unmet budget' ((Test-Path -LiteralPath $a)-and -not(Test-Path -LiteralPath $b)-and -not $r.policy.budgetSatisfied -and $r.policy.retainedBytes -gt 1000)
        $area=New-StorageArea 'reference-input';$run=New-StorageRuntime $area 'native-donor';$ref=Join-Path $Directory 'ActiveStorageReferences.json'
        Write-GtJson $ref @{schemaVersion=1;paths=@((Join-Path $run 'payload.bin'))}
        $r=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1;ReferencePath=@($ref)} -Apply
        Add-StorageCheck 'external authoring reference retains the actual owning run' ((Test-Path -LiteralPath $run)-and @($r.retainedDirectories|Where-Object reason -eq 'declared-input-reference').Count -eq 1)
        $native=(New-StorageNativeCompanion $run -Bytes 16384 -DirectRuntime).path;Write-GtJson $ref @{schemaVersion=1;paths=@($native)}
        $r=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1;ReferencePath=@($ref)} -Apply
        Add-StorageCheck 'referenced native companion retains its evidence owner' ((Test-Path -LiteralPath $run)-and(Test-Path -LiteralPath $native)-and @($r.retainedDirectories|Where-Object reason -eq 'preserved-native-companion').Count -eq 1)
        foreach($kind in @('outside','schema','type','unknown')) {
            $bad=switch($kind){'outside'{@{schemaVersion=1;paths=@($workspace)}}'schema'{@{schemaVersion=2;paths=@($run)}}'type'{@{schemaVersion=1;paths='not-array'}}'unknown'{@{schemaVersion=1;paths=@($run);execute=$true}}}
            Write-GtJson $ref $bad
            Add-StorageCheck ('invalid reference manifest is rejected before mutation: '+$kind) ((Test-StorageRejection {Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1;ReferencePath=@($ref)} -Apply})-and(Test-Path -LiteralPath $run))
        }
        $area=New-StorageArea 'budget-build-closure'
        $build=New-StorageBuild $area '20260101-000000-001-Build-abcd' 9
        $consumer=New-StorageConsumer $area 'unique-consumer' $build 8
        $r=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxRetainedBytes=1000} -Apply
        Add-StorageCheck 'budget releases the build when its last consumer is retired' (-not(Test-Path -LiteralPath $consumer)-and -not(Test-Path -LiteralPath $build)-and $r.policy.budgetSatisfied)
        $area=New-StorageArea 'hard-build-closure'
        $required=New-StorageBuild $area '20260101-000000-001-Build-abcd' 9
        $unneeded=New-StorageBuild $area '20260101-000000-002-Build-abce' 7
        $consumer=New-StorageConsumer $area 'current-consumer' $required 8
        $r=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxRetainedBytes=1000;PreserveDirectory=@($consumer)} -Apply
        Add-StorageCheck 'hard retained consumer preserves its exact build while unrelated build expires' ((Test-Path -LiteralPath $consumer)-and(Test-Path -LiteralPath $required)-and -not(Test-Path -LiteralPath $unneeded)-and -not $r.policy.budgetSatisfied)
        $area=New-StorageArea 'scoped-build-closure'
        $buildArea=Join-Path $area 'builds';[void][IO.Directory]::CreateDirectory($buildArea)
        $required=New-StorageBuild $buildArea '20260101-000000-001-Build-abcd' 9
        $unneeded=New-StorageBuild $buildArea '20260101-000000-002-Build-abce' 7
        $consumer=New-StorageConsumer (Join-Path $area 'consumers') 'external-consumer' $required 8
        $r=Invoke-StorageCleanup $buildArea -Prune -Options @{KeepLatestPerCase=0}
        Add-StorageCheck 'subtree preview retains a build required by an external consumer' ((Test-Path -LiteralPath $required)-and(Test-Path -LiteralPath $unneeded)-and @($r.actions|Where-Object {$_.kind -eq 'superseded-run' -and $_.path -eq $required}).Count -eq 0-and @($r.actions|Where-Object {$_.kind -eq 'superseded-run' -and $_.path -eq $unneeded}).Count -eq 1)
        $r=Invoke-StorageCleanup $buildArea -Prune -Options @{KeepLatestPerCase=0} -Apply
        Add-StorageCheck 'subtree apply preserves the external consumer and required build only' ((Test-Path -LiteralPath $consumer)-and(Test-Path -LiteralPath $required)-and -not(Test-Path -LiteralPath $unneeded))
        $area=New-StorageArea 'late-external-build-closure'
        $buildArea=Join-Path $area 'builds';[void][IO.Directory]::CreateDirectory($buildArea)
        $required=New-StorageBuild $buildArea '20260101-000000-001-Build-abcd' 9
        $consumer=New-StorageConsumer (Join-Path $area 'consumers') 'late-consumer' $required 8
        $reference=Join-Path $consumer 'Build.json';Remove-Item -LiteralPath $reference
        Set-StorageQueryMutation $reference ([IO.File]::ReadAllText((Join-Path $required 'Build.json'))) -Raw
        try {
            $r=Invoke-StorageCleanup $buildArea -Prune -Options @{KeepLatestPerCase=0} -Apply
            Add-StorageCheck 'a consumer outside the subtree appearing before deletion preserves its build' ((Test-Path -LiteralPath $required)-and(Test-Path -LiteralPath $reference)-and @($r.actions|Where-Object {$_.path -eq $required -and $_.kind -eq 'retained' -and -not $_.applied}).Count -eq 1)
        } finally { Clear-StorageQueryMutation }
        $area=New-StorageArea 'hard-native-budget';$run=New-StorageRuntime $area 'current-native';$native=(New-StorageNativeCompanion $run -Bytes 32768 -DirectRuntime).path
        $r=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxRetainedBytes=20000;PreserveDirectory=@($run)} -Apply
        Add-StorageCheck 'byte budget includes native payloads of hard retained owners' ((Test-Path -LiteralPath $run)-and(Test-Path -LiteralPath $native)-and $r.policy.retainedBytes -gt 32768 -and -not $r.policy.budgetSatisfied)
        $area=New-StorageArea 'age-window';$oldRun=New-StorageRuntime $area 'old-unique' 8;$youngRun=New-StorageRuntime $area 'young-unique' 0.25
        $r=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1} -Apply
        Add-StorageCheck 'age policy preserves a recent unique ID and retires the old one' (-not(Test-Path -LiteralPath $oldRun)-and(Test-Path -LiteralPath $youngRun))
        $area=New-StorageArea 'negative-budget';$run=New-StorageRuntime $area 'untouched'
        Add-StorageCheck 'negative byte budget rejects before mutation' ((Test-StorageRejection {Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxRetainedBytes=-1} -Apply})-and(Test-Path -LiteralPath $run))

        $area=New-StorageArea 'root-is-owned-run';$run=New-StorageRuntime $area 'owned-root'
        $r=Invoke-StorageCleanup $run -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1;MaxRetainedBytes=1} -Apply
        Add-StorageCheck 'an owned ArtifactRoot keeps itself and its cleanup receipt' ((Test-Path -LiteralPath $run)-and(Test-Path -LiteralPath (Join-Path $run 'Result.json'))-and @($r.retainedDirectories|Where-Object reason -eq 'cleanup-report-directory').Count -eq 1 -and $r.actions.Count -eq 0)
        $area=New-StorageArea 'manifest-owner';$owner=New-StorageRuntime $area 'manifest-owner';$run=New-StorageRuntime $area 'current-donor';$ref=Join-Path $owner 'ActiveStorageReferences.json'
        Write-GtJson $ref @{schemaVersion=1;paths=@($run)}
        $r=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1;ReferencePath=@($ref)} -Apply
        Add-StorageCheck 'the active reference manifest itself retains its owning run' ((Test-Path -LiteralPath $owner)-and(Test-Path -LiteralPath $run))
        $area=New-StorageArea 'changed-manifest';$run=New-StorageRuntime $area 'newly-referenced';$ref=Join-Path $Directory 'ChangingReferences.json'
        Write-GtJson $ref @{schemaVersion=1;paths=@()};Set-StorageQueryMutation $ref @{schemaVersion=1;paths=@($run)}
        Add-StorageCheck 'manifest changed during planning aborts before deleting its newly referenced run' ((Test-StorageRejection {Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1;ReferencePath=@($ref)} -Apply})-and(Test-Path -LiteralPath $run))
        Clear-StorageQueryMutation
        $area=New-StorageArea 'changed-consumer';$build=New-StorageBuild $area '20260101-000000-001-Build-abcd' 9;$run=New-StorageConsumer $area '000-consumer' $build 8
        $changed=Get-Content -LiteralPath (Join-Path $run 'Result.json') -Raw|ConvertFrom-Json;$changed.reason='changed after planning'
        Set-StorageQueryMutation (Join-Path $run 'Result.json') $changed
        Add-StorageCheck 'consumer identity failure leaves both consumer and required build intact' ((Test-StorageRejection {Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1} -Apply})-and(Test-Path -LiteralPath $run)-and(Test-Path -LiteralPath $build))
        Clear-StorageQueryMutation
        $area=New-StorageArea 'changed-marker';$build=New-StorageBuild $area '20260101-000000-001-Build-abcd' 9;$run=New-StorageConsumer $area '000-consumer' $build 8
        $marker=Join-Path $game 'Mods/.UBGameTestTransaction.json';Set-StorageQueryMutation $marker @{transactionPath=(Join-Path $run 'Transaction.json')}
        Add-StorageCheck 'a recovery marker appearing before deletion preserves consumer and build' ((Test-StorageRejection {Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1} -Apply})-and(Test-Path -LiteralPath $run)-and(Test-Path -LiteralPath $build))
        Clear-StorageQueryMutation;Remove-Item -LiteralPath $marker
        $area=New-StorageArea 'changed-restore';$build=New-StorageBuild $area '20260101-000000-001-Build-abcd' 9;$run=New-StorageConsumer $area '000-consumer' $build 8
        Set-StorageQueryMutation (Join-Path $run 'Transaction.json') @{schemaVersion=1;state='deployed';gameDirectory=$game;files=@()}
        Add-StorageCheck 'restore state changed before deletion preserves consumer and build' ((Test-StorageRejection {Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1} -Apply})-and(Test-Path -LiteralPath $run)-and(Test-Path -LiteralPath $build))
        Clear-StorageQueryMutation
        $area=New-StorageArea 'changed-native-marker';$run=New-StorageRuntime $area 'owner';$native=(New-StorageNativeCompanion $run -Bytes 16384 -DirectRuntime).path
        Set-StorageQueryMutation (Join-Path $native '.ub-runtime-userdata') 'changed-owner' 4
        Add-StorageCheck 'native ownership marker changed at the last process check prevents both deletions' ((Test-StorageRejection {Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1} -Apply})-and(Test-Path -LiteralPath $run)-and(Test-Path -LiteralPath $native))
        Clear-StorageQueryMutation
        $area=New-StorageArea 'changed-reference-before-backup';$build=New-StorageBuild $area '20260101-000000-001-Build-abcd';$run=New-StorageConsumer $area 'consumer' $build
        [void][IO.Directory]::CreateDirectory((Join-Path $run 'Backup'));[IO.File]::WriteAllBytes((Join-Path $run 'Backup/000001.bin'),[byte[]](1,2,3))
        Write-GtJson (Join-Path $run 'Transaction.json') @{schemaVersion=1;state='restored';gameDirectory=$game;files=@(@{existed=$true;backup='Backup/000001.bin'})}
        $ref=Join-Path $Directory 'BackupReferences.json';Write-GtJson $ref @{schemaVersion=1;paths=@()};Set-StorageQueryMutation $ref @{schemaVersion=1;paths=@($run)}
        $backup=Join-Path $run 'Backup/000001.bin';$backupHash=Get-GtHash $backup
        $aborted=$false;$backupPlan=$null;$failureReason=''
        try { $backupPlan=Invoke-StorageCleanup $area -Prune -Options @{KeepLatestPerCase=2;MaxAgeDays=1;ReferencePath=@($ref)} -Apply }
        catch { $aborted=$true;$failureReason=$_.Exception.Message }
        $referenceGuard=if ($aborted) { $failureReason -ceq 'A storage reference manifest changed during cleanup.' } else {
            @($backupPlan.actions | Where-Object { $_.kind -eq 'retained' -and $_.path -eq (Split-Path -Parent $backup) -and
                $_.reason -ceq 'A storage reference manifest changed during cleanup.' }).Count -eq 1 -and $backupPlan.retiredRuns.Count -eq 0
        }
        Add-StorageCheck 'reference hash is rechecked before the first backup release' ($referenceGuard -and
            (Test-Path -LiteralPath $backup) -and (Get-GtHash $backup) -eq $backupHash -and (Test-Path -LiteralPath $run) -and (Test-Path -LiteralPath $build))
        Clear-StorageQueryMutation


        # Native data is transient; the restored evidence and its source freeze remain owned.
        function New-StorageReleaseOwner([string]$Name) {
            $outer=New-StorageRun ('native-release-'+$Name) 'restored' 'native-release-contract' 0
            $inner=New-StorageRuntime (Join-Path $outer 'Runtime') 'owned-run' 0
            $companion=New-StorageNativeCompanion $outer -Bytes 4096
            $result=Read-GtObject (Join-Path $outer 'Result.json')
            $result|Add-Member -NotePropertyMembers @{status='PASS';evidenceDirectory=$outer;displaySettingsRestored=$true;originalSavePreserved=$true;runtimeResult=(Join-Path $inner 'Result.json')}
            Write-GtJson (Join-Path $outer 'Result.json') $result
            [IO.File]::WriteAllText((Join-Path $outer 'source-input.txt'),'owned immutable input')
            Write-GtJson (Join-Path $outer 'SourceFreeze.json') @{schemaVersion=1;files=@(@{path=(Join-Path $outer 'source-input.txt');sha256=(Get-GtHash (Join-Path $outer 'source-input.txt'))})}
            return [pscustomobject]@{run=$outer;runtime=$inner;native=$companion;resultHash=(Get-GtHash (Join-Path $outer 'Result.json'));
                runtimeHash=(Get-GtHash (Join-Path $inner 'Result.json'));sourceHash=(Get-GtHash (Join-Path $outer 'SourceFreeze.json'))}
        }
        $release=New-StorageReleaseOwner 'completed'
        $provenance=Join-Path $Directory 'NativeReleaseProvenance.json'
        Write-GtJson $provenance @{schemaVersion=1;paths=@($release.run)}
        $preview=Clear-GtNativeUserData -RunDirectory $release.run -ReferencePath @($provenance) -Preview
        Add-StorageCheck 'native release preview preserves data and creates no receipt or journal' ($preview.status -ceq 'PREVIEW' -and
            (Test-Path -LiteralPath $release.native.path) -and -not(Test-Path -LiteralPath (Join-Path $release.run 'NativeUserDataRelease.json')) -and
            -not(Test-Path -LiteralPath (Join-Path $release.run 'NativeUserDataRelease.jsonl')))
        $released=Clear-GtNativeUserData -RunDirectory $release.run -ReferencePath @($provenance)
        $releaseReceipt=Read-GtObject (Join-Path $release.run 'NativeUserDataRelease.json')
        $releaseEvents=@(Get-Content -LiteralPath (Join-Path $release.run 'NativeUserDataRelease.jsonl')|ForEach-Object{$_|ConvertFrom-Json})
        $releaseEventNames=@($releaseEvents|ForEach-Object event)
        Add-StorageCheck 'completed provenance retains immutable evidence while transient native data releases durably' ($released.status -ceq 'RELEASED' -and
            $releaseReceipt.status -ceq 'RELEASED' -and -not(Test-Path -LiteralPath $release.native.path) -and
            (Get-GtHash (Join-Path $release.run 'Result.json')) -ceq $release.resultHash -and
            (Get-GtHash (Join-Path $release.runtime 'Result.json')) -ceq $release.runtimeHash -and
            (Get-GtHash (Join-Path $release.run 'SourceFreeze.json')) -ceq $release.sourceHash -and
            [array]::IndexOf($releaseEventNames,'action-planned') -ge 0 -and
            [array]::IndexOf($releaseEventNames,'action-complete') -gt [array]::IndexOf($releaseEventNames,'action-planned') -and
            $releaseEventNames[-1] -ceq 'release-completed')
        $again=Clear-GtNativeUserData -RunDirectory $release.run
        Add-StorageCheck 'native data release is idempotent after its evidence owner remains' ($again.status -ceq 'ALREADY_RELEASED' -and
            $again.actions.Count -eq 0 -and (Test-Path -LiteralPath (Join-Path $release.run 'SourceFreeze.json')) -and
            (Get-GtHash (Join-Path $release.run 'Result.json')) -ceq $release.resultHash)

        $keptNative=$true
        foreach($mode in @('reference','preserve','keep')) {
            $release=New-StorageReleaseOwner $mode
            $parameters=@{RunDirectory=$release.run}
            if($mode -eq 'reference') {
                $reference=Join-Path $Directory 'NativeReleaseReferences.json'
                Write-GtJson $reference @{schemaVersion=1;paths=@((Join-Path $release.native.path 'UserPreferences.xml'))}
                $parameters.ReferencePath=@($reference)
            } elseif($mode -eq 'preserve') {$parameters.PreserveDirectory=@($release.native.path)} else {$parameters.Keep=$true}
            $held=Clear-GtNativeUserData @parameters
            $keptNative=$keptNative -and $held.status -ceq 'RETAINED' -and (Test-Path -LiteralPath $release.native.path) -and
                @($held.actions|Where-Object reason -eq 'explicit-native-data-reference').Count -eq 1
        }
        Add-StorageCheck 'explicit native file references preserve roots and explicit keep retain native data' $keptNative

        $keptOutput=$true
        foreach($folder in @('Saves','Characters')) {
            $release=New-StorageReleaseOwner ('output-'+$folder)
            $produced=Join-Path $release.native.path ($folder+'/nested/owned.bin')
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $produced))
            [IO.File]::WriteAllBytes($produced,[byte[]](1,2,3))
            $hash=Get-GtHash $produced
            $held=Clear-GtNativeUserData -RunDirectory $release.run
            $keptOutput=$keptOutput -and $held.status -ceq 'RETAINED' -and (Test-Path -LiteralPath $produced) -and
                (Get-GtHash $produced) -ceq $hash -and @($held.actions|Where-Object reason -eq 'unexported-native-output').Count -eq 1
        }
        Add-StorageCheck 'unexported saves and characters preserve their exact native bytes' $keptOutput

        $keptUnrestored=$true
        foreach($fault in @('outer','inner','owner')) {
            $release=New-StorageReleaseOwner ('incomplete-'+$fault)
            if($fault -eq 'outer') {
                $transaction=Read-GtObject (Join-Path $release.run 'Transaction.json');$transaction.state='deployed'
                Write-GtJson (Join-Path $release.run 'Transaction.json') $transaction
            } elseif($fault -eq 'inner') {Set-StorageRuntimeResult $release.runtime {$args[0].settingsRestored=$false}}
            else {[IO.File]::WriteAllText((Join-Path $release.native.path '.ub-runtime-userdata'),'different-owner')}
            $held=Clear-GtNativeUserData -RunDirectory $release.run
            $keptUnrestored=$keptUnrestored -and $held.status -ceq 'RETAINED' -and (Test-Path -LiteralPath $release.native.path) -and
                -not[string]::IsNullOrWhiteSpace($held.reason)
        }
        Add-StorageCheck 'incomplete outer or inner restoration and invalid native owners retain data' $keptUnrestored

        $keptRunning=$true
        foreach($mode in @('active','query-failed')) {
            $release=New-StorageReleaseOwner ('process-'+$mode)
            if($mode -eq 'active') {Write-GtJson $processFixture @{querySucceeded=$true;matches=@(@{pid=4318;startTimeUtc='2020-01-01T00:00:00Z';path=(Join-Path $game 'Solasta.exe');readErrors=@{}})}}
            else {Write-GtJson $processFixture @{querySucceeded=$false;matches=@()}}
            try {
                $held=Clear-GtNativeUserData -RunDirectory $release.run
                $keptRunning=$keptRunning -and $held.status -ceq 'RETAINED' -and (Test-Path -LiteralPath $release.native.path)
            } finally {Remove-Item -LiteralPath $processFixture -ErrorAction SilentlyContinue}
        }
        Add-StorageCheck 'active and unqueryable native owners retain their data' $keptRunning

        $release=New-StorageReleaseOwner 'multiple-companions'
        $secondRuntime=New-StorageRuntime (Join-Path $release.run 'Runtime') 'second-owned-run' 0
        $secondNative=New-StorageNativeCompanion $secondRuntime -Bytes 4096 -DirectRuntime
        Set-StorageRuntimeResult $secondRuntime {$args[0].displaySettingsRestored=$false}
        $secondBytes=Join-Path $secondNative.path 'UserPreferences.xml'
        $secondHash=Get-GtHash $secondBytes
        $held=Clear-GtNativeUserData -RunDirectory $release.run
        Add-StorageCheck 'each native companion requires its own restored runtime result' ($held.status -ceq 'RETAINED' -and
            (Test-Path -LiteralPath $secondBytes) -and (Get-GtHash $secondBytes) -ceq $secondHash -and
            (Get-GtHash (Join-Path $release.run 'Result.json')) -ceq $release.resultHash)

        $release=New-StorageReleaseOwner 'late-reference'
        $reference=Join-Path $Directory 'NativeReleaseLateReferences.json'
        Write-GtJson $reference @{schemaVersion=1;paths=@()}
        Set-StorageQueryMutation $reference @{schemaVersion=1;paths=@($release.native.path)} 3
        try {
            $held=Clear-GtNativeUserData -RunDirectory $release.run -ReferencePath @($reference)
            $changed=Read-GtObject $reference
            Add-StorageCheck 'a reference changing after the initial snapshot prevents native release' ($held.status -ceq 'RETAINED' -and
                $release.native.path -in $changed.paths -and (Test-Path -LiteralPath $release.native.path) -and
                (Get-GtHash (Join-Path $release.run 'Result.json')) -ceq $release.resultHash)
        } finally {Clear-StorageQueryMutation}
        $release=New-StorageReleaseOwner 'late-consumer'
        $lateConsumer=Join-Path $tool 'Cases/LateNativeConsumer.json'
        Set-StorageQueryMutation $lateConsumer @{nativeInput=$release.native.owner} 3
        try {
            $held=Clear-GtNativeUserData -RunDirectory $release.run
            Add-StorageCheck 'a new case consumer is re-read before native data release' ($held.status -ceq 'RETAINED' -and
                (Test-Path -LiteralPath $lateConsumer) -and (Test-Path -LiteralPath $release.native.path))
        } finally {Clear-StorageQueryMutation;if(Test-Path -LiteralPath $lateConsumer){Remove-Item -LiteralPath $lateConsumer}}
        $release=New-StorageReleaseOwner 'late-owner'
        $ownerMarker=Join-Path $release.native.path '.ub-runtime-userdata'
        Set-StorageQueryMutation $ownerMarker 'changed-at-final-process-query' 3 -Raw
        try {
            $held=Clear-GtNativeUserData -RunDirectory $release.run
            Add-StorageCheck 'the native marker is checked after the final process query' ($held.status -ceq 'RETAINED' -and
                (Test-Path -LiteralPath $release.native.path) -and [IO.File]::ReadAllText($ownerMarker) -ceq 'changed-at-final-process-query')
        } finally {Clear-StorageQueryMutation}

        $journalArtifactRoot=$script:GameTestArtifactRoot
        $journalContracts=Invoke-StorageJournalContracts $Sources (Join-Path $Directory 'Journal')
        Add-StorageCheck 'nested journal fixtures restore the outer artifact scope' ($script:GameTestArtifactRoot -ceq $journalArtifactRoot)
        foreach($check in $journalContracts.checks){Add-StorageCheck ($check.mode+' '+$check.name) $check.passed}

        $checkpoints=@(Get-ChildItem -LiteralPath $script:GameTestArtifactRoot -Filter Storage-Cleanup-*.json -File -Recurse | ForEach-Object {Get-Content -LiteralPath $_.FullName -Raw|ConvertFrom-Json})
        Add-StorageCheck 'all applied run deletions retain a prior result/hash diagnostic' (@($checkpoints | ForEach-Object {
            $plan=$_;foreach($action in @($plan.actions|Where-Object{ $_.kind -eq 'superseded-run' -and $_.applied })) {
                if(@($plan.retiredRuns|Where-Object{ $_.path -eq $action.path -and $_.result -and $_.identitySha256 -match '^[A-F0-9]{64}$'}).Count -ne 1){$action}
            }
        }).Count -eq 0)

        return [ordered]@{status=$(if(@($checks | Where-Object status -ne 'PASS').Count){'FAIL'}else{'PASS'});scope='Actual cleanup and path/restore/delete sources run against owned dummy files, real directory junctions and native companions in one mirrored workspace. Process lookup, the copied mutex, summary writer observation and a synthetic pre-delete failure are controlled fixture boundaries; no installed game or real artifacts are modified.';cases=@($checks.ToArray())}
    } finally {
        foreach ($name in $saved.Keys) { Set-Variable -Name $name -Scope Script -Value $saved[$name] }
    }
}

function Invoke-ProcessThreadAcquisitionContracts([string]$Source,[string]$Directory) {
    [void][IO.Directory]::CreateDirectory($Directory)
    $sourceText=[IO.File]::ReadAllText($Source)
    function Read-PssMember([string]$Text,[string]$Marker,[bool]$Struct=$false) {
        $start=$Text.IndexOf($Marker,[StringComparison]::Ordinal)
        if($start -lt 0 -or $Text.IndexOf($Marker,$start+$Marker.Length,[StringComparison]::Ordinal) -ge 0){throw 'A process-thread acquisition member is missing or ambiguous.'}
        $opening=$Text.IndexOf('{',$start);if($opening -lt 0){throw 'A process-thread acquisition member has no body.'}
        $depth=0;$quoted=$false;$line=$false;$block=$false
        for($cursor=$opening;$cursor -lt $Text.Length;$cursor++) {
            $a=$Text[$cursor];$pair=if($cursor+1-lt$Text.Length){$Text.Substring($cursor,2)}else{''}
            if($line){if($a-eq"`n"){$line=$false}}
            elseif($block){if($pair-ceq'*/'){$block=$false;$cursor++}}
            elseif($quoted){if($a-eq'\'){$cursor++}elseif($a-eq'"'){$quoted=$false}}
            elseif($pair-ceq'//'){$line=$true;$cursor++}
            elseif($pair-ceq'/*'){$block=$true;$cursor++}
            elseif($a-eq'"'){$quoted=$true}
            elseif($a-eq'{'){$depth++}
            elseif($a-eq'}'){
                $depth--;if($depth-eq0){
                    if($Struct){
                        $attribute=$Text.LastIndexOf('[StructLayout',$start,[StringComparison]::Ordinal)
                        if($attribute-lt0-or$Text.Substring($attribute,$start-$attribute)-notmatch'^\[StructLayout\(LayoutKind.Sequential\)\]\s*$'){throw 'The actual PSS struct layout attribute was not preserved.'}
                        $start=$attribute
                    }
                    return $Text.Substring($start,$cursor-$start+1)
                }
            }
        }
        throw 'A process-thread acquisition member is unterminated.'
    }
    $marker='private static uint[] CurrentProcessThreadIds(out ProcessThreadSnapshot acquisition)'
    $method=Read-PssMember $sourceText $marker
    $receipt=Read-PssMember $sourceText 'public sealed class ProcessThreadSnapshot'
    $structs=@(foreach($name in @('PssFileTime','PssThreadInformation','PssThreadEntry')){Read-PssMember $sourceText ('private struct '+$name) $true}) -join "`n"
    $boundary=@'
public sealed class Outcome {
    public string name,error; public bool passed,rejected; public int markerFreeCalls,snapshotFreeCalls;
    public ProcessThreadSnapshot acquisition; public uint[] ids;
}
public static class Collector {
    sealed class Scenario {
        public uint count=2,captureCode,queryCode,markerCode,walkCode,terminalCode=259,markerFreeCode,snapshotFreeCode,contextLength;
        public bool captureNull,markerNull,markerFreeThrow,snapshotFreeThrow,contextNonzero;
        public uint[] tids=new uint[]{8,7};public uint pid=99;
    }
    static Scenario scenario;static int cursor,markerFreeCalls,snapshotFreeCalls;
    static IntPtr GetCurrentProcess(){return new IntPtr(11);}
    static uint GetCurrentProcessId(){return 99;}
    static uint GetCurrentThreadId(){return 7;}
    static void Need(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    static uint PssCaptureSnapshot(IntPtr process,uint flags,uint contextFlags,out IntPtr snapshot){
        Need(process==new IntPtr(11)&&flags==0x80&&contextFlags==0,"Wrong actual capture boundary arguments");
        snapshot=scenario.captureNull?IntPtr.Zero:new IntPtr(22);return scenario.captureCode;
    }
    static uint PssQuerySnapshot(IntPtr snapshot,uint informationClass,out PssThreadInformation information,uint length){
        Need(snapshot==new IntPtr(22)&&informationClass==5&&length==8,"Wrong actual query boundary arguments");
        information=new PssThreadInformation{threadsCaptured=scenario.count,contextLength=scenario.contextLength};return scenario.queryCode;
    }
    static uint PssWalkMarkerCreate(IntPtr allocator,out IntPtr marker){Need(allocator==IntPtr.Zero,"Unexpected custom allocator");marker=scenario.markerNull?IntPtr.Zero:new IntPtr(33);return scenario.markerCode;}
    static uint PssWalkSnapshot(IntPtr snapshot,uint informationClass,IntPtr marker,out PssThreadEntry entry,uint length){
        Need(snapshot==new IntPtr(22)&&informationClass==3&&marker==new IntPtr(33)&&length==Marshal.SizeOf(typeof(PssThreadEntry)),"Wrong actual walk boundary arguments");
        entry=new PssThreadEntry();if(scenario.walkCode!=0)return scenario.walkCode;
        if(cursor>=scenario.tids.Length)return scenario.terminalCode;
        entry.processId=scenario.pid;entry.threadId=scenario.tids[cursor++];
        if(scenario.contextNonzero){entry.contextRecord=new IntPtr(0xFEED);entry.sizeOfContextRecord=64;}
        return 0;
    }
    static uint PssWalkMarkerFree(IntPtr marker){markerFreeCalls++;Need(marker==new IntPtr(33),"Wrong marker freed");if(scenario.markerFreeThrow)throw new InvalidOperationException("Fake marker-free boundary error");return scenario.markerFreeCode;}
    static uint PssFreeSnapshot(IntPtr process,IntPtr snapshot){snapshotFreeCalls++;Need(process==new IntPtr(11)&&snapshot==new IntPtr(22),"Wrong owner/snapshot freed");if(scenario.snapshotFreeThrow)throw new InvalidOperationException("Fake snapshot-free boundary error");return scenario.snapshotFreeCode;}
    static Outcome Test(string name,Action<Scenario> mutate,bool reject,int markerCalls,int snapshotCalls){
        scenario=new Scenario();mutate(scenario);cursor=markerFreeCalls=snapshotFreeCalls=0;
        var result=new Outcome{name=name};ProcessThreadSnapshot acquisition=null;
        try{result.ids=CurrentProcessThreadIds(out acquisition);}catch(Exception error){result.rejected=true;result.error=error.ToString();}
        result.acquisition=acquisition;result.markerFreeCalls=markerFreeCalls;result.snapshotFreeCalls=snapshotFreeCalls;
        result.passed=result.rejected==reject&&markerFreeCalls==markerCalls&&snapshotFreeCalls==snapshotCalls&&acquisition!=null&&acquisition.succeeded==!reject;
        if(!reject)result.passed=result.passed&&result.ids.SequenceEqual(new uint[]{7,8})&&acquisition.released&&acquisition.walkEndCode==259&&acquisition.currentThreadPresent&&acquisition.entryBytes==(IntPtr.Size==8?120:92);
        return result;
    }
    public static Outcome[] Run(){return new[]{
        Test("actual managed layout, flags, count, sorted IDs and owned release",s=>{},false,1,1),
        Test("unrequested context fields are observed without zero assumption",s=>{s.contextNonzero=true;s.contextLength=4096;},false,1,1),
        Test("zero captured count is rejected",s=>s.count=0,true,0,1),
        Test("zero thread ID is rejected",s=>s.tids=new uint[]{7,0},true,1,1),
        Test("foreign process ID is rejected",s=>s.pid=100,true,1,1),
        Test("duplicate thread ID is rejected",s=>s.tids=new uint[]{7,7},true,1,1),
        Test("missing current thread is rejected",s=>s.tids=new uint[]{8,9},true,1,1),
        Test("walk shorter than query is rejected",s=>s.count=3,true,1,1),
        Test("walk longer than query is rejected",s=>s.count=1,true,1,1),
        Test("terminal error is not interpreted as end of walk",s=>s.terminalCode=87,true,1,1),
        Test("capture error acquires no resource",s=>s.captureCode=5,true,0,0),
        Test("query error releases the snapshot",s=>s.queryCode=87,true,0,1),
        Test("marker creation error releases the snapshot",s=>s.markerCode=8,true,0,1),
        Test("walk error releases both resources",s=>s.walkCode=5,true,1,1),
        Test("success with null snapshot rejects without invalid free",s=>s.captureNull=true,true,0,0),
        Test("success with null marker rejects without invalid marker free",s=>s.markerNull=true,true,0,1),
        Test("marker free failure still attempts snapshot free",s=>s.markerFreeCode=6,true,1,1),
        Test("snapshot free failure is not success",s=>s.snapshotFreeCode=6,true,1,1),
        Test("marker free exception still attempts snapshot free",s=>s.markerFreeThrow=true,true,1,1),
        Test("snapshot free exception is not success",s=>s.snapshotFreeThrow=true,true,1,1)
    };}


'@
    $namespace='ProcessThreadBoundaryContracts'+[guid]::NewGuid().ToString('N')
    $managed="using System;using System.Collections.Generic;using System.ComponentModel;using System.Diagnostics;using System.Linq;using System.Runtime.InteropServices;namespace $namespace {`n"+$receipt+"`n"+$boundary+"`n"+$structs+"`n"+$method+"`n}}"
    if($managed-match'DllImport|\bextern\s'){throw 'Managed thread-acquisition contracts must not contain native imports.'}
    if(-not$managed.Contains($method)-or-not$managed.Contains($receipt)){throw 'Actual source acquisition boundaries were not preserved.'}
    $managedPath=Join-Path $Directory 'ProcessThread.Managed.cs'
    [IO.File]::WriteAllText($managedPath,$managed,[Text.UTF8Encoding]::new($false))
    $types=@(Add-Type -TypeDefinition $managed -PassThru)
    $collector=@($types|Where-Object FullName -ceq ($namespace+'.Collector'))
    if($collector.Count-ne1){throw 'The actual managed acquisition boundary did not compile.'}
    $checks=@($collector[0].GetMethod('Run').Invoke($null,@()))
    $extraction=@(foreach($trial in @('missing-method','duplicate-method','unterminated-body')) {
        $inputText=switch($trial){
            'missing-method' {$sourceText.Replace($marker,'RemovedBoundary')}
            'duplicate-method' {$sourceText+"`n"+$method}
            'unterminated-body' {$marker+' {'}
        }
        $rejected=$false;try{[void](Read-PssMember $inputText $marker)}catch{$rejected=$true}
        [ordered]@{name=$trial;passed=$rejected}
    })
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$methodSha=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($method)))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
    $result=[ordered]@{status=$(if($checks.Count-ne20-or@($checks|Where-Object passed -ne $true).Count-or@($extraction|Where-Object passed -ne $true).Count){'FAIL'}else{'PASS'});
        scope='Actual process-thread collector/receipt/structs with fake native API boundaries and actual managed Marshal layout; no native API call.';
        sourceSha256=(Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash;methodSha256=$methodSha;managedSource=$managedPath;checks=$checks;extractionChecks=$extraction}
    Write-GtJson (Join-Path $Directory 'Result.json') $result
    return $result
}

function Invoke-DesktopFocusBoundaryContract([string]$Source,[string]$Directory,$RuntimeAst,$DesktopAst,$GameTestAst) {
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

    $threadAcquisition=Invoke-ProcessThreadAcquisitionContracts $Source (Join-Path $Directory 'ProcessThreadAcquisition')
    if($threadAcquisition.status -ne 'PASS'){throw 'A process-thread acquisition contract failed.'}

    # BEGIN DATA-ONLY DESKTOP CONTRACTS
    $proofChecks=[Collections.Generic.List[object]]::new()
    function Add-DesktopProofCheck([string]$Name,[bool]$Passed) { $proofChecks.Add([ordered]@{name=$Name;status=$(if($Passed){'PASS'}else{'FAIL'})}) }
    Invoke-Expression (Read-ContractFunction $DesktopAst 'Test-VerificationNonInputDesktopEvidence')
function New-DesktopInputQuery { [UBVerification.BooleanQuery]@{succeeded=$true;errorCode=0;bytesNeeded=4;rawValue=0;value=$false} }
function New-DesktopProofFixture {
    $name='UBGameTest_00000000000000000000000000000000'
    $window=[UBVerification.OwnedWindowSnapshot]@{windowHandle=123;processId=99;nativeThreadId=7;desktop=$name;desktopQuerySucceeded=$true;
        identityQuerySucceeded=$true;clientRectQuerySucceeded=$true;clientWidth=640;clientHeight=480;visible=$true;inputQuery=(New-DesktopInputQuery)}
    $snapshot=[UBVerification.DesktopSnapshot]@{capturedUtc=[datetime]::UtcNow.ToString('O');observationSource='game-main-thread';windowStation='WinSta0';expectedDesktop=$name;runnerDesktop=$name;
        processId=99;nativeThreadId=7;currentDesktopQuerySucceeded=$true;windowStationQuerySucceeded=$true;runnerInputQuery=(New-DesktopInputQuery);runnerReceivesInput=$false;
        inputDesktop='Default';inputDesktopQuerySucceeded=$true;inputDesktopCloseSucceeded=$true;complete=$true;isolated=$true;nonInputIsolated=$true;
        ownedWindowsQuerySucceeded=$true;processThreadQuerySucceeded=$true;processThreadCohortStable=$true;processThreadIds=[uint32[]]@(7,8);processThreadIdsAfter=[uint32[]]@(7,8);
        ownedWindows=[UBVerification.OwnedWindowSnapshot[]]@($window);ownedUiThreads=[UBVerification.OwnedUiThreadSnapshot[]]@(
            [UBVerification.OwnedUiThreadSnapshot]@{nativeThreadId=7;enumerationReturned=$true;querySucceeded=$true;windowCount=1},
            [UBVerification.OwnedUiThreadSnapshot]@{nativeThreadId=8;enumerationReturned=$false;querySucceeded=$true;windowCount=0})}
    $snapshot.processUiScanAttempts=[UBVerification.ProcessUiScan[]]@([UBVerification.ProcessUiScan]@{attempt=1;querySucceeded=$true;cohortStable=$true;
        beforeThreadIds=$snapshot.processThreadIds;afterThreadIds=$snapshot.processThreadIdsAfter;threads=$snapshot.ownedUiThreads;windows=$snapshot.ownedWindows})
    return $snapshot
}
function Test-DesktopOwnershipProof([string]$Name,[scriptblock]$Mutation,[bool]$Expected,[Nullable[bool]]$ExpectedPowerShell=$null) {
    $snapshot=New-DesktopProofFixture;& $Mutation $snapshot
    $actual=[UBVerification.DesktopReader]::HasNonInputOwnershipProof($snapshot,'UBGameTest_00000000000000000000000000000000',99,7)
    $raw=$snapshot|ConvertTo-Json -Depth 30|ConvertFrom-Json
    $fromSaved=Test-VerificationNonInputDesktopEvidence -Snapshot $raw -ExpectedDesktop 'UBGameTest_00000000000000000000000000000000' -ExpectedProcessId 99 -ExpectedMainThreadId 7 -ExpectedWindowStation 'WinSta0'
    $expectedSaved=if($null-eq$ExpectedPowerShell){$Expected}else{[bool]$ExpectedPowerShell}
    Add-DesktopProofCheck $Name ($actual -eq $Expected -and $fromSaved -eq $expectedSaved)
}
Test-DesktopOwnershipProof 'complete known input identity' {param($s)} $true
Test-DesktopOwnershipProof 'remote access denied preserves own direct proof' {param($s)$s.inputDesktop=$null;$s.inputDesktopQuerySucceeded=$false;$s.inputDesktopCloseSucceeded=$false;$s.inputDesktopQueryError=5;$s.complete=$false;$s.isolated=$false} $true
Test-DesktopOwnershipProof 'another remote query error has identical independent proof semantics' {param($s)$s.inputDesktop=$null;$s.inputDesktopQuerySucceeded=$false;$s.inputDesktopQueryError=87;$s.complete=$false;$s.isolated=$false} $true
Test-DesktopOwnershipProof 'changed remote input name is preserved but is not own input' {param($s)$s.inputDesktop='OtherObservedDesktop'} $true
Test-DesktopOwnershipProof 'own input true is rejected' {param($s)$s.runnerInputQuery.rawValue=1;$s.runnerInputQuery.value=$true;$s.runnerReceivesInput=$true} $false
Test-DesktopOwnershipProof 'own query failed cannot be replaced by raw zero' {param($s)$s.runnerInputQuery.succeeded=$false} $false
Test-DesktopOwnershipProof 'own error despite success is rejected' {param($s)$s.runnerInputQuery.errorCode=5} $false
Test-DesktopOwnershipProof 'own null query is rejected' {param($s)$s.runnerInputQuery=$null} $false
Test-DesktopOwnershipProof 'own byte shortage is rejected' {param($s)$s.runnerInputQuery.bytesNeeded=0} $false
Test-DesktopOwnershipProof 'own invalid byte width is rejected' {param($s)$s.runnerInputQuery.bytesNeeded=8} $false
Test-DesktopOwnershipProof 'own contradictory raw true is rejected' {param($s)$s.runnerInputQuery.rawValue=1} $false
Test-DesktopOwnershipProof 'own contradictory boolean true is rejected' {param($s)$s.runnerInputQuery.value=$true} $false
Test-DesktopOwnershipProof 'own value unknown is rejected' {param($s)$s.runnerInputQuery.value=$null} $false
Test-DesktopOwnershipProof 'current desktop identity unknown is rejected' {param($s)$s.currentDesktopQuerySucceeded=$false} $false
Test-DesktopOwnershipProof 'actual private desktop mismatch is rejected' {param($s)$s.runnerDesktop='Default'} $false
Test-DesktopOwnershipProof 'declared private desktop mismatch is rejected' {param($s)$s.expectedDesktop='Wrong'} $false
Test-DesktopOwnershipProof 'process owner mismatch is rejected' {param($s)$s.processId=98} $false
Test-DesktopOwnershipProof 'main thread mismatch is rejected' {param($s)$s.nativeThreadId=8} $false
Test-DesktopOwnershipProof 'window station mismatch is independently rejected by saved evidence owner' {param($s)$s.windowStation='OtherStation'} $true $false
Test-DesktopOwnershipProof 'process thread query failure is rejected' {param($s)$s.processThreadQuerySucceeded=$false} $false
Test-DesktopOwnershipProof 'unresolved thread churn is rejected' {param($s)$s.processThreadCohortStable=$false} $false
Test-DesktopOwnershipProof 'new unobserved thread is rejected' {param($s)$s.processThreadIdsAfter=[uint32[]]@(7,8,9)} $false
Test-DesktopOwnershipProof 'duplicate thread identity is rejected' {param($s)$s.processThreadIds=[uint32[]]@(7,7);$s.processThreadIdsAfter=[uint32[]]@(7,7)} $false
Test-DesktopOwnershipProof 'missing UI thread enumeration record is rejected' {param($s)$s.ownedUiThreads=[UBVerification.OwnedUiThreadSnapshot[]]@($s.ownedUiThreads[0])} $false
Test-DesktopOwnershipProof 'UI thread enumeration failed is rejected' {param($s)$s.ownedUiThreads[1].querySucceeded=$false} $false
Test-DesktopOwnershipProof 'UI thread callback failure is rejected' {param($s)$s.ownedUiThreads[1].error='unavailable'} $false
Test-DesktopOwnershipProof 'empty enumeration with error is rejected' {param($s)$s.ownedUiThreads[1].errorCode=5} $false
Test-DesktopOwnershipProof 'documented no-window thread remains represented' {param($s)} $true
Test-DesktopOwnershipProof 'false enumeration with callbacks is contradictory' {param($s)$s.ownedUiThreads[0].enumerationReturned=$false} $false
Test-DesktopOwnershipProof 'completed true enumeration of an empty thread is valid' {param($s)$s.ownedUiThreads[1].enumerationReturned=$true} $true
Test-DesktopOwnershipProof 'window on a different desktop is rejected' {param($s)$s.ownedWindows[0].desktop='DifferentPrivateDesktop'} $false
Test-DesktopOwnershipProof 'foreign window owner is rejected' {param($s)$s.ownedWindows[0].processId=98} $false
Test-DesktopOwnershipProof 'window owner query failure is rejected' {param($s)$s.ownedWindows[0].identityQuerySucceeded=$false} $false
Test-DesktopOwnershipProof 'window desktop query failure is rejected' {param($s)$s.ownedWindows[0].desktopQuerySucceeded=$false} $false
Test-DesktopOwnershipProof 'window input query failure is rejected' {param($s)$s.ownedWindows[0].inputQuery.succeeded=$false} $false
Test-DesktopOwnershipProof 'window input true is rejected' {param($s)$s.ownedWindows[0].inputQuery.rawValue=1;$s.ownedWindows[0].inputQuery.value=$true} $false
Test-DesktopOwnershipProof 'window client area query failure is rejected' {param($s)$s.ownedWindows[0].clientRectQuerySucceeded=$false} $false
Test-DesktopOwnershipProof 'missing primary UI window is rejected' {param($s)$s.ownedWindows[0].clientWidth=0;$s.ownedWindows[0].clientHeight=0} $false
Test-DesktopOwnershipProof 'window count mismatch is rejected' {param($s)$s.ownedUiThreads[0].windowCount=2} $false
Test-DesktopOwnershipProof 'duplicate window is rejected' {param($s)$s.ownedWindows=[UBVerification.OwnedWindowSnapshot[]]@($s.ownedWindows[0],$s.ownedWindows[0]);$s.ownedUiThreads[0].windowCount=2} $false
Test-DesktopOwnershipProof 'window from unobserved process thread is rejected' {param($s)$s.ownedWindows[0].nativeThreadId=9} $false
Test-DesktopOwnershipProof 'saved declared failed proof is not trusted as a pass' {param($s)$s.nonInputIsolated=$false} $true $false
Test-DesktopOwnershipProof 'missing scan history cannot claim complete acquisition' {param($s)$s.processUiScanAttempts=$null} $false
Test-DesktopOwnershipProof 'safe thread-cohort retry retains both acquisition attempts' {param($s)
    $prior=(New-DesktopProofFixture).processUiScanAttempts[0];$prior.afterThreadIds=[uint32[]]@(7,8,9);$prior.cohortStable=$false
    $last=$s.processUiScanAttempts[0];$last.attempt=2;$s.processUiScanAttempts=[UBVerification.ProcessUiScan[]]@($prior,$last)
} $true
Test-DesktopOwnershipProof 'earlier owned input true is never hidden by later stable acquisition' {param($s)
    $prior=(New-DesktopProofFixture).processUiScanAttempts[0];$prior.afterThreadIds=[uint32[]]@(7,8,9);$prior.cohortStable=$false;$prior.windows[0].inputQuery.rawValue=1;$prior.windows[0].inputQuery.value=$true
    $last=$s.processUiScanAttempts[0];$last.attempt=2;$s.processUiScanAttempts=[UBVerification.ProcessUiScan[]]@($prior,$last)
} $false
Test-DesktopOwnershipProof 'earlier owned window query failure is never hidden by later stable acquisition' {param($s)
    $prior=(New-DesktopProofFixture).processUiScanAttempts[0];$prior.afterThreadIds=[uint32[]]@(7,8,9);$prior.cohortStable=$false;$prior.windows[0].desktopQuerySucceeded=$false
    $last=$s.processUiScanAttempts[0];$last.attempt=2;$s.processUiScanAttempts=[UBVerification.ProcessUiScan[]]@($prior,$last)
} $false
Test-DesktopOwnershipProof 'earlier enumeration failure is never hidden by later stable acquisition' {param($s)
    $prior=(New-DesktopProofFixture).processUiScanAttempts[0];$prior.afterThreadIds=[uint32[]]@(7,8,9);$prior.cohortStable=$false;$prior.threads[1].querySucceeded=$false
    $last=$s.processUiScanAttempts[0];$last.attempt=2;$s.processUiScanAttempts=[UBVerification.ProcessUiScan[]]@($prior,$last)
} $false

$modeEnd=$RuntimeAst.Extent.Text.IndexOf('$repoRoot =')
$beforeMode=$RuntimeAst.EndBlock.Statements | Where-Object {
    if ($_.Extent.EndOffset -ge $modeEnd) { return $false }
    if ($_ -is [Management.Automation.Language.IfStatementAst]) { return $true }
    if ($_ -is [Management.Automation.Language.AssignmentStatementAst]) { return $_.Left.Extent.Text -ceq '$requirePrivateDesktop' }
    return $false
}
$modeBody=($beforeMode|ForEach-Object{$_.Extent.Text})-join[Environment]::NewLine
foreach($case in @(
    @{name='strict';strict=$true;direct=$false;background=$true;show=$false;startup=$false;action=$true;reject=$false},
    @{name='direct';strict=$false;direct=$true;background=$true;show=$false;startup=$false;action=$true;reject=$false},
    @{name='both';strict=$true;direct=$true;background=$true;show=$false;startup=$false;action=$false;reject=$true},
    @{name='direct visible';strict=$false;direct=$true;background=$true;show=$true;startup=$false;action=$false;reject=$true},
    @{name='direct startup';strict=$false;direct=$true;background=$true;show=$false;startup=$true;action=$false;reject=$true},
    @{name='direct without background';strict=$false;direct=$true;background=$false;show=$false;startup=$false;action=$false;reject=$true},
    @{name='action without private mode';strict=$false;direct=$false;background=$true;show=$false;startup=$false;action=$true;reject=$true}
)) {
    $rejected=$false
    & {param($case,$body) $RequireOtherAppForeground=$case.strict;$RequireNonInputDesktop=$case.direct;$RunInBackground=$case.background;$ShowGameWindow=$case.show;$StartupOnly=$case.startup;$RequireNativeActionProof=$case.action
        $rejected=$false;try{. ([scriptblock]::Create($body))}catch{$rejected=$true};Add-DesktopProofCheck ('mode preflight: '+$case.name) ($rejected-eq$case.reject)
    } $case $modeBody
}

# Execute the actual terminal history consumer, not a second implementation of it.
$historyAssignment=@($RuntimeAst.FindAll({param($n)$n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$unverifiedSamples'},$true))
$historyGuard=@($RuntimeAst.FindAll({param($n)$n -is [Management.Automation.Language.IfStatementAst] -and $n.Extent.Text.Contains("throw 'The game desktop history contains missing or unsuccessful acquisition.'")},$true)|Where-Object{$_.Extent.Text.StartsWith('if ($desktopSamples.Count')})
if($historyAssignment.Count-ne1-or$historyGuard.Count-ne1){throw 'Terminal desktop history consumer owner is not unique.'}
$historyBody=$historyAssignment[0].Extent.Text+[Environment]::NewLine+$historyGuard[0].Extent.Text
foreach($case in @(
    @{name='known ownership';mutate={param($s)};direct=$true;strict=$true},
    @{name='unknown remote identity';mutate={param($s)$s.inputDesktopQuerySucceeded=$false;$s.inputDesktop=$null;$s.inputDesktopQueryError=5;$s.complete=$false;$s.isolated=$false};direct=$true;strict=$false},
    @{name='changed remote identity';mutate={param($s)$s.inputDesktop='Other'};direct=$true;strict=$false},
    @{name='own input counterexample';mutate={param($s)$s.runnerInputQuery.rawValue=1;$s.runnerInputQuery.value=$true};direct=$false;strict=$false},
    @{name='unknown own thread coverage';mutate={param($s)$s.processThreadCohortStable=$false};direct=$false;strict=$false},
    @{name='wrong observation source';mutate={param($s)$s.observationSource='unknown'};direct=$false;strict=$false}
)) {
    foreach($strictMode in @($false,$true)) {
        & {
            param($case,$strictMode,$body)
            $sample=New-DesktopProofFixture;& $case.mutate $sample
            $desktopSamples=@($sample|ConvertTo-Json -Depth 30|ConvertFrom-Json);$RequireOtherAppForeground=$strictMode
            $expectedDesktopName='UBGameTest_00000000000000000000000000000000'
            $result=[pscustomobject]@{processId=99;foreground=[pscustomobject]@{primaryThreadId=7};desktop=[pscustomobject]@{launchWindowStation='WinSta0';before=[pscustomobject]@{inputDesktop='Default'}}}
            $rejected=$false;try{. ([scriptblock]::Create($body))}catch{$rejected=$true}
            $expected=if($strictMode){$case.strict}else{$case.direct}
            Add-DesktopProofCheck ('actual terminal history '+$case.name+' strict='+$strictMode) ((-not$rejected)-eq$expected)
        } $case $strictMode $historyBody
    }
}
foreach($strictMode in @($false,$true)) {
    & {param($strictMode,$body)
        $desktopSamples=@();$RequireOtherAppForeground=$strictMode;$rejected=$false
        try{. ([scriptblock]::Create($body))}catch{$rejected=$true}
        Add-DesktopProofCheck ('empty terminal history is rejected strict='+$strictMode) $rejected
    } $strictMode $historyBody
}

$launchParameters=@($GameTestAst.FindAll({param($n)$n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$parameters' -and $n.Extent.Text.Contains('RunName=$case.id')},$true))
if($launchParameters.Count-ne1){throw 'Game test runtime mode argument owner is not unique.'}
foreach($mode in @('default','direct','visual')) {
    & {param($mode,$body)
        $VisualAcceptance=$mode-eq'visual';$RequireNonInputDesktop=$mode-eq'direct'
        $case=[pscustomobject]@{id='generic';timeoutSeconds=30};$environment=[pscustomobject]@{gameExe='C:\Dummy\Game.exe'};$runtimeRoot='C:\Dummy\Runtime'
        $adapter=[pscustomobject]@{saveScenario=$true};$build=[pscustomobject]@{manifest=@([pscustomobject]@{path='SolastaUnfinishedBusiness.dll';sha256=('0'*64)})}
        . ([scriptblock]::Create($body))
        Add-DesktopProofCheck ('actual game-test launch selection '+$mode) ($parameters.RequireOtherAppForeground-eq($mode-eq'default')-and
            $parameters.RequireNonInputDesktop-eq($mode-eq'direct')-and$parameters.RequireNativeActionProof-eq($mode-ne'visual'))
    } $mode $launchParameters[0].Extent.Text
}

    # END DATA-ONLY DESKTOP CONTRACTS

    function Test-UnchangedHostForeground($Before,$After) {
        foreach($sample in @($Before,$After)) {
            if($null -eq $sample){return $false}
            foreach($field in @('windowHandle','processId','nativeThreadId')) {
                if($null -eq $sample.PSObject.Properties[$field] -or $null -eq $sample.$field){return $false}
            }
            if($sample.windowHandle -eq 0) {
                if($sample.processId -ne 0 -or $sample.nativeThreadId -ne 0){return $false}
            } elseif($sample.processId -le 0 -or $sample.nativeThreadId -le 0){return $false}
        }
        return $Before.windowHandle -eq $After.windowHandle -and
            $Before.processId -eq $After.processId -and $Before.nativeThreadId -eq $After.nativeThreadId
    }
    $foregroundTrials=@(foreach($mode in @('window-unchanged','none-unchanged','window-changed','owner-changed','none-with-owner','missing-owner')) {
        $first=[pscustomobject]@{windowHandle=123;processId=456;nativeThreadId=789}
        $last=[pscustomobject]@{windowHandle=123;processId=456;nativeThreadId=789}
        switch($mode) {
            'none-unchanged' {$first.windowHandle=0;$first.processId=0;$first.nativeThreadId=0;$last.windowHandle=0;$last.processId=0;$last.nativeThreadId=0}
            'window-changed' {$last.windowHandle=124}
            'owner-changed' {$last.processId=457}
            'none-with-owner' {$first.windowHandle=0;$last.windowHandle=0}
            'missing-owner' {$first.processId=$null;$last.processId=$null}
        }
        $expected=$mode -in @('window-unchanged','none-unchanged')
        $accepted=Test-UnchangedHostForeground $first $last
        [ordered]@{mode=$mode;expected=$expected;accepted=$accepted;passed=($accepted -eq $expected)}
    })

    function Get-ObservedFocusHostKind($Snapshot) {
        if($null -eq $Snapshot){return $null}
        foreach($field in @('currentDesktopQuerySucceeded','currentDesktopQueryError','windowStationQuerySucceeded','windowStationQueryError',
                'inputDesktopQuerySucceeded','inputDesktopQueryError','inputDesktopCloseSucceeded','inputDesktopCloseError',
                'runnerDesktop','inputDesktop','windowStation','processId','nativeThreadId','runnerInputQuery','runnerReceivesInput','guiThread')) {
            if($null -eq $Snapshot.PSObject.Properties[$field] -or $null -eq $Snapshot.$field){return $null}
        }
        $query=$Snapshot.runnerInputQuery;$gui=$Snapshot.guiThread
        foreach($field in @('succeeded','errorCode','bytesNeeded','rawValue','value')) {
            if($null -eq $query.PSObject.Properties[$field] -or $null -eq $query.$field){return $null}
        }
        foreach($field in @('querySucceeded','queryError','nativeThreadId','activeWindow','focusWindow')) {
            if($null -eq $gui.PSObject.Properties[$field] -or $null -eq $gui.$field){return $null}
        }
        foreach($field in @('currentDesktopQuerySucceeded','windowStationQuerySucceeded','inputDesktopQuerySucceeded','inputDesktopCloseSucceeded')) {
            if($Snapshot.$field -isnot [bool]){return $null}
        }
        if($Snapshot.runnerDesktop -isnot [string] -or $Snapshot.inputDesktop -isnot [string] -or
            $Snapshot.windowStation -isnot [string] -or $query.succeeded -isnot [bool] -or $gui.querySucceeded -isnot [bool]){return $null}
        if($Snapshot.currentDesktopQuerySucceeded -ne $true -or $Snapshot.currentDesktopQueryError -ne 0 -or
            $Snapshot.windowStationQuerySucceeded -ne $true -or $Snapshot.windowStationQueryError -ne 0 -or
            $Snapshot.inputDesktopQuerySucceeded -ne $true -or $Snapshot.inputDesktopQueryError -ne 0 -or
            $Snapshot.inputDesktopCloseSucceeded -ne $true -or $Snapshot.inputDesktopCloseError -ne 0 -or
            [string]::IsNullOrEmpty($Snapshot.runnerDesktop) -or [string]::IsNullOrEmpty($Snapshot.inputDesktop) -or
            [string]::IsNullOrEmpty($Snapshot.windowStation) -or $Snapshot.processId -le 0 -or $Snapshot.nativeThreadId -le 0 -or
            $query.succeeded -ne $true -or $query.errorCode -ne 0 -or $query.bytesNeeded -ne 4 -or
            ($query.rawValue -isnot [int] -and $query.rawValue -isnot [long]) -or $query.rawValue -notin @(0,1) -or
            $query.value -isnot [bool] -or $Snapshot.runnerReceivesInput -isnot [bool] -or
            $query.value -ne ($query.rawValue -eq 1) -or $Snapshot.runnerReceivesInput -ne $query.value -or
            $gui.querySucceeded -ne $true -or $gui.queryError -ne 0 -or $gui.nativeThreadId -ne $Snapshot.nativeThreadId){return $null}
        $same=[string]::Equals($Snapshot.runnerDesktop,$Snapshot.inputDesktop,[StringComparison]::OrdinalIgnoreCase)
        if($query.rawValue -eq 1 -and $same){return 'INPUT_HOST'}
        if($query.rawValue -eq 0 -and -not $same -and $Snapshot.runnerDesktop -cnotmatch '^UBGameTest_[a-f0-9]{32}$'){return 'NON_INPUT_HOST'}
        return $null
    }
    function Test-SameObservedFocusHost($Before,$After,[string]$Kind) {
        if(-not $Kind -or (Get-ObservedFocusHostKind $Before) -cne $Kind -or
            (Get-ObservedFocusHostKind $After) -cne $Kind){return $false}
        foreach($field in @('runnerDesktop','inputDesktop','windowStation','processId','nativeThreadId','runnerReceivesInput')) {
            if($Before.$field -cne $After.$field){return $false}
        }
        foreach($field in @('activeWindow','focusWindow','nativeThreadId')) {
            if($Before.guiThread.$field -ne $After.guiThread.$field){return $false}
        }
        return $Before.runnerInputQuery.rawValue -eq $After.runnerInputQuery.rawValue -and
            $Before.runnerInputQuery.value -eq $After.runnerInputQuery.value
    }

    $before=[UBVerification.DesktopReader]::ReadCurrent($null,'contract-host/before')
    $foregroundBefore=[UBVerification.DesktopReader]::ReadForeground()
    $hostKind=Get-ObservedFocusHostKind $before
    if (-not $hostKind -or -not (Test-UnchangedHostForeground $foregroundBefore $foregroundBefore)) {
        Write-GtJson (Join-Path $Directory 'HostPrerequisite.json') @{desktop=$before;foreground=$foregroundBefore}
        throw 'The desktop focus contract requires known current/input/UOI/GUI and foreground observations from an unowned host.'
    }
    $hostObservationTrials=@(foreach($mode in @('input-control','non-input-control','missing-uoi','failed-uoi','short-uoi','unknown-raw','contradictory-bool','missing-input-name','failed-gui','wrong-gui-thread','input-name-with-raw-zero','owned-name')) {
        $sample=$before|ConvertTo-Json -Depth 30|ConvertFrom-Json
        $sample.runnerDesktop='ObservedHost';$sample.inputDesktop='ObservedInput';$sample.runnerInputQuery.rawValue=0
        $sample.runnerInputQuery.value=$false;$sample.runnerReceivesInput=$false
        switch($mode) {
            'input-control' {$sample.runnerDesktop=$sample.inputDesktop;$sample.runnerInputQuery.rawValue=1;$sample.runnerInputQuery.value=$true;$sample.runnerReceivesInput=$true}
            'missing-uoi' {$sample.runnerInputQuery=$null}
            'failed-uoi' {$sample.runnerInputQuery.succeeded=$false}
            'short-uoi' {$sample.runnerInputQuery.bytesNeeded=0}
            'unknown-raw' {$sample.runnerInputQuery.rawValue=$null}
            'contradictory-bool' {$sample.runnerInputQuery.value=$true}
            'missing-input-name' {$sample.inputDesktop=$null}
            'failed-gui' {$sample.guiThread.querySucceeded=$false}
            'wrong-gui-thread' {$sample.guiThread.nativeThreadId=0}
            'input-name-with-raw-zero' {$sample.runnerDesktop=$sample.inputDesktop}
            'owned-name' {$sample.runnerDesktop='UBGameTest_00000000000000000000000000000000'}
        }
        $expected=switch($mode){'input-control' {'INPUT_HOST'};'non-input-control' {'NON_INPUT_HOST'};default {$null}}
        $kind=Get-ObservedFocusHostKind $sample
        [ordered]@{mode=$mode;expected=$expected;observed=$kind;passed=($kind -ceq $expected)}
    })
    $rejection=$null
    try { [UBVerification.DesktopReader]::DeactivateCurrentWindow(1,1) }
    catch { $rejection=$_.Exception.GetBaseException() }
    $foregroundAfter=[UBVerification.DesktopReader]::ReadForeground()
    $after=[UBVerification.DesktopReader]::ReadCurrent($null,'contract-host/after')
    $receipt=$after.focusActivation
    $checks=[Collections.Generic.List[object]]::new()
    $checks.Add([ordered]@{name='actual unowned host entry rejected before activation';passed=(
        $null -ne $rejection -and $rejection -is [InvalidOperationException] -and
        $rejection.Message -ceq 'Focus verification requires the owned non-input desktop.' -and
        $null -ne $receipt -and -not $receipt.attempted -and -not $receipt.completed -and
        $null -eq $receipt.after -and [bool]$receipt.error)})
    $checks.Add([ordered]@{name='rejection retains actual host desktop UOI and GUI acquisition';passed=(
        $null -ne $receipt -and (Test-SameObservedFocusHost $before $receipt.before $hostKind))})
    $checks.Add([ordered]@{name='host foreground PID thread and window unchanged';passed=(
        Test-UnchangedHostForeground $foregroundBefore $foregroundAfter)})
    $checks.Add([ordered]@{name='host observation does not claim or acquire complete process UI coverage';passed=(
        -not $before.processThreadQuerySucceeded -and -not $after.processThreadQuerySucceeded -and
        $null -eq $before.processThreadIds -and $null -eq $after.processThreadIds -and
        $null -eq $before.processUiScanAttempts -and $null -eq $after.processUiScanAttempts -and
        -not $before.nonInputIsolated -and -not $after.nonInputIsolated)})
    $checks.Add([ordered]@{name='host desktop UOI and GUI remain unchanged without a created peer';passed=(
        (Test-SameObservedFocusHost $before $after $hostKind) -and $peer.GetValue($null) -eq [IntPtr]::Zero)})

    # An invalid handle exercises the real Win32 failure, without creating or switching a desktop.
    $failedInput=$readInput.Invoke($null,[object[]]@([IntPtr]::Zero))
    $checks.Add([ordered]@{name='actual failed UOI query retains unknown BOOL';passed=(
        -not $failedInput.succeeded -and
        $null -eq $failedInput.rawValue -and $null -eq $failedInput.value)})
    $trials=@(foreach ($mode in @('declared-valid-control','actual-invalid-handle','failed-with-false-output','short-bool-output','missing-query','incomplete-owned-coverage','remote-name-unavailable-control')) {
        $snapshot=New-DesktopProofFixture
        $snapshot.guiThread=[UBVerification.GuiThreadSnapshot]::new();$snapshot.guiThread.querySucceeded=$true
        switch ($mode) {
            'actual-invalid-handle' { $snapshot.runnerInputQuery=$failedInput }
            'failed-with-false-output' { $snapshot.runnerInputQuery.succeeded=$false; $snapshot.runnerInputQuery.errorCode=6 }
            'short-bool-output' { $snapshot.runnerInputQuery.bytesNeeded=0 }
            'missing-query' { $snapshot.runnerInputQuery=$null }
            'incomplete-owned-coverage' { $snapshot.processThreadQuerySucceeded=$false }
            'remote-name-unavailable-control' { $snapshot.complete=$false;$snapshot.isolated=$false;$snapshot.inputDesktop=$null;$snapshot.inputDesktopQuerySucceeded=$false;$snapshot.inputDesktopQueryError=5 }
        }
        $accepted=$false; $failure=$null
        try { [void]$requireDesktop.Invoke($null,[object[]]@($snapshot)); $accepted=$true }
        catch { $failure=$_.Exception.GetBaseException().Message }
        $expectedAcceptance=$mode -in @('declared-valid-control','remote-name-unavailable-control')
        [ordered]@{mode=$mode;accepted=$accepted;failure=$failure;passed=(
            $accepted -eq $expectedAcceptance -and ($expectedAcceptance -or
                $failure -ceq 'Focus verification requires the owned non-input desktop.'))}
    })
    [ordered]@{
        status=$(if (@($proofChecks | Where-Object status -ne 'PASS').Count -or @($checks | Where-Object passed -eq $false).Count -or @($trials | Where-Object passed -eq $false).Count -or @($foregroundTrials | Where-Object passed -eq $false).Count -or @($hostObservationTrials | Where-Object passed -eq $false).Count) {'FAIL'}else{'PASS'})
        scope='Compiled actual desktop source: real unowned-host refusal before activation, current/input/UOI/GUI and foreground readback, and invalid-handle UOI failure. Declared snapshots separately exercise the guard; no desktop creation, activation, input or game launch.'
        assembly=$assembly;threadAcquisition=$threadAcquisition;ownershipProofCases=@($proofChecks.ToArray());checks=@($checks.ToArray());guardTrials=$trials;foregroundObservationTrials=$foregroundTrials;hostObservationTrials=$hostObservationTrials
        hostKind=$hostKind;hostBefore=$before;hostAfter=$after;foregroundBefore=$foregroundBefore;foregroundAfter=$foregroundAfter
        hostForegroundStateBefore=$(if($foregroundBefore.windowHandle -eq 0){'NONE'}else{'WINDOW'})
        hostForegroundStateAfter=$(if($foregroundAfter.windowHandle -eq 0){'NONE'}else{'WINDOW'})
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
        $adapterFunction=(Get-Command Get-GtAdapter -CommandType Function).ScriptBlock.Ast
        $dispatch=@($adapterFunction.FindAll({param($n) $n -is [Management.Automation.Language.SwitchStatementAst]},$true))
        $registered=@(if($dispatch.Count -eq 1){$dispatch[0].Clauses | ForEach-Object {$_.Item1.Value} | Sort-Object})
        $builtInDeclaration=@($adapterFunction.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$builtIn'},$true))
        $declaredNames=@(if($builtInDeclaration.Count -eq 1){$builtInDeclaration[0].Right.FindAll({param($n) $n -is [Management.Automation.Language.StringConstantExpressionAst]},$true) | ForEach-Object {$_.Value} | Sort-Object})
        $environmentOnly=($registered.Count -eq 2 -and ($registered -join ',') -ceq 'save-smoke,startup' -and ($declaredNames -join ',') -ceq 'save-smoke,startup')
        $records+=@([ordered]@{mode='shared-adapter-ownership';status=$(if($environmentOnly){'PASS'}else{'FAIL'});checks=@([ordered]@{name='only startup and save-smoke are reserved environment adapters';passed=$environmentOnly})})
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
        'tools/runtime-verification/EvidencePaths.ps1',
        'tools/runtime-verification/Invoke-RuntimeVerification.ps1',
        'tools/runtime-verification/Invoke-SolastaSemiAutoTest.ps1',
        'tools/runtime-verification/Probes/PresentationCapture.cs',
        'tools/game-test/GameTest.Common.ps1',
        'tools/game-test/New-GameTestBuild.ps1',
        'tools/game-test/Clear-GameTestArtifacts.ps1',
        'tools/game-test/Invoke-GameTest.ps1',
        'tools/game-test/Review-PresentationEvidence.ps1',
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
    foreach($policy in @(@{relative='.gitignore';archive='Root.gitignore'},@{relative='tools/game-test/.gitignore';archive='GameTest.gitignore'})) {
        $source=Join-Path $script:GameTestRepo $policy.relative
        $hash=Get-GtHash $source
        $archive=Join-Path $run ('InputSources/'+$policy.archive)
        Copy-GtFile $source $archive -ExpectedSha256 $hash
        $archived[$policy.relative]=$archive
        $result.sources+=[ordered]@{path=$policy.relative;sha256=$hash;archivePath=$archive}
    }
    $result.sourceTracking=Invoke-SourceTrackingContracts $archived['.gitignore'] $archived['tools/game-test/.gitignore'] (Join-Path $run 'SourceTracking')
    $desktop=Read-ContractSource $archived['tools/runtime-verification/WindowsDesktop.ps1']
    $display=Read-ContractSource $archived['tools/runtime-verification/NativeDisplaySettings.ps1']
    $runtime=Read-ContractSource $archived['tools/runtime-verification/Invoke-RuntimeVerification.ps1']
    $result.displayCleanup=Invoke-DisplayCleanupContracts $runtime (Join-Path $run 'DisplayCleanup')
    $result.assemblyReferences=Invoke-AssemblyReferenceContracts $runtime (Join-Path $run 'AssemblyReferences')
    $result.foregroundObservation=Invoke-ForegroundObservationContract $runtime (Join-Path $run 'ForegroundObservation')
    if ($result.foregroundObservation.status -ne 'PASS') { throw 'A foreground observation contract failed.' }
    $common=Read-ContractSource $archived['tools/game-test/GameTest.Common.ps1']
    $result.persistentSync=Invoke-PersistentSyncContracts (Read-ContractSource $archived['tools/runtime-verification/Invoke-SolastaSemiAutoTest.ps1']) $desktop (Join-Path $run 'PersistentSync')
    $result.nativeEvidencePaths=Invoke-NativeEvidencePathContracts $archived['tools/runtime-verification/EvidencePaths.ps1'] $common $runtime (Join-Path $run 'NativeEvidencePaths')
    $result.packageManifest=Invoke-PackageManifestContracts (Join-Path $run 'PackageManifest')
    $buildScript=Read-ContractSource $archived['tools/game-test/New-GameTestBuild.ps1']
    $result.buildDiagnostics=Invoke-BuildDiagnosticContracts $buildScript (Join-Path $run 'BuildDiagnostics')
    $result.sourceIdentity=Invoke-SourceIdentityContracts $common (Join-Path $run 'SourceIdentity')
    $result.modLoadPlan=Invoke-ModLoadPlanContracts $runtime (Join-Path $run 'ModLoadPlan')
    $result.nativeUserData=Invoke-NativeUserDataContracts $runtime (Join-Path $run 'NativeUserData')
    $result.presentationReview=Invoke-PresentationReviewContracts $archived['tools/game-test/Review-PresentationEvidence.ps1'] (Read-ContractSource $archived['tools/game-test/Invoke-GameTest.ps1']) (Join-Path $run 'PresentationReview')
    $result.presentationWriter=Invoke-PresentationWriterContracts $archived['tools/runtime-verification/Probes/PresentationCapture.cs'] $archived['tools/game-test/Review-PresentationEvidence.ps1'] (Join-Path $run 'PresentationWriter')
    [void](Read-ContractSource $archived['tools/game-test/Clear-GameTestArtifacts.ps1'])
    $result.storageCleanup=Invoke-StorageCleanupContracts $archived (Join-Path $run 'StorageCleanup')
    $result.desktopFocusBoundary=Invoke-DesktopFocusBoundaryContract $archived['tools/runtime-verification/WindowsDesktop.cs'] (Join-Path $run 'DesktopFocusBoundary') $runtime $desktop (Read-ContractSource $archived['tools/game-test/Invoke-GameTest.ps1'])
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
        Read-ContractFunction $desktop 'Assert-VerificationGameStopped'
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
    } elseif (@($result.processGuards | Where-Object status -ne 'PASS').Count -or $result.processLifecycle.status -eq 'FAIL' -or $result.nativeActionProof.status -eq 'FAIL' -or $result.caseValidation.status -eq 'FAIL' -or $result.compilationBoundary.status -eq 'FAIL' -or $result.displayCleanup.status -ne 'PASS' -or $result.assemblyReferences.status -ne 'PASS' -or $result.desktopFocusBoundary.status -ne 'PASS' -or $result.storageCleanup.status -ne 'PASS' -or $result.packageManifest.status -ne 'PASS' -or $result.buildDiagnostics.status -ne 'PASS' -or $result.sourceIdentity.status -ne 'PASS' -or $result.modLoadPlan.status -ne 'PASS' -or $result.nativeUserData.status -ne 'PASS' -or $result.presentationReview.status -ne 'PASS' -or $result.nativeEvidencePaths.status -ne 'PASS' -or $result.sourceTracking.status -ne 'PASS' -or $result.persistentSync.status -ne 'PASS' -or $result.presentationWriter.status -ne 'PASS') {
        $result.status='FAIL'; $result.reason='A game-test contract failed.'
    } elseif ($SkipProcessLifecycle) {
        $result.processLifecycle=[ordered]@{status='SKIPPED';reason='Skipped by -SkipProcessLifecycle.'}
        $result.status='PASS'; $result.reason="All 30 process guard contracts, $($compileCases.Count) compilation boundary contracts, $($result.assemblyReferences.cases.Count) assembly reference contracts, display cleanup contracts, $($proofCases.Count) native action proof contracts, $($result.caseValidation.cases.Count) case validation contracts, $($result.storageCleanup.cases.Count) artifact cleanup contracts, $($result.packageManifest.cases.Count) package manifest contracts, $($result.buildDiagnostics.cases.Count) native build diagnostic contracts, verification mod load-order contracts, native user-data isolation contracts, native evidence path contracts, presentation manifest aggregation contracts, development source tracking contracts, persistent sync contracts, presentation writer contracts and desktop focus safety contracts passed. Process lifecycle checks were skipped."
    } else {
        $result.status='PASS'; $result.reason="All 30 process guard contracts, $($compileCases.Count) compilation boundary contracts, $($result.assemblyReferences.cases.Count) assembly reference contracts, display cleanup contracts, $($cases.Count) owned process lifecycle cases, $($proofCases.Count) native action proof contracts, $($result.caseValidation.cases.Count) case validation contracts, $($result.storageCleanup.cases.Count) artifact cleanup contracts, $($result.packageManifest.cases.Count) package manifest contracts, $($result.buildDiagnostics.cases.Count) native build diagnostic contracts, verification mod load-order contracts, native user-data isolation contracts, native evidence path contracts, presentation manifest aggregation contracts, development source tracking contracts, persistent sync contracts, presentation writer contracts and desktop focus safety contracts passed."
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
