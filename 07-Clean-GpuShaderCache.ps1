#requires -Version 5.1
<#
  07-Clean-GpuShaderCache.ps1  --  detect the installed GPU vendor and clear
  that vendor's compiled shader cache.

  DETECTION is PCI-vendor-id based, not name based:
      VEN_10DE -> NVIDIA        VEN_1002 / VEN_1022 -> AMD        VEN_8086 -> Intel
  Virtual / indirect display adapters (remote-desktop IDD drivers such as Oray,
  Parsec or IddSample, and Microsoft Basic Render) report PNPDeviceID values
  like "ROOT\DISPLAY\0000" or "SWD\..." with no PCI vendor id. They are NOT
  treated as GPUs. When no real GPU is present the script says so and skips
  the entire step instead of guessing.

  WHAT IS DELETED: compiled shader / pipeline blobs only. Drivers and games
  recompile them on next launch, so expect a short stutter the first time a
  game or D3D app starts afterwards. Nothing is removed from the driver itself.

  DRY RUN BY DEFAULT -- add -Execute to apply.

    .\07-Clean-GpuShaderCache.ps1                             # detect + plan only
    .\07-Clean-GpuShaderCache.ps1 -Execute                    # clear vendor caches
    .\07-Clean-GpuShaderCache.ps1 -Execute -IncludeD3DSCache  # + Windows D3D cache
    .\07-Clean-GpuShaderCache.ps1 -ShowAll                    # list every adapter
#>
[CmdletBinding()]
param(
    [switch]$Execute,
    [switch]$IncludeD3DSCache,
    [switch]$ShowAll
)

. (Join-Path $PSScriptRoot '_Common.ps1')

$mode = 'DRY RUN (nothing is deleted)'
if ($Execute) { $mode = 'EXECUTE' }

Write-Host '=================================================================='
Write-Host (" 07-Clean-GpuShaderCache.ps1    mode={0}" -f $mode)
Write-Host '=================================================================='
if (-not $Execute) { Write-Warn2 'DRY RUN: append -Execute to actually delete. Nothing was changed.' }

# ---------------------------------------------------------------- detect
Write-Step 'DETECTING GRAPHICS ADAPTERS'

$gpu = Get-GpuVendor

foreach ($a in $gpu.Adapters) {
    $tag = 'adapter'
    if ($a.Vendor -in @('NVIDIA', 'AMD', 'Intel')) { $tag = 'GPU    ' }
    if ($a.Vendor -eq 'Virtual') { $tag = 'skipped' }
    if ($ShowAll -or $a.Vendor -ne 'Virtual') {
        Write-Host ("   [{0}] {1,-26} {2}" -f $tag, $a.Vendor, $a.Name)
        Write-Host ("             {0}" -f $a.Reason)
    }
}

if ($gpu.Vendors.Count -eq 0) {
    Write-Host ''
    Write-Warn2 'No discrete or integrated GPU detected (no display adapter reported a PCI vendor id).'
    Write-Warn2 'SKIPPING the shader-cache step - nothing was changed.'
    Write-Host '=================================================================='
    return
}

$vendorList = ($gpu.Vendors -join ' + ')
Write-Ok ("detected GPU vendor(s): {0}" -f $vendorList)

# ---------------------------------------------------------------- targets
$targets = Get-GpuShaderCachePaths -Vendors $gpu.Vendors -IncludeVendorNeutral:$IncludeD3DSCache

Add-AllowedRoot $targets
if ($IncludeD3DSCache) {
    # D3DSCache is vendor neutral (used by Windows itself for D3D12), but it is
    # only cleared when explicitly asked for.
    Write-Host '   note: -IncludeD3DSCache also clears the vendor-neutral Windows D3D cache.'
}

$freeBefore = Get-FreeGB 'C:'
Write-Host ("`n C: free before : {0} GB" -f $freeBefore)
Write-Host ''

# ---------------------------------------------------------------- clean
Write-Step ("SHADER CACHE ({0})" -f $vendorList)

$rows = New-Object System.Collections.ArrayList
$present = 0

foreach ($p in $targets) {
    if (-not (Test-Path -LiteralPath $p)) {
        Write-Host ("   {0,10}       (absent)  {1}" -f '-', $p)
        continue
    }
    $present++
    try { [void](Assert-SafeTarget -Path $p) } catch { Write-Err2 $_.Exception.Message; continue }
    $r = Remove-ContentGuarded -Path $p -Execute:$Execute -Label 'GpuShaderCache'
    [void]$rows.Add($r)
    $verb = 'would free'
    if ($Execute) { $verb = 'freed     ' }
    Write-Host ("   {0,10:N1} MB  {1}  {2}" -f $r.FreedMB, $verb, $p)
}

if ($present -eq 0) {
    Write-Host ''
    Write-Warn2 ("{0} driver(s) detected but no shader-cache directory exists yet." -f $vendorList)
    Write-Warn2 'Nothing to clean - this is normal on a fresh driver install.'
}

# ---------------------------------------------------------------- summary
$freeAfter = Get-FreeGB 'C:'
if ($rows.Count -gt 0) {
    [void](Show-Summary -Rows $rows -FreeBefore $freeBefore -FreeAfter $freeAfter `
        -Title ("07-Clean-GpuShaderCache  [{0}]  ({1})" -f $vendorList, $mode) -Tag 'gpu-shadercache' -DryRun:(-not $Execute))
} else {
    Write-Host ''
    Write-Host '=================================================================='
    Write-Host (" 07-Clean-GpuShaderCache  [{0}]  ({1})" -f $vendorList, $mode)
    Write-Host '=================================================================='
    Write-Host '   no cache directory present - nothing to report'
    Write-Host '=================================================================='
}

if (-not $Execute) { Write-Warn2 'This was a DRY RUN. Re-run with -Execute to apply.' }
