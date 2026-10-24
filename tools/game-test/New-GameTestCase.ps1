[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SavePath,
    [Parameter(Mandatory=$true)][string]$SettingsFile,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$')][string]$Id='save-move-smoke',
    [ValidateRange(0,2147483647)][int]$ActorIndex=0,
    [ValidateSet('party','guest')][string]$ActorSource='party',
    [ValidateCount(3,3)][int[]]$Offset=@(1,0,0),
    [ValidateRange(1,6)][int]$LoadedPartyCount=4,
    [ValidateRange(30,1800)][int]$TimeoutSeconds=300,
    [switch]$ManualGate,
    [switch]$VerifyFocusLoss,
    [ValidateRange(1,1800)][int]$ManualGateTimeoutSeconds=120,
    [ValidateNotNullOrEmpty()][string]$SetupPowerName,
    [ValidateSet('party','guest')][string]$SetupActorSource='party',
    [ValidateRange(0,2147483647)][int]$SetupActorIndex=0,
    [ValidateCount(3,3)][int[]]$SetupPositionOffset=@(1,0,0),
    [ValidateRange(0,32)][int]$SetupExpectedGuestCount=1
)
. (Join-Path $PSScriptRoot 'GameTest.Common.ps1')
$output=Get-GtPath $OutputDirectory
Assert-GtPlainPath $output
if (Test-Path -LiteralPath $output) { throw 'Use a new OutputDirectory for a fixed fixture.' }
Assert-GtFile $SavePath; Assert-GtFile $SettingsFile
if ([IO.Path]::GetExtension($SavePath) -ne '.sav') { throw 'SavePath must name a .sav file.' }
[xml]$settingsXml=[IO.File]::ReadAllText((Get-GtPath $SettingsFile))
if ($settingsXml.DocumentElement.Name -ne 'Settings') { throw 'SettingsFile must contain UB Settings XML.' }
foreach ($value in $Offset) { if ([Math]::Abs($value) -gt 3) { throw 'Offset must be within three cells per axis.' } }
if (@($Offset | Where-Object { $_ -ne 0 }).Count -eq 0) { throw 'Offset must change position.' }
if ($ManualGate -and $ManualGateTimeoutSeconds -gt $TimeoutSeconds) { throw 'ManualGateTimeoutSeconds must not exceed TimeoutSeconds.' }
if (-not $ManualGate -and $PSBoundParameters.ContainsKey('ManualGateTimeoutSeconds')) { throw 'Use ManualGate with ManualGateTimeoutSeconds.' }
if ($SetupPowerName) {
    if ([string]::IsNullOrWhiteSpace($SetupPowerName)) { throw 'SetupPowerName must name an owned usable power.' }
    foreach ($value in $SetupPositionOffset) { if ($value -lt -3 -or $value -gt 3) { throw 'SetupPositionOffset must be within three cells per axis.' } }
    if (@($SetupPositionOffset | Where-Object { $_ -ne 0 }).Count -eq 0) { throw 'SetupPositionOffset must change position.' }
} elseif (@($PSBoundParameters.Keys | Where-Object { $_ -like 'Setup*' }).Count) { throw 'SetupPowerName is required with setup options.' }
New-Item -ItemType Directory -Path $output | Out-Null
$fixedSettings=Join-Path $output 'Settings.xml'
Copy-GtFile $SettingsFile $fixedSettings
$nativePreferences=@()
$native=Join-Path $env:USERPROFILE 'AppData\LocalLow\Tactical Adventures\Solasta'
foreach ($kind in @('UserPreferences','PlatformPreferences')) {
    $path=Join-Path $native ($kind + '.xml')
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $fixedPath=Join-Path $output ($kind + '.xml')
        Copy-GtFile $path $fixedPath
        $nativePreferences += [ordered]@{kind=$kind; path=$fixedPath; sha256=Get-GtHash $fixedPath}
    }
}
$case=[ordered]@{
    schemaVersion=1; id=$Id; adapter='save-smoke'
    initialState=[ordered]@{ savePath=Get-GtPath $SavePath; saveSha256=Get-GtHash $SavePath; settingsFile=$fixedSettings; settingsSha256=Get-GtHash $fixedSettings; nativePreferences=$nativePreferences }
    operation=[ordered]@{ kind='move'; actorSource=$ActorSource; actorIndex=$ActorIndex; offset=$Offset; manualGate=[bool]$ManualGate; manualGateTimeoutSeconds=$(if($ManualGate){$ManualGateTimeoutSeconds}else{0}); verifyFocusLoss=[bool]$VerifyFocusLoss }
    expected=[ordered]@{ loadedPartyCount=$LoadedPartyCount; requireUserLocation=$true; requireExploration=$true }
    observation=[ordered]@{ enabled=$false }; timeoutSeconds=$TimeoutSeconds
}
if ($SetupPowerName) {
    $case.initialState['setup']=[ordered]@{kind='use-power';actorSource=$SetupActorSource;actorIndex=$SetupActorIndex;powerName=$SetupPowerName;positionOffset=$SetupPositionOffset;expectedGuestCount=$SetupExpectedGuestCount}
}
$path=Join-Path $output 'Case.json'
Write-GtJson $path $case
[void](Read-GtCase $path)
Write-Host "Case: $path"
$path
