#requires -Version 5.1
<#
  09-Pause-Updates.ps1  --  pause Windows Update, so cleaning the update caches is not
  undone within minutes.

  WHY THIS EXISTS (measured, see README section 2.3)

  The single most self-defeating thing this toolset can do is clean the update caches
  while Windows is downloading updates. Measured on the reference machine, one minute
  apart: SoftwareDistribution\Download went 0 -> 803.9 MB, the Delivery Optimization
  cache 0 -> 4,958.9 MB, and C: USED space went UP by 480 MB while the tool reported
  "freed". 02-Clean-System.ps1 can only warn about this; pausing is how you actually
  stop it.

  WHAT IT CHANGES

  The same registry values the Settings app writes when you press "Pause updates"
  (HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings). Nothing else is touched: no
  service is stopped, no policy key is created, no task is disabled.

  THE ONE THING THAT MAKES THIS SAFE TO RUN

  The previous state is snapshotted to reports\updates-pause-state.json BEFORE anything
  is written, and -Resume restores it EXACTLY - including deleting values that this
  script created and that were not there before. A pause an administrator had already
  set is therefore put back as it was, not replaced by ours.

  DRY RUN BY DEFAULT. Without -Execute nothing is written; the plan prints the values
  and the exact expiry it would set.

  Usage
    .\09-Pause-Updates.ps1                      # dry run: what pausing would change
    .\09-Pause-Updates.ps1 -Execute             # pause for 7 days
    .\09-Pause-Updates.ps1 -Execute -Days 14    # pause for 14 days (1..35)
    .\09-Pause-Updates.ps1 -Status              # read-only: current pause state
    .\09-Pause-Updates.ps1 -Resume              # dry run: what restoring would change
    .\09-Pause-Updates.ps1 -Resume -Execute     # restore the snapshotted state
#>
[CmdletBinding()]
param(
    [switch]$Execute,
    [switch]$Status,
    [switch]$Resume,
    [ValidateRange(1, 35)][int]$Days = 7
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_Common.ps1')

$KeyPath  = 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings'
$ValueNames = @(
    'PauseUpdatesStartTime', 'PauseUpdatesExpiryTime',
    'PauseFeatureUpdatesStartTime', 'PauseFeatureUpdatesEndTime',
    'PauseQualityUpdatesStartTime', 'PauseQualityUpdatesEndTime'
)

function Get-PauseState {
    <#
      Returns an ordered map name -> value for the six values, with $null for "not set".
      Reading is done defensively: on a machine where the UX key does not exist yet (a
      fresh install before Settings was ever opened) every value is simply absent, which
      is a valid state and not an error.
    #>
    $state = [ordered]@{}
    foreach ($n in $ValueNames) { $state[$n] = $null }

    if (-not (Test-Path -LiteralPath $KeyPath)) { return $state }
    $props = Get-ItemProperty -LiteralPath $KeyPath -ErrorAction SilentlyContinue
    if (-not $props) { return $state }

    foreach ($n in $ValueNames) {
        $p = $props.PSObject.Properties[$n]
        if ($p -and $null -ne $p.Value) { $state[$n] = [string]$p.Value }
    }
    return $state
}

function Write-Report {
    param(
        [Parameter(Mandatory)][string]$Action,
        [Parameter(Mandatory)]$Before,
        [Parameter(Mandatory)]$After,
        [Parameter(Mandatory)][string]$Verdict,
        [string]$Note = ''
    )
    $rows = New-Object System.Collections.ArrayList
    foreach ($n in $ValueNames) {
        [void]$rows.Add((New-Object psobject -Property ([ordered]@{
            Name   = $n
            Before = [string]$Before[$n]
            After  = [string]$After[$n]
        })))
    }

    # Same file naming rule as the cleanup scripts: a dry run must never overwrite the
    # record of a real change, or a preview destroys the audit trail.
    $fileTag = 'updates-pause'
    if (-not $Execute) { $fileTag = $fileTag + '.dryrun' }

    $rep = Join-Path (New-ReportDir) ("{0}.json" -f $fileTag)
    [ordered]@{
        Title  = ("09-Pause-Updates  ({0})" -f $Action)
        Tag    = 'updates-pause'
        Mode   = $(if ($Execute) { 'execute' } else { 'dry-run' })
        Time   = (Get-Date).ToString('o')
        Verdict = $Verdict
        Action = $Action
        Days   = $Days
        Note   = $Note
        Rows   = @($rows)
    } | ConvertTo-Json -Depth 6 | ForEach-Object { $null = Write-ReportText -Path $rep -Text $_ }
    Write-Host ("   report -> {0}" -f $rep)
    return $rep
}

# ------------------------------------------------------------------ status

Write-Host '=================================================================='
Write-Host ' 09-Pause-Updates.ps1'
Write-Host '=================================================================='

$current = Get-PauseState

if ($Status) {
    Write-Host ' Current Windows Update pause state (read-only):'
    Write-Host ''
    $any = $false
    foreach ($n in $ValueNames) {
        if ($null -ne $current[$n]) { $any = $true; Write-Host ("   {0,-32} {1}" -f $n, $current[$n]) }
    }
    if (-not $any) { Write-Host '   (no pause values set - Windows Update is not paused)' }

    $exp = $current['PauseUpdatesExpiryTime']
    if ($exp) {
        try {
            $left = ([datetime]::Parse($exp).ToUniversalTime() - (Get-Date).ToUniversalTime())
            if ($left.TotalSeconds -gt 0) {
                Write-Host ''
                Write-Host ("   paused for another {0:N1} day(s)" -f $left.TotalDays)
            } else {
                Write-Host ''
                Write-Warn2 'The recorded pause has already expired.'
            }
        } catch { Write-Warn2 'The expiry value could not be parsed.' }
    }

    Write-Host ('   registry: {0}' -f $KeyPath)
    Write-Host '=================================================================='
    return
}

$stateFile = Join-Path (New-ReportDir) 'updates-pause-state.json'

# ------------------------------------------------------------------ resume

if ($Resume) {
    Write-Step 'RESTORE THE SNAPSHOTTED PAUSE STATE'

    if (-not (Test-Path -LiteralPath $stateFile)) {
        Write-Err2 ("no snapshot to restore: {0}" -f $stateFile)
        Write-Warn2 'Pause something first (09-Pause-Updates.ps1 -Execute), or clear the pause in Settings.'
        Write-Host '=================================================================='
        exit 2
    }

    $snap = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
    $target = [ordered]@{}
    foreach ($n in $ValueNames) {
        $v = $snap.Previous.PSObject.Properties[$n]
        $target[$n] = if ($v -and $null -ne $v.Value) { [string]$v.Value } else { $null }
    }

    Write-Host (' snapshot taken : {0}' -f $snap.Time)
    foreach ($n in $ValueNames) {
        $was = if ($null -eq $target[$n]) { '(not set)' } else { $target[$n] }
        $now = if ($null -eq $current[$n]) { '(not set)' } else { $current[$n] }
        if ($was -ne $now) { Write-Host ("   {0,-32} {1}  ->  {2}" -f $n, $now, $was) }
    }

    if (-not $Execute) {
        Write-Warn2 'DRY RUN - the registry was not touched. Add -Execute to restore.'
        [void](Write-Report -Action 'resume' -Before $current -After $target -Verdict 'dry-run' `
            -Note 'restores the values snapshotted before the pause; values this script created are deleted')
        Write-Host '=================================================================='
        return
    }

    if (-not (Test-IsAdmin)) { Write-Err2 'Administrator rights are required (HKLM).'; exit 3 }
    if (-not (Test-Path -LiteralPath $KeyPath)) { New-Item -Path $KeyPath -Force | Out-Null }

    foreach ($n in $ValueNames) {
        if ($null -eq $target[$n]) {
            # Deleting matters as much as setting: if we created the value and it was not
            # there before, leaving it behind would silently keep updates paused.
            Remove-ItemProperty -LiteralPath $KeyPath -Name $n -ErrorAction SilentlyContinue
        } else {
            New-ItemProperty -LiteralPath $KeyPath -Name $n -Value $target[$n] -PropertyType String -Force | Out-Null
        }
    }

    $after = Get-PauseState
    $ok = $true
    foreach ($n in $ValueNames) {
        $want = [string]$target[$n]; $got = [string]$after[$n]
        if ($want -ne $got) { $ok = $false; Write-Err2 ("   {0} is '{1}' but should be '{2}'" -f $n, $got, $want) }
    }

    if ($ok) { Write-Ok 'restored to the snapshotted state (verified by re-reading the registry)' }
    else     { Write-Err2 'RESTORE DID NOT MATCH - check the values above' }

    [void](Write-Report -Action 'resume' -Before $current -After $after `
        -Verdict $(if ($ok) { 'RESTORED' } else { 'MISMATCH' }) `
        -Note 'restores the values snapshotted before the pause; values this script created are deleted')
    Write-Host '=================================================================='
    return
}

# ------------------------------------------------------------------ pause

Write-Step ("PAUSE WINDOWS UPDATE FOR {0} DAY(S)" -f $Days)

$startIso = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$endIso   = (Get-Date).ToUniversalTime().AddDays($Days).ToString('yyyy-MM-ddTHH:mm:ssZ')

$target = [ordered]@{}
foreach ($n in $ValueNames) { $target[$n] = $null }
$target['PauseUpdatesStartTime']        = $startIso
$target['PauseUpdatesExpiryTime']       = $endIso
$target['PauseFeatureUpdatesStartTime'] = $startIso
$target['PauseFeatureUpdatesEndTime']   = $startIso   # feature pause: start-only is enough
$target['PauseQualityUpdatesStartTime'] = $startIso
$target['PauseQualityUpdatesEndTime']   = $startIso

Write-Host (" start : {0}" -f $startIso)
Write-Host (" until : {0}   ({1} day(s))" -f $endIso, $Days)
Write-Host ''
Write-Host ' changes:'
foreach ($n in $ValueNames) {
    $was = if ($null -eq $current[$n]) { '(not set)' } else { $current[$n] }
    Write-Host ("   {0,-32} {1}  ->  {2}" -f $n, $was, $target[$n])
}

if (-not $Execute) {
    Write-Host ''
    Write-Warn2 'DRY RUN - the registry was not touched. Add -Execute to pause.'
    Write-Host ' The previous values would be snapshotted first, and -Resume puts them back'
    Write-Host ' exactly (including deleting values that were not set before).'
    [void](Write-Report -Action 'pause' -Before $current -After $target -Verdict 'dry-run' `
        -Note 'snapshot is written to updates-pause-state.json before anything is applied')
    Write-Host '=================================================================='
    return
}

if (-not (Test-IsAdmin)) { Write-Err2 'Administrator rights are required (HKLM).'; exit 3 }

# Snapshot BEFORE writing anything: this file is the only reason -Resume can put back a
# pause that was already in place.
[ordered]@{
    Time     = (Get-Date).ToString('o')
    Key      = $KeyPath
    Days     = $Days
    Previous = $current
} | ConvertTo-Json -Depth 6 | ForEach-Object { $null = Write-ReportText -Path $stateFile -Text $_ }
Write-Host ''
Write-Host (" snapshot -> {0}" -f $stateFile)

if (-not (Test-Path -LiteralPath $KeyPath)) { New-Item -Path $KeyPath -Force | Out-Null }
foreach ($n in $ValueNames) {
    New-ItemProperty -LiteralPath $KeyPath -Name $n -Value $target[$n] -PropertyType String -Force | Out-Null
}

$after = Get-PauseState
$ok = $true
foreach ($n in $ValueNames) {
    if ([string]$target[$n] -ne [string]$after[$n]) {
        $ok = $false
        Write-Err2 ("   {0} is '{1}' but should be '{2}'" -f $n, $after[$n], $target[$n])
    }
}

if ($ok) { Write-Ok ("Windows Update is paused until {0} (verified by re-reading the registry)" -f $endIso) }
else     { Write-Err2 'PAUSE DID NOT APPLY - check the values above' }

Write-Host ''
Write-Warn2 'Remember to run:  09-Pause-Updates.ps1 -Resume -Execute   when you are done cleaning.'
Write-Host ' (Windows also resumes by itself when the pause expires.)'

[void](Write-Report -Action 'pause' -Before $current -After $after `
    -Verdict $(if ($ok) { 'PAUSED' } else { 'MISMATCH' }) `
    -Note 'snapshot is written to updates-pause-state.json before anything is applied')
Write-Host '=================================================================='
