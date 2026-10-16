# CLean regression tests.
#
# These cover the parts that broke in the field and are easy to break again: the fast size
# walker (must agree with the old Get-ChildItem sum), the delete path when files are held
# open, the per-tree safety valve, the noise floor on the net-gain guard, the report writer,
# the skipped-item log (batching + rotation), and script hygiene.
#
#   Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
#   .\src\Tests.ps1
#
# Everything runs against throwaway fixtures under %TEMP%; the only shared file touched is
# reports\skipped.log (gitignored), which is backed up and restored. Exit code 1 on failure.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $repo '_Common.ps1')

$script:Pass = 0
$script:Fail = 0

function Check {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    if ($Ok) { $script:Pass++; Write-Host ("  [PASS] {0}" -f $Name) }
    else     { $script:Fail++; Write-Host ("  [FAIL] {0} {1}" -f $Name, $Detail) }
}

function New-Fixture {
    param([string]$Name)
    $p = Join-Path $env:TEMP $Name
    if (Test-Path $p) { Remove-Item $p -Recurse -Force -EA SilentlyContinue }
    New-Item -ItemType Directory -Path $p -Force | Out-Null
    return $p
}

Write-Host '=== 1. fast size walker agrees with the old implementation ==='
$root = New-Fixture 'clean-t-size'
New-Item -ItemType Directory -Path (Join-Path $root 'a\b') -Force | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $root 'a\one.bin'), (New-Object byte[] 4096))
[System.IO.File]::WriteAllBytes((Join-Path $root 'a\b\two.bin'), (New-Object byte[] 8192))
$old = [int64]0
foreach ($f in (Get-ChildItem -LiteralPath $root -Recurse -Force -File -EA SilentlyContinue)) { $old += $f.Length }
$new = Get-PathBytes -Path $root
Check 'Get-PathBytes == Get-ChildItem sum' ($old -eq $new) "(old=$old new=$new)"
$map = Get-DirSizes -Path $root
Check 'Get-DirSizes root total matches' ($map[$root] -eq $new) "(map=$($map[$root]))"
# Keys are long-form (DirectoryInfo.FullName), while %TEMP% hands us the 8.3 short form:
# the walker must resolve the root, and the caller's own spelling must still work as an alias.
# Mixing the two shapes silently missed lookups until this test caught it.
$rootLong = (New-Object System.IO.DirectoryInfo($root)).FullName
Check 'Get-DirSizes has the nested dir (long form)' ($map.ContainsKey((Join-Path $rootLong 'a\b')))
Check 'Get-DirSizes keeps the short-form root usable' ($map.ContainsKey($root))

Write-Host '=== 2. delete path: one file held open inside a tree ==='
$tree = Join-Path $root 'held'
New-Item -ItemType Directory -Path $tree -Force | Out-Null
[System.IO.File]::WriteAllText((Join-Path $tree 'free1.bin'), 'x')
[System.IO.File]::WriteAllText((Join-Path $tree 'free2.bin'), 'x')
[System.IO.File]::WriteAllText((Join-Path $tree 'held.bin'), 'x')
$h = [System.IO.File]::Open((Join-Path $tree 'held.bin'), 'Open', 'ReadWrite', 'None')
Add-AllowedRoot @($root)
$r = Remove-TargetGuarded -Path $root -Include @('*') -Mode children -Execute -Label 't2'
$h.Close()
$left = @(Get-ChildItem $tree -File -EA SilentlyContinue | ForEach-Object { $_.Name })
Check 'unlocked files were still removed' (($left -notcontains 'free1.bin') -and ($left -notcontains 'free2.bin')) "(left: $($left -join ','))"
Check 'the in-use file was kept' ($left -contains 'held.bin')
Check 'only the in-use file counted as locked' ($r.Locked -eq 1) "(locked=$($r.Locked))"

Write-Host '=== 3. safety valve: a whole tree denied item by item ==='
$valve = New-Fixture 'clean-t-valve'
$sub = Join-Path $valve 'sub'
New-Item -ItemType Directory -Path $sub -Force | Out-Null
$handles = @()
for ($i = 1; $i -le 250; $i++) {
    $p = Join-Path $sub ("held{0:d3}.bin" -f $i)
    [System.IO.File]::WriteAllText($p, 'x')
    $handles += [System.IO.File]::Open($p, 'Open', 'ReadWrite', 'None')
}
Add-AllowedRoot @($valve)
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$rv = Remove-TargetGuarded -Path $valve -Include @('*') -Mode children -Execute -Label 't3'
$sw.Stop()
foreach ($x in $handles) { $x.Close() }
Check 'valve stopped at 200 instead of grinding 250' ($rv.Locked -eq 200) "(locked=$($rv.Locked))"
Check 'valve finished quickly' ($sw.Elapsed.TotalSeconds -lt 30) "($([int]$sw.Elapsed.TotalSeconds)s)"
Remove-Item $valve -Recurse -Force -EA SilentlyContinue

Write-Host '=== 4. net-gain guard: noise floor and attribution ==='
$MB = 1MB
$g = Start-NetGainGuard -Drive 'C:'
$g.CurrentFree = $g.StartFree + [int64](3.5 * $MB)
$null = Step-NetGainGuard -Label 'removed-nothing' -AbortIfNegative -RemovedItems 0
Check '3.5 MB drop with 0 items removed does not abort' (-not $g.Aborted)
$g.CurrentFree = $g.StartFree + [int64](3.5 * $MB)
$null = Step-NetGainGuard -Label 'small' -AbortIfNegative -RemovedItems 500
Check '3.5 MB drop after removals stays within the noise floor' (-not $g.Aborted)
$g.CurrentFree = $g.StartFree + [int64](200 * $MB)
$null = Step-NetGainGuard -Label 'real-regression' -AbortIfNegative -RemovedItems 500
Check '200 MB drop after removals DOES abort' ($g.Aborted)
$g2 = Start-NetGainGuard -Drive 'C:'
$g2.StartFree = (Get-FreeBytes -Drive 'C:') + [int64](3.5 * $MB)
$v = Stop-NetGainGuard
Check 'whole-run -3.5 MB verdict is not REGRESSION' ($v.Verdict -ne 'REGRESSION') "(verdict=$($v.Verdict))"
$script:NetGuard = $null

Write-Host '=== 5. report writer survives a locked report file ==='
$repDir = New-Fixture 'clean-t-rep'
$rep = Join-Path $repDir 'system.json'
$w = Write-ReportText -Path $rep -Text '{"n":1}'
Check 'plain write works' ((Test-Path $rep) -and ((Get-Content $rep -Raw) -match '"n":1')) "(-> $w)"
$hold = [System.IO.File]::Open($rep, 'Open', 'Read', 'Read')      # reader without share-delete
$w2 = Write-ReportText -Path $rep -Text '{"n":2}'
$hold.Close()
Check 'write while the report is held open still lands somewhere' ($w2 -ne $null) "(-> $w2)"
$landed = (Test-Path $w2) -and ((Get-Content $w2 -Raw) -match '"n":2')
Check 'the numbers were preserved' $landed "(-> $w2)"
Remove-Item $repDir -Recurse -Force -EA SilentlyContinue

Write-Host '=== 6. skipped-item log: UTF-8, batched, rotates at 16 MB ==='
$log = Join-Path $repo 'reports\skipped.log'
$backup = $null
if (Test-Path $log) { $backup = [System.IO.File]::ReadAllBytes($log) }
$dir = Split-Path -Parent $log
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
# A CJK message, assembled from code points so THIS file stays pure ASCII: Windows
# PowerShell 5.1 reads a BOM-less .ps1 as ANSI, where a literal would be mojibake - and
# in 5.1 the mojibake ate a quote and the whole suite failed to parse. The round trip
# below is still a real UTF-8 test: the text is written by Add-SkipLog and read back.
$cjk = -join @(0x5BF9, 0x8DEF, 0x5F84, 0x7684, 0x8BBF, 0x95EE, 0x88AB, 0x62D2, 0x7EDD |
        ForEach-Object { [char]$_ })
$script:SkipLogBuffer.Clear()
for ($i = 0; $i -lt 12; $i++) { Add-SkipLog ("PROBE`t{0}`t{1}" -f $i, $cjk) }
Flush-SkipLog
$txt = Get-Content $log -Raw -Encoding UTF8
Check 'batched lines were written' ($txt -match 'PROBE' -and $txt -match '11')
Check 'non-ASCII survives as UTF-8' ($txt -match $cjk)
[System.IO.File]::WriteAllBytes($log, (New-Object byte[] (17MB)))
$script:SkipLogBuffer.Clear()
Add-SkipLog ("ROTATE-PROBE`tt`tnow")
Flush-SkipLog
Check 'rotated to .1 at 16 MB' (Test-Path "$log.1")
Check 'new line landed in the fresh file' ((Get-Content $log -Raw -Encoding UTF8) -match 'ROTATE-PROBE')
Remove-Item "$log.1" -Force -EA SilentlyContinue
if ($backup) { [System.IO.File]::WriteAllBytes($log, $backup) } else { Remove-Item $log -Force -EA SilentlyContinue }

Write-Host '=== 7. script hygiene: ASCII + LF only (Windows PowerShell 5.1 reads ANSI without a BOM) ==='
$bad = @()
# src\ is scanned as well: this check used to stop at the repo root, and that blind spot is
# exactly how a BOM-less UTF-8 Tests.ps1 carrying Chinese shipped and could not be parsed
# at all by Windows PowerShell 5.1 - the shell the repo builds and runs on.
$targets = @(Get-ChildItem $repo -Filter *.ps1 -File) +
           @(Get-ChildItem (Join-Path $repo 'src') -Filter *.ps1 -File)
foreach ($f in $targets) {
    $b = [System.IO.File]::ReadAllBytes($f.FullName)
    $na = 0; $crlf = 0
    for ($i = 0; $i -lt $b.Length; $i++) {
        if ($b[$i] -gt 127) { $na++ }
        if ($b[$i] -eq 10 -and $i -gt 0 -and $b[$i - 1] -eq 13) { $crlf++ }
    }
    if ($na -gt 0 -or $crlf -gt 0) { $bad += ("{0} (non-ASCII={1} CRLF={2})" -f $f.Name, $na, $crlf) }
}
Check 'every script is pure ASCII and LF-only' ($bad.Count -eq 0) ($bad -join '; ')

Write-Host '=== 8. program icon: the assets themselves ==='
Add-Type -AssemblyName System.Drawing

function Get-AlphaBounds {
    # Margins between the drawn pixels (alpha > 8) and each edge of the image. LockBits
    # rather than 65k GetPixel calls, which in PowerShell would dominate the suite.
    param([string]$Path)
    $bmp = [System.Drawing.Bitmap]::FromFile($Path)
    try {
        $w = $bmp.Width; $h = $bmp.Height
        $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
        $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $stride = $data.Stride
            $buf = New-Object byte[] ($stride * $h)
            [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
        } finally { $bmp.UnlockBits($data) }
        $l = $w; $r = -1; $t = $h; $b = -1
        for ($y = 0; $y -lt $h; $y++) {
            $row = $y * $stride
            for ($x = 0; $x -lt $w; $x++) {
                if ($buf[$row + $x * 4 + 3] -gt 8) {
                    if ($x -lt $l) { $l = $x }
                    if ($x -gt $r) { $r = $x }
                    if ($y -lt $t) { $t = $y }
                    if ($y -gt $b) { $b = $y }
                }
            }
        }
        return @{ Left = $l; Right = ($w - 1 - $r); Top = $t; Bottom = ($h - 1 - $b) }
    } finally { $bmp.Dispose() }
}

$icoPath      = Join-Path $repo 'assets\CLean.ico'
$iconPng      = Join-Path $repo 'assets\CLean-icon-256.png'
$iconPngRound = Join-Path $repo 'assets\CLean-icon-rounded-256.png'
Check 'the three icon files are in the repo' ((Test-Path $icoPath) -and (Test-Path $iconPng) -and (Test-Path $iconPngRound))

# Windows picks a frame per size: 16 = tree/list, 32 = desktop, 48 = medium icons,
# 256 = extra large + the shell's jumbo view. Drop any of those and the shell starts
# rescaling (blurry) instead of picking the frame drawn for that size.
$icoSizes = @()
if (Test-Path $icoPath) {
    $icoBytes = [System.IO.File]::ReadAllBytes($icoPath)
    $icoCount = [BitConverter]::ToUInt16($icoBytes, 4)
    for ($i = 0; $i -lt $icoCount; $i++) {
        $w = [int]$icoBytes[6 + $i * 16]
        if ($w -eq 0) { $w = 256 }
        $icoSizes += $w
    }
}
$needSizes = @(16, 32, 48, 256)
$goneSizes = @($needSizes | Where-Object { $icoSizes -notcontains $_ })
Check 'the ico carries every size the shell asks for' ($goneSizes.Count -eq 0) "(has: $($icoSizes -join ','))"
Check 'the ico tops out at 256px' (($icoSizes | Measure-Object -Maximum).Maximum -eq 256) "(has: $($icoSizes -join ','))"

$pngImg = [System.Drawing.Bitmap]::FromFile($iconPng)
try {
    Check 'the icon png is 256x256' (($pngImg.Width -eq 256) -and ($pngImg.Height -eq 256)) "($($pngImg.Width)x$($pngImg.Height))"
    Check 'the icon png really has an alpha channel' ($pngImg.PixelFormat -eq [System.Drawing.Imaging.PixelFormat]::Format32bppArgb) "($($pngImg.PixelFormat))"
    Check 'the icon png corners are fully transparent' (($pngImg.GetPixel(0, 0).A -eq 0) -and ($pngImg.GetPixel(255, 0).A -eq 0) -and
        ($pngImg.GetPixel(0, 255).A -eq 0) -and ($pngImg.GetPixel(255, 255).A -eq 0))
    Check 'the icon png centre is opaque' ($pngImg.GetPixel(128, 128).A -eq 255)
} finally { $pngImg.Dispose() }

$eb = Get-AlphaBounds $iconPng
Check 'the artwork clears all four edges (nothing got cropped)' (($eb.Left -ge 4) -and ($eb.Right -ge 4) -and ($eb.Top -ge 4) -and ($eb.Bottom -ge 4)) "(L=$($eb.Left) R=$($eb.Right) T=$($eb.Top) B=$($eb.Bottom))"
Check 'the artwork is centred (opposite margins within 4px)' (([Math]::Abs($eb.Left - $eb.Right) -le 4) -and ([Math]::Abs($eb.Top - $eb.Bottom) -le 4)) "(L=$($eb.Left) R=$($eb.Right) T=$($eb.Top) B=$($eb.Bottom))"

$rb = [System.Drawing.Bitmap]::FromFile($iconPngRound)
try {
    Check 'the rounded variant has transparent corners' (($rb.GetPixel(0, 0).A -eq 0) -and ($rb.GetPixel(255, 255).A -eq 0))
    Check 'the rounded variant still covers the edge midpoints' (($rb.GetPixel(0, 128).A -eq 255) -and ($rb.GetPixel(128, 0).A -eq 255))
} finally { $rb.Dispose() }

# The icon must reach the users through ONE file: Build.ps1 embeds it, CLean.iss points
# SetupIconFile at it. A second hard-coded path is how the two would drift apart.
$buildSrc = Get-Content (Join-Path $repo 'src\Build.ps1') -Raw
$issSrc = Get-Content (Join-Path $repo 'installer\CLean.iss') -Raw
Check 'Build.ps1 embeds the icon with /win32icon' ($buildSrc -match '/win32icon') 
Check 'Build.ps1 reads assets\CLean.ico' ($buildSrc -match 'assets\\CLean\.ico')
Check 'CLean.iss uses the same file for Setup' ($issSrc -match 'SetupIconFile=\.\.\\assets\\CLean\.ico')

Write-Host '=== 9. the compiled exe carries that icon (ask Windows, not the bytes) ==='
# "The build script looks right" is not evidence, so compile the console target and let
# Windows read the icon back out of the PE. A PE with no icon resources hands out the
# generic blue Windows icon (rgb 13,125,204 as measured here); a red centre can only
# come from our own artwork.
$probeExe = Join-Path $repo 'CLean-test-icon.exe'
Remove-Item $probeExe -Force -EA SilentlyContinue
$buildOk = $true
$buildErr = ''
try { & (Join-Path $repo 'src\Build.ps1') -OutputName 'CLean-test-icon.exe' | Out-Null }
catch { $buildOk = $false; $buildErr = $_.Exception.Message }
Check 'Build.ps1 compiles the console target' $buildOk $buildErr
if ($buildOk) {
    $exeIcon = [System.Drawing.Icon]::ExtractAssociatedIcon($probeExe)
    Check 'the exe carries an icon resource' ($exeIcon -ne $null)
    if ($exeIcon) {
        $px = $exeIcon.ToBitmap().GetPixel(16, 16)
        Check 'the exe icon is our artwork, not the Windows default' (($px.R -gt 100) -and ($px.G -lt 70) -and ($px.B -lt 70)) "(rgb=$($px.R),$($px.G),$($px.B))"
    }
}
Remove-Item $probeExe -Force -EA SilentlyContinue

Write-Host '=== 10. motion: tokens, curves and the reduced-motion gate ==='
# Three things can rot here without anyone noticing: Build.ps1 stops compiling Motion.cs,
# someone "tidies" a duration, or the easing maths quietly becomes an approximation. The
# durations are checked as literals (they are a documented contract, see README) and the
# curves are checked by compiling Motion.cs alone - only Theme.Mix may come from outside,
# which the minimal stub in the probe enforces.
$motionSrc = Join-Path $repo 'src\Motion.cs'
Check 'src\Motion.cs exists' (Test-Path $motionSrc)
$mSrc = Get-Content $motionSrc -Raw
Check 'Build.ps1 compiles Motion.cs into the GUI' ($buildSrc -match "extraSources = @\([^)]*'Motion\.cs'")

$mBytes = [System.IO.File]::ReadAllBytes($motionSrc)
Check 'Motion.cs is UTF-8 without a BOM' (-not (($mBytes[0] -eq 0xEF) -and ($mBytes[1] -eq 0xBB) -and ($mBytes[2] -eq 0xBF)))
Check 'Motion.cs uses LF line endings like its neighbours' ([Array]::IndexOf($mBytes, [byte]13) -lt 0)

foreach ($tok in @(@('DurPress', 110), @('DurHover', 140), @('DurToggle', 170), @('DurRail', 190),
                    @('DurPanel', 210), @('DurPage', 200), @('DurData', 420))) {
    Check ("Motion." + $tok[0] + " is still " + $tok[1] + "ms") ($mSrc -match ("const int " + $tok[0] + " = " + $tok[1] + ";"))
}
Check 'the audit curve is still cubic-bezier(0.23, 1, 0.32, 1)' ($mSrc -match '0\.23, 1, 0\.32, 1')
Check 'the travel curve is still cubic-bezier(0.77, 0, 0.175, 1)' ($mSrc -match '0\.77, 0, 0\.175, 1')
Check 'reduced motion keeps colour but drops movement' (($mSrc -match 'Reduced \? Math\.Min\(ms, 90\) : ms') -and ($mSrc -match 'Reduced \? 0 : ms'))

$probeDir = New-Fixture 'clean-t-motion'
$probeCs = Join-Path $probeDir 'MotionProbe.cs'
$probeExe = Join-Path $probeDir 'motion-probe.exe'
$probeCode = @'
using System;
using System.Drawing;
using System.Threading;

// The only things Motion.cs is allowed to need from the GUI layer: one colour lerp and the
// stored preference. Anything else it starts reaching for shows up here as a compile error.
internal static class Theme
{
    internal static Color Mix(Color a, Color b, float t)
    {
        if (t < 0f) t = 0f;
        if (t > 1f) t = 1f;
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }
}

internal static class Settings
{
    internal static string MotionPreference = "auto";
}

internal static class MotionProbe
{
    private static int _fail;

    private static void Check(string name, bool ok, string detail)
    {
        if (!ok) { _fail++; Console.WriteLine("FAIL " + name + " " + detail); }
    }

    private static void Near(string name, double got, double want, double tol)
    {
        Check(name, Math.Abs(got - want) <= tol,
              "(got " + got.ToString("0.####") + " want " + want.ToString("0.####") + ")");
    }

    private static int Main()
    {
        Near("Out(0)", Motion.Apply(Ease.Out, 0.0), 0.0, 1e-9);
        Near("Out(1)", Motion.Apply(Ease.Out, 1.0), 1.0, 1e-9);
        Near("Out(0.5)", Motion.Apply(Ease.Out, 0.5), 0.9657, 0.002);
        Near("InOut(0.5)", Motion.Apply(Ease.InOut, 0.5), 0.5960, 0.002);
        Near("Linear(0.37)", Motion.Apply(Ease.Linear, 0.37), 0.37, 1e-9);
        Near("Clamp01(-1)", Motion.Clamp01(-1.0), 0.0, 1e-12);
        Near("Clamp01(2)", Motion.Clamp01(2.0), 1.0, 1e-12);

        bool mono = true;
        double prev = -1.0;
        for (int i = 0; i <= 200; i++)
        {
            double v = Motion.Apply(Ease.Out, i / 200.0);
            if (v < prev - 1e-9) mono = false;
            prev = v;
        }
        Check("Out never goes backwards", mono, "");

        mono = true; prev = -1.0;
        double peak = 0.0;
        for (int i = 0; i <= 200; i++)
        {
            double v = Motion.Apply(Ease.InOut, i / 200.0);
            if (v < prev - 1e-9) mono = false;
            prev = v;
            double b = Motion.Apply(Ease.OutBack, i / 200.0);
            if (b > peak) peak = b;
        }
        Check("InOut never goes backwards", mono, "");
        Check("OutBack overshoots then lands", (peak > 1.005) && (peak < 1.15), "(peak " + peak.ToString("0.####") + ")");
        Near("OutBack(1)", Motion.Apply(Ease.OutBack, 1.0), 1.0, 1e-9);

        Color red = Color.FromArgb(255, 0, 0);
        Color blue = Color.FromArgb(0, 0, 255);
        Check("Blend(0) is the first colour", Motion.Blend(red, blue, 0.0) == red, "");
        Check("Blend(1) is the second colour", Motion.Blend(red, blue, 1.0) == blue, "");
        Check("Blend clamps past the end", Motion.Blend(red, blue, -3.0) == red, "");
        Near("Blend(0.5) is halfway", Motion.Blend(red, blue, 0.5).R, 128.0, 1.0);

        Motion.Preference = "auto";
        Motion.ForceFull = true;
        Motion.ForceReduced = false;
        Check("auto while Windows animates is not reduced", !Motion.Reduced, "");
        Check("Dur passes through when animating", Motion.Dur(Motion.DurRail) == Motion.DurRail, "");
        Check("Soft passes through when animating", Motion.Soft(Motion.DurPanel) == Motion.DurPanel, "");

        Motion.ForceFull = false;
        Motion.ForceReduced = true;
        Check("the forced-reduced hook bites", Motion.Reduced, "");
        Check("reduced motion snaps movement to zero", Motion.Dur(Motion.DurRail) == 0, "");
        Check("reduced motion keeps colour, only shorter",
              (Motion.Soft(Motion.DurPanel) > 0) && (Motion.Soft(Motion.DurPanel) <= 90),
              "(soft " + Motion.Soft(Motion.DurPanel) + ")");

        Motion.ForceReduced = false;
        Motion.ForceFull = true;
        Motion.Preference = "on";
        Check("preference 'on' animates even with Windows off", !Motion.Reduced, "");
        Motion.Preference = "off";
        Check("preference 'off' animates nothing", Motion.Reduced, "");
        Motion.SetPreference("nonsense");
        Check("an unknown preference falls back to auto", Settings.MotionPreference == "auto", "");
        Motion.Preference = "auto";

        // A channel must be re-targetable mid-flight: that is the whole difference between
        // a transition and a keyframe replay.
        Channel ch = new Channel();
        ch.SnapTo(0.0);
        double t0 = Motion.Now;
        ch.To(1.0, 300, Ease.Linear);
        while (ch.Running && (Motion.Now - t0) < 150) { ch.Advance(); Thread.Sleep(1); }
        double mid = ch.Value;
        Check("the channel is on its way", (mid > 0.15) && (mid < 0.9), "(mid " + mid.ToString("0.###") + ")");
        ch.To(0.0, 300, Ease.Linear);
        double after = ch.Value;
        Check("re-targeting does not jump", Math.Abs(after - mid) < 0.2,
              "(mid " + mid.ToString("0.###") + " after " + after.ToString("0.###") + ")");
        while (ch.Running && (Motion.Now - t0) < 900) { ch.Advance(); Thread.Sleep(1); }
        Check("the channel settles where it was sent", Math.Abs(ch.Value) < 0.02, "(value " + ch.Value.ToString("0.###") + ")");
        ch.SnapTo(0.25);
        Check("SnapTo is immediate and stops the channel", (Math.Abs(ch.Value - 0.25) < 1e-9) && !ch.Running, "");

        Console.WriteLine(_fail == 0 ? "motion probe ok" : (_fail + " motion checks failed"));
        return _fail == 0 ? 0 : 1;
    }
}
'@
[System.IO.File]::WriteAllText($probeCs, $probeCode, (New-Object System.Text.UTF8Encoding($false)))

$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$compiled = $true
$compileOut = ''
$oldEAP = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $compileOut = (& $csc /nologo /target:exe /codepage:65001 /main:MotionProbe /out:$probeExe `
        /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll $motionSrc $probeCs 2>&1) -join ' | '
    $compiled = ($LASTEXITCODE -eq 0) -and (Test-Path $probeExe)
} catch { $compiled = $false; $compileOut = $_.Exception.Message }
$ErrorActionPreference = $oldEAP
Check 'Motion.cs compiles on its own (Theme.Mix is all it borrows)' $compiled $compileOut

if ($compiled) {
    $ErrorActionPreference = 'Continue'
    $probeOut = & $probeExe 2>&1
    $probeCode2 = $LASTEXITCODE
    $ErrorActionPreference = $oldEAP
    $probeOut | Where-Object { $_ -like 'FAIL*' } | ForEach-Object { Write-Host ("    " + $_) }
    Check 'the motion maths probe passes' ($probeCode2 -eq 0) "($($probeOut -join ' '))"
}
Remove-Item $probeDir -Recurse -Force -EA SilentlyContinue

Remove-Item $root -Recurse -Force -EA SilentlyContinue

Write-Host ''
Write-Host ("=== {0} passed, {1} failed ===" -f $script:Pass, $script:Fail)
if ($script:Fail -gt 0) { exit 1 }
exit 0
