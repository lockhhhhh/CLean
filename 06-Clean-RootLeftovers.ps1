#requires -Version 5.1
<#
  06-Clean-RootLeftovers.ps1  --  leftovers sitting directly at C:\ root that a
  full-drive scan flagged as pure cache / abandoned installer residue.

  Targets (each one is opt-in through a switch; nothing runs by default):
    -WinREAgent   C:\$WinREAgent          upgrade rollback data (~1.3 GB).
                                          Removing it forfeits rollback of the
                                          last Windows update.
    -HlsCache     C:\<32-hex-guid>        abandoned HLS video fragment cache
                                          (*.hls\*.ts) left by a player/downloader.
    -KuGouCache   C:\KuGou\Temp\tp2p      KuGou music P2P cache.
    -StaleInstaller  printer driver package / stray single-purpose dirs at C:\.

  DRY RUN BY DEFAULT -- add -Execute to apply. Both switches together:
    .\06-Clean-RootLeftovers.ps1 -WinREAgent -HlsCache -KuGouCache -Execute
#>
[CmdletBinding()]
param(
    [switch]$WinREAgent,
    [switch]$HlsCache,
    [switch]$KuGouCache,
    [switch]$StaleInstaller,
    [switch]$Execute
)

. (Join-Path $PSScriptRoot '_Common.ps1')

# A 32-hex-character directory name at the drive root is a downloader cache key.
$hexRoot = Get-ChildItem -LiteralPath 'C:\' -Directory -Force -ErrorAction SilentlyContinue |
           Where-Object { $_.Name -match '^[0-9a-fA-F]{32}$' } |
           Select-Object -ExpandProperty FullName

Add-AllowedRoot @(
    'C:\$WinREAgent'
    'C:\KuGou\Temp\tp2p'
    'C:\KuGou\Temp'
    'C:\M1130MFP_M1210MFP_Full_Solution'
    'C:\xunlei.d00'
    'C:\$360Section'
)
foreach ($h in $hexRoot) { Add-AllowedRoot @($h) }

$mode = 'DRY RUN (nothing is deleted)'
if ($Execute) { $mode = 'EXECUTE' }

Write-Host '=================================================================='
Write-Host (" 06-Clean-RootLeftovers.ps1    mode={0}" -f $mode)
Write-Host '=================================================================='
if (-not $Execute) { Write-Warn2 'DRY RUN: append -Execute to apply. Nothing was changed.' }

$freeBefore = Get-FreeGB 'C:'
Write-Host (" C: free before : {0} GB`n" -f $freeBefore)

$rows = New-Object System.Collections.ArrayList

# Returns $true when the path is refused, so callers can bail out early.
function Test-Refused {
    param([string]$Path)
    if (Test-SafeTarget -Path $Path) { return $false }
    Write-Err2 ("REFUSED (not on the allowlist): {0}" -f $Path)
    return $true
}

function Do-Tree {
    param([string]$Path, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path)) { Write-Host ("   {0,10}       (absent)  {1}" -f '-', $Path); return }
    if (Test-Refused $Path) { return }
    $r = Remove-TreeGuarded -Path $Path -Execute:$Execute -Label $Label
    [void]$rows.Add($r)
    $verb = 'would free'
    if ($Execute) { $verb = 'freed     ' }
    Write-Host ("   {0,10:N1} MB  {1}  {2}" -f $r.FreedMB, $verb, $Path)
}

if ($WinREAgent) {
    Write-Step 'WINDOWS RECOVERY AGENT LEFTOVER ($WinREAgent)'
    Write-Warn2 'note: this is the rollback data for your last Windows update.'
    Do-Tree 'C:\$WinREAgent' 'WinREAgent'
}

if ($HlsCache) {
    Write-Step 'ABANDONED HLS VIDEO CACHE AT C:\ ROOT'
    if ($hexRoot.Count -eq 0) { Write-Host '   (none found)' }
    foreach ($h in $hexRoot) {
        $mb = Get-DirSizeMB -Path $h
        Write-Host ("   {0,10:N1} MB  {1}   [hex-guid folder]" -f $mb, $h)
        Do-Tree $h 'HlsCache'
    }
}

if ($KuGouCache) {
    Write-Step 'KUGOU P2P CACHE'
    Do-Tree 'C:\KuGou\Temp\tp2p' 'KuGouP2P'
}

if ($StaleInstaller) {
    Write-Step 'STRAY INSTALLER / TOOL DIRECTORIES AT C:\ ROOT'
    Write-Warn2 'review each path below before -Execute: these are programs, not caches.'
    foreach ($p in @('C:\M1130MFP_M1210MFP_Full_Solution', 'C:\xunlei.d00', 'C:\$360Section')) {
        Do-Tree $p 'StaleInstaller'
    }
}

$freeAfter = Get-FreeGB 'C:'
[void](Show-Summary -Rows $rows -FreeBefore $freeBefore -FreeAfter $freeAfter `
    -Title ("06-Clean-RootLeftovers  ({0})" -f $mode) -Tag 'rootleftovers' -DryRun:(-not $Execute))

if (-not $Execute) { Write-Warn2 'This was a DRY RUN. Re-run with -Execute to apply.' }
