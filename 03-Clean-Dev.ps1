#requires -Version 5.1
<#
  03-Clean-Dev.ps1  --  developer toolchain cache cleanup.

  Covers: npm cache, pnpm store, pip cache, yarn cache, Go module cache,
          Gradle caches, Maven repo, NuGet packages, Cargo registry,
          Python/HF/torch caches under ~/.cache.

  DRY RUN BY DEFAULT -- nothing is deleted / no tool is invoked unless -Execute.

  Usage:
    .\03-Clean-Dev.ps1                  # plan only
    .\03-Clean-Dev.ps1 -Execute         # run the caches' own prune/clean commands
    .\03-Clean-Dev.ps1 -Execute -Aggressive   # ALSO wipe Gradle/Maven/NuGet/Cargo
                                              # (big win, but the next build re-downloads)
#>
[CmdletBinding()]
param(
    [switch]$Execute,
    [switch]$Aggressive
)

. (Join-Path $PSScriptRoot '_Common.ps1')

$U  = $env:USERPROFILE
$LA = $env:LOCALAPPDATA
$RA = $env:APPDATA

Add-AllowedRoot @(
    (Join-Path $LA 'npm-cache')
    (Join-Path $U  '.npm')
    (Join-Path $LA 'pnpm')
    (Join-Path $LA 'pnpm-store')
    (Join-Path $U  '.pnpm-store')
    (Join-Path $U  '.local\share\pnpm')
    (Join-Path $U  '.yarn')
    (Join-Path $LA 'Yarn')
    (Join-Path $U  '.bun')
    (Join-Path $LA 'pip')
    (Join-Path $U  '.cache')
    (Join-Path $U  'AppData\Local\conda')
    (Join-Path $U  '.conda')
    (Join-Path $U  'go\pkg\mod')
    (Join-Path $U  '.gradle')
    (Join-Path $U  '.m2')
    (Join-Path $U  '.nuget')
    (Join-Path $U  '.cargo')
    (Join-Path $RA 'npm-cache')
)

$mode = 'DRY RUN (nothing is deleted)'
if ($Execute) { $mode = 'EXECUTE' }

Write-Host '=================================================================='
Write-Host (" 03-Clean-Dev.ps1        mode={0}   Aggressive={1}" -f $mode, $Aggressive)
Write-Host '=================================================================='
if (-not $Execute) { Write-Warn2 'DRY RUN: append -Execute to actually clean. Nothing was changed.' }

$freeBefore = Get-FreeGB 'C:'
Write-Host (" C: free before : {0} GB`n" -f $freeBefore)

$rows = New-Object System.Collections.ArrayList

# --- path-based cleaner ---------------------------------------------------
function Do-Clean {
    param([string]$Path, [string[]]$Filter = @('*'), [string]$Label = '')
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Host ("   {0,10}       (absent)  {1}" -f '-', $Path)
        return
    }
    try { [void](Assert-SafeTarget -Path $Path) } catch { Write-Err2 $_.Exception.Message; return }
    $r = Remove-ContentGuarded -Path $Path -Filter $Filter -Execute:$Execute -Label $Label
    [void]$rows.Add($r)
    $verb = 'would free'
    if ($Execute) { $verb = 'freed     ' }
    Write-Host ("   {0,10:N1} MB  {1}  {2}" -f $r.FreedMB, $verb, $Path)
}

# --- tool-invocation cleaner ---------------------------------------------
function Invoke-CacheTool {
    param(
        [string]  $Exe,
        [string[]]$ArgList = @(),
        [string]  $CachePath = '',
        [string]  $Label = ''
    )
    $cmd = Get-Command $Exe -ErrorAction SilentlyContinue
    if (-not $cmd) {
        Write-Host ("   {0,10}       (tool '{1}' not installed - skipped)" -f '-', $Exe)
        return
    }
    $before = 0
    if ($CachePath -and (Test-Path -LiteralPath $CachePath)) { $before = Get-DirSizeMB -Path $CachePath }

    if (-not $Execute) {
        Write-Host ("   {0,10:N1} MB  would run: {1} {2}" -f $before, $Exe, ($ArgList -join ' '))
        [void]$rows.Add((New-Object psobject -Property ([ordered]@{
            Path = ("{0} {1}" -f $Exe, ($ArgList -join ' ')); Label = $Label; Exists = $true
            BeforeMB = $before; AfterMB = $before; FreedMB = 0; ItemsGone = 0; Locked = 0
        })))
        return
    }

    Write-Host ("   running: {0} {1}" -f $Exe, ($ArgList -join ' '))
    try {
        & $Exe @ArgList 2>&1 | ForEach-Object { Write-Host ("       {0}" -f $_) }
    } catch {
        Write-Warn2 ("{0} reported an error (continuing): {1}" -f $Exe, $_.Exception.Message)
    }
    $after = $before
    if ($CachePath -and (Test-Path -LiteralPath $CachePath)) { $after = Get-DirSizeMB -Path $CachePath }
    $freed = [math]::Round(($before - $after) / 1, 1)
    if ($freed -lt 0) { $freed = 0 }
    Write-Ok ("{0}: {1:N1} MB -> {2:N1} MB  (freed {3:N1} MB)" -f $Exe, $before, $after, $freed)
    [void]$rows.Add((New-Object psobject -Property ([ordered]@{
        Path = (Join-Path $PSScriptRoot ("reports\{0}" -f $Label)); Label = $Label; Exists = $true
        BeforeMB = $before; AfterMB = $after; FreedMB = $freed; ItemsGone = 0; Locked = 0
    })))
}

# --- Node ----------------------------------------------------------------
Write-Step 'NODE.JS / NPM'
$npmCache = Join-Path $LA 'npm-cache'
Invoke-CacheTool -Exe 'npm' -ArgList @('cache', 'clean', '--force') -CachePath $npmCache -Label 'npm'
Do-Clean -Path (Join-Path $U '.npm\_logs') -Label 'npm-logs'

Write-Step 'PNPM'
$pnpmStore = Join-Path $LA 'pnpm'
Invoke-CacheTool -Exe 'pnpm' -ArgList @('store', 'prune') -CachePath $pnpmStore -Label 'pnpm'
if ($Aggressive) {
    Do-Clean -Path (Join-Path $LA 'pnpm\store') -Label 'pnpm-store'
    Do-Clean -Path (Join-Path $U '.local\share\pnpm\store') -Label 'pnpm-store-alt'
}

Write-Step 'YARN'
$yarnCache = Join-Path $LA 'Yarn\Cache'
Invoke-CacheTool -Exe 'yarn' -ArgList @('cache', 'clean') -CachePath $yarnCache -Label 'yarn'
Do-Clean -Path $yarnCache -Label 'yarn-cache'

# --- Python --------------------------------------------------------------
Write-Step 'PYTHON / PIP'
$pipCache = Join-Path $LA 'pip\Cache'
Invoke-CacheTool -Exe 'pip' -ArgList @('cache', 'purge') -CachePath $pipCache -Label 'pip'
Do-Clean -Path (Join-Path $U '.cache\pip') -Label 'pip-alt'
Do-Clean -Path (Join-Path $U '.cache\huggingface') -Label 'huggingface'
Do-Clean -Path (Join-Path $U '.cache\torch') -Label 'torch'
if ($Aggressive) {
    Do-Clean -Path (Join-Path $U 'AppData\Local\conda\conda\pkgs') -Label 'conda-pkgs'
    Do-Clean -Path (Join-Path $U '.conda\pkgs') -Label 'conda-pkgs2'
}

# --- Go -----------------------------------------------------------------
Write-Step 'GO MODULE CACHE'
Invoke-CacheTool -Exe 'go' -ArgList @('clean', '-modcache') -CachePath (Join-Path $U 'go\pkg\mod') -Label 'go'
if ($Aggressive) { Do-Clean -Path (Join-Path $U 'go\pkg\mod') -Label 'go-modcache' }

# --- JVM / .NET / Rust (aggressive: they re-download on next build) -------
if ($Aggressive) {
    Write-Step 'GRADLE / MAVEN / NUGET / CARGO  (aggressive)'
    Do-Clean -Path (Join-Path $U '.gradle\caches')   -Label 'gradle'
    Do-Clean -Path (Join-Path $U '.gradle\daemon')   -Label 'gradle-daemon'
    Do-Clean -Path (Join-Path $U '.m2\repository')   -Label 'maven'
    Do-Clean -Path (Join-Path $U '.nuget\packages')  -Label 'nuget'
    Do-Clean -Path (Join-Path $U '.cargo\registry')  -Label 'cargo'
    Do-Clean -Path (Join-Path $U '.cargo\git')       -Label 'cargo-git'
} else {
    Write-Step 'GRADLE / MAVEN / NUGET / CARGO'
    foreach ($p in @(
        (Join-Path $U '.gradle\caches'), (Join-Path $U '.m2\repository'),
        (Join-Path $U '.nuget\packages'), (Join-Path $U '.cargo\registry'))) {
        if (Test-Path -LiteralPath $p) {
            Write-Host ("   {0,10:N1} MB  kept (pass -Aggressive to wipe)  {1}" -f (Get-DirSizeMB -Path $p), $p)
        }
    }
}

$freeAfter = Get-FreeGB 'C:'
[void](Show-Summary -Rows $rows -FreeBefore $freeBefore -FreeAfter $freeAfter `
    -Title ("03-Clean-Dev  ({0})" -f $mode) -Tag 'dev' -DryRun:(-not $Execute))

if (-not $Execute) { Write-Warn2 'This was a DRY RUN. Re-run with -Execute to apply.' }
