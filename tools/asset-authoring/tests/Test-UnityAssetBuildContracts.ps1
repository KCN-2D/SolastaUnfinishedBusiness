[CmdletBinding(DefaultParameterSetName = 'All')]
param(
    [Parameter(Mandatory = $true)] [string] $UnityPath,
    [Parameter(Mandatory = $true, ParameterSetName = 'All')] [string] $FbxPath,
    [Parameter(Mandatory = $true)] [string] $GuiProjectPath,
    [Parameter(ParameterSetName = 'All')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Materials')] [string] $MaterialRecipePath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Materials')] [switch] $MaterialsOnly,
    [switch] $InstallGuiWorker,
    [ValidateRange(5, 300)] [int] $TimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$launcher = Join-Path $PSScriptRoot '..\Invoke-UnityAssetBuild.ps1'
$project = (Resolve-Path -LiteralPath $GuiProjectPath).ProviderPath
$fbx = if (!$MaterialsOnly) { (Resolve-Path -LiteralPath $FbxPath).ProviderPath } else { $null }
$marker = Join-Path $project '.solasta-asset-authoring-project'
if (!(Test-Path -LiteralPath $marker -PathType Leaf) -or
    [IO.File]::ReadAllText($marker).Trim() -ne 'SolastaUBAssetAuthoring:UnityProject:1') { throw 'A marked authoring project is required.' }
$jobs = Join-Path $project '.asset-authoring-jobs'
$ready = Get-Content -LiteralPath (Join-Path $jobs 'worker.json') -Raw | ConvertFrom-Json
if ($ready.schemaVersion -ne 2) { throw 'Refresh the current GUI worker before running its contracts.' }
$originalProcess = Get-Process -Id $ready.processId
$originalStart = $originalProcess.StartTime.ToUniversalTime()
$inputHash = if ($fbx) { (Get-FileHash -LiteralPath $fbx -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
$checks = [Collections.Generic.List[object]]::new()

function Add-Check([string] $Name, [bool] $Passed, [object] $Evidence) {
    $checks.Add([pscustomobject]@{ name = $Name; passed = $Passed; evidence = $Evidence })
    if (!$Passed) { throw "Contract failed: $Name" }
}

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
