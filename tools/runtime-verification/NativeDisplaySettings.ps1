# Unity stores Windows display preferences separately from the game's XML.
. (Join-Path $PSScriptRoot 'WindowsDesktop.ps1')
$script:NativeDisplayRegistryPath = 'Software\Tactical Adventures\Solasta'

function Test-NativeDisplayValueName([string] $Name) {
    return $Name -cmatch '^(Screenmanager [^\\/]+|UnitySelectMonitor_[^\\/]+)$'
}

function Assert-NativeDisplaySettingsSnapshot([object] $Snapshot) {
    if (($Snapshot.schemaVersion -isnot [int] -and $Snapshot.schemaVersion -isnot [long]) -or
        $Snapshot.schemaVersion -ne 1 -or $Snapshot.keyPath -cne $script:NativeDisplayRegistryPath -or
        $Snapshot.keyExisted -isnot [bool] -or
        $Snapshot.readSucceeded -isnot [bool] -or -not $Snapshot.readSucceeded) {
        throw 'Invalid display-preferences snapshot.'
    }
    $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($Snapshot.values)) {
        if (-not (Test-NativeDisplayValueName $entry.name) -or -not $names.Add($entry.name) -or
            $entry.kind -cne 'DWord' -or ($entry.value -isnot [int] -and $entry.value -isnot [long]) -or
            [decimal]$entry.value -lt [int]::MinValue -or [decimal]$entry.value -gt [int]::MaxValue -or
            [decimal]$entry.value -ne [decimal][int]$entry.value) {
            throw 'The display snapshot contains an unsupported name, kind or value.'
        }
    }
}

function Get-NativeDisplaySettingsSnapshot {
    $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:NativeDisplayRegistryPath,$false)
    $values=@()
    try {
        if ($null -ne $key) {
            foreach ($name in @($key.GetValueNames() | Where-Object { Test-NativeDisplayValueName $_ } | Sort-Object)) {
                $kind=$key.GetValueKind($name)
                if ($kind -ne [Microsoft.Win32.RegistryValueKind]::DWord) {
                    throw "Unsupported display-preference kind: $name ($kind)."
                }
                $values += [ordered]@{name=$name;kind='DWord';value=[int]$key.GetValue($name)}
            }
        }
        return [ordered]@{schemaVersion=1;keyPath=$script:NativeDisplayRegistryPath;readSucceeded=$true;keyExisted=($null -ne $key);values=@($values)}
    } finally { if ($null -ne $key) { $key.Dispose() } }
}

function Assert-NativeDisplaySettingsRestored([object] $Snapshot) {
    Assert-NativeDisplaySettingsSnapshot $Snapshot
    $actual=Get-NativeDisplaySettingsSnapshot
    if ((@($actual.values) | ConvertTo-Json -Depth 4 -Compress) -cne
        (@($Snapshot.values) | ConvertTo-Json -Depth 4 -Compress)) {
        throw 'Unity display preferences differ from the saved values.'
    }
}

function Restore-NativeDisplaySettings([object] $Snapshot) {
    Assert-NativeDisplaySettingsSnapshot $Snapshot
    Assert-VerificationGameStopped
    # Validate both sides before the first write. Do not touch other PlayerPrefs.
    $current=Get-NativeDisplaySettingsSnapshot
    $wanted=@($Snapshot.values | ForEach-Object { $_.name })
    $changes=@($current.values | Where-Object { $_.name -notin $wanted }).Count
    foreach ($entry in @($Snapshot.values)) {
        $existing=@($current.values | Where-Object name -eq $entry.name)
        if ($existing.Count -ne 1 -or [int]$existing[0].value -ne [int]$entry.value) { $changes++ }
    }
    if ($changes -gt 0) {
        $key=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($script:NativeDisplayRegistryPath)
        try {
            foreach ($entry in @($current.values)) {
                if ($entry.name -notin $wanted) { $key.DeleteValue($entry.name,$true) }
            }
            foreach ($entry in @($Snapshot.values)) {
                $key.SetValue($entry.name,[int]$entry.value,[Microsoft.Win32.RegistryValueKind]::DWord)
            }
            $key.Flush()
        } finally { $key.Dispose() }
    }
    Assert-NativeDisplaySettingsRestored $Snapshot
    return [ordered]@{succeeded=$true;changedValues=$changes;scope='Unity display preferences';values=@($Snapshot.values).Count}
}
