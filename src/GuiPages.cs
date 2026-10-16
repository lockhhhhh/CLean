// CLean · 窗口版 -- pages, fast scanner and side windows.
//
// Split out of GuiLauncher.cs (v0.4) because the window outgrew "one card with a few
// option rows": every function is now its own page, the two scanning functions own a C#
// directory scanner instead of a PowerShell one, and the output log lives in its own
// window. Both files are compiled into the same single-file EXE by src\Build.ps1; the
// theme facade (Theme / Palette / IThemed) and the drawing primitives (Card, FlatButton,
// Pill, FlatMenuRenderer) still live in GuiLauncher.cs.
//
// Deliberately conservative C# 5 syntax, same as GuiLauncher.cs: the in-box csc.exe on
// older Windows builds may not accept newer language versions.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// ============================================================== output log bus

// One place every line goes. The log used to be a pane inside the window; it is now a
// separate window opened from a header button, so the lines must outlive that pane and
// survive it being closed - hence a ring buffer plus an event.
internal static class Log
{
    private const int Capacity = 4000;

    private static readonly List<string> _lines = new List<string>();
    private static int _unread;
    private static bool _open;

    // Raised on the UI thread for every appended line (callers marshal first).
    internal static event Action<string> Line;

    internal static int Unread { get { return _unread; } }

    private static bool Open
    {
        get { return _open; }
        set { _open = value; if (value) _unread = 0; }
    }

    internal static void MarkOpen(bool isOpen) { _open = isOpen; if (isOpen) _unread = 0; }

    internal static void Append(string line)
    {
        if (line == null) return;
        lock (_lines)
        {
            _lines.Add(line);
            if (_lines.Count > Capacity) _lines.RemoveRange(0, _lines.Count - Capacity);
        }
        if (!_open) _unread++;

        Action<string> handler = Line;
        if (handler != null)
        {
            try { handler(line); }
            catch { /* a listener must never break a run */ }
        }
    }

    internal static void Clear()
    {
        lock (_lines) _lines.Clear();
        _unread = 0;
    }

    internal static string[] Snapshot()
    {
        lock (_lines) return _lines.ToArray();
    }
}

// ==================================================================== settings

// ONE key=value file for every UI preference (%LOCALAPPDATA%\CLean\settings.txt).
//
// There used to be two: theme.txt (read by Theme in GuiLauncher.cs) and ui.txt (read by
// Prefs here), each with its own hand-rolled reader doing exactly the same thing. The
// format stays deliberately boring - ASCII "key=value" lines - so a missing, damaged or
// read-only file is never an error: the defaults stand, and deleting the file returns the
// tool to its factory state.
//
// theme.txt / ui.txt are still honoured ONCE, when settings.txt does not exist yet, so
// upgrading does not silently reset somebody's theme or rail state.
internal static class Settings
{
    private static readonly Dictionary<string, string> _values =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal static string FilePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CLean", "settings.txt");
    }

    internal static void Load()
    {
        _values.Clear();
        string p = FilePath();
        if (File.Exists(p)) { ReadInto(p); return; }

        // One-time migration from the two v1.0 files.
        string dir = Path.GetDirectoryName(p);
        bool migrated = false;
        if (!String.IsNullOrEmpty(dir))
        {
            if (ReadInto(Path.Combine(dir, "theme.txt"))) migrated = true;
            if (ReadInto(Path.Combine(dir, "ui.txt"))) migrated = true;
        }
        if (migrated) Save();
    }

    // Returns true when the file existed and contributed at least one key.
    private static bool ReadInto(string path)
    {
        bool any = false;
        try
        {
            if (!File.Exists(path)) return false;
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string s = lines[i].Trim();
                int eq = s.IndexOf('=');
                if (eq <= 0) continue;
                string k = s.Substring(0, eq).Trim().ToLowerInvariant();
                string v = s.Substring(eq + 1).Trim().ToLowerInvariant();
                if (k.Length == 0) continue;
                _values[k] = v;
                any = true;
            }
        }
        catch { /* this file is unreadable; the others still count */ }
        return any;
    }

    internal static string Get(string key, string fallback)
    {
        string v;
        return _values.TryGetValue(key, out v) ? v : fallback;
    }

    internal static void Set(string key, string value)
    {
        _values[key] = value;
        Save();
    }

    private static void Save()
    {
        try
        {
            string p = FilePath();
            string dir = Path.GetDirectoryName(p);
            if (!String.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Sorted, so the file is stable and hand-diffable. The key set is tiny.
            List<string> keys = new List<string>(_values.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < keys.Count; i++)
                sb.Append(keys[i]).Append('=').Append(_values[keys[i]]).Append("\r\n");
            File.WriteAllText(p, sb.ToString(), new UTF8Encoding(false));
        }
        catch { /* a read-only profile must not break the UI */ }
    }

    // ---- the function rail's collapsed state (was: Prefs) -------------------
    internal static bool RailExpanded
    {
        get { return Get("rail", "expanded") != "collapsed"; }
        set { Set("rail", value ? "expanded" : "collapsed"); }
    }

    // ---- the window's animation preference (see Motion.Preference) ----------
    // Kept as a plain key here rather than a bool: "auto" is the default and means
    // "follow Windows", which is not the same as either "on" or "off".
    internal static string MotionPreference
    {
        get { return Get("motion", "auto"); }
        set { Set("motion", value); }
    }
}

// ================================================================ cleanup ledger

// The cleanup ledger: every cleanup report the tool has written, in time order, next to
// what the disk's own free-space counter says NOW.
//
// This is the question the tool exists to answer and could never answer before: "上次清出来的
// 空间还在吗？" The numbers have been in the reports all along (FreeBeforeGB / FreeAfterGB /
// NetFreeMB / Verdict per run, plus the shadow-copy diff area, which is the usual suspect);
// nobody added them up.
//
// HONESTY NOTE, because this is the number people will quote: the last column is the change
// in the volume's free space SINCE that run, read from the same counter the cleanup itself
// is measured against. It is not attribution - Windows updates, browsers and page files
// move that number too. The column therefore says 之后的变化, and the ledger only claims
// what it can measure.
internal static class Ledger
{
    internal static string FilePath(string reportsDir)
    {
        return System.IO.Path.Combine(reportsDir, "ledger.json");
    }

    // Returns the number of cleanup runs found, or -1 when it could not be written.
    internal static int Build(string reportsDir, out string path)
    {
        path = FilePath(reportsDir);
        try
        {
            Directory.CreateDirectory(reportsDir);

            double freeNowGB = 0;
            try
            {
                DriveInfo di = new DriveInfo("C");
                if (di.IsReady) freeNowGB = di.AvailableFreeSpace / 1073741824.0;
            }
            catch { /* no drive info: leave 0 and say so below */ }

            string[] files = Directory.GetFiles(reportsDir, "*.json");
            List<string[]> rows = new List<string[]>();     // time, tag, phase, net, logical, freeAfter, verdict, shadow, since
            double cleanedMB = 0;
            int executed = 0;

            for (int i = 0; i < files.Length; i++)
            {
                string name = System.IO.Path.GetFileName(files[i]);
                if (String.Equals(name, "ledger.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (String.Equals(name, "scan-cli.json", StringComparison.OrdinalIgnoreCase)) continue;

                JNode root = JsonRead.LoadFile(files[i]);
                if (root == null) continue;

                // Only cleanup reports: they are the ones carrying a verdict.
                JNode verdict = root.Get("Verdict");
                if (verdict == null) continue;

                string time = Text(root.Get("Time"));
                string tag = Text(root.Get("Tag"));
                string mode = Text(root.Get("Mode"));
                bool dry = mode.IndexOf("dry", StringComparison.OrdinalIgnoreCase) >= 0
                        || String.Equals(verdict.Scalar, "dry-run", StringComparison.OrdinalIgnoreCase);

                JNode netNode = root.Get("NetFreeMB");
                double net = netNode != null ? netNode.AsDouble(0) : 0;
                if (!dry) { cleanedMB += net; executed++; }

                double freeAfter = 0;
                JNode fa = root.Get("FreeAfterGB");
                if (fa != null) freeAfter = fa.AsDouble(0);

                double shadowPct = -1;
                JNode guard = root.Get("NetGuard");
                if (guard != null)
                {
                    JNode pct = guard.Get("ShadowUsedPct");
                    if (pct != null) shadowPct = pct.AsDouble(-1);
                }

                string since = "";
                if (freeNowGB > 0 && freeAfter > 0)
                    since = (freeNowGB - freeAfter).ToString("0.00");

                rows.Add(new string[] {
                    time, tag, dry ? "预演" : "执行",
                    dry ? "" : net.ToString("0.0"),
                    Text(root.Get("LogicalMB")),
                    freeAfter > 0 ? freeAfter.ToString("0.00") : "",
                    verdict.Scalar,
                    shadowPct >= 0 ? shadowPct.ToString("0.0") : "",
                    since
                });
            }

            rows.Sort(delegate(string[] a, string[] b) { return String.CompareOrdinal(a[0], b[0]); });

            StringBuilder sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"Title\": \"清理账本（历次战果与之后的变化）\",\r\n");
            sb.Append("  \"Mode\": \"read-only ledger\",\r\n");
            sb.Append("  \"Time\": \"").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\",\r\n");
            // Single line on purpose: a raw newline inside a JSON string makes the whole
            // file unparseable, and this note is exactly the kind of text that wants to wrap.
            sb.Append("  \"Note\": \"之后的变化 = C 盘可用空间与该次运行结束时之差，来自卷自身的计数器；")
              .Append("Windows 更新、浏览器、页面文件也会改动它，所以它只说明变了多少，不说明是谁改的。\",\r\n");
            sb.Append("  \"FreeNowGB\": ").Append(freeNowGB.ToString("0.00")).Append(",\r\n");
            sb.Append("  \"CleanupRuns\": ").Append(executed).Append(",\r\n");
            sb.Append("  \"TotalNetFreedMB\": ").Append(cleanedMB.ToString("0.0")).Append(",\r\n");
            sb.Append("  \"Entries\": [\r\n");
            for (int i = 0; i < rows.Count; i++)
            {
                string[] r = rows[i];
                if (i > 0) sb.Append(",\r\n");
                sb.Append("    { \"Time\": \"").Append(Json.Escape(r[0])).Append("\"")
                  .Append(", \"Tag\": \"").Append(Json.Escape(r[1])).Append("\"")
                  .Append(", \"Phase\": \"").Append(Json.Escape(r[2])).Append("\"");
                if (r[3].Length > 0) sb.Append(", \"NetFreeMB\": ").Append(r[3]);
                if (r[4].Length > 0) sb.Append(", \"LogicalMB\": ").Append(r[4]);
                if (r[5].Length > 0) sb.Append(", \"FreeAfterGB\": ").Append(r[5]);
                sb.Append(", \"Verdict\": \"").Append(Json.Escape(r[6])).Append("\"");
                if (r[7].Length > 0) sb.Append(", \"ShadowUsedPct\": ").Append(r[7]);
                if (r[8].Length > 0) sb.Append(", \"SinceThenGB\": ").Append(r[8]);
                sb.Append(" }");
            }
            sb.Append("\r\n  ]\r\n}\r\n");

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return rows.Count;
        }
        catch
        {
            return -1;
        }
    }

    private static string Text(JNode n)
    {
        return n != null && n.Scalar != null ? n.Scalar : "";
    }
}

// ================================================================ fast scanner

// One scanned folder.
internal sealed class ScanHit
{
    internal string Path = "";
    internal long Bytes;
    internal long Files;
    internal bool Selected;

    internal double MB { get { return Bytes / 1048576.0; } }

    // Owner-drawn rows paint themselves, but the text also feeds UI Automation (and any
    // accessibility tool reading the list), so it must be the useful summary.
    public override string ToString()
    {
        return Fmt.Bytes(Bytes) + "  " + Path;
    }
}

// Result of a scan, in the order the UI shows it.
internal sealed class ScanOutcome
{
    internal string Root = "";
    internal long TotalBytes;
    internal long Files;
    internal long Dirs;
    internal int Denied;
    internal int ReparseSkipped;
    internal int DepthCapped;
    internal bool Cancelled;        // stopped before it covered everything it was asked to
    internal long ElapsedMs;
    internal int Threads;
    internal readonly List<ScanHit> Hits = new List<ScanHit>();

    // The largest single FILES seen during the same walk (Files = 1). Kept next to the
    // directory hits because "which folder is big" and "which file is big" are the same
    // answer recorded at two granularities, and the walk already sees every file.
    internal readonly List<ScanHit> TopFiles = new List<ScanHit>();

    internal double TotalMB { get { return TotalBytes / 1048576.0; } }
}

// Live counters for one running scan. The UI polls these from a timer instead of being
// called back from worker threads, which keeps every cross-thread concern in one place:
// plain Interlocked counters plus one volatile string.
internal sealed class ScanRun
{
    private long _files;
    private long _dirs;
    private long _bytes;
    private int _denied;
    private int _reparse;
    private int _depthCapped;
    private volatile string _current = "";
    private volatile bool _cancel;

    // Live hits, so the page's animation can show folders/bars appearing while the scan
    // is still running (the final list is still built separately at the end).
    private readonly List<ScanHit> _live = new List<ScanHit>();
    private readonly object _liveGate = new object();

    internal readonly string Root;
    internal readonly DateTime Started = DateTime.Now;

    internal ScanRun(string root) { Root = root; }

    internal void ReportHit(string path, long bytes)
    {
        lock (_liveGate)
        {
            for (int i = 0; i < _live.Count; i++)
                if (String.Equals(_live[i].Path, path, StringComparison.OrdinalIgnoreCase)) return;
            if (_live.Count >= 24) return;

            ScanHit hit = new ScanHit();
            hit.Path = path;
            hit.Bytes = bytes;
            _live.Add(hit);
        }
    }

    internal List<ScanHit> LiveHits()
    {
        lock (_liveGate)
        {
            List<ScanHit> copy = new List<ScanHit>(_live);
            copy.Sort(delegate(ScanHit a, ScanHit b) { return b.Bytes.CompareTo(a.Bytes); });
            return copy;
        }
    }

    internal long Files { get { return Interlocked.Read(ref _files); } }
    internal long Dirs { get { return Interlocked.Read(ref _dirs); } }
    internal long Bytes { get { return Interlocked.Read(ref _bytes); } }
    internal int Denied { get { return _denied; } }
    internal int ReparseSkipped { get { return _reparse; } }
    internal int DepthCapped { get { return _depthCapped; } }
    internal string Current { get { return _current; } }
    internal bool Cancelled { get { return _cancel; } }
    internal long ElapsedMs { get { return (long)(DateTime.Now - Started).TotalMilliseconds; } }

    internal void Cancel() { _cancel = true; }

    internal void AddFile(long bytes)
    {
        Interlocked.Increment(ref _files);
        Interlocked.Add(ref _bytes, bytes);
    }

    internal void AddDir() { Interlocked.Increment(ref _dirs); }
    internal void AddDenied() { Interlocked.Increment(ref _denied); }
    internal void AddReparse() { Interlocked.Increment(ref _reparse); }
    internal void AddDepthCapped() { Interlocked.Increment(ref _depthCapped); }
    internal void SetCurrent(string dir) { _current = dir; }
}

// --------------------------------------------------------------------------------
// WHY A C# SCANNER INSTEAD OF THE .ps1 ONE
//
// 05-Scan-LargeDirs.ps1 walks the tree with Get-ChildItem: every entry becomes a
// FileSystemInfo object, which costs far more than the directory read itself. This
// scanner calls FindFirstFileEx with FindExInfoBasic + FIND_FIRST_EX_LARGE_FETCH (the
// trick WizTree/Everything use: no per-entry open, 64 KB directory buffers) and runs
// independent subtrees in parallel.
//
// ACCURACY: each entry's size is read from the same WIN32_FIND_DATA field that
// FileInfo.Length exposes, so totals match `Get-ChildItem -Recurse | Measure Length` for
// the same tree (verified against the PowerShell scanner, see README). Reparse points
// are counted but never descended into, exactly like the .ps1 scripts: following a
// junction would count somebody else's bytes twice.
// --------------------------------------------------------------------------------
internal static class FastScan
{
    private const int FindExInfoBasic = 1;
    private const int FindExSearchNameMatch = 0;
    private const int FindFirstExLargeFetch = 2;
    internal const int MaxDepthHardCap = 512;

    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATA
    {
        internal uint dwFileAttributes;
        internal uint ftCreationTimeLow, ftCreationTimeHigh;
        internal uint ftLastAccessTimeLow, ftLastAccessTimeHigh;
        internal uint ftLastWriteTimeLow, ftLastWriteTimeHigh;
        internal uint nFileSizeHigh;
        internal uint nFileSizeLow;
        internal uint dwReserved0;
        internal uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        internal string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileEx(string lpFileName, int fInfoLevelId,
        out WIN32_FIND_DATA lpFindFileData, int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextFile(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr hFindFile);

    internal sealed class Entry
    {
        internal string Name;
        internal long Size;
        internal bool IsDir;
        internal bool IsReparse;
    }

    // WHY THE \\?\ PREFIX: paths inside a node_modules tree routinely pass MAX_PATH, and
    // the verbatim prefix is the only way to reach them without a longPathAware manifest
    // (which needs a newer OS). The prefix is a property of the CALL only - every path
    // that leaves this class is stored and shown in its plain form, because both the user
    // and the deleter (04-Clean-Hidden.ps1) need the ordinary path.
    private static string ForApi(string dir)
    {
        return (dir.Length > 240) ? (@"\\?\" + dir) : dir;
    }

    private static string Join(string dir, string name)
    {
        if (dir.EndsWith("\\", StringComparison.Ordinal)) return dir + name;
        return dir + "\\" + name;
    }

    // Lists one directory into a reusable buffer. False = the directory could not be
    // opened at all (access denied, vanished, not a directory).
    internal static bool ListDir(string dir, List<Entry> into)
    {
        into.Clear();

        WIN32_FIND_DATA data;
        IntPtr h = FindFirstFileEx(ForApi(Join(dir, "*")), FindExInfoBasic, out data,
            FindExSearchNameMatch, IntPtr.Zero, FindFirstExLargeFetch);
        if (h == new IntPtr(-1))
        {
            // FindExInfoBasic / LARGE_FETCH are rejected by a few file-system drivers and
            // by some network redirectors; retry the plain way before giving up, so a
            // slower-but-correct scan is still possible everywhere.
            h = FindFirstFileEx(ForApi(Join(dir, "*")), 0, out data,
                FindExSearchNameMatch, IntPtr.Zero, 0);
            // Any failure to enumerate counts as "cannot read" for the caller: the error
            // code is not discriminable here (access denied and a directory that vanished
            // mid-walk land on the same return), so the summary says 无法访问 rather than
            // claiming that every one of them was a permission problem.
            if (h == new IntPtr(-1)) return false;
        }

        try
        {
            while (true)
            {
                string name = data.cFileName;
                if (name != "." && name != "..")
                {
                    Entry e = new Entry();
                    e.Name = name;
                    e.IsDir = (data.dwFileAttributes & FileAttributeDirectory) != 0;
                    e.IsReparse = (data.dwFileAttributes & FileAttributeReparsePoint) != 0;
                    e.Size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                    into.Add(e);
                }
                if (!FindNextFile(h, out data)) break;
            }
        }
        finally
        {
            FindClose(h);
        }
        return true;
    }

    // Full recursive size of one directory. Used for a matched cache folder; the caller
    // prunes at the match, so a nested cache is never added to the totals twice.
    private static long SizeOf(string dir, ScanRun run, int depth, out long files)
    {
        files = 0;
        if (depth > MaxDepthHardCap) { run.AddDepthCapped(); return 0; }

        List<Entry> entries = new List<Entry>(64);
        if (!ListDir(dir, entries))
        {
            run.AddDenied();
            return 0;
        }

        long total = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            Entry e = entries[i];
            if (e.IsDir)
            {
                if (e.IsReparse) { run.AddReparse(); continue; }
                long subFiles;
                total += SizeOf(Join(dir, e.Name), run, depth + 1, out subFiles);
                files += subFiles;
            }
            else
            {
                total += e.Size;
                files++;
                run.AddFile(e.Size);
            }
        }
        return total;
    }

    // ---------------------------------------------------------------- cache scan

    // Walks the tree for directories whose NAME is an unambiguously regenerable project
    // cache. A match is measured and PRUNED: caches nested inside it are part of its
    // size, so listing them again would inflate the total the user is asked to confirm
    // (04-Clean-Hidden.ps1 lists every level, which is why its rows overlap).
    internal static List<ScanHit> FindProjectCaches(string root, string[] names, int maxDepth, ScanRun run, long floor)
    {
        List<ScanHit> hits = new List<ScanHit>();
        List<string> starts = new List<string>();
        List<Entry> entries = new List<Entry>(256);

        if (ListDir(root, entries))
        {
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].IsDir && !entries[i].IsReparse) starts.Add(Join(root, entries[i].Name));
        }
        else
        {
            run.AddDenied();
        }

        object gate = new object();
        ParallelOptions opts = new ParallelOptions();
        opts.MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount);

        Parallel.ForEach(starts, opts, delegate(string start)
        {
            if (run.Cancelled) return;
            Walker w = new Walker(names, maxDepth, run, floor);
            w.Walk(start, 0);
            lock (gate) hits.AddRange(w.Hits);
        });

        hits.Sort(delegate(ScanHit a, ScanHit b) { return b.Bytes.CompareTo(a.Bytes); });
        return hits;
    }

    // One subtree walk. A class so parallel workers collect into their own list and take
    // no lock per directory.
    private sealed class Walker
    {
        private readonly string[] _names;
        private readonly int _maxDepth;
        private readonly ScanRun _run;
        private readonly long _floor;       // live tiles must match the final size floor

        internal readonly List<ScanHit> Hits = new List<ScanHit>();

        internal Walker(string[] names, int maxDepth, ScanRun run, long floor)
        {
            _names = names;
            _maxDepth = maxDepth;
            _run = run;
            _floor = floor;
        }

        internal void Walk(string dir, int depth)
        {
            if (_run.Cancelled) return;
            if (depth > _maxDepth) { _run.AddDepthCapped(); return; }

            _run.SetCurrent(dir);

            // WHY A FRESH BUFFER PER CALL: Walk recurses, and a single shared entry list
            // would be overwritten by the recursive call - the parent's loop would then
            // keep iterating the CHILD's entries (measured: the whole user profile came
            // back as 39 directories instead of hundreds of thousands).
            List<Entry> entries = new List<Entry>(64);
            if (!ListDir(dir, entries))
            {
                _run.AddDenied();
                return;
            }
            _run.AddDir();

            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (!e.IsDir)
                {
                    _run.AddFile(e.Size);
                    continue;
                }
                if (e.IsReparse) { _run.AddReparse(); continue; }

                string child = Join(dir, e.Name);
                if (IsCacheName(e.Name))
                {
                    long files;
                    long bytes = SizeOf(child, _run, depth + 1, out files);

                    ScanHit hit = new ScanHit();
                    hit.Path = child;
                    hit.Bytes = bytes;
                    hit.Files = files;
                    Hits.Add(hit);
                    // Only report what the final list will keep: the outcome applies the
                    // same floor at the end, and tiles for folders that get dropped used
                    // to stay on screen for the rest of the scan.
                    if (bytes >= _floor) _run.ReportHit(child, bytes);
                    continue;   // measured and pruned - do not descend again
                }
                Walk(child, depth + 1);
            }
        }

        private bool IsCacheName(string name)
        {
            for (int i = 0; i < _names.Length; i++)
                if (String.Equals(_names[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    // One outcome for a whole set of roots, shared by the page and by --scan-cache so
    // both report identical numbers. Roots are walked one after another; the parallelism
    // lives inside each walk.
    internal static ScanOutcome ScanProjectCaches(string[] roots, string[] names, int maxDepth,
        int minMB, ScanRun run)
    {
        ScanOutcome outcome = new ScanOutcome();
        outcome.Threads = Math.Max(1, Environment.ProcessorCount);
        outcome.Root = String.Join(" | ", roots);

        long floor = minMB * 1048576L;

        List<ScanHit> all = new List<ScanHit>();
        for (int i = 0; i < roots.Length; i++)
        {
            if (run.Cancelled) break;
            all.AddRange(FindProjectCaches(roots[i], names, maxDepth, run, floor));
        }
        all.Sort(delegate(ScanHit a, ScanHit b) { return b.Bytes.CompareTo(a.Bytes); });

        for (int i = 0; i < all.Count; i++)
            if (all[i].Bytes >= floor) outcome.Hits.Add(all[i]);

        outcome.Files = run.Files;
        outcome.Dirs = run.Dirs;
        outcome.TotalBytes = run.Bytes;
        outcome.Denied = run.Denied;
        outcome.ReparseSkipped = run.ReparseSkipped;
        outcome.DepthCapped = run.DepthCapped;
        outcome.ElapsedMs = run.ElapsedMs;
        return outcome;
    }

    // ---------------------------------------------------------------- large dirs
    // Single pass that keeps only directories above a size floor, then reports the
    // DEEPEST ones still above it: that answers "what is actually eating the disk",
    // because the parents of those nodes are large only by inheritance.
    // (05-Scan-LargeDirs.ps1 aggregates by fixed depth instead, so its rows always name
    // the same handful of top-level folders no matter what actually grew.)
    internal static ScanOutcome ScanLargeDirs(string root, long floorBytes, int maxHits, ScanRun run, int topFiles)
    {
        ScanOutcome outcome = new ScanOutcome();
        outcome.Root = root;

        List<ScanHit> rootFiles = new List<ScanHit>();
        List<string> starts = new List<string>();
        List<Entry> entries = new List<Entry>(256);
        if (ListDir(root, entries))
        {
            run.AddDir();
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (e.IsDir)
                {
                    if (e.IsReparse) run.AddReparse();
                    else starts.Add(Join(root, e.Name));
                }
                else
                {
                    run.AddFile(e.Size);
                    KeepBigFile(rootFiles, Join(root, e.Name), e.Size, topFiles);
                }
            }
        }
        else
        {
            run.AddDenied();
        }

        Dictionary<string, DirNode> big = new Dictionary<string, DirNode>(StringComparer.OrdinalIgnoreCase);
        object gate = new object();
        List<ScanHit> fileTop = new List<ScanHit>(rootFiles);

        ParallelOptions opts = new ParallelOptions();
        opts.MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount);
        outcome.Threads = opts.MaxDegreeOfParallelism;

        Parallel.ForEach(starts, opts, delegate(string start)
        {
            if (run.Cancelled) return;

            Dictionary<string, DirNode> local = new Dictionary<string, DirNode>(StringComparer.OrdinalIgnoreCase);
            List<ScanHit> localFiles = new List<ScanHit>();
            long sub = AggregateTree(start, root, floorBytes, 0, local, run, localFiles, topFiles);

            // The start node has no parent call to record it, so a big immediate child of
            // the root (C:\ProgramData, say, whose own children are all small) would
            // otherwise never appear as a culprit at all.
            if (sub >= floorBytes)
            {
                DirNode self = new DirNode();
                self.Parent = root;
                self.Bytes = sub;
                local[start] = self;
            }

            lock (gate)
            {
                foreach (KeyValuePair<string, DirNode> kv in local)
                {
                    if (!big.ContainsKey(kv.Key)) big[kv.Key] = kv.Value;
                }
                fileTop.AddRange(localFiles);
            }
        });

        // Each worker bounded its own list, so this final pass is the only sort.
        fileTop.Sort(delegate(ScanHit a, ScanHit b) { return b.Bytes.CompareTo(a.Bytes); });
        if (topFiles > 0 && fileTop.Count > topFiles)
            fileTop.RemoveRange(topFiles, fileTop.Count - topFiles);
        outcome.TopFiles.AddRange(fileTop);

        // A node is a "culprit" when no big node sits below it: mark the parents of every
        // big node, then keep the unmarked ones. One linear pass over a small map.
        Dictionary<string, bool> hasBigChild = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, DirNode> kv in big)
        {
            if (big.ContainsKey(kv.Value.Parent)) hasBigChild[kv.Value.Parent] = true;
        }

        List<ScanHit> hits = new List<ScanHit>();
        foreach (KeyValuePair<string, DirNode> kv in big)
        {
            if (hasBigChild.ContainsKey(kv.Key)) continue;
            ScanHit hit = new ScanHit();
            hit.Path = kv.Key;
            hit.Bytes = kv.Value.Bytes;
            hits.Add(hit);
        }
        hits.Sort(delegate(ScanHit a, ScanHit b) { return b.Bytes.CompareTo(a.Bytes); });
        if (hits.Count > maxHits) hits.RemoveRange(maxHits, hits.Count - maxHits);

        outcome.Files = run.Files;
        outcome.Dirs = run.Dirs;
        outcome.TotalBytes = run.Bytes;
        outcome.Denied = run.Denied;
        outcome.ReparseSkipped = run.ReparseSkipped;
        outcome.DepthCapped = run.DepthCapped;
        outcome.ElapsedMs = run.ElapsedMs;
        outcome.Hits.AddRange(hits);
        return outcome;
    }

    private sealed class DirNode
    {
        internal string Parent;
        internal long Bytes;
    }

    // Returns the total size of $dir and records every descendant at or above the floor.
    private static long AggregateTree(string dir, string parent, long floorBytes, int depth,
        Dictionary<string, DirNode> big, ScanRun run, List<ScanHit> top, int cap)
    {
        if (depth > MaxDepthHardCap) { run.AddDepthCapped(); return 0; }
        if (run.Cancelled) return 0;

        List<Entry> entries = new List<Entry>(128);
        if (!ListDir(dir, entries))
        {
            run.AddDenied();
            return 0;
        }
        run.AddDir();
        run.SetCurrent(dir);

        long total = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            Entry e = entries[i];
            if (e.IsDir)
            {
                if (e.IsReparse) { run.AddReparse(); continue; }
                string child = Join(dir, e.Name);
                long sub = AggregateTree(child, dir, floorBytes, depth + 1, big, run, top, cap);
                total += sub;
                if (sub >= floorBytes)
                {
                    DirNode node = new DirNode();
                    node.Parent = dir;
                    node.Bytes = sub;
                    big[child] = node;
                    run.ReportHit(child, sub);   // live bar chart on the page
                }
            }
            else
            {
                total += e.Size;
                run.AddFile(e.Size);
                KeepBigFile(top, Join(dir, e.Name), e.Size, cap);
            }
        }
        return total;
    }

    // A bounded "largest files" list. This runs once per file, so it has to stay cheap:
    // the list is only searched for its minimum once it is full. Bounded per worker rather
    // than shared behind a lock on purpose - one lock per file would cost the scanner the
    // throughput that is its whole point.
    private static void KeepBigFile(List<ScanHit> into, string path, long bytes, int cap)
    {
        if (cap <= 0) return;

        if (into.Count < cap)
        {
            ScanHit add = new ScanHit();
            add.Path = path;
            add.Bytes = bytes;
            add.Files = 1;
            into.Add(add);
            return;
        }

        int min = 0;
        for (int i = 1; i < into.Count; i++) if (into[i].Bytes < into[min].Bytes) min = i;
        if (bytes <= into[min].Bytes) return;

        ScanHit repl = new ScanHit();
        repl.Path = path;
        repl.Bytes = bytes;
        repl.Files = 1;
        into[min] = repl;
    }
}

// ================================================ animations live in GuiAnims.cs
//
// v0.5: 每个页面显示自己的动画（AnimBase 的十个 DrawMotif 子类），运行结束显示结果卡；
// 页面不再铺任何原始日志，日志只留在右上角「输出日志」的独立窗口里。
// 原来的 ScanPanel（通用扫描条）与 TailView（尾行窗）已删除，由 GuiAnims.cs 接替。

// ================================================================ result list

// The folder list produced by a scan: checkbox, name, full path, size, and a size bar
// relative to the biggest row. Owner-drawn, and it inherits the same hard-won flicker
// fix (narrow, erase-free row repaint + WS_EX_COMPOSITED on the form).
internal class ResultList : ListBox, IThemed
{
    private int _hot = -1;
    private long _maxBytes = 1;

    // Set false by a read-only scan mode: the row keeps its name/path/size and the size
    // bar, but shows no checkbox and cannot be ticked.
    internal bool ShowCheck = true;

    // Raised whenever the checked set changes, so the page can update its summary line.
    internal event EventHandler Changed;

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, ref RECT lpRect, bool bErase);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    internal ResultList()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 46;
        BorderStyle = BorderStyle.None;
        Font = Theme.FontUI;
        IntegralHeight = false;
        DoubleBuffered = true;
        SelectionMode = SelectionMode.One;
        ApplyPalette();
    }

    public void ApplyPalette()
    {
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
        Invalidate();
    }

    internal void SetHits(List<ScanHit> hits)
    {
        Items.Clear();
        _maxBytes = 1;
        for (int i = 0; i < hits.Count; i++)
        {
            Items.Add(hits[i]);
            if (hits[i].Bytes > _maxBytes) _maxBytes = hits[i].Bytes;
        }
        Invalidate();
    }

    internal List<ScanHit> Hits()
    {
        List<ScanHit> list = new List<ScanHit>(Items.Count);
        for (int i = 0; i < Items.Count; i++) list.Add((ScanHit)Items[i]);
        return list;
    }

    internal int CheckedCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < Items.Count; i++) if (((ScanHit)Items[i]).Selected) n++;
            return n;
        }
    }

    internal long CheckedBytes
    {
        get
        {
            long b = 0;
            for (int i = 0; i < Items.Count; i++)
            {
                ScanHit h = (ScanHit)Items[i];
                if (h.Selected) b += h.Bytes;
            }
            return b;
        }
    }

    internal void SetAll(bool selected)
    {
        for (int i = 0; i < Items.Count; i++) ((ScanHit)Items[i]).Selected = selected;
        Invalidate();
        RaiseChanged();
    }

    // Checks the top N rows (by size, which is the order the list is already in).
    internal void CheckTop(int n)
    {
        for (int i = 0; i < Items.Count; i++) ((ScanHit)Items[i]).Selected = (i < n);
        Invalidate();
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        EventHandler handler = Changed;
        if (handler != null) handler(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int index = IndexFromPoint(e.Location);
        if (ShowCheck && index >= 0 && index < Items.Count && e.X < BoxZone)
        {
            ScanHit hit = (ScanHit)Items[index];
            hit.Selected = !hit.Selected;
            RepaintRow(index);
            RaiseChanged();
            // Do not let the native control treat this as a selection click: the checkbox
            // and the row highlight are separate ideas here.
            return;
        }
        base.OnMouseDown(e);
    }

    private const int BoxZone = 46;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && SelectedIndex >= 0)
        {
            ScanHit hit = (ScanHit)Items[SelectedIndex];
            hit.Selected = !hit.Selected;
            RepaintRow(SelectedIndex);
            RaiseChanged();
            e.Handled = true;
            return;
        }
        if (e.Control && e.KeyCode == Keys.A)
        {
            SetAll(true);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = IndexFromPoint(e.Location);
        if (i != _hot)
        {
            int previous = _hot;
            _hot = i;
            RepaintRow(previous);
            RepaintRow(i);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        int previous = _hot;
        _hot = -1;
        RepaintRow(previous);
        base.OnMouseLeave(e);
    }

    // Erase-free, single-row repaint: only the rows whose state changed are repainted, and
    // without an erase, because a native ListBox row would otherwise be blanked before it
    // is redrawn and the text would visibly flash.
    private void RepaintRow(int index)
    {
        if (index < 0 || index >= Items.Count) return;

        Rectangle row = GetItemRectangle(index);
        if (!IsHandleCreated || row.Width <= 0 || row.Height <= 0)
        {
            Invalidate();
            return;
        }

        RECT rect = new RECT();
        rect.Left = row.Left;
        rect.Top = row.Top;
        rect.Right = row.Right;
        rect.Bottom = row.Bottom;
        InvalidateRect(Handle, ref rect, false);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        ScanHit hit = (ScanHit)Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

        using (SolidBrush clear = new SolidBrush(BackColor))
            g.FillRectangle(clear, e.Bounds);

        Rectangle row = new Rectangle(e.Bounds.X + 2, e.Bounds.Top + 2, Width - 6, e.Bounds.Height - 4);
        if (selected) Theme.FillRound(g, row, 9, Theme.AccentSoft);
        else if (e.Index == _hot) Theme.FillRound(g, row, 9, Theme.Hover);

        // relative size bar along the bottom of the row
        double frac = (double)hit.Bytes / _maxBytes;
        if (frac > 0.02)
        {
            int barW = (int)Math.Round((row.Width - 96) * frac);
            if (barW > 1)
            {
                Rectangle bar = new Rectangle(row.X + (ShowCheck ? 46 : 12), row.Bottom - 5, barW, 2);
                Theme.FillRound(g, bar, 1, Color.FromArgb(hit.Selected ? 200 : 110, Theme.Accent));
            }
        }

        int textX = row.X + 12;
        // The checkbox only exists on a list that can actually delete. A read-only result
        // must not look tickable, or the user ticks rows and then looks for a button that
        // is not there.
        if (ShowCheck)
        {
            Rectangle box = new Rectangle(row.X + 12, row.Y + (row.Height - 20) / 2, 20, 20);
            if (hit.Selected)
            {
                Theme.FillRound(g, box, 6, Theme.Accent);
                using (Pen pen = new Pen(Theme.OnAccent, 2f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLines(pen, new Point[]
                    {
                        new Point(box.X + 5,  box.Y + 10),
                        new Point(box.X + 8,  box.Y + 14),
                        new Point(box.X + 15, box.Y + 6)
                    });
                }
            }
            else
            {
                Theme.FillRound(g, box, 6, Theme.Card);
                Theme.DrawRound(g, box, 6, Theme.CheckBorder, 1.2f);
            }
            textX = box.Right + 12;
        }

        int sizeW = 116;
        int textW = Width - textX - sizeW - 16;
        if (textW < 60) textW = 60;

        string name = System.IO.Path.GetFileName(hit.Path);
        if (String.IsNullOrEmpty(name)) name = hit.Path;
        string parent = System.IO.Path.GetDirectoryName(hit.Path);

        TextRenderer.DrawText(g, name, selected ? Theme.FontUIB : Theme.FontUI,
            new Rectangle(textX, row.Y + 6, textW, 19),
            selected ? Theme.AccentDark : Theme.Text, Theme.LeftFlags);

        string detail = hit.Files > 0
            ? String.Format("{0} · {1:N0} 个文件", parent, hit.Files)
            : parent;
        TextRenderer.DrawText(g, detail, Theme.FontSmall,
            new Rectangle(textX, row.Y + 25, textW, 17), Theme.TextMuted, Theme.LeftFlags);

        TextRenderer.DrawText(g, Fmt.Bytes(hit.Bytes), Theme.FontUIB,
            new Rectangle(Width - sizeW - 10, row.Y, sizeW, row.Height),
            selected ? Theme.AccentDark : Theme.Text,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding);
    }
}

// ===================================================================== rail item

// One entry of the collapsible function rail. Collapsed it is just the numbered chip;
// expanded it also shows the title and the one-line role.
internal class RailButton : Control, IThemed
{
    private bool _hot;

    internal int Number;
    internal string TitleText = "";
    internal string SubText = "";
    internal bool Active;
    internal bool Collapsed;

    internal RailButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Height = 48;
    }

    public void ApplyPalette()
    {
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

    internal void SetActive(bool active)
    {
        if (Active == active) return;
        Active = active;
        Invalidate();
    }

    internal void SetCollapsed(bool collapsed)
    {
        if (Collapsed == collapsed) return;
        Collapsed = collapsed;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Card);

        Rectangle row = new Rectangle(6, 3, Width - 12, Height - 6);
        if (Active)
        {
            Theme.FillRound(g, row, 10, Theme.AccentSoft);
            Theme.FillRound(g, new Rectangle(row.X + 1, row.Y + 8, 3, row.Height - 16), 2, Theme.Accent);
        }
        else if (_hot)
        {
            Theme.FillRound(g, row, 10, Theme.Hover);
        }

        if (Collapsed)
        {
            Rectangle chip = new Rectangle((Width - 28) / 2, (Height - 28) / 2, 28, 28);
            Theme.FillRound(g, chip, 9, Active ? Theme.Accent : Theme.ChipBg);
            TextRenderer.DrawText(g, Number.ToString(), Theme.FontSmallB, chip,
                Active ? Theme.OnAccent : Theme.TextMuted, Theme.CenterFlags);
            return;
        }

        Rectangle chip2 = new Rectangle(row.X + 10, row.Y + (row.Height - 28) / 2, 28, 28);
        Theme.FillRound(g, chip2, 9, Active ? Theme.Accent : Theme.ChipBg);
        TextRenderer.DrawText(g, Number.ToString(), Theme.FontSmallB, chip2,
            Active ? Theme.OnAccent : Theme.TextMuted, Theme.CenterFlags);

        int textX = chip2.Right + 12;
        int textW = Width - textX - 10;
        if (textW < 40) textW = 40;

        TextRenderer.DrawText(g, TitleText, Active ? Theme.FontUIB : Theme.FontUI,
            new Rectangle(textX, row.Y + 5, textW, 19),
            Active ? Theme.AccentDark : Theme.Text, Theme.LeftFlags);
        TextRenderer.DrawText(g, SubText, Theme.FontSmall,
            new Rectangle(textX, row.Y + 23, textW, 16), Theme.TextMuted, Theme.LeftFlags);
    }
}

// ================================================================== log window

// The output log, now a window of its own behind the header's [日志] button. It follows
// the theme through Theme.PaletteChanged, so it recolours together with the main window.
internal sealed class LogWindow : Form, IThemed
{
    private readonly TextBox _text;
    private readonly PillCheck _autoScroll;
    private readonly FlatButton _btnClear, _btnSave;
    private readonly Label _status;
    private readonly Band _bar;

    internal LogWindow()
    {
        Text = Program.AppName + " · 输出日志";
        Font = Theme.FontUI;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(940, 620);
        MinimumSize = new Size(520, 320);
        DoubleBuffered = true;

        Card card = new Card();
        card.Dock = DockStyle.Fill;
        card.Padding = new Padding(14, 12, 14, 12);

        _text = new TextBox();
        _text.Dock = DockStyle.Fill;
        _text.Multiline = true;
        _text.ReadOnly = true;
        _text.WordWrap = false;
        _text.ScrollBars = ScrollBars.Both;
        _text.BorderStyle = BorderStyle.None;
        _text.Font = Theme.FontMono;

        _bar = new Band();
        _bar.Dock = DockStyle.Bottom;
        _bar.Height = 56;
        _bar.TopLine = true;

        _status = new Label();
        _status.AutoSize = false;
        _status.Dock = DockStyle.Left;
        _status.Width = 240;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(18, 0, 0, 0);

        FlowLayoutPanel right = new FlowLayoutPanel();
        right.Dock = DockStyle.Right;
        right.Width = 400;
        right.FlowDirection = FlowDirection.RightToLeft;
        right.WrapContents = false;
        right.Padding = new Padding(0, 11, 18, 0);

        _btnSave = new FlatButton("保存到文件", Theme.FontUI);
        _btnSave.Size = new Size(112, 34);
        _btnSave.Role = ButtonRole.Ghost;
        _btnSave.Margin = new Padding(8, 0, 0, 0);
        _btnSave.Click += new EventHandler(OnSaveClick);

        _btnClear = new FlatButton("清空", Theme.FontUI);
        _btnClear.Size = new Size(78, 34);
        _btnClear.Role = ButtonRole.Ghost;
        _btnClear.Margin = new Padding(8, 0, 0, 0);
        _btnClear.Click += new EventHandler(OnClearClick);

        _autoScroll = new PillCheck("自动滚动");
        _autoScroll.Checked = true;
        _autoScroll.Margin = new Padding(0, 4, 8, 0);

        right.Controls.Add(_btnSave);
        right.Controls.Add(_btnClear);
        right.Controls.Add(_autoScroll);

        _bar.Controls.Add(_status);
        _bar.Controls.Add(right);

        card.Controls.Add(_text);
        Controls.Add(card);
        Controls.Add(_bar);

        Theme.PaletteChanged += ApplyPalette;
        Log.Line += OnLogLine;
        FormClosed += delegate
        {
            Theme.PaletteChanged -= ApplyPalette;
            Log.Line -= OnLogLine;
            Log.MarkOpen(false);
        };

        // replay whatever happened before the window was opened
        _text.Text = String.Join("\r\n", Log.Snapshot());
        if (_text.TextLength > 0) _text.AppendText("\r\n");

        ApplyPalette();
        UpdateStatus();
        ScrollToEnd();
    }

    public void ApplyPalette()
    {
        Band bar = _bar;
        bar.ApplyPalette();
        _status.ForeColor = Theme.TextMuted;
        _text.BackColor = Theme.ConsoleBg;
        _text.ForeColor = Theme.ConsoleFg;
        Invalidate(true);
    }

    private void OnLogLine(string line)
    {
        try
        {
            Append(line + "\r\n");
        }
        catch { /* closing */ }
    }

    private void Append(string text)
    {
        const int cap = 400000;
        if (_text.TextLength > cap) _text.Text = _text.Text.Substring(cap / 2);
        _text.AppendText(text);
        if (_autoScroll.Checked) ScrollToEnd();
        UpdateStatus();
    }

    private void ScrollToEnd()
    {
        _text.SelectionStart = _text.TextLength;
        _text.ScrollToCaret();
    }

    private void UpdateStatus()
    {
        _status.Text = String.Format("共 {0:N0} 行 · 上限 4000 行（超出丢弃最早的）",
            _text.Lines.Length);
    }

    private void OnClearClick(object sender, EventArgs e)
    {
        Log.Clear();
        _text.Clear();
        UpdateStatus();
    }

    private void OnSaveClick(object sender, EventArgs e)
    {
        using (SaveFileDialog dlg = new SaveFileDialog())
        {
            dlg.Title = "保存输出日志";
            dlg.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
            dlg.FileName = "CLean-log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                // UTF-8 WITH BOM: this file is meant to be opened in Notepad on a Chinese
                // Windows, which would read BOM-less UTF-8 as GBK and show mojibake.
                File.WriteAllText(dlg.FileName, _text.Text, new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败: " + ex.Message, Program.AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Log.MarkOpen(true);
        ScrollToEnd();
    }
}

// ==================================================================== helpers

internal static class Fmt
{
    internal static string Bytes(long bytes)
    {
        double b = bytes;
        if (b >= 1073741824.0) return (b / 1073741824.0).ToString("0.00") + " GB";
        if (b >= 1048576.0) return (b / 1048576.0).ToString("0.0") + " MB";
        if (b >= 1024.0) return (b / 1024.0).ToString("0") + " KB";
        return b.ToString("0") + " B";
    }

    internal static string Seconds(long ms)
    {
        if (ms < 1000) return ms + " 毫秒";
        if (ms < 60000) return (ms / 1000.0).ToString("0.0") + " 秒";
        return (ms / 60000).ToString("0.0") + " 分钟";
    }

    internal static string Rate(long files, long ms)
    {
        if (ms <= 0) return "-";
        return (files * 1000.0 / ms).ToString("N0") + " 文件/秒";
    }
}

// Minimal JSON writing for the scan reports - no serializer reference, no dependency,
// which is the same rule the rest of this tool follows.
internal static class Json
{
    internal static string Escape(string s)
    {
        if (s == null) return "";
        StringBuilder sb = new StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\') sb.Append("\\\\");
            else if (c == '"') sb.Append("\\\"");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\t') sb.Append("\\t");
            else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.ToString();
    }
}

// ============================================================== page contract

// Everything the window needs to run one phase of one page. Phase 1 is execute = false
// (read-only or dry run); phase 2 is the same command with execute = true and only ever
// runs from a deliberate click on the confirmation strip.
internal sealed class PendingRun
{
    internal string Title = "";
    internal string Script = "";                 // file name inside the runtime directory
    internal Func<bool, string> Args;            // execute -> command-line arguments
    internal bool ReadOnly;
    internal string Confirm = "";                // confirmation-strip sentence
    internal string ReportTag = "";              // <tag>.dryrun.json the dry run wrote, if any
    internal ToolPage Owner;

    internal string ArgsFor(bool execute) { return Args != null ? (Args(execute) ?? "") : ""; }
}

internal abstract class ToolPage
{
    internal string Title = "";
    internal string Sub = "";
    internal string Help = "";
    internal bool ReadOnly;

    internal MainForm Host;
    internal Card Root;
    internal Panel TitleRow;
    internal Label LblTitle;
    internal Label LblHelp;
    internal Pill ModePill;
    internal FlowLayoutPanel Options;
    internal Panel Chrome;          // options + content, the part below the title
    internal Panel ContentHost;     // subclass fills this
    internal AnimBase Anim;         // this function's own animation (GuiAnims.cs)

    // Raised when the page's own action buttons change state (selection, busy, ...).
    internal event EventHandler StateChanged;

    internal Control View { get { return Root; } }

    // ---- page sizing (see LayoutPage) --------------------------------------
    // How tall the option stack may grow before it scrolls itself, and the smallest the
    // content area may get before the whole PAGE scrolls. Both exist because v1.1 added
    // option rows (catalogue / update-pause / export): with an AutoSize options row at a
    // small window height the options ate the page and the report table got nothing.
    internal int OptionsCap = 216;
    internal int ContentMinHeight = 400;

    private TableLayoutPanel _tlp;
    private bool _layingOut;

    // What the page's animation strip says at rest. Virtual because the scan pages start
    // a scan rather than a dry run, so "确认后才会删除" would be wrong on a read-only one.
    internal virtual string IdleHint()
    {
        return ReadOnly ? "只读功能：点下方按钮开始，结束后这里显示结论" : "点下方按钮预演，确认后才会删除";
    }

    internal void Build(MainForm host)
    {
        Host = host;

        Root = new Card();
        Root.Dock = DockStyle.Fill;
        Root.Padding = new Padding(18, 16, 18, 18);
        // The page scrolls when the window is too short to show everything. This only works
        // because the table below is docked Top (not Fill): a docked-Fill child is always
        // exactly the client size, so AutoScroll can never see an overflow to scroll.
        Root.AutoScroll = true;

        _tlp = new TableLayoutPanel();
        _tlp.Dock = DockStyle.Top;
        _tlp.ColumnCount = 1;
        _tlp.RowCount = 4;
        _tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));    // title row
        _tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));    // help
        _tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 0f));     // options -> LayoutPage
        _tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 400f));   // content -> LayoutPage

        TitleRow = new Panel();
        TitleRow.Dock = DockStyle.Fill;
        Role(TitleRow, "card");

        LblTitle = new Label();
        LblTitle.Text = Title;
        LblTitle.Font = Theme.FontAction;
        LblTitle.Dock = DockStyle.Fill;
        LblTitle.TextAlign = ContentAlignment.MiddleLeft;
        LblTitle.Padding = new Padding(2, 0, 0, 0);
        Role(LblTitle, "card|text");

        ModePill = new Pill(ReadOnly ? "只读" : "需确认", ReadOnly ? PillTone.Success : PillTone.Warn, Theme.FontSmallB);
        ModePill.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        TitleRow.Controls.Add(LblTitle);
        TitleRow.Controls.Add(ModePill);
        TitleRow.Resize += delegate { PlacePill(); };
        PlacePill();

        LblHelp = new Label();
        LblHelp.Text = Help;
        LblHelp.Font = Theme.FontUI;
        LblHelp.Dock = DockStyle.Fill;
        LblHelp.Margin = new Padding(2, 0, 0, 8);
        Role(LblHelp, "card|muted");

        Options = new FlowLayoutPanel();
        Options.Dock = DockStyle.Fill;
        // AutoSize off and AutoScroll on: the option stack is capped by LayoutPage and
        // scrolls itself once it no longer fits, instead of pushing the content area off
        // the page.
        Options.AutoSize = false;
        Options.AutoScroll = true;
        Options.FlowDirection = FlowDirection.TopDown;
        Options.WrapContents = false;
        Options.Margin = new Padding(0, 0, 0, 8);
        Role(Options, "card");

        Chrome = new Panel();
        Chrome.Dock = DockStyle.Fill;
        Chrome.Margin = new Padding(0);
        Role(Chrome, "card");

        Anim = MakeAnim();
        Anim.Dock = DockStyle.Top;
        Anim.Margin = new Padding(0, 0, 0, 8);
        // Idle state right away: the strip doubles as the page's "what this function does"
        // line at rest, and publishing its text is also what gives it an accessible name.
        Anim.SetIdle("就绪 · " + Title, IdleHint());

        ContentHost = new Panel();
        ContentHost.Dock = DockStyle.Fill;
        Role(ContentHost, "card");

        // Fill first, then the top-docked animation strip, so the animation stays at the
        // top of the page and the content takes the rest.
        Chrome.Controls.Add(ContentHost);
        Chrome.Controls.Add(Anim);

        _tlp.Controls.Add(TitleRow, 0, 0);
        _tlp.Controls.Add(LblHelp, 0, 1);
        _tlp.Controls.Add(Options, 0, 2);
        _tlp.Controls.Add(Chrome, 0, 3);
        Root.Controls.Add(_tlp);
        Root.Resize += delegate { LayoutPage(); };

        BuildContent();
        Options.Visible = Options.Controls.Count > 0;
        LayoutPage();
    }

    // Keeps every row reachable in a short window: the option stack is capped and scrolls
    // itself, the content area keeps at least ContentMinHeight, and the page gets its own
    // scrollbar when the two together no longer fit. On a tall window the content row just
    // takes the remaining space, so nothing changes for users who never resize.
    private void LayoutPage()
    {
        if (_layingOut || _tlp == null || Options == null || Chrome == null) return;

        int width = Root.ClientSize.Width - Root.Padding.Horizontal;
        if (width < 80) return;

        // NOT Options.Visible: Control.Visible reports false when any ANCESTOR is hidden, and
        // every page except the current one is hidden - which collapsed the option row to
        // 1 px on every page that was not on screen. The page's own intent is "do I have
        // option rows", so ask that instead.
        // + Margin.Vertical because the cell subtracts the panel's own bottom margin: without
        // it the cap was 8 px short and the option stack grew a pointless scrollbar for a
        // single clipped pixel row.
        int optH = Options.Controls.Count > 0
            ? Math.Min(Options.PreferredSize.Height + Options.Margin.Vertical, OptionsCap)
            : 0;
        int rows = 40 + 40 + optH;
        int avail = Root.ClientSize.Height - Root.Padding.Vertical;
        int contentH = Math.Max(ContentMinHeight, avail - rows);

        Size want = new Size(width, rows + contentH);
        if (_tlp.Size == want) return;              // no change: never re-enter the layout

        _layingOut = true;
        try
        {
            _tlp.RowStyles[2].Height = optH;
            _tlp.RowStyles[3].Height = contentH;
            _tlp.Size = want;
        }
        finally { _layingOut = false; }
    }

    internal static T Role<T>(T c, string role) where T : Control
    {
        c.Tag = role;
        return c;
    }

    internal static Label MakeRowLabel(string text)
    {
        Label l = new Label();
        l.Text = text;
        l.Font = Theme.FontUI;
        l.AutoSize = false;
        l.Size = new Size(76, 30);
        l.TextAlign = ContentAlignment.MiddleLeft;
        l.Margin = new Padding(0, 0, 6, 0);
        Role(l, "card|muted");
        return l;
    }

    internal static FlowLayoutPanel MakeRow(Control first, params Control[] rest)
    {
        FlowLayoutPanel row = new FlowLayoutPanel();
        row.AutoSize = true;
        row.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        row.FlowDirection = FlowDirection.LeftToRight;
        row.WrapContents = true;
        row.Margin = new Padding(0, 0, 0, 8);
        row.Padding = new Padding(0);
        Role(row, "card");
        if (first != null) row.Controls.Add(first);
        for (int i = 0; i < rest.Length; i++) row.Controls.Add(rest[i]);
        return row;
    }

    internal static PillRadio MakeRadio(string text, bool isChecked)
    {
        PillRadio r = new PillRadio(text);
        r.Checked = isChecked;
        r.Margin = new Padding(0, 0, 8, 0);
        return r;
    }

    // Protected, not private: a page whose mode changes the badge text (the merged scan
    // page switches between 只读 and 需确认) has to re-place the pill after SetContent,
    // because anchoring does not re-run when a child changes its own width.
    protected void PlacePill()
    {
        if (ModePill == null || ModePill.Parent == null) return;
        Control row = ModePill.Parent;
        ModePill.Location = new Point(
            Math.Max(0, row.Width - ModePill.Width),
            Math.Max(0, (row.Height - ModePill.Height) / 2));
    }

    internal void RaiseStateChanged()
    {
        EventHandler handler = StateChanged;
        if (handler != null) handler(this, EventArgs.Empty);
    }

    protected abstract void BuildContent();

    // Which animation this page shows (GuiAnims.cs). One per function, always animating.
    protected abstract AnimBase MakeAnim();

    // 运行结束的回执：脚本类页面把它铺成结果卡，扫描页用自己的列表 + 汇总。
    internal virtual void ShowRunResult(bool cancelled, int exitCode, int seconds,
        int ok, int warn, int err, string key) { }

    internal virtual string RunLabel() { return ReadOnly ? "运行（只读，不会删除文件）" : "开始预演（不删除文件）"; }
    internal virtual bool CanRun { get { return true; } }
    internal virtual void RunPrimary() { }
    internal virtual void CancelWork() { }
    internal virtual bool Busy { get { return false; } }
    internal virtual void OnEnter() { }
    internal virtual void OnLeave() { }
    internal virtual void OnRunFinished(string title, bool execute, int exitCode) { }
    internal virtual string PlanLines() { return ""; }
    internal virtual void Shutdown() { }
}

// =============================================================== script page

// A page whose work is one of the embedded .ps1 scripts: same two-phase contract as
// before (dry run, then a deliberate confirmation). The page shows its own animation
// while running and a result card afterwards - never the raw output, which lives only in
// the log window behind the header button.
//
// Renders the JSON report its own run wrote (ReportTag), as a table - see the report
// section below. Not sealed: originally a read-only ReportPage derived from it, and the
// report machinery has since moved in here so the destructive pages get it too.
internal class ScriptPage : ToolPage
{
    private readonly string _script;
    private readonly Func<bool, string> _args;
    private readonly Func<string> _validate;
    private readonly Action<ScriptPage> _buildOptions;
    private readonly AnimBase _anim;

    internal SummaryView Summary;

    // ---- report rendering (ReportView.cs) -----------------------------------
    // The <tag> the script uses for its reports (`<tag>.json` / `<tag>.dryrun.json`),
    // declared per page right where the script name is, instead of being derived from the
    // script file name later: that mapping is exactly the kind of string match that drifts
    // silently when a script is renamed. Two things use it - the confirmation strip reads
    // `<tag>.dryrun.json` for the dry run's measured numbers, and the page renders as a
    // table whichever report its last run wrote.
    //
    // This lives on the base class so the DESTRUCTIVE pages get the table too: system.json
    // is a per-target record of what each target actually released, which the result card
    // could only ever summarise in one line. A page whose script writes no report leaves
    // the tag empty and keeps the card alone.
    internal string ReportTag = "";
    internal string ReportEmptyHint = "";    // what the table will hold, for the empty state

    private string _reportFamily;            // set by a secondary action (e.g. 查看清单)
    private ReportList _view;
    private FlatButton _btnExport;
    private ReportDoc _doc;

    internal ScriptPage(string title, string sub, string help, string script, bool readOnly,
        Func<bool, string> args, Func<string> validate, Action<ScriptPage> buildOptions, AnimBase anim)
    {
        Title = title;
        Sub = sub;
        Help = help;
        _script = script;
        ReadOnly = readOnly;
        _args = args;
        _validate = validate;
        _buildOptions = buildOptions;
        _anim = anim;
        // A result card (176 px) plus a readable slice of the report table, plus the
        // animation strip: below this the page scrolls rather than squeezing the table out.
        ContentMinHeight = 360;
    }

    protected override AnimBase MakeAnim()
    {
        return _anim;
    }

    protected override void BuildContent()
    {
        Summary = new SummaryView();
        Summary.Dock = DockStyle.Fill;
        Summary.Margin = new Padding(0);
        Summary.Reset(ReadOnly
            ? "只读功能：运行结果的关键结论会显示在这里，原始输出请看右上角「输出日志」。"
            : "点下方按钮开始预演；预演完成后才会出现确认条，点「执行删除」才真正删除。");
        ContentHost.Controls.Add(Summary);

        if (_buildOptions != null) _buildOptions(this);

        if (ReportTag.Length > 0) BuildReport();
        BuildActionRow();
        ShowLastReport();
    }

    // The page's secondary actions, collected so they can share ONE horizontal row with the
    // report export button. Pages add theirs with AddActionButton() from their option
    // builder. They used to be one button per row, which at a small window height pushed the
    // report table off the page entirely.
    private readonly List<Control> _actionButtons = new List<Control>();

    internal void AddActionButton(Control button)
    {
        if (button != null) _actionButtons.Add(button);
    }

    private void BuildActionRow()
    {
        int n = _actionButtons.Count + (_btnExport != null ? 1 : 0);
        if (n == 0) return;

        Control[] row = new Control[n];
        int k = 0;
        if (_btnExport != null) row[k++] = _btnExport;
        for (int i = 0; i < _actionButtons.Count; i++) row[k++] = _actionButtons[i];

        // MakeRow wraps (WrapContents = true), so on a very narrow window these fall onto a
        // second line instead of being clipped at the right edge.
        AddOptionRow(ToolPage.MakeRow(null, row));
    }

    // ------------------------------------------------------------- report table

    private void BuildReport()
    {
        // The card keeps a fixed strip at the top and the table gets the rest. Re-added in
        // the order the scan pages use, which is the order that docks correctly: the Fill
        // control first, then the Top one.
        //
        // The two heights are not arbitrary: SummaryView paints state at y=14, meta at 48,
        // counts at 72 and the key line from y=96, with the hint pinned to Height-26. At
        // 96px only "state + hint" fits (the resting state); a result card needs 176px or
        // the wrapped key line runs under the hint.
        Summary.Dock = DockStyle.Top;
        Summary.Height = 96;

        _view = new ReportList();
        _view.Dock = DockStyle.Fill;
        _view.Margin = new Padding(0);

        ContentHost.Controls.Remove(Summary);
        ContentHost.Controls.Add(_view);
        ContentHost.Controls.Add(Summary);

        _btnExport = new FlatButton("导出 CSV", Theme.FontUI);
        _btnExport.Role = ButtonRole.Ghost;
        _btnExport.Size = new Size(96, 32);
        _btnExport.Margin = new Padding(0, 0, 8, 0);
        _btnExport.Enabled = false;
        _btnExport.Click += delegate { ExportCsv(); };
        // The row itself is assembled by BuildActionRow(), after the page has contributed
        // its own buttons.
    }

    // The report family in force: normally the page's own, but a secondary action can
    // point the table at a different report (系统缓存清理's "查看清理清单" shows
    // catalog.json). Running the page's primary action hands the table back.
    internal void UseReportFamily(string tag)
    {
        _reportFamily = tag;
    }

    // Re-read the table from disk. Used by an action that produces a report without running
    // a script - the cleanup ledger is built by the GUI itself, out of the other reports.
    internal void ReloadReport()
    {
        if (ReportTag.Length == 0) return;
        ShowLastReport();
    }

    private string ActiveTag()
    {
        return _reportFamily != null ? _reportFamily : ReportTag;
    }

    // A dry run and a real run write different files (<tag>.dryrun.json vs <tag>.json), so
    // the table shows whichever the LAST run produced instead of guessing by phase.
    private string NewestReportPath()
    {
        string root = Host != null ? Host.RuntimeDir : null;
        string tag = ActiveTag();
        if (String.IsNullOrEmpty(root) || String.IsNullOrEmpty(tag)) return null;

        string dir = System.IO.Path.Combine(root, "reports");
        string plain = System.IO.Path.Combine(dir, tag + ".json");
        string dry = System.IO.Path.Combine(dir, tag + ".dryrun.json");
        bool hasPlain = File.Exists(plain);
        bool hasDry = File.Exists(dry);
        if (hasPlain && hasDry)
            return File.GetLastWriteTimeUtc(plain) >= File.GetLastWriteTimeUtc(dry) ? plain : dry;
        if (hasPlain) return plain;
        if (hasDry) return dry;
        return null;
    }

    // On open, show whatever the previous run wrote instead of an empty page - the file is
    // the audit trail, so reading it is always valid. The card says so explicitly, so a
    // stale table can never be mistaken for a fresh run.
    private void ShowLastReport()
    {
        if (ReportTag.Length == 0) return;

        string path = NewestReportPath();
        ReportDoc doc = path == null ? null : ReportDoc.Load(path, Title);
        bool hasContent = doc != null && !doc.IsEmpty;
        Show(doc, hasContent);

        if (hasContent)
        {
            // The card grew to fit a result (see BuildReport for the geometry), exactly as
            // RefreshReport does after a run. Without this it stayed 96 px - tall enough for
            // "state + hint" only - and the counts line landed on top of the hint.
            Summary.Height = 176;
            // "尚未运行" next to a populated table would be a contradiction: the card has
            // to say that what is on screen came from the previous run's report.
            Summary.ShowResult("已载入上次报告", System.IO.Path.GetFileName(path), Describe(doc),
                doc.Title, Theme.TextMuted);
        }
        else
        {
            Summary.Reset("点下方按钮运行一次，" + ReportEmptyHint + "会显示在这里。");
        }
    }

    private static string Describe(ReportDoc doc)
    {
        int rows = 0;
        for (int i = 0; i < doc.Tables.Count; i++) rows += doc.Tables[i].Rows.Count;
        return String.Format("{0} 个表 · {1} 行", doc.Tables.Count, rows);
    }

    private void Show(ReportDoc doc, bool enableExport)
    {
        _doc = doc;
        _view.SetDoc(doc);
        _btnExport.Enabled = enableExport;
    }

    private void ExportCsv()
    {
        if (_doc == null || _doc.IsEmpty) return;
        try
        {
            string dir = System.IO.Path.Combine(Host.RuntimeDir, "reports");
            Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir,
                String.Format("export-{0}-{1:yyyyMMdd-HHmmss}.csv", ActiveTag(), DateTime.Now));
            // UTF-8 with BOM, same as the log window's "save to file": without it Excel
            // reads the Chinese column contents as GBK and shows mojibake.
            File.WriteAllText(path, _doc.ToCsv(), new UTF8Encoding(true));
            Host.Info("已导出 " + _doc.Title + "：\r\n" + path
                    + "\r\n\r\nUTF-8 带 BOM，Excel / 记事本直接打开不会乱码。");
        }
        catch (Exception ex)
        {
            Host.Warn("导出失败：" + ex.Message);
        }
    }

    internal override void ShowRunResult(bool cancelled, int exitCode, int seconds,
        int ok, int warn, int err, string key)
    {
        bool bad = (err > 0) || (exitCode != 0 && !cancelled);
        string state;
        Color tone;
        if (cancelled) { state = "已取消"; tone = Theme.Warn; }
        else if (bad) { state = "运行结束，退出码 " + exitCode; tone = Theme.Danger; }
        else { state = "完成"; tone = Theme.Success; }

        string meta = String.Format("用时 {0} 秒 · {1}", seconds, _script);
        string counts = String.Format("成功 {0} · 警告 {1} · 失败 {2}", ok, warn, err);
        // A result card needs 176 px (see BuildReport for the geometry). Set here rather than
        // only inside RefreshReport, because a cancelled run returns before reaching it and
        // would otherwise draw its counts into a 96 px card.
        if (ReportTag.Length > 0) Summary.Height = 176;
        Summary.ShowResult(state, meta, counts, key != null ? key : "", tone);

        if (cancelled) return;
        RefreshReport(exitCode, seconds);
    }

    // Pull in the report this run just wrote. Called for both a dry run and a real run:
    // which file to read is decided by the tag and by what is on disk, not by the phase.
    protected void RefreshReport(int exitCode, int seconds)
    {
        if (ReportTag.Length == 0) return;

        string path = NewestReportPath();
        ReportDoc doc = path == null ? null : ReportDoc.Load(path, Title);
        if (doc != null && !doc.IsEmpty)
        {
            Summary.Height = 176;      // room for the key line, see BuildReport
            Show(doc, true);
            return;
        }

        // The run finished but there is no usable report. Say which tag was expected
        // rather than leaving the previous table on screen looking current.
        Show(null, false);
        if (exitCode == 0)
        {
            Summary.Height = 176;
            Summary.ShowResult("完成，但没找到报告",
                String.Format("用时 {0} 秒 · {1}.json", seconds, ActiveTag()),
                "脚本没有写出可解析的 JSON 报告",
                "原始输出请看右上角「输出日志」。", Theme.Warn);
        }
    }

    internal void AddOptionRow(Control row)
    {
        Options.Controls.Add(row);
    }

    internal PendingRun MakeRun()
    {
        PendingRun run = new PendingRun();
        run.Title = Title;
        run.Script = _script;
        run.Args = _args;
        run.ReadOnly = ReadOnly;
        run.Owner = this;
        run.ReportTag = ReportTag;
        run.Confirm = ReadOnly
            ? ""
            : "预演完成，尚未删除任何文件。现在执行「" + Title + "」？";
        return run;
    }

    internal override void RunPrimary()
    {
        // The page's own action always drives the page's own report; a secondary action
        // (系统缓存清理's 查看清理清单) may have pointed the table somewhere else.
        _reportFamily = null;
        string problem = _validate != null ? _validate() : null;
        if (problem != null)
        {
            Host.Warn(problem);
            return;
        }
        Host.RunScript(MakeRun(), false);
    }

    internal override string PlanLines()
    {
        if (_args == null) return "";
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("    阶段 1 : " + ScriptLine(false));
        sb.AppendLine(ReadOnly
            ? "    阶段 2 : (无) 只读功能，永远不会传 -Execute"
            : "    阶段 2 : " + ScriptLine(true));
        return sb.ToString();
    }

    private string ScriptLine(bool execute)
    {
        string a = _args(execute);
        return _script + (a != null && a.Trim().Length > 0 ? " " + a.Trim() : " (无参数)");
    }
}

// ============================================================ scan page base

// Shared plumbing for the two pages that scan the disk themselves: the result list, the
// selection summary, the poll timer that drives the animation, and the report file.
internal abstract class ScanPageBase : ToolPage
{
    internal ResultList List;
    internal Label Summary;
    internal FlowLayoutPanel Actions;
    internal FlatButton BtnPrimary;
    internal Panel ActionRow;

    private readonly System.Windows.Forms.Timer _poll = new System.Windows.Forms.Timer();
    private ScanRun _run;
    private bool _scanning;
    private bool _scanned;          // distinguishes "never scanned" from "found nothing"
    private long _lastPollFiles;
    private DateTime _lastPollAt;

    internal ScanOutcome Outcome;

    private readonly AnimBase _anim;

    protected ScanPageBase(AnimBase anim, bool readOnly)
    {
        // Driven by the page, not hard-coded: the large-directory page is read-only and
        // must say so on its badge, while the cache page owns the delete path.
        ReadOnly = readOnly;
        _anim = anim;
    }

    internal override string IdleHint()
    {
        return "点下方按钮开始扫描，结果会实时列在下面的清单里";
    }

    protected override AnimBase MakeAnim()
    {
        return _anim;
    }

    protected override void BuildContent()
    {
        ActionRow = new Panel();
        ActionRow.Dock = DockStyle.Top;
        ActionRow.Height = 44;
        Role(ActionRow, "card");

        Summary = new Label();
        Summary.AutoSize = false;
        Summary.Dock = DockStyle.Fill;
        Summary.TextAlign = ContentAlignment.MiddleLeft;
        Summary.Font = Theme.FontUI;
        Role(Summary, "card|muted");

        Actions = new FlowLayoutPanel();
        Actions.Dock = DockStyle.Right;
        // Wide enough for the busiest mode: 删除选中项 + 开始扫描 + 仅选大项 + 全不选 + 全选
        // is 588px with margins, which the old 520 silently clipped at the left edge.
        Actions.Width = 604;
        Actions.FlowDirection = FlowDirection.RightToLeft;
        Actions.WrapContents = false;
        Role(Actions, "card");

        BtnPrimary = new FlatButton("开始扫描", Theme.FontUIB);
        BtnPrimary.Size = new Size(150, 34);
        BtnPrimary.Role = ButtonRole.Primary;
        BtnPrimary.Margin = new Padding(8, 0, 0, 0);
        BtnPrimary.Click += delegate { StartScan(); };

        BuildActions();

        ActionRow.Controls.Add(Summary);
        ActionRow.Controls.Add(Actions);

        List = new ResultList();
        List.Dock = DockStyle.Fill;
        List.Margin = new Padding(0);
        List.Changed += delegate { UpdateSummary(); RaiseStateChanged(); };

        ContentHost.Controls.Add(List);
        ContentHost.Controls.Add(ActionRow);

        _poll.Interval = 250;
        _poll.Tick += delegate { PollScan(); };

        UpdateSummary();
    }

    // Subclasses add their own buttons to Actions (right to left).
    protected abstract void BuildActions();

    // The actual scan, executed on a worker thread. Must honour run.Cancelled.
    protected abstract ScanOutcome ScanWork(ScanRun run);

    // Roots the scan covered, used both for the report and for the deletion allowlist.
    internal abstract string[] Roots { get; }

    internal override string RunLabel() { return _scanned ? "重新扫描" : "开始扫描"; }
    internal override bool Busy { get { return _scanning; } }

    internal override void RunPrimary()
    {
        StartScan();
    }

    internal override void CancelWork()
    {
        if (_run != null) _run.Cancel();
    }

    internal void StartScan()
    {
        if (_scanning) return;

        _scanning = true;
        Outcome = null;
        List.Items.Clear();
        UpdateSummary();

        string root = Roots.Length > 0 ? Roots[0] : "";
        _run = new ScanRun(root);
        _lastPollFiles = 0;
        _lastPollAt = DateTime.Now;

        Anim.BeginRun("正在扫描 · " + ScanCaption());
        Anim.FeedLive(null);
        _poll.Start();
        RaiseStateChanged();
        Host.SetBusy(true, "扫描中");
        Log.Append("");
        Log.Append(">>> " + Title + " 开始扫描：" + String.Join(" | ", Roots));

        MainForm form = Host;
        Task.Factory.StartNew(delegate
        {
            ScanOutcome result = null;
            Exception failure = null;
            try
            {
                result = ScanWork(_run);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            try
            {
                form.BeginInvoke((MethodInvoker)delegate { FinishScan(result, failure); });
            }
            catch { /* window already gone */ }
        });
    }

    protected virtual string ScanCaption() { return Title; }

    private void PollScan()
    {
        if (_run == null) return;

        long files = _run.Files;
        DateTime now = DateTime.Now;
        double ms = (now - _lastPollAt).TotalMilliseconds;
        string rate = "-";
        if (ms > 200)
        {
            rate = Fmt.Rate(files - _lastPollFiles, (long)ms);
            _lastPollFiles = files;
            _lastPollAt = now;
        }

        Anim.SetDetail(String.Format(
            "已扫描 {0:N0} 个文件 · {1:N0} 个目录 · {2} · 用时 {3} · {4} · {5}",
            files, _run.Dirs, Fmt.Bytes(_run.Bytes), Fmt.Seconds(_run.ElapsedMs), rate, _run.Current));
        Anim.FeedLive(_run.LiveHits());
    }

    private void FinishScan(ScanOutcome outcome, Exception failure)
    {
        _poll.Stop();
        _scanning = false;

        if (failure != null)
        {
            Anim.EndRun("扫描失败", failure.Message);
            Log.Append("[x] 扫描异常: " + failure.Message);
            Program.Report("扫描", failure);
            Host.SetBusy(false, "扫描失败");
            RaiseStateChanged();
            return;
        }

        bool cancelled = _run != null && _run.Cancelled;
        outcome.Cancelled = cancelled;      // the report must not read like a finished scan

        Outcome = outcome;
        _scanned = true;
        List.SetHits(outcome.Hits);
        OnScanFinished(outcome);

        string summary = String.Format(
            "{0} · {1:N0} 个文件 · {2:N0} 个目录 · 用时 {3} · {4} 线程{5}{6}",
            Fmt.Bytes(outcome.TotalBytes), outcome.Files, outcome.Dirs,
            Fmt.Seconds(outcome.ElapsedMs), outcome.Threads,
            outcome.Denied > 0 ? String.Format(" · {0} 个目录无法访问", outcome.Denied) : "",
            outcome.DepthCapped > 0 ? String.Format(" · {0} 处因深度封顶未展开", outcome.DepthCapped) : "");

        Anim.FeedLive(outcome.Hits);   // bars / tiles settle on the real result
        if (cancelled)
        {
            // The workers stop part-way, so the hits are real but partial: announcing
            // them with the same words as a finished scan would overstate the result.
            Anim.EndRun("扫描已取消 · 以下是已扫到的部分结果", summary);
            Log.Append("[!] " + Title + " 扫描已取消（部分结果）：" + summary);
        }
        else
        {
            Anim.EndRun(outcome.Hits.Count > 0
                ? String.Format("扫描完成 · 找到 {0} 项", outcome.Hits.Count)
                : "扫描完成 · 没有符合条件的目录",
                summary);
            Log.Append("[+] " + Title + " 扫描完成：" + summary);
        }
        for (int i = 0; i < outcome.Hits.Count && i < 40; i++)
            Log.Append(String.Format("    {0,12}  {1}", Fmt.Bytes(outcome.Hits[i].Bytes), outcome.Hits[i].Path));

        WriteReport(outcome);
        UpdateSummary();
        RaiseStateChanged();
        Host.SetBusy(false, cancelled
            ? "已取消 · 已扫到 " + Fmt.Bytes(outcome.TotalBytes)
            : "就绪 · 扫描用时 " + Fmt.Seconds(outcome.ElapsedMs));
        Host.ScanFinished();
    }

    // Hook: fill in any per-row notes before the list is shown.
    protected virtual void OnScanFinished(ScanOutcome outcome) { }

    private void WriteReport(ScanOutcome outcome)
    {
        try
        {
            string dir = System.IO.Path.Combine(Host.RuntimeDir, "reports");
            Directory.CreateDirectory(dir);
            StringBuilder sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"Tool\": \"CLean-GUI\",\r\n");
            sb.Append("  \"Page\": \"").Append(Json.Escape(Title)).Append("\",\r\n");
            sb.Append("  \"Time\": \"").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\",\r\n");
            sb.Append("  \"Roots\": [");
            for (int i = 0; i < Roots.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("\"").Append(Json.Escape(Roots[i])).Append("\"");
            }
            sb.Append("],\r\n");
            sb.Append("  \"TotalMB\": ").Append((outcome.TotalBytes / 1048576.0).ToString("0.0")).Append(",\r\n");
            sb.Append("  \"Files\": ").Append(outcome.Files).Append(",\r\n");
            sb.Append("  \"Dirs\": ").Append(outcome.Dirs).Append(",\r\n");
            sb.Append("  \"ElapsedMs\": ").Append(outcome.ElapsedMs).Append(",\r\n");
            sb.Append("  \"Threads\": ").Append(outcome.Threads).Append(",\r\n");
            sb.Append("  \"Denied\": ").Append(outcome.Denied).Append(",\r\n");
            sb.Append("  \"ReparseSkipped\": ").Append(outcome.ReparseSkipped).Append(",\r\n");
            sb.Append("  \"DepthCapped\": ").Append(outcome.DepthCapped).Append(",\r\n");
            sb.Append("  \"Cancelled\": ").Append(outcome.Cancelled ? "true" : "false").Append(",\r\n");
            sb.Append("  \"Items\": [\r\n");
            for (int i = 0; i < outcome.Hits.Count; i++)
            {
                ScanHit h = outcome.Hits[i];
                if (i > 0) sb.Append(",\r\n");
                sb.Append("    { \"MB\": ").Append((h.Bytes / 1048576.0).ToString("0.0"))
                  .Append(", \"Files\": ").Append(h.Files)
                  .Append(", \"Path\": \"").Append(Json.Escape(h.Path)).Append("\" }");
            }
            sb.Append("\r\n  ],\r\n");
            sb.Append("  \"TopFiles\": [\r\n");
            for (int i = 0; i < outcome.TopFiles.Count; i++)
            {
                ScanHit h = outcome.TopFiles[i];
                if (i > 0) sb.Append(",\r\n");
                sb.Append("    { \"MB\": ").Append((h.Bytes / 1048576.0).ToString("0.0"))
                  .Append(", \"Files\": ").Append(h.Files)
                  .Append(", \"Path\": \"").Append(Json.Escape(h.Path)).Append("\" }");
            }
            sb.Append("\r\n  ]\r\n}\r\n");

            File.WriteAllText(System.IO.Path.Combine(dir, ReportName), sb.ToString(), new UTF8Encoding(true));
            Log.Append("    报告 → " + System.IO.Path.Combine(dir, ReportName));
        }
        catch (Exception ex)
        {
            Log.Append("[!] 写报告失败: " + ex.Message);
        }
    }

    protected abstract string ReportName { get; }

    internal void UpdateSummary()
    {
        int n = List.CheckedCount;
        long bytes = List.CheckedBytes;
        string text;
        if (!_scanned) text = SummaryIdle();
        else if (List.Items.Count == 0) text = String.Format("已扫描完成 · 没有符合条件的目录（可把阈值调小或深度改为「不限层数」）· 用时 {0}",
            Outcome != null ? Fmt.Seconds(Outcome.ElapsedMs) : "-");
        else text = String.Format("共 {0} 项 · {1}；已选 {2} 项 · {3}",
            List.Items.Count, Fmt.Bytes(TotalBytes()), n, Fmt.Bytes(bytes));

        Summary.Text = text;
        OnSummaryUpdated();
    }

    protected virtual string SummaryIdle() { return "尚未扫描。点「开始扫描」开始。"; }
    protected virtual void OnSummaryUpdated() { }

    // Forgets the previous scan. Used when the page's mode changes: the rows still in the
    // list answer the OLD mode's question (cache folders under a badge that now says 只读),
    // and leaving them there would be a lie the user cannot see through.
    protected void ResetScan()
    {
        Outcome = null;
        _scanned = false;
        List.Items.Clear();
        UpdateSummary();
    }

    internal long TotalBytes()
    {
        long b = 0;
        for (int i = 0; i < List.Items.Count; i++) b += ((ScanHit)List.Items[i]).Bytes;
        return b;
    }

    internal override void Shutdown()
    {
        _poll.Stop();
        if (_run != null) _run.Cancel();
    }
}

// ============================================ 07 扫描页（大目录 / 项目缓存）

// ONE page for the two scanning questions, because they are the same question asked
// twice: "where did the space go" and "which of these can I delete" share the scanner,
// the size-sorted list, the animation and the scope picker. As two pages they also
// needed a hand-off channel between them, and that channel had a failure mode the user
// could not act on: pick a big folder on the read-only page and, if its name was not in
// the cache list, you got "已记录，扫描完成后会自动勾选" followed by "没找到已记录的目录".
//
// WHAT THE MODE SWITCH PROTECTS - this is the whole reason it is safe:
//   * 大目录 mode is READ-ONLY. ResultList.ShowCheck is off (no checkbox is drawn and
//     rows cannot be ticked), there is no delete button, and the badge says 只读.
//   * 项目缓存 mode is the only mode that deletes, and it only ever offers directories
//     whose NAME is in the project-cache list - one source, _SystemCatalog.ps1's
//     Get-ProjectCacheNames - and every path is still re-validated by
//     04-Clean-Hidden.ps1 against its own allowlist and denylist before anything goes.
//   * "在此目录下找可清理缓存" does NOT queue the row for deletion: it re-scans INSIDE
//     that folder and lets the cache-name rule decide. That is why this page can never
//     be talked into deleting a Documents folder, and why the old hand-off's dead end
//     disappears (if there is a cache in there, the scan finds it; if there is not, the
//     empty state says so).
internal sealed class ScanPage : ScanPageBase
{
    private const int ModeDirs = 0;
    private const int ModeCaches = 1;

    // Internal because --scan-cache reuses exactly this list.
    internal static string[] CacheNames { get { return ProjectCacheNames.Names; } }

    private int _mode = ModeCaches;
    private string _rootOverride;       // set by "在此目录下找可清理缓存"
    private bool _applyingMode;         // suppresses the scope radios' own handlers

    private PillRadio _radModeDirs, _radModeCaches;
    private PillRadio _radScopeProfileDesk, _radScopeProfile, _radScopeDrive;
    private PillRadio _radFloor100, _radFloor500, _radFloor1000;
    private PillRadio _radViewDirs, _radViewFiles;
    private PillRadio _radMin20, _radMin50, _radMin100;
    private PillRadio _radDepth5, _radDepthAll;

    private FlatButton _btnDelete, _btnSelectAll, _btnSelectNone, _btnSelectBig;
    private FlatButton _btnOpen, _btnCopy, _btnScanInside;

    // Option rows whose visibility follows the mode, plus the verdict line (which only
    // the read-only mode produces). Kept as fields because a FlowLayoutPanel does not
    // reflow hidden children by itself.
    private Control _rowFloor, _rowMin, _rowDepth, _rowRowActions, _rowVerdict, _rowView;
    private Label _verdict;

    internal ScanPage(AnimBase anim) : base(anim, false)    // 项目缓存 mode owns the delete path
    {
        // Action row + a result list worth scrolling through, plus the animation strip.
        ContentMinHeight = 420;
        Title = "扫描（大目录 / 项目缓存）";
        Sub = "大目录只读 · 项目缓存可勾选删除";
        Help = "一个扫描器，两种看法。「大目录」找出真正吃掉空间的目录（只读，不勾选也不删除）："
             + "只报「最大的那一层」，父目录很大往往只是因为子目录大；还能切换看「最大文件 Top 100」。"
             + "「项目缓存」找出 node_modules / .cache / .next / __pycache__ / venv 等项目缓存目录，"
             + "可勾选删除；删除交给 04-Clean-Hidden.ps1，每个路径都要过它的白名单与黑名单，"
             + "先预演、确认之后才真正删。父级命中后不再重复统计内部嵌套缓存，所以体积不会互相叠加。"
             + "把一个文件夹拖进窗口，就会只分析那个文件夹。";
    }

    // ------------------------------------------------------------- construction

    protected override void BuildActions()
    {
        // Right-to-left inside a right-docked panel: the first one added is rightmost.
        _btnDelete = new FlatButton("删除选中项", Theme.FontUIB);
        _btnDelete.Size = new Size(150, 34);
        _btnDelete.Role = ButtonRole.Danger;
        _btnDelete.Margin = new Padding(8, 0, 0, 0);
        _btnDelete.Click += delegate { DeleteChecked(); };

        _btnSelectBig = new FlatButton("仅选大项", Theme.FontUI);
        _btnSelectBig.Size = new Size(96, 34);
        _btnSelectBig.Role = ButtonRole.Ghost;
        _btnSelectBig.Margin = new Padding(8, 0, 0, 0);
        _btnSelectBig.Click += delegate { List.CheckTop(10); };

        _btnSelectNone = new FlatButton("全不选", Theme.FontUI);
        _btnSelectNone.Size = new Size(84, 34);
        _btnSelectNone.Role = ButtonRole.Ghost;
        _btnSelectNone.Margin = new Padding(8, 0, 0, 0);
        _btnSelectNone.Click += delegate { List.SetAll(false); };

        _btnSelectAll = new FlatButton("全选", Theme.FontUI);
        _btnSelectAll.Size = new Size(76, 34);
        _btnSelectAll.Role = ButtonRole.Ghost;
        _btnSelectAll.Margin = new Padding(8, 0, 0, 0);
        _btnSelectAll.Click += delegate { List.SetAll(true); };

        Actions.Controls.Add(_btnDelete);
        Actions.Controls.Add(BtnPrimary);
        Actions.Controls.Add(_btnSelectBig);
        Actions.Controls.Add(_btnSelectNone);
        Actions.Controls.Add(_btnSelectAll);
    }

    protected override void BuildContent()
    {
        _radModeDirs = MakeRadio("大目录（只读）", false);
        _radModeCaches = MakeRadio("项目缓存（可勾选删除）", true);
        _radModeDirs.CheckedChanged += delegate { if (_radModeDirs.Checked) SetMode(ModeDirs); };
        _radModeCaches.CheckedChanged += delegate { if (_radModeCaches.Checked) SetMode(ModeCaches); };
        AddOption(ToolPage.MakeRow(ToolPage.MakeRowLabel("模式"), _radModeDirs, _radModeCaches));

        // One scope row for both modes: all three answers are meaningful in either mode
        // (大目录 只 uses the first root, 项目缓存 uses all of them).
        _radScopeProfileDesk = MakeRadio("用户目录 + 桌面", true);
        _radScopeProfile = MakeRadio("仅用户目录", false);
        _radScopeDrive = MakeRadio("整个 C 盘", false);
        AttachScopeClear(_radScopeProfileDesk);
        AttachScopeClear(_radScopeProfile);
        AttachScopeClear(_radScopeDrive);
        AddOption(ToolPage.MakeRow(ToolPage.MakeRowLabel("扫描范围"), _radScopeProfileDesk, _radScopeProfile, _radScopeDrive));

        _radFloor100 = MakeRadio("≥ 100 MB", false);
        _radFloor500 = MakeRadio("≥ 500 MB", true);
        _radFloor1000 = MakeRadio("≥ 1 GB", false);
        _rowFloor = ToolPage.MakeRow(ToolPage.MakeRowLabel("元凶阈值"), _radFloor100, _radFloor500, _radFloor1000);
        AddOption(_rowFloor);

        // Same walk, two answers. Building the file list costs one comparison per file, so
        // the view switch is a re-display of the outcome already in memory, not a rescan.
        _radViewDirs = MakeRadio("目录（谁在吃空间）", true);
        _radViewFiles = MakeRadio("最大文件 Top 100", false);
        _radViewDirs.CheckedChanged += delegate { if (_radViewDirs.Checked) RefreshList(); };
        _radViewFiles.CheckedChanged += delegate { if (_radViewFiles.Checked) RefreshList(); };
        _rowView = ToolPage.MakeRow(ToolPage.MakeRowLabel("看什么"), _radViewDirs, _radViewFiles);
        AddOption(_rowView);

        _radMin20 = MakeRadio("≥ 20 MB", false);
        _radMin50 = MakeRadio("≥ 50 MB", true);
        _radMin100 = MakeRadio("≥ 100 MB", false);
        _rowMin = ToolPage.MakeRow(ToolPage.MakeRowLabel("最小体积"), _radMin20, _radMin50, _radMin100);
        AddOption(_rowMin);

        _radDepth5 = MakeRadio("5 层（快）", true);
        _radDepthAll = MakeRadio("不限层数（全）", false);
        _rowDepth = ToolPage.MakeRow(ToolPage.MakeRowLabel("扫描深度"), _radDepth5, _radDepthAll);
        AddOption(_rowDepth);

        // Row actions live with the results rather than in the bottom bar: they all act
        // on the selected row, and the bottom bar has to stay readable for the two
        // primary actions. They are wide, so they get the full page width here.
        _btnOpen = new FlatButton("打开位置", Theme.FontUI);
        _btnOpen.Size = new Size(96, 32);
        _btnOpen.Role = ButtonRole.Ghost;
        _btnOpen.Margin = new Padding(0, 0, 8, 0);
        _btnOpen.Click += delegate { OpenSelected(); };

        _btnCopy = new FlatButton("复制路径", Theme.FontUI);
        _btnCopy.Size = new Size(96, 32);
        _btnCopy.Role = ButtonRole.Ghost;
        _btnCopy.Margin = new Padding(0, 0, 8, 0);
        _btnCopy.Click += delegate { CopySelected(); };

        _btnScanInside = new FlatButton("在此目录下找可清理缓存", Theme.FontUI);
        _btnScanInside.Size = new Size(196, 32);
        _btnScanInside.Role = ButtonRole.Ghost;
        _btnScanInside.Margin = new Padding(0, 0, 8, 0);
        _btnScanInside.Click += delegate { ScanInsideSelected(); };

        _rowRowActions = ToolPage.MakeRow(null, _btnOpen, _btnCopy, _btnScanInside);
        AddOption(_rowRowActions);

        _verdict = new Label();
        _verdict.AutoSize = false;
        _verdict.Height = 22;
        _verdict.Font = Theme.FontSmall;
        ToolPage.Role(_verdict, "card|muted");
        _rowVerdict = ToolPage.MakeRow(null, _verdict);
        // The verdict is one long sentence with a path in it, so its width has to follow the
        // row instead of being a guess: a fixed 760 px was wider than the page on a narrow
        // window, and the text was then cut at the panel edge without even an ellipsis.
        _rowVerdict.Resize += delegate
        {
            _verdict.Width = Math.Max(160, _rowVerdict.ClientSize.Width - 8);
        };
        _verdict.Width = 700;
        AddOption(_rowVerdict);

        base.BuildContent();
        ApplyMode();
    }

    private void AddOption(Control row)
    {
        Options.Controls.Add(row);
    }

    // Any deliberate scope change drops the "scan inside this folder" override - the two
    // would otherwise contradict each other with nothing on screen saying which won.
    private void AttachScopeClear(PillRadio r)
    {
        r.CheckedChanged += delegate
        {
            if (_applyingMode) return;
            if (r.Checked) ClearOverride();
        };
    }

    private void ClearOverride()
    {
        if (_rootOverride == null) return;
        _rootOverride = null;
        UpdateSummary();
    }

    // ------------------------------------------------------------- mode

    private void SetMode(int mode)
    {
        if (Busy) return;               // a scan is running with the current mode
        if (_mode == mode) return;
        _mode = mode;
        ApplyMode();
    }

    private void ApplyMode()
    {
        bool dirs = (_mode == ModeDirs);

        // A read-only list must not look tickable, and any ticks from the other mode must
        // not survive into it (a hidden tick would still count towards the summary).
        List.ShowCheck = !dirs;
        if (dirs)
        {
            for (int i = 0; i < List.Items.Count; i++) ((ScanHit)List.Items[i]).Selected = false;
        }
        ((ScanAnim)Anim).Tiles = !dirs;
        List.Invalidate();

        ModePill.SetContent(dirs ? "只读" : "需确认", dirs ? PillTone.Success : PillTone.Warn);
        PlacePill();                    // SetContent changes the width; anchoring will not re-run
        Anim.SetIdle("就绪 · " + Title, IdleHint());

        _rowFloor.Visible = dirs;
        _rowView.Visible = dirs;
        _rowVerdict.Visible = dirs;
        _rowMin.Visible = !dirs;
        _rowDepth.Visible = !dirs;
        _rowRowActions.Visible = dirs;

        _btnDelete.Visible = !dirs;
        _btnSelectAll.Visible = !dirs;
        _btnSelectNone.Visible = !dirs;
        _btnSelectBig.Visible = !dirs;

        // A conventional starting point per mode; the user can change it straight after.
        _applyingMode = true;
        if (dirs)
        {
            _radScopeDrive.Checked = true;
            _radViewDirs.Checked = true;
        }
        else _radScopeProfileDesk.Checked = true;
        _applyingMode = false;

        // The other mode's rows do not answer this mode's question.
        ResetScan();
        UpdateVerdict();
        RaiseStateChanged();
    }

    internal override string IdleHint()
    {
        return _mode == ModeDirs
            ? "点下方按钮开始扫描，结果会实时列在下面的清单里；这一模式只读，不提供删除"
            : "点下方按钮开始扫描；勾选结果后点「删除选中项」，先预演、确认之后才删除";
    }

    // ------------------------------------------------------------- scan inputs

    internal override string[] Roots
    {
        get
        {
            if (!String.IsNullOrEmpty(_rootOverride)) return new string[] { _rootOverride };

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (_radScopeDrive != null && _radScopeDrive.Checked) return new string[] { @"C:\" };
            if (_radScopeProfile != null && _radScopeProfile.Checked) return new string[] { profile };
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            return new string[] { profile, desktop };
        }
    }

    private int FloorMB()
    {
        if (_radFloor100 != null && _radFloor100.Checked) return 100;
        if (_radFloor1000 != null && _radFloor1000.Checked) return 1000;
        return 500;
    }

    private int MinMB()
    {
        if (_radMin20 != null && _radMin20.Checked) return 20;
        if (_radMin100 != null && _radMin100.Checked) return 100;
        return 50;
    }

    private int MaxDepth()
    {
        return (_radDepthAll != null && _radDepthAll.Checked) ? FastScan.MaxDepthHardCap : 5;
    }

    protected override ScanOutcome ScanWork(ScanRun run)
    {
        if (_mode == ModeDirs) return FastScan.ScanLargeDirs(Roots[0], FloorMB() * 1048576L, 200, run, 100);
        return FastScan.ScanProjectCaches(Roots, CacheNames, MaxDepth(), MinMB(), run);
    }

    // ------------------------------------------------------------- page chrome

    protected override string ScanCaption()
    {
        return _mode == ModeDirs ? "大目录分析（只读）" : "项目缓存扫描";
    }

    protected override string ReportName
    {
        get { return _mode == ModeDirs ? "largedirs-gui.json" : "hidden-gui.json"; }
    }

    protected override string SummaryIdle()
    {
        if (_mode == ModeDirs)
        {
            return _rootOverride != null
                ? "范围已指定为 " + _rootOverride + "（拖放或「在此目录下找可清理缓存」）。点「开始扫描」开始。"
                : "尚未扫描。整盘扫描通常需要数十秒（并行遍历），过程中可以随时取消。";
        }
        return "尚未扫描。勾选要删除的目录后，点「删除选中项」——会先预演，再让你确认。";
    }

    // Entry point for a folder dropped on the window. The mode is forced to the READ-ONLY
    // one on purpose: dropping a folder must never be a step towards deleting it. If the
    // user does want to clean inside it, the row action next to the results re-scans there
    // in the cache mode, where the cache-name rule decides what is deletable.
    internal void ScanFolder(string folder)
    {
        if (Busy) { Host.Warn("已经有一个扫描在跑，等它结束再拖放。"); return; }
        if (String.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            Host.Warn("拖放的目录不存在：" + folder);
            return;
        }

        SetMode(ModeDirs);
        _rootOverride = folder;
        UpdateSummary();
        Anim.SetDetail("范围来自拖放：" + folder);
        StartScan();
    }

    // The read-only mode's one-line answer: which folder - or which file - is actually
    // eating the disk.
    protected override void OnScanFinished(ScanOutcome outcome)
    {
        if (_mode != ModeDirs) return;

        // FinishScan filled the list with the directory hits; if the file view is the one
        // showing, put the file list in instead. Both answers came out of the same walk.
        RefreshList();
    }

    private void UpdateVerdict()
    {
        if (_mode != ModeDirs) return;

        if (ShowingFiles())
        {
            _verdict.Text = (Outcome != null && Outcome.TopFiles.Count > 0)
                ? String.Format("最大的单个文件：{0} — {1}",
                    Fmt.Bytes(Outcome.TopFiles[0].Bytes), Outcome.TopFiles[0].Path)
                : "这次扫描里没有记录到单个文件。";
            return;
        }

        long top = (Outcome != null && Outcome.Hits.Count > 0) ? Outcome.Hits[0].Bytes : 0;
        if (top > 0)
        {
            double share = Outcome.TotalBytes > 0 ? (100.0 * top / Outcome.TotalBytes) : 0;
            _verdict.Text = String.Format("占用最大：{0}（占本次扫描 {1} 的 {2:0.0}%）— {3}",
                Fmt.Bytes(top), Fmt.Bytes(Outcome.TotalBytes), share, Outcome.Hits[0].Path);
        }
        else
        {
            _verdict.Text = "没有超过阈值的目录 —— 可以把阈值调低再看。";
        }
    }

    private bool ShowingFiles()
    {
        return _mode == ModeDirs && _radViewFiles != null && _radViewFiles.Checked;
    }

    // Re-display the outcome that is already in memory: the walk collected both answers.
    private void RefreshList()
    {
        if (Outcome == null) return;
        List.SetHits(ShowingFiles() ? Outcome.TopFiles : Outcome.Hits);
        UpdateVerdict();
        UpdateSummary();
        if (ShowingFiles())
        {
            Anim.SetDetail(Outcome.TopFiles.Count > 0
                ? String.Format("按体积排序的最大文件：{0} 个 · 最大 {1}",
                    Outcome.TopFiles.Count, Fmt.Bytes(Outcome.TopFiles[0].Bytes))
                : "这次扫描里没有记录到单个文件");
        }
    }

    protected override void OnSummaryUpdated()
    {
        bool busy = Busy;
        bool caches = (_mode == ModeCaches);
        bool has = List.Items.Count > 0;
        bool any = List.CheckedCount > 0;

        _radModeDirs.Enabled = !busy;
        _radModeCaches.Enabled = !busy;
        _radViewDirs.Enabled = !busy;
        _radViewFiles.Enabled = !busy;

        _btnDelete.Enabled = caches && !busy && any;
        _btnDelete.Text = any ? "删除选中项 (" + List.CheckedCount + ")" : "删除选中项";
        _btnSelectAll.Enabled = caches && !busy && has;
        _btnSelectNone.Enabled = caches && !busy && has;
        _btnSelectBig.Enabled = caches && !busy && has;

        _btnOpen.Enabled = !busy && has;
        _btnCopy.Enabled = !busy && has;
        _btnScanInside.Enabled = !busy && has && _mode == ModeDirs;

        // The read-only mode has no selection, so the base line's "已选 M 项 · Y" would be
        // noise; rewrite it after the base has composed it.
        if (_mode == ModeDirs)
        {
            if (Outcome == null) Summary.Text = SummaryIdle();
            else if (List.Items.Count == 0)
                Summary.Text = ShowingFiles()
                    ? "已扫描完成 · 没有记录到任何单个文件"
                    : "已扫描完成 · 没有超过元凶阈值的目录（可把阈值调低再看）";
            else
                Summary.Text = String.Format("共 {0} 项 · {1}{2}",
                    List.Items.Count, Fmt.Bytes(TotalBytes()),
                    ShowingFiles() ? " · 单个文件，按体积排序" : "");
        }
    }

    internal override string PlanLines()
    {
        StringBuilder sb = new StringBuilder();
        if (_mode == ModeDirs)
        {
            sb.AppendLine("    模式  : 大目录（只读，内置 C# 扫描器，不调用 PowerShell）");
            sb.AppendLine("    范围  : " + String.Join(" | ", Roots[0]) + "    元凶阈值: " + FloorMB() + " MB");
            sb.AppendLine("    删除  : 这一模式没有删除路径");
        }
        else
        {
            sb.AppendLine("    阶段 1 : 04-Clean-Hidden.ps1 -ScanRoots <扫描范围> -MinMB " + MinMB()
                        + " -Paths <勾选的 N 个目录>   (预演：逐条重过白名单/黑名单并测体积)");
            sb.AppendLine("    阶段 2 : 同上 + -Execute                    (只有点确认条上的「执行删除」才走这条)");
            sb.AppendLine("    扫描范围: " + String.Join(" | ", Roots));
            sb.AppendLine("    扫描深度: " + (MaxDepth() == FastScan.MaxDepthHardCap ? "不限层数" : "5 层")
                        + "    最小体积: " + MinMB() + " MB");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------- row actions

    private ScanHit Selected()
    {
        if (List.SelectedIndex < 0) return null;
        return (ScanHit)List.Items[List.SelectedIndex];
    }

    private void OpenSelected()
    {
        ScanHit hit = Selected();
        if (hit == null) { Host.Warn("请先在列表里点一行。"); return; }
        Host.RevealInExplorer(hit.Path);
    }

    private void CopySelected()
    {
        ScanHit hit = Selected();
        if (hit == null) { Host.Warn("请先在列表里点一行。"); return; }
        try
        {
            Clipboard.SetText(hit.Path);
            Host.Info("已复制路径：\r\n" + hit.Path);
        }
        catch (Exception ex) { Host.Warn("复制失败: " + ex.Message); }
    }

    // Re-scan inside the selected folder in 项目缓存 mode. Deliberately NOT "queue this
    // row for deletion": the cache-name rule and 04's allowlist stay the only things that
    // decide what may be removed.
    private void ScanInsideSelected()
    {
        ScanHit hit = Selected();
        if (hit == null) { Host.Warn("请先在列表里点一行。"); return; }
        if (ShowingFiles())
        {
            Host.Warn("「在此目录下找可清理缓存」接受的是一行目录。\r\n"
                    + "请先把「看什么」切回「目录」，选中那个文件夹。");
            return;
        }

        SetMode(ModeCaches);
        _rootOverride = hit.Path;
        UpdateSummary();
        Anim.SetDetail("范围已改为 " + hit.Path);
        StartScan();
    }

    // ------------------------------------------------------------- delete path

    // Phase 1 = a plan produced by 04 itself (it re-validates every path against its own
    // allowlist and denylist); phase 2 = the same list with -Execute. Nothing is deleted
    // before the confirmation strip is clicked.
    private void DeleteChecked()
    {
        List<ScanHit> paths = new List<ScanHit>();
        for (int i = 0; i < List.Items.Count; i++)
        {
            ScanHit h = (ScanHit)List.Items[i];
            if (h.Selected) paths.Add(h);
        }
        if (paths.Count == 0) { Host.Warn("请先勾选要删除的目录。"); return; }

        StringBuilder sb = new StringBuilder();
        sb.Append("-ScanRoots ");
        string[] roots = Roots;
        for (int i = 0; i < roots.Length; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("'").Append(roots[i].Replace("'", "''")).Append("'");
        }
        sb.Append(" -MinMB ").Append(MinMB());
        sb.Append(" -Paths ");
        for (int i = 0; i < paths.Count; i++)
        {
            // Wrap every 40 paths for the log's sake - the comma still has to be there,
            // or the array literal would break at the newline.
            if (i > 0) sb.Append((i % 40 == 0) ? ",`r`n  " : ",");
            sb.Append("'").Append(paths[i].Path.Replace("'", "''")).Append("'");
        }

        string args = sb.ToString();
        long bytes = List.CheckedBytes;

        PendingRun run = new PendingRun();
        run.Title = String.Format("删除 {0} 个项目缓存（{1}）", paths.Count, Fmt.Bytes(bytes));
        run.Script = "04-Clean-Hidden.ps1";
        run.Args = delegate(bool execute) { return args + (execute ? " -Execute" : ""); };
        run.ReadOnly = false;
        run.ReportTag = "hidden";       // names hidden.dryrun.json for the confirm strip
        run.Owner = this;
        run.Confirm = String.Format("已预演：将删除勾选的 {0} 个目录（{1}）。确认执行？",
            paths.Count, Fmt.Bytes(bytes));

        Host.RunScript(run, false);
    }

    // After a real run, drop the folders that are gone and update the totals.
    internal override void OnRunFinished(string title, bool execute, int exitCode)
    {
        if (!execute) return;

        List<ScanHit> remaining = new List<ScanHit>();
        List<ScanHit> hits = List.Hits();
        int gone = 0;
        for (int i = 0; i < hits.Count; i++)
        {
            if (!Directory.Exists(hits[i].Path))
            {
                gone++;
                continue;
            }
            remaining.Add(hits[i]);
        }

        List.SetHits(remaining);
        UpdateSummary();
        if (gone > 0)
        {
            Anim.EndRun(String.Format("已删除 {0} 个目录", gone), "列表已刷新，其余保留");
            Host.Info(String.Format("已删除 {0} 个目录；仍存在的 {1} 个已保留在列表里。",
                gone, remaining.Count));
        }
    }
}
