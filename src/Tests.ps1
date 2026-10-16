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

Remove-Item $root -Recurse -Force -EA SilentlyContinue

Write-Host ''
Write-Host ("=== {0} passed, {1} failed ===" -f $script:Pass, $script:Fail)
if ($script:Fail -gt 0) { exit 1 }
exit 0
