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

    # WHY NOT Get-ChildItem -Recurse (measured, 2026-10-16)
    #
    # On C:\Windows\SoftwareDistribution\Download (309,730 files) Get-ChildItem -Recurse
    # -Force -File costs 16.1 s PER CALL, and the dry run called this twice per target.
    # DirectoryInfo.GetFileSystemInfos() walks the same tree in ~2 s, and summing while
    # walking costs nothing extra because no FileInfo is handed to the caller.
    #
    # Reparse points are NOT followed. That also matches the rule the deletion engine
    # already follows: Get-ChildItem -Recurse in Windows PowerShell 5.1 walks straight into
    # junctions, which double-counts a subtree and can loop.
    $sum = [int64]0
    $stack = New-Object System.Collections.Stack
    $stack.Push($Path)
    while ($stack.Count -gt 0) {
        $cur = [string]$stack.Pop()
        $di = $null
        try { $di = New-Object System.IO.DirectoryInfo($cur) } catch { continue }
        $entries = $null
        try { $entries = $di.GetFileSystemInfos() } catch { continue }

        foreach ($e in $entries) {
            $attr = $e.Attributes
            if (($attr -band [IO.FileAttributes]::Directory) -ne 0) {
                if (($attr -band [IO.FileAttributes]::ReparsePoint) -eq 0) { $stack.Push($e.FullName) }
            } else {
                $sum += [int64]$e.Length
            }
        }
    }
    return $sum
}

<#
  Subtree size of EVERY directory under $Path, in ONE traversal.

  Why this exists: Remove-TargetGuarded used to call Get-PathBytes once per selected
  directory child. On SoftwareDistribution\Download (51 directory children) that was
  29.4 s of the 53 s the target took. The same walk also yields the target's own total, so
  BeforeMB no longer needs a separate traversal either.

  Returns a Dictionary[dir, bytes] that contains $Path itself. Reparse points are not
  followed, so a junction is neither counted nor descended into.
#>
function Get-DirSizes {
    param([Parameter(Mandatory)][string]$Path)
    $map = New-Object 'System.Collections.Generic.Dictionary[string,int64]' ([StringComparer]::OrdinalIgnoreCase)

    # Resolve to the long form before walking. DirectoryInfo.FullName yields names such as
    # C:\Users\Administrator\... while the caller may have handed us the 8.3 short form
    # (C:\Users\ADMINI~1\...); mixing the two shapes makes lookups miss silently, which the
    # test suite caught. The caller's own spelling of the root stays usable as an alias.
    $resolved = $Path
    try { $resolved = (New-Object System.IO.DirectoryInfo($Path)).FullName } catch { }
    [void](Get-DirSizeInto -Dir $resolved -Map $map)
    if ($resolved -ne $Path -and $map.ContainsKey($resolved)) { $map[$Path] = $map[$resolved] }

    return $map
}

function Get-DirSizeInto {
    param([string]$Dir, $Map)
    $total = [int64]0
    $di = $null
    try { $di = New-Object System.IO.DirectoryInfo($Dir) } catch { return 0 }

    $entries = $null
    try { $entries = $di.GetFileSystemInfos() } catch { }

    if ($entries) {
        foreach ($e in $entries) {
            $attr = $e.Attributes
            if (($attr -band [IO.FileAttributes]::Directory) -ne 0) {
                if (($attr -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
                $total += Get-DirSizeInto -Dir $e.FullName -Map $Map
            } else {
                $total += [int64]$e.Length
            }
        }
    }

    $Map[$Dir] = $total
    return $total
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
    param(
        [string]$Drive = 'C:',
        [double]$NoiseFloorMB = 32,
        [double]$NoisePerMinMB = 8
    )
    $shadow = Get-ShadowStorageInfo -Drive $Drive
    $script:NetGuard = [ordered]@{
        Drive         = $Drive.TrimEnd(':').ToUpper()
        StartFree     = Get-FreeBytes -Drive $Drive
        CurrentFree   = [int64]0
        StartUsed     = Get-UsedBytes -Drive $Drive
        StartShadow   = [int64]$shadow.UsedBytes
        ShadowAtStart = $shadow
        StartTime     = Get-Date
        LastStepTime  = (Get-Date)
        Steps         = (New-Object System.Collections.ArrayList)
        Regressions   = (New-Object System.Collections.ArrayList)
        Aborted       = $false
        AbortReason   = ''
        MaxDropMB     = 0
        # Noise floor. Measured on this machine with NOTHING running: C: free space jitters by
        # ~4 MB in 30 s and drifts ~7 MB/min (436 MB/hour) from Defender, telemetry and
        # browsers alone. A verdict built on a smaller number than that describes the
        # background, not the cleanup - one real run aborted every remaining target over a
        # 3.5 MB whole-run change, which is less than half a minute of this volume's own noise.
        NoiseFloorMB  = $NoiseFloorMB
        NoisePerMinMB = $NoisePerMinMB
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
        [double]$ToleranceMB = 0,
        [int]$RemovedItems = -1        # -1 = unknown, 0 = this step removed nothing at all
    )
    $g = $script:NetGuard
    if (-not $g) { return $false }
    $free  = Get-FreeBytes -Drive $g.Drive
    $delta = $free - [int64]$g.CurrentFree
    $g.CurrentFree = $free

    $stepMinutes = ((Get-Date) - $g.LastStepTime).TotalMinutes
    if ($stepMinutes -lt 0) { $stepMinutes = 0 }
    $g.LastStepTime = Get-Date

    [void]$g.Steps.Add((New-Object psobject -Property ([ordered]@{
        Label = $Label; DeltaMB = [math]::Round($delta / 1MB, 1); Time = (Get-Date).ToString('HH:mm:ss')
    })))

    if ($delta -lt 0) {
        $mb = [math]::Round($delta / 1MB, 1)
        if ([math]::Abs($mb) -gt $g.MaxDropMB) { $g.MaxDropMB = [math]::Abs($mb) }
        [void]$g.Regressions.Add(("{0}: {1:N1} MB" -f $Label, $mb))

        # The tolerance is a floor plus a rate: a step that took three minutes is allowed the
        # three minutes' worth of writes that other processes did during it.
        $eff = [double]$g.NoiseFloorMB
        if ($ToleranceMB -gt $eff) { $eff = $ToleranceMB }
        $scaled = [double]$g.NoisePerMinMB * $stepMinutes
        if ($scaled -gt $eff) { $eff = $scaled }

        # Attribution. If this step removed nothing, a fall in free space cannot be its doing -
        # that is exactly the update-cache case here: every delete was denied, so the target
        # freed zero bytes while other writers kept going, and the run was stopped for it.
        $attributable = ($RemovedItems -ne 0)

        if ($AbortIfNegative -and $attributable -and ([math]::Abs($mb) -gt $eff)) {
            $g.Aborted = $true
            $g.AbortReason = ("free space fell {0:N1} MB during '{1}' after removing items (noise floor {2:N0} MB over {3:N1} min)" -f $mb, $Label, $eff, $stepMinutes)
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

    # The whole-run verdict gets the same noise floor, scaled by how long the run took. Below
    # it the volume's own writers dominate, and the honest answer is "unmeasurable", not "the
    # guarantee broke".
    $runMinutes = ((Get-Date) - $g.StartTime).TotalMinutes
    if ($runMinutes -lt 0) { $runMinutes = 0 }
    $eff = [double]$g.NoiseFloorMB
    $scaled = [double]$g.NoisePerMinMB * $runMinutes
    if ($scaled -gt $eff) { $eff = $scaled }

    $verdict = 'GAIN'
    if ($netFree -lt 0) { $verdict = 'REGRESSION' }
    elseif ($netFree -eq 0) { $verdict = 'NEUTRAL' }
    if ($verdict -eq 'REGRESSION' -and ([math]::Abs($netFree / 1MB) -le $eff)) { $verdict = 'NEUTRAL' }

    return New-Object psobject -Property ([ordered]@{
        Drive = $g.Drive
        FreeStartGB = [math]::Round([int64]$g.StartFree / 1GB, 2)
        FreeEndGB   = [math]::Round($free / 1GB, 2)
        NetFreeMB   = [math]::Round($netFree / 1MB, 1)
        NetUsedMB   = [math]::Round($netUsed / 1MB, 1)
        NoiseFloorMB = [math]::Round($eff, 1)
        RunMinutes   = [math]::Round($runMinutes, 2)
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
$script:SkipLogBuffer = New-Object System.Collections.ArrayList

<#
  Writes a report file without ever truncating the live one.

  WHY: the window shell keeps the report it is displaying open for reading, so a plain
  Set-Content on reports\system.json fails outright. Measured at the end of a real -Execute
  run: Set-Content : access to "...\runtime\reports\system.json" was denied - the run lost its
  numbers and the GUI had nothing to show. Writing a fresh file and swapping works because
  the reader's handle is on the OLD file; if even the swap is denied, a timestamped name is
  used so the numbers are never lost.
#>
function Write-ReportText {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text
    )
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        try { New-Item -ItemType Directory -Path $dir -Force | Out-Null } catch { }
    }

    $tmp = "$Path.new"
    try {
        Set-Content -LiteralPath $tmp -Value $Text -Encoding UTF8 -ErrorAction Stop
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        Move-Item -LiteralPath $tmp -Destination $Path -Force -ErrorAction Stop
        return $Path
    } catch {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    }

    $alt = Join-Path $dir ("{0}-{1}{2}" -f [System.IO.Path]::GetFileNameWithoutExtension($Path),
                                          (Get-Date -Format 'HHmmss'),
                                          [System.IO.Path]::GetExtension($Path))
    try {
        Set-Content -LiteralPath $alt -Value $Text -Encoding UTF8 -ErrorAction Stop
        Write-Warn2 ("report was locked; wrote {0} instead" -f $alt)
        return $alt
    } catch { return $null }
}

function Add-SkipLog {
    param([string]$Line)
    if ([string]::IsNullOrWhiteSpace($Line)) { return }
    if (-not $script:SkipLogBuffer) { $script:SkipLogBuffer = New-Object System.Collections.ArrayList }
    [void]$script:SkipLogBuffer.Add($Line)
    if ($script:SkipLogBuffer.Count -ge 500) { Flush-SkipLog }
}

# Why buffered: every skipped item used to be its own Add-Content call. This machine ended up
# with a 78 MB / 344,131-line skipped.log, i.e. hundreds of thousands of open/append/close
# cycles during one run. Batches of 500 keep the record intact and the run fast.
#
# Why UTF-8: Add-Content without an explicit encoding writes the ANSI code page, so every
# Chinese error message came back as mojibake when the GUI read the file
# (an access-denied message rendered as "??-??..."). The log is a user-facing record; it has
# to be readable.
function Flush-SkipLog {
    if (-not $script:SkipLogBuffer -or $script:SkipLogBuffer.Count -eq 0) { return }
    try {
        $dir = Join-Path $PSScriptRoot 'reports'
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $path = Join-Path $dir 'skipped.log'

        # Rotate. A single bad target can produce hundreds of thousands of lines: this machine
        # reached 78 MB in one afternoon, and a log that big is a bug of its own. One previous
        # file is kept.
        if ((Test-Path -LiteralPath $path) -and (Get-Item -LiteralPath $path).Length -gt 16MB) {
            $old = "$path.1"
            if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue }
            Move-Item -LiteralPath $path -Destination $old -Force -ErrorAction SilentlyContinue
        }

        Add-Content -LiteralPath $path -Value $script:SkipLogBuffer.ToArray() `
                    -Encoding UTF8 -ErrorAction SilentlyContinue
    } catch { }
    $script:SkipLogBuffer.Clear()
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

    # ---- ONE traversal for every size we need ------------------------------
    # Before this, every selected directory child cost its own full Get-PathBytes walk
    # (51 of them on SoftwareDistribution\Download = 29.4 s) and BeforeMB cost another.
    $dirSizes = Get-DirSizes -Path $Path
    $rootBytes = [int64]0
    if ($dirSizes.ContainsKey($Path)) { $rootBytes = $dirSizes[$Path] }

    # ---- the deletion set is the TOP-MOST selection ------------------------
    # In 'children' mode the enumeration returns a directory AND everything inside it, so a
    # file under a selected directory was selected twice. That inflated every size by the
    # depth of the tree - measured on this machine: a 2,806 MB directory reported as
    # "10,092.6 MB selected", and a 6,882 MB one as "13,764.6 MB" (exactly 2x). It also made
    # real runs log the files inside an already-removed directory as LOCKED, i.e. thousands
    # of phantom failures in skipped.log.
    #
    # 'files' mode passes -FilesOnly, so it has no selected directories and $top is $selected.
    $selDirs = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($it in $selected) {
        if (($it.Attributes -band [IO.FileAttributes]::Directory) -ne 0) { [void]$selDirs.Add($it.FullName) }
    }

    if ($selDirs.Count -eq 0) {
        $top = $selected
    } else {
        $built = New-Object System.Collections.ArrayList
        foreach ($it in $selected) {
            $parent = [System.IO.Path]::GetDirectoryName($it.FullName)
            if (-not [string]::IsNullOrEmpty($parent) -and $selDirs.Contains($parent)) { continue }
            [void]$built.Add($it)
        }
        $top = @($built)
    }

    $selBytes = [int64]0
    foreach ($it in $top) {
        if (($it.Attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            $sz = [int64]0
            if ($dirSizes.ContainsKey($it.FullName)) { $sz = $dirSizes[$it.FullName] }
            $selBytes += $sz
        } else {
            $selBytes += [int64]$it.Length
        }
    }
    $res.SelectedMB = [math]::Round($selBytes / 1MB, 1)
    $res.BeforeMB   = [math]::Round($rootBytes / 1MB, 1)

    if (-not $Execute) {
        # A dry run reports the SELECTION only. It must never promise free-space
        # gain, because gain can only be established by deleting and measuring.
        #
        # It CAN predict one large source of disappointment: a file held open by
        # another process can be unlinked, but its clusters are not returned
        # while a handle remains open (delete-pending). That is why 182 MB of
        # C:\Windows\SystemTemp measured here shrank to 0.2 MB on a real run.
        # Each selected file is therefore probed for exclusive access.
        # $top, not $selected: these are the items a real run would remove, and their sizes
        # come out of the same map the selection used. Iterating the raw selection counted a
        # nested directory's subtree once per ancestor level, which is how "probably free"
        # could come out NEGATIVE (measured: -6,882.0 MB of 6,882.3 MB selected).
        $lockedBytes = [int64]0; $lockedCount = 0; $probed = 0; $lockedDirs = 0
        foreach ($it in $top) {
            if (($it.Attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                # a directory cannot be probed directly; sample the files inside
                if (Test-DirContainsLockedFiles -Path $it.FullName -MaxProbe 20) {
                    $lockedDirs++
                    $sz = [int64]0
                    if ($dirSizes.ContainsKey($it.FullName)) { $sz = $dirSizes[$it.FullName] }
                    $lockedBytes += $sz
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
        # A dry run deletes nothing, so "after" equals "before" by definition. Walking the
        # tree a second time to prove that cost ~16 s per target on the update cache.
        $res.AfterMB         = $res.BeforeMB
        return $res
    }

    # ---- hard-link screen --------------------------------------------------
    # Deleting one link of a hard-linked file frees zero bytes while inflating
    # every length-based total. Probe the big files (fsutil costs a call each)
    # and refuse the ones that cannot actually free space.
    if (-not $SkipHardlinkProbe -and $MaxHardlinkProbe -gt 0) {
        $budget = $MaxHardlinkProbe
        $kept   = New-Object System.Collections.ArrayList
        # $top, not $selected: anything nested under a selected directory is removed WITH
        # that directory, so probing or listing it separately is wasted work.
        foreach ($it in $top) {
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
        $top = @($kept)
    }

    # ---- delete ------------------------------------------------------------
    # $top: one Remove-Item -Recurse per top-most item, so files inside a removed directory
    # are never touched individually (that is what used to fill skipped.log with phantom locks).
    #
    # BUT a recursive delete is all-or-nothing: ONE file held open inside a tree fails the
    # whole subtree and frees nothing. Measured on this machine - Windows Update's services
    # hold files under SoftwareDistribution\Download, every top-level GUID directory came back
    # "access denied", the target freed 0 bytes while other processes kept writing, and the
    # net-gain guard then reported that C: used space GREW. So when the tree delete fails,
    # fall back to taking the files one at a time: that is what the old per-file loop did, and
    # it is the difference between freeing most of a cache and freeing none of it.
    foreach ($it in $top) {
        if (($it.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            $res.ReparseSkipped++
            Add-SkipLog ("REPARSE-SKIPPED`t{0}" -f $it.FullName)
            continue
        }

        $isDir = (($it.Attributes -band [IO.FileAttributes]::Directory) -ne 0)
        try {
            if ($isDir) { Remove-Item -LiteralPath $it.FullName -Recurse -Force -ErrorAction Stop }
            else        { Remove-Item -LiteralPath $it.FullName -Force -ErrorAction Stop }
            $res.ItemsGone++
            continue
        } catch {
            if (-not $isDir) {
                $res.Locked++
                $line = ("LOCKED`t{0}`t{1}" -f $it.FullName, $_.Exception.Message)
                [void]$script:Failures.Add($line)
                Add-SkipLog $line
                continue
            }
            Add-SkipLog ("TREE-FAILED`t{0}`t{1}" -f $it.FullName, $_.Exception.Message)
        }

        $denied = 0
        foreach ($f in @(Get-TreeEntries -Path $it.FullName -FilesOnly)) {
            try {
                Remove-Item -LiteralPath $f.FullName -Force -ErrorAction Stop
                $res.ItemsGone++
                $denied = 0
            } catch {
                $res.Locked++
                $denied++
                $line = ("LOCKED`t{0}`t{1}" -f $f.FullName, $_.Exception.Message)
                [void]$script:Failures.Add($line)
                Add-SkipLog $line

                # Safety valve: when a whole tree is denied item by item, something is holding
                # it (Windows Update / Delivery Optimization still running). Grinding through
                # 300,000 files that all fail is what turned one target into a 20-minute run
                # and a 78 MB log, and it frees nothing. Give up on this tree and say why.
                if ($denied -ge 200) {
                    Add-SkipLog ("TREE-ABANDONED`t{0}`t{1} consecutive denials - the service holding this cache is most likely still running" -f $it.FullName, $denied)
                    break
                }
            }
        }
        # take the directories that are empty now (fails harmlessly while anything is left)
        try { Remove-Item -LiteralPath $it.FullName -Recurse -Force -ErrorAction Stop } catch { }
    }
    Flush-SkipLog

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
    Flush-SkipLog    # never lose the tail of the skipped-item record
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
        # Say WHICH target - and therefore which program - is holding them. A run that selects
        # gigabytes and reclaims nothing is useless unless it tells the user what to close.
        # Measured here: 20,080 denials, 8,370 of them Edge's cache, 5,312 the 360 browser's,
        # 4,984 the user Temp: the files were open in running programs, not ACL-protected.
        $pathList = @($Rows | Where-Object { $_.Path } | ForEach-Object { $_.Path } | Sort-Object Length -Descending)
        $byTarget = @{}
        foreach ($line in $fails) {
            $p = ($line -split "`t")[1]
            if (-not $p) { continue }
            $key = 'other'
            foreach ($rp in $pathList) {
                if ($p.StartsWith($rp, [System.StringComparison]::OrdinalIgnoreCase)) { $key = $rp; break }
            }
            if ($byTarget.ContainsKey($key)) { $byTarget[$key]++ } else { $byTarget[$key] = 1 }
        }
        $byTarget.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 5 | ForEach-Object {
            Write-Host ("     {0,6}  {1}" -f $_.Value, $_.Key)
        }
        Write-Warn2 'These are held by programs that are RUNNING (browsers and download managers are'
        Write-Warn2 'the usual ones). Close them and run again to actually reclaim the space.'
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
    $repObj | ConvertTo-Json -Depth 8 | ForEach-Object { $null = Write-ReportText -Path $rep -Text $_ }
    Write-Host ("   report -> {0}" -f $rep)
    if ($DryRun) { return [math]::Round($tot, 1) }
    return [math]::Round($netTot, 1)
}
