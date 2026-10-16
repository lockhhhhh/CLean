#requires -Version 5.1
<#
  _SystemCatalog.ps1  --  the catalogue of system junk / cache targets, plus the
  discovery pass that finds cache directories the catalogue does not know about.

  WHY A CATALOGUE AND NOT A LIST OF DIRECTORIES
  The first version of this toolset knew 12 paths and deleted their whole
  contents. That is both too little (the big consumers were never listed) and
  too much (a whole-tree delete takes whatever happens to be there, including
  an application's only copy of something). Every entry here is therefore
  POINT-TO-POINT:

    Path      an exact directory (never a drive or a program folder)
    Include   the exact name patterns that may be deleted inside it
    Exclude   patterns that are never touched
    Mode      'children' = the matching direct children
              'files'    = the matching files at any depth, empty dirs pruned
    MinAgeDays  age guard (an item must be older than N days)
    Risk      safe | moderate | aggressive   -- selects what a profile runs
    ServiceStop  services to stop first so their cache is not locked/rewritten

  Nothing outside these entries can be deleted: every path still has to pass
  Test-SafeTarget's allowlist and denylist in _Common.ps1.
#>

# Project-cache directory names: each one belongs to a package manager or build tool
# and regenerates on the next install/build, so the whole directory may be removed.
#
# THIS FUNCTION IS THE ONLY PLACE THIS LIST EXISTS. Three copies used to live in the
# repo (04-Clean-Hidden.ps1, 05-Scan-LargeDirs.ps1 and the GUI's C# scanner) and they
# had already drifted: the GUI knew .tox but not .nuget, 05 knew .nuget but not .tox.
# 05's list is a different thing on purpose (it only LABELS rows, so it also covers
# system caches - see the comment there). The GUI's copy is generated from here by
# src\Build.ps1 at build time, so there is nothing left to keep in step by hand.
function Get-ProjectCacheNames {
    return @(
        'node_modules', '.cache', '.next', '.nuxt', '.turbo', '.parcel-cache',
        '.pytest_cache', '.mypy_cache', '.ruff_cache', '__pycache__',
        '.venv', 'venv', '.tox', '.svelte-kit', '.angular', 'bower_components'
    )
}

# --------------------------------------------------------------- name sets
<#
  Directory names that are, unambiguously, regenerable caches. Used by the
  discovery pass; deliberately excludes anything a user creates on purpose.
#>
function Get-CacheNameSets {
    return @{
        # leaf names whose whole contents are a cache -> delete contents, any age
        CacheNames = @(
            'Cache', 'Cache2', 'Code Cache', 'GPUCache', 'ShaderCache', 'GrShaderCache',
            'DawnCache', 'DawnGraphiteCache', 'DawnWebGPUCache', 'CachedData',
            'CacheStorage', 'ScriptCache', 'CachedProfilesData', 'Crashpad',
            'CrashDumps', 'Minidump', 'blob_storage_cache', 'GPUCacheData'
        )
        # leaf names that hold a mix of junk and real data -> delete only junk
        # extensions, and only past the age guard
        JunkDirNames = @('Temp', 'temp', 'tmp', 'Tmp', 'logs', 'Logs', 'log', 'CrashReports')
        JunkExtensions = @('*.log', '*.tmp', '*.temp', '*.old', '*.bak', '*.dmp',
                           '*.etl', '*.mdmp', '*.wer', '*.~*', '*.chk', '*.$$$')
    }
}

function Get-UserProfileDirs {
    $out = New-Object System.Collections.ArrayList
    $root = Join-Path $env:SystemDrive 'Users'
    if (-not (Test-Path -LiteralPath $root)) { return @() }
    foreach ($d in @(Get-ChildItem -LiteralPath $root -Directory -Force -ErrorAction SilentlyContinue)) {
        if (($d.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
        if ($d.Name -in @('Default', 'Default User', 'All Users', 'Public', 'WDAGUtilityAccount')) { continue }
        [void]$out.Add($d.FullName)
    }
    return @($out)
}

# --------------------------------------------------------------- entries
function New-CatalogEntry {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Group,
        [Parameter(Mandatory)][string]$Path,
        [string[]]$Include = @('*'),
        [string[]]$Exclude = @(),
        [ValidateSet('children', 'files', 'file')][string]$Mode = 'children',
        [int]$MinAgeDays = 0,
        [ValidateSet('safe', 'moderate', 'aggressive')][string]$Risk = 'safe',
        [switch]$RequiresAdmin,
        [switch]$Regenerates,
        [string[]]$ServiceStop = @(),
        [string]$Note = ''
    )
    return New-Object psobject -Property ([ordered]@{
        Id = $Id; Group = $Group; Path = $Path
        Include = $Include; Exclude = $Exclude; Mode = $Mode
        MinAgeDays = $MinAgeDays; Risk = $Risk
        RequiresAdmin = [bool]$RequiresAdmin; Regenerates = [bool]$Regenerates
        ServiceStop = $ServiceStop; Note = $Note
    })
}

<#
  The catalogue.

  Profiles
    safe        only entries whose contents are unambiguously junk
    standard    safe + moderate (browser caches, per-user temp of other accounts)
    aggressive  everything, including caches whose rebuild costs real time
#>
function Get-SystemCacheCatalog {
    param(
        [ValidateSet('safe', 'standard', 'aggressive')][string]$Profile = 'standard',
        [int]$TempAgeDays = 1,
        [switch]$IncludeGpuShader,
        [switch]$IncludeD3DSCache,
        [switch]$Discover,
        [int]$DiscoverMinMB = 20,
        [int]$DiscoverTop = 30
    )

    $LA = $env:LOCALAPPDATA
    $PD = $env:ProgramData
    $W  = $env:SystemRoot
    $entries = New-Object System.Collections.ArrayList

    # ---------------- TEMP / scratch ------------------------------------
    [void]$entries.Add((New-CatalogEntry -Id 'temp.user' -Group 'TEMP' -Path $env:TEMP `
        -Mode 'children' -MinAgeDays $TempAgeDays -Risk 'safe' -Regenerates `
        -Note 'this account''s %TEMP%; age-guarded so files in use are untouched'))
    [void]$entries.Add((New-CatalogEntry -Id 'temp.windows' -Group 'TEMP' -Path (Join-Path $W 'Temp') `
        -Mode 'children' -MinAgeDays $TempAgeDays -Risk 'safe' -Regenerates -RequiresAdmin))
    [void]$entries.Add((New-CatalogEntry -Id 'temp.system' -Group 'TEMP' -Path (Join-Path $W 'SystemTemp') `
        -Mode 'children' -MinAgeDays $TempAgeDays -Risk 'safe' -Regenerates -RequiresAdmin `
        -Note 'Windows 11 system scratch; absent on Windows 10'))

    # other accounts' temp: moderate, because it is somebody else's session
    foreach ($u in (Get-UserProfileDirs)) {
        if ($u -ieq $env:USERPROFILE) { continue }
        [void]$entries.Add((New-CatalogEntry -Id ('temp.user.' + (Split-Path $u -Leaf)) -Group 'TEMP' `
            -Path (Join-Path $u 'AppData\Local\Temp') -Mode 'children' -MinAgeDays $TempAgeDays `
            -Risk 'moderate' -Regenerates -RequiresAdmin))
    }

    # ---------------- WINDOWS UPDATE / DELIVERY OPTIMIZATION ------------
    $wu = Join-Path $W 'SoftwareDistribution'
    [void]$entries.Add((New-CatalogEntry -Id 'wu.download' -Group 'UPDATE' -Path (Join-Path $wu 'Download') `
        -Mode 'children' -Risk 'safe' -Regenerates -RequiresAdmin -ServiceStop @('wuauserv', 'bits') `
        -Note 'downloaded update payloads already installed; re-downloaded only if the update is still needed'))
    [void]$entries.Add((New-CatalogEntry -Id 'wu.datastore.logs' -Group 'UPDATE' -Path (Join-Path $wu 'DataStore\Logs') `
        -Mode 'files' -Include @('*.edb', '*.jrs', '*.log', '*.chk') -Risk 'moderate' -RequiresAdmin `
        -ServiceStop @('wuauserv', 'bits') -Note 'update database journals only - the database itself is kept'))
    [void]$entries.Add((New-CatalogEntry -Id 'wu.postreboot' -Group 'UPDATE' -Path (Join-Path $wu 'PostRebootEventCache.V2') `
        -Mode 'children' -Risk 'moderate' -Regenerates -RequiresAdmin -ServiceStop @('wuauserv') `
        -Note 'scheduled-task cache rebuilt after the next update check'))
    [void]$entries.Add((New-CatalogEntry -Id 'wu.selfupdate' -Group 'UPDATE' -Path (Join-Path $wu 'SelfUpdate') `
        -Mode 'children' -MinAgeDays 7 -Risk 'aggressive' -Regenerates -RequiresAdmin -ServiceStop @('wuauserv') `
        -Note 'Windows Update self-update packages older than 7 days'))
    [void]$entries.Add((New-CatalogEntry -Id 'bits.downloader' -Group 'UPDATE' `
        -Path (Join-Path $PD 'Microsoft\Network\Downloader') `
        -Mode 'files' -Include @('qmgr*.dat', 'qmgr*.dat.new', 'qmgr*.bak') -Risk 'moderate' `
        -RequiresAdmin -ServiceStop @('bits', 'wuauserv') -Note 'BITS job database; rebuilt empty'))

    $do = Join-Path $W 'ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization'
    [void]$entries.Add((New-CatalogEntry -Id 'do.cache' -Group 'UPDATE' -Path (Join-Path $do 'Cache') `
        -Mode 'children' -Risk 'safe' -Regenerates -RequiresAdmin -ServiceStop @('DoSvc') `
        -Note 'P2P update blobs shared with other PCs'))
    [void]$entries.Add((New-CatalogEntry -Id 'do.logs' -Group 'UPDATE' -Path (Join-Path $do 'Logs') `
        -Mode 'children' -Risk 'safe' -Regenerates -RequiresAdmin -ServiceStop @('DoSvc')))
    foreach ($u in @($env:USERPROFILE) + (Get-UserProfileDirs)) {
        $ud = Join-Path $u 'AppData\Local\Microsoft\Windows\DeliveryOptimization'
        [void]$entries.Add((New-CatalogEntry -Id ('do.user.' + (Split-Path $u -Leaf)) -Group 'UPDATE' `
            -Path $ud -Mode 'files' -Include @('*.tmp', '*.etl', '*.log', '*.bin') -MinAgeDays 1 `
            -Risk 'moderate' -Regenerates -ServiceStop @('DoSvc')))
    }

    # ---------------- LOGS / diagnostics --------------------------------
    [void]$entries.Add((New-CatalogEntry -Id 'logs.windows' -Group 'LOGS' -Path (Join-Path $W 'Logs') `
        -Mode 'files' -Include @('*.log', '*.etl', '*.cab', '*.txt', '*.old', '*.bak') `
        -MinAgeDays 1 -Risk 'safe' -RequiresAdmin `
        -Note 'CBS / DISM / MoSetup / SIH / WindowsUpdate / NetSetup logs, one sweep, exact extensions'))
    [void]$entries.Add((New-CatalogEntry -Id 'logs.panther' -Group 'LOGS' -Path (Join-Path $W 'Panther') `
        -Mode 'files' -Include @('*.log', '*.etl') -MinAgeDays 7 -Risk 'moderate' -RequiresAdmin `
        -Note 'setup logs older than a week; the newest failures stay for diagnosis'))
    # NOTE: C:\Windows\System32\LogFiles is deliberately NOT a target. The hard
    # denylist in _Common.ps1 refuses everything under System32, and that floor
    # is worth more than the few MB of WMI/HTTP trace files it costs.
    [void]$entries.Add((New-CatalogEntry -Id 'logs.telemetry' -Group 'LOGS' -Path (Join-Path $PD 'Microsoft\Diagnosis') `
        -Mode 'files' -Include @('*.etl', '*.cab', '*.rbs', '*.tmp') -MinAgeDays 7 -Risk 'moderate' -RequiresAdmin `
        -Note 'telemetry staging files; locked ones are skipped'))

    # ---------------- DUMPS / ERROR REPORTING ---------------------------
    # MEMORY.DMP is a single named file: registered as an exact-file target so
    # that C:\Windows itself never becomes a deletable root.
    [void]$entries.Add((New-CatalogEntry -Id 'dump.memory' -Group 'DUMPS' -Path (Join-Path $W 'MEMORY.DMP') `
        -Mode 'file' -Risk 'safe' -RequiresAdmin `
        -Note 'kernel crash dump (single exact file, not a directory sweep)'))
    [void]$entries.Add((New-CatalogEntry -Id 'dump.minidump' -Group 'DUMPS' -Path (Join-Path $W 'Minidump') `
        -Mode 'children' -Include @('*.dmp') -Risk 'safe' -RequiresAdmin))
    [void]$entries.Add((New-CatalogEntry -Id 'dump.livekernel' -Group 'DUMPS' -Path (Join-Path $W 'LiveKernelReports') `
        -Mode 'children' -Risk 'safe' -RequiresAdmin -Note 'live kernel dumps - historically multi-GB here'))
    [void]$entries.Add((New-CatalogEntry -Id 'dump.user' -Group 'DUMPS' -Path (Join-Path $LA 'CrashDumps') `
        -Mode 'children' -Include @('*.dmp') -Risk 'safe'))
    foreach ($wer in @('ReportArchive', 'ReportQueue', 'Temp', 'ERC')) {
        [void]$entries.Add((New-CatalogEntry -Id ('wer.pd.' + $wer.ToLower()) -Group 'DUMPS' `
            -Path (Join-Path $PD ("Microsoft\Windows\WER\{0}" -f $wer)) -Mode 'children' `
            -Risk 'safe' -RequiresAdmin -Regenerates))
    }
    foreach ($wer in @('ReportArchive', 'ReportQueue')) {
        [void]$entries.Add((New-CatalogEntry -Id ('wer.user.' + $wer.ToLower()) -Group 'DUMPS' `
            -Path (Join-Path $LA ("Microsoft\Windows\WER\{0}" -f $wer)) -Mode 'children' -Risk 'safe'))
    }

    # ---------------- SHELL / web cache ---------------------------------
    [void]$entries.Add((New-CatalogEntry -Id 'shell.thumbnails' -Group 'SHELL' `
        -Path (Join-Path $LA 'Microsoft\Windows\Explorer') `
        -Mode 'files' -Include @('thumbcache_*.db', 'iconcache_*.db') -Risk 'safe' -Regenerates `
        -Note 'held open by explorer.exe - locked files are skipped, use -RestartExplorer to unlock'))
    [void]$entries.Add((New-CatalogEntry -Id 'shell.inetcache' -Group 'SHELL' `
        -Path (Join-Path $LA 'Microsoft\Windows\INetCache') -Mode 'files' -MinAgeDays 1 `
        -Risk 'safe' -Regenerates))
    [void]$entries.Add((New-CatalogEntry -Id 'shell.webcache' -Group 'SHELL' `
        -Path (Join-Path $LA 'Microsoft\Windows\WebCache') `
        -Mode 'files' -Include @('*.dat', '*.jrs', '*.log', '*.edb') -MinAgeDays 1 `
        -Risk 'moderate' -Regenerates -Note 'WinINet history database - locked while apps use it'))
    [void]$entries.Add((New-CatalogEntry -Id 'shell.caches' -Group 'SHELL' `
        -Path (Join-Path $LA 'Microsoft\Windows\Caches') -Mode 'files' -Risk 'moderate' -Regenerates))
    [void]$entries.Add((New-CatalogEntry -Id 'shell.fontcache' -Group 'SHELL' `
        -Path (Join-Path $W 'ServiceProfiles\LocalService\AppData\Local\FontCache') `
        -Mode 'files' -Include @('*.dat') -Risk 'moderate' -RequiresAdmin -Regenerates `
        -ServiceStop @('FontCache') -Note 'rebuilt on demand; requires the FontCache service stopped'))
    [void]$entries.Add((New-CatalogEntry -Id 'shell.searchindex' -Group 'SHELL' `
        -Path (Join-Path $PD 'Microsoft\Search\Data\Applications\Windows') `
        -Mode 'files' -Include @('*.edb', '*.jfm', '*.log') -Risk 'aggressive' -RequiresAdmin `
        -Regenerates -ServiceStop @('WSearch') `
        -Note 'Windows Search index: big win, but a full re-index runs afterwards'))

    # ---------------- STORE APPS / packaged software ---------------------
    foreach ($u in @($env:USERPROFILE) + (Get-UserProfileDirs)) {
        $pkgRoot = Join-Path $u 'AppData\Local\Packages'
        if (-not (Test-Path -LiteralPath $pkgRoot)) { continue }
        foreach ($p in @(Get-ChildItem -LiteralPath $pkgRoot -Directory -Force -ErrorAction SilentlyContinue)) {
            if (($p.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            $tags = @(
                @{ Sub = 'LocalCache\Local\Microsoft\Windows\INetCache'; Name = 'inetcache' },
                @{ Sub = 'LocalCache\Local\Temp';                          Name = 'temp' },
                @{ Sub = 'LocalCache\Roaming\Microsoft\Windows\Recent';    Name = 'recent' }
            )
            foreach ($t in $tags) {
                $pth = Join-Path $p.FullName $t.Sub
                if (-not (Test-Path -LiteralPath $pth)) { continue }
                $mode = 'files'
                $inc  = @('*')
                $age  = 1
                if ($t.Name -ne 'temp') { $inc = @('*.tmp', '*.log', '*.etl', '*.dmp') }
                [void]$entries.Add((New-CatalogEntry -Id ('store.' + $t.Name + '.' + $p.Name) -Group 'STORE' `
                    -Path $pth -Mode $mode -Include $inc -MinAgeDays $age -Risk 'moderate' -Regenerates `
                    -Note 'per-app packaged cache'))
            }
        }
    }

    # ---------------- BROWSER caches ------------------------------------
    # Chromium-family caches live under <User Data>\<profile>\<cache kind>.
    $browsers = @(
        @{ Id = 'edge';    Root = (Join-Path $LA 'Microsoft\Edge\User Data') }
        @{ Id = 'chrome';  Root = (Join-Path $LA 'Google\Chrome\User Data') }
        @{ Id = 'brave';   Root = (Join-Path $LA 'BraveSoftware\Brave-Browser\User Data') }
        @{ Id = 'vivaldi'; Root = (Join-Path $LA 'Vivaldi\User Data') }
        @{ Id = 'qq';      Root = (Join-Path $LA 'Tencent\QQBrowser\User Data') }
        @{ Id = 'se360';   Root = (Join-Path $env:APPDATA '360se6\User Data') }
    )
    $cacheKinds = @(
        'Cache', 'Code Cache', 'GPUCache', 'ShaderCache', 'GrShaderCache',
        'DawnGraphiteCache', 'DawnWebGPUCache', 'Service Worker\CacheStorage',
        'Service Worker\ScriptCache', 'Crashpad\reports', 'component_crx_cache',
        'optimization_guide_hint_cache_store', 'Media Cache'
    )
    foreach ($b in $browsers) {
        if (-not (Test-Path -LiteralPath $b.Root)) { continue }
        $profiles = @(Get-ChildItem -LiteralPath $b.Root -Directory -Force -ErrorAction SilentlyContinue |
                      Where-Object { $_.Name -eq 'Default' -or $_.Name -like 'Profile *' -or $_.Name -like 'Guest Profile' })
        foreach ($pr in $profiles) {
            foreach ($kind in $cacheKinds) {
                $pth = Join-Path $pr.FullName $kind
                if (-not (Test-Path -LiteralPath $pth)) { continue }
                [void]$entries.Add((New-CatalogEntry -Id ("browser.{0}.{1}.{2}" -f $b.Id, $pr.Name.Replace(' ', ''), ($kind -replace '[^A-Za-z]', '')) `
                    -Group 'BROWSER' -Path $pth -Mode 'files' -MinAgeDays 0 -Risk 'moderate' -Regenerates `
                    -Note 'page / shader cache; browsing state (cookies, logins, history) is never touched'))
            }
        }
    }
    # Firefox keeps its cache one level deeper
    $ff = Join-Path $LA 'Mozilla\Firefox\Profiles'
    if (Test-Path -LiteralPath $ff) {
        foreach ($pr in @(Get-ChildItem -LiteralPath $ff -Directory -Force -ErrorAction SilentlyContinue)) {
            foreach ($kind in @('cache2', 'startupCache', 'shader-cache', 'thumbnails')) {
                $pth = Join-Path $pr.FullName $kind
                if (-not (Test-Path -LiteralPath $pth)) { continue }
                [void]$entries.Add((New-CatalogEntry -Id ("browser.firefox.{0}.{1}" -f $pr.Name, $kind) -Group 'BROWSER' `
                    -Path $pth -Mode 'files' -Risk 'moderate' -Regenerates))
            }
        }
    }

    # ---------------- GPU shader caches (opt-in) -------------------------
    if ($IncludeGpuShader) {
        $gpu = Get-GpuVendor
        if ($gpu.Vendors.Count -gt 0) {
            foreach ($p in (Get-GpuShaderCachePaths -Vendors $gpu.Vendors -IncludeVendorNeutral:$IncludeD3DSCache)) {
                [void]$entries.Add((New-CatalogEntry -Id ('gpu.' + ($p -replace '[\\: ]', '_')) -Group 'GPU' `
                    -Path $p -Mode 'files' -Risk 'safe' -Regenerates `
                    -Note 'compiled shaders for the detected GPU vendor; games recompile on next launch'))
            }
        }
    }

    # ---------------- DISCOVERED application caches ----------------------
    if ($Discover) {
        foreach ($d in (Get-DiscoveredCacheDirs -MinMB $DiscoverMinMB -Top $DiscoverTop)) {
            [void]$entries.Add((New-CatalogEntry -Id ('app.' + ($d.Path -replace '[\\: ]', '_')) -Group 'APP' `
                -Path $d.Path -Mode 'files' -Include $d.Include -MinAgeDays $d.MinAgeDays `
                -Risk 'moderate' -Regenerates -Note $d.Note))
        }
    }

    # filter by profile: a profile is a RISK CEILING, not a risk name
    $rank  = @{ 'safe' = 1; 'moderate' = 2; 'aggressive' = 3 }
    $limit = switch ($Profile) { 'safe' { 1 } 'standard' { 2 } 'aggressive' { 3 } default { 2 } }
    return @($entries | Where-Object { $rank[$_.Risk] -le $limit })
}

# --------------------------------------------------------------- discovery
<#
  Find cache directories the catalogue does not know about.

  Bounded on purpose: fixed roots, a depth limit, reparse points skipped, and a
  size threshold, because an unbounded walk of %APPDATA% is both slow and a good
  way to find things that must not be deleted. Only leaf names from the strict
  cache-name sets qualify, and log/temp-like names are restricted to junk
  extensions with an age guard.
#>
function Get-DiscoveredCacheDirs {
    param(
        [string[]]$Roots = @(),
        [int]$MaxDepth = 4,
        [int]$MinMB = 20,
        [int]$Top = 30,
        [int]$MaxMeasure = 300
    )
    if ($Roots.Count -eq 0) {
        $Roots = @($env:LOCALAPPDATA, $env:APPDATA, (Join-Path $env:ProgramData 'Microsoft'),
                   (Join-Path $env:USERPROFILE '.cache')) |
                 Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique
    }
    $sets    = Get-CacheNameSets
    $matches = New-Object System.Collections.ArrayList

    foreach ($root in $Roots) {
        $queue = New-Object System.Collections.Queue
        $queue.Enqueue(@{ Path = $root; Depth = 0 })
        while ($queue.Count -gt 0) {
            $cur = $queue.Dequeue()
            if ($cur.Depth -ge $MaxDepth) { continue }
            $di = $null
            try { $di = New-Object System.IO.DirectoryInfo($cur.Path) } catch { continue }
            $subs = $null
            try { $subs = $di.GetDirectories() } catch { continue }
            foreach ($s in $subs) {
                if (($s.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
                $nm = $s.Name
                $isCache = $sets.CacheNames -contains $nm
                $isJunk  = $sets.JunkDirNames -contains $nm
                if ($isCache -or $isJunk) {
                    [void]$matches.Add((New-Object psobject -Property ([ordered]@{
                        Path = $s.FullName; Name = $nm; IsCache = $isCache
                    })))
                    # a cache directory's own subdirectories are not separate targets
                    continue
                }
                $queue.Enqueue(@{ Path = $s.FullName; Depth = $cur.Depth + 1 })
            }
        }
    }

    $measured = New-Object System.Collections.ArrayList
    $count = 0
    foreach ($m in $matches) {
        if ($count -ge $MaxMeasure) { break }
        $count++
        $mb = Get-DirSizeMB -Path $m.Path
        if ($mb -lt $MinMB) { continue }
        $inc = @('*'); $age = 0; $note = 'cache directory (contents regenerate)'
        if (-not $m.IsCache) {
            $inc  = $sets.JunkExtensions
            $age  = 1
            $note = 'log / temp directory: junk extensions only, older than 1 day'
        }
        [void]$measured.Add((New-Object psobject -Property ([ordered]@{
            Path = $m.Path; MB = $mb; Include = $inc; MinAgeDays = $age; Note = $note
        })))
    }
    return @($measured | Sort-Object MB -Descending | Select-Object -First $Top)
}
