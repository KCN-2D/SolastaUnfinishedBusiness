# Resumable character-model recipe closure; Unity contracts stay in the shared worker.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string[]]$PlanPath,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$GuiProjectPath,
    [Parameter(Mandatory=$true)][string]$UnityPath,
    [string[]]$ReuseResultPath = @(),
    [ValidateRange(2,16)][int]$ChunkSize = 8,
    [switch]$ValidateOnly,
    [string]$UnityCliPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../..')).Path
$authoring = Join-Path $repository 'tools/asset-authoring'
$entry = Join-Path $authoring 'Invoke-UnityAssetBuild.ps1'
$backgroundEditor = Join-Path $authoring 'UnityAssetBuild/BackgroundEditor.ps1'
$worker = Join-Path $authoring 'UnityAssetBuild/Assets/Editor/AssetBundleBuilder.cs'
$project = (Resolve-Path -LiteralPath $GuiProjectPath).Path
$editor = (Resolve-Path -LiteralPath $UnityPath).Path
$root = [IO.Path]::GetFullPath($OutputDirectory)
$tools = Join-Path $repository 'tools'
$progress = Join-Path $root 'Actual.json'
$bundleReport = Join-Path $root 'unity-report.json'
$stop = Join-Path $root 'STOP'

. $backgroundEditor
if (!(Test-AssetStorageWithin (Get-AssetStoragePath $root) $tools) -or $root -ieq $tools) {
    throw 'Model build output must stay inside tools.'
}

function Get-RecipeHash([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-BuildRecord([string]$Path, $Value) {
    $temporary = $Path + '.writing'
    [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 90 -Compress), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

$planIdentities = @(foreach ($path in $PlanPath) {
    $file = (Resolve-Path -LiteralPath $path).Path
    [pscustomobject]@{path=$file;sha256=Get-RecipeHash $file}
})
$declarations = @(foreach ($pin in $planIdentities) {
    $plan = Get-Content -LiteralPath $pin.path -Raw | ConvertFrom-Json
    if ((Get-RecipeHash $pin.path) -cne $pin.sha256) { throw 'A model plan changed while it was read.' }
    if ($plan.PSObject.Properties['baseManifest'] -and
        (Get-RecipeHash $plan.baseManifest.path) -cne $plan.baseManifest.sha256) {
        throw 'The admitted original public catalog changed.'
    }
    if ($plan.PSObject.Properties['jobs']) {
        foreach ($job in $plan.jobs) {
            [pscustomobject]@{path=(Resolve-Path -LiteralPath $job.recipe.path).Path;expected=$job}
        }
    } elseif ($plan.PSObject.Properties['recipes']) {
        foreach ($recipePath in $plan.recipes) {
            [pscustomobject]@{path=(Resolve-Path -LiteralPath $recipePath).Path;expected=$null}
        }
    } else {
        throw 'A model plan requires jobs or mesh recipes.'
    }
})
if (!$declarations.Count -or @($declarations | Group-Object path | Where-Object Count -NE 1).Count) {
    throw 'Every model recipe must be declared once.'
}

# Check current dependency bytes through the same entry used for compilation.
$jobs = @(for ($first = 0; $first -lt $declarations.Count; $first += $ChunkSize) {
    $batch = @($declarations | Select-Object -Skip $first -First $ChunkSize)
    $validated = & $entry -UnityPath $editor -GuiProjectPath $project -MeshRecipePath ([string[]]$batch.path) -ValidateOnly
    if ($validated.status -cne 'PASS_AUTHORING_INPUT' -or @($validated.inputs).Count -ne $batch.Count) {
        throw 'The common worker did not validate the whole recipe batch.'
    }
    for ($index = 0; $index -lt $batch.Count; $index++) {
        $job = $validated.inputs[$index]
        $explicit = $batch[$index].expected
        if ($job.path -cne $batch[$index].path -or ($explicit -and (
            $job.sha256 -cne $explicit.recipe.sha256 -or $job.bundleName -cne $explicit.outputFile -or
            $job.meshes -ne $explicit.meshes -or $job.sourceBundles -ne $explicit.sourceBundles -or
            $job.sourceAssets -ne $explicit.sourceAssets))) {
            throw "A validated recipe differs from its declared job: $($job.path)"
        }
        $job
    }
})
if (@($jobs | Group-Object bundleName | Where-Object Count -NE 1).Count) {
    throw 'Every model bundle needs one recipe.'
}
if ($ValidateOnly) {
    Write-Output ('PASS frozen inputs ' + $jobs.Count)
    return
}

$workerHash = Get-RecipeHash $worker
$engine = @(foreach ($path in @($editor,$entry,$backgroundEditor,$worker)) {
    Get-RecipeHash $path
})
$buildIdentity = [ordered]@{
    toolSha256=$engine
    projectFiles=@(foreach ($directory in 'ProjectSettings','Packages') {
        $folder = Join-Path $project $directory
        if (!(Test-Path -LiteralPath $folder -PathType Container)) {
            [ordered]@{path=$directory;sha256=$null}
            continue
        }
        foreach ($file in (Get-ChildItem -LiteralPath $folder -File | Sort-Object Name)) {
            [ordered]@{path=($directory+'/'+$file.Name);sha256=Get-RecipeHash $file.FullName}
        }
    })
    colorSpace='Linear'
    target='StandaloneWindows64'
}
$buildKey = $buildIdentity | ConvertTo-Json -Depth 5 -Compress
$results = [Collections.Generic.List[object]]::new()
$chunks = [Collections.Generic.List[object]]::new()
$session = $null
$shutdown = $false
$batchError = $null
$paused = $false
$clock = [Diagnostics.Stopwatch]::StartNew()

function Save-Progress([string]$Status) {
    Write-BuildRecord $progress ([ordered]@{
        schemaVersion=2;status=$Status;plans=@($planIdentities.path);planInputs=$planIdentities
        buildIdentity=$buildIdentity;workerSha256=$workerHash;editorNormallyReleased=$shutdown
        session=$(if ($session) { $session.RunDirectory } else { $null })
        expectedJobs=$jobs.Count;completedJobs=$results.Count;reusedJobs=@($results | Where-Object reused).Count
        chunkSize=$ChunkSize;chunks=$chunks.ToArray();results=$results.ToArray()
        bundleReport=$(if ($Status -ceq 'COMPLETE_HUMANOID_ENGINE_BATCH') { $bundleReport } else { $null })
        error=$batchError;elapsedSeconds=$clock.Elapsed.TotalSeconds;updatedUtc=[DateTime]::UtcNow.ToString('O')
    })
}

function Assert-JobReport($JobReport, $Job) {
    if (!$JobReport.success -or $JobReport.stage -cne 'complete' -or $JobReport.validationScope -cne 'mesh_templates' -or
        $JobReport.inputSha256 -cne $Job.sha256 -or $JobReport.compiledSourceSha256 -cne $workerHash -or
        @($JobReport.meshes).Count -ne $Job.meshes -or @($JobReport.sourceAssets).Count -ne $Job.sourceAssets -or
        $JobReport.sourceBundlesLoaded -ne $Job.sourceBundles -or $JobReport.sourceBundlesReleased -ne $Job.sourceBundles) {
        throw "The report does not match its validated mesh recipe: $($Job.bundleName)"
    }
}

function Save-ModelMember($Job, $JobReport, [string]$ArchiveName, [bool]$Reused) {
    Assert-JobReport $JobReport $Job
    $bundle = Get-AssetStoragePath $JobReport.bundlePath -ReadOnlyReference
    if ((Get-RecipeHash $bundle) -cne $JobReport.bundleSha256) { throw 'The member bundle differs from its report.' }
    $archive = Join-Path $root ('Built/' + $ArchiveName)
    $proofRoot = Join-Path $root ('Reports/' + $ArchiveName)
    [void][IO.Directory]::CreateDirectory($archive)
    [void][IO.Directory]::CreateDirectory($proofRoot)
    $target = Join-Path $archive $Job.bundleName
    $proof = Join-Path $proofRoot ($Job.bundleName + '.json')
    if (Test-Path -LiteralPath $target) { throw 'The completed output already exists.' }
    New-Item -ItemType HardLink -Path ('\\?\' + $target) -Target ('\\?\' + $bundle) | Out-Null
    $JobReport.bundlePath = $target
    Write-BuildRecord $proof $JobReport
    $results.Add([pscustomobject]@{
        file=$Job.bundleName;inputSha256=$Job.sha256;bundlePath=$target;sha256=$JobReport.bundleSha256
        bytes=(Get-Item -LiteralPath $target).Length;report=$proof;reportSha256=Get-RecipeHash $proof;reused=$Reused
    })
}

function Import-ModelReceipt([string]$ReceiptPath, [bool]$Resume) {
    $receipt = Get-Content -LiteralPath (Get-AssetStoragePath $ReceiptPath -ReadOnlyReference) -Raw | ConvertFrom-Json
    if (!$receipt.PSObject.Properties['schemaVersion'] -or $receipt.schemaVersion -ne 2 -or
        !$receipt.editorNormallyReleased -or ($receipt.buildIdentity | ConvertTo-Json -Depth 5 -Compress) -cne $buildKey) {
        if ($Resume) { throw 'Use a new output directory. The previous build has no matching released tool and project identity.' }
        return
    }
    if ($Resume -and (ConvertTo-Json -InputObject @($receipt.planInputs) -Compress) -cne
        (ConvertTo-Json -InputObject $planIdentities -Compress)) {
        throw 'The resumed recipe plans differ. Use a new output directory with -ReuseResultPath.'
    }
    foreach ($row in $receipt.results) {
        $job = $jobs | Where-Object { $_.bundleName -ceq $row.file -and $_.sha256 -ceq $row.inputSha256 }
        if (!$job -or $row.file -cin @($results | ForEach-Object file)) { continue }
        try {
            $proof = Get-AssetStoragePath $row.report -ReadOnlyReference
            $path = Get-AssetStoragePath $row.bundlePath -ReadOnlyReference
            if ((Get-RecipeHash $proof) -cne $row.reportSha256 -or (Get-Item -LiteralPath $path).Length -ne $row.bytes) {
                throw 'A verified recipe report or output length changed.'
            }
            $report = Get-Content -LiteralPath $proof -Raw | ConvertFrom-Json
            Assert-JobReport $report $job
            if ($report.bundlePath -cne $path -or $report.bundleSha256 -cne $row.sha256) {
                throw 'The verified recipe report and output identity differ.'
            }
            if ($Resume) {
                if ((Get-RecipeHash $path) -cne $row.sha256) { throw 'A completed bundle changed before resume.' }
                $results.Add($row)
            } else {
                Save-ModelMember -Job $job -JobReport $report -ArchiveName ('Reuse-' + [guid]::NewGuid().ToString('N').Substring(0,8)) -Reused $true
            }
        } catch {
            if ($Resume) { throw }
            Write-Verbose "Ignored a reusable member of $ReceiptPath because $($_.Exception.Message)"
        }
    }
    if ($Resume) { foreach ($chunk in $receipt.chunks) { $chunks.Add($chunk) } }
}

$buildLock = Enter-AssetStorageOperation $root
try {
    [void][IO.Directory]::CreateDirectory($root)
    if (Test-Path -LiteralPath $progress) { Import-ModelReceipt $progress $true }
    foreach ($path in $ReuseResultPath) { Import-ModelReceipt $path $false }
    if ($results.Count -lt $jobs.Count -and !(Test-Path -LiteralPath $stop)) {
        if (!$UnityCliPath) { $UnityCliPath = Join-Path $env:LOCALAPPDATA 'Unity/bin/unity.exe' }
        $session = Start-AssetBackgroundEditor -Project $project -Editor $editor -Cli $UnityCliPath -SourceSha256 $workerHash -TimeoutSeconds 300
    }
    while ($results.Count -lt $jobs.Count) {
        if (Test-Path -LiteralPath $stop) { $paused = $true; break }
        $completedNames = @($results | ForEach-Object file)
        $selected = @($jobs | Where-Object bundleName -CNotIn $completedNames | Select-Object -First $ChunkSize)
        Save-Progress 'RUNNING_HUMANOID_ENGINE_BATCH'
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $output = & $entry -UnityPath $editor -MeshRecipePath ([string[]]$selected.path) -GuiProjectPath $project -TimeoutSeconds 1200
        $timer.Stop()
        $prefix = 'Unity AssetBundle and reload validation succeeded: '
        $location = @($output | Where-Object { $_ -is [string] -and $_.StartsWith($prefix, [StringComparison]::Ordinal) })
        if ($location.Count -ne 1) { throw 'The common worker returned no unique validated report.' }
        $rootReport = $location[0].Substring($prefix.Length)
        $report = Get-Content -LiteralPath $rootReport -Raw | ConvertFrom-Json
        $children = @(if ($selected.Count -gt 1) { $report.bundles } else { $report })
        if (!$report.success -or $children.Count -ne $selected.Count -or
            ($selected.Count -gt 1 -and $report.buildPipelineInvocations -ne 1)) {
            throw 'A chunk failed its bundle closure.'
        }
        $archiveName = Split-Path -Leaf (Split-Path -Parent (Split-Path -Parent $rootReport))
        for ($index = 0; $index -lt $selected.Count; $index++) {
            Save-ModelMember -Job $selected[$index] -JobReport $children[$index] -ArchiveName $archiveName -Reused $false
            Save-Progress 'RUNNING_HUMANOID_ENGINE_BATCH'
        }
        $chunks.Add([pscustomobject]@{
            report=$rootReport;members=@($selected.bundleName);wrapperSeconds=$timer.Elapsed.TotalSeconds
            buildPipelineInvocations=1
        })
        Write-Output ('Completed ' + $results.Count + '/' + $jobs.Count + ' in ' + [math]::Round($timer.Elapsed.TotalSeconds,2) + ' seconds')
    }
} catch {
    $batchError = $_.Exception.ToString()
    throw
} finally {
    try {
        if ($session) { Stop-AssetBackgroundEditor $session }
        $shutdown = $true
        if (!$batchError -and !$paused -and $results.Count -eq $jobs.Count) {
            $members = @($jobs | ForEach-Object {
                $file = $_.bundleName
                $record = $results | Where-Object file -CEQ $file
                Get-Content -LiteralPath $record.report -Raw | ConvertFrom-Json
            })
            $versions = @($members.unityVersion | Sort-Object -Unique)
            if ($versions.Count -ne 1 -or [string]::IsNullOrWhiteSpace($versions[0])) {
                throw 'The validated model bundle receipts disagree on the Unity version.'
            }
            Write-BuildRecord $bundleReport ([ordered]@{
                success=$true;unityVersion=$versions[0];bundles=$members;buildPipelineInvocations=$chunks.Count
            })
            Save-Progress 'COMPLETE_HUMANOID_ENGINE_BATCH'
            Write-Output "Model bundle and reload validation succeeded: $bundleReport"
        } else {
            Save-Progress $(if ($paused -and !$batchError) { 'PAUSED_AT_COMPLETED_CHUNK' } else { 'FAIL' })
        }
    } catch {
        if (!$batchError) { $batchError = $_.Exception.ToString() }
        Save-Progress 'FAIL'
        throw
    } finally {
        $buildLock.ReleaseMutex()
        $buildLock.Dispose()
    }
}
