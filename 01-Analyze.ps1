#requires -Version 5.1
<#
  01-Analyze.ps1  --  READ-ONLY disk usage analysis for C: drive cleanup.
  Deletes NOTHING. Safe to run at any time.
  Output: console table + reports\before.json (or reports\after.json with -Tag after).
#>
[CmdletBinding()]
param(
    [string]$Tag = 'before',
    [int]$TopDirs = 20
)

$ErrorActionPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$Root     = $PSScriptRoot
$RepDir   = Join-Path $Root 'reports'
if (-not (Test-Path -LiteralPath $RepDir)) { New-Item -ItemType Directory -Path $RepDir -Force | Out-Null }
$RepFile  = Join-Path $RepDir ("{0}.json" -f $Tag)

# ---------------------------------------------------------------- helpers
function Get-DirSize {
    param([string]$Path)
    $o = New-Object psobject -Property ([ordered]@{
        Path = $Path; Exists = $false; MB = 0; Files = 0
    })
    if ([string]::IsNullOrWhiteSpace($Path)) { return $o }
    if (-not (Test-Path -LiteralPath $Path)) { return $o }
    $sum = [int64]0; $count = 0
    foreach ($f in (Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue)) {
        $sum += $f.Length; $count++
    }
    $o.Exists = $true
    $o.MB     = [math]::Round($sum / 1MB, 1)
    $o.Files  = $count
    return $o
}

function Get-DiskInfo {
    $d = Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' -ErrorAction SilentlyContinue
    foreach ($v in $d) {
        New-Object psobject -Property ([ordered]@{
            Drive   = $v.DeviceID
            TotalGB = [math]::Round($v.Size / 1GB, 2)
            FreeGB  = [math]::Round($v.FreeSpace / 1GB, 2)
        })
    }
}

# ---------------------------------------------------------------- targets
$U  = $env:USERPROFILE
$LA = $env:LOCALAPPDATA
$RA = $env:APPDATA
$PD = $env:ProgramData

$targets = [ordered]@{
    'SYSTEM / TEMP' = @(
        $env:TEMP
        'C:\Windows\Temp'
        (Join-Path $LA 'Temp')
    )
    'SYSTEM / UPDATE' = @(
        'C:\Windows\SoftwareDistribution\Download'
        'C:\Windows\SoftwareDistribution\DeliveryOptimization'
        (Join-Path $PD 'Microsoft\Network\Downloader')
        'C:\Windows\Logs\CBS'
    )
    'SYSTEM / PREFETCH-CACHE' = @(
        'C:\Windows\Prefetch'
        (Join-Path $LA 'Microsoft\Windows\Explorer')
        (Join-Path $LA 'Microsoft\Windows\INetCache')
        (Join-Path $LA 'Microsoft\Windows\WebCache')
    )
    'SYSTEM / DUMP-REPORTS' = @(
        'C:\Windows\Minidump'
        (Join-Path $LA 'CrashDumps')
        (Join-Path $PD 'Microsoft\Windows\WER')
        'C:\Windows\LiveKernelReports'
    )
    'SYSTEM / OTHER' = @(
        (Join-Path $env:SystemRoot 'Installer')
        'C:\Windows.old'
        'C:\$Recycle.Bin'
    )
    'DEV / NODE' = @(
        (Join-Path $LA 'npm-cache')
        (Join-Path $U  '.npm')
        (Join-Path $LA 'pnpm')
        (Join-Path $LA 'pnpm-store')
        (Join-Path $U  '.pnpm-store')
        (Join-Path $U  '.yarn')
        (Join-Path $LA 'Yarn\Cache')
        (Join-Path $U  '.bun\install\cache')
    )
    'DEV / PYTHON' = @(
        (Join-Path $LA 'pip\Cache')
        (Join-Path $U  '.cache\pip')
        (Join-Path $U  'AppData\Local\conda\conda\pkgs')
        (Join-Path $U  '.conda\pkgs')
        (Join-Path $U  '.cache\huggingface')
        (Join-Path $U  '.cache\torch')
    )
    'DEV / JVM-OTHER' = @(
        (Join-Path $U '.gradle\caches')
        (Join-Path $U '.m2\repository')
        (Join-Path $U '.nuget\packages')
        (Join-Path $U '.cargo\registry')
        (Join-Path $U '.rustup\toolchains')
        (Join-Path $U 'go\pkg\mod')
        (Join-Path $U '.docker')
    )
    'DEV / EDITOR' = @(
        (Join-Path $U '.vscode\extensions')
        (Join-Path $U '.cursor\extensions')
        (Join-Path $LA 'Microsoft\VSApplicationInsights')
        (Join-Path $RA 'Code\Cache')
        (Join-Path $RA 'Code\CachedData')
        (Join-Path $RA 'Code\logs')
        (Join-Path $RA 'Cursor\Cache')
    )
    'AGENT / HARNESS' = @(
        (Join-Path $U '.dsh\sessions')
        (Join-Path $U '.dsh\storages')
        (Join-Path $U '.dsh\logs')
        (Join-Path $U '.dsh\profiles')
        (Join-Path $U '.cache')
        (Join-Path $U '.claude')
        (Join-Path $U '.codex')
    )
    'BROWSER' = @(
        (Join-Path $LA 'Google\Chrome\User Data\Default\Cache')
        (Join-Path $LA 'Google\Chrome\User Data\Default\Code Cache')
        (Join-Path $LA 'Microsoft\Edge\User Data\Default\Cache')
        (Join-Path $LA 'Microsoft\Edge\User Data\Default\Code Cache')
    )
}

# ---------------------------------------------------------------- run
$diskBefore = @(Get-DiskInfo)
$rows       = New-Object System.Collections.ArrayList

Write-Output '=================================================================='
Write-Output (" C: CLEANUP ANALYSIS  (tag={0})" -f $Tag)
Write-Output (" Host={0}  Time={1}" -f $env:COMPUTERNAME, (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Write-Output '=================================================================='
foreach ($d in $diskBefore) {
    Write-Output (" {0}  total={1,8} GB   free={2,8} GB   used={3,5}%" -f `
        $d.Drive, $d.TotalGB, $d.FreeGB, [math]::Round(100 - ($d.FreeGB / $d.TotalGB * 100), 1))
}
Write-Output ''

$grand = [int64]0
foreach ($cat in $targets.Keys) {
    Write-Output ("--- {0}" -f $cat)
    $catSum = [int64]0
    foreach ($p in $targets[$cat]) {
        if ([string]::IsNullOrWhiteSpace($p)) { continue }
        $s = Get-DirSize -Path $p
        if (-not $s.Exists) {
            Write-Output ("      {0,10}  (missing)  {1}" -f '-', $p)
            continue
        }
        $catSum += [math]::Round($s.MB * 1MB)
        $grand  += [math]::Round($s.MB * 1MB)
        Write-Output ("      {0,10:N1} MB  {1,8} files  {2}" -f $s.MB, $s.Files, $p)
        [void]$rows.Add((New-Object psobject -Property ([ordered]@{
            Category = $cat; Path = $p; MB = $s.MB; Files = $s.Files
        })))
    }
    Write-Output ("      {0,10:N1} MB  == subtotal ==`n" -f ($catSum / 1MB))
}

# ------------------------------------------- catalogue-wide system junk sweep
# The fixed list above is the historical view. The catalogue is the authoritative
# inventory: ~50 audited targets for the 'standard' profile, including everything
# the fixed list misses (C:\Windows\SystemTemp, per-user WER, browser caches of
# every profile, font cache, WebCache, DataStore journals, telemetry staging).
Write-Output '--- CATALOGUE-WIDE SYSTEM JUNK  (exactly what 02-Clean-System would target)'
$catFile = Join-Path $Root '_SystemCatalog.ps1'
$catRows = New-Object System.Collections.ArrayList
if (Test-Path -LiteralPath $catFile) {
    . (Join-Path $Root '_Common.ps1')
    . $catFile
    $cat = @(Get-SystemCacheCatalog -Profile 'standard' -TempAgeDays 1)
    $groupSum = @{}
    foreach ($e in $cat) {
        if (-not (Test-Path -LiteralPath $e.Path)) { continue }
        $s = Get-DirSize -Path $e.Path
        if (-not $s.Exists) { continue }
        if (-not $groupSum.ContainsKey($e.Group)) { $groupSum[$e.Group] = [int64]0 }
        $groupSum[$e.Group] += [math]::Round($s.MB * 1MB)
        [void]$catRows.Add((New-Object psobject -Property ([ordered]@{
            Category = ('CATALOG / ' + $e.Group); Path = $e.Path; MB = $s.MB; Files = $s.Files
            Id = $e.Id; Risk = $e.Risk; Mode = $e.Mode
        })))
    }
    foreach ($g in ($groupSum.Keys | Sort-Object)) {
        Write-Output ("      {0,10:N1} MB  == {1} ==" -f ($groupSum[$g] / 1MB), $g)
    }
    $catTotal = ($groupSum.Values | Measure-Object -Sum).Sum
    Write-Output ("      {0,10:N1} MB  == CATALOGUE TOTAL ({1} target(s) present) ==" -f ($catTotal / 1MB), $catRows.Count)
    Write-Output '      (run 02-Clean-System.ps1 -ListCatalog for the full target list,'
    Write-Output '       or 02-Clean-System.ps1 -Discover to find unlisted cache directories)'
    foreach ($r in $catRows) {
        [void]$rows.Add($r)
        $grand += [math]::Round($r.MB * 1MB)
    }
} else {
    Write-Output '      (catalogue file missing - skipping this section)'
}
Write-Output ''

# ------------------------------------------------- top-level profile dirsWrite-Output ("--- TOP {0} DIRECTORIES UNDER USER PROFILE" -f $TopDirs)
$profileDirs = New-Object System.Collections.ArrayList
foreach ($d in (Get-ChildItem -LiteralPath $U -Directory -Force -ErrorAction SilentlyContinue)) {
    [void]$profileDirs.Add($d.FullName)
}
foreach ($sub in @($LA, $RA)) {
    foreach ($d in (Get-ChildItem -LiteralPath $sub -Directory -Force -ErrorAction SilentlyContinue)) {
        [void]$profileDirs.Add($d.FullName)
    }
}
$measured = foreach ($p in $profileDirs) {
    $s = Get-DirSize -Path $p
    New-Object psobject -Property ([ordered]@{ Path = $p; MB = $s.MB; Files = $s.Files })
}
$top = $measured | Sort-Object MB -Descending | Select-Object -First $TopDirs
foreach ($t in $top) {
    Write-Output ("      {0,10:N1} MB  {1,8} files  {2}" -f $t.MB, $t.Files, $t.Path)
}

# ------------------------------------------------- big single files on C:
Write-Output ''
Write-Output '--- LARGE FILES AT C:\ ROOT (paging / hibernation)'
foreach ($f in @('C:\hiberfil.sys', 'C:\pagefile.sys', 'C:\swapfile.sys')) {
    $i = Get-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
    if ($i) { Write-Output ("      {0,10:N1} MB  {1}" -f ($i.Length / 1MB), $f) }
}

# ------------------------------------------------- persist
$report = [ordered]@{
    Tag        = $Tag
    Time       = (Get-Date).ToString('o')
    Computer   = $env:COMPUTERNAME
    Disks      = $diskBefore
    Targets    = $rows
    TargetMB   = [math]::Round($grand / 1MB, 1)
    TopDirs    = @($top)
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $RepFile -Encoding UTF8

Write-Output ''
Write-Output '=================================================================='
Write-Output (" TOTAL measured cache footprint : {0:N1} MB ({1:N2} GB)" -f ($grand / 1MB), ($grand / 1GB))
Write-Output (" Report written to              : {0}" -f $RepFile)
Write-Output '=================================================================='
