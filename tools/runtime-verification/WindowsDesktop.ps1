# Runs on an owned non-input desktop and reads metadata; no host input.
function Get-VerificationProcessState {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$Name)
    $processes=[Collections.Generic.List[object]]::new()
    $queryError=$null
    $absenceBasis=$null
    try {
        Get-Process -Name $Name -ErrorAction Stop | ForEach-Object { $processes.Add($_) }
        if ($processes.Count -eq 0) { $absenceBasis='completed-empty-query' }
    } catch {
        if ($processes.Count -eq 0 -and $_.FullyQualifiedErrorId.Split(',')[0] -ceq 'NoProcessFoundForGivenName' -and
            $_.CategoryInfo.Category -eq [Management.Automation.ErrorCategory]::ObjectNotFound) {
            $absenceBasis='NoProcessFoundForGivenName'
        } else {
            $queryError=[ordered]@{id=$_.FullyQualifiedErrorId;category=[string]$_.CategoryInfo.Category;message=$_.Exception.Message}
        }
    }
    $matches=@(foreach ($process in $processes) {
        $entry=[ordered]@{pid=$null;startTimeUtc=$null;path=$null;readErrors=[ordered]@{}}
        try { $entry.pid=[int]$process.Id }
        catch { $entry.readErrors['pid']=$_.Exception.Message }
        try { $entry.startTimeUtc=$process.StartTime.ToUniversalTime().ToString('O') }
        catch { $entry.readErrors['startTimeUtc']=$_.Exception.Message }
        try {
            $entry.path=[string]$process.Path
            if ([string]::IsNullOrEmpty($entry.path)) { throw 'The executable path is unavailable.' }
        } catch { $entry.readErrors['path']=$_.Exception.Message }
        $entry
    })
    [pscustomobject]@{observedUtc=[datetime]::UtcNow.ToString('O');name=$Name;querySucceeded=($null -eq $queryError);absenceBasis=$absenceBasis;matches=$matches;queryError=$queryError}
}

function Assert-VerificationGameStopped {
    $processState=Get-VerificationProcessState -Name Solasta
    $processDiagnostic=$processState | ConvertTo-Json -Depth 6 -Compress
    if (-not $processState.querySucceeded) {
        throw ('Cannot verify that Solasta is stopped. Process state: ' + $processDiagnostic)
    }
    if ($processState.matches.Count) {
        throw ('Close Solasta before modifying game files or preferences. Process state: ' + $processDiagnostic)
    }
}

function Get-VerificationDesktop {
    [CmdletBinding()]
    param()
    if (-not ('UBVerification.DesktopReader' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot 'WindowsDesktop.cs') | Out-Null
    }
    [UBVerification.DesktopReader]::ReadCurrent($null, 'host-runner')
}

function Get-VerificationForeground {
    [CmdletBinding()]
    param()
    if (-not ('UBVerification.DesktopReader' -as [type])) { [void](Get-VerificationDesktop) }
    [UBVerification.DesktopReader]::ReadForeground()
}

function Start-VerificationBackgroundProcess {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][string]$FilePath,
        [Parameter(Mandatory=$true)][string]$WorkingDirectory,
        [Parameter(Mandatory=$true)][string]$DesktopName, [string[]]$ArgumentList=@())
    if (-not ('UBVerification.DesktopReader' -as [type])) { [void](Get-VerificationDesktop) }
    [UBVerification.DesktopReader]::StartBackground([IO.Path]::GetFullPath($FilePath),
        [IO.Path]::GetFullPath($WorkingDirectory), $ArgumentList, $DesktopName)
}
