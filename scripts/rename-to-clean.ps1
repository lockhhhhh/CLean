#requires -Version 5.1
<#
  rename-to-clean.ps1 -- one-shot finish line for the CLean rename.

  WHY THIS FILE EXISTS: the rename has already been applied to every source and
  doc file, but the shell channel inside the DSH session is dead (pwsh exits
  0xC0000142 = STATUS_DLL_INIT_FAILED), so the agent could not run csc.exe, git
  or gh. Run this once from YOUR OWN terminal. It is disposable -- delete it
  once it has done its job.

  Order of operations:
    1. rebuild CLean.exe + CLean-GUI.exe through src\Build.ps1 (same csc.exe)
    2. delete the stale pre-rename *.exe in the repo root   (-Execute)
    3. git add -A + commit                                   (-Commit)
    4. git push                                              (-Push)
    5. gh repo rename + re-point origin                      (-RenameRepo)
    6. repo-FOLDER rename                                    (-RenameFolder)

  The .exe files are gitignored, so steps 1-2 and steps 3-5 are independent.

  ASCII-ONLY ON PURPOSE: Windows PowerShell 5.1 decodes a .ps1 that has no
  UTF-8 BOM as ANSI(936), so a Chinese literal in here would become mojibake
  (exactly the trap that src\Build.ps1 documents). Nothing in this file needs a
  non-ASCII character; the paths and file names that do are derived at runtime.

  Usage (switches are independent; with none of them this only reports):
    pwsh -File .\scripts\rename-to-clean.ps1
    pwsh -File .\scripts\rename-to-clean.ps1 -Execute
    pwsh -File .\scripts\rename-to-clean.ps1 -Execute -Commit -Push -RenameRepo
    pwsh -File .\scripts\rename-to-clean.ps1 -Execute -RenameFolder
#>
[CmdletBinding()]
param(
    [switch]$Execute,
    [switch]$Commit,
    [switch]$Push,
    [switch]$RenameRepo,
    [switch]$RenameFolder,
    [string]$RepoName = 'CLean',
    [string]$Message = 'Rename the tool to CLean'
)

$ErrorActionPreference = 'Stop'

$scriptsDir = $PSScriptRoot
$root       = Split-Path -Parent $scriptsDir
$build      = Join-Path (Join-Path $root 'src') 'Build.ps1'

function Say([string]$text) { Write-Host $text }
function Step([string]$text) { Write-Host ''; Write-Host ('== ' + $text) }

Say ('repo root : ' + $root)
if (-not (Test-Path -LiteralPath $build)) { throw ('not found: ' + $build) }
Say 'Switches: -Execute (rebuild + old-exe cleanup), -Commit, -Push, -RenameRepo, -RenameFolder.'
Say 'With none of them this run only reports.'

# --- 0. environment sanity ------------------------------------------------
Step 'environment'
$envHome = $env:HOME
Say ('HOME      : ' + $(if ([string]::IsNullOrEmpty($envHome)) { '(unset)' } else { $envHome }))
if ($envHome -and ($envHome -ne $env:USERPROFILE)) {
    Write-Host '  WARNING: HOME differs from USERPROFILE.' -ForegroundColor Yellow
    Write-Host '  git and gh read HOME, so they use the config that lives there.'
    Write-Host '  If that is the DSH sandbox HOME, clear it for this session first:'
    Write-Host '      $env:HOME = $env:USERPROFILE'
}

# --- 1. sanity: no old ASCII name left in source or docs ------------------
Step 'verify rename is complete'
$exts = @('*.md', '*.cs', '*.ps1', '*.manifest', '*.json', '*.sh')
$self = $PSCommandPath
$left = @(Get-ChildItem -LiteralPath $root -Recurse -File -Include $exts |
        Where-Object { $_.FullName -notlike '*\.git\*' -and $_.FullName -ne $self } |
        Select-String -Pattern 'CDiskCleaner|cdisk-cleaner' -List)
if ($left.Count -eq 0) { Say '  clean: no CDiskCleaner / cdisk-cleaner left outside this script' }
else { foreach ($m in $left) { Say ('  STILL THERE: ' + $m.Path + ':' + $m.LineNumber + '  ' + $m.Line.Trim()) } }

# --- 2. rebuild under the new output names --------------------------------
Step 'rebuild'
$targets = @(
    [pscustomobject]@{ Name = 'CLean.exe';     Gui = $false },
    [pscustomobject]@{ Name = 'CLean-GUI.exe'; Gui = $true }
)
foreach ($t in $targets) {
    $out = Join-Path $root $t.Name
    if (-not $Execute) { Say ('  would build : ' + $t.Name); continue }
    $psArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $build, '-OutputName', $t.Name)
    if ($t.Gui) { $psArgs += '-Gui' }
    & powershell.exe @psArgs
    if ($LASTEXITCODE -ne 0) { throw ('build failed for ' + $t.Name) }
    if (-not (Test-Path -LiteralPath $out)) { throw ('Build.ps1 reported success but ' + $t.Name + ' is missing') }
    Say ('  built : {0}  ({1:N0} bytes)  [{2}]' -f $t.Name,
        (Get-Item -LiteralPath $out).Length, $(if ($t.Gui) { 'GUI' } else { 'console' }))
}
if ($Execute) {
    foreach ($t in $targets) {
        $out = Join-Path $root $t.Name
        if (-not (Test-Path -LiteralPath $out)) { throw ('refusing to touch old exes: ' + $t.Name + ' does not exist') }
    }
}

# --- 3. stale pre-rename exes --------------------------------------------
Step 'stale exes'
$keep  = @('CLean.exe', 'CLean-GUI.exe')
$stale = @(Get-ChildItem -LiteralPath $root -Filter '*.exe' -File | Where-Object { $keep -notcontains $_.Name })
if ($stale.Count -eq 0) { Say '  none' }
foreach ($f in $stale) {
    Say ('  stale : ' + $f.Name + ('  ({0:N0} bytes)' -f $f.Length))
    if ($Execute) { Remove-Item -LiteralPath $f.FullName -Force; Say '          removed' }
}

# --- 4. commit / push ----------------------------------------------------
Step 'git'
Push-Location $root
try {
    Say ('  branch : ' + (@(git rev-parse --abbrev-ref HEAD) -join ' '))
    Say ('  HEAD   : ' + (@(git rev-parse --short HEAD) -join ' '))
    Say '  status :'
    git --no-pager status --short | ForEach-Object { Say ('    ' + $_) }

    if ($Commit) {
        git add -A
        git commit -m $Message
        if ($LASTEXITCODE -ne 0) { throw 'git commit failed' }
        Say ('  committed: ' + (@(git rev-parse --short HEAD) -join ' '))
    }
    else { Say '  (skipped; pass -Commit)' }

    if ($Push) {
        git push
        if ($LASTEXITCODE -ne 0) { throw 'git push failed' }
    }
    elseif ($Commit) { Say '  (not pushed; pass -Push)' }
}
finally { Pop-Location }

# --- 5. GitHub repo rename ----------------------------------------------
Step 'github repo'
if (-not $RenameRepo) {
    Say ('  (skipped; pass -RenameRepo to run: gh repo rename ' + $RepoName + ' --yes)')
}
else {
    Push-Location $root
    try {
        gh repo rename $RepoName --yes
        if ($LASTEXITCODE -ne 0) { throw 'gh repo rename failed' }
        $slug = (@(gh repo view --json nameWithOwner --jq '.nameWithOwner') -join '').Trim()
        if ($slug) {
            git remote set-url origin ('https://github.com/' + $slug + '.git')
            Say ('  origin : ' + (@(git remote get-url origin) -join ''))
        }
        else { Say '  WARNING: could not read the new slug; fix origin by hand with git remote set-url' }
    }
    finally { Pop-Location }
}

# --- 6. repo FOLDER rename ----------------------------------------------
Step 'repo folder'
Say ('  current: ' + $root)
$newPath = Join-Path (Split-Path -Parent $root) 'CLean'
if ($RenameFolder) {
    if (Test-Path -LiteralPath $newPath) { Say ('  target already exists, skipping: ' + $newPath) }
    else {
        try { Rename-Item -LiteralPath $root -NewName 'CLean'; Say ('  renamed to: ' + $newPath) }
        catch {
            Say ('  FAILED: ' + $_.Exception.Message)
            Say '  Close every terminal / session sitting in this folder, then retry --'
            Say ('      Rename-Item -LiteralPath ''' + $root + ''' -NewName ''CLean''')
        }
    }
}
else {
    Say '  not attempted. Close this session first, then run this from the PARENT folder:'
    Say ('      Rename-Item -LiteralPath ''' + $root + ''' -NewName ''CLean''')
    Say '  (DSH points its workspace at the old path -- reopen the session on the new one.)'
}

Write-Host ''
Write-Host 'done. This helper script can be deleted now.'
