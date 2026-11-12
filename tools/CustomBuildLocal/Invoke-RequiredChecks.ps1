[CmdletBinding()]
param([switch]$CodexHook, [string]$PlanPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$eventName = 'Manual'
$eventInput = $null
$lease = $null
$script:logPath = $null

function Test-GitWrite([string]$Command) {
    $tokens = $null; $errors = $null
    $tree = [Management.Automation.Language.Parser]::ParseInput($Command,[ref]$tokens,[ref]$errors)
    foreach ($call in $tree.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst]},$true)) {
        $name = $call.GetCommandName()
        if (-not $name -or [IO.Path]::GetFileName($name) -notmatch '^git(?:\.exe)?$') { continue }
        $words = @($call.CommandElements | Select-Object -Skip 1 | ForEach-Object {
            if ($_ -is [Management.Automation.Language.StringConstantExpressionAst]) { $_.Value } else { $_.Extent.Text }
        })
        $index = 0
        while ($index -lt $words.Count -and $words[$index].StartsWith('-')) {
            if ($words[$index] -in @('-C','-c','--git-dir','--work-tree','--namespace','--config-env')) { $index += 2 } else { $index++ }
        }
        if ($index -lt $words.Count -and $words[$index] -in @('commit','push')) { return $true }
    }
    return $false
}

function Invoke-CheckProcess([string]$File, [string[]]$Arguments) {
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $File @Arguments 2>&1 | ForEach-Object { $_.ToString() })
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previousPreference }
    if ($script:logPath) { [IO.File]::AppendAllText($script:logPath,($output -join "`r`n") + "`r`n",[Text.UTF8Encoding]::new($false)) }
    if ($code -ne 0) { throw (($output | Select-Object -Last 10) -join "`n") }
    return $output
}

function Test-PackageMatch($First, $Second) {
    if (@($First.manifest).Count -ne @($Second.manifest).Count) { return $false }
    $expected = @{}
    foreach ($entry in $First.manifest) { $expected[$entry.path] = $entry.sha256 }
    foreach ($entry in $Second.manifest) {
        if (-not $expected.ContainsKey($entry.path) -or $expected[$entry.path] -ine $entry.sha256) { return $false }
    }
    return $true
}

try {
    if ($CodexHook) {
        $inputReader = [IO.StreamReader]::new([Console]::OpenStandardInput(),[Text.UTF8Encoding]::new($false),$true)
        try { $eventInput = $inputReader.ReadToEnd() | ConvertFrom-Json } finally { $inputReader.Dispose() }
        $eventName = [string]$eventInput.hook_event_name
        if ($eventName -eq 'PreToolUse') {
            $command = [string]$eventInput.tool_input.command
            if (-not (Test-GitWrite $command)) {
                Write-Output '{}'
                exit 0
            }
        } elseif ($eventName -ne 'Stop') { Write-Output '{}'; exit 0 }
    }
    if (-not $PlanPath) { $PlanPath = Join-Path $repositoryRoot '.codex/verification.json' }
    $plan = [IO.File]::ReadAllText($PlanPath) | ConvertFrom-Json
    if ($plan.schemaVersion -ne 1) { throw '必須検証の設定形式が不正です。' }
    . (Join-Path $repositoryRoot 'tools/game-test/GameTest.Common.ps1')
    $evidenceRoot = Get-GtPath $plan.artifactRoot
    if (-not (Test-GtWithin $evidenceRoot $script:GameTestArtifactRoot) -or $evidenceRoot -eq $script:GameTestArtifactRoot) { throw '必須検証の証跡先が不正です。' }
    [void][IO.Directory]::CreateDirectory($evidenceRoot)
    $script:logPath = Join-Path $evidenceRoot 'RequiredChecks.log'
    $lease = [IO.File]::Open((Join-Path $evidenceRoot 'RequiredChecks.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    Push-Location -LiteralPath $repositoryRoot
    try {
        [void](Invoke-CheckProcess 'git' @('-c','core.safecrlf=false','diff','--check'))
        [void](Invoke-CheckProcess 'git' @('diff','--cached','--check'))
        $head = [string](Invoke-GtGit @('rev-parse','HEAD'))
        $previousPreference = $ErrorActionPreference
        try { $ErrorActionPreference = 'Continue'; & git rev-parse --verify '@{upstream}' 2>$null | Out-Null; $hasUpstream = $LASTEXITCODE -eq 0 }
        finally { $ErrorActionPreference = $previousPreference }
        $base = if ($hasUpstream) { [string](Invoke-GtGit @('merge-base','HEAD','@{upstream}')) } else { $head }
        $translationArguments = @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',
            (Join-Path $repositoryRoot 'tools/localization/Test-Translations.ps1'),'-RepositoryRoot',$repositoryRoot,'-BaseCommit',$base)
        [void](Invoke-CheckProcess 'powershell.exe' $translationArguments)
        if ($CodexHook -and $eventName -eq 'PreToolUse' -and $command -match '(?i)\bcommit\b') {
            [void](Invoke-CheckProcess 'powershell.exe' ($translationArguments + '-Staged'))
        }
        $analyzer = if ($plan.PSObject.Properties['scriptAnalyzer']) { $plan.scriptAnalyzer } else { 'PSScriptAnalyzer' }
        Import-Module $analyzer -ErrorAction Stop
        $scriptFiles = @(Invoke-GtGit @('diff',$base,'--name-only','--diff-filter=ACMR','--','*.ps1'))
        $scriptFiles += @(Invoke-GtGit @('ls-files','--others','--exclude-standard','--','*.ps1'))
        $maintainedScripts = @('tools/CustomBuildLocal/Invoke-RequiredChecks.ps1','tools/CustomBuildLocal/Test-RequiredChecks.ps1',
            'tools/localization/Test-Translations.ps1','tools/localization/Test-TranslationChecks.ps1')
        $scriptFiles += $maintainedScripts
        $analysis = @(foreach ($scriptFile in @($scriptFiles | Sort-Object -Unique)) {
            $fullPath = Join-Path $repositoryRoot $scriptFile
            if ([IO.File]::Exists($fullPath)) { Invoke-ScriptAnalyzer -Path $fullPath -Severity Warning,Error }
        })
        Write-GtJson (Join-Path $evidenceRoot 'ScriptAnalysis.json') ([ordered]@{warningsAndErrors=$analysis.Count;files=@($scriptFiles | Sort-Object -Unique);findings=@($analysis | Select-Object ScriptName,Line,RuleName,Message)})
        if ($analysis.Count -gt 0) { throw (($analysis | ForEach-Object { "$($_.ScriptName):$($_.Line) $($_.RuleName): $($_.Message)" }) -join "`n") }
        $contractSources = @($maintainedScripts | ForEach-Object { [ordered]@{path=$_;sha256=(Get-GtHash (Join-Path $repositoryRoot $_))} })
        $contractPath = Join-Path $evidenceRoot 'VerificationContracts.json'
        $contracts = if ([IO.File]::Exists($contractPath)) { Read-GtObject $contractPath } else { $null }
        if (-not $contracts -or $contracts.status -ne 'PASS' -or
            ($contracts.sources | ConvertTo-Json -Compress) -cne ($contractSources | ConvertTo-Json -Compress)) {
            foreach ($testScript in @('tools/CustomBuildLocal/Test-RequiredChecks.ps1','tools/localization/Test-TranslationChecks.ps1')) {
                [void](Invoke-CheckProcess 'powershell.exe' @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $repositoryRoot $testScript)))
            }
            Write-GtJson $contractPath ([ordered]@{status='PASS';sources=$contractSources;utc=[datetime]::UtcNow.ToString('O')})
        }
        $environment = Get-GtEnvironment $plan.gameExe
        $verificationChanged = $false
        $cachePath = Join-Path $evidenceRoot 'RequiredChecks.json'
        $cached = if ([IO.File]::Exists($cachePath)) { Read-GtObject $cachePath } else { $null }
        $buildDirectory = $plan.buildDirectory
        if ($cached -and $cached.status -eq 'PASS') { $buildDirectory = $cached.buildDirectory }
        $build = if ($buildDirectory -and [IO.File]::Exists((Join-Path $buildDirectory 'Build.json'))) { Read-GtObject (Join-Path $buildDirectory 'Build.json') } else { $null }
        $sameInputs = $build -and $build.status -eq 'PASS' -and
            (Test-GtSourceContent $build.environment.source $environment.source) -and
            $build.environment.gameExe -ieq $environment.gameExe -and $build.environment.gameSha256 -ieq $environment.gameSha256 -and
            ($build.environment.assemblies | ConvertTo-Json -Depth 8 -Compress) -ceq ($environment.assemblies | ConvertTo-Json -Depth 8 -Compress) -and
            ($build.environment.buildInputs | ConvertTo-Json -Depth 8 -Compress) -ceq ($environment.buildInputs | ConvertTo-Json -Depth 8 -Compress) -and
            ($build.environment.sdks | ConvertTo-Json -Compress) -ceq ($environment.sdks | ConvertTo-Json -Compress)
        if (-not $sameInputs) {
            $verificationChanged = $true
            $output = Invoke-CheckProcess 'powershell.exe' @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',
                (Join-Path $repositoryRoot 'tools/game-test/New-GameTestBuild.ps1'),'-GameExePath',$plan.gameExe,'-EvidenceRoot',(Join-Path $evidenceRoot 'Builds'))
            $buildDirectory = [string]($output | Select-Object -Last 1)
            $build = Read-GtObject (Join-Path $buildDirectory 'Build.json')
            if ($build.status -ne 'PASS') { throw '正式ビルドが成功していません。' }
        }
        $nativePath = $plan.native.result
        if ($cached -and $cached.status -eq 'PASS') { $nativePath = $cached.nativeResult }
        $inputCase = Read-GtObject $plan.native.casePath
        $nativeFiles = @($plan.native.casePath,$plan.native.validator) + @($plan.native.sources) + @($plan.native.references)
        if ($inputCase.initialState.PSObject.Properties['settingsFile']) { $nativeFiles += $inputCase.initialState.settingsFile }
        if ($inputCase.initialState.PSObject.Properties['savePath']) { $nativeFiles += $inputCase.initialState.savePath }
        if ($inputCase.initialState.PSObject.Properties['nativePreferences']) { $nativeFiles += @($inputCase.initialState.nativePreferences | ForEach-Object path) }
        $nativeInputs = [ordered]@{adapter=$plan.native.adapter;type=$plan.native.type;files=@($nativeFiles | Sort-Object -Unique | ForEach-Object { [ordered]@{path=$_;sha256=(Get-GtHash $_)} })}
        $native = if ([IO.File]::Exists($nativePath)) { Read-GtObject $nativePath } else { $null }
        $nativeBuildPath = if ($native) { Join-Path (Split-Path -Parent $nativePath) 'Build.json' } else { $null }
        $testedBuild = if ($nativeBuildPath -and [IO.File]::Exists($nativeBuildPath)) { Read-GtObject $nativeBuildPath } else { $null }
        $nativeValid = $native -and $native.status -eq 'PASS' -and $native.restored -and $testedBuild -and (Test-PackageMatch $testedBuild $build) -and
            $testedBuild.environment.gameSha256 -ieq $environment.gameSha256 -and
            ($testedBuild.environment.assemblies | ConvertTo-Json -Depth 8 -Compress) -ceq ($environment.assemblies | ConvertTo-Json -Depth 8 -Compress)
        $nativeValid = $nativeValid -and $cached -and $cached.PSObject.Properties['nativeInputs'] -and
            ($cached.nativeInputs | ConvertTo-Json -Depth 6 -Compress) -ceq ($nativeInputs | ConvertTo-Json -Depth 6 -Compress)
        if ($nativeValid) {
            foreach ($source in $native.toolSources) {
                if (-not [IO.File]::Exists($source.path) -or (Get-GtHash $source.path) -ine $source.sha256) { $nativeValid = $false; break }
            }
        }
        if (-not $nativeValid) {
            $verificationChanged = $true
            $gameDirectory = Split-Path -Parent $plan.gameExe
            # Array parameters are bound in a child PowerShell script, not flattened by -File.
            $runnerPath = Join-Path $evidenceRoot 'RunRequiredNative.ps1'
            $runner = '$p=Get-Content -LiteralPath $args[0] -Raw|ConvertFrom-Json; & $p.runner -CasePath $p.casePath -BuildDirectory $p.buildDirectory -GameExePath $p.gameExe -AuthorizedGameDirectory $p.gameDirectory -EvidenceRoot $p.evidenceRoot -ScenarioAdapter $p.adapter -ScenarioSource @($p.sources) -ScenarioType $p.type -ScenarioValidator $p.validator -ScenarioReferencePath @($p.references) -ScenarioObservation $false -Deploy -Launch -RequireNonInputDesktop'
            [IO.File]::WriteAllText($runnerPath,$runner,[Text.UTF8Encoding]::new($true))
            $runPlanPath = Join-Path $evidenceRoot 'RequiredNative.json'
            Write-GtJson $runPlanPath ([ordered]@{runner=(Join-Path $repositoryRoot 'tools/game-test/Invoke-GameTest.ps1');casePath=$plan.native.casePath;buildDirectory=$buildDirectory;gameExe=$plan.gameExe;gameDirectory=$gameDirectory;evidenceRoot=$plan.native.evidenceRoot;adapter=$plan.native.adapter;sources=$plan.native.sources;type=$plan.native.type;validator=$plan.native.validator;references=$plan.native.references})
            $output = Invoke-CheckProcess 'powershell.exe' @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$runnerPath,$runPlanPath)
            $nativeDirectory = [string]($output | Select-Object -Last 1)
            $nativePath = Join-Path $nativeDirectory 'Result.json'
            $native = Read-GtObject $nativePath
            if ($native.status -ne 'PASS' -or -not $native.restored) { throw '実ゲーム検証または復元が完了していません。' }
        }
        $zipPath = Join-Path $repositoryRoot 'tools/CustomBuildLocal/SolastaUnfinishedBusiness.zip'
        if ((Get-GtHash $zipPath) -ine $build.zipSha256) { throw '配布ZIPと正式ビルドの内容が一致していません。' }
        $cleanupPath = Join-Path $evidenceRoot 'RunRequiredCleanup.ps1'
        $cleanup = '$p=Get-Content -LiteralPath $args[0] -Raw|ConvertFrom-Json; if($p.pruneRuns){& $p.runner -ArtifactRoot $p.root -PruneSuperseded -KeepLatestPerCase 1 -PreserveDirectory @($p.preserve) -Apply}; & $p.runner -ArtifactRoot $p.reportRoot -PruneReports -PruneLooseFiles -PreserveDirectory @($p.preserve) -Apply'
        [IO.File]::WriteAllText($cleanupPath,$cleanup,[Text.UTF8Encoding]::new($true))
        $cleanupPlan = Join-Path $evidenceRoot 'RequiredCleanup.json'
        Write-GtJson $cleanupPlan ([ordered]@{runner=(Join-Path $repositoryRoot 'tools/game-test/Clear-GameTestArtifacts.ps1');root=$evidenceRoot;reportRoot=$script:GameTestArtifactRoot;pruneRuns=$verificationChanged;preserve=@($buildDirectory,(Split-Path -Parent $nativePath))})
        [void](Invoke-CheckProcess 'powershell.exe' @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$cleanupPath,$cleanupPlan))
        Write-GtJson $cachePath ([ordered]@{schemaVersion=1;status='PASS';utc=[datetime]::UtcNow.ToString('O');buildDirectory=$buildDirectory;nativeResult=$nativePath;nativeInputs=$nativeInputs;source=$environment.source;packageSha256=$build.zipSha256;event=$eventName})
        if ($CodexHook) { Write-Output '{}' } else { Write-Output "必須検証成功: $cachePath" }
    } finally { Pop-Location }
    exit 0
} catch {
    $reason = '必須検証に失敗しました。' + $_.Exception.Message
    if ($CodexHook -and $eventName -eq 'PreToolUse') {
        @{hookSpecificOutput=@{hookEventName='PreToolUse';permissionDecision='deny';permissionDecisionReason=$reason}} | ConvertTo-Json -Depth 5 -Compress
    } elseif ($CodexHook) { @{decision='block';reason=$reason} | ConvertTo-Json -Compress }
    else { [Console]::Error.WriteLine($reason) }
    if ($CodexHook) { exit 0 }
    exit 1
} finally { if ($lease) { $lease.Dispose() } }
