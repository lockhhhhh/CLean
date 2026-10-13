#requires -Version 5.1
<#
  Build.ps1  --  package the whole toolset into ONE self-contained .exe.

  Uses the csc.exe that ships inside .NET Framework 4.x, present on every
  Windows 10 / 11 machine: no .NET SDK, no NuGet, no network, no PowerShell
  module required.

  The launcher plus every .ps1 in the project root are compiled into a single
  executable; the scripts travel as embedded resources and are extracted to
  %LOCALAPPDATA%\CDiskCleaner\runtime at first run.

  WHY THE STAGING DIRECTORY: Windows PowerShell 5.1 converts arguments for
  native executables through the console code page. With a UTF-8 console any
  non-ASCII character in a csc argument is corrupted (the path turns into
  mojibake and csc fails with CS2021 "filename too long or invalid"). The build
  therefore compiles inside an ASCII-only temp directory and copies the finished
  binary to its real, possibly non-ASCII name afterwards - PowerShell's own
  file cmdlets are Unicode-safe.

  Two targets share this build:
    (default)  Launcher.cs     -> console menu,  C盘清理工具.exe
    -Gui       GuiLauncher.cs  -> WinForms GUI,  C盘清理工具-GUI.exe

  Usage:
    .\src\Build.ps1
    .\src\Build.ps1 -Gui
    .\src\Build.ps1 -OutputName 'MyCleaner.exe' -KeepStage
#>
[CmdletBinding()]
param(
    [string]$OutputName,
    [switch]$Gui,
    [switch]$KeepStage
)

$ErrorActionPreference = 'Stop'

$srcDir  = $PSScriptRoot
$root    = Split-Path -Parent $srcDir

# The GUI target compiles GuiLauncher.cs as a windowed app; the default target
# compiles Launcher.cs as a console app. Everything else is identical.
# The GUI is split over three sources (window / pages+scanner / animations); all go to csc.
$sourceFile = 'Launcher.cs'
$extraSources = @()
$target     = 'exe'
if ($Gui) {
    $sourceFile = 'GuiLauncher.cs'
    $extraSources = @('GuiPages.cs', 'GuiAnims.cs')
    $target = 'winexe'
}

if (-not $PSBoundParameters.ContainsKey('OutputName')) {
    $OutputName = if ($Gui) { 'C盘清理工具-GUI.exe' } else { 'C盘清理工具.exe' }
}
$outPath = Join-Path $root $OutputName

$srcFile = Join-Path $srcDir $sourceFile
if (-not (Test-Path -LiteralPath $srcFile)) { throw ("source not found: {0}" -f $srcFile) }

# Windows PowerShell 5.1 decodes a BOM-less .ps1 as ANSI(936), so the Chinese
# literals in THIS file turn into mojibake whose bytes are illegal in a file name.
# Catch that here instead of failing later inside Copy-Item with a cryptic message.
if ($OutputName -match '[<>:"/\\|?*\x00-\x1F]') {
    throw ("OutputName contains characters that are illegal in a path: '{0}'. " +
           "If it looks like mojibake, this script is missing its UTF-8 BOM " +
           "(keep it ASCII-only, or write it as UTF-8 WITH BOM)." -f $OutputName)
}

# ASCII name used inside the staging directory
$stageExe = 'CDiskCleaner.exe'

Write-Host '=================================================================='
Write-Host (" Building single-file {0} launcher" -f $(if ($Gui) { 'GUI' } else { 'console' }))
Write-Host '=================================================================='

# --- 1. locate the in-box C# compiler ------------------------------------
$csc = $null
foreach ($candidate in @(
        (Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe'))) {
    if (Test-Path -LiteralPath $candidate) { $csc = $candidate; break }
}
if (-not $csc) {
    throw 'csc.exe not found. This build needs .NET Framework 4.x (present on Windows 10/11 by default).'
}
Write-Host ("  compiler : {0}" -f $csc)

# --- 2. collect the PowerShell payload -----------------------------------
$scripts = @(Get-ChildItem -LiteralPath $root -Filter '*.ps1' -File | Sort-Object Name)
if ($scripts.Count -eq 0) { throw ("no .ps1 scripts found in {0}" -f $root) }
Write-Host ("  embedding: {0} script(s)" -f $scripts.Count)
foreach ($s in $scripts) { Write-Host ("             - {0}" -f $s.Name) }

# --- 3. stage everything under an ASCII-only path ------------------------
$stage = Join-Path $env:TEMP ('cdcleaner-build-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
if ($stage -match '[^\x20-\x7E]') {
    throw ("staging path is not ASCII-only: {0}" -f $stage)
}
Write-Host ("  staging  : {0}" -f $stage)

New-Item -ItemType Directory -Path $stage -Force | Out-Null

try {
    Copy-Item -LiteralPath $srcFile                 -Destination $stage
    Copy-Item -LiteralPath (Join-Path $srcDir 'app.manifest') -Destination $stage
    foreach ($s in $scripts) { Copy-Item -LiteralPath $s.FullName -Destination $stage }

    # --- 4. compile -------------------------------------------------------
    # /codepage:65001 is REQUIRED: the launcher sources are UTF-8 without a BOM and
    # hold Chinese string literals. Without it csc assumes the system ANSI code page
    # (936 on Chinese Windows) and every Chinese label becomes mojibake.
    $cscArgs = New-Object System.Collections.ArrayList
    [void]$cscArgs.AddRange(@(
            '/nologo'
            ('/target:{0}' -f $target)
            '/platform:anycpu'
            '/optimize+'
            '/codepage:65001'
            '/win32manifest:app.manifest'
            ('/out:{0}' -f $stageExe)
        ))

    if ($Gui) {
        # WinForms / GDI+ are not in csc's default reference set; point at the
        # in-box framework assemblies explicitly.
        $fw = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
        if (-not (Test-Path -LiteralPath $fw)) {
            $fw = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319'
        }
        foreach ($ref in @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')) {
            $refPath = Join-Path $fw $ref
            if (-not (Test-Path -LiteralPath $refPath)) { throw ("missing framework assembly: {0}" -f $refPath) }
            [void]$cscArgs.Add(('/reference:{0}' -f $refPath))
        }
    }

    foreach ($s in $scripts) {
        # resource name is the bare file name, which is what the launcher looks for
        [void]$cscArgs.Add(('/resource:{0},{1}' -f $s.Name, $s.Name))
    }
    [void]$cscArgs.Add($sourceFile)
    foreach ($extra in $extraSources) {
        $extraPath = Join-Path $srcDir $extra
        if (-not (Test-Path -LiteralPath $extraPath)) { throw ("source not found: {0}" -f $extraPath) }
        Copy-Item -LiteralPath $extraPath -Destination $stage
        [void]$cscArgs.Add($extra)
    }

    Write-Host ''
    Write-Host '  compiling...'

    $prevEncoding = [Console]::OutputEncoding
    $exit = 0
    Push-Location $stage
    try {
        # Align the console code page with csc's native message encoding so any
        # compiler diagnostic is readable instead of mangled.
        try { [Console]::OutputEncoding = [System.Text.Encoding]::Default } catch { }
        & $csc $cscArgs
        $exit = $LASTEXITCODE
    } finally {
        Pop-Location
        try { [Console]::OutputEncoding = $prevEncoding } catch { }
    }

    if ($exit -ne 0) { throw ("compile failed (csc exit {0})" -f $exit) }

    $stageOut = Join-Path $stage $stageExe
    if (-not (Test-Path -LiteralPath $stageOut)) {
        throw 'compile reported success but produced no output file.'
    }

    # --- 5. verify the payload really is embedded -------------------------
    # LoadFile reflects over metadata only; it never executes Main.
    $asm   = [System.Reflection.Assembly]::LoadFile($stageOut)
    $names = @($asm.GetManifestResourceNames() | Sort-Object)
    $ps1   = @($names | Where-Object { $_ -like '*.ps1' })

    $missing = @($scripts | Where-Object { $ps1 -notcontains $_.Name })
    if ($missing.Count -gt 0) {
        throw ("these scripts were NOT embedded: {0}" -f (($missing | ForEach-Object { $_.Name }) -join ', '))
    }
    if ($ps1.Count -ne $scripts.Count) {
        throw ("resource count mismatch: embedded {0}, expected {1}" -f $ps1.Count, $scripts.Count)
    }

    # --- 6. publish under the real (possibly non-ASCII) name --------------
    Copy-Item -LiteralPath $stageOut -Destination $outPath -Force

    Write-Host ''
    Write-Host '------------------------------------------------------------------'
    Write-Host ("  target : {0}  ({1})" -f $sourceFile, $target)
    Write-Host ("  output : {0}" -f $outPath)
    Write-Host ("  size   : {0:N0} bytes" -f (Get-Item -LiteralPath $outPath).Length)
    Write-Host ("  embedded resources: {0} ({1} scripts verified)" -f $names.Count, $ps1.Count)
    foreach ($n in $ps1) { Write-Host ("      {0}" -f $n) }
    Write-Host '------------------------------------------------------------------'
    Write-Host '  BUILD OK - single-file executable is ready.'
    Write-Host '  Copy it anywhere on Windows 10 / 11 (x64 or x86); it needs nothing else.'
    Write-Host '=================================================================='
}
finally {
    if ($KeepStage) {
        Write-Host ("  staging kept: {0}" -f $stage)
    } else {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}
