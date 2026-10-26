[CmdletBinding(DefaultParameterSetName = 'All')]
param(
    [Parameter(Mandatory = $true)] [string] $UnityPath,
    [Parameter(Mandatory = $true, ParameterSetName = 'All')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Background')] [string] $FbxPath,
    [Parameter(Mandatory = $true)] [string] $GuiProjectPath,
    [Parameter(ParameterSetName = 'All')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Materials')] [string] $MaterialRecipePath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Materials')] [switch] $MaterialsOnly,
    [Parameter(Mandatory = $true, ParameterSetName = 'Meshes')] [string] $MeshRecipePath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Meshes')] [switch] $MeshesOnly,
    [string] $WorkspaceRoot,
    [Parameter(Mandatory = $true, ParameterSetName = 'Background')] [switch] $BackgroundEditorOnly,
    [Parameter(Mandatory = $true, ParameterSetName = 'CliTimeout')] [switch] $CliTimeoutOnly,
    [Parameter(Mandatory = $true, ParameterSetName = 'CallerStop')] [switch] $CallerStopOnly,
    [string] $UnityCliPath,
    [switch] $InstallGuiWorker,
    [ValidateRange(5, 300)] [int] $TimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$launcher = Join-Path $PSScriptRoot '..\Invoke-UnityAssetBuild.ps1'
$project = (Resolve-Path -LiteralPath $GuiProjectPath).ProviderPath
$fbx = if (!$MaterialsOnly -and !$MeshesOnly -and !$CliTimeoutOnly -and !$CallerStopOnly) { (Resolve-Path -LiteralPath $FbxPath).ProviderPath } else { $null }
$marker = Join-Path $project '.solasta-asset-authoring-project'
if (!(Test-Path -LiteralPath $marker -PathType Leaf) -or
    [IO.File]::ReadAllText($marker).Trim() -ne 'SolastaUBAssetAuthoring:UnityProject:1') { throw 'A marked authoring project is required.' }
$jobs = Join-Path $project '.asset-authoring-jobs'
[void][IO.Directory]::CreateDirectory((Join-Path $jobs 'BackgroundSessions'))
$checks = [Collections.Generic.List[object]]::new()
function Add-Check([string] $Name, [bool] $Passed, [object] $Evidence) {
    $checks.Add([pscustomobject]@{ name = $Name; passed = $Passed; evidence = $Evidence })
    if (!$Passed) { throw "Contract failed: $Name" }
}
if ($MeshesOnly) {
    . (Join-Path $PSScriptRoot '../UnityAssetBuild/BackgroundEditor.ps1')
    $editor=(Resolve-Path -LiteralPath $UnityPath).ProviderPath
    $cli=if ($UnityCliPath) { (Resolve-Path -LiteralPath $UnityCliPath).ProviderPath } else { Join-Path $env:LOCALAPPDATA 'Unity/bin/unity.exe' }
    $recipePath=(Resolve-Path -LiteralPath $MeshRecipePath).ProviderPath
    $sourceHash=(Get-FileHash -LiteralPath (Join-Path $project 'Assets/Editor/AssetBundleBuilder.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
    $recipeHash=(Get-FileHash -LiteralPath $recipePath -Algorithm SHA256).Hash
    $ownedSession=$null; $failure=$null; $completed=$false
    $resultPath=Join-Path $jobs ('contracts-meshes-'+[guid]::NewGuid().ToString('N')+'.json')
    function MeshJobIds { @(Get-ChildItem -LiteralPath $jobs -Directory | Where-Object Name -Match '^[a-f0-9]{32}$' | ForEach-Object Name) }
    $arguments=@{UnityPath=$UnityPath;GuiProjectPath=$project;TimeoutSeconds=$TimeoutSeconds}
    if ($WorkspaceRoot) { $arguments.WorkspaceRoot=$WorkspaceRoot }
    try {
        $ownedSession=Start-AssetBackgroundEditor $project $editor $cli $sourceHash $TimeoutSeconds
        $before=MeshJobIds
        & $launcher @arguments -MeshRecipePath $recipePath
        $created=@(MeshJobIds|Where-Object {$_ -notin $before})
        Add-Check 'unique-mesh-job' ($created.Count -eq 1) $created
        $reportPath=Join-Path $jobs ($created[0]+'/Output/unity-report.json')
        $report=Get-Content -LiteralPath $reportPath -Raw|ConvertFrom-Json
        $recipe=Get-Content -LiteralPath $recipePath -Raw|ConvertFrom-Json
        Add-Check 'mesh-native-full-channel-readback' ($report.success -and $report.validationScope -eq 'mesh_templates' -and
            @($report.meshes).Count -eq @($recipe.meshes).Count -and @($report.meshes|Where-Object {!$_.exact -or !$_.boundsExact}).Count -eq 0 -and
            @($report.meshes|ForEach-Object channels|Where-Object {!$_.exact}).Count -eq 0) $reportPath
        Add-Check 'mesh-scene-state-preserved' ($report.activeScenePathBefore -eq $report.activeScenePathAfter -and
            $report.activeSceneDirtyBefore -eq $report.activeSceneDirtyAfter -and $report.sceneCountBefore -eq $report.sceneCountAfter) $reportPath
        $fixture=Join-Path $jobs ('MeshInputContracts/'+[guid]::NewGuid().ToString('N'))
        [void][IO.Directory]::CreateDirectory($fixture)
        foreach ($kind in @('nonfinite','channel-length','indices','bounds','duplicate-name','path-name')) {
            $bad=Get-Content -LiteralPath $recipePath -Raw|ConvertFrom-Json
            switch($kind) {
                'nonfinite' {$bad.meshes[0].vertices[0]=[double]::NaN}
                'channel-length' {$bad.meshes[0].normals=@(1,0,0)}
                'indices' {$bad.meshes[0].subMeshes[0].indices[0]=2147483647}
                'bounds' {$bad.meshes[0].bounds.size=@(0,0,0)}
                'duplicate-name' {$bad.meshes=@($bad.meshes)+@($bad.meshes[0])}
                'path-name' {$bad.meshes[0].name='../outside'}
            }
            $input=Join-Path $fixture ($kind+'.json')
            [IO.File]::WriteAllText($input,($bad|ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
            $before=MeshJobIds;$rejected=$false
            try { & $launcher @arguments -MeshRecipePath $input } catch { $rejected=$true }
            Add-Check ('mesh-preflight-reject-'+$kind) ($rejected -and @(MeshJobIds|Where-Object {$_ -notin $before}).Count -eq 0) $input
        }
        # Existing input contracts remain before worker dispatch; no extra rig/material build.
        foreach($legacy in @('rig-extension','material-schema')) {
            $before=MeshJobIds;$rejected=$false
            try {
                if ($legacy -eq 'rig-extension') { & $launcher @arguments -FbxPath $recipePath }
                else { & $launcher @arguments -MaterialRecipePath $recipePath }
            } catch { $rejected=$true }
            Add-Check ('legacy-preflight-'+$legacy) ($rejected -and @(MeshJobIds|Where-Object {$_ -notin $before}).Count -eq 0) $recipePath
        }
        # A direct owned queue fault proves the worker independently checks before asset creation.
        $id=[guid]::NewGuid().ToString('N');$job=Join-Path $jobs $id;$input=Join-Path $project ('Assets/AuthoringJobs/'+$id+'/Input')
        [void][IO.Directory]::CreateDirectory($job);[void][IO.Directory]::CreateDirectory($input)
        [IO.File]::WriteAllText((Join-Path $job '.solasta-asset-authoring-job'),'SolastaUBAssetAuthoring:UnityJob:1')
        $bad=Get-Content -LiteralPath $recipePath -Raw|ConvertFrom-Json;$bad.meshes[0].subMeshes[0].indices[0]=-1
        $copied=Join-Path $input 'mesh-recipe.json';[IO.File]::WriteAllText($copied,($bad|ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
        $request=@{schemaVersion=1;jobId=$id;kind='mesh-templates';inputSha256=(Get-FileHash -LiteralPath $copied -Algorithm SHA256).Hash.ToLowerInvariant()}
        [IO.File]::WriteAllText((Join-Path $job 'request.writing'),($request|ConvertTo-Json));[IO.File]::Move((Join-Path $job 'request.writing'),(Join-Path $job 'request.json'))
        $failedReport=Join-Path $job 'Output/unity-report.json';$deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while (!(Test-Path -LiteralPath $failedReport) -and [DateTime]::UtcNow -lt $deadline) {Start-Sleep -Milliseconds 250}
        if (!(Test-Path -LiteralPath $failedReport)) {throw 'Rejected mesh input did not return a worker report.'}
        $failed=Get-Content -LiteralPath $failedReport -Raw|ConvertFrom-Json
        Add-Check 'mesh-worker-reject-invalid-index' (!$failed.success -and $failed.error -like '*index range*' -and
            !(Test-Path -LiteralPath (Join-Path $project ('Assets/AuthoringJobs/'+$id+'/Generated'))) -and
            !(Test-Path -LiteralPath (Join-Path $job ('Output/'+$recipe.bundleName)))) $failedReport
        Add-Check 'mesh-source-unchanged' ((Get-FileHash -LiteralPath $recipePath -Algorithm SHA256).Hash -eq $recipeHash) $recipeHash
        Add-Check 'mesh-borrowed-worker-still-owned' ($null -ne (Get-AssetOwnedProcess $ownedSession.Ready.editor)) $ownedSession.Ready.editor
        $completed=$true
    } catch {$failure=$_.Exception.ToString();throw}
    finally {
        if($ownedSession) {
            Stop-AssetBackgroundEditor $ownedSession
            $state=Get-Content -LiteralPath (Join-Path $ownedSession.RunDirectory 'session.json') -Raw|ConvertFrom-Json
            Add-Check 'mesh-normal-owned-shutdown' ($state.status -eq 'complete' -and $state.editorReleased -and !$state.forcedExit -and
                !(Get-AssetOwnedProcess $state.editor)) $ownedSession.RunDirectory
        }
        [IO.File]::WriteAllText($resultPath,(@{passed=$completed;error=$failure;scope='mesh-templates-one-build-basic-input-contracts';checks=$checks}|ConvertTo-Json -Depth 12))
        Write-Output "Mesh template contracts: $resultPath"
    }
    return
}
if ($CallerStopOnly) {
    $helper=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../UnityAssetBuild/BackgroundEditor.ps1')).ProviderPath
    . $helper
    $fixtureRoot=Join-Path $jobs ('CallerStopContracts/'+[guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($fixtureRoot)
    $fakeCli=Join-Path $fixtureRoot 'ReceiptOnlyCli.exe'
    $source=Join-Path $fixtureRoot 'ReceiptOnlyCli.cs'
    [IO.File]::WriteAllText($source,@'
using System;
class ReceiptOnlyCli {
    static string E(string s) { return s.Replace("\\","\\\\").Replace("\"","\\\""); }
    static void Main(string[] a) { Console.WriteLine("{\"success\":true,\"data\":{\"editorPath\":\""+E(a[5])+"\",\"projectPath\":\""+E(a[1])+"\"}}"); }
}
'@)
    & (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /target:exe ('/out:'+$fakeCli) $source
    if ($LASTEXITCODE -ne 0) { throw 'The receipt-only CLI fixture did not compile.' }
    $sourceHash=(Get-FileHash -LiteralPath (Join-Path $project 'Assets/Editor/AssetBundleBuilder.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
    $editor=(Resolve-Path -LiteralPath $UnityPath).ProviderPath
    $sessions=Join-Path $jobs 'BackgroundSessions'
    function Wait-CallerFixture([string[]]$Before) {
        $deadline=[DateTime]::UtcNow.AddSeconds(15)
        while ([DateTime]::UtcNow -lt $deadline) {
            $created=@(Get-ChildItem -LiteralPath $sessions -Directory | Where-Object Name -NotIn $Before)
            if ($created.Count -eq 1) {
                $statePath=Join-Path $created[0].FullName 'session.json'
                if (Test-Path -LiteralPath $statePath) {
                    $state=Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
                    if ($state.PSObject.Properties['cli'] -and $state.cli.PSObject.Properties['exitCode']) { return $statePath }
                }
            }
            Start-Sleep -Milliseconds 100
        }
        throw 'The actual background host did not reach its startup wait.'
    }
    function Assert-CallerFixtureReleased([string]$StatePath,[string]$Name,[DateTime]$Stopped) {
        $deadline=[DateTime]::UtcNow.AddSeconds(10)
        do {
            $state=Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
            if ($state.PSObject.Properties['finishedUtc']) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        Add-Check $Name ($state.status -eq 'failed' -and $state.cliReleased -and $state.editorReleased -and
            !$state.forcedExit -and $state.error -match 'caller (cancelled|ended)' -and
            ([DateTime]::UtcNow-$Stopped).TotalSeconds -lt 10 -and !(Get-AssetOwnedProcess $state.cli)) $StatePath
    }
    $before=@(Get-ChildItem -LiteralPath $sessions -Directory | ForEach-Object Name)
    $pipeline=[PowerShell]::Create()
    try {
        [void]$pipeline.AddScript('param($helper,$project,$editor,$cli,$hash); . $helper; Start-AssetBackgroundEditor $project $editor $cli $hash 60').AddArgument($helper).AddArgument($project).AddArgument($editor).AddArgument($fakeCli).AddArgument($sourceHash)
        $pending=$pipeline.BeginInvoke()
        $statePath=Wait-CallerFixture $before
        $stopped=[DateTime]::UtcNow
        $pipeline.Stop()
        Assert-CallerFixtureReleased $statePath 'cancelled-pipeline-releases-starting-host' $stopped
    }
    finally { if ($pipeline.InvocationStateInfo.State -eq 'Running') { $pipeline.Stop() }; $pipeline.Dispose() }
    $before=@(Get-ChildItem -LiteralPath $sessions -Directory | ForEach-Object Name)
    $caller=Join-Path $fixtureRoot 'Caller.ps1'
    [IO.File]::WriteAllText($caller,'param($helper,$project,$editor,$cli,$hash); . $helper; Start-AssetBackgroundEditor $project $editor $cli $hash 60')
    $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=(Get-Process -Id $PID).Path;$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    foreach ($arg in @('-NoProfile','-File',$caller,$helper,$project,$editor,$fakeCli,$sourceHash)) { $start.ArgumentList.Add($arg) }
    $ownedCaller=[Diagnostics.Process]::Start($start)
    try {
        $statePath=Wait-CallerFixture $before
        $stopped=[DateTime]::UtcNow
        $ownedCaller.Kill();[void]$ownedCaller.WaitForExit(5000)
        Assert-CallerFixtureReleased $statePath 'ended-caller-releases-starting-host' $stopped
    }
    finally { if (!$ownedCaller.HasExited) { $ownedCaller.Kill();[void]$ownedCaller.WaitForExit(5000) }; $ownedCaller.Dispose() }
    $reportPath=Join-Path $fixtureRoot 'contracts.json'
    [IO.File]::WriteAllText($reportPath,(@{success=$true;scope='actual-background-host-startup-caller-lifecycle';checks=$checks}|ConvertTo-Json -Depth 12))
    Write-Output "Caller stop contracts passed: $reportPath"
    return
}
if ($CliTimeoutOnly) {
    . (Join-Path $PSScriptRoot '../UnityAssetBuild/BackgroundEditor.ps1')
    $fixtureRoot=Join-Path $jobs ('CliTimeoutContracts/'+[guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $source=Join-Path $fixtureRoot 'DelayedCli.cs'
    $fakeCli=Join-Path $fixtureRoot 'DelayedCli.exe'
    [IO.File]::WriteAllText($source, 'using System; using System.IO; using System.Threading; class DelayedCli { static void Main() { Thread.Sleep(60000); File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"late-launch.txt"),"unexpected late launcher action"); } }')
    $compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    & $compiler /nologo /target:exe ('/out:'+$fakeCli) $source
    if ($LASTEXITCODE -ne 0) { throw 'The owned delayed CLI fixture did not compile.' }
    $sessions=Join-Path $jobs 'BackgroundSessions'
    $before=@(Get-ChildItem -LiteralPath $sessions -Directory | ForEach-Object Name)
    $sourceHash=(Get-FileHash -LiteralPath (Join-Path $project 'Assets/Editor/AssetBundleBuilder.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
    $failed=$false
    try { $unexpected=Start-AssetBackgroundEditor $project (Resolve-Path -LiteralPath $UnityPath).ProviderPath $fakeCli $sourceHash 30 }
    catch { $failed=$true }
    $created=@(Get-ChildItem -LiteralPath $sessions -Directory | Where-Object Name -NotIn $before)
    Add-Check 'one-timeout-session' ($failed -and $created.Count -eq 1) @($created.Name)
    $statePath=Join-Path $created[0].FullName 'session.json'
    $state=Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    Add-Check 'owned-cli-stopped-before-release' ($state.status -eq 'failed' -and $state.cliReleased -and $state.cliTerminatedAfterFailure -and
        $state.cli.path -eq $fakeCli -and !(Get-AssetOwnedProcess $state.cli) -and $state.editorReleased -and
        !(Test-Path -LiteralPath (Join-Path $fixtureRoot 'late-launch.txt'))) $statePath
    $reportPath=Join-Path $fixtureRoot 'contracts.json'
    [IO.File]::WriteAllText($reportPath, (@{success=$true;scope='owned-cli-timeout-cleanup';checks=$checks} | ConvertTo-Json -Depth 12))
    Write-Output "CLI timeout contracts passed: $reportPath"
    return
}
if ($BackgroundEditorOnly) {
    $sessions = Join-Path $jobs 'BackgroundSessions'
    if ($InstallGuiWorker) {
        & $launcher -UnityPath $UnityPath -FbxPath $fbx -GuiProjectPath $project -StartBackgroundEditor -UnityCliPath $UnityCliPath -InstallGuiWorker -TimeoutSeconds $TimeoutSeconds
    }
    . (Join-Path $PSScriptRoot '../UnityAssetBuild/BackgroundEditor.ps1')
    $sourceHash=(Get-FileHash -LiteralPath (Join-Path $project 'Assets/Editor/AssetBundleBuilder.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
    $cli=if ($UnityCliPath) { $UnityCliPath } else { Join-Path $env:LOCALAPPDATA 'Unity/bin/unity.exe' }
    $ownedSession=Start-AssetBackgroundEditor $project (Resolve-Path -LiteralPath $UnityPath).ProviderPath $cli $sourceHash $TimeoutSeconds
    try {
        $environmentScript=Join-Path $PSScriptRoot '../Test-AssetEnvironment.ps1'
        $environmentHash=(Get-FileHash -LiteralPath $environmentScript -Algorithm SHA256).Hash
        $environmentText=& $environmentScript -UnityPath $UnityPath -GuiProjectPath $project -RequireReadyEditor
        $environment=$environmentText | ConvertFrom-Json
        $environmentPath=Join-Path $ownedSession.RunDirectory 'environment.json'
        [IO.File]::WriteAllText($environmentPath,($environment | ConvertTo-Json -Depth 12))
        Add-Check 'environment-live-isolated-worker' ($environment.editor_worker_ready -and $environment.editor_non_input_worker_ready -and
            @($environment.editor_workers | Where-Object { $_.ready -and $_.isolated -and $_.process_id -eq $ownedSession.Ready.editor.pid }).Count -eq 1 -and
            (Get-FileHash -LiteralPath $environmentScript -Algorithm SHA256).Hash -eq $environmentHash) $environmentPath
        $rejected=$false
        try { $unexpected=Start-AssetBackgroundEditor $project (Resolve-Path -LiteralPath $UnityPath).ProviderPath $cli $sourceHash $TimeoutSeconds }
        catch { $rejected=$_.Exception.Message -like 'The selected project*open*' }
        Add-Check 'reject-second-owner' $rejected $ownedSession.RunDirectory
        $readyPath=Join-Path $jobs 'worker.json'
        [IO.File]::Delete($readyPath)
        $rejectedWithoutReceipt=$false
        try { $unexpected=Start-AssetBackgroundEditor $project (Resolve-Path -LiteralPath $UnityPath).ProviderPath $cli $sourceHash $TimeoutSeconds }
        catch { $rejectedWithoutReceipt=$_.Exception.Message -like '*native UnityLockfile is locked*' }
        Add-Check 'native-lock-without-worker-receipt' $rejectedWithoutReceipt $ownedSession.RunDirectory
        & $launcher -UnityPath $UnityPath -FbxPath $fbx -GuiProjectPath $project -TimeoutSeconds $TimeoutSeconds
        Add-Check 'borrowed-worker-remains-running' ($null -ne (Get-AssetOwnedProcess $ownedSession.Ready.editor)) $ownedSession.Ready.editor
    } finally { Stop-AssetBackgroundEditor $ownedSession }
    $previousOwner = $null
    foreach ($cycle in @('first', 'repeat', 'rejected-input')) {
        $before = @(if (Test-Path -LiteralPath $sessions) { Get-ChildItem -LiteralPath $sessions -Directory | ForEach-Object Name })
        $input = $fbx
        if ($cycle -eq 'rejected-input') {
            $input = Join-Path $jobs ('invalid-' + [guid]::NewGuid().ToString('N') + '.fbx')
            [IO.File]::WriteAllText($input, 'invalid FBX fixture')
        }
        $failed = $false
        try {
            & $launcher -UnityPath $UnityPath -FbxPath $input -GuiProjectPath $project -StartBackgroundEditor -UnityCliPath $UnityCliPath -InstallGuiWorker:$InstallGuiWorker -TimeoutSeconds $TimeoutSeconds
        } catch { $failed = $true; if ($cycle -ne 'rejected-input') { throw } }
        finally { if ($cycle -eq 'rejected-input') { Remove-Item -LiteralPath $input -Force } }
        Add-Check ('background-result-' + $cycle) ($failed -eq ($cycle -eq 'rejected-input')) $failed
        $created = @(Get-ChildItem -LiteralPath $sessions -Directory | Where-Object Name -NotIn $before)
        Add-Check ('one-owned-session-' + $cycle) ($created.Count -eq 1) @($created.Name)
        $statePath = Join-Path $created[0].FullName 'session.json'
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        Add-Check ('isolated-main-thread-' + $cycle) ($state.worker.mainThread -gt 0 -and $state.worker.nativeMainThread -gt 0 -and
            $state.worker.desktopQuerySucceeded -and !$state.worker.desktopReceivesInput -and $state.worker.desktop -eq $state.desktop -and
            $state.worker.desktop -ne $state.worker.inputDesktop -and $state.worker.ownerId -eq $state.ownerId -and
            $state.worker.processId -eq $state.editor.pid -and $state.cli.exitCode -eq 0 -and !$state.cli.batchMode -and !$state.cli.waitRequested) $statePath
        Add-Check ('normal-owned-shutdown-' + $cycle) ($state.status -eq 'complete' -and $state.editorReleased -and !$state.forcedExit -and
            !(Get-Process -Id $state.editor.pid -ErrorAction SilentlyContinue)) $statePath
        if ($previousOwner) { Add-Check ('distinct-owner-' + $cycle) ($state.ownerId -ne $previousOwner) $state.ownerId }
        $previousOwner = $state.ownerId
    }
    $reportPath = Join-Path $jobs ('contracts-background-' + [guid]::NewGuid().ToString('N') + '.json')
    [IO.File]::WriteAllText($reportPath, (@{success=$true;scope='native-background-editor-lifecycle';checks=$checks} | ConvertTo-Json -Depth 12))
    Write-Output "Background Editor contracts passed: $reportPath"
    return
}
$ready = Get-Content -LiteralPath (Join-Path $jobs 'worker.json') -Raw | ConvertFrom-Json
if ($ready.schemaVersion -ne 2) { throw 'Refresh the current GUI worker before running its contracts.' }
$originalProcess = Get-Process -Id $ready.processId
$originalStart = $originalProcess.StartTime.ToUniversalTime()
$inputHash = if ($fbx) { (Get-FileHash -LiteralPath $fbx -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }

function Get-JobIds {
    @(Get-ChildItem -LiteralPath $jobs -Directory | Where-Object Name -Match '^[a-f0-9]{32}$' | ForEach-Object Name)
}

function Invoke-NativeBuild([string] $Label) {
    $before = Get-JobIds
    & $launcher -UnityPath $UnityPath -FbxPath $fbx -GuiProjectPath $project -TimeoutSeconds $TimeoutSeconds -InstallGuiWorker:$InstallGuiWorker
    $created = @(Get-JobIds | Where-Object { $_ -notin $before })
    Add-Check ('unique-job-' + $Label) ($created.Count -eq 1) $created
    $reportPath = Join-Path $jobs ($created[0] + '\Output\unity-report.json')
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    Add-Check ('native-build-reload-' + $Label) ($report.success -and $report.stage -eq 'complete' -and
        $report.reloaded.sampledFrames -eq 25 -and $report.reloaded.deformedFrames -gt 0 -and
        $report.reloaded.boneCount -eq $report.imported.boneCount -and $report.reloaded.vertexCount -eq $report.imported.vertexCount -and
        $report.activeScenePathBefore -eq $report.activeScenePathAfter -and
        $report.activeSceneDirtyBefore -eq $report.activeSceneDirtyAfter -and $report.sceneCountBefore -eq $report.sceneCountAfter) $reportPath
}

function Wait-ReloadedSource([string] $Hash, [DateTime] $PreviousAssemblyTime) {
    $readyPath = Join-Path $jobs 'worker.json'
    $assemblyPath = Join-Path $project 'Library\ScriptAssemblies\Assembly-CSharp-Editor.dll'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $receipt = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
        $process = Get-Process -Id $ready.processId
        if ($process.StartTime.ToUniversalTime() -ne $originalStart) { throw 'The original GUI worker process ended.' }
        $compiledAt = (Get-Item -LiteralPath $assemblyPath).LastWriteTimeUtc
        if ($receipt.schemaVersion -eq 2 -and $receipt.sourceSha256 -eq $Hash -and $receipt.mainThread -eq $ready.mainThread -and
            $compiledAt -gt $PreviousAssemblyTime -and
            ([DateTime]::UtcNow - ([DateTime]$receipt.updatedUtc).ToUniversalTime()).TotalSeconds -lt 5) { return $compiledAt }
        Start-Sleep -Milliseconds 250
    }
    throw 'The modified fixed worker source did not self-import and recompile.'
}

function Submit-RejectedJob([string] $Kind) {
    $id = [guid]::NewGuid().ToString('N')
    $jobRoot = Join-Path $jobs $id
    $input = Join-Path $project ('Assets\AuthoringJobs\' + $id + '\Input')
    [IO.Directory]::CreateDirectory($jobRoot) | Out-Null
    [IO.Directory]::CreateDirectory($input) | Out-Null
    [IO.File]::WriteAllText((Join-Path $jobRoot '.solasta-asset-authoring-job'), 'SolastaUBAssetAuthoring:UnityJob:1')
    [IO.File]::Copy($fbx, (Join-Path $input 'SampleRig.fbx'), $false)
    $request = @{ schemaVersion = 1; jobId = $id; inputSha256 = $inputHash }
    switch ($Kind) {
        'changed-input' { $request.inputSha256 = '0' * 64 }
        'schema' { $request.schemaVersion = 2 }
        'identity' { $request.jobId = [guid]::NewGuid().ToString('N') }
        'interrupted' { [IO.File]::WriteAllText((Join-Path $jobRoot 'started.json'), [DateTime]::UtcNow.ToString('o')) }
        default { throw "Unknown contract: $Kind" }
    }
    [IO.File]::WriteAllText((Join-Path $jobRoot 'request.writing'), ($request | ConvertTo-Json))
    [IO.File]::Move((Join-Path $jobRoot 'request.writing'), (Join-Path $jobRoot 'request.json'))
    $reportPath = Join-Path $jobRoot 'Output\unity-report.json'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (!(Test-Path -LiteralPath $reportPath -PathType Leaf) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (!(Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw "Rejected job did not report its failure: $jobRoot" }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    Add-Check ('reject-' + $Kind) (!$report.success -and $report.error -and
        $report.jobId -eq $id -and $report.processId -eq $ready.processId -and $report.mainThread -eq $ready.mainThread -and
        !(Test-Path -LiteralPath (Join-Path $jobRoot 'Output\ub_authoring_sample'))) $reportPath
}

function Test-MaterialJobs {
    $recipePath = (Resolve-Path -LiteralPath $MaterialRecipePath).ProviderPath
    $before = Get-JobIds
    & $launcher -UnityPath $UnityPath -MaterialRecipePath $recipePath -GuiProjectPath $project -TimeoutSeconds $TimeoutSeconds -InstallGuiWorker:$InstallGuiWorker
    $created = @(Get-JobIds | Where-Object { $_ -notin $before })
    Add-Check 'unique-material-job' ($created.Count -eq 1) $created
    $reportPath = Join-Path $jobs ($created[0] + '\Output\unity-report.json')
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    Add-Check 'native-material-reload' ($report.success -and $report.validationScope -eq 'materials' -and
        @($report.materials).Count -gt 0 -and @($report.materials | Where-Object { !$_.shaderSupported -or !$_.passes }).Count -eq 0) $reportPath
    $fixture = Join-Path $jobs ('MaterialContracts\' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($fixture) | Out-Null
    $originalRecipe = Get-Content -LiteralPath $recipePath -Raw | ConvertFrom-Json
    foreach ($asset in $originalRecipe.assets) {
        $destination = Join-Path $fixture $asset.path
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy((Join-Path ([IO.Path]::GetDirectoryName($recipePath)) $asset.path), $destination, $false)
    }
    foreach ($kind in @('required-pass', 'property-type', 'asset-hash', 'unknown-field', 'numeric-type', 'asset-path')) {
        $recipe = Get-Content -LiteralPath $recipePath -Raw | ConvertFrom-Json
        switch ($kind) {
            'required-pass' { $recipe.materials[0].requiredPasses = @('__MissingValidationPass__') }
            'property-type' {
                $color = @($recipe.materials[0].properties | Where-Object kind -eq 'color')[0]
                $recipe.materials[0].properties = @([pscustomobject]@{ name = $color.name; kind = 'float'; value = 0.5 })
            }
            'asset-hash' { $recipe.assets[0].sha256 = '0' * 64 }
            'unknown-field' { $recipe | Add-Member -NotePropertyName unsupported -NotePropertyValue $true }
            'numeric-type' { @($recipe.materials[0].properties | Where-Object kind -eq 'float')[0].value = 'invalid' }
            'asset-path' { $recipe.assets[0].path = '../Outside.shader' }
        }
        $input = Join-Path $fixture ($kind + '.json')
        [IO.File]::WriteAllText($input, ($recipe | ConvertTo-Json -Depth 20))
        $before = Get-JobIds
        $rejected = $false
        try { & $launcher -UnityPath $UnityPath -MaterialRecipePath $input -GuiProjectPath $project -TimeoutSeconds $TimeoutSeconds }
        catch { $rejected = $true }
        $created = @(Get-JobIds | Where-Object { $_ -notin $before })
        if ($kind -in @('required-pass', 'property-type')) {
            Add-Check ('material-native-reject-' + $kind) ($rejected -and $created.Count -eq 1) $created
            $failedPath = Join-Path $jobs ($created[0] + '\Output\unity-report.json')
            $failed = Get-Content -LiteralPath $failedPath -Raw | ConvertFrom-Json
            Add-Check ('material-failed-report-' + $kind) (!$failed.success -and $failed.error -and $failed.validationScope -eq 'materials' -and
                !(Test-Path -LiteralPath (Join-Path $jobs ($created[0] + '\Output\' + $originalRecipe.bundleName)))) $failedPath
        }
        else { Add-Check ('material-preflight-reject-' + $kind) ($rejected -and $created.Count -eq 0) $input }
    }
}

function Test-MaterialIdentity {
    $id = [guid]::NewGuid().ToString('N')
    $jobRoot = Join-Path $jobs $id
    $input = Join-Path $project ('Assets\AuthoringJobs\' + $id + '\Input')
    [IO.Directory]::CreateDirectory($jobRoot) | Out-Null
    [IO.File]::WriteAllText((Join-Path $jobRoot '.solasta-asset-authoring-job'), 'SolastaUBAssetAuthoring:UnityJob:1')
    $assets = @()
    Add-Type -AssemblyName System.Drawing
    foreach ($side in @('A', 'B')) {
        $directory = Join-Path $input $side
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        $tint = if ($side -eq 'A') { 'fixed3(1, 0.5, 0.5)' } else { 'fixed3(0.5, 0.5, 1)' }
        $shader = @"
Shader "AuthoringContracts/SharedIdentity" {
 Properties { _MainTex ("Albedo", 2D) = "white" {} }
 SubShader { Tags { "RenderType" = "Opaque" }
 CGPROGRAM
 #pragma target 3.0
 #pragma surface surf Standard
 sampler2D _MainTex;
 struct Input { float2 uv_MainTex; };
 void surf(Input input, inout SurfaceOutputStandard output) {
  output.Albedo = tex2D(_MainTex, input.uv_MainTex).rgb * $tint;
  output.Alpha = 1;
 }
 ENDCG
 }
 FallBack Off
}
"@
        [IO.File]::WriteAllText((Join-Path $directory 'Shared.shader'), $shader)
        $bitmap = [Drawing.Bitmap]::new(2, 2)
        try {
            $color = if ($side -eq 'A') { [Drawing.Color]::Red } else { [Drawing.Color]::Blue }
            foreach ($x in 0..1) { foreach ($y in 0..1) { $bitmap.SetPixel($x, $y, $color) } }
            $bitmap.Save((Join-Path $directory 'Shared.png'), [Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $bitmap.Dispose() }
        foreach ($name in @('Shared.shader', 'Shared.png')) {
            $assets += @{path="$side/$name"; sha256=(Get-FileHash -LiteralPath (Join-Path $directory $name) -Algorithm SHA256).Hash.ToLowerInvariant()}
        }
    }
    $recipe = @{schemaVersion=1; bundleName='material_identity_contract'; assets=$assets; materials=@(
        @{name='Expected';shader='A/Shared.shader';requiredPasses=@('FORWARD');properties=@(@{name='_MainTex';kind='texture';texture='A/Shared.png'})},
        @{name='Alternative';shader='B/Shared.shader';requiredPasses=@('FORWARD');properties=@(@{name='_MainTex';kind='texture';texture='B/Shared.png'})}
    )}
    $recipePath = Join-Path $input 'recipe.json'
    [IO.File]::WriteAllText($recipePath, ($recipe | ConvertTo-Json -Depth 10))
    $request = @{schemaVersion=1;jobId=$id;kind='material-identity-contracts';inputSha256=(Get-FileHash -LiteralPath $recipePath -Algorithm SHA256).Hash.ToLowerInvariant()}
    [IO.File]::WriteAllText((Join-Path $jobRoot 'request.writing'), ($request | ConvertTo-Json))
    [IO.File]::Move((Join-Path $jobRoot 'request.writing'), (Join-Path $jobRoot 'request.json'))
    $reportPath = Join-Path $jobRoot 'Output\unity-report.json'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (!(Test-Path -LiteralPath $reportPath -PathType Leaf) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (!(Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw "The material identity job did not complete: $jobRoot" }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    Add-Check 'material-identity-native-reload' ($report.success -and $report.validationScope -eq 'material_identity_contracts' -and
        $report.jobId -eq $id -and $report.processId -eq $ready.processId -and $report.mainThread -eq $ready.mainThread -and
        @($report.identityContracts).Count -eq 2 -and $report.activeScenePathBefore -eq $report.activeScenePathAfter -and
        $report.activeSceneDirtyBefore -eq $report.activeSceneDirtyAfter -and $report.sceneCountBefore -eq $report.sceneCountAfter) $reportPath
    foreach ($kind in @('shader', 'texture')) {
        $contract = @($report.identityContracts | Where-Object kind -eq $kind)
        Add-Check ('material-reject-same-name-' + $kind) ($contract.Count -eq 1 -and $contract[0].rejected -and
            $contract[0].expectedName -eq $contract[0].alternativeName -and $contract[0].expectedAsset -ne $contract[0].alternativeAsset -and
            $contract[0].expectedInstanceId -ne $contract[0].alternativeInstanceId -and $contract[0].rejection) $reportPath
    }
}

$resultPath = Join-Path $jobs ('contracts-' + [guid]::NewGuid().ToString('N') + '.json')
$completed = $false
$failure = $null
try {
    if (!$MaterialsOnly) {
        foreach ($iteration in 1..2) { Invoke-NativeBuild $iteration }
        foreach ($kind in @('changed-input', 'schema', 'identity', 'interrupted')) { Submit-RejectedJob $kind }
        $source = Join-Path $project 'Assets\Editor\AssetBundleBuilder.cs'
        $originalBytes = [IO.File]::ReadAllBytes($source)
        $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
        $assemblyTime = (Get-Item -LiteralPath (Join-Path $project 'Library\ScriptAssemblies\Assembly-CSharp-Editor.dll')).LastWriteTimeUtc
        try {
            [IO.File]::AppendAllText($source, ([Environment]::NewLine + '// Reload contract ' + [guid]::NewGuid().ToString('N') + [Environment]::NewLine))
            $changedHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
            $assemblyTime = Wait-ReloadedSource $changedHash $assemblyTime
            Add-Check 'changed-worker-self-reloaded' $true $changedHash
        }
        finally {
            [IO.File]::WriteAllBytes($source, $originalBytes)
            $null = Wait-ReloadedSource $sourceHash $assemblyTime
            Add-Check 'original-worker-self-reloaded' $true $sourceHash
        }
        Invoke-NativeBuild 'after-reload'
    }
    if (![string]::IsNullOrWhiteSpace($MaterialRecipePath)) { Test-MaterialJobs; Test-MaterialIdentity }
    $process = Get-Process -Id $ready.processId
    Add-Check 'existing-editor-kept-running' ($process.StartTime.ToUniversalTime() -eq $originalStart) $process.Id
    if ($fbx) { Add-Check 'source-fbx-unchanged' ((Get-FileHash -LiteralPath $fbx -Algorithm SHA256).Hash.ToLowerInvariant() -eq $inputHash) $inputHash }
    $completed = $true
}
catch {
    $failure = $_.Exception.ToString()
    throw
}
finally {
    [IO.File]::WriteAllText($resultPath, (@{ schemaVersion = 1; checks = @($checks.ToArray()); passed = $completed -and @($checks | Where-Object { !$_.passed }).Count -eq 0; error = $failure;
        unityVersion = $ready.unityVersion; processId = $ready.processId; completedUtc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json -Depth 8))
    Write-Output "Unity worker contract results: $resultPath"
}
