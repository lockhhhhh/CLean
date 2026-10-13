#requires -Version 5.1
<#
  05-Scan-LargeDirs.ps1  --  READ-ONLY. One-pass aggregation of directory sizes
  across a drive, so you can see where the space actually went.

  Single traversal (much faster than measuring N directories one by one),
  aggregated at depth 3 and rolled up to depth 1.

  Usage:
    .\05-Scan-LargeDirs.ps1                       # scan C:\, top 40
    .\05-Scan-LargeDirs.ps1 -Root 'C:\Users' -Top 30
#>
[CmdletBinding()]
param(
    [string]$Root = 'C:\',
    [int]   $Top  = 40,
    [string]$Tag  = 'largedirs'
)

$ErrorActionPreference = 'SilentlyContinue'
if (-not $Root.EndsWith('\')) { $Root = $Root + '\' }

$depth3 = @{}
$depth1 = @{}
$cacheHits = @{}
$cacheNames = @('node_modules', '.cache', '.gradle', '.m2', '.nuget', '.venv', 'venv',
                '__pycache__', '.next', '.nuxt', '.turbo', 'Cache', 'Code Cache',
                'GPUCache', 'CachedData', 'Temp', 'Logs', 'logs', 'CrashDumps')

Write-Host '=================================================================='
Write-Host (" 05-Scan-LargeDirs  root={0}" -f $Root)
Write-Host '=================================================================='
Write-Host ' scanning (single pass)...'

$n = 0
foreach ($f in (Get-ChildItem -LiteralPath $Root -Recurse -Force -File -ErrorAction SilentlyContinue)) {
    $n++
    $rel = $f.FullName.Substring($Root.Length)
    $segs = $rel.Split('\')
    if ($segs.Length -lt 2) { continue }
    $k1 = $segs[0]
    $k3 = if ($segs.Length -ge 4) { ($segs[0..2] -join '\') } else { ($segs[0..($segs.Length - 2)] -join '\') }
    $len = $f.Length
    if ($depth1.ContainsKey($k1)) { $depth1[$k1] += $len } else { $depth1[$k1] = $len }
    if ($depth3.ContainsKey($k3)) { $depth3[$k3] += $len } else { $depth3[$k3] = $len }
    if ($cacheNames -contains $segs[-2]) {
        if ($cacheHits.ContainsKey($k3)) { $cacheHits[$k3] += $len } else { $cacheHits[$k3] = $len }
    }
}

Write-Host (" {0} files scanned`n" -f $n)

Write-Host '--- TOP-LEVEL DIRECTORIES'
$depth1.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {
    Write-Host ("   {0,12:N1} MB  {1}{2}" -f ($_.Value / 1MB), $Root, $_.Key)
}

Write-Host ''
Write-Host ("--- TOP {0} SUBTREES (depth 3)" -f $Top)
$topList = $depth3.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First $Top
foreach ($e in $topList) {
    Write-Host ("   {0,12:N1} MB  {1}{2}" -f ($e.Value / 1MB), $Root, $e.Key)
}

Write-Host ''
Write-Host '--- CACHE-LIKE SUBTREES (candidates for cleanup)'
$cacheHits.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 25 | ForEach-Object {
    Write-Host ("   {0,12:N1} MB  {1}{2}" -f ($_.Value / 1MB), $Root, $_.Key)
}

$repDir = Join-Path $PSScriptRoot 'reports'
if (-not (Test-Path -LiteralPath $repDir)) { New-Item -ItemType Directory -Path $repDir -Force | Out-Null }
[ordered]@{
    Root = $Root; Time = (Get-Date).ToString('o'); Files = $n
    Depth1 = @($depth1.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {
        [ordered]@{ Path = ($Root + $_.Key); MB = [math]::Round($_.Value / 1MB, 1) } })
    Depth3 = @($topList | ForEach-Object {
        [ordered]@{ Path = ($Root + $_.Key); MB = [math]::Round($_.Value / 1MB, 1) } })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $repDir ("{0}.json" -f $Tag)) -Encoding UTF8
Write-Host ("`n report -> {0}" -f (Join-Path $repDir ("{0}.json" -f $Tag)))
