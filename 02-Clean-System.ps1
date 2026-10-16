#requires -Version 5.1
<#
  02-Clean-System.ps1  --  system junk / cache cleanup, rebuilt around an
  explicit catalogue and a hard net-free-space guarantee.

  WHAT CHANGED AND WHY

  1. DISCOVERY IS REAL NOW
     The old version knew 12 hard-coded directories and swept their whole
     contents. This one uses _SystemCatalog.ps1: ~90 audited point-to-point
     targets (update caches, logs, dumps, WER, shell caches, font cache,
     per-app packaged caches, every Chromium-family browser profile's cache,
     GPU shader caches) plus a bounded discovery pass that finds cache
     directories nobody hard-coded. Run -Discover or -ListCatalog to see them.

  2. EVERY TARGET IS POINT-TO-POINT
     Each entry names an exact directory plus exact name patterns, an age
     guard, and a mode. Whole-tree sweeps of an application directory are gone.
     Files are deleted individually; empty subdirectories are pruned.

  3. FREE SPACE CAN ONLY GO UP
     "Freed MB" used to be the sum of file lengths, which is fiction: it counts
     hard-linked files that free nothing, ignores compression and shadow copies,
     and cannot see a service rewriting the cache. Now every target is bracketed
     by a reading of the VOLUME's own free-space counter, and the run aborts the
     moment free space goes backwards. NetFreeMB is reported next to the logical
     figure, and only NetFreeMB counts.
     Measured on the reference machine: a single 800 MB write/delete cycle
     permanently consumed 87.3 MB of C: through shadow-copy copy-on-write, which
     is exactly the kind of loss the old accounting could not see.

  4. THE HIDDEN CONSUMER IS NAMED
     Volume Shadow Copy diff storage is reported before and after. When it is
     near its cap it absorbs freed space silently; -ShadowTrim reclaims it by
     dropping old restore points (opt-in, destructive to restore points).

  DRY RUN BY DEFAULT. Nothing is deleted without -Execute.

  Usage
    .\02-Clean-System.ps1                          # plan only (profile standard)
    .\02-Clean-System.ps1 -Discover                # read-only: find junk caches
    .\02-Clean-System.ps1 -ListCatalog             # read-only: show all targets
    .\02-Clean-System.ps1 -Execute                 # clean, abort on regression
    .\02-Clean-System.ps1 -Execute -TempAgeDays 0  # also today's temp files
    .\02-Clean-System.ps1 -Execute -Profile safe   # most conservative set
    .\02-Clean-System.ps1 -Execute -Profile aggressive -Discover
    .\02-Clean-System.ps1 -Execute -Only temp.user,logs.windows,dump.livekernel
    .\02-Clean-System.ps1 -Execute -Skip browser.*,app.*
    .\02-Clean-System.ps1 -Execute -RestartExplorer        # unlock thumbcache
    .\02-Clean-System.ps1 -Execute -IncludeRecycleBin
    .\02-Clean-System.ps1 -Execute -IncludeGpuShader -IncludeD3DSCache
    .\02-Clean-System.ps1 -Execute -IncludeComponentStore   # DISM component store
    .\02-Clean-System.ps1 -Execute -ShadowTrim -ShadowTargetGB 2
#>
[CmdletBinding()]
param(
    [switch]$Execute,
    [int]   $TempAgeDays = 1,
    [ValidateSet('safe', 'standard', 'aggressive')][string]$Profile = 'standard',
    [switch]$Discover,
    [int]   $DiscoverMinMB = 20,
    [int]   $DiscoverTop = 30,
    [switch]$ListCatalog,
    [string[]]$Only = @(),
    [string[]]$Skip = @(),
    [switch]$RestartExplorer,
    [switch]$IncludeRecycleBin,
    [switch]$IncludeGpuShader,
    [switch]$IncludeD3DSCache,
    [switch]$IncludeComponentStore,
    [switch]$ShadowTrim,
    [double]$ShadowTargetGB = 2,
    [switch]$NoAbortOnRegression,
    [double]$RegressionToleranceMB = 16,
    [switch]$NoGuard
)

$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot '_Common.ps1')
. (Join-Path $PSScriptRoot '_SystemCatalog.ps1')

$mode = 'DRY RUN (nothing is deleted)'
if ($Execute) { $mode = 'EXECUTE' }
$admin = Test-IsAdmin

Write-Host '=================================================================='
Write-Host (" 02-Clean-System.ps1    mode={0}   profile={1}" -f $mode, $Profile)
Write-Host (" Administrator={0}   TempAgeDays={1}" -f $admin, $TempAgeDays)
Write-Host '=================================================================='
if ($Execute -and -not $admin) {
    Write-Warn2 'Not elevated: Windows\Temp, update caches, WER and the font cache will fail or be skipped.'
}
if (-not $Execute) { Write-Warn2 'DRY RUN: append -Execute to actually delete. Nothing was changed.' }

# ---------------------------------------------------------------- preflight
$shadow0 = Get-ShadowStorageInfo
if ($shadow0.Ok) {
    Write-Host (" Shadow copy storage on C: : {0:N2} GB used of {1:N2} GB ({2}%), {3} restore point(s)" -f `
        ($shadow0.UsedBytes / 1GB), ($shadow0.MaxBytes / 1GB), $shadow0.UsedPct, $shadow0.Copies)
    if ($shadow0.UsedPct -ge 60) {
        Write-Warn2 'Shadow storage is mostly full. Deleted blocks that a restore point still'
        Write-Warn2 'references do not come back as free space, and reusing them makes VSS copy'
        Write-Warn2 'the old data into the diff area - C: can go UP while this script reports'
        Write-Warn2 '"freed". This run will therefore report NET free space, not file sizes.'
        if ($Execute -and -not $ShadowTrim) {
            Write-Warn2 'Add -ShadowTrim to reclaim it (WARNING: that drops old restore points).'
        }
    }
} else {
    Write-Warn2 'Shadow copy state unknown (vssadmin unavailable) - net accounting still applies.'
}
$freeBefore = Get-FreeGB 'C:'
Write-Host (" C: free before : {0} GB" -f $freeBefore)
Write-Host ''

# ------------------------------------------------- update activity preflight
<#
  Cleaning the update caches while Windows is downloading updates is the single
  most self-defeating thing this script can do: the moment wuauserv / DoSvc
  notices its cache is gone it starts refilling it, so C: used space grows while
  the tool reports "freed".

  Measured on the reference machine, minutes apart:
    SoftwareDistribution\Download        0 MB  ->   803.9 MB
    DeliveryOptimization Cache           0 MB  -> 4,958.9 MB
    C: free                          105.95 GB  ->  105.47 GB   (USED WENT UP 480 MB)

  The check below samples those two caches twice and looks for growth.
#>
if (-not $Discover -and -not $ListCatalog) {
    $wuCache = 'C:\Windows\SoftwareDistribution\Download'
    $doCache = 'C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache'
    $busy = @(Get-Service -Name 'wuauserv', 'DoSvc', 'TrustedInstaller', 'BITS' -ErrorAction SilentlyContinue |
              Where-Object { $_.Status -eq 'Running' } | Select-Object -ExpandProperty Name)

    Write-Step 'CHECKING WHETHER WINDOWS IS ACTIVELY DOWNLOADING UPDATES'
    $b1 = [int64]0
    foreach ($p in @($wuCache, $doCache)) { if (Test-Path -LiteralPath $p) { $b1 += Get-PathBytes -Path $p } }
    Start-Sleep -Seconds 3
    $b2 = [int64]0
    foreach ($p in @($wuCache, $doCache)) { if (Test-Path -LiteralPath $p) { $b2 += Get-PathBytes -Path $p } }
    $grow = $b2 - $b1
    Write-Host ("   update caches now: {0:N1} MB   (change over 3 s: {1:N1} MB)   running: {2}" -f `
        ($b2 / 1MB), ($grow / 1MB), $(if ($busy.Count) { $busy -join ',' } else { 'none' }))

    if ($grow -gt 1MB) {
        Write-Warn2 'Windows is DOWNLOADING UPDATES RIGHT NOW.'
        Write-Warn2 'Cleaning the update caches now will be undone within minutes and C: used'
        Write-Warn2 'space will grow - this is the "cleaned it and it got fuller" case.'
        Write-Warn2 'Recommended: pause updates first, or exclude these targets:'
        Write-Warn2 '   -Skip wu.*,do.*,bits.*        (or)  Settings > Windows Update > Pause'
        if ($Execute -and -not $Skip -contains 'wu.*') {
            Write-Warn2 'Proceeding, but the update-cache targets are likely to report ~0 net gain.'
        }
    } elseif ($busy.Count -gt 0 -and $b2 -gt (300MB)) {
        Write-Warn2 ("update services are running and {0:N1} MB of update cache is present: a re-download may follow" -f ($b2 / 1MB))
    } else {
        Write-Ok 'no active update download detected'
    }
    Write-Host ''
}

# ---------------------------------------------------------------- discovery
if ($Discover) {
    Write-Step 'DISCOVERING cache directories not hard-coded in the catalogue'
    Write-Host ("   scanning %LOCALAPPDATA%, %APPDATA%, ProgramData\Microsoft, ~\.cache (depth 4, >= {0} MB)" -f $DiscoverMinMB)
    $found = @(Get-DiscoveredCacheDirs -MinMB $DiscoverMinMB -Top $DiscoverTop)
    Write-Host ''
    if ($found.Count -eq 0) {
        Write-Host ("   nothing >= {0} MB found." -f $DiscoverMinMB)
    } else {
        Write-Host '   SIZE          MINAGE  DIRECTORY'
        Write-Host '   ------------  ------  ------------------------------------------------'
        foreach ($f in $found) {
            Write-Host ("   {0,10:N1} MB  {1,5}d  {2}" -f $f.MB, $f.MinAgeDays, $f.Path)
        }
        $sum = ($found | Measure-Object MB -Sum).Sum
        Write-Host ''
        Write-Ok ("{0} cache director(ies), {1:N1} MB total ({2:N2} GB) - all regenerable" -f $found.Count, $sum, ($sum / 1024))
        Write-Host '   add -Profile aggressive -Discover to include these in the cleanup set.'
    }

    # big leftovers this script deliberately does not touch
    Write-Host ''
    Write-Step 'BIG LEFTOVERS (handled by 06-Clean-RootLeftovers.ps1, not by this script)'
    $leftovers = New-Object System.Collections.ArrayList
    foreach ($p in @('C:\$WinREAgent', 'C:\Windows.old', 'C:\$Windows.~BT', 'C:\$Windows.~WS',
                     'C:\Windows\Installer', 'C:\$Recycle.Bin')) {
        if (-not (Test-Path -LiteralPath $p)) { continue }
        $mb = Get-DirSizeMB -Path $p
        if ($mb -lt 50) { continue }
        [void]$leftovers.Add((New-Object psobject -Property ([ordered]@{ Path = $p; MB = $mb })))
        Write-Host ("   {0,10:N1} MB  {1}" -f $mb, $p)
    }
    $hexRoots = @(Get-ChildItem -LiteralPath 'C:\' -Directory -Force -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -match '^[0-9a-fA-F]{32}$' })
    foreach ($h in $hexRoots) {
        $mb = Get-DirSizeMB -Path $h.FullName
        [void]$leftovers.Add((New-Object psobject -Property ([ordered]@{ Path = $h.FullName; MB = $mb })))
        Write-Host ("   {0,10:N1} MB  {1}   [abandoned HLS cache]" -f $mb, $h.FullName)
    }
    $rep = Join-Path (New-ReportDir) 'discover.json'
    [ordered]@{
        Time = (Get-Date).ToString('o'); Mode = 'read-only discovery'
        MinMB = $DiscoverMinMB; Found = @($found); Leftovers = @($leftovers)
        TotalFoundMB = [math]::Round((($found | Measure-Object MB -Sum).Sum), 1)
    } | ConvertTo-Json -Depth 6 | ForEach-Object { $null = Write-ReportText -Path $rep -Text $_ }
    Write-Host ("`n   report -> {0}" -f $rep)
    Write-Host '=================================================================='
    return
}

# ---------------------------------------------------------------- catalogue
$catalog = @(Get-SystemCacheCatalog -Profile $Profile -TempAgeDays $TempAgeDays `
    -IncludeGpuShader:$IncludeGpuShader -IncludeD3DSCache:$IncludeD3DSCache `
    -Discover:($Profile -eq 'aggressive') -DiscoverMinMB $DiscoverMinMB -DiscoverTop $DiscoverTop)

# -Only / -Skip operate on entry ids and support wildcards
if ($Only.Count -gt 0) {
    $sel = New-Object System.Collections.ArrayList
    foreach ($e in $catalog) {
        foreach ($p in $Only) { if ($e.Id -like $p -or $e.Group -like $p) { [void]$sel.Add($e); break } }
    }
    $catalog = @($sel)
}
if ($Skip.Count -gt 0) {
    $catalog = @($catalog | Where-Object {
        $drop = $false
        foreach ($p in $Skip) { if ($_.Id -like $p -or $_.Group -like $p) { $drop = $true; break } }
        -not $drop
    })
}

# de-duplicate by resolved path: %TEMP% is often the 8.3 alias of the same
# directory as %LOCALAPPDATA%\Temp, and it must not be cleaned twice
$seen = @{}
$dedup = New-Object System.Collections.ArrayList
foreach ($e in $catalog) {
    $key = $e.Path
    $item = Get-Item -LiteralPath $e.Path -Force -ErrorAction SilentlyContinue
    if ($item) { $key = $item.FullName }
    $key = $key.TrimEnd('\').ToLower()
    if ($seen.ContainsKey($key)) { continue }
    $seen[$key] = $true
    [void]$dedup.Add($e)
}
$catalog = @($dedup)

if ($ListCatalog) {
    Write-Host (" catalogue: {0} target(s) for profile '{1}'" -f $catalog.Count, $Profile)
    Write-Host ''
    Write-Host '   ID                                    RISK        MODE      AGE  PATH'
    Write-Host '   ------------------------------------  ----------  --------  ---  ----'
    foreach ($e in $catalog) {
        $adm = ''
        if ($e.RequiresAdmin) { $adm = ' [admin]' }
        Write-Host ("   {0,-36}  {1,-10}  {2,-8}  {3,3}d  {4}{5}" -f `
            $e.Id, $e.Risk, $e.Mode, $e.MinAgeDays, $e.Path, $adm)
    }
    Write-Host ''
    Write-Host ("   {0} of them need Administrator; {1} are 'safe', {2} 'moderate', {3} 'aggressive'." -f `
        @($catalog | Where-Object { $_.RequiresAdmin }).Count,
        @($catalog | Where-Object { $_.Risk -eq 'safe' }).Count,
        @($catalog | Where-Object { $_.Risk -eq 'moderate' }).Count,
        @($catalog | Where-Object { $_.Risk -eq 'aggressive' }).Count)

    # The window's "cleanup list" page shows a real table, so the catalogue is also
    # written as a report instead of being scraped out of stdout: a text scraper in the
    # GUI can silently drift from what the script actually targets, a report cannot.
    # Array values are joined on purpose - ConvertTo-Json turns a one-element array into
    # a bare string, which would make the consumer's column types inconsistent.
    $catRows = foreach ($e in $catalog) {
        New-Object psobject -Property ([ordered]@{
            Id            = $e.Id
            Group         = $e.Group
            Risk          = $e.Risk
            Mode          = $e.Mode
            MinAgeDays    = $e.MinAgeDays
            RequiresAdmin = [bool]$e.RequiresAdmin
            Regenerates   = [bool]$e.Regenerates
            ServiceStop   = ($e.ServiceStop -join ',')
            Include       = ($e.Include -join ' ')
            Path          = $e.Path
            Note          = $e.Note
        })
    }
    $catRep = Join-Path (New-ReportDir) 'catalog.json'
    [ordered]@{
        Title      = ("System cache cleanup catalogue (profile={0})" -f $Profile)
        Time       = (Get-Date).ToString('o')
        Mode       = 'read-only catalogue'
        Profile    = $Profile
        Total      = $catalog.Count
        AdminCount = @($catalog | Where-Object { $_.RequiresAdmin }).Count
        SafeCount  = @($catalog | Where-Object { $_.Risk -eq 'safe' }).Count
        ModCount   = @($catalog | Where-Object { $_.Risk -eq 'moderate' }).Count
        AggrCount  = @($catalog | Where-Object { $_.Risk -eq 'aggressive' }).Count
        Targets    = @($catRows)
    } | ConvertTo-Json -Depth 6 | ForEach-Object { $null = Write-ReportText -Path $catRep -Text $_ }
    Write-Host ("   report -> {0}" -f $catRep)
    Write-Host '=================================================================='
    return
}

# ---------------------------------------------------------------- run
$rows = New-Object System.Collections.ArrayList
$guard = $null
if ($Execute -and -not $NoGuard) { $guard = Start-NetGainGuard -Drive 'C:' }

function Resolve-EntryTarget {
    param($Entry)
    if ($Entry.Mode -eq 'file') { Add-AllowedFile @($Entry.Path) }
    else { Add-AllowedRoot @($Entry.Path) }
}

$script:stopped = @()
function Stop-EntryServices {
    param([string[]]$Names)
    if (-not $Execute) { return }
    $names = @($Names | Where-Object { $_ })
    if (($names -join ',') -eq ($script:stopped -join ',')) { return }
    foreach ($s in $script:stopped) {
        try { Start-Service -Name $s -ErrorAction Stop } catch { Write-Warn2 ("could not restart service '{0}'" -f $s) }
    }
    $script:stopped = @()
    foreach ($s in $names) {
        try { Stop-Service -Name $s -Force -ErrorAction Stop; $script:stopped += $s }
        catch { Write-Warn2 ("could not stop service '{0}' - locked items will be skipped" -f $s) }
    }
    if ($script:stopped.Count -gt 0) { Start-Sleep -Seconds 2 }
}

try {
    $currentGroup = ''
    foreach ($entry in $catalog) {
        if ($entry.Group -ne $currentGroup) {
            $currentGroup = $entry.Group
            Write-Step ("GROUP: {0} (risk={1})" -f $currentGroup, ($catalog | Where-Object { $_.Group -eq $currentGroup } | Select-Object -First 1).Risk)
        }

        if (-not (Test-Path -LiteralPath $entry.Path)) {
            Write-Host ("   {0,10}       (absent)  {1}  [{2}]" -f '-', $entry.Path, $entry.Id)
            continue
        }
        if ($entry.RequiresAdmin -and $Execute -and -not $admin) {
            Write-Warn2 ("skipped (needs Administrator): {0}" -f $entry.Path)
            continue
        }

        Resolve-EntryTarget -Entry $entry
        Stop-EntryServices -Names $entry.ServiceStop

        $r = Remove-TargetGuarded -Path $entry.Path -Include $entry.Include -Exclude $entry.Exclude `
            -Mode $entry.Mode -MinAgeDays $entry.MinAgeDays -Execute:$Execute -Label $entry.Id
        [void]$rows.Add($r)

        if (-not $r.Exists) { continue }
        if ($r.SelectedItems -eq 0) {
            Write-Host ("   {0,10}       nothing selected  {1}  [{2}]" -f '-', $entry.Path, $entry.Id)
        } elseif (-not $Execute) {
            # The dry run now predicts lock contention as well as raw size, so the
            # number shown is close to what a real run will actually release.
            $prob = $r.FreedMB
            if (@($r.PSObject.Properties.Name) -contains 'EstimatedMB') { $prob = $r.EstimatedMB }
            $lockNote = ''
            if ($r.LockedPredicted -gt 0) {
                $lockNote = ("  ({0} of {1} selected item(s) are held open and will not release space)" -f `
                    $r.LockedPredicted, $r.SelectedItems)
            }
            Write-Host ("   {0,10:N1} MB  probably free of {1:N1} MB selected  {2}  [{3}]{4}" -f `
                $prob, $r.SelectedMB, $entry.Path, $entry.Id, $lockNote)
        } else {
            $extra = ''
            if ($r.LinkedSkipped -gt 0) { $extra += (" hardlink-skipped={0}" -f $r.LinkedSkipped) }
            if ($r.Locked -gt 0)       { $extra += (" locked={0}" -f $r.Locked) }
            if ($r.PrunedDirs -gt 0)   { $extra += (" pruned-dirs={0}" -f $r.PrunedDirs) }
            Write-Host ("   {0,8:N1} MB net  {1,8:N1} MB logical  {2,5} removed  {3}  [{4}]{5}" -f `
                $r.NetFreeMB, $r.FreedMB, $r.ItemsGone, $entry.Path, $entry.Id, $extra)
            if ($r.Hint) { Write-Warn2 ("      {0}" -f $r.Hint) }
        }

        # the guarantee: one negative step and the run stops
        if ($Execute -and $guard) {
            $regressed = Step-NetGainGuard -Label $entry.Id -AbortIfNegative:(-not $NoAbortOnRegression) `
                                           -ToleranceMB $RegressionToleranceMB -RemovedItems $r.ItemsGone
            if ($regressed) {
                Write-Warn2 ("free space fell during '{0}' - look for the row above with the negative net figure." -f $entry.Id)
            }
            if ($guard.Aborted) {
                Write-Err2 'ABORTING: free space went backwards. No further target will be touched.'
                break
            }
        }
    }
} finally {
    foreach ($s in $script:stopped) {
        try { Start-Service -Name $s -ErrorAction Stop; Write-Ok ("service restarted: {0}" -f $s) }
        catch { Write-Warn2 ("could not restart service '{0}'" -f $s) }
    }
}

# ---------------------------------------------------------------- extras
# Once the guarantee has fired, the optional extra steps are skipped too.
$guardTripped = ($guard -and $guard.Aborted)
if ($guardTripped) { Write-Warn2 'net-gain guard is tripped - optional extra steps are skipped.' }

if ($RestartExplorer -and $Execute -and -not $guardTripped) {
    Write-Step 'THUMBNAIL / ICON CACHE (explorer restarted to release the files)'
    Write-Warn2 'stopping explorer.exe so thumbcache_*.db can be replaced'
    $explorerStopped = $false
    try { Stop-Process -Name explorer -Force -ErrorAction Stop; $explorerStopped = $true; Start-Sleep -Seconds 3 } catch {}
    try {
        $la = $env:LOCALAPPDATA
        $thumbs = Join-Path $la 'Microsoft\Windows\Explorer'
        Add-AllowedRoot @($thumbs)
        $r = Remove-TargetGuarded -Path $thumbs -Include @('thumbcache_*.db', 'iconcache_*.db') `
            -Mode 'files' -Execute -Label 'thumbnails-unlocked'
        [void]$rows.Add($r)
        Write-Host ("   {0,8:N1} MB net  {1} removed  {2}" -f $r.NetFreeMB, $r.ItemsGone, $thumbs)
        if ($guard) { [void](Step-NetGainGuard -Label 'thumbnails-unlocked' -AbortIfNegative:(-not $NoAbortOnRegression) -ToleranceMB $RegressionToleranceMB) }
    } finally {
        if ($explorerStopped) { try { Start-Process 'explorer.exe' } catch {} }
    }
}

if ($IncludeRecycleBin -and -not $guardTripped) {
    Write-Step 'RECYCLE BIN'
    if ($Execute) {
        $b = Get-FreeBytes
        try {
            Clear-RecycleBin -Force -ErrorAction Stop
            Start-Sleep -Milliseconds 800
            $a = Get-FreeBytes
            Write-Host ("   {0,8:N1} MB net  recycle bin cleared" -f (($a - $b) / 1MB))
            [void]$rows.Add((New-Object psobject -Property ([ordered]@{
                Path = 'C:\$Recycle.Bin'; Label = 'recyclebin'; Mode = 'api'; Exists = $true
                SelectedItems = 0; SelectedMB = 0; BeforeMB = 0; AfterMB = 0; FreedMB = [math]::Round(($a - $b) / 1MB, 1)
                FreeBeforeMB = [math]::Round($b / 1MB, 1); FreeAfterMB = [math]::Round($a / 1MB, 1)
                NetFreeMB = [math]::Round(($a - $b) / 1MB, 1); ShadowGrowthMB = 0
                ItemsGone = 0; Locked = 0; LinkedSkipped = 0; HardlinkProbed = 0
                ReparseSkipped = 0; PrunedDirs = 0; Truncated = $false
                Regression = (($a - $b) -lt 0); Error = ''
            })))
            if ($guard) { [void](Step-NetGainGuard -Label 'recyclebin' -AbortIfNegative:(-not $NoAbortOnRegression) -ToleranceMB $RegressionToleranceMB) }
        } catch { Write-Warn2 ("recycle bin: {0}" -f $_.Exception.Message) }
    } else {
        Write-Host '   [dry] would run: Clear-RecycleBin -Force'
    }
}

if ($IncludeComponentStore -and -not $guardTripped) {
    Write-Step 'WINDOWS COMPONENT STORE (DISM StartComponentCleanup)'
    $dism = Get-SystemToolPath 'dism.exe'
    if (-not $dism) {
        Write-Warn2 'dism.exe not found - skipped'
    } elseif (-not $Execute) {
        Write-Host '   [dry] would run: dism /Online /Cleanup-Image /StartComponentCleanup'
        Write-Host '   this supersedes installed updates and can take 10+ minutes; measured net gain is reported.'
    } elseif (-not $admin) {
        Write-Warn2 'needs Administrator - skipped'
    } else {
        Write-Host '   running DISM (this can take a long time)...'
        $b = Get-FreeBytes
        & $dism /Online /Cleanup-Image /StartComponentCleanup 2>&1 | ForEach-Object { Write-Host ("      {0}" -f $_) }
        Start-Sleep -Seconds 2
        $a = Get-FreeBytes
        Write-Host ("   {0,8:N1} MB net  component store cleanup" -f (($a - $b) / 1MB))
        if ($guard) { [void](Step-NetGainGuard -Label 'component-store' -AbortIfNegative:(-not $NoAbortOnRegression) -ToleranceMB $RegressionToleranceMB) }
    }
}

if ($ShadowTrim) {
    Write-Step 'RECLAIMING SHADOW COPY STORAGE'
    Write-Warn2 'this DELETES old restore points (their old data is what fills the diff area).'
    Write-Warn2 'The newest restore point is kept; the rest are dropped oldest-first.'
    $vss = Get-SystemToolPath 'vssadmin.exe'
    $info = Get-ShadowStorageInfo
    if (-not $info.Ok) {
        Write-Warn2 'shadow storage state unknown - skipped'
    } elseif ($info.UsedBytes -le ($ShadowTargetGB * 1GB)) {
        Write-Ok ("already below the {0} GB target ({1:N2} GB used) - nothing to do" -f $ShadowTargetGB, ($info.UsedBytes / 1GB))
    } elseif (-not $Execute) {
        Write-Host ("   [dry] would delete old restore points until used <= {0} GB (now {1:N2} GB, {2} point(s))" -f `
            $ShadowTargetGB, ($info.UsedBytes / 1GB), $info.Copies)
    } elseif (-not $admin) {
        Write-Warn2 'needs Administrator - skipped'
    } else {
        $b = Get-FreeBytes
        $round = 0
        while ($round -lt 20) {
            $round++
            $cur = Get-ShadowStorageInfo
            if (-not $cur.Ok -or $cur.UsedBytes -le ($ShadowTargetGB * 1GB)) { break }
            Write-Host ("   dropping oldest restore point (used {0:N2} GB)..." -f ($cur.UsedBytes / 1GB))
            $out = & $vss delete shadows /for=C: /oldest /quiet 2>&1 | Out-String
            if ($out -and $out.Trim().Length -gt 0) { Write-Host ("      {0}" -f $out.Trim()) }
            Start-Sleep -Seconds 2
        }
        $a = Get-FreeBytes
        Write-Host ("   {0,8:N1} MB net  shadow storage reclaimed" -f (($a - $b) / 1MB))
        if ($guard) { [void](Step-NetGainGuard -Label 'shadow-trim' -AbortIfNegative:$false) }
    }
}

# ---------------------------------------------------------------- summary
$freeAfter = Get-FreeGB 'C:'
$ng = $null
if ($guard) { $ng = Stop-NetGainGuard }

$title = ("02-Clean-System  ({0}, profile={1})" -f $mode, $Profile)
[void](Show-Summary -Rows $rows -FreeBefore $freeBefore -FreeAfter $freeAfter `
    -Title $title -Tag 'system' -DryRun:(-not $Execute) -NetGuard $ng)

if (-not $Execute) {
    Write-Warn2 'This was a DRY RUN. Re-run with -Execute to apply.'
    Write-Host  '  "probably free" excludes files another process holds open: those can be'
    Write-Host  '  unlinked but keep their clusters until the handle closes (delete-pending),'
    Write-Host  '  so they would not return space. Net free space is measured on a real run.'
} elseif ($ng) {
    if ($ng.Verdict -eq 'GAIN') {
        Write-Ok ("GUARANTEE HELD: C: used space shrank by {0:N1} MB (free {1} GB -> {2} GB)." -f `
            (-1 * $ng.NetUsedMB), $ng.FreeStartGB, $ng.FreeEndGB)
    } elseif ($ng.Verdict -eq 'NEUTRAL') {
        Write-Warn2 ("C: free space moved {0:N1} MB - inside this volume's noise floor (about {1:N0} MB)." -f `
            $ng.NetFreeMB, $ng.NoiseFloorMB)
        Write-Warn2 'Defender, telemetry and browsers move that much on their own, so this run'
        Write-Warn2 'freed nothing measurable. Check the "removed / locked" columns above.'
    } else {
        Write-Err2 ("GUARANTEE BROKEN: C: used space GREW by {0:N1} MB (noise floor ~{1:N0} MB)." -f `
            $ng.NetUsedMB, $ng.NoiseFloorMB)
        if ($ng.Aborted) {
            Write-Err2 ('The guard stopped work on the first target that went backwards: ' + $ng.AbortReason)
        } else {
            Write-Err2 'Some target freed less than background writers took; no single step was'
            Write-Err2 'attributable to the deletions, so work was not stopped.'
        }
        Write-Err2 'Run 08-Diagnose-SpaceGrowth.ps1 to attribute it (shadow copies vs. concurrent writers).'
    }
}
