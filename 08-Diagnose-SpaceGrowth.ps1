#requires -Version 5.1
<#
  08-Diagnose-SpaceGrowth.ps1  --  READ-ONLY root-cause diagnosis.

  Answers one question: "why does C: free space go DOWN (used space go UP)
  after a cleanup run, instead of up?"

  Deletes NOTHING. Safe to run any time.

  It probes the five mechanisms that make a cleanup net-negative or net-neutral:

    1. VOLUME SHADOW COPY (System Restore) copy-on-write
       Freed clusters stay referenced by a shadow copy. The moment anything
       reuses them, VSS copies the ORIGINAL blocks into the diff area, so the
       gain is eaten again. Big deletions with VSS on can end up net-zero.

    2. HARD LINKS  (Windows Update payloads)
       Files under SoftwareDistribution\Download are hard-linked into WinSxS.
       Deleting one link frees ZERO bytes while every "MB freed" sum that adds
       up FileInfo.Length reports the full size. This is the classic fake win.

    3. REGENERATION
       Prefetch, thumbcache, INetCache, DeliveryOptimization and the whole
       Windows Update download cache are rebuilt by the services that own them,
       usually within minutes to hours.

    4. WINDOWS-MANAGED SPACE that only shrinks through dedicated switches
       pagefile.sys / hiberfil.sys / swapfile.sys / Reserved Storage.

    5. LOGICAL SIZE vs ALLOCATED SIZE
       NTFS compression and sparse files make FileInfo.Length overstate the
       real on-disk cost, so byte-sum "freed" numbers are optimistic.

  Ground truth for any cleanup is the VOLUME FREE-SPACE delta, measured before
  and after - never a sum of file lengths. This script measures both so the
  difference is visible.

  Usage:
    .\08-Diagnose-SpaceGrowth.ps1                 # fast probes
    .\08-Diagnose-SpaceGrowth.ps1 -Deep           # + WinSxS / component store
    .\08-Diagnose-SpaceGrowth.ps1 -Tag d2        # custom report tag
#>
[CmdletBinding()]
param(
    [string]$Tag = 'spacegrowth',
    [switch]$Deep,
    [int]   $HardlinkSample = 20
)

$ErrorActionPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot '_Common.ps1')

$LA = $env:LOCALAPPDATA
$PD = $env:ProgramData
$U  = $env:USERPROFILE

function Write-Head {
    param([string]$Text)
    Write-Host ''
    Write-Host '=================================================================='
    Write-Host (" {0}" -f $Text)
    Write-Host '=================================================================='
}

function Get-FreeBytes {
    param([string]$Drive = 'C:')
    $d = Get-CimInstance Win32_LogicalDisk -Filter ("DeviceID='{0}'" -f $Drive) -ErrorAction SilentlyContinue
    if ($d) { return [int64]$d.FreeSpace }
    return [int64]0
}

$findings = New-Object System.Collections.ArrayList
function Add-Finding {
    param([string]$Id, [string]$Severity, [string]$Text)
    [void]$findings.Add((New-Object psobject -Property ([ordered]@{
        Id = $Id; Severity = $Severity; Text = $Text
    })))
}

Write-Host '=================================================================='
Write-Host ' 08-Diagnose-SpaceGrowth.ps1     READ-ONLY (deletes nothing)'
Write-Host (" Host={0}  Time={1}" -f $env:COMPUTERNAME, (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Write-Host '=================================================================='

$freeStart = Get-FreeBytes 'C:'
$disk      = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='C:'" -ErrorAction SilentlyContinue

# ------------------------------------------------------------- 1. volume
Write-Head '1. VOLUME STATE (ground truth)'
Write-Host ("   C: total {0:N2} GB   free {1:N2} GB   used {2:N2} GB" -f `
    ($disk.Size / 1GB), ($freeStart / 1GB), (($disk.Size - $freeStart) / 1GB))

# ------------------------------------------------------------- 2. VSS
Write-Head '2. SHADOW COPIES / SYSTEM RESTORE  (copy-on-write can eat freed space)'
$shadow = @()
try {
    # A bare "vssadmin" resolves only when System32 happens to be on PATH; under a
    # restricted PATH (service session, sandbox) the probe silently fails. Always
    # resolve the in-box tool first (Sysnative-aware for 32-bit PowerShell).
    $vss = Get-SystemToolPath 'vssadmin.exe'
    if ($vss -and (Test-IsAdmin)) {
        $raw = & $vss list shadowstorage 2>&1 | Out-String
        Write-Host $raw.Trim()
    } elseif (-not $vss) {
        Write-Warn2 'vssadmin.exe not found - falling back to CIM'
    } else {
        Write-Warn2 'not elevated - vssadmin is limited, falling back to CIM'
    }
} catch { Write-Warn2 ("vssadmin failed: {0}" -f $_.Exception.Message) }

try {
    $cim = @(Get-CimInstance Win32_ShadowStorage -ErrorAction SilentlyContinue)
    foreach ($s in $cim) {
        $maxB  = [int64]$s.MaxSpace
        $usedB = [int64]$s.UsedSpace
        $pct   = 0
        if ($maxB -gt 0) { $pct = [math]::Round($usedB / $maxB * 100, 1) }
        $shadow += (New-Object psobject -Property ([ordered]@{
            UsedGB = [math]::Round($usedB / 1GB, 2)
            MaxGB  = [math]::Round($maxB / 1GB, 2)
            UsedPct = $pct
        }))
    }
    $shadows = @(Get-CimInstance Win32_ShadowCopy -ErrorAction SilentlyContinue)
    Write-Host ("   shadow copies present : {0}" -f $shadows.Count)
    foreach ($s in $shadows) {
        Write-Host ("      {0}   {1}" -f $s.InstallDate, $s.VolumeName)
    }
    foreach ($s in $shadow) {
        Write-Host ("   diff area used {0} GB of {1} GB  ({2}%)" -f $s.UsedGB, $s.MaxGB, $s.UsedPct)
        if ($s.UsedPct -ge 60) {
            Add-Finding 'vss-cow' 'HIGH' ("shadow storage is {0}% full ({1} GB of {2} GB): freed clusters stay referenced by a shadow copy, so deleting files can free LESS than it deletes" -f $s.UsedPct, $s.UsedGB, $s.MaxGB)
        }
    }
    if ($shadows.Count -gt 0 -and $shadow.Count -eq 0) {
        Add-Finding 'vss-cow' 'HIGH' ("{0} shadow copy(ies) exist: deletions may be absorbed by copy-on-write" -f $shadows.Count)
    }
} catch { Write-Warn2 ("shadow probe failed: {0}" -f $_.Exception.Message) }

# ------------------------------------------------- 3. reserved storage
Write-Head '3. WINDOWS RESERVED STORAGE / COMPONENT STORE'
$reserved = $null
try {
    $dism = Get-SystemToolPath 'dism.exe'
    if ($dism -and (Test-IsAdmin)) {
        $rs = (& $dism /Online /Get-ReservedStorageState 2>&1 | Out-String)
        Write-Host $rs.Trim()
        if ($rs -match '(?i)enabled') { $reserved = 'Enabled' }
        elseif ($rs -match '(?i)disabled') { $reserved = 'Disabled' }
    } elseif (-not $dism) {
        Write-Warn2 'dism.exe not found - reserved-storage state unknown'
    } else {
        Write-Warn2 'not elevated - reserved-storage state unknown'
    }
} catch { Write-Warn2 'dism reserved-storage probe failed' }

if ($Deep) {
    Write-Host '   measuring C:\Windows\WinSxS (may take a minute)...'
    $winsxs = Get-PathBytes -Path 'C:\Windows\WinSxS'
    Write-Host ("   WinSxS logical size : {0:N1} GB  (use 'dism /Online /Cleanup-Image /AnalyzeComponentStore' for the reclaimable part)" -f ($winsxs / 1GB))
}

# ------------------------------------------------- 4. system-managed space
Write-Head '4. WINDOWS-MANAGED FILES AT C:\ ROOT'
$sysFiles = @()
foreach ($f in @('C:\hiberfil.sys', 'C:\pagefile.sys', 'C:\swapfile.sys')) {
    $i = Get-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
    if ($i) {
        $mb = [math]::Round($i.Length / 1MB, 1)
        $sysFiles += (New-Object psobject -Property ([ordered]@{ Path = $f; MB = $mb }))
        Write-Host ("   {0,10:N1} MB  {1}   (last write {2})" -f $mb, $f, $i.LastWriteTime)
    } else {
        Write-Host ("   {0,10}       {1}" -f '(absent)', $f)
    }
}
if ((Test-Path 'C:\hiberfil.sys')) {
    Add-Finding 'hiberfil' 'INFO' 'hiberfil.sys exists (fast startup / hibernate). It is typically 40% of RAM and only removable with: powercfg /h off'
}
$pf = Get-CimInstance Win32_PageFileUsage -ErrorAction SilentlyContinue
if ($pf) { Write-Host ("   pagefile configured: {0} MB (peak {1} MB)" -f $pf.AllocatedBaseSize, $pf.PeakUsage) }

# ------------------------------------------------- 5. regeneration probe
Write-Head '5. REGENERATION PROBE (caches the cleaner already deleted, measured now)'
$regrow = [ordered]@{
    'WU download cache'      = 'C:\Windows\SoftwareDistribution\Download'
    'WU datastore'           = 'C:\Windows\SoftwareDistribution\DataStore'
    'DeliveryOptimization'   = 'C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache'
    'DO logs'                = 'C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Logs'
    'Prefetch'               = 'C:\Windows\Prefetch'
    'LiveKernelReports'      = 'C:\Windows\LiveKernelReports'
    'Minidump'               = 'C:\Windows\Minidump'
    'CBS logs'               = 'C:\Windows\Logs\CBS'
    'WindowsUpdate logs'     = 'C:\Windows\Logs\WindowsUpdate'
    'WER (ProgramData)'      = (Join-Path $PD 'Microsoft\Windows\WER')
    'Downloader (PD)'        = (Join-Path $PD 'Microsoft\Network\Downloader')
    'user TEMP'              = $env:TEMP
    'Windows Temp'           = 'C:\Windows\Temp'
    'thumbcache/iconcache'   = (Join-Path $LA 'Microsoft\Windows\Explorer')
    'INetCache'              = (Join-Path $LA 'Microsoft\Windows\INetCache')
    'WebCache'               = (Join-Path $LA 'Microsoft\Windows\WebCache')
    'CrashDumps'             = (Join-Path $LA 'CrashDumps')
    'NVIDIA DXCache'         = (Join-Path $LA 'NVIDIA\DXCache')
    'NVIDIA GLCache'         = (Join-Path $LA 'NVIDIA\GLCache')
    'D3DSCache'              = (Join-Path $LA 'D3DSCache')
    'DirectX Shader Cache'   = (Join-Path $LA 'Microsoft\DirectX Shader Cache')
    'Recycle Bin'            = 'C:\$Recycle.Bin'
    'Edge Cache'             = (Join-Path $LA 'Microsoft\Edge\User Data\Default\Cache')
    'Edge Code Cache'        = (Join-Path $LA 'Microsoft\Edge\User Data\Default\Code Cache')
    'Chrome Cache'           = (Join-Path $LA 'Google\Chrome\User Data\Default\Cache')
    'Windows.old'            = 'C:\Windows.old'
    'WinREAgent'             = 'C:\$WinREAgent'
}
$regrowRows = New-Object System.Collections.ArrayList
foreach ($k in $regrow.Keys) {
    $p = $regrow[$k]
    if ([string]::IsNullOrWhiteSpace($p) -or -not (Test-Path -LiteralPath $p)) {
        Write-Host ("   {0,10}       (absent)  {1}" -f '-', $k)
        continue
    }
    $b = Get-PathBytes -Path $p
    $mb = [math]::Round($b / 1MB, 1)
    [void]$regrowRows.Add((New-Object psobject -Property ([ordered]@{ Name = $k; Path = $p; MB = $mb })))
    $mark = ''
    if ($mb -ge 500) { $mark = '  <== LARGE' }
    Write-Host ("   {0,10:N1} MB  {1,-22} {2}{3}" -f $mb, $k, $p, $mark)
}
$regrowTotal = ($regrowRows | Measure-Object MB -Sum).Sum
Write-Host ("   ---- regrowth-prone total: {0:N1} MB ({1:N2} GB)" -f $regrowTotal, ($regrowTotal / 1024))
if ($regrowTotal -ge 2000) {
    Add-Finding 'regrowth' 'HIGH' ("{0:N1} MB of regeneration-prone cache is present again: these directories are re-created by their owning service, so they cannot be 'cleaned once'" -f $regrowTotal)
}

# ------------------------------------------------- 6. hardlink probe
Write-Head '6. HARD-LINK PROBE (Windows Update payloads are linked into WinSxS)'
$wlTargets = @(
    'C:\Windows\SoftwareDistribution\Download',
    (Join-Path $PD 'Microsoft\Network\Downloader')
)
$hardlinkRows = New-Object System.Collections.ArrayList
foreach ($t in $wlTargets) {
    if (-not (Test-Path -LiteralPath $t)) { continue }
    $files = @(Get-ChildItem -LiteralPath $t -Recurse -Force -File -ErrorAction SilentlyContinue |
               Sort-Object Length -Descending | Select-Object -First $HardlinkSample)
    $linked = 0; $logicalBytes = [int64]0; $linkedBytes = [int64]0
    foreach ($f in $files) {
        $logicalBytes += $f.Length
        $n = 0
        try {
            $out = (& fsutil hardlink list $f.FullName 2>&1 | Out-String)
            $n = @($out -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 }).Count
        } catch {}
        if ($n -gt 1) { $linked++; $linkedBytes += $f.Length }
    }
    if ($files.Count -gt 0) {
        Write-Host ("   {0}" -f $t)
        Write-Host ("      sampled {0} largest file(s): {1} have MORE THAN ONE hard link" -f $files.Count, $linked)
        Write-Host ("      sampled logical size {0:N1} MB, of which {1:N1} MB is hard-linked (deleting that link frees ~0)" -f ($logicalBytes / 1MB), ($linkedBytes / 1MB))
        [void]$hardlinkRows.Add((New-Object psobject -Property ([ordered]@{
            Path = $t; Sampled = $files.Count; Linked = $linked
            SampledMB = [math]::Round($logicalBytes / 1MB, 1)
            LinkedMB  = [math]::Round($linkedBytes / 1MB, 1)
        })))
        if ($linked -gt 0) {
            Add-Finding 'hardlink' 'HIGH' ("{0} of {1} sampled file(s) under {2} are hard-linked: any 'MB freed' figure built by summing file lengths is inflated over those files" -f $linked, $files.Count, $t)
        }
    }
}

# ------------------------------------------------- 7. update services
Write-Head '7. UPDATE / SERVICE STATE (who keeps writing during a cleanup)'
foreach ($svc in @('wuauserv', 'bits', 'DoSvc', 'TrustedInstaller', 'WinDefend', 'WSearch', 'SysMain')) {
    $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
    if ($s) { Write-Host ("   {0,-16} {1}" -f $s.Name, $s.Status) }
}
$pending = @(Get-CimInstance Win32_QuickFixEngineering -ErrorAction SilentlyContinue).Count
Write-Host ("   installed hotfixes reported : {0}" -f $pending)

# ------------------------------------------------- 8. verdict
Write-Head '8. VERDICT'
if ($findings.Count -eq 0) {
    Write-Ok 'no space-regrowth mechanism detected by these probes.'
} else {
    foreach ($f in $findings) {
        $color = 'Yellow'
        if ($f.Severity -eq 'INFO') { $color = 'Gray' }
        Write-Host ("   [{0,-4}] {1}" -f $f.Severity, $f.Text) -ForegroundColor $color
    }
}

$freeEnd = Get-FreeBytes 'C:'
Write-Host ''
Write-Host ("   C: free during this read-only run: {0:N2} GB -> {1:N2} GB (drift {2:N1} MB)" -f `
    ($freeStart / 1GB), ($freeEnd / 1GB), (($freeEnd - $freeStart) / 1MB))

# ------------------------------------------------- 9. report
$report = [ordered]@{
    Tag = $Tag; Time = (Get-Date).ToString('o'); Computer = $env:COMPUTERNAME
    FreeStartGB = [math]::Round($freeStart / 1GB, 2)
    FreeEndGB   = [math]::Round($freeEnd / 1GB, 2)
    DriftMB     = [math]::Round(($freeEnd - $freeStart) / 1MB, 1)
    Shadow      = @($shadow)
    SystemFiles = @($sysFiles)
    Regrow      = @($regrowRows)
    RegrowMB    = [math]::Round($regrowTotal, 1)
    Hardlinks   = @($hardlinkRows)
    Findings    = @($findings)
}
$rep = Join-Path (New-ReportDir) ("{0}.json" -f $Tag)
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $rep -Encoding UTF8
Write-Host ("   report -> {0}" -f $rep)
Write-Host '=================================================================='
