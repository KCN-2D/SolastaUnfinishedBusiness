[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$CasePath,
    [Parameter(Mandatory=$true)][string]$BuildDirectory,
    [Parameter(Mandatory=$true)][string]$GameExePath,
    [string]$AuthorizedGameDirectory,
    [string]$EvidenceRoot,
    [switch]$Deploy,
    [switch]$Launch,
    [switch]$VisualAcceptance,
    [switch]$ObserveDisplay,
    [switch]$ValidateOnly,
    [string]$ScenarioAdapter,
    [string[]]$ScenarioSource=@(),
    [string]$ScenarioType,
    [string]$ScenarioValidator,
    [Nullable[bool]]$ScenarioObservation=$null,
    [Nullable[bool]]$ScenarioSaveScenario=$null,
    [string[]]$ScenarioReferencePath=@()
)
. (Join-Path $PSScriptRoot 'GameTest.Common.ps1')
. (Join-Path $script:GameTestRepo 'tools\runtime-verification\NativeDisplaySettings.ps1')
if (-not $EvidenceRoot) { $EvidenceRoot=$script:GameTestArtifactRoot }
$CasePath=Resolve-GtArtifactPath $CasePath
$localRequested=$ScenarioAdapter -or @($ScenarioSource).Count -gt 0 -or $ScenarioType -or $ScenarioValidator -or $null -ne $ScenarioObservation -or $null -ne $ScenarioSaveScenario -or @($ScenarioReferencePath).Count -gt 0
if ($localRequested) {
    $adapter=Get-GtAdapter -Name $ScenarioAdapter -ScenarioSource $ScenarioSource -ScenarioType $ScenarioType -ScenarioValidator $ScenarioValidator -ScenarioObservation $ScenarioObservation -ScenarioSaveScenario $ScenarioSaveScenario -ScenarioReferencePath $ScenarioReferencePath
    $case=Read-GtCase $CasePath -AdapterDefinition $adapter
} else {
    $case=Read-GtCase $CasePath
    $adapter=Get-GtAdapter $case.adapter
}
$run = New-GtDirectory $EvidenceRoot $case.id
$events = [Collections.Generic.List[object]]::new()
function Add-GtEvent([string]$Kind,[string]$Message) {
    $events.Add([ordered]@{ utc=[datetime]::UtcNow.ToString('O'); kind=$Kind; message=$Message })
    Write-GtJson (Join-Path $run 'Operations.json') @($events.ToArray())
}
$acceptanceScope = if ($VisualAcceptance) { 'visual-observation' }
    elseif ($adapter.saveScenario -and $case.operation.verifyFocusLoss) { 'unity-focus-loss' }
    elseif ($adapter.saveScenario) { 'native-non-occupying-runner' }
    else { 'native-observation' }
$result = [ordered]@{ schemaVersion=1; caseId=$case.id; executionMode=$(if($VisualAcceptance){'visual-acceptance'}else{'native-regression'}); acceptanceScope=$acceptanceScope; status='BLOCKED'; reason='Preconditions have not completed.'; startedUtc=[datetime]::UtcNow.ToString('O'); evidenceDirectory=$run; runtimeResult=$null; restored=$false; images=@(); operations='Operations.json' }
$transactionCreated=$false; $runtimeFailure=$null; $oldCaseEnvironment=[Environment]::GetEnvironmentVariable('UB_GAME_TEST_CASE_PATH','Process'); $operationLock=$null
try {
    $operationLock=Enter-GtLock
    Add-GtEvent 'validation-started' 'Validating the existing case and staged build.'
    if ($case.adapter -eq 'startup' -and @($case.initialState.nativePreferences).Count -gt 0) {
        throw 'Fixed native preferences require a probe harness with isolated user data. Startup-only cases cannot stage real user preferences.'
    }
    if ($ObserveDisplay -and $case.adapter -eq 'startup') { throw 'ObserveDisplay requires a temporary probe harness.' }
    if (-not $VisualAcceptance -and $case.adapter -eq 'startup') { throw 'The startup adapter has no background harness. Use -VisualAcceptance for startup-only acceptance.' }
    if (-not $VisualAcceptance -and $adapter.saveScenario -and $case.operation.manualGate) { throw 'Manual gate cases require -VisualAcceptance. Native regression does not use mouse or keyboard input.' }
    if ($VisualAcceptance -and $adapter.saveScenario -and $case.operation.verifyFocusLoss) { throw 'verifyFocusLoss requires native regression without -VisualAcceptance.' }
    if (-not $VisualAcceptance -and -not $ValidateOnly) {
        . (Join-Path $script:GameTestRepo 'tools\runtime-verification\WindowsDesktop.ps1')
        $foreground=Get-VerificationForeground
        $result['foregroundBeforeDeployment']=[ordered]@{utc=$foreground.capturedUtc;processId=$foreground.processId}
        if ($foreground.processId -eq 0) { throw 'No foreground application could be identified; no game files were deployed.' }
        [void](Get-Command Start-VerificationBackgroundProcess -ErrorAction Stop)
        Add-GtEvent 'background-verified' 'A foreground application was identified and the non-activating launcher is available.'
    }
    $buildDirectory=Resolve-GtArtifactPath $BuildDirectory
    $build=Read-GtObject (Join-Path $buildDirectory 'Build.json')
    if ($build.status -ne 'PASS' -or $build.schemaVersion -ne 1) { throw 'A successful New-GameTestBuild output is required.' }
    $stage=Get-GtPath (Join-Path $buildDirectory $build.stage)
    if (-not (Test-GtWithin $stage $buildDirectory) -or $stage -eq $buildDirectory) { throw 'Invalid staging path.' }
    Assert-GtManifest $stage @($build.manifest)
    Add-GtEvent 'stage-verified' 'Staging manifest SHA-256 verification completed.'
    $environment=Get-GtEnvironment $GameExePath $build.environment.source.state.targetCommit
    if ($environment.source.digest -ne $build.environment.source.digest) { throw 'Working tree differs from the staged build.' }
    if (($environment.buildInputs | ConvertTo-Json -Compress) -ne ($build.environment.buildInputs | ConvertTo-Json -Compress)) { throw 'The build wrapper or runtime patch differs from the staged build.' }
    if ($environment.gameSha256 -ne $build.environment.gameSha256 -or ($environment.assemblies | ConvertTo-Json -Compress) -ne ($build.environment.assemblies | ConvertTo-Json -Compress)) { throw 'Game DLLs differ from build diagnosis.' }
    Add-GtEvent 'environment-verified' 'Source identity, build inputs and installed game DLLs verified.'
    $game=Split-Path -Parent $environment.gameExe
    $installed=Join-Path $game 'Mods\SolastaUnfinishedBusiness'
    Assert-GtTree $installed
    Assert-GtFile (Join-Path $installed 'SolastaUnfinishedBusiness.dll')
    Assert-GtGameStopped
    $markerPath=Join-Path $game 'Mods\.UBGameTestTransaction.json'
    if (Test-Path -LiteralPath $markerPath) { throw "A previous transaction requires recovery: $markerPath" }
    if (Test-Path -LiteralPath (Join-Path $game 'Mods\UBRuntimeVerification')) { throw 'An existing verification mod requires recovery before a new run.' }
    $formalOutput=Join-Path $script:GameTestBuildRoot 'Output\SolastaUnfinishedBusiness'
    Assert-GtManifest $formalOutput @($build.manifest)
    Copy-GtFile (Join-Path $buildDirectory 'Build.json') (Join-Path $run 'Build.json')
    Copy-GtFile (Join-Path $buildDirectory 'Build.log') (Join-Path $run 'Build.log')
    Write-GtJson (Join-Path $run 'Environment.json') $environment
    $fixedProbeSources=@($adapter.sources); $fixedProbeType=$adapter.probeType
    $toolSources=@(Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter '*.ps1')
    $toolSources+=@('Invoke-RuntimeVerification.ps1','RuntimeVerificationHarness.cs','WindowsDesktop.ps1','WindowsDesktop.cs','NativeDisplaySettings.ps1','EvidencePaths.ps1','Probes\DisplayStateProbe.cs' | ForEach-Object { Get-Item -LiteralPath (Join-Path $script:GameTestRepo ('tools\runtime-verification\' + $_)) })
    foreach ($source in $fixedProbeSources) { $toolSources+=Get-Item -LiteralPath $source }
    if ($adapter.caseValidator) { $toolSources+=Get-Item -LiteralPath $adapter.caseValidator }
    $result['toolSources']=@($toolSources | ForEach-Object { [ordered]@{path=$_.FullName;sha256=Get-GtHash $_.FullName} })
    $result['adapterSources']=@()
    $adapterInputs=@($fixedProbeSources)
    if ($adapter.caseValidator) { $adapterInputs+=$adapter.caseValidator }
    $archiveIndex=0
    foreach ($path in $adapterInputs) {
        $archiveIndex++
        $hash=Get-GtHash $path
        $archived=Join-Path $run ('InputAdapterSources/{0:D3}-{1}' -f $archiveIndex,[IO.Path]::GetFileName($path))
        Copy-GtFile $path $archived -ExpectedSha256 $hash
        $result.adapterSources+=[ordered]@{path=$path;sha256=$hash;archivePath=$archived}
    }
    $result['adapterReferences']=@()
    $runtimeReferencePaths=@()
    $referenceIndex=0
    foreach ($path in $adapter.referencePaths) {
        $referenceIndex++
        $hash=Get-GtHash $path
        $archived=Join-Path $run ('InputAdapterReferences/{0:D3}-{1}' -f $referenceIndex,[IO.Path]::GetFileName($path))
        Copy-GtFile $path $archived -ExpectedSha256 $hash
        $result.adapterReferences+=[ordered]@{path=$path;sha256=$hash;archivePath=$archived}
        $runtimeReferencePaths+=$archived
    }
    Copy-GtFile $CasePath (Join-Path $run 'InputCase.json')
    Assert-GtProperties $case.initialState @('savePath','saveSha256','settingsFile','settingsSha256','nativePreferences','setup') @('settingsFile','settingsSha256','nativePreferences')
    $settingsFile=Resolve-GtArtifactPath $case.initialState.settingsFile
    if ($case.initialState.settingsSha256 -notmatch '^[a-fA-F0-9]{64}$' -or (Get-GtHash $settingsFile) -ne $case.initialState.settingsSha256) { throw 'Fixed settings hash mismatch.' }
    [xml]$settingsXml=[IO.File]::ReadAllText($settingsFile)
    if ($settingsXml.DocumentElement.Name -ne 'Settings') { throw 'Expected a UB Settings XML snapshot.' }
    Copy-GtFile $settingsFile (Join-Path $run 'Input\Settings.xml')
    $case.initialState.settingsFile=Join-Path $run 'Input\Settings.xml'
    $preferenceKinds=@{}
    foreach ($preference in @($case.initialState.nativePreferences)) {
        Assert-GtProperties $preference @('kind','path','sha256') @('kind','path','sha256')
        if ($preference.kind -notin @('UserPreferences','PlatformPreferences') -or $preferenceKinds.ContainsKey($preference.kind)) { throw 'Invalid or duplicate native preference kind.' }
        $preferenceKinds[$preference.kind]=$true
        $preference.path=Resolve-GtArtifactPath $preference.path
        if ($preference.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or (Get-GtHash $preference.path) -ne $preference.sha256) { throw 'Native preference hash mismatch.' }
        $fixedPreference=Join-Path $run ('Input\' + $preference.kind + '.xml')
        Copy-GtFile $preference.path $fixedPreference
        $preference.path=$fixedPreference
    }
    if ($adapter.saveScenario) {
        Assert-GtProperties $case.initialState @('savePath','saveSha256','settingsFile','settingsSha256','nativePreferences','setup') @('savePath','saveSha256','settingsFile','settingsSha256','nativePreferences')
        $original=Get-GtPath $case.initialState.savePath
        if ([IO.Path]::GetExtension($original) -ne '.sav' -or $case.initialState.saveSha256 -notmatch '^[a-fA-F0-9]{64}$' -or (Get-GtHash $original) -ne $case.initialState.saveSha256) { throw 'Save input hash mismatch.' }
        $stamp=Get-Item -LiteralPath $original
        $result['originalSave']=[ordered]@{ path=$original; sha256=Get-GtHash $original; lastWriteUtc=$stamp.LastWriteTimeUtc.ToString('O'); lastWriteUtcTicks=$stamp.LastWriteTimeUtc.Ticks; attributes=[int]$stamp.Attributes; length=$stamp.Length }
        $clone=Join-Path $run ('Input\' + [IO.Path]::GetFileName($original))
        Copy-GtFile $original $clone
        $case.initialState.savePath=$clone
        $case.initialState | Add-Member -NotePropertyName originalSavePath -NotePropertyValue $original
    } elseif (-not $adapter.caseValidator) {
        Assert-GtProperties $case.operation @('kind') @('kind')
        Assert-GtProperties $case.expected @('initialized') @('initialized')
        if ($case.operation.kind -ne 'startup' -or $case.expected.initialized -ne $true) { throw 'Invalid startup case.' }
    }
    Write-GtJson (Join-Path $run 'Case.json') $case
    Add-GtEvent 'validated' 'Case, fixed settings, source identity, staging and installed game DLLs verified.'
    if ($ValidateOnly) {
        $result.reason='Validation succeeded. Deployment and launch were not requested.'
    } else {
        if (-not $Deploy -or -not $Launch -or -not $AuthorizedGameDirectory -or (Get-GtPath $AuthorizedGameDirectory) -ne $game) { throw 'Execution requires -Deploy -Launch and -AuthorizedGameDirectory matching the installed game.' }
        $backup=Join-Path $run 'Backup'
        New-Item -ItemType Directory -Path $backup | Out-Null
        $paths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $assemblyCaches=@(Get-GtAssemblyCaches $installed 'SolastaUnfinishedBusiness.dll')
        foreach ($cacheFile in $assemblyCaches) { [void]$paths.Add($cacheFile.FullName) }
        foreach ($entry in $build.manifest) {
            if ($entry.path -eq 'Settings.xml' -or $entry.path.StartsWith('Settings/',[StringComparison]::OrdinalIgnoreCase)) { continue }
            [void]$paths.Add((Join-Path $installed $entry.path))
        }
        [void]$paths.Add((Join-Path $installed 'Settings.xml'))
        if (Test-Path -LiteralPath (Join-Path $installed 'Settings')) {
            foreach ($file in Get-ChildItem -LiteralPath (Join-Path $installed 'Settings') -Recurse -File -Force) { [void]$paths.Add($file.FullName) }
        }
        [void]$paths.Add((Join-Path $game 'Solasta_Data\Managed\UnityModManager\Params.xml'))
        $profiles=Join-Path $installed 'Settings'
        $profileNames=@()
        if (Test-Path -LiteralPath $profiles -PathType Container) { $profileNames=@(Get-GtManifest $profiles | ForEach-Object { $_.path }) }
        $transaction=[ordered]@{ schemaVersion=1; gameDirectory=$game; state='prepared'; temporaryProbeOwned=($case.adapter -ne 'startup'); files=@(); createdDirectories=@(); settingsProfileNames=$profileNames; nativeDisplaySettings=(Get-NativeDisplaySettingsSnapshot) }
        Assert-NativeDisplaySettingsSnapshot $transaction.nativeDisplaySettings
        $pdbEntry=@($build.manifest | Where-Object path -eq 'SolastaUnfinishedBusiness.pdb')
        $transaction['assemblyCache']=[ordered]@{originalNames=@($assemblyCaches | ForEach-Object Name);expectedSha256=$build.builtUbSha256;expectedPdbSha256=$(if($pdbEntry.Count -eq 1){$pdbEntry[0].sha256}else{$null})}
        $index=0
        Add-GtEvent 'backup-started' 'Backing up every package target and user settings before deployment.'
        foreach ($path in ($paths | Sort-Object)) {
            Assert-GtPlainPath $path
            $entry=[ordered]@{ path=$path; existed=(Test-Path -LiteralPath $path -PathType Leaf); backup=$null; sha256=$null; lastWriteUtc=$null; lastWriteUtcTicks=$null; attributes=0 }
            if ($entry.existed) {
                $index++; $entry.backup=('Backup/{0:D6}.bin' -f $index)
                $item=Get-Item -LiteralPath $path -Force
                $entry.lastWriteUtc=$item.LastWriteTimeUtc.ToString('O'); $entry.lastWriteUtcTicks=$item.LastWriteTimeUtc.Ticks; $entry.attributes=[int]$item.Attributes; $entry.sha256=Get-GtHash $path
                Copy-GtFile $path (Join-Path $run $entry.backup) -ExpectedSha256 $entry.sha256
            } else {
                $parent=Split-Path -Parent $path
                while ((Test-GtWithin $parent $installed) -and -not (Test-Path -LiteralPath $parent)) {
                    $transaction.createdDirectories += $parent; $parent=Split-Path -Parent $parent
                }
            }
            $transaction.files += $entry
        }
        Write-GtJson (Join-Path $run 'Transaction.json') $transaction
        $transactionCreated=$true
        Assert-GtGameStopped
        Add-GtEvent 'backup-completed' ('Verified backup entries: ' + $index)
        Write-GtJson $markerPath @{transactionPath=(Join-Path $run 'Transaction.json')}
        foreach ($entry in $build.manifest) {
            if ($entry.path -eq 'Settings.xml' -or $entry.path.StartsWith('Settings/',[StringComparison]::OrdinalIgnoreCase)) { continue }
            Copy-GtFile (Join-Path $stage $entry.path) (Join-Path $installed $entry.path) -ExpectedSha256 $entry.sha256 -SkipIfIdentical
        }
        Copy-GtFile $case.initialState.settingsFile (Join-Path $installed 'Settings.xml')
        $transaction.state='deployed'; Write-GtJson (Join-Path $run 'Transaction.json') $transaction
        Add-GtEvent 'deployed' ('Staged package explicitly deployed to ' + $installed)
        [Environment]::SetEnvironmentVariable('UB_GAME_TEST_CASE_PATH',(Join-Path $run 'Case.json'),'Process')
        $runtimeRoot=Join-Path $run 'Runtime'
        $parameters=@{ RunName=$case.id; GameExePath=$environment.gameExe; EvidenceRoot=$runtimeRoot; SkipBuild=$true; ShowGameWindow=[bool]$VisualAcceptance; RunInBackground=(-not $VisualAcceptance); RequireOtherAppForeground=(-not $VisualAcceptance); RequireNativeActionProof=($adapter.saveScenario -and -not $VisualAcceptance); DedicatedPlayerLog=$true; TimeoutSeconds=[int]$case.timeoutSeconds; ExpectedUbSha256=(@($build.manifest | Where-Object { $_.path -eq 'SolastaUnfinishedBusiness.dll' })[0].sha256) }
        if (@($case.initialState.nativePreferences).Count -gt 0) { $parameters.NativePreferencesInputDirectory = Join-Path $run 'Input' }
        if ($case.adapter -eq 'startup') { $parameters.StartupOnly=$true }
        else {
            $parameters.ProbeSource=$fixedProbeSources
            $parameters.ProbeType=@($fixedProbeType)
            $parameters.ReferencePath=@((Join-Path $game 'Solasta_Data\Managed\UnityModManager\0Harmony.dll')) + $runtimeReferencePaths
            if ($ObserveDisplay) {
                $parameters.ProbeSource=@((Join-Path $script:GameTestRepo 'tools\runtime-verification\Probes\DisplayStateProbe.cs')) + $parameters.ProbeSource
                $parameters.ProbeType=@('DisplayStateProbe') + $parameters.ProbeType
            }
        }
        Add-GtEvent 'launched' ('Delegating process and temporary-mod lifecycle to the existing runtime runner; adapter=' + $case.adapter)
        try { & (Join-Path $script:GameTestRepo 'tools\runtime-verification\Invoke-RuntimeVerification.ps1') @parameters *> (Join-Path $run 'Runner.log') }
        catch { $runtimeFailure=$_.Exception.Message }
        $runtimeFiles=@(Get-ChildItem -LiteralPath $runtimeRoot -Filter Result.json -Recurse -File)
        if ($runtimeFiles.Count -ne 1) { $result.status='ERROR'; throw 'Runtime runner did not produce exactly one Result.json.' }
        $runtime=Read-GtObject $runtimeFiles[0].FullName
        $result.runtimeResult=$runtimeFiles[0].FullName
        Copy-GtFile (Join-Path $run 'Build.log') (Join-Path $runtimeFiles[0].DirectoryName 'Build.log')
        $result.status=switch ($runtime.status) { 'success' {'PASS'} 'incomplete' {'BLOCKED'} default {'ERROR'} }
        $result.reason=$runtime.reason
        $scenarioPath=Join-Path $runtimeFiles[0].DirectoryName 'GameTest-Smoke.json'
        if ($adapter.saveScenario -and (Test-Path -LiteralPath $scenarioPath)) {
            $scenario=Read-GtObject $scenarioPath
            if ($scenario.status -notin @('PASS','FAIL','BLOCKED','ERROR')) { throw 'Unknown scenario status.' }
            $result['scenarioResult']=$scenarioPath
            if ($scenario.status -ne 'PASS') { $result.status=$scenario.status; $result.reason=$scenario.detail }
        }
        elseif ($adapter.saveScenario -and $result.status -eq 'PASS') { throw 'The save-smoke scenario did not write its required result.' }
        if ($runtimeFailure -and $result.status -eq 'PASS') { $result.status='ERROR'; $result.reason=$runtimeFailure }
        if (-not $runtime.settingsRestored -or -not $runtime.displaySettingsRestored -or -not $runtime.probeRemoved) { $result.status='ERROR'; $result.reason+=' Runtime cleanup was incomplete.' }
        $result.images=@(Get-ChildItem -LiteralPath $runtimeRoot -Filter '*.png' -Recurse -File | ForEach-Object { [ordered]@{ path=$_.FullName; sha256=Get-GtHash $_.FullName } })
        if ($adapter.saveScenario -and $result.status -eq 'PASS' -and $result.images.Count -lt 2) { throw 'The save-smoke result is missing before/after image evidence.' }
        Assert-GtManifest $formalOutput @($build.manifest)
        if ((Get-GtSourceIdentity $build.environment.source.state.targetCommit).digest -ne $build.environment.source.digest) { $result.status='ERROR'; $result.reason+=' Source or index changed during the run.' }
        $changedTools=@(foreach ($tool in $result.toolSources) {
            $observedHash=Get-GtHash $tool.path
            if ($observedHash -ne $tool.sha256) {
                [ordered]@{path=$tool.path;expectedSha256=$tool.sha256;observedSha256=$observedHash}
            }
        })
        if ($changedTools.Count) {
            $result['toolSourceChanges']=$changedTools
            $result.status='ERROR'
            $result.reason+=' Test tools changed during the run: ' + (($changedTools | ForEach-Object path) -join ', ')
        }
        Add-GtEvent 'observed' ($result.status + ': ' + $result.reason)
    }
}
catch {
    if ($transactionCreated) { $result.status='ERROR' }
    $result.reason=$_.Exception.Message
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $run 'Error.log') -Encoding UTF8
}
finally {
    [Environment]::SetEnvironmentVariable('UB_GAME_TEST_CASE_PATH',$oldCaseEnvironment,'Process')
    if ($transactionCreated) {
        try {
            Add-GtEvent 'restore-started' 'Restoring the original package and settings after game exit.'
            & (Join-Path $PSScriptRoot 'Restore-GameTest.ps1') -RunDirectory $run -AuthorizedGameDirectory $AuthorizedGameDirectory *> (Join-Path $run 'Restore.log')
            $result.restored=((Read-GtObject (Join-Path $run 'Transaction.json')).state -eq 'restored')
            $result['displaySettingsRestored']=((Read-GtObject (Join-Path $run 'Transaction.json')).nativeDisplayRestoration.succeeded -eq $true)
            if (-not $result.restored -or -not $result.displaySettingsRestored) { throw 'The file or display-preference transaction was not restored.' }
            Add-GtEvent 'restored' 'Original installed package and settings restored.'
        } catch { $result.status='ERROR'; $result.reason+=' Restoration failed: ' + $_.Exception.Message }
    }
    if ('originalSave' -in $result.Keys) {
        try {
            $original=Get-Item -LiteralPath $result.originalSave.path
            $result['originalSavePreserved']=((Get-GtHash $original.FullName) -eq $result.originalSave.sha256 -and $original.LastWriteTimeUtc.Ticks -eq $result.originalSave.lastWriteUtcTicks -and [int]$original.Attributes -eq $result.originalSave.attributes -and $original.Length -eq $result.originalSave.length)
            if (-not $result.originalSavePreserved) { throw 'Original save fingerprint changed.' }
        } catch { $result.status='ERROR'; $result.reason+=' ' + $_.Exception.Message }
    }
    $result['completedUtc']=[datetime]::UtcNow.ToString('O')
    Write-GtJson (Join-Path $run 'Result.json') $result
    if ($null -ne $operationLock) { $operationLock.ReleaseMutex(); $operationLock.Dispose() }
}
if (-not $ValidateOnly -and $result.runtimeResult -and (Test-Path -LiteralPath $result.runtimeResult)) {
    $result['nativeVerificationStatus']=$result.status
    try {
        $reviewPath=Join-Path $PSScriptRoot 'Review-PresentationEvidence.ps1'
        $reviewSource=@($result.toolSources | Where-Object { $_.path -eq $reviewPath })
        if ($reviewSource.Count -ne 1 -or (Get-GtHash $reviewPath) -ne $reviewSource[0].sha256) {
            throw 'Presentation review source differs from the recorded test tools.'
        }
        $review=& $reviewPath `
            -EvidencePath (Split-Path -Parent $result.runtimeResult) `
            -OutputDirectory (Join-Path $run 'PresentationReview')
        if ((Get-GtHash $reviewPath) -ne $reviewSource[0].sha256) {
            throw 'Presentation review source changed during review.'
        }
        $result['presentationReview']=$review
        if ($review.status -eq 'FAIL' -and $result.status -eq 'PASS') {
            $result.status='FAIL'
            $result.reason='Presentation evidence contains a failed check. See PresentationReview.'
        } elseif ($review.status -eq 'NOT_RECORDED' -and $result.status -eq 'PASS') {
            $result.status='BLOCKED'
            $result.reason='Required presentation evidence is missing. See PresentationReview.'
        }
    }
    catch {
        $result['presentationReview']=[ordered]@{status='ERROR';reason=$_.Exception.Message}
        if ($result.status -eq 'PASS') { $result.status='ERROR'; $result.reason='Presentation review failed: ' + $_.Exception.Message }
    }
    Write-GtJson (Join-Path $run 'Result.json') $result
}
Write-Host "$($result.status): $($result.reason)"
Write-Host "Evidence: $run"
if ($result.status -in @('FAIL','ERROR') -or ($result.status -eq 'BLOCKED' -and -not $ValidateOnly)) { throw "Game test $($result.status). See $run" }
$run
