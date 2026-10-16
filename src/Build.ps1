#requires -Version 5.1
<#
  Build.ps1  --  package the whole toolset into ONE self-contained .exe.

  Uses the csc.exe that ships inside .NET Framework 4.x, present on every
  Windows 10 / 11 machine: no .NET SDK, no NuGet, no network, no PowerShell
  module required.

  The launcher plus every .ps1 in the project root are compiled into a single
  executable; the scripts travel as embedded resources and are extracted to
  %LOCALAPPDATA%\CLean\runtime at first run.

  WHY THE STAGING DIRECTORY: Windows PowerShell 5.1 converts arguments for
  native executables through the console code page. With a UTF-8 console any
  non-ASCII character in a csc argument is corrupted (the path turns into
  mojibake and csc fails with CS2021 "filename too long or invalid"). The build
  therefore compiles inside an ASCII-only temp directory and copies the finished
  binary to its real, possibly non-ASCII name afterwards - PowerShell's own
  file cmdlets are Unicode-safe.

  VERSION at the repo root is the single source of truth for the version: this
  script turns it into VersionInfo.cs (generated into the staging directory,
  never committed) and installer\CLean.iss reads the very same file.

  assets\CLean.ico is the single source of the program icon: it is embedded here
  with /win32icon (verified afterwards by looking for the 256px frame inside the
  finished PE) and installer\CLean.iss points SetupIconFile at the very same file,
  so the two EXEs, Setup.exe and every shortcut cannot disagree about the icon.

  Two targets share this build:
    (default)  Launcher.cs     -> console menu,  CLean.exe
    -Gui       GuiLauncher.cs  -> WinForms GUI,  CLean-GUI.exe

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

# --- the single source of truth for the product version -------------------
$versionFile = Join-Path $root 'VERSION'
if (-not (Test-Path -LiteralPath $versionFile)) { throw ("missing: {0}" -f $versionFile) }
$version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw ("VERSION must look like 1.2.3 but contains '{0}'" -f $version)
}
# AssemblyVersion wants four parts; the product version stays three.
$version4  = $version + '.0'
$product   = 'CLean'
$publisher = 'lockhhhhh'
$repoUrl   = 'https://github.com/lockhhhhh/CLean'

# --- the one program icon --------------------------------------------------
# Same rule as VERSION: exactly one file, read by this build AND by
# installer\CLean.iss (SetupIconFile). It is copied into the ASCII staging
# directory and referenced by that short ASCII name for the same code-page
# reason as everything else passed to csc.
$iconPath = Join-Path $root 'assets\CLean.ico'
if (-not (Test-Path -LiteralPath $iconPath)) { throw ("icon not found: {0}" -f $iconPath) }
$iconName = 'app.ico'

# Two helpers for the icon self-check in step 5b. They live in this file rather than
# in a module because Build.ps1 has no external dependency on purpose.
function Get-IcoFrame {
    # The largest frame of an .ico, as @{ Width = n; Bytes = byte[] }. Every directory
    # entry is bounds-checked: a truncated .ico must fail the build with a readable
    # message, not with an index-out-of-range from deep inside Array.Copy.
    param([byte[]]$Bytes)
    if ($Bytes.Length -lt 6 -or $Bytes[0] -ne 0 -or $Bytes[1] -ne 0 -or $Bytes[2] -ne 1 -or $Bytes[3] -ne 0) {
        throw 'the icon is not an .ico file (bad ICONDIR header)'
    }
    $count = [BitConverter]::ToUInt16($Bytes, 4)
    if ($count -lt 1 -or (6 + $count * 16) -gt $Bytes.Length) { throw 'the .ico declares no usable frame' }
    $best = $null
    $bestWidth = -1
    for ($i = 0; $i -lt $count; $i++) {
        $e = 6 + $i * 16
        $w = [int]$Bytes[$e]
        if ($w -eq 0) { $w = 256 }                 # 0 encodes 256 in an ICONDIR entry
        $len = [BitConverter]::ToInt32($Bytes, $e + 8)
        $off = [BitConverter]::ToInt32($Bytes, $e + 12)
        if ($len -le 0 -or $off -lt 0 -or ($off + $len) -gt $Bytes.Length) {
            throw ("the .ico is truncated (frame {0}: offset {1} + {2} bytes)" -f $i, $off, $len)
        }
        if ($w -gt $bestWidth) {
            $bestWidth = $w
            $frame = New-Object byte[] $len
            [Array]::Copy($Bytes, $off, $frame, 0, $len)
            $best = $frame
        }
    }
    return @{ Width = $bestWidth; Bytes = $best }
}

function Find-ByteSequence {
    # Offset of $Needle in $Haystack, or -1. [Array]::IndexOf scans at native speed and
    # the byte-by-byte compare only runs where the first byte already matches, so hunting
    # for a 40 KB frame inside a few hundred KB of PE is fast.
    param([byte[]]$Haystack, [byte[]]$Needle)
    $limit = $Haystack.Length - $Needle.Length
    $i = 0
    while ($i -le $limit) {
        $j = [Array]::IndexOf($Haystack, $Needle[0], $i)
        if ($j -lt 0 -or $j -gt $limit) { return -1 }
        $hit = $true
        for ($k = 1; $k -lt $Needle.Length; $k++) {
            if ($Haystack[$j + $k] -ne $Needle[$k]) { $hit = $false; break }
        }
        if ($hit) { return $j }
        $i = $j + 1
    }
    return -1
}

# The GUI target compiles GuiLauncher.cs as a windowed app; the default target
# compiles Launcher.cs as a console app. Everything else is identical.
# UpdateCheck.cs (the "is there a newer release?" client) is shared by both.
# The GUI is split over four sources (window / pages+scanner / animations / report
# rendering); all go to csc.
$sourceFile = 'Launcher.cs'
$extraSources = @('UpdateCheck.cs')
$target     = 'exe'
if ($Gui) {
    $sourceFile = 'GuiLauncher.cs'
    $extraSources = @('GuiPages.cs', 'GuiAnims.cs', 'ReportView.cs', 'UpdateCheck.cs')
    $target = 'winexe'
}

if (-not $PSBoundParameters.ContainsKey('OutputName')) {
    $OutputName = if ($Gui) { 'CLean-GUI.exe' } else { 'CLean.exe' }
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
$stageExe = 'CLean.exe'

Write-Host '=================================================================='
Write-Host (" Building single-file {0} launcher   v{1}" -f $(if ($Gui) { 'GUI' } else { 'console' }), $version)
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
Write-Host ("  version  : {0}   (read from VERSION)" -f $version)

# --- 2. collect the PowerShell payload -----------------------------------
$scripts = @(Get-ChildItem -LiteralPath $root -Filter '*.ps1' -File | Sort-Object Name)
if ($scripts.Count -eq 0) { throw ("no .ps1 scripts found in {0}" -f $root) }
Write-Host ("  embedding: {0} script(s)" -f $scripts.Count)
foreach ($s in $scripts) { Write-Host ("             - {0}" -f $s.Name) }

# --- 3. stage everything under an ASCII-only path ------------------------
$stage = Join-Path $env:TEMP ('clean-build-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
if ($stage -match '[^\x20-\x7E]') {
    throw ("staging path is not ASCII-only: {0}" -f $stage)
}
Write-Host ("  staging  : {0}" -f $stage)

New-Item -ItemType Directory -Path $stage -Force | Out-Null

try {
    Copy-Item -LiteralPath $srcFile                 -Destination $stage
    Copy-Item -LiteralPath (Join-Path $srcDir 'app.manifest') -Destination $stage
    Copy-Item -LiteralPath $iconPath                -Destination (Join-Path $stage $iconName)
    foreach ($s in $scripts) { Copy-Item -LiteralPath $s.FullName -Destination $stage }

    # --- 3b. generate the version metadata for this build -----------------
    # Written straight into the staging area: it is a build product, so it can
    # never be committed and can never drift away from VERSION.
    $versionInfo = @"
// Generated by src/Build.ps1 from VERSION ($version). Do not edit, do not commit.
// Assembly attributes must precede every type declaration in the file (CS1730).
[assembly: System.Reflection.AssemblyTitle("$product")]
[assembly: System.Reflection.AssemblyProduct("$product")]
[assembly: System.Reflection.AssemblyCompany("$publisher")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (C) $publisher")]
[assembly: System.Reflection.AssemblyVersion("$version4")]
[assembly: System.Reflection.AssemblyFileVersion("$version4")]
[assembly: System.Reflection.AssemblyInformationalVersion("$version")]

internal static class VersionInfo
{
    internal const string Version    = "$version";
    internal const string Product    = "$product";
    internal const string Publisher  = "$publisher";
    internal const string ProjectUrl = "$repoUrl";
}
"@
    $viPath = Join-Path $stage 'VersionInfo.cs'
    [System.IO.File]::WriteAllText($viPath, $versionInfo, (New-Object System.Text.UTF8Encoding($false)))

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
            ('/win32icon:{0}' -f $iconName)
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

        # --- the project-cache names, generated from the script that owns them -----
        # The GUI's scanner has to know these names in C#, and a second hand-kept copy is
        # exactly what drifted before (the C# list knew .tox but not .nuget). Same trick as
        # VersionInfo.cs: read the single source, emit a constant into the staging
        # directory, never commit it.
        $catalogPath = Join-Path $root '_SystemCatalog.ps1'
        if (-not (Test-Path -LiteralPath $catalogPath)) { throw ("missing: {0}" -f $catalogPath) }
        . $catalogPath
        $cacheNames = @(Get-ProjectCacheNames)
        if ($cacheNames.Count -lt 5) {
            throw ("Get-ProjectCacheNames returned {0} names - refusing to generate the C# list" -f $cacheNames.Count)
        }
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.AppendLine('// GENERATED by src\Build.ps1 from _SystemCatalog.ps1 -> Get-ProjectCacheNames.')
        [void]$sb.AppendLine('// Do not edit and do not add a second copy: the script is the single source.')
        [void]$sb.AppendLine('internal static class ProjectCacheNames')
        [void]$sb.AppendLine('{')
        [void]$sb.AppendLine('    internal static readonly string[] Names = new string[]')
        [void]$sb.AppendLine('    {')
        for ($i = 0; $i -lt $cacheNames.Count; $i++) {
            $tail = if ($i -lt ($cacheNames.Count - 1)) { ',' } else { '' }
            [void]$sb.AppendLine(('        "{0}"{1}' -f $cacheNames[$i], $tail))
        }
        [void]$sb.AppendLine('    };')
        [void]$sb.AppendLine('}')
        [System.IO.File]::WriteAllText((Join-Path $stage 'ProjectCacheNames.cs'), $sb.ToString(),
            (New-Object System.Text.UTF8Encoding($false)))
        [void]$cscArgs.Add('ProjectCacheNames.cs')
        Write-Host ("  cache names: {0} generated from _SystemCatalog.ps1" -f $cacheNames.Count)
    }

    foreach ($s in $scripts) {
        # resource name is the bare file name, which is what the launcher looks for
        [void]$cscArgs.Add(('/resource:{0},{1}' -f $s.Name, $s.Name))
    }
    [void]$cscArgs.Add('VersionInfo.cs')
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

    # The update check compares against the version baked into the binary, so a
    # wrong one would make it lie. Read it back out of the compiled metadata.
    $asmVersion = $asm.GetName().Version.ToString()
    if ($asmVersion -ne $version4) {
        throw ("baked-in assembly version is {0}, expected {1}" -f $asmVersion, $version4)
    }

    # --- 5b. verify the icon really landed in the binary ------------------
    # /win32icon takes a path and says nothing when the path is wrong or the frames are
    # unusable, and a missing icon is easy to miss by eye. csc copies each frame of the
    # .ico verbatim into an RT_ICON resource, so the exact byte image of the largest
    # frame has to be somewhere in the PE - unless it is not.
    $icoBytes = [System.IO.File]::ReadAllBytes($iconPath)
    $frame    = Get-IcoFrame -Bytes $icoBytes
    $exeBytes = [System.IO.File]::ReadAllBytes($stageOut)
    $iconAt   = Find-ByteSequence -Haystack $exeBytes -Needle $frame.Bytes
    if ($iconAt -lt 0) {
        throw ("the {0}px icon frame is not in the compiled exe - /win32icon did not take effect" -f $frame.Width)
    }

    # --- 6. publish under the real (possibly non-ASCII) name --------------
    Copy-Item -LiteralPath $stageOut -Destination $outPath -Force

    Write-Host ''
    Write-Host '------------------------------------------------------------------'
    Write-Host ("  target : {0}  ({1})" -f $sourceFile, $target)
    Write-Host ("  output : {0}" -f $outPath)
    Write-Host ("  version: {0}  (assembly {1})" -f $version, $asmVersion)
    Write-Host ("  size   : {0:N0} bytes" -f (Get-Item -LiteralPath $outPath).Length)
    Write-Host ("  icon   : {0}  ({1}px frame found at PE offset {2}, {3} frames total)" -f
        (Split-Path -Leaf $iconPath), $frame.Width, $iconAt, ([BitConverter]::ToUInt16($icoBytes, 4)))
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
