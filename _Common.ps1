#requires -Version 5.1
<#
  _Common.ps1  --  shared safety helpers for the C: cleanup scripts.

  Design rules enforced here:
    * Every deletion target must pass Test-SafeTarget (root allowlist + hard denylist).
    * Actual freed space is measured as (bytes before) - (bytes after), never assumed.
    * A locked / in-use item is recorded and SKIPPED; it never aborts the run.
    * Reparse points (junctions / symlinks) are never recursed into or deleted.
  Scripts are ASCII-only on purpose: Windows PowerShell 5.1 reads .ps1 as ANSI
  unless a BOM is present, so non-ASCII in a script is a mojibake hazard.
#>

$ErrorActionPreference = 'Continue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

# --------------------------------------------------------------- state
$script:AllowedRoots = @()
$script:AllowedFiles = @()
$script:DeniedRoots  = @(
    'C:\Windows\System32'
    'C:\Windows\SysWOW64'
    'C:\Windows\WinSxS'
    'C:\Windows\Installer'
    'C:\Windows\Fonts'
    'C:\Windows\assembly'
    'C:\Program Files'
    'C:\Program Files (x86)'
    'C:\ProgramData\Microsoft\Windows\Start Menu'
    'C:\Users\Default'
    'C:\Users\Public'
    'C:\Recovery'
    'C:\PerfLogs'
)
$script:Failures = New-Object System.Collections.ArrayList

function Add-AllowedRoot { param([string[]]$Roots)
    foreach ($r in $Roots) { if (-not [string]::IsNullOrWhiteSpace($r)) { $script:AllowedRoots += $r } }
}

<#
  Exact single-file targets (e.g. C:\Windows\MEMORY.DMP).

  Needed because the alternative - allowlisting C:\Windows so that one file
  inside it can be deleted - would make the whole Windows directory deletable.
  An allowlisted file is permitted on its own; its parent directory is not.
#>
function Add-AllowedFile { param([string[]]$Files)
    foreach ($f in $Files) {
        if ([string]::IsNullOrWhiteSpace($f)) { continue }
        $script:AllowedFiles += [System.IO.Path]::GetFullPath($f)
    }
}

function Get-AllowedRoots { return @($script:AllowedRoots) }
function Get-AllowedFiles { return @($script:AllowedFiles) }

function Get-Failures { return $script:Failures }

function Write-Step  { param([string]$m) Write-Host ("[*] {0}" -f $m) }
function Write-Ok    { param([string]$m) Write-Host ("[+] {0}" -f $m) -ForegroundColor Green }
function Write-Warn2 { param([string]$m) Write-Host ("[!] {0}" -f $m) -ForegroundColor Yellow }
function Write-Err2  { param([string]$m) Write-Host ("[x] {0}" -f $m) -ForegroundColor Red }

# --------------------------------------------------------------- guards
function Test-IsAdmin {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}

function Test-SafeTarget {
    param([Parameter(Mandatory)][string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    $full = $null
    try { $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\') } catch { return $false }
    if ($full -match '^[A-Za-z]:$') { return $false }                       # bare drive root
    if ($full.Length -lt 12) { return $false }                              # C:\Windows\T is far shorter than any real target
    foreach ($d in $script:DeniedRoots) {
        $df = $d.TrimEnd('\')
        if ($full -eq $df -or $full.StartsWith($df + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $up = $env:USERPROFILE.TrimEnd('\')
        if ($full -eq $up) { return $false }                                # never the profile root itself
        foreach ($keep in @('Desktop', 'Documents', 'Pictures', 'Videos', 'Music', 'Downloads', 'Favorites', 'OneDrive')) {
            if ($full -eq ($up + '\' + $keep)) { return $false }
        }
    }
    foreach ($r in $script:AllowedRoots) {
        $rf = [System.IO.Path]::GetFullPath($r).TrimEnd('\')
        if ($full -eq $rf) { return $true }
        if ($full.StartsWith($rf + '\', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    foreach ($f in $script:AllowedFiles) {
        if ($full -eq $f) { return $true }
    }
    return $false
}

function Assert-SafeTarget {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-SafeTarget -Path $Path)) {
        throw ("REFUSED: target is not on the cleanup allowlist -> {0}" -f $Path)
    }
    return $true
}

# --------------------------------------------------------------- measure
function Get-FreeGB {
    param([string]$Drive = 'C:')
    $d = Get-CimInstance Win32_LogicalDisk -Filter ("DeviceID='{0}'" -f $Drive) -ErrorAction SilentlyContinue
    if ($d) { return [math]::Round($d.FreeSpace / 1GB, 2) }
    return $null
}

function Get-PathBytes {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return [int64]0 }
    $sum = [int64]0
    foreach ($f in (Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue)) {
        $sum += $f.Length
    }
    return $sum
}

function Get-DirSizeMB { param([string]$Path) return [math]::Round((Get-PathBytes -Path $Path) / 1MB, 1) }

# --------------------------------------------------------------- system tools
<#
  Resolve an in-box system tool by name.

  Two traps this avoids:
    * Under a restricted PATH (sandboxed / service session) a bare "vssadmin"
      is not found at all -- every probe silently fails. Always use the full
      path below System32.
    * 32-bit PowerShell on 64-bit Windows gets System32 redirected to SysWOW64,
      which does NOT contain vssadmin.exe or dism.exe. Sysnative wins there.
#>
function Get-SystemToolPath {
    param([Parameter(Mandatory)][string]$Name)
    $candidates = New-Object System.Collections.ArrayList
    if ([IntPtr]::Size -eq 4) {
        [void]$candidates.Add((Join-Path $env:SystemRoot ("Sysnative\{0}" -f $Name)))
    }
    [void]$candidates.Add((Join-Path $env:SystemRoot ("System32\{0}" -f $Name)))
    foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { return $c } }
    return $null
}

# --------------------------------------------------------------- ground truth
<#
  FREE SPACE IS THE ONLY HONEST MEASURE OF A CLEANUP.

  Every older version of these scripts reported "freed" as the difference of
  summed FileInfo.Length values. That number is fiction:
    * it counts a hard-linked file once per link, though removing one link
      frees nothing;
    * it ignores NTFS compression and sparse files;
    * it ignores Volume Shadow Copy, which keeps the deleted blocks alive;
    * it cannot see a service re-writing the directory while we delete.
  Get-FreeBytes reads the volume's own free-space counter instead.
#>
function Get-FreeBytes {
    param([string]$Drive = 'C:')
    $d = $Drive.TrimEnd(':').ToUpper()
    $disk = Get-CimInstance Win32_LogicalDisk -Filter ("DeviceID='{0}:'" -f $d) -ErrorAction SilentlyContinue
    if ($disk) { return [int64]$disk.FreeSpace }
    try { return [int64](Get-PSDrive -Name $d -ErrorAction Stop).Free } catch { }
    return [int64]0
}

function Get-UsedBytes {
    param([string]$Drive = 'C:')
    $d = $Drive.TrimEnd(':').ToUpper()
    $disk = Get-CimInstance Win32_LogicalDisk -Filter ("DeviceID='{0}:'" -f $d) -ErrorAction SilentlyContinue
    if ($disk) { return [int64]($disk.Size - $disk.FreeSpace) }
    return [int64]0
}

function ConvertTo-ByteSize {
    param([string]$Number, [string]$Unit)
    $n = 0.0
    try { $n = [double]($Number -replace ',', '.') } catch { return [int64]0 }
    switch ($Unit.ToUpper()) {
        'KB' { return [int64]($n * 1KB) }
        'MB' { return [int64]($n * 1MB) }
        'GB' { return [int64]($n * 1GB) }
        'TB' { return [int64]($n * 1TB) }
        default { return [int64]$n }
    }
}

<#
  Volume Shadow Copy state for one drive.

  This is the single most important number a cleaner can read. On a volume with
  System Restore on, deleted blocks stay referenced by the restore points, so:
    * the free-space gain from a deletion can be far smaller than the deletion;
    * as soon as any process reuses those clusters, VSS copies the ORIGINAL
      snapshot content into the diff area, consuming the space again;
    * the diff area grows silently toward its cap, so C: used space can go UP
      while a cleaner reports GBs "freed".
  Measured on the reference machine: an 800 MB write reused freed clusters and
  grew the diff area by 87.3 MB that never came back after the file was deleted.

  Labels in vssadmin output are localized, so the parser keys on the drive
  letter and the unit strings (GB/MB/TB are not translated) instead of on the
  English wording, and falls back to CIM when that fails.
#>
function Get-ShadowStorageInfo {
    param([string]$Drive = 'C:')
    $dl = $Drive.TrimEnd(':').ToUpper()
    $o = New-Object psobject -Property ([ordered]@{
        Ok = $false; Source = 'none'; Drive = $dl
        UsedBytes = [int64]0; AllocatedBytes = [int64]0; MaxBytes = [int64]0
        UsedPct = 0; Copies = 0
    })

    $vss = Get-SystemToolPath 'vssadmin.exe'
    if ($vss) {
        try {
            $txt    = (& $vss list shadowstorage 2>&1 | Out-String)
            $blocks = $txt -split '(?m)^\s*\S.*association'
            if ($blocks.Count -le 1) { $blocks = $txt -split "`r?`n`r?`n" }
            foreach ($b in $blocks) {
                if ($b -notmatch ('\(\s*' + $dl + ':\s*\)')) { continue }
                $nums = [regex]::Matches($b, '(?i)([0-9]+(?:[.,][0-9]+)?)\s*(KB|MB|GB|TB)')
                if ($nums.Count -ge 3) {
                    $o.UsedBytes      = ConvertTo-ByteSize $nums[0].Groups[1].Value $nums[0].Groups[2].Value
                    $o.AllocatedBytes = ConvertTo-ByteSize $nums[1].Groups[1].Value $nums[1].Groups[2].Value
                    $o.MaxBytes       = ConvertTo-ByteSize $nums[2].Groups[1].Value $nums[2].Groups[2].Value
                    $o.Ok = $true; $o.Source = 'vssadmin'
                }
                break
            }
            $stxt = (& $vss list shadows 2>&1 | Out-String)
            $sblocks = $stxt -split '(?m)^\s*\S.*creation time'
            if ($sblocks.Count -le 1) { $sblocks = $stxt -split "`r?`n`r?`n" }
            $n = 0
            foreach ($b in $sblocks) { if ($b -match ('\(\s*' + $dl + ':\s*\)')) { $n++ } }
            if ($n -gt 0) { $o.Copies = $n }
        } catch { }
    }

    if (-not $o.Ok) {
        try {
            $all = @(Get-CimInstance Win32_ShadowStorage -ErrorAction SilentlyContinue)
            $best = $all | Sort-Object UsedSpace -Descending | Select-Object -First 1
            if ($best -and [int64]$best.MaxSpace -gt 0) {
                $o.UsedBytes      = [int64]$best.UsedSpace
                $o.AllocatedBytes = [int64]$best.AllocatedSpace
                $o.MaxBytes       = [int64]$best.MaxSpace
                $o.Ok = $true; $o.Source = 'cim'
            }
            if ($o.Copies -eq 0) { $o.Copies = @(Get-CimInstance Win32_ShadowCopy -ErrorAction SilentlyContinue).Count }
        } catch { }
    }
    if ($o.MaxBytes -gt 0) { $o.UsedPct = [math]::Round($o.UsedBytes / $o.MaxBytes * 100, 1) }
    return $o
}

# --------------------------------------------------------------- net-gain guard
<#
  A cleanup may only ever move free space UP. The guard samples the volume
  before every target and after every target; a single negative step marks the
  run as a regression and (by default) stops all remaining work, so the tool can
  never keep deleting while space goes backwards.
#>
$script:NetGuard = $null

function Start-NetGainGuard {
    param([string]$Drive = 'C:')
    $shadow = Get-ShadowStorageInfo -Drive $Drive
    $script:NetGuard = [ordered]@{
        Drive         = $Drive.TrimEnd(':').ToUpper()
        StartFree     = Get-FreeBytes -Drive $Drive
        CurrentFree   = [int64]0
        StartUsed     = Get-UsedBytes -Drive $Drive
        StartShadow   = [int64]$shadow.UsedBytes
        ShadowAtStart = $shadow
        StartTime     = Get-Date
        Steps         = (New-Object System.Collections.ArrayList)
        Regressions   = (New-Object System.Collections.ArrayList)
        Aborted       = $false
        AbortReason   = ''
        MaxDropMB     = 0
    }
    $script:NetGuard.CurrentFree = $script:NetGuard.StartFree
    return $script:NetGuard
}

function Get-NetGuard { return $script:NetGuard }

<#
  Record one step. Returns $true when free space went DOWN during that step.
  -AbortIfNegative stops the whole run (the default policy of the cleaner).
#>
function Step-NetGainGuard {
    param(
        [string]$Label = '',
        [switch]$AbortIfNegative,
        [double]$ToleranceMB = 0
    )
    if (-not $script:NetGuard) { return $false }
    $free  = Get-FreeBytes -Drive $script:NetGuard.Drive
    $delta = $free - [int64]$script:NetGuard.CurrentFree
    $script:NetGuard.CurrentFree = $free
    [void]$script:NetGuard.Steps.Add((New-Object psobject -Property ([ordered]@{
        Label = $Label; DeltaMB = [math]::Round($delta / 1MB, 1); Time = (Get-Date).ToString('HH:mm:ss')
    })))
    if ($delta -lt 0) {
        $mb = [math]::Round($delta / 1MB, 1)
        if ([math]::Abs($mb) -gt $script:NetGuard.MaxDropMB) { $script:NetGuard.MaxDropMB = [math]::Abs($mb) }
        [void]$script:NetGuard.Regressions.Add(("{0}: {1:N1} MB" -f $Label, $mb))
        if ($AbortIfNegative -and ([math]::Abs($mb) -gt $ToleranceMB)) {
            $script:NetGuard.Aborted = $true
            $script:NetGuard.AbortReason = ("free space fell {0:N1} MB during '{1}' (tolerance {2} MB)" -f $mb, $Label, $ToleranceMB)
        }
        return $true
    }
    return $false
}

function Stop-NetGainGuard {
    $g = $script:NetGuard
    if (-not $g) { return $null }
    $shadow = Get-ShadowStorageInfo -Drive $g.Drive
    $free   = Get-FreeBytes -Drive $g.Drive
    $g.CurrentFree = $free
    $netFree = $free - [int64]$g.StartFree
    $netUsed = (Get-UsedBytes -Drive $g.Drive) - [int64]$g.StartUsed
    $shadowGrowth = [int64]$shadow.UsedBytes - [int64]$g.StartShadow
    $verdict = 'GAIN'
    if ($netFree -lt 0) { $verdict = 'REGRESSION' }
    elseif ($netFree -eq 0) { $verdict = 'NEUTRAL' }
    return New-Object psobject -Property ([ordered]@{
        Drive = $g.Drive
        FreeStartGB = [math]::Round([int64]$g.StartFree / 1GB, 2)
        FreeEndGB   = [math]::Round($free / 1GB, 2)
        NetFreeMB   = [math]::Round($netFree / 1MB, 1)
        NetUsedMB   = [math]::Round($netUsed / 1MB, 1)
        ShadowGrowthMB = [math]::Round($shadowGrowth / 1MB, 1)
        ShadowUsedGB   = [math]::Round([int64]$shadow.UsedBytes / 1GB, 2)
        ShadowMaxGB    = [math]::Round([int64]$shadow.MaxBytes / 1GB, 2)
        ShadowUsedPct  = $shadow.UsedPct
        ShadowCopies   = $shadow.Copies
        Verdict     = $verdict
        Aborted     = $g.Aborted
        AbortReason = $g.AbortReason
        Steps       = @($g.Steps)
        Regressions = @($g.Regressions)
    })
}

# --------------------------------------------------------------- hard links
<#
  Number of hard links pointing at a file, or -1 when it cannot be determined.

  Windows Update payloads under SoftwareDistribution\Download are commonly
  hard-linked into WinSxS. Removing one link frees NOTHING, yet every size sum
  built on FileInfo.Length reports the full file. Probing costs one fsutil call
  per file, so callers must throttle it (see -MaxHardlinkProbe).
#>
function Get-HardLinkCount {
    param([Parameter(Mandatory)][string]$Path)
    $fs = Get-SystemToolPath 'fsutil.exe'
    if (-not $fs) { return -1 }
    try {
        $out = @(& $fs hardlink list $Path 2>$null | Where-Object { $_ -and $_.ToString().Trim().Length -gt 0 })
        if ($out.Count -eq 0) { return -1 }
        return $out.Count
    } catch { return -1 }
}

# --------------------------------------------------------------- safe walker
<#
  Recursive enumeration that NEVER follows a reparse point.

  Get-ChildItem -Recurse in Windows PowerShell 5.1 walks straight into
  junctions (e.g. the %APPDATA% compatibility links), which both double-counts
  and can loop. This walker keeps an explicit stack and checks the reparse flag
  before descending. Returns FileSystemInfo objects.
#>
function Get-TreeEntries {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string[]]$Include = @('*'),
        [string[]]$Exclude = @(),
        [switch]$FilesOnly,
        [int]$MaxItems = 0,
        [ref]$Truncated
    )
    $out   = New-Object System.Collections.ArrayList
    $stack = New-Object System.Collections.Stack
    $stack.Push($Path)
    $trunc = $false

    while ($stack.Count -gt 0) {
        $cur = [string]$stack.Pop()
        $di  = $null
        try { $di = New-Object System.IO.DirectoryInfo($cur) } catch { continue }
        $entries = $null
        try { $entries = $di.GetFileSystemInfos() } catch { continue }

        foreach ($e in $entries) {
            $isDir  = (($e.Attributes -band [IO.FileAttributes]::Directory) -ne 0)
            $isLink = (($e.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)

            if ($isDir -and -not $isLink) { $stack.Push($e.FullName) }

            $match = $false
            foreach ($f in $Include) { if ($e.Name -like $f) { $match = $true; break } }
            if ($match -and $Exclude.Count -gt 0) {
                foreach ($x in $Exclude) { if ($e.Name -like $x) { $match = $false; break } }
            }
            if (-not $match) { continue }
            if ($FilesOnly -and $isDir) { continue }
            # a reparse point is listed (never descended) so the caller can skip
            # and log it rather than silently ignoring it
            [void]$out.Add($e)

            if ($MaxItems -gt 0 -and $out.Count -ge $MaxItems) { $trunc = $true; break }
        }
        if ($trunc) { break }
    }
    if ($Truncated) { $Truncated.Value = $trunc }
    return @($out)
}

function Test-ReparsePoint {
    param([Parameter(Mandatory)][string]$Path)
    try {
        $i = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        return (($i.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
    } catch { return $false }
}

<#
  Can this file be removed without fighting another process?

  A file held open can still be unlinked on NTFS, but its clusters stay
  allocated until the last handle closes ("delete-pending"), so such a file
  returns NO space even though the delete appears to succeed. The probe opens it
  with FileShare.None; a read-only file is retried with read access, because
  read-only is not the same as in-use (Remove-Item -Force handles read-only).
#>
function Test-FileLockable {
    param([Parameter(Mandatory)][string]$Path)
    $fh = $null
    try {
        $fh = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open,
                                     [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        return $true
    } catch {
        try {
            $fh2 = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open,
                                          [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
            $fh2.Dispose()
            return $true
        } catch { return $false }
    } finally {
        if ($fh) { $fh.Dispose() }
    }
}

<#
  Whether a directory likely cannot be removed right now: it is treated as
  locked when any of a small sample of the files inside it is locked, or when
  the directory cannot even be enumerated.
#>
function Test-DirContainsLockedFiles {
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$MaxProbe = 20
    )
    $entries = @()
    try { $entries = @(Get-TreeEntries -Path $Path -FilesOnly -MaxItems $MaxProbe) } catch { return $true }
    if ($entries.Count -eq 0) { return $false }
    foreach ($e in $entries) {
        if (-not (Test-FileLockable -Path $e.FullName)) { return $true }
    }
    return $false
}

function Remove-EmptySubdirs {
    param([Parameter(Mandatory)][string]$Path)
    $removed = 0
    $dirs = New-Object System.Collections.ArrayList
    $stack = New-Object System.Collections.Stack
    $stack.Push($Path)
    while ($stack.Count -gt 0) {
        $cur = [string]$stack.Pop()
        $di = $null
        try { $di = New-Object System.IO.DirectoryInfo($cur) } catch { continue }
        $subs = $null
        try { $subs = $di.GetDirectories() } catch { continue }
        foreach ($s in $subs) {
            if (($s.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            [void]$dirs.Add($s.FullName)
            $stack.Push($s.FullName)
        }
    }
    # deepest first: a parent only becomes empty after its children are gone
    foreach ($d in ($dirs | Sort-Object Length -Descending)) {
        try {
            if ([System.IO.Directory]::GetFileSystemEntries($d).Length -eq 0) {
                Remove-Item -LiteralPath $d -Force -ErrorAction Stop
                $removed++
            }
        } catch { }
    }
    return $removed
}

# --------------------------------------------------------------- the one deleter
function Add-SkipLog {
    param([string]$Line)
    if ([string]::IsNullOrWhiteSpace($Line)) { return }
    $dir = Join-Path $PSScriptRoot 'reports'
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Add-Content -Path (Join-Path $dir 'skipped.log') -Value $Line -ErrorAction SilentlyContinue
}

<#
  THE POINT-TO-POINT DELETER.

  A target is an exact path plus an explicit name-pattern selection -- the engine
  never sweeps an unknown tree. For every target it:

    * selects items by name pattern (and excludes more by pattern),
    * guards by age when asked,
    * refuses reparse points (never deleted, never followed),
    * refuses hard-linked files whose removal frees nothing,
    * deletes item by item, recording each locked item instead of aborting,
    * then MEASURES the volume's own free-space delta for that target alone.

  FreedMB (logical bytes of the selected items) and NetFreeMB (ground truth) are
  reported side by side, because they routinely disagree -- and only NetFreeMB
  is allowed to count as reclaimed space.

  Mode 'children' : delete the matching direct children.
  Mode 'files'    : delete the matching files at any depth (reparse points are
                    never descended into), then prune the empty subdirectories
                    left behind. $Path itself always survives.
#>
function Remove-TargetGuarded {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string[]]$Include = @('*'),
        [string[]]$Exclude = @(),
        [ValidateSet('children', 'files', 'file')][string]$Mode = 'children',
        [int]     $MinAgeDays = 0,
        [switch]  $Execute,
        [string]  $Label = '',
        [int]     $MaxItems = 0,
        [switch]  $SkipHardlinkProbe,
        [int]     $MaxHardlinkProbe = 100,
        [int64]   $HardlinkProbeMinBytes = 1048576,
        [int]     $SettleMs = 800,
        [switch]  $NoNetMeasure,
        [int]     $MaxLockProbe = 4000
    )

    $res = New-Object psobject -Property ([ordered]@{
        Path = $Path; Label = $Label; Mode = $Mode; Exists = $false
        SelectedItems = 0; SelectedMB = 0; EstimatedMB = 0; BeforeMB = 0; AfterMB = 0; FreedMB = 0
        FreeBeforeMB = 0; FreeAfterMB = 0; NetFreeMB = 0; ShadowGrowthMB = 0
        ItemsGone = 0; Locked = 0; LinkedSkipped = 0; HardlinkProbed = 0
        LockProbeCount = 0; LockedPredicted = 0
        ReparseSkipped = 0; PrunedDirs = 0; Truncated = $false
        Regression = $false; Hint = ''; Error = ''
    })

    if ([string]::IsNullOrWhiteSpace($Path)) { return $res }
    if (-not (Test-Path -LiteralPath $Path)) { return $res }
    try { [void](Assert-SafeTarget -Path $Path) } catch {
        $res.Error = $_.Exception.Message
        Write-Err2 $res.Error
        return $res
    }
    $res.Exists = $true

    $freeBefore = [int64]0; $shadowBefore = [int64]0
    if (-not $NoNetMeasure) {
        $freeBefore   = Get-FreeBytes
        $res.FreeBeforeMB = [math]::Round($freeBefore / 1MB, 1)
    }

    # ---- select ------------------------------------------------------------
    $trunc = $false
    if ($Mode -eq 'file') {
        # single exact file target (registered through Add-AllowedFile)
        $items = @()
        $one = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        if ($one -and (($one.Attributes -band [IO.FileAttributes]::Directory) -eq 0)) {
            $hit = $false
            foreach ($f in $Include) { if ($one.Name -like $f) { $hit = $true; break } }
            if ($hit) { $items = @($one) }
        }
    } elseif ($Mode -eq 'files') {
        $items = @(Get-TreeEntries -Path $Path -Include $Include -Exclude $Exclude `
                                   -FilesOnly -MaxItems $MaxItems -Truncated ([ref]$trunc))
    } else {
        $items = @(Get-TreeEntries -Path $Path -Include $Include -Exclude $Exclude `
                                   -MaxItems $MaxItems -Truncated ([ref]$trunc))
    }
    $res.Truncated = $trunc

    $selected = @($items)
    if ($MinAgeDays -gt 0) {
        $cutoff   = (Get-Date).AddDays(-1 * [int][math]::Abs($MinAgeDays))
        $selected = @($selected | Where-Object { $_.LastWriteTime -le $cutoff })
    }
    $res.SelectedItems = $selected.Count

    $selBytes = [int64]0
    foreach ($it in $selected) {
        if (($it.Attributes -band [IO.FileAttributes]::Directory) -ne 0) { $selBytes += Get-PathBytes -Path $it.FullName }
        else { $selBytes += [int64]$it.Length }
    }
    $res.SelectedMB = [math]::Round($selBytes / 1MB, 1)
    $res.BeforeMB   = [math]::Round((Get-PathBytes -Path $Path) / 1MB, 1)

    if (-not $Execute) {
        # A dry run reports the SELECTION only. It must never promise free-space
        # gain, because gain can only be established by deleting and measuring.
        #
        # It CAN predict one large source of disappointment: a file held open by
        # another process can be unlinked, but its clusters are not returned
        # while a handle remains open (delete-pending). That is why 182 MB of
        # C:\Windows\SystemTemp measured here shrank to 0.2 MB on a real run.
        # Each selected file is therefore probed for exclusive access.
        $lockedBytes = [int64]0; $lockedCount = 0; $probed = 0; $lockedDirs = 0
        foreach ($it in $selected) {
            if (($it.Attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                # a directory cannot be probed directly; sample the files inside
                if (Test-DirContainsLockedFiles -Path $it.FullName -MaxProbe 20) {
                    $lockedDirs++
                    $lockedBytes += Get-PathBytes -Path $it.FullName
                }
                continue
            }
            if ($MaxLockProbe -gt 0 -and $probed -ge $MaxLockProbe) { break }
            $probed++
            if (-not (Test-FileLockable -Path $it.FullName)) {
                $lockedCount++
                $lockedBytes += [int64]$it.Length
            }
        }
        $res.LockProbeCount  = $probed
        $res.LockedPredicted = $lockedCount + $lockedDirs
        $res.FreedMB         = $res.SelectedMB
        $res.EstimatedMB     = [math]::Round(($selBytes - $lockedBytes) / 1MB, 1)
        $res.AfterMB         = [math]::Round((Get-PathBytes -Path $Path) / 1MB, 1)
        return $res
    }

    # ---- hard-link screen --------------------------------------------------
    # Deleting one link of a hard-linked file frees zero bytes while inflating
    # every length-based total. Probe the big files (fsutil costs a call each)
    # and refuse the ones that cannot actually free space.
    if (-not $SkipHardlinkProbe -and $MaxHardlinkProbe -gt 0) {
        $budget = $MaxHardlinkProbe
        $kept   = New-Object System.Collections.ArrayList
        foreach ($it in $selected) {
            $isDir = (($it.Attributes -band [IO.FileAttributes]::Directory) -ne 0)
            if ($isDir -or $budget -le 0 -or [int64]$it.Length -lt $HardlinkProbeMinBytes) {
                [void]$kept.Add($it); continue
            }
            $budget--
            $res.HardlinkProbed++
            $links = Get-HardLinkCount -Path $it.FullName
            if ($links -gt 1) {
                $res.LinkedSkipped++
                Add-SkipLog ("HARDLINK-SKIPPED`t{0}`tlinks={1}" -f $it.FullName, $links)
                continue
            }
            [void]$kept.Add($it)
        }
        $selected = @($kept)
    }

    # ---- delete ------------------------------------------------------------
    foreach ($it in $selected) {
        if (($it.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            $res.ReparseSkipped++
            Add-SkipLog ("REPARSE-SKIPPED`t{0}" -f $it.FullName)
            continue
        }
        try {
            if (($it.Attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                Remove-Item -LiteralPath $it.FullName -Recurse -Force -ErrorAction Stop
            } else {
                Remove-Item -LiteralPath $it.FullName -Force -ErrorAction Stop
            }
            $res.ItemsGone++
        } catch {
            $res.Locked++
            $line = ("LOCKED`t{0}`t{1}" -f $it.FullName, $_.Exception.Message)
            [void]$script:Failures.Add($line)
            Add-SkipLog $line
        }
    }

    if ($Mode -eq 'files') { $res.PrunedDirs = Remove-EmptySubdirs -Path $Path }

    $res.AfterMB = [math]::Round((Get-PathBytes -Path $Path) / 1MB, 1)
    $res.FreedMB = [math]::Round($res.BeforeMB - $res.AfterMB, 1)

    # ---- ground truth for THIS target --------------------------------------
    if (-not $NoNetMeasure) {
        if ($SettleMs -gt 0) { Start-Sleep -Milliseconds $SettleMs }
        $freeAfter = Get-FreeBytes
        $res.FreeAfterMB = [math]::Round($freeAfter / 1MB, 1)
        $res.NetFreeMB   = [math]::Round(($freeAfter - $freeBefore) / 1MB, 1)
        # the shadow probe costs ~2 vssadmin calls, so only pay it for real targets
        if ($res.SelectedMB -ge 50) {
            $res.ShadowGrowthMB = [math]::Round(([int64](Get-ShadowStorageInfo).UsedBytes - $shadowBefore) / 1MB, 1)
        }
        if ($res.NetFreeMB -lt 0) { $res.Regression = $true }

        # Explain a shortfall instead of hiding it. Three mechanisms dominate, and
        # each one has a different remedy.
        if ($res.FreedMB -ge 5 -and $res.NetFreeMB -lt ($res.FreedMB * 0.6)) {
            if ($res.ShadowGrowthMB -gt 1) {
                $res.Hint = ("shadow copies absorbed {0:N1} MB of the {1:N1} MB removed (copy-on-write)" -f `
                    $res.ShadowGrowthMB, $res.FreedMB)
            } elseif ($res.Locked -gt 0) {
                $res.Hint = ("{0} item(s) stayed locked; the remainder is held open elsewhere (delete-pending) or was rewritten by the owning service" -f $res.Locked)
            } else {
                $res.Hint = 'files unlinked while another process still holds them keep their clusters until that handle closes (delete-pending)'
            }
        }
    }
    return $res
}

<#
  Clears the CONTENTS of $Path (never $Path itself).
  Kept as the stable entry point used by the older scripts; it is now a thin
  wrapper over Remove-TargetGuarded so every caller gains the same guarantees
  (hard-link screen, ground-truth NetFreeMB, reparse-point refusal).
#>
function Remove-ContentGuarded {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string[]]$Filter     = @('*'),
        [int]     $MinAgeDays = 0,
        [switch]  $Execute,
        [string]  $Label      = ''
    )
    return Remove-TargetGuarded -Path $Path -Include $Filter -Mode 'children' `
        -MinAgeDays $MinAgeDays -Execute:$Execute -Label $Label
}

# --------------------------------------------------------------- whole-tree deleter
<#
  Deletes $Path ITSELF (a self-contained project cache such as node_modules).
  Same guarantees as Remove-ContentGuarded: allowlist, measured freed bytes,
  locked-item skip, and reparse points are never followed.
  Only for directories explicitly named by the operator.
#>
function Remove-TreeGuarded {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$Execute,
        [string]$Label = ''
    )
    $res = New-Object psobject -Property ([ordered]@{
        Path = $Path; Label = $Label; Mode = 'tree'; Exists = $false
        SelectedItems = 0; SelectedMB = 0; BeforeMB = 0; AfterMB = 0; FreedMB = 0
        FreeBeforeMB = 0; FreeAfterMB = 0; NetFreeMB = 0; ShadowGrowthMB = 0
        ItemsGone = 0; Locked = 0; LinkedSkipped = 0; HardlinkProbed = 0
        ReparseSkipped = 0; PrunedDirs = 0; Truncated = $false
        Regression = $false; Error = ''
    })
    if (-not (Test-Path -LiteralPath $Path)) { return $res }
    try { [void](Assert-SafeTarget -Path $Path) } catch {
        $res.Error = $_.Exception.Message; Write-Err2 $res.Error; return $res
    }

    $res.Exists   = $true
    $res.BeforeMB = Get-DirSizeMB -Path $Path
    $res.SelectedMB = $res.BeforeMB
    if (-not $Execute) { $res.AfterMB = $res.BeforeMB; return $res }

    $freeBefore = Get-FreeBytes
    $res.FreeBeforeMB = [math]::Round($freeBefore / 1MB, 1)

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Write-Warn2 ("skipped reparse point (never deleted): {0}" -f $Path)
        $res.ReparseSkipped = 1
        Add-SkipLog ("REPARSE-SKIPPED`t{0}" -f $Path)
        $res.AfterMB = $res.BeforeMB
        return $res
    }
    try {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
        $res.ItemsGone = 1
    } catch {
        $res.Locked = 1
        $line = ("LOCKED`t{0}`t{1}" -f $Path, $_.Exception.Message)
        [void]$script:Failures.Add($line)
        Add-SkipLog $line
    }
    if (Test-Path -LiteralPath $Path) { $res.AfterMB = Get-DirSizeMB -Path $Path } else { $res.AfterMB = 0 }
    $res.FreedMB = [math]::Round($res.BeforeMB - $res.AfterMB, 1)
    Start-Sleep -Milliseconds 800
    $freeAfter = Get-FreeBytes
    $res.FreeAfterMB = [math]::Round($freeAfter / 1MB, 1)
    $res.NetFreeMB   = [math]::Round(($freeAfter - $freeBefore) / 1MB, 1)
    if ($res.NetFreeMB -lt 0) { $res.Regression = $true }
    return $res
}

# --------------------------------------------------------------- GPU detection
<#
  PCI vendor ids of display adapters that actually own a shader cache.
  Detection is deliberately PCI-id based: virtual / indirect display adapters
  (remote-desktop IDD drivers such as Oray / Parsec / IddSample, Microsoft Basic
  Render) report PNPDeviceID values like "ROOT\DISPLAY\0000" or "SWD\..." and
  carry NO PCI vendor id -- they are not GPUs and own no shader cache.
  (ASCII-only: this file has no BOM, so 5.1 would read a UTF-8 dash as GBK.)
#>
$script:GpuVendorIds = [ordered]@{
    '10DE' = 'NVIDIA'
    '1002' = 'AMD'
    '1022' = 'AMD'
    '8086' = 'Intel'
}

<#
  Enumerate display adapters and classify them by GPU vendor.
  @returns an object with:
    Vendors  - unique vendor names detected ('NVIDIA' / 'AMD' / 'Intel')
    Adapters - every display adapter seen, with Name / Vendor / Reason
#>
function Get-GpuVendor {
    $vendors = New-Object System.Collections.ArrayList
    $adapters = New-Object System.Collections.ArrayList

    foreach ($a in @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue)) {
        $name = [string]$a.Name
        $pnp  = [string]$a.PNPDeviceID

        if ($pnp -match '(?i)VEN_([0-9A-F]{4})') {
            $ven    = $Matches[1].ToUpper()
            $vendor = $script:GpuVendorIds[$ven]
            if ($vendor) {
                if (-not $vendors.Contains($vendor)) { [void]$vendors.Add($vendor) }
                [void]$adapters.Add((New-Object psobject -Property ([ordered]@{
                    Name = $name; Vendor = $vendor; PnpId = $pnp
                    Reason = ('PCI VEN_{0}' -f $ven)
                })))
            } else {
                [void]$adapters.Add((New-Object psobject -Property ([ordered]@{
                    Name = $name; Vendor = 'Other'; PnpId = $pnp
                    Reason = ('unknown PCI vendor VEN_{0}' -f $ven)
                })))
            }
        } else {
            [void]$adapters.Add((New-Object psobject -Property ([ordered]@{
                Name = $name; Vendor = 'Virtual'; PnpId = $pnp
                Reason = 'non-PCI / virtual display adapter'
            })))
        }
    }

    return New-Object psobject -Property @{
        Vendors  = @($vendors)
        Adapters = @($adapters)
    }
}

<#
  Shader-cache directories for the given vendors.
  Covers both the modern and legacy NVIDIA driver layouts ("NVIDIA" vs
  "NVIDIA Corporation", NV_Cache vs DXCache/GLCache).
  @param Vendors              vendor names returned by Get-GpuVendor
  @param IncludeVendorNeutral also include the vendor-agnostic Windows D3D cache
#>
function Get-GpuShaderCachePaths {
    param(
        [string[]]$Vendors,
        [switch] $IncludeVendorNeutral
    )
    $LA = $env:LOCALAPPDATA
    $PD = $env:ProgramData

    $map = @{
        'NVIDIA' = @(
            (Join-Path $LA 'NVIDIA\DXCache')
            (Join-Path $LA 'NVIDIA\GLCache')
            (Join-Path $LA 'NVIDIA\ComputeCache')
            (Join-Path $LA 'NVIDIA\NV_Cache')
            (Join-Path $LA 'NVIDIA Corporation\NV_Cache')
            (Join-Path $LA 'Temp\NVIDIA Corporation\NV_Cache')
            (Join-Path $PD 'NVIDIA Corporation\NV_Cache')
        )
        'AMD' = @(
            (Join-Path $LA 'AMD\DxCache')
            (Join-Path $LA 'AMD\DxcCache')
            (Join-Path $LA 'AMD\GLCache')
            (Join-Path $LA 'AMD\OglCache')
            (Join-Path $LA 'AMD\VkCache')
            (Join-Path $LA 'AMD\Dx9Cache')
        )
        'Intel' = @(
            (Join-Path $LA 'Intel\ShaderCache')
            (Join-Path $LA 'Intel\Shadercache')
        )
    }

    $out = New-Object System.Collections.ArrayList
    foreach ($v in $Vendors) {
        if ($map.ContainsKey($v)) { foreach ($p in $map[$v]) { [void]$out.Add($p) } }
    }
    if ($IncludeVendorNeutral) {
        foreach ($p in @(
            (Join-Path $LA 'D3DSCache')
            (Join-Path $LA 'Microsoft\DirectX Shader Cache')
            (Join-Path $LA 'Microsoft\Direct3D\ShaderCache')
        )) { [void]$out.Add($p) }
    }
    return @($out | Select-Object -Unique)
}

# --------------------------------------------------------------- reporting
function New-ReportDir {
    $d = Join-Path $PSScriptRoot 'reports'
    if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    return $d
}

function Show-Summary {
    param(
        [Parameter(Mandatory)]$Rows,
        [Parameter(Mandatory)][double]$FreeBefore,
        [Parameter(Mandatory)][double]$FreeAfter,
        [Parameter(Mandatory)][string]$Title,
        [string]$Tag = 'cleanup',
        [switch]$DryRun,
        $NetGuard
    )
    Write-Host ''
    Write-Host '=================================================================='
    Write-Host (" {0}" -f $Title)
    Write-Host '=================================================================='
    if (-not $DryRun) {
        Write-Host '   NET-FREE   LOGICAL   removed  locked   target'
        Write-Host '   ---------  ---------  -------  ------   ------------------------'
    }
    $tot = 0.0        # logical MB (sum of file lengths)
    $netTot = 0.0     # ground-truth MB (volume free-space delta per target)
    $hasNet = $false
    foreach ($r in $Rows) {
        if (-not $r.Exists) { continue }
        $tot += [double]$r.FreedMB
        $net = $null
        if (@($r.PSObject.Properties.Name) -contains 'NetFreeMB') { $net = [double]$r.NetFreeMB; $hasNet = $true }
        if ($DryRun -or $null -eq $net) {
            Write-Host ("   {0,10:N1} MB  {1,5} removed  {2,4} locked   {3}" -f `
                [double]$r.FreedMB, $r.ItemsGone, $r.Locked, $r.Path)
        } else {
            $netTot += $net
            $mark = ''
            if ($net -lt 0) { $mark = '  <== WENT BACKWARDS' }
            elseif ($net -le 0.01 -and [double]$r.FreedMB -ge 50) { $mark = '  <== nothing actually freed' }
            Write-Host ("   {0,8:N1} MB  {1,8:N1} MB  {2,7}  {3,6}   {4}{5}" -f `
                $net, [double]$r.FreedMB, $r.ItemsGone, $r.Locked, $r.Path, $mark)
        }
    }
    Write-Host '------------------------------------------------------------------'
    $delta  = [math]::Round($FreeAfter - $FreeBefore, 2)          # GB, positive = free space grew
    if ($DryRun) {
        Write-Host ("   SELECTED (logical)     : {0:N1} MB  ({1:N2} GB)   <-- upper bound only" -f $tot, ($tot / 1024))
        Write-Host ("   C: free before / after : {0} GB  ->  {1} GB" -f $FreeBefore, $FreeAfter)
        Write-Host '   net gain is measured only by a real (-Execute) run.'
    } else {
        Write-Host ("   LOGICAL (file lengths) : {0:N1} MB  ({1:N2} GB)   <-- NOT reclaimed space" -f $tot, ($tot / 1024))
        if ($hasNet) {
            Write-Host ("   NET FREE (ground truth): {0:N1} MB  ({1:N2} GB)" -f $netTot, ($netTot / 1024))
        }
        Write-Host ("   C: free before / after : {0} GB  ->  {1} GB   (delta {2} GB)" -f `
            $FreeBefore, $FreeAfter, $delta)
        Write-Host ("   C: USED space          : changed by {0} GB   (negative = shrank, which is the goal)" -f (-1 * $delta))
        if ($delta -gt 0) {
            Write-Ok ("C: used space went DOWN by {0} GB - free space only increased." -f $delta)
        } elseif ($delta -eq 0) {
            Write-Warn2 'C: free space is unchanged. Nothing that was selected could actually be reclaimed.'
        } else {
            Write-Err2 ("REGRESSION: C: free space fell {0} GB. Used space went UP." -f ([math]::Abs($delta)))
        }
        if ($hasNet -and ($netTot + 1) -lt ($tot / 1)) {
            Write-Warn2 ('the {0:N1} MB of selected data yielded {1:N1} MB of real free space. Remaining' -f $tot, $netTot)
            Write-Warn2 'explanations, in order of likelihood:'
            Write-Warn2 '  * Volume Shadow Copy: the deleted blocks are still referenced by a restore'
            Write-Warn2 '    point, and reusing them makes VSS copy the old content back into the diff area;'
            Write-Warn2 '  * hard links: removing one link of a linked file frees nothing (now skipped);'
            Write-Warn2 '  * the owning service rewrote the cache while we deleted it.'
            Write-Warn2 'Run 08-Diagnose-SpaceGrowth.ps1 to quantify which one is active.'
        }
    }
    if ($NetGuard) {
        Write-Host ("   shadow storage         : {0} GB used of {1} GB ({2}%)  copies={3}  growth this run={4:N1} MB" -f `
            $NetGuard.ShadowUsedGB, $NetGuard.ShadowMaxGB, $NetGuard.ShadowUsedPct, $NetGuard.ShadowCopies, [double]$NetGuard.ShadowGrowthMB)
        if ([double]$NetGuard.ShadowGrowthMB -gt 0) {
            Write-Warn2 ("shadow storage absorbed {0:N1} MB during this run - that space is NOT available to you." -f [double]$NetGuard.ShadowGrowthMB)
        }
        if ($NetGuard.Aborted) {
            Write-Err2 ("RUN ABORTED: {0}" -f $NetGuard.AbortReason)
            Write-Err2 'remaining targets were skipped so free space cannot keep going backwards.'
        }
    }
    $fails = Get-Failures
    if ($fails.Count -gt 0) {
        Write-Warn2 ("{0} item(s) were locked / in use and were SKIPPED (see reports\skipped.log)" -f $fails.Count)
    }
    Write-Host '=================================================================='

    # A dry run must NOT overwrite the report of a real cleanup, or a mere
    # preview destroys the audit trail. Dry runs get their own file.
    $fileTag = $Tag
    $runMode = 'execute'
    if ($DryRun) { $fileTag = $Tag + '.dryrun'; $runMode = 'dry-run' }
    $rep = Join-Path (New-ReportDir) ("{0}.json" -f $fileTag)
    $repObj = [ordered]@{
        Title = $Title; Tag = $Tag; Mode = $runMode; Time = (Get-Date).ToString('o')
        FreeBeforeGB = $FreeBefore; FreeAfterGB = $FreeAfter
        FreedMB = [math]::Round($tot, 1)
        LogicalMB = [math]::Round($tot, 1)
        NetFreeMB = $null
        Verdict = 'dry-run'
        Rows = @($Rows)
        Failures = @($fails)
    }
    if (-not $DryRun) {
        $repObj.NetFreeMB = [math]::Round($netTot, 1)
        $verdict = 'GAIN'
        if ($delta -lt 0) { $verdict = 'REGRESSION' } elseif ($delta -eq 0) { $verdict = 'NEUTRAL' }
        $repObj.Verdict = $verdict
    }
    if ($NetGuard) { $repObj.NetGuard = $NetGuard }
    $repObj | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $rep -Encoding UTF8
    Write-Host ("   report -> {0}" -f $rep)
    if ($DryRun) { return [math]::Round($tot, 1) }
    return [math]::Round($netTot, 1)
}
