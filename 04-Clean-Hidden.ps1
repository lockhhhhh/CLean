#requires -Version 5.1
<#
  04-Clean-Hidden.ps1  --  find and (only on explicit request) remove hidden
  project-cache folders: node_modules, .cache, .next, __pycache__, venvs, and
  the harness session/storage trees.

  Three modes:
    SCAN  (default)   lists every match with its size. Deletes NOTHING.
    PLAN  (-Paths, no -Execute)
                      prints, per named path, whether it passes the guard and how
                      much it would free. Deletes NOTHING. This is phase 1 of the
                      GUI's two-phase flow.
    DELETE            only runs with -Execute AND an explicit -Paths list.

  Usage:
    .\04-Clean-Hidden.ps1                                  # scan Desktop + profile
    .\04-Clean-Hidden.ps1 -ScanRoots 'D:\work' -MinMB 50
    .\04-Clean-Hidden.ps1 -ScanRoots 'D:\work' -Paths 'D:\work\app\node_modules'
    .\04-Clean-Hidden.ps1 -ScanRoots 'D:\work' -Paths 'D:\work\app\node_modules' -Execute

  Nothing is ever deleted implicitly: DELETE mode requires the exact paths to be
  named on the command line, and each one must sit under a scanned root.
#>
[CmdletBinding()]
param(
    [string[]]$ScanRoots = @(),
    [int]     $MinMB     = 50,
    [int]     $Depth     = 5,
    [switch]  $Execute,
    [string[]]$Paths     = @()
)

. (Join-Path $PSScriptRoot '_Common.ps1')
. (Join-Path $PSScriptRoot '_SystemCatalog.ps1')

if ($ScanRoots.Count -eq 0) {
    $ScanRoots = @(
        [Environment]::GetFolderPath('Desktop'),
        $env:USERPROFILE
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
}

foreach ($r in $ScanRoots) { Add-AllowedRoot @($r) }

# Names that are unambiguously regenerable caches. Defined in _SystemCatalog.ps1, not
# here: this file, 05 and the GUI each carried a copy and they had drifted apart.
$cacheNames = @(Get-ProjectCacheNames)

$mode = 'SCAN (read-only)'
if ($Execute) { $mode = 'DELETE' }
elseif ($Paths.Count -gt 0) { $mode = 'PLAN (dry run)' }

Write-Host '=================================================================='
Write-Host (" 04-Clean-Hidden.ps1    mode={0}" -f $mode)
Write-Host (" roots: {0}" -f ($ScanRoots -join ' | '))
Write-Host '=================================================================='

# ------------------------------------------------- plan / delete named paths
<#
  Two-phase deployment of the same guard: naming the paths WITHOUT -Execute only
  measures them (and reports every path the allowlist/denylist refuses), and only
  -Execute removes anything. The GUI drives exactly this pair, so its confirmation
  strip can never delete something this script has not first listed.
#>
if ($Paths.Count -gt 0) {
    $freeBefore = Get-FreeGB 'C:'
    Write-Host (" C: free before : {0} GB" -f $freeBefore)
    if (-not $Execute) {
        Write-Host (" planning {0} path(s) - nothing will be deleted`n" -f $Paths.Count)
    } else {
        Write-Host ''
    }

    $rows = New-Object System.Collections.ArrayList
    $plannedMB = 0.0
    $refused = 0

    foreach ($p in $Paths) {
        if (-not (Test-SafeTarget -Path $p)) {
            Write-Err2 ("REFUSED (not under a scanned root / on the denylist): {0}" -f $p)
            $refused++
            continue
        }
        if (-not (Test-Path -LiteralPath $p)) {
            Write-Warn2 ("skip (not found): {0}" -f $p)
            continue
        }

        if (-not $Execute) {
            $mb = Get-DirSizeMB -Path $p
            $plannedMB += $mb
            Write-Host ("   would free {0,10:N1} MB   {1}" -f $mb, $p)
            [void]$rows.Add((New-Object psobject -Property ([ordered]@{
                Path = $p; Label = 'project-cache'; Mode = 'tree'; Exists = $true
                SelectedItems = 0; SelectedMB = $mb; BeforeMB = $mb; AfterMB = $mb; FreedMB = $mb
                FreeBeforeMB = 0; FreeAfterMB = 0; NetFreeMB = 0; ShadowGrowthMB = 0
                ItemsGone = 0; Locked = 0; LinkedSkipped = 0; HardlinkProbed = 0
                ReparseSkipped = 0; PrunedDirs = 0; Truncated = $false
                Regression = $false; Error = ''
            })))
            continue
        }

        Write-Step ("removing {0}" -f $p)
        $r = Remove-TreeGuarded -Path $p -Execute -Label 'project-cache'
        [void]$rows.Add($r)
        if ($r.FreedMB -gt 0) { Write-Ok  ("freed {0:N1} MB" -f $r.FreedMB) }
        elseif ($r.Locked -gt 0) { Write-Warn2 'locked / in use - SKIPPED' }
    }

    if (-not $Execute) {
        Write-Host ''
        Write-Ok ("plan: {0} path(s), {1:N1} MB ({2:N2} GB) would be removed; {3} refused by the guard" -f `
            $rows.Count, $plannedMB, ($plannedMB / 1024), $refused)
        Write-Warn2 'DRY RUN - nothing was deleted. Re-run the same command with -Execute to delete.'
        [void](Show-Summary -Rows $rows -FreeBefore $freeBefore -FreeAfter $freeBefore `
            -Title '04-Clean-Hidden (plan)' -Tag 'hidden' -DryRun)
        return
    }

    $freeAfter = Get-FreeGB 'C:'
    [void](Show-Summary -Rows $rows -FreeBefore $freeBefore -FreeAfter $freeAfter `
        -Title '04-Clean-Hidden' -Tag 'hidden')
    return
}

if ($Execute) {
    Write-Err2 'DELETE mode requires an explicit -Paths list. Nothing was deleted.'
    return
}

# ---------------------------------------------------------------- scan
Write-Host (" scanning for cache folders >= {0} MB (depth {1})...`n" -f $MinMB, $Depth)
$found = New-Object System.Collections.ArrayList
foreach ($root in $ScanRoots) {
    if (-not (Test-Path -LiteralPath $root)) { continue }
    $dirs = Get-ChildItem -LiteralPath $root -Directory -Recurse -Force -Depth $Depth -ErrorAction SilentlyContinue |
            Where-Object { $cacheNames -contains $_.Name }
    foreach ($d in $dirs) {
        if ($d.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        $mb = Get-DirSizeMB -Path $d.FullName
        if ($mb -ge $MinMB) {
            [void]$found.Add((New-Object psobject -Property ([ordered]@{ MB = $mb; Path = $d.FullName })))
        }
    }
}
if ($found.Count -eq 0) {
    Write-Host ("   no cache folder >= {0} MB found under the scanned roots." -f $MinMB)
} else {
    $found | Sort-Object MB -Descending | ForEach-Object {
        Write-Host ("   {0,10:N1} MB  {1}" -f $_.MB, $_.Path)
    }
    $sum = ($found | Measure-Object MB -Sum).Sum
    Write-Host ''
    Write-Ok ("{0} folder(s), {1:N1} MB total ({2:N2} GB)" -f $found.Count, $sum, ($sum / 1024))
}
Write-Host ''
Write-Warn2 'SCAN ONLY - nothing was deleted. To remove specific folders, re-run with:'
Write-Host  '          .\04-Clean-Hidden.ps1 -ScanRoots ''<root>'' -Paths ''<exact path>'' [-Execute]'
