// CLean · 窗口版 -- WinForms launcher for the same PowerShell payload.
//
// Build:  .\src\Build.ps1 -Gui      (in-box csc.exe, no SDK required)
// Target: Windows 10 / 11 with .NET Framework 4.x (present by default).
//
// Sibling of Launcher.cs. Same idea - every .ps1 travels embedded in this single
// EXE, is extracted to %LOCALAPPDATA%\CLean\runtime on start, and is then
// driven as a child process - but the console menu is replaced by a window.
//
// UI: flat, card based. Everything is drawn in code (no designer, no image assets,
// no third-party library): rounded cards, pill radios/checks, an owner-drawn action
// list, a dark console panel and a segmented usage bar.
//
// APPEARANCE: every colour the UI paints comes from Theme, a facade over one active
// Palette. The palette is generated from two knobs - a mode (light / dark / follow
// Windows) and an accent colour - so a new theme is a table row, not a repaint. The
// choice is stored in %LOCALAPPDATA%\CLean\theme.txt and applied to the live
// control tree without restarting (see ApplyPaletteAll / IThemed).
//
// SAFETY CONTRACT (identical to the console launcher, and to the scripts' own):
//   * a destructive action is first run WITHOUT -Execute; its plan is streamed to
//     the console pane; only after the run finishes successfully (exit code 0 and
//     not cancelled) does the confirmation strip appear, and only a deliberate
//     click re-runs the same action with -Execute;
//   * the extracted scripts re-verify every path against their own allowlist, so
//     nothing outside it can be deleted even if this UI is misused.
//
// Deliberately conservative C# 5 syntax: the in-box csc.exe on older Windows
// builds may not accept newer language versions.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

internal static class Program
{
    internal const string AppName = "CLean";
    // Baked in by src/Build.ps1 from the VERSION file at the repo root.
    internal const string AppVersion = VersionInfo.Version;
    // Kept short on purpose: the header's action group is right-docked and the
    // window can be as narrow as 1060 px, so the tagline must not grow.
    internal const string AppTagline = "窗口版 v" + VersionInfo.Version + " · 每个功能独立动画 · 页面无日志 · 内置快速扫描器";

    [STAThread]
    private static int Main(string[] args)
    {
        HideOwnConsole();
        UpdateCheck.RunningVersion = AppVersion;

        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
        }
        catch { /* style APIs unavailable: the window still works */ }

        // A crash inside a UI event must not vanish silently: log it and say where.
        Application.ThreadException += new System.Threading.ThreadExceptionEventHandler(OnThreadException);

        // Appearance and UI state first: the saved mode/accent and the rail's collapsed
        // state have to be in place before the first control is constructed, or the
        // window would visibly flash the defaults.
        Theme.LoadPreference();
        Prefs.Load();

        if (args != null && args.Length > 0)
        {
            // Auditable dry contract: --print-plan writes, for every page, the exact
            // command line the button would run in each phase, and exits. It never
            // touches the disk, so it is safe to run at any time.
            if (args[0] == "--print-plan" || args[0] == "-print-plan") return PrintPlan();

            // Headless scans, used to audit the built-in scanner (and to compare it with
            // the PowerShell one). Read-only by construction: they only measure.
            if ((args[0] == "--scan-dirs" || args[0] == "--scan-cache") && args.Length >= 2)
                return ScanCli(args[1], args[0] == "--scan-cache");

            // Same contract as the console build: 0 = up to date, 10 = newer
            // release exists, 1 = the check itself failed. A windowed process has
            // nowhere to print, so the verdict also lands in update-cli.json next
            // to the other machine-readable reports.
            if (args[0] == "--version" || args[0] == "-version")
            {
                Console.WriteLine(AppName + " v" + AppVersion);
                return 0;
            }
            if (args[0] == "--check-update" || args[0] == "-check-update")
            {
                bool force = args.Length > 1 && (args[1] == "--force" || args[1] == "-force");
                return CheckUpdateCli(force);
            }
        }

        try
        {
            Application.Run(new MainForm());
            return 0;
        }
        catch (Exception ex)
        {
            Report("启动", ex);
            return 1;
        }
    }

    private static int CheckUpdateCli(bool force)
    {
        UpdateInfo info = UpdateCheck.Check(force);

        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CLean");
            Directory.CreateDirectory(dir);

            StringBuilder sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"Tool\": \"CLean-GUI\",\r\n");
            sb.Append("  \"Current\": \"").Append(Json.Escape(AppVersion)).Append("\",\r\n");
            sb.Append("  \"Ok\": ").Append(info.Ok ? "true" : "false").Append(",\r\n");
            sb.Append("  \"NoRelease\": ").Append(info.NoRelease ? "true" : "false").Append(",\r\n");
            sb.Append("  \"FromCache\": ").Append(info.FromCache ? "true" : "false").Append(",\r\n");
            sb.Append("  \"Latest\": \"").Append(Json.Escape(info.Latest == null ? "" : info.Latest)).Append("\",\r\n");
            sb.Append("  \"IsNewer\": ").Append(info.IsNewer ? "true" : "false").Append(",\r\n");
            sb.Append("  \"PageUrl\": \"").Append(Json.Escape(info.PageUrl)).Append("\",\r\n");
            sb.Append("  \"Error\": \"").Append(Json.Escape(info.Error == null ? "" : info.Error)).Append("\"\r\n");
            sb.Append("}\r\n");

            File.WriteAllText(Path.Combine(dir, "update-cli.json"), sb.ToString(), new UTF8Encoding(true));
        }
        catch { /* reporting is best-effort; the exit code is the contract */ }

        if (!info.Ok) return 1;
        return info.IsNewer ? 10 : 0;
    }

    // --scan-dirs <root>  : large-directory analysis over one root
    // --scan-cache <root> : project-cache scan over one root
    // Both write reports\scan-cli.json next to the GUI's other reports and print the
    // summary to gui-crash.log only on failure; success is proven by the JSON.
    private static int ScanCli(string root, bool caches)
    {
        try
        {
            ScanRun run = new ScanRun(root);
            ScanOutcome outcome = caches
                ? FastScan.ScanProjectCaches(new string[] { root },
                      HiddenCachePage.CacheNames, 5, 50, run)
                : FastScan.ScanLargeDirs(root, 500L * 1048576L, 200, run);

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CLean", "runtime", "reports");
            Directory.CreateDirectory(dir);

            StringBuilder sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"Mode\": \"").Append(caches ? "cache" : "largedirs").Append("\",\r\n");
            sb.Append("  \"Root\": \"").Append(Json.Escape(root)).Append("\",\r\n");
            sb.Append("  \"TotalMB\": ").Append((outcome.TotalBytes / 1048576.0).ToString("0.0")).Append(",\r\n");
            sb.Append("  \"Files\": ").Append(outcome.Files).Append(",\r\n");
            sb.Append("  \"Dirs\": ").Append(outcome.Dirs).Append(",\r\n");
            sb.Append("  \"ElapsedMs\": ").Append(outcome.ElapsedMs).Append(",\r\n");
            sb.Append("  \"Threads\": ").Append(outcome.Threads).Append(",\r\n");
            sb.Append("  \"Denied\": ").Append(outcome.Denied).Append(",\r\n");
            sb.Append("  \"ReparseSkipped\": ").Append(outcome.ReparseSkipped).Append(",\r\n");
            sb.Append("  \"Items\": [\r\n");
            for (int i = 0; i < outcome.Hits.Count; i++)
            {
                if (i > 0) sb.Append(",\r\n");
                sb.Append("    { \"MB\": ").Append((outcome.Hits[i].Bytes / 1048576.0).ToString("0.0"))
                  .Append(", \"Files\": ").Append(outcome.Hits[i].Files)
                  .Append(", \"Path\": \"").Append(Json.Escape(outcome.Hits[i].Path)).Append("\" }");
            }
            sb.Append("\r\n  ]\r\n}\r\n");

            File.WriteAllText(Path.Combine(dir, "scan-cli.json"), sb.ToString(), new UTF8Encoding(true));
            return 0;
        }
        catch (Exception ex)
        {
            Report("扫描", ex);
            return 1;
        }
    }

    private static int PrintPlan()
    {
        string path = "(无法写入)";
        try
        {
            MainForm form = new MainForm();
            string plan = form.DumpPlan();

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CLean");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, "gui-command-plan.txt");
            // UTF-8 WITH BOM: this file is meant to be opened in Notepad on a
            // Chinese Windows, which otherwise reads BOM-less UTF-8 as GBK.
            File.WriteAllText(path, plan, new UTF8Encoding(true));
            return 0;
        }
        catch (Exception ex)
        {
            Report("打印计划", ex);
            return 1;
        }
    }

    private static void OnThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
    {
        Report("界面", e.Exception);
    }

    // Writes the full exception to %LOCALAPPDATA%\CLean\gui-crash.log and
    // tells the user the path, so a failure on someone else's machine is
    // diagnosable without a debugger.
    internal static void Report(string where, Exception ex)
    {
        string path = "(无法写入日志)";
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CLean");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, "gui-crash.log");
            File.AppendAllText(path, String.Format(
                "===== {0:yyyy-MM-dd HH:mm:ss}  [{1}] =====\r\n{2}\r\n\r\n",
                DateTime.Now, where, ex));
        }
        catch { }

        MessageBox.Show("程序出错(" + where + "):\r\n" + ex.Message + "\r\n\r\n详细信息已写入:\r\n" + path,
            AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // A windowed app must not flash a console window every time the cleanup
    // scripts shell out to vssadmin / dism / fsutil. Giving THIS process a hidden
    // console means those child console apps inherit it instead of allocating a
    // visible one of their own.
    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("user32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void HideOwnConsole()
    {
        try
        {
            // Launched from an existing console (dev runs): reuse it, do not hide it.
            if (GetConsoleWindow() != IntPtr.Zero) return;
            if (!AllocConsole()) return;
            ShowWindow(GetConsoleWindow(), 0); // SW_HIDE
        }
        catch { /* no console at all: children may briefly flash, which is cosmetic */ }
    }
}

// ============================================================== design tokens

// One complete colour set, generated by Theme.Build() from a mode plus an accent.
// Nothing here is hand-tuned per theme: the derived colours (hover, soft tint, the
// readable ink for text on a filled surface) are computed, so adding an accent is a
// single table row in Theme.Accents and cannot produce an unreadable combination.
internal sealed class Palette
{
    internal string Mode;          // "light" | "dark" (never "system")
    internal string AccentId;
    internal string AccentName;

    internal Color Bg, Card, Border, Text, TextMuted, TextFaint;
    internal Color Accent, AccentHot, AccentDeep, AccentSoft, OnAccent;
    internal Color Success, SuccessSoft, Danger, DangerSoft, DangerHot;
    internal Color Warn, WarnSoft, WarnBorder;
    internal Color Hover, Track, ChipBg, CheckBorder, UsageWarn;
    internal Color ConsoleBg, ConsoleBd, ConsoleFg, ConsoleMuted, ConsoleBtn, ConsoleBtnHot;

    internal bool Dark { get { return Mode == "dark"; } }
}

internal sealed class AccentDef
{
    internal readonly string Id;
    internal readonly string Name;
    internal readonly Color Light;   // on light backgrounds
    internal readonly Color Dark;    // on dark backgrounds: brighter by design

    internal AccentDef(string id, string name, Color light, Color dark)
    {
        Id = id;
        Name = name;
        Light = light;
        Dark = dark;
    }
}

// Implemented by every control that CACHES a theme colour in a field. Controls that
// only read Theme.* inside OnPaint need no work on a theme switch beyond the
// Invalidate() that re-applying their Tag role already performs.
//
// (The implementing method has to be declared public even though this interface is
// internal: C# 5 rejects a non-public implicit interface implementation with CS0737.)
internal interface IThemed
{
    void ApplyPalette();
}

// The rest of the UI only ever says Theme.Accent / Theme.Card / ...; those names are
// now properties over the active palette, so a runtime switch reaches every painting
// control without a single call site changing.
internal static class Theme
{
    internal const string ModeSystem = "system";   // follow Windows
    internal const string ModeLight = "light";
    internal const string ModeDark = "dark";

    // Ink used on a bright fill (see On()): a dark-mode accent is bright enough that
    // white text on it would be the unreadable choice.
    internal static readonly Color Ink = Color.FromArgb(18, 20, 26);

    private static readonly AccentDef[] Accents = new AccentDef[]
    {
        new AccentDef("blue",   "蓝",   Color.FromArgb(43, 108, 246), Color.FromArgb(110, 156, 255)),
        new AccentDef("teal",   "青",   Color.FromArgb(14, 138, 138), Color.FromArgb(63, 199, 199)),
        new AccentDef("green",  "绿",   Color.FromArgb(21, 138, 78),  Color.FromArgb(78, 203, 138)),
        new AccentDef("orange", "橙",   Color.FromArgb(194, 105, 26), Color.FromArgb(240, 169, 60)),
        new AccentDef("purple", "紫",   Color.FromArgb(109, 69, 217), Color.FromArgb(169, 146, 255)),
        new AccentDef("rose",   "玫红", Color.FromArgb(198, 42, 110), Color.FromArgb(255, 134, 176))
    };

    private static string _mode = ModeSystem;
    private static string _accentId = "blue";
    private static Palette _current = Build(ModeLight, "blue");

    internal static Palette Current { get { return _current; } }
    internal static string Mode { get { return _mode; } }
    internal static string AccentId { get { return _accentId; } }
    internal static AccentDef[] AccentList { get { return Accents; } }

    internal static Color Bg { get { return _current.Bg; } }
    internal static Color Card { get { return _current.Card; } }
    internal static Color Border { get { return _current.Border; } }
    internal static Color Text { get { return _current.Text; } }
    internal static Color TextMuted { get { return _current.TextMuted; } }
    internal static Color TextFaint { get { return _current.TextFaint; } }

    internal static Color Accent { get { return _current.Accent; } }
    internal static Color AccentHot { get { return _current.AccentHot; } }
    internal static Color AccentDark { get { return _current.AccentDeep; } }
    internal static Color AccentDeep { get { return _current.AccentDeep; } }
    internal static Color AccentSoft { get { return _current.AccentSoft; } }
    internal static Color OnAccent { get { return _current.OnAccent; } }

    internal static Color Success { get { return _current.Success; } }
    internal static Color SuccessSoft { get { return _current.SuccessSoft; } }
    internal static Color Danger { get { return _current.Danger; } }
    internal static Color DangerSoft { get { return _current.DangerSoft; } }
    internal static Color DangerHot { get { return _current.DangerHot; } }
    internal static Color Warn { get { return _current.Warn; } }
    internal static Color WarnSoft { get { return _current.WarnSoft; } }
    internal static Color WarnBorder { get { return _current.WarnBorder; } }

    internal static Color Hover { get { return _current.Hover; } }
    internal static Color Track { get { return _current.Track; } }
    internal static Color ChipBg { get { return _current.ChipBg; } }
    internal static Color CheckBorder { get { return _current.CheckBorder; } }
    internal static Color UsageWarn { get { return _current.UsageWarn; } }

    internal static Color ConsoleBg { get { return _current.ConsoleBg; } }
    internal static Color ConsoleBd { get { return _current.ConsoleBd; } }
    internal static Color ConsoleFg { get { return _current.ConsoleFg; } }
    internal static Color ConsoleMuted { get { return _current.ConsoleMuted; } }
    internal static Color ConsoleBtn { get { return _current.ConsoleBtn; } }
    internal static Color ConsoleBtnHot { get { return _current.ConsoleBtnHot; } }

    internal static readonly Font FontUI = new Font("Microsoft YaHei UI", 9F);
    internal static readonly Font FontUIB = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
    internal static readonly Font FontSmall = new Font("Microsoft YaHei UI", 8.25F);
    internal static readonly Font FontSmallB = new Font("Microsoft YaHei UI", 8.25F, FontStyle.Bold);
    internal static readonly Font FontSection = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
    internal static readonly Font FontTitle = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
    internal static readonly Font FontAction = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
    internal static readonly Font FontBig = new Font("Microsoft YaHei UI", 19F, FontStyle.Bold);
    internal static readonly Font FontMono = new Font("Consolas", 9F);
    internal static readonly Font FontLogo = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);

    internal const TextFormatFlags CenterFlags =
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

    internal const TextFormatFlags LeftFlags =
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
        TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;

    // ------------------------------------------------------------ theme engine

    // Raised after the main window has re-applied the palette, so side windows (the log
    // window) can recolour themselves without a second dispatch mechanism.
    internal static event Action PaletteChanged;

    internal static void RaisePaletteChanged()
    {
        Action handler = PaletteChanged;
        if (handler != null)
        {
            try { handler(); }
            catch { /* a listener must never break the window */ }
        }
    }

    internal static void SetMode(string mode)
    {
        if (mode != ModeSystem && mode != ModeLight && mode != ModeDark) return;
        _mode = mode;
        Refresh();
    }

    internal static void SetAccent(string id)
    {
        if (FindAccent(id) == null) return;
        _accentId = id;
        Refresh();
    }

    // Rebuilds the active palette from the two knobs. Called when either changes and
    // again when Windows switches light/dark while "follow system" is selected.
    internal static void Refresh()
    {
        _current = Build(ResolveMode(), _accentId);
    }

    // "system" is not a palette of its own: it resolves to the light or the dark one.
    internal static string ResolveMode()
    {
        if (_mode == ModeDark) return ModeDark;
        if (_mode == ModeLight) return ModeLight;
        return SystemPrefersDark() ? ModeDark : ModeLight;
    }

    // Windows 10 1809+ records the app-theme choice here; 0 means dark. A missing
    // value counts as light, which is what Windows itself falls back to, and any
    // unreadable/blocked key must never be fatal - the UI just stays light.
    internal static bool SystemPrefersDark()
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                       @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                if (k != null)
                {
                    object v = k.GetValue("AppsUseLightTheme");
                    if (v is int) return ((int)v) == 0;
                }
            }
        }
        catch { }
        return false;
    }

    internal static Color AccentColorOf(string id)
    {
        AccentDef d = FindAccent(id);
        if (d == null) d = Accents[0];
        return _current.Dark ? d.Dark : d.Light;
    }

    private static AccentDef FindAccent(string id)
    {
        for (int i = 0; i < Accents.Length; i++)
            if (Accents[i].Id == id) return Accents[i];
        return null;
    }

    internal static string ModeName(string mode)
    {
        if (mode == ModeDark) return "深色";
        if (mode == ModeLight) return "浅色";
        return "跟随系统";
    }

    // Drives both the appearance button's tooltip and the menu header, so what the
    // user sees always states the mode AND what "follow system" currently resolves to.
    internal static string Describe()
    {
        string s = ModeName(_mode);
        if (_mode == ModeSystem) s += "（当前 " + (_current.Dark ? "深色" : "浅色") + "）";
        return s + " · 强调色 " + _current.AccentName;
    }

    private static string PreferencePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CLean", "theme.txt");
    }

    // Two ASCII "key=value" lines (%LOCALAPPDATA%\CLean\theme.txt) - easy to
    // read, hand-edit or delete. A missing or damaged file is never an error: the
    // defaults (follow system, blue) simply stand.
    internal static void LoadPreference()
    {
        try
        {
            string path = PreferencePath();
            if (File.Exists(path))
            {
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim().ToLowerInvariant();
                    if (key == "mode" && (val == ModeDark || val == ModeLight || val == ModeSystem)) _mode = val;
                    else if (key == "accent" && FindAccent(val) != null) _accentId = val;
                }
            }
        }
        catch { /* unreadable profile: keep the defaults */ }
        Refresh();
    }

    internal static void SavePreference()
    {
        try
        {
            string path = PreferencePath();
            string dir = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path,
                "mode=" + _mode + "\r\naccent=" + _accentId + "\r\n",
                new UTF8Encoding(false));
        }
        catch { /* a read-only profile must not break the UI */ }
    }

    // ------------------------------------------------------------ palette build

    private static Palette Build(string mode, string accentId)
    {
        bool dark = (mode == ModeDark);
        AccentDef ad = FindAccent(accentId);
        if (ad == null) ad = Accents[0];

        Palette p = new Palette();
        p.Mode = dark ? ModeDark : ModeLight;
        p.AccentId = ad.Id;
        p.AccentName = ad.Name;

        if (dark)
        {
            p.Bg = Color.FromArgb(14, 16, 20);
            p.Card = Color.FromArgb(22, 25, 34);
            p.Border = Color.FromArgb(39, 44, 56);
            p.Text = Color.FromArgb(231, 234, 242);
            p.TextMuted = Color.FromArgb(152, 162, 179);
            p.TextFaint = Color.FromArgb(108, 118, 133);
            p.Hover = Color.FromArgb(29, 34, 48);
            p.Track = Color.FromArgb(35, 41, 56);
            p.ChipBg = Color.FromArgb(30, 36, 49);
            p.CheckBorder = Color.FromArgb(58, 65, 82);
            p.UsageWarn = Color.FromArgb(224, 163, 60);

            p.Success = Color.FromArgb(76, 199, 127);
            p.SuccessSoft = Color.FromArgb(23, 48, 31);
            p.Danger = Color.FromArgb(255, 107, 107);
            p.DangerSoft = Color.FromArgb(58, 31, 31);
            p.DangerHot = Lighten(p.Danger, 0.10f);
            p.Warn = Color.FromArgb(240, 180, 74);
            p.WarnSoft = Color.FromArgb(51, 40, 15);
            p.WarnBorder = Color.FromArgb(94, 74, 30);

            // The console pane keeps its own dark tokens in both modes; on a dark
            // window it goes a shade deeper so it still reads as a distinct surface.
            p.ConsoleBg = Color.FromArgb(11, 13, 17);
            p.ConsoleBd = Color.FromArgb(38, 43, 54);
            p.ConsoleFg = Color.FromArgb(214, 218, 227);
            p.ConsoleMuted = Color.FromArgb(138, 147, 163);
            p.ConsoleBtn = Color.FromArgb(35, 39, 50);
            p.ConsoleBtnHot = Color.FromArgb(48, 54, 68);
        }
        else
        {
            p.Bg = Color.FromArgb(243, 245, 249);
            p.Card = Color.White;
            p.Border = Color.FromArgb(226, 231, 239);
            p.Text = Color.FromArgb(30, 36, 48);
            p.TextMuted = Color.FromArgb(107, 116, 128);
            p.TextFaint = Color.FromArgb(150, 158, 170);
            p.Hover = Color.FromArgb(240, 243, 249);
            p.Track = Color.FromArgb(231, 236, 244);
            p.ChipBg = Color.FromArgb(242, 245, 250);
            p.CheckBorder = Color.FromArgb(203, 211, 223);
            p.UsageWarn = Color.FromArgb(234, 140, 20);

            p.Success = Color.FromArgb(21, 145, 66);
            p.SuccessSoft = Color.FromArgb(230, 246, 235);
            p.Danger = Color.FromArgb(214, 44, 44);
            p.DangerSoft = Color.FromArgb(253, 235, 235);
            p.DangerHot = Darken(p.Danger, 0.08f);
            p.Warn = Color.FromArgb(166, 76, 5);
            p.WarnSoft = Color.FromArgb(255, 248, 229);
            p.WarnBorder = Color.FromArgb(240, 213, 152);

            p.ConsoleBg = Color.FromArgb(21, 23, 29);
            p.ConsoleBd = Color.FromArgb(38, 42, 52);
            p.ConsoleFg = Color.FromArgb(214, 218, 227);
            p.ConsoleMuted = Color.FromArgb(138, 147, 163);
            p.ConsoleBtn = Color.FromArgb(35, 39, 50);
            p.ConsoleBtnHot = Color.FromArgb(48, 54, 68);
        }

        p.Accent = dark ? ad.Dark : ad.Light;
        p.AccentHot = dark ? Lighten(p.Accent, 0.12f) : Darken(p.Accent, 0.10f);
        p.AccentDeep = dark ? Lighten(p.Accent, 0.08f) : Darken(p.Accent, 0.22f);
        // The soft tint is the selected-row / hovered-chip surface: a wash of the
        // accent over the card colour, which keeps the accent recognisable in both modes.
        p.AccentSoft = dark ? Mix(p.Accent, p.Card, 0.84f) : Mix(p.Accent, Color.White, 0.90f);
        p.OnAccent = On(p.Accent);
        return p;
    }

    // Perceived brightness, used only to pick readable ink on a filled surface. The
    // bright accents of the dark palettes therefore get near-black text, while the
    // deeper light-mode ones and the light-mode danger red keep white.
    internal static double Lum(Color c)
    {
        return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
    }

    internal static Color On(Color c)
    {
        return Lum(c) > 0.55 ? Ink : Color.White;
    }

    internal static GraphicsPath Round(Rectangle r, int radius)
    {
        GraphicsPath p = new GraphicsPath();
        if (r.Width <= 0 || r.Height <= 0) { p.AddRectangle(new Rectangle(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height))); return p; }

        int d = radius * 2;
        if (radius <= 0 || d > r.Width || d > r.Height) { p.AddRectangle(r); return p; }

        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    internal static void FillRound(Graphics g, Rectangle r, int radius, Color c)
    {
        if (c.A == 0) return;
        using (GraphicsPath p = Round(r, radius))
        using (SolidBrush b = new SolidBrush(c))
            g.FillPath(b, p);
    }

    internal static void DrawRound(Graphics g, Rectangle r, int radius, Color c, float w)
    {
        using (GraphicsPath p = Round(r, radius))
        using (Pen pen = new Pen(c, w))
            g.DrawPath(pen, p);
    }

    internal static Color Mix(Color a, Color b, float t)
    {
        if (t < 0f) t = 0f;
        if (t > 1f) t = 1f;
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    internal static Color Darken(Color c, float t) { return Mix(c, Color.Black, t); }
    internal static Color Lighten(Color c, float t) { return Mix(c, Color.White, t); }
}

// ============================================================== ui primitives

// A rounded, bordered white surface. Paints its parent's colour behind the corners
// so the rounding is real on any background.
internal class Card : Panel, IThemed
{
    private int _radius = 12;
    private Color _border = Theme.Border;

    // Where this card's colours come from. Properties rather than fields, so setting
    // one immediately re-colours the card and no call site has to remember to.
    private bool _consoleStyle;
    private bool _warnStyle;

    internal bool ConsoleStyle
    {
        get { return _consoleStyle; }
        set { _consoleStyle = value; ApplyPalette(); }
    }

    internal bool WarnStyle
    {
        get { return _warnStyle; }
        set { _warnStyle = value; ApplyPalette(); }
    }

    internal int Radius
    {
        get { return _radius; }
        set { _radius = value; Invalidate(); }
    }

    internal Color BorderColor
    {
        get { return _border; }
        set { _border = value; Invalidate(); }
    }

    internal Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        ApplyPalette();
    }

    public void ApplyPalette()
    {
        if (ConsoleStyle) { BackColor = Theme.ConsoleBg; _border = Theme.ConsoleBd; }
        else if (WarnStyle) { BackColor = Theme.WarnSoft; _border = Theme.WarnBorder; }
        else { BackColor = Theme.Card; _border = Theme.Border; }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);

        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.FillRound(g, r, _radius, BackColor);
        if (_border != Color.Empty) Theme.DrawRound(g, r, _radius, _border, 1f);

        base.OnPaint(e);
    }
}

// A full-width band (header / action bar) with an optional hairline rule.
internal class Band : Panel, IThemed
{
    internal bool TopLine;
    internal bool BottomLine;
    internal Color LineColor = Theme.Border;

    internal Band()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        ApplyPalette();
    }

    public void ApplyPalette()
    {
        BackColor = Theme.Card;
        LineColor = Theme.Border;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        using (Pen p = new Pen(LineColor))
        {
            if (TopLine) e.Graphics.DrawLine(p, 0, 0, Width, 0);
            if (BottomLine) e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
        base.OnPaint(e);
    }
}

// Semantic colour role of a Pill. Storing the role (not the two colours) is what lets
// the badge be recoloured when the theme changes.
internal enum PillTone
{
    Neutral,
    Accent,
    Success,
    Danger,
    Warn
}

// Small rounded status badge (system / privilege / mode).
internal class Pill : Control, IThemed
{
    private string _text;
    private PillTone _tone;
    private Color _fg;
    private Color _bg;
    private readonly Font _font;

    internal Pill(string text, PillTone tone, Font font)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _text = text;
        _tone = tone;
        _font = font;
        Height = 24;
        AutoSize = false;
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
        Width = Measure(text);
        ApplyPalette();
    }

    private int Measure(string t)
    {
        return TextRenderer.MeasureText(t, _font).Width + 20;
    }

    internal void SetContent(string text, PillTone tone)
    {
        _text = text;
        _tone = tone;
        Width = Measure(text);
        ApplyPalette();
    }

    public void ApplyPalette()
    {
        switch (_tone)
        {
            case PillTone.Accent: _fg = Theme.AccentDeep; _bg = Theme.AccentSoft; break;
            case PillTone.Success: _fg = Theme.Success; _bg = Theme.SuccessSoft; break;
            case PillTone.Danger: _fg = Theme.Danger; _bg = Theme.DangerSoft; break;
            case PillTone.Warn: _fg = Theme.Warn; _bg = Theme.WarnSoft; break;
            default: _fg = Theme.Mix(Theme.Text, Theme.Card, 0.15f); _bg = Theme.Hover; break;
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Card);

        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.FillRound(g, r, Height / 2, _bg);
        TextRenderer.DrawText(g, _text, _font, r, _fg, Theme.CenterFlags);
    }
}

// The visual variant a FlatButton takes from the palette.
internal enum ButtonRole
{
    Ghost,      // card fill, hairline border
    Primary,    // accent fill, readable ink (white or near-black, see Theme.On)
    Danger,     // red fill, readable ink
    GhostWarn,  // card fill with the amber border/text of the confirm strip
    Console     // the small button living inside the dark console pane
}

// Flat button with five palette-driven variants: primary (accent), ghost (outline),
// danger, the amber confirm ghost, and the console button.
internal class FlatButton : Button, IThemed
{
    internal Color Fill = Theme.Card;
    internal Color FillHot = Color.Empty;
    internal Color FillDown = Color.Empty;
    internal Color TextColor = Theme.Text;
    internal Color BorderColor = Color.Empty;
    internal int Radius = 8;

    private ButtonRole _role = ButtonRole.Ghost;
    private bool _hot;
    private bool _down;

    internal ButtonRole Role
    {
        get { return _role; }
        set { _role = value; ApplyPalette(); }
    }

    internal FlatButton(string text, Font font)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Text = text;
        Font = font;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Size = new Size(110, 34);
        Cursor = Cursors.Hand;
        ApplyPalette();
    }

    public void ApplyPalette()
    {
        switch (_role)
        {
            case ButtonRole.Primary:
                Fill = Theme.Accent;
                FillHot = Theme.AccentHot;
                FillDown = Theme.AccentHot;
                TextColor = Theme.OnAccent;
                BorderColor = Color.Empty;
                break;

            case ButtonRole.Danger:
                Fill = Theme.Danger;
                FillHot = Theme.DangerHot;
                FillDown = Theme.DangerHot;
                TextColor = Theme.On(Theme.Danger);
                BorderColor = Color.Empty;
                break;

            case ButtonRole.GhostWarn:
                Fill = Theme.Card;
                FillHot = Theme.WarnSoft;
                FillDown = Theme.WarnSoft;
                TextColor = Theme.Warn;
                BorderColor = Theme.WarnBorder;
                break;

            case ButtonRole.Console:
                Fill = Theme.ConsoleBtn;
                FillHot = Theme.ConsoleBtnHot;
                FillDown = Theme.ConsoleBtnHot;
                TextColor = Theme.ConsoleMuted;
                BorderColor = Color.Empty;
                break;

            default:
                Fill = Theme.Card;
                FillHot = Theme.Hover;
                FillDown = Theme.Hover;
                TextColor = Theme.Text;
                BorderColor = Theme.Border;
                break;
        }
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { _down = false; _hot = false; Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);

        Color fill = Fill;
        if (!Enabled)
        {
            fill = Theme.Mix(Fill, Theme.Bg, 0.55f);
        }
        else if (_down)
        {
            fill = (FillDown != Color.Empty) ? FillDown : Theme.Darken(Fill, 0.08f);
        }
        else if (_hot)
        {
            fill = (FillHot != Color.Empty) ? FillHot : Theme.Darken(Fill, 0.04f);
        }

        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.FillRound(g, r, Radius, fill);

        if (BorderColor != Color.Empty)
            Theme.DrawRound(g, r, Radius, Enabled ? BorderColor : Theme.Border, 1f);

        Color fg = Enabled ? TextColor : Theme.TextFaint;
        TextRenderer.DrawText(g, Text, Font, r, fg, Theme.CenterFlags);

        // NOTE: Button.OnPaint paints the default Windows chrome (background, text,
        // focus cues). Calling it here would draw that on top of everything above,
        // so this control is fully custom-painted and never chains to the base.
    }
}

// Radio drawn as a pill: the segmented-control look, no OS glyph.
internal class PillRadio : RadioButton, IThemed
{
    private bool _hot;
    private readonly int _pad = 14;

    internal PillRadio(string text)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Text = text;
        Font = Theme.FontUI;
        AutoSize = false;
        Height = 30;
        Width = TextRenderer.MeasureText(text, Font).Width + _pad * 2;
        Cursor = Cursors.Hand;
        CheckedChanged += delegate { Invalidate(); };
    }

    public void ApplyPalette()
    {
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Card);

        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        if (Checked)
        {
            Theme.FillRound(g, r, Height / 2, Theme.Accent);
            TextRenderer.DrawText(g, Text, Font, r, Theme.OnAccent, Theme.CenterFlags);
        }
        else
        {
            Theme.FillRound(g, r, Height / 2, _hot ? Theme.Hover : Theme.Card);
            Theme.DrawRound(g, r, Height / 2, _hot ? Theme.Accent : Theme.Border, 1f);
            TextRenderer.DrawText(g, Text, Font, r, Theme.Text, Theme.CenterFlags);
        }
    }
}

// Checkbox drawn as a rounded box plus a hand-drawn tick.
internal class PillCheck : CheckBox, IThemed
{
    private bool _hot;
    private const int Box = 17;
    private const int Gap = 8;

    internal PillCheck(string text)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Text = text;
        Font = Theme.FontUI;
        AutoSize = false;
        Height = 26;
        Width = Box + Gap + TextRenderer.MeasureText(text, Font).Width + 4;
        Cursor = Cursors.Hand;
        CheckedChanged += delegate { Invalidate(); };
    }

    public void ApplyPalette()
    {
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Card);

        int top = (Height - Box) / 2;
        Rectangle box = new Rectangle(0, top, Box, Box);

        if (Checked)
        {
            Theme.FillRound(g, box, 5, Theme.Accent);
            using (Pen pen = new Pen(Theme.OnAccent, 2f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLines(pen, new Point[]
                {
                    new Point(box.X + 4,  box.Y + 9),
                    new Point(box.X + 7,  box.Y + 12),
                    new Point(box.X + 13, box.Y + 5)
                });
            }
        }
        else
        {
            Theme.FillRound(g, box, 5, Theme.Card);
            Theme.DrawRound(g, box, 5, _hot ? Theme.Accent : Theme.CheckBorder, 1.2f);
        }

        Rectangle textRect = new Rectangle(Box + Gap, 0, Width - Box - Gap, Height);
        TextRenderer.DrawText(g, Text, Font, textRect, Theme.Text, Theme.LeftFlags);
    }
}

// Segmented disk-usage bar; colour warns as the volume fills up.
internal class UsageBar : Control, IThemed
{
    private double _used;
    private double _total;

    internal UsageBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 8;
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
    }

    public void ApplyPalette()
    {
        Invalidate();
    }

    internal void SetValue(long used, long total)
    {
        _used = used;
        _total = total;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Card);

        int radius = Math.Max(1, Height / 2);
        Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.FillRound(g, track, radius, Theme.Track);

        double pct = (_total > 0) ? (_used / _total) : 0;
        if (pct < 0) pct = 0;
        if (pct > 1) pct = 1;

        Color fill = Theme.Accent;
        if (pct >= 0.92) fill = Theme.Danger;
        else if (pct >= 0.80) fill = Theme.UsageWarn;

        int w = (int)Math.Round((Width - 1) * pct);
        if (w > 2)
        {
            Rectangle bar = new Rectangle(0, 0, w, Height - 1);
            Theme.FillRound(g, bar, radius, fill);
        }
    }
}

// Indeterminate activity bar - a sliding accent segment, no themed ProgressBar.
internal class ActivityBar : Control, IThemed
{
    private readonly Timer _timer;
    private int _pos;

    internal ActivityBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 6;
        Visible = false;
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
        _timer = new Timer();
        _timer.Interval = 28;
        _timer.Tick += delegate
        {
            int span = Math.Max(40, Width);
            _pos = (_pos + 7) % span;
            Invalidate();
        };
    }

    public void ApplyPalette()
    {
        Invalidate();
    }

    internal void Start()
    {
        _pos = 0;
        Visible = true;
        _timer.Start();
    }

    internal void Stop()
    {
        _timer.Stop();
        Visible = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Card);

        int radius = Math.Max(1, Height / 2);
        Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.FillRound(g, track, radius, Theme.Track);

        int seg = Math.Max(30, Width / 3);
        int x = _pos - seg;
        if (x + seg > Width - 1) x = Width - 1 - seg;
        if (x < 0) x = 0;

        Rectangle bar = new Rectangle(x, 0, Math.Min(seg, Width - 1 - x), Height - 1);
        if (bar.Width > 2) Theme.FillRound(g, bar, radius, Theme.Accent);
    }
}

// Owner-drawn action list: number chip, title, one-line role, accent selection.
internal class ActionListBox : ListBox, IThemed
{
    internal string[] Sub = new string[0];
    private int _hot = -1;

    // WHY THE HOVER REPAINT IS NARROWED *AND* ERASE-FREE
    // A ListBox is a native comctl32 control that paints its own items, so the
    // double-buffering styles set below never cover the rows (they only apply to
    // controls WinForms paints itself). A plain Invalidate() therefore makes the
    // control erase its whole client area first, which flashed the entire list white
    // every time the pointer crossed a row. Two changes fix that:
    //   * only the one or two rows whose hover state actually changed are repainted;
    //   * that repaint is requested with InvalidateRect(..., bErase: FALSE), so the
    //     native control does not erase the row first (OnDrawItem fills the row's
    //     full bounds itself, so there is no stale pixel left behind).
    // The remaining "erase, then draw" flicker inside a single row is handled at the
    // window level: MainForm sets WS_EX_COMPOSITED, so the OS composes the window and
    // its children and a half-painted row is never shown. Painting a row into an
    // offscreen bitmap and blitting it instead was tried and rejected: GDI text on a
    // GDI+ bitmap does not go through font smoothing, so the hovered row ended up with
    // jagged text that did not match the rows painted by the platform.
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

    internal ActionListBox()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 50;
        BorderStyle = BorderStyle.None;
        Font = Theme.FontUI;
        IntegralHeight = false;
        DoubleBuffered = true;
        ApplyPalette();
    }

    // BackColor/ForeColor are cached on the control, so they have to be re-pushed on a
    // theme switch; the rows themselves read Theme.* at paint time.
    public void ApplyPalette()
    {
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = IndexFromPoint(e.Location);
        if (i != _hot)
        {
            int previous = _hot;
            _hot = i;
            RepaintRow(previous);   // drop the old highlight
            RepaintRow(i);          // and add it to the new row
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

    // Repaints one row, without letting the native list box erase it first.
    private void RepaintRow(int index)
    {
        if (index < 0 || index >= Items.Count) return;

        Rectangle row = GetItemRectangle(index);
        // No handle yet, or the row is scrolled out of view: fall back to a normal
        // repaint so a hover change is never silently dropped.
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
        InvalidateRect(Handle, ref rect, false);   // bErase = FALSE
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (SolidBrush clear = new SolidBrush(BackColor))
            g.FillRectangle(clear, e.Bounds);

        DrawRow(g, e.Index, e.Bounds,
            (e.State & DrawItemState.Selected) == DrawItemState.Selected);
    }

    // Paints one row in control coordinates (the caller has already laid down the
    // row's background).
    private void DrawRow(Graphics g, int index, Rectangle bounds, bool selected)
    {
        Rectangle row = new Rectangle(bounds.X, bounds.Top + 3, Width - 1, bounds.Height - 6);

        if (selected)
        {
            Theme.FillRound(g, row, 10, Theme.AccentSoft);
            Theme.FillRound(g, new Rectangle(row.X + 1, row.Y + 8, 3, row.Height - 16), 2, Theme.Accent);
        }
        else if (index == _hot)
        {
            Theme.FillRound(g, row, 10, Theme.Hover);
        }

        Rectangle chip = new Rectangle(row.X + 12, row.Y + (row.Height - 26) / 2, 26, 26);
        Theme.FillRound(g, chip, 9, selected ? Theme.Accent : Theme.ChipBg);
        TextRenderer.DrawText(g, (index + 1).ToString(), Theme.FontSmallB, chip,
            selected ? Theme.OnAccent : Theme.TextMuted, Theme.CenterFlags);

        int textX = chip.Right + 12;
        int textW = Width - textX - 12;
        if (textW < 20) textW = 20;

        TextRenderer.DrawText(g, Convert.ToString(Items[index]), selected ? Theme.FontUIB : Theme.FontUI,
            new Rectangle(textX, row.Y + 6, textW, 20),
            selected ? Theme.AccentDark : Theme.Text, Theme.LeftFlags);

        if (index < Sub.Length)
        {
            TextRenderer.DrawText(g, Sub[index], Theme.FontSmall,
                new Rectangle(textX, row.Y + 26, textW, 17),
                selected ? Theme.Mix(Theme.AccentDark, Theme.TextMuted, 0.55f) : Theme.TextMuted,
                Theme.LeftFlags);
        }
    }
}

// ============================================================== appearance menu

// Draws the appearance drop-down with the same palette as the window. A custom
// ToolStripRenderer rather than the system renderer: the professional renderer paints
// a light gradient and its own check glyph, which on a dark theme looks like a piece
// of another application. Item roles ride in ToolStripItem.Tag:
//   "head"          -> a section caption (small, faint, not clickable)
//   "mode:light"    -> a mode entry
//   "accent:blue"   -> an accent entry, drawn with a colour swatch
internal sealed class FlatMenuRenderer : ToolStripRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(Theme.Card);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        Rectangle r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using (Pen p = new Pen(Theme.Border))
            e.Graphics.DrawRectangle(p, r);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(Theme.Card);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle full = new Rectangle(0, 0, e.Item.Width, e.Item.Height);

        if (e.Item.Selected && e.Item.Enabled)
        {
            Theme.FillRound(g, new Rectangle(3, 1, full.Width - 6, full.Height - 2), 6, Theme.AccentSoft);
            Theme.FillRound(g, new Rectangle(5, 5, 3, Math.Max(6, full.Height - 10)), 2, Theme.Accent);
        }
        else
        {
            using (SolidBrush b = new SolidBrush(Theme.Card))
                g.FillRectangle(b, full);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        string tag = Convert.ToString(e.Item.Tag);
        bool head = (tag == "head");
        Rectangle tr = e.TextRectangle;

        // The accent entries carry a swatch, so their label is shifted right to make
        // room; the swatch itself states the colour better than the name could.
        if (tag.StartsWith("accent:"))
        {
            int size = 12;
            Theme.FillRound(e.Graphics,
                new Rectangle(tr.Left, tr.Top + Math.Max(0, (tr.Height - size) / 2), size, size),
                4, Theme.AccentColorOf(tag.Substring(7)));
            tr = new Rectangle(tr.Left + size + 8, tr.Top, Math.Max(10, tr.Width - size - 8), tr.Height);
        }

        Color fg = Theme.Text;
        if (!e.Item.Enabled || head) fg = Theme.TextFaint;

        TextRenderer.DrawText(e.Graphics, e.Text, head ? Theme.FontSmallB : Theme.FontUI, tr, fg,
            e.TextFormat | TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        Rectangle b = e.ImageRectangle;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (Pen pen = new Pen(Theme.Accent, 2f))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            e.Graphics.DrawLines(pen, new Point[]
            {
                new Point(b.Left + 3, b.Top + b.Height / 2),
                new Point(b.Left + b.Width / 2, b.Bottom - 4),
                new Point(b.Right - 3, b.Top + 4)
            });
        }
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        Rectangle r;
        if (e.Vertical)
            r = new Rectangle(e.Item.Width / 2, 2, 1, Math.Max(1, e.Item.Height - 4));
        else
            r = new Rectangle(10, e.Item.Height / 2, Math.Max(1, e.Item.Width - 20), 1);

        using (SolidBrush b = new SolidBrush(Theme.Border))
            e.Graphics.FillRectangle(b, r);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        Rectangle a = e.ArrowRectangle;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush b = new SolidBrush(Theme.TextFaint))
            e.Graphics.FillPolygon(b, new Point[]
            {
                new Point(a.Left, a.Top + 2),
                new Point(a.Left + 4, a.Top + a.Height / 2),
                new Point(a.Left, a.Bottom - 2)
            });
    }
}

// ==================================================================== window

internal sealed class MainForm : Form
{
    // ------------------------------------------------------------ page model

    // Index order here IS the order in the rail.
    private ToolPage[] _pages;
    private int _current;

    private ToolPage CurrentPage
    {
        get { return (_pages != null && _current >= 0 && _current < _pages.Length) ? _pages[_current] : null; }
    }

    // Index order here IS the order in the rail. Each page gets its OWN animation
    // (GuiAnims.cs), so a function is recognisable at a glance while it works.
    private ToolPage[] BuildPages()
    {
        return new ToolPage[]
        {
            PageAnalyze(),
            PageCacheClean(),
            PageDiscover(),
            PageCatalog(),
            PageGpu(),
            PageRoots(),
            PageDev(),
            new LargeDirsPage(new LargeDirsAnim()),
            new HiddenCachePage(new CacheTilesAnim()),
            PageDiagnose()
        };
    }

    private ToolPage PageAnalyze()
    {
        return new ScriptPage("空间分析", "只读 · 磁盘与清单概况",
            "只读：磁盘概况 + 固定清理清单 + 清单级缓存汇总。运行结果写入报告目录。",
            "01-Analyze.ps1", true,
            delegate(bool execute) { return "-Tag before"; },
            null, null, new AnalyzeAnim());
    }

    private ToolPage PageCacheClean()
    {
        PillRadio radDepthStd = null, radDepthDeep = null;
        PillRadio radSafe = null, radStd = null, radAggr = null;
        PillCheck chkExplorer = null, chkRecycle = null;

        return new ScriptPage("系统缓存清理", "预演后确认 · 点到点删除",
            "点到点删除系统缓存。先预演并标出被占用项，确认后才真正删除；逐目标实测净空间，一旦空间反降立即中止。",
            "02-Clean-System.ps1", false,
            delegate(bool execute)
            {
                StringBuilder a = new StringBuilder();
                if (radDepthDeep != null && radDepthDeep.Checked) a.Append(" -TempAgeDays 0");
                a.Append(" -Profile ").Append(ProfileName(radSafe, radStd, radAggr));
                if (chkExplorer != null && chkExplorer.Checked) a.Append(" -RestartExplorer");
                if (chkRecycle != null && chkRecycle.Checked) a.Append(" -IncludeRecycleBin");
                if (execute) a.Append(" -Execute");
                return a.ToString();
            },
            null,
            delegate(ScriptPage p)
            {
                radDepthStd = new PillRadio("标准");
                radDepthStd.Checked = true;
                radDepthStd.Margin = new Padding(0, 0, 8, 0);
                radDepthDeep = new PillRadio("深度");
                radDepthDeep.Margin = new Padding(0, 0, 8, 0);
                _tips.SetToolTip(radDepthStd, "只删除最后修改早于 1 天的临时文件（安全，推荐）");
                _tips.SetToolTip(radDepthDeep, "连同今天产生的临时文件一并删除；可能影响正在运行的程序，被占用的文件仍会自动跳过");
                p.AddOptionRow(ToolPage.MakeRow(ToolPage.MakeRowLabel("清理深度"), radDepthStd, radDepthDeep));

                radSafe = new PillRadio("保守");
                radSafe.Margin = new Padding(0, 0, 8, 0);
                radStd = new PillRadio("标准");
                radStd.Checked = true;
                radStd.Margin = new Padding(0, 0, 8, 0);
                radAggr = new PillRadio("完整");
                radAggr.Margin = new Padding(0, 0, 8, 0);
                _tips.SetToolTip(radSafe, "只有绝对无争议的：临时文件、日志、转储、WER、更新缓存、缩略图、INetCache、字体缓存");
                _tips.SetToolTip(radStd, "在保守的基础上再加浏览器 Cache / Code Cache / GPUCache（不含 Cookie、登录态、历史）");
                _tips.SetToolTip(radAggr, "再加应用缓存发现结果、搜索索引等重建代价高的项");
                p.AddOptionRow(ToolPage.MakeRow(ToolPage.MakeRowLabel("清理档位"), radSafe, radStd, radAggr));

                chkExplorer = new PillCheck("重启 explorer 解锁缩略图");
                chkExplorer.Margin = new Padding(0, 0, 18, 0);
                chkRecycle = new PillCheck("包含回收站");
                chkRecycle.Margin = new Padding(0, 0, 18, 0);
                _tips.SetToolTip(chkExplorer, "explorer 持有缩略图缓存句柄，重启后才能释放这部分空间（explorer 会闪一下）");
                _tips.SetToolTip(chkRecycle, "把回收站也算进清理范围；清空后无法还原");
                p.AddOptionRow(ToolPage.MakeRow(null, chkExplorer, chkRecycle));
            }, new CacheCleanAnim());
    }

    private ToolPage PageDiscover()
    {
        return new ScriptPage("无用缓存发现", "只读 · 扫描未列清单缓存",
            "只读：扫描未列入清单的缓存目录，按体积排序，并列出本工具不碰的大块残留。",
            "02-Clean-System.ps1", true,
            delegate(bool execute) { return "-Discover -DiscoverMinMB 20"; },
            null, null, new DiscoverAnim());
    }

    private ToolPage PageCatalog()
    {
        PillRadio radSafe = null, radStd = null, radAggr = null;
        return new ScriptPage("清理清单一览", "只读 · 打印全部目标",
            "只读：打印全部清理目标（含风险级别、文件名模式、时限、是否需要管理员）。",
            "02-Clean-System.ps1", true,
            delegate(bool execute) { return "-ListCatalog -Profile " + ProfileName(radSafe, radStd, radAggr); },
            null,
            delegate(ScriptPage p)
            {
                radSafe = new PillRadio("保守");
                radSafe.Margin = new Padding(0, 0, 8, 0);
                radStd = new PillRadio("标准");
                radStd.Checked = true;
                radStd.Margin = new Padding(0, 0, 8, 0);
                radAggr = new PillRadio("完整");
                radAggr.Margin = new Padding(0, 0, 8, 0);
                p.AddOptionRow(ToolPage.MakeRow(ToolPage.MakeRowLabel("清理档位"), radSafe, radStd, radAggr));
            }, new CatalogAnim());
    }

    private ToolPage PageGpu()
    {
        PillCheck chkD3d = null;
        return new ScriptPage("显卡着色器缓存", "预演后确认 · N 卡 / A 卡",
            "自动识别 N 卡 / A 卡。删除后游戏首次启动会重新编译着色器，可能短暂卡顿。",
            "07-Clean-GpuShaderCache.ps1", false,
            delegate(bool execute)
            {
                StringBuilder a = new StringBuilder();
                a.Append("-ShowAll");
                if (chkD3d != null && chkD3d.Checked) a.Append(" -IncludeD3DSCache");
                if (execute) a.Append(" -Execute");
                return a.ToString();
            },
            null,
            delegate(ScriptPage p)
            {
                chkD3d = new PillCheck("含 D3D 着色器缓存");
                chkD3d.Margin = new Padding(0, 0, 18, 0);
                _tips.SetToolTip(chkD3d, "同时清理 DirectX 着色器缓存（-IncludeD3DSCache）");
                p.AddOptionRow(ToolPage.MakeRow(null, chkD3d));
            }, new GpuAnim());
    }

    private ToolPage PageRoots()
    {
        PillCheck chkWinRE = null, chkHls = null, chkKuGou = null;
        return new ScriptPage("C盘根目录残留", "预演后确认 · HLS / P2P / 回滚数据",
            "HLS 视频分片缓存 / 酷狗 P2P 缓存 / $WinREAgent 更新回滚数据。删除回滚数据后无法回退该次更新。",
            "06-Clean-RootLeftovers.ps1", false,
            delegate(bool execute)
            {
                StringBuilder a = new StringBuilder();
                if (chkWinRE != null && chkWinRE.Checked) a.Append(" -WinREAgent");
                if (chkHls != null && chkHls.Checked) a.Append(" -HlsCache");
                if (chkKuGou != null && chkKuGou.Checked) a.Append(" -KuGouCache");
                if (execute) a.Append(" -Execute");
                return a.ToString();
            },
            delegate()
            {
                if ((chkWinRE == null || !chkWinRE.Checked) &&
                    (chkHls == null || !chkHls.Checked) &&
                    (chkKuGou == null || !chkKuGou.Checked))
                {
                    return "请至少勾选一项根目录残留（HLS 视频分片缓存 / 酷狗 P2P 缓存 / $WinREAgent），"
                         + "否则 06 脚本不会执行任何清理。";
                }
                return null;
            },
            delegate(ScriptPage p)
            {
                chkWinRE = new PillCheck("$WinREAgent 回滚数据");
                chkWinRE.Margin = new Padding(0, 0, 18, 0);
                chkHls = new PillCheck("HLS 视频分片缓存");
                chkHls.Checked = true;
                chkHls.Margin = new Padding(0, 0, 18, 0);
                chkKuGou = new PillCheck("酷狗 P2P 缓存");
                chkKuGou.Checked = true;
                chkKuGou.Margin = new Padding(0, 0, 18, 0);
                _tips.SetToolTip(chkWinRE, "上次系统更新的回滚数据；删除后无法回退该次更新（默认不勾）");
                _tips.SetToolTip(chkHls, "C 盘根目录下 32 位十六进制名目录里的视频分片缓存，通常可安全删除");
                _tips.SetToolTip(chkKuGou, "酷狗 P2P 缓存（C:\\KuGou\\Temp\\tp2p）");
                p.AddOptionRow(ToolPage.MakeRow(null, chkWinRE, chkHls, chkKuGou));
            }, new RootsAnim());
    }

    private ToolPage PageDev()
    {
        PillCheck chkAggr = null;
        return new ScriptPage("开发环境缓存", "预演后确认 · npm / pip / go",
            "npm / pnpm / pip / yarn / go 等包管理器缓存，删除后需要重新下载依赖。",
            "03-Clean-Dev.ps1", false,
            delegate(bool execute)
            {
                StringBuilder a = new StringBuilder();
                if (chkAggr != null && chkAggr.Checked) a.Append(" -Aggressive");
                if (execute) a.Append(" -Execute");
                return a.ToString();
            },
            null,
            delegate(ScriptPage p)
            {
                chkAggr = new PillCheck("激进模式（含全局包缓存）");
                chkAggr.Margin = new Padding(0, 0, 18, 0);
                _tips.SetToolTip(chkAggr, "连全局 npm / pip / yarn 缓存一并清理，下次构建需要重新下载依赖（-Aggressive）");
                p.AddOptionRow(ToolPage.MakeRow(null, chkAggr));
            }, new DevAnim());
    }

    private ToolPage PageDiagnose()
    {
        PillCheck chkDeep = null;
        return new ScriptPage("空间反升诊断", "只读 · 量化反升原因",
            "只读：量化「为什么清完反而更满」—— 卷影副本写时复制、句柄未释放、缓存再生等。",
            "08-Diagnose-SpaceGrowth.ps1", true,
            delegate(bool execute)
            {
                return (chkDeep != null && chkDeep.Checked) ? "-Deep" : "";
            },
            null,
            delegate(ScriptPage p)
            {
                chkDeep = new PillCheck("深度采样");
                chkDeep.Margin = new Padding(0, 0, 18, 0);
                _tips.SetToolTip(chkDeep, "更慢、更细的采样（-Deep），用于定位顽固的空间反升");
                p.AddOptionRow(ToolPage.MakeRow(null, chkDeep));
            }, new DiagnoseAnim());
    }

    private static string ProfileName(PillRadio safe, PillRadio std, PillRadio aggr)
    {
        if (aggr != null && aggr.Checked) return "aggressive";
        if (safe != null && safe.Checked) return "safe";
        return "standard";
    }

    // ------------------------------------------------------------- state

    private string _runtimeDir;
    private string _powershell;

    internal string RuntimeDir { get { return _runtimeDir; } }
    internal ToolTip Tips { get { return _tips; } }

    private Process _proc;              // non-null while a child run is in flight
    private PendingRun _running;
    private PendingRun _pending;        // destructive action awaiting confirmation
    private bool _cancelRequested;
    private DateTime _runStarted;
    private int _runSeconds;
    private readonly Timer _elapsed = new Timer();

    // Tally of the last run's output lines: the pages show these numbers instead of the
    // raw output (the raw output is still in the log window).
    private int _countOk, _countWarn, _countErr;
    private string _lastOk = "", _lastErr = "";

    // Appearance: the drop-down is built once and refilled on every open; the watcher
    // polls the Windows app-theme flag, and only while the mode is "follow system".
    private ContextMenuStrip _themeMenu;
    private readonly Timer _themeWatch = new Timer();
    private bool _lastSystemDark;
    private readonly Timer _logBadge = new Timer();
    private int _logBadgeUnread = -1;

    // ------------------------------------------------------------- controls

    private Label _lblTitle, _lblTagline;
    private FlatButton _btnRefresh, _btnOpenReports, _btnElevate, _btnTheme, _btnLog;

    private Card _hero;
    private Label _lblDiskName, _lblFreeValue, _lblFreeUnit, _lblDiskDetail;
    private UsageBar _usage;
    private Pill _pillOs, _pillAdmin;
    private FlowLayoutPanel _heroPills;

    private Panel _rail;
    private FlowLayoutPanel _railItems;
    private FlatButton _btnRailToggle;
    private RailButton[] _railButtons;
    private Panel _pageHost;

    private Label _lblStatus;
    private ActivityBar _activity;
    private FlatButton _btnRun, _btnCancel;

    private Card _confirmCard;
    private Label _lblConfirm;
    private FlatButton _btnConfirmYes, _btnConfirmNo;
    private Panel _confirmStrip;

    private ToolTip _tips;
    private LogWindow _logWindow;

    // ------------------------------------------------------------- ctor

    internal MainForm()
    {
        // The Chinese descriptor lives in the title bar rather than the header:
        // the title bar has room, the header's right-docked action group does not.
        Text = Program.AppName + " · Windows C 盘清理工具  v" + Program.AppVersion;
        Font = Theme.FontUI;
        BackColor = Theme.Bg;
        StartPosition = FormStartPosition.CenterScreen;

        // Size to the desktop, not to a guess: on a 1366x768 laptop the roomy default
        // would otherwise hang off the bottom of the screen.
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        int cw = Math.Min(1240, Math.Max(900, wa.Width - 60));
        int ch = Math.Min(920, Math.Max(580, wa.Height - 60));
        MinimumSize = new Size(720, 500);            // temporary floor while sizing
        ClientSize = new Size(cw, ch);
        MinimumSize = new Size(Math.Min(1060, cw), Math.Min(660, ch));

        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Font;
        _tips = new ToolTip();
        _tips.AutoPopDelay = 15000;
        _tips.InitialDelay = 350;
        _tips.ReshowDelay = 100;

        _elapsed.Interval = 1000;
        _elapsed.Tick += delegate
        {
            _runSeconds++;
            UpdateRunStatus();
        };

        // Windows' app theme can change while the tool is open (the Settings toggle, or a
        // scheduled light/dark switch). Polling the flag is far cheaper and far more
        // reliable here than SystemEvents, which needs its own message pump.
        _themeWatch.Interval = 1500;
        _themeWatch.Tick += new EventHandler(OnThemeWatchTick);
        _lastSystemDark = Theme.SystemPrefersDark();

        // The [日志] button shows a dot while lines arrive with the log window closed.
        _logBadge.Interval = 700;
        _logBadge.Tick += delegate { UpdateLogButton(); };

        BuildLayout();

        // Paint everything from the active palette before the window is shown. The same
        // pass runs on every theme change, so "first paint" is not a special case.
        ApplyPaletteAll();
        Log.Line += OnAnyLogLine;

        Load += new EventHandler(OnLoad);
        Shown += new EventHandler(OnShown);
        FormClosing += new FormClosingEventHandler(OnFormClosing);
    }

    // ------------------------------------------------------------- layout

    // WS_EX_COMPOSITED lets the window manager compose this window and all of its
    // descendants, which is what makes the list rows flicker-free: a native ListBox
    // erases a row before redrawing its highlight and text, and without composition that
    // half-painted row is briefly visible.
    // It must be set on the FORM, not on the list: CreateWindowEx rejects
    // WS_EX_COMPOSITED for the list box window class and the control's handle then fails
    // to be created at all (Win32Exception "创建窗口句柄时出错").
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x02000000;   // WS_EX_COMPOSITED
            return cp;
        }
    }

    private void BuildLayout()
    {
        // Add order matters for docking: a Fill control must go in first, and among
        // same-edge docks the LAST added ends up on the outer edge.
        Panel content = new Panel();
        content.Dock = DockStyle.Fill;
        content.Padding = new Padding(18, 14, 18, 12);
        Role(content, "bg");

        Panel body = new Panel();
        body.Dock = DockStyle.Fill;
        Role(body, "bg");

        BuildRail();
        _pageHost = new Panel();
        _pageHost.Dock = DockStyle.Fill;
        _pageHost.Padding = new Padding(12, 0, 0, 0);
        Role(_pageHost, "bg");

        body.Controls.Add(_pageHost);
        body.Controls.Add(_rail);

        Panel gap = new Panel();
        gap.Dock = DockStyle.Top;
        gap.Height = 12;
        Role(gap, "bg");

        content.Controls.Add(body);
        content.Controls.Add(gap);
        BuildHero(content);

        Controls.Add(content);
        Controls.Add(BuildHeader());
        Controls.Add(BuildConfirmStrip());
        Controls.Add(BuildActionBar());

        BuildPages();
        CreatePages();
        ShowPage(1);            // 系统缓存清理, the common case
    }

    // --- header -------------------------------------------------------------

    private Band BuildHeader()
    {
        Band header = new Band();
        header.Dock = DockStyle.Top;
        header.Height = 86;
        header.BottomLine = true;

        Panel logo = new Panel();
        logo.Location = new Point(18, 23);
        logo.Size = new Size(40, 40);
        Role(logo, "card");
        logo.Paint += new PaintEventHandler(DrawLogo);

        _lblTitle = new Label();
        _lblTitle.Text = Program.AppName;
        _lblTitle.Font = Theme.FontTitle;
        _lblTitle.AutoSize = true;
        _lblTitle.Location = new Point(72, 20);
        Role(_lblTitle, "card|text");

        _lblTagline = new Label();
        _lblTagline.Text = Program.AppTagline;
        _lblTagline.Font = Theme.FontSmall;
        _lblTagline.AutoSize = true;
        _lblTagline.Location = new Point(74, 50);
        // The tagline doubles as the update affordance. A sixth header button
        // would not fit: the action group is right-docked at a fixed width and
        // the window can shrink to 1060 px.
        _lblTagline.Cursor = Cursors.Hand;
        _lblTagline.Click += new EventHandler(OnUpdateClick);
        Role(_lblTagline, "card|muted");

        FlowLayoutPanel actions = new FlowLayoutPanel();
        actions.Dock = DockStyle.Right;
        actions.Width = 570;   // five buttons: log, appearance, reports, refresh, elevate
        actions.FlowDirection = FlowDirection.RightToLeft;
        actions.WrapContents = false;
        actions.Padding = new Padding(0, 25, 18, 0);
        Role(actions, "card");

        _btnElevate = MakeGhost("以管理员身份重启", 150, 34);
        _btnElevate.Margin = new Padding(8, 0, 0, 0);
        _btnElevate.Click += new EventHandler(OnElevateClick);

        _btnRefresh = MakeGhost("刷新", 76, 34);
        _btnRefresh.Margin = new Padding(8, 0, 0, 0);
        _btnRefresh.Click += new EventHandler(OnRefreshClick);

        _btnOpenReports = MakeGhost("打开报告目录", 128, 34);
        _btnOpenReports.Margin = new Padding(8, 0, 0, 0);
        _btnOpenReports.Click += new EventHandler(OnOpenReportsClick);

        _btnTheme = MakeGhost("外观 ▾", 86, 34);
        _btnTheme.Margin = new Padding(8, 0, 0, 0);
        _btnTheme.Click += new EventHandler(OnThemeClick);

        _btnLog = MakeGhost("输出日志", 100, 34);
        _btnLog.Margin = new Padding(0);
        _btnLog.Click += new EventHandler(OnLogClick);

        actions.Controls.Add(_btnElevate);
        actions.Controls.Add(_btnRefresh);
        actions.Controls.Add(_btnOpenReports);
        actions.Controls.Add(_btnTheme);
        actions.Controls.Add(_btnLog);

        header.Controls.Add(logo);
        header.Controls.Add(_lblTitle);
        header.Controls.Add(_lblTagline);
        header.Controls.Add(actions);
        return header;
    }

    private void DrawLogo(object sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(0, 0, 39, 39);
        Theme.FillRound(g, r, 11, Theme.Accent);
        TextRenderer.DrawText(g, "C:", Theme.FontLogo, r, Theme.OnAccent, Theme.CenterFlags);
    }

    // --- hero (disk + privilege) -------------------------------------------

    private void BuildHero(Panel content)
    {
        _hero = new Card();
        _hero.Dock = DockStyle.Top;
        _hero.Height = 118;
        _hero.Padding = new Padding(0);

        _lblDiskName = new Label();
        _lblDiskName.Text = "C: 系统盘";
        _lblDiskName.Font = Theme.FontSection;
        _lblDiskName.AutoSize = true;
        _lblDiskName.Location = new Point(20, 14);
        Role(_lblDiskName, "card|text");

        _lblFreeValue = new Label();
        _lblFreeValue.Text = "--";
        _lblFreeValue.Font = Theme.FontBig;
        _lblFreeValue.AutoSize = true;
        _lblFreeValue.Location = new Point(18, 34);
        Role(_lblFreeValue, "card|text");

        _lblFreeUnit = new Label();
        _lblFreeUnit.Text = "可用";
        _lblFreeUnit.Font = Theme.FontUI;
        _lblFreeUnit.AutoSize = true;
        Role(_lblFreeUnit, "card|muted");

        _lblDiskDetail = new Label();
        _lblDiskDetail.Text = "";
        _lblDiskDetail.Font = Theme.FontSmall;
        _lblDiskDetail.AutoSize = true;
        Role(_lblDiskDetail, "card|muted");

        _usage = new UsageBar();
        _usage.Location = new Point(20, 92);
        _usage.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _usage.Width = _hero.Width - 40;

        _heroPills = new FlowLayoutPanel();
        _heroPills.AutoSize = true;
        _heroPills.FlowDirection = FlowDirection.LeftToRight;
        _heroPills.WrapContents = false;
        Role(_heroPills, "card");

        _pillOs = new Pill("Windows", PillTone.Neutral, Theme.FontSmallB);
        _pillOs.Margin = new Padding(0, 0, 8, 0);
        _pillAdmin = new Pill("权限", PillTone.Success, Theme.FontSmallB);
        _heroPills.Controls.Add(_pillOs);
        _heroPills.Controls.Add(_pillAdmin);

        _hero.Controls.Add(_lblDiskName);
        _hero.Controls.Add(_lblFreeValue);
        _hero.Controls.Add(_lblFreeUnit);
        _hero.Controls.Add(_lblDiskDetail);
        _hero.Controls.Add(_usage);
        _hero.Controls.Add(_heroPills);

        _hero.Resize += delegate { LayoutHero(); };

        _lblFreeValue.SizeChanged += delegate
        {
            _lblFreeUnit.Location = new Point(_lblFreeValue.Right + 8, _lblFreeValue.Top + 16);
        };
        _lblDiskDetail.Location = new Point(20, 72);

        content.Controls.Add(_hero);
    }

    private void LayoutHero()
    {
        _usage.Width = Math.Max(120, _hero.Width - 40);
        _heroPills.Location = new Point(Math.Max(20, _hero.Width - _heroPills.Width - 20), 16);
    }

    // --- function rail ------------------------------------------------------

    // The function list is tucked away: expanded it shows titles, collapsed it is a strip
    // of numbers, and either way the page keeps the rest of the window.
    private void BuildRail()
    {
        _rail = new Panel();
        _rail.Dock = DockStyle.Left;
        _rail.Width = Prefs.RailExpanded ? 292 : 64;
        _rail.Padding = new Padding(6, 8, 6, 8);
        Role(_rail, "card");

        Panel head = new Panel();
        head.Dock = DockStyle.Top;
        head.Height = 42;
        Role(head, "card");

        _btnRailToggle = MakeGhost("☰  折叠功能栏", 150, 32);
        _btnRailToggle.Dock = DockStyle.Fill;
        _btnRailToggle.Click += delegate { ToggleRail(); };
        head.Controls.Add(_btnRailToggle);

        _railItems = new FlowLayoutPanel();
        _railItems.Dock = DockStyle.Fill;
        _railItems.FlowDirection = FlowDirection.TopDown;
        _railItems.WrapContents = false;
        _railItems.AutoScroll = true;
        Role(_railItems, "card");
        // The rail is sized by docking and re-measured while the form lays itself out, so
        // the item widths have to follow it instead of being computed once.
        _railItems.SizeChanged += delegate { ApplyRailState(); };

        _rail.Controls.Add(_railItems);
        _rail.Controls.Add(head);
    }

    private void BuildRailItems()
    {
        _railButtons = new RailButton[_pages.Length];
        for (int i = 0; i < _pages.Length; i++)
        {
            RailButton b = new RailButton();
            b.Number = i + 1;
            b.TitleText = _pages[i].Title;
            b.SubText = _pages[i].Sub;
            b.Width = _railItems.ClientSize.Width - 6;
            b.Margin = new Padding(0, 0, 0, 4);
            // The rail item is entirely custom-painted, so it has to carry its name
            // explicitly: Text is what accessibility (and UI automation) reads off the
            // window, and OnPaint never draws it.
            b.Text = (i + 1) + ". " + _pages[i].Title;
            b.AccessibleName = b.Text;
            b.AccessibleDescription = _pages[i].Sub;
            int index = i;
            b.Click += delegate { ShowPage(index); };
            _tips.SetToolTip(b, (i + 1) + ". " + _pages[i].Title + " — " + _pages[i].Sub);
            _railItems.Controls.Add(b);
            _railButtons[i] = b;
        }
        ApplyRailState();
    }

    private void ToggleRail()
    {
        Prefs.RailExpanded = !Prefs.RailExpanded;
        ApplyRailState();
    }

    private void ApplyRailState()
    {
        bool expanded = Prefs.RailExpanded;
        _rail.Width = expanded ? 292 : 64;
        _btnRailToggle.Text = expanded ? "☰  折叠功能栏" : "☰";
        if (_railButtons != null)
        {
            for (int i = 0; i < _railButtons.Length; i++)
            {
                _railButtons[i].SetCollapsed(!expanded);
                _railButtons[i].Width = Math.Max(40, _railItems.ClientSize.Width - 6 - (expanded ? 0 : 0));
                _railButtons[i].Invalidate();
            }
        }
    }

    private void ShowPage(int index)
    {
        if (_pages == null || index < 0 || index >= _pages.Length) return;

        ToolPage previous = CurrentPage;
        _current = index;

        for (int i = 0; i < _pages.Length; i++)
        {
            bool visible = (i == index);
            _pages[i].View.Visible = visible;
            if (_railButtons != null) _railButtons[i].SetActive(visible);
        }

        if (previous != null && previous != _pages[index]) previous.OnLeave();
        _pages[index].OnEnter();
        if (_rail != null && _railItems != null) _railItems.ScrollControlIntoView(_railButtons[index]);

        _pages[index].View.BringToFront();
        UpdateRunButton();
        SetBusy(_proc != null || _pages[index].Busy, null);
    }

    private void CreatePages()
    {
        _pages = BuildPages();
        for (int i = 0; i < _pages.Length; i++)
        {
            _pages[i].Build(this);
            _pages[i].View.Visible = false;
            _pages[i].StateChanged += delegate { UpdateRunButton(); };
            _pageHost.Controls.Add(_pages[i].View);
        }
        BuildRailItems();
    }

    // --- bottom action bar --------------------------------------------------

    private Band BuildActionBar()
    {
        Band bar = new Band();
        bar.Dock = DockStyle.Bottom;
        bar.Height = 82;
        bar.TopLine = true;

        _lblStatus = new Label();
        _lblStatus.Text = "正在初始化...";
        _lblStatus.Font = Theme.FontUI;
        _lblStatus.AutoSize = false;
        _lblStatus.Location = new Point(20, 24);
        _lblStatus.Size = new Size(520, 22);
        _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        Role(_lblStatus, "card|muted");

        _activity = new ActivityBar();
        _activity.Location = new Point(20, 54);
        _activity.Width = 200;

        FlowLayoutPanel buttons = new FlowLayoutPanel();
        buttons.Dock = DockStyle.Right;
        buttons.Width = 400;
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.WrapContents = false;
        buttons.Padding = new Padding(0, 22, 18, 0);
        Role(buttons, "card");

        _btnRun = new FlatButton("开始预演（不删除文件）", Theme.FontUIB);
        _btnRun.Size = new Size(224, 40);
        _btnRun.Role = ButtonRole.Primary;
        _btnRun.Radius = 9;
        _btnRun.Margin = new Padding(0);
        _btnRun.Click += new EventHandler(OnRunClick);

        _btnCancel = new FlatButton("取消当前任务", Theme.FontUI);
        _btnCancel.Size = new Size(124, 40);
        _btnCancel.Role = ButtonRole.Ghost;
        _btnCancel.Radius = 9;
        _btnCancel.Enabled = false;
        _btnCancel.Margin = new Padding(0, 0, 12, 0);
        _btnCancel.Click += new EventHandler(OnCancelClick);

        buttons.Controls.Add(_btnRun);
        buttons.Controls.Add(_btnCancel);

        bar.Controls.Add(_lblStatus);
        bar.Controls.Add(_activity);
        bar.Controls.Add(buttons);
        return bar;
    }

    // --- confirmation strip -------------------------------------------------

    private Panel BuildConfirmStrip()
    {
        _confirmStrip = new Panel();
        _confirmStrip.Dock = DockStyle.Bottom;
        _confirmStrip.Height = 78;
        _confirmStrip.Padding = new Padding(18, 0, 18, 12);
        _confirmStrip.Visible = false;
        Role(_confirmStrip, "bg");

        _confirmCard = new Card();
        _confirmCard.Dock = DockStyle.Fill;
        _confirmCard.WarnStyle = true;
        _confirmCard.Padding = new Padding(18, 0, 14, 0);

        _lblConfirm = new Label();
        _lblConfirm.Text = "";
        _lblConfirm.Font = Theme.FontUIB;
        _lblConfirm.Dock = DockStyle.Fill;
        _lblConfirm.TextAlign = ContentAlignment.MiddleLeft;
        Role(_lblConfirm, "warn");

        FlowLayoutPanel buttons = new FlowLayoutPanel();
        buttons.Dock = DockStyle.Right;
        buttons.Width = 268;
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.WrapContents = false;
        buttons.Padding = new Padding(0, 9, 0, 0);
        Role(buttons, "warn-soft");

        _btnConfirmYes = new FlatButton("执行删除", Theme.FontUIB);
        _btnConfirmYes.Size = new Size(126, 38);
        _btnConfirmYes.Role = ButtonRole.Danger;
        _btnConfirmYes.Radius = 9;
        _btnConfirmYes.Margin = new Padding(0);
        _btnConfirmYes.Click += new EventHandler(OnConfirmYesClick);

        _btnConfirmNo = new FlatButton("取消", Theme.FontUI);
        _btnConfirmNo.Size = new Size(92, 38);
        _btnConfirmNo.Role = ButtonRole.GhostWarn;
        _btnConfirmNo.Radius = 9;
        _btnConfirmNo.Margin = new Padding(0, 0, 12, 0);
        _btnConfirmNo.Click += new EventHandler(OnConfirmNoClick);

        buttons.Controls.Add(_btnConfirmYes);
        buttons.Controls.Add(_btnConfirmNo);

        _confirmCard.Controls.Add(_lblConfirm);
        _confirmCard.Controls.Add(buttons);
        _confirmStrip.Controls.Add(_confirmCard);
        return _confirmStrip;
    }

    private FlatButton MakeGhost(string text, int w, int h)
    {
        FlatButton b = new FlatButton(text, Theme.FontUI);
        b.Size = new Size(w, h);
        b.Role = ButtonRole.Ghost;
        b.Radius = 8;
        return b;
    }

    // ------------------------------------------------------------- appearance

    // Pushes the active palette into the whole control tree. Runs once after
    // BuildLayout() and again on every theme change, so "the colours are correct" has
    // exactly one code path and no per-theme special cases.
    private void ApplyPaletteAll()
    {
        BackColor = Theme.Bg;
        ApplyPaletteTree(this);
        UpdateThemeButton();
        UpdateLogButton();
        UpdateUpdateLabel();
        ApplyTitleBarTheme();
        Theme.RaisePaletteChanged();
    }

    // ------------------------------------------------------------- update check

    // Non-null once a newer release is known to exist; drives the tagline CTA.
    private UpdateInfo _updateInfo;
    private bool _updateBusy;

    // Runs on a thread-pool thread. force == false accepts a fresh cache entry, so
    // a normal launch only touches the network at most once a day.
    private void StartUpdateCheck(bool force)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        UpdateUpdateLabel();

        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            UpdateInfo info = UpdateCheck.Check(force);
            try
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _updateBusy = false;
                        ApplyUpdateInfo(info, force);
                    });
                }
            }
            catch { /* the window went away while we were asking */ }
        });
    }

    // announce == true only for a check the user asked for: the automatic startup
    // check stays quiet unless there is something worth clicking.
    private void ApplyUpdateInfo(UpdateInfo info, bool announce)
    {
        _updateInfo = (info != null && info.Ok && info.IsNewer) ? info : null;
        UpdateUpdateLabel();

        if (!announce || info == null) return;

        if (!info.Ok)
        {
            Info("检查更新失败：" + info.Error + "\r\n\r\n（这只影响更新提示，清理功能不受影响。）");
            return;
        }
        if (info.NoRelease)
        {
            Warn("作者还没有在 GitHub 上发布任何版本。");
            return;
        }
        if (!info.IsNewer)
        {
            Info("已是最新版本 v" + Program.AppVersion + "。");
            return;
        }
        if (Confirm("发现新版本 v" + info.Latest + "（当前 v" + Program.AppVersion + "）。\r\n\r\n现在打开下载页？"))
        {
            OpenUrl(info.PageUrl);
        }
    }

    private void UpdateUpdateLabel()
    {
        if (_lblTagline == null) return;

        if (_updateBusy)
        {
            _lblTagline.Text = "正在检查更新...";
            _lblTagline.ForeColor = Theme.TextFaint;
            _tips.SetToolTip(_lblTagline, "正在向 GitHub 查询最新发布");
            return;
        }
        if (_updateInfo != null)
        {
            _lblTagline.Text = "发现新版本 v" + _updateInfo.Latest + " —— 点击打开下载页";
            _lblTagline.ForeColor = Theme.Accent;
            _tips.SetToolTip(_lblTagline,
                "当前 v" + Program.AppVersion + "，最新 v" + _updateInfo.Latest + "；点击打开下载页");
            return;
        }
        _lblTagline.Text = Program.AppTagline;
        _lblTagline.ForeColor = Theme.TextMuted;
        _tips.SetToolTip(_lblTagline, "点击检查更新");
    }

    private void OnUpdateClick(object sender, EventArgs e)
    {
        if (_updateBusy) return;

        if (_updateInfo != null)
        {
            if (Confirm("新版本 v" + _updateInfo.Latest + " 已发布（当前 v" + Program.AppVersion + "）。\r\n\r\n" +
                        "现在打开下载页？"))
            {
                OpenUrl(_updateInfo.PageUrl);
            }
            return;
        }
        StartUpdateCheck(true);
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(url); }   // ShellExecute: the default browser takes it
        catch (Exception ex)
        {
            Warn("无法打开浏览器：" + ex.Message + "\r\n\r\n请手动访问：\r\n" + url);
        }
    }

    private bool Confirm(string message)
    {
        return MessageBox.Show(this, message, Program.AppName,
                   MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    // A control learns its colours in one of two ways: it implements IThemed (it caches
    // them in fields, so they must be re-pushed), or it declares a colour role in Tag.
    private static void ApplyPaletteTree(Control root)
    {
        foreach (Control c in root.Controls)
        {
            IThemed themed = c as IThemed;
            if (themed != null) themed.ApplyPalette();

            string role = c.Tag as string;
            if (role != null) ApplyRole(c, role);

            ApplyPaletteTree(c);
        }
    }

    // A role is a '|'-separated list of tokens. Background tokens: bg / card /
    // warn-soft / console. Foreground tokens: text / muted / faint / warn /
    // console-muted. "log" is both (the console TextBox). One label commonly needs both
    // halves, hence e.g. "card|muted".
    private static void ApplyRole(Control c, string role)
    {
        string[] parts = role.Split('|');
        for (int i = 0; i < parts.Length; i++)
        {
            switch (parts[i])
            {
                case "bg": c.BackColor = Theme.Bg; break;
                case "card": c.BackColor = Theme.Card; break;
                case "warn-soft": c.BackColor = Theme.WarnSoft; break;
                case "console": c.BackColor = Theme.ConsoleBg; break;
                case "log": c.BackColor = Theme.ConsoleBg; c.ForeColor = Theme.ConsoleFg; break;
                case "text": c.ForeColor = Theme.Text; break;
                case "muted": c.ForeColor = Theme.TextMuted; break;
                case "faint": c.ForeColor = Theme.TextFaint; break;
                case "warn": c.ForeColor = Theme.Warn; break;
                case "console-muted": c.ForeColor = Theme.ConsoleMuted; break;
            }
        }
        c.Invalidate();
    }

    // Declares a control's colour role on creation; returns it so the call reads like an
    // ordinary statement next to the rest of the control's setup.
    private static T Role<T>(T c, string role) where T : Control
    {
        c.Tag = role;
        return c;
    }

    // The window frame is drawn by DWM, not by us: without this the title bar would keep
    // the old light/dark look after a switch. 20 is DWMWA_USE_IMMERSIVE_DARK_MODE on
    // Windows 10 20H1+; the 1809/1903 builds answered to 19, so that is the fallback.
    // Any failure (older build, DWM composition off) is cosmetic and ignored.
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyTitleBarTheme()
    {
        if (!IsHandleCreated) return;
        try
        {
            int v = Theme.Current.Dark ? 1 : 0;
            if (DwmSetWindowAttribute(Handle, 20, ref v, 4) != 0)
                DwmSetWindowAttribute(Handle, 19, ref v, 4);
        }
        catch { }
    }

    private void UpdateThemeButton()
    {
        if (_btnTheme == null) return;
        _tips.SetToolTip(_btnTheme, "颜色主题（当前：" + Theme.Describe() + "）");
    }

    private void UpdateLogButton()
    {
        if (_btnLog == null) return;
        int unread = Log.Unread;
        if (unread == _logBadgeUnread) return;
        _logBadgeUnread = unread;
        _btnLog.Text = unread > 0 ? ("输出日志 • " + unread) : "输出日志";
        _tips.SetToolTip(_btnLog, unread > 0
            ? ("有 " + unread + " 行新输出（点开查看）")
            : "打开独立窗口查看完整输出日志");
    }

    private void OnLogClick(object sender, EventArgs e)
    {
        if (_logWindow == null || _logWindow.IsDisposed)
        {
            _logWindow = new LogWindow();
            _logWindow.FormClosed += delegate { _logWindow = null; UpdateLogButton(); };
            _logWindow.Show(this);
        }
        else
        {
            _logWindow.Activate();
        }
        Log.MarkOpen(true);
        UpdateLogButton();
    }

    // Every line goes to the log bus (and thus to the log window). The pages no longer
    // show raw output, so all they take from it is a tally plus the last success line,
    // which becomes the page's result card.
    private void OnAnyLogLine(string line)
    {
        if (line.StartsWith("[+]")) { _countOk++; _lastOk = line.Substring(3).Trim(); }
        else if (line.StartsWith("[!]")) _countWarn++;
        else if (line.StartsWith("[x]")) { _countErr++; _lastErr = line.Substring(3).Trim(); }

        if (_logWindow != null && !_logWindow.IsDisposed) return;
        if (!_logBadge.Enabled) _logBadge.Start();
    }

    private void OnThemeClick(object sender, EventArgs e)
    {
        if (_themeMenu == null)
        {
            _themeMenu = new ContextMenuStrip();
            _themeMenu.Renderer = new FlatMenuRenderer();
            _themeMenu.ShowImageMargin = false;   // the accent swatch is drawn by the renderer
            _themeMenu.ShowCheckMargin = true;    // the tick still gets its own column
            _themeMenu.DropShadowEnabled = false;
            _themeMenu.Padding = new Padding(0, 4, 0, 4);
        }

        // Rebuilt on every open, so the tick, the swatch colours and the "follow system"
        // note always describe the state the window is in right now.
        _themeMenu.Items.Clear();
        _themeMenu.BackColor = Theme.Card;
        _themeMenu.ForeColor = Theme.Text;

        _themeMenu.Items.Add(SectionItem("浅色 / 深色"));
        AddModeItem(Theme.ModeSystem, "跟随系统");
        AddModeItem(Theme.ModeLight, "浅色");
        AddModeItem(Theme.ModeDark, "深色");

        _themeMenu.Items.Add(new ToolStripSeparator());

        _themeMenu.Items.Add(SectionItem("强调色"));
        AccentDef[] accents = Theme.AccentList;
        for (int i = 0; i < accents.Length; i++)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(accents[i].Name);
            it.Tag = "accent:" + accents[i].Id;
            it.Checked = (accents[i].Id == Theme.AccentId);
            it.Click += new EventHandler(OnThemeMenuItemClick);
            _themeMenu.Items.Add(it);
        }

        _themeMenu.Show(_btnTheme, new Point(0, _btnTheme.Height + 4));
    }

    private static ToolStripMenuItem SectionItem(string text)
    {
        ToolStripMenuItem head = new ToolStripMenuItem(text);
        head.Enabled = false;
        head.Tag = "head";
        return head;
    }

    private void AddModeItem(string mode, string label)
    {
        string text = label;
        if (mode == Theme.ModeSystem)
            text += "（当前 " + (Theme.Current.Dark ? "深色" : "浅色") + "）";

        ToolStripMenuItem it = new ToolStripMenuItem(text);
        it.Tag = "mode:" + mode;
        it.Checked = (Theme.Mode == mode);
        it.Click += new EventHandler(OnThemeMenuItemClick);
        _themeMenu.Items.Add(it);
    }

    private void OnThemeMenuItemClick(object sender, EventArgs e)
    {
        string tag = Convert.ToString(((ToolStripMenuItem)sender).Tag);
        if (tag.StartsWith("mode:")) Theme.SetMode(tag.Substring(5));
        else if (tag.StartsWith("accent:")) Theme.SetAccent(tag.Substring(7));
        else return;

        // Selecting "follow system" (or any mode) re-syncs the cached OS state, so the
        // watcher below cannot fire a bogus change on its next tick.
        _lastSystemDark = Theme.SystemPrefersDark();
        Theme.SavePreference();
        ApplyPaletteAll();
        Log.Append("[外观] 已切换为：" + Theme.Describe());
    }

    // Only "follow system" has anything to watch. The flag is re-read every 1.5 s while
    // the window is open: one registry read, no message pump, and it also catches the
    // scheduled light/dark switch that never sends a message at all.
    private void OnThemeWatchTick(object sender, EventArgs e)
    {
        if (Theme.Mode != Theme.ModeSystem) return;

        bool dark = Theme.SystemPrefersDark();
        if (dark == _lastSystemDark) return;

        _lastSystemDark = dark;
        Theme.Refresh();
        ApplyPaletteAll();
        Log.Append("[外观] 检测到 Windows 主题变化，已自动跟随为" + (dark ? "深色" : "浅色") + "。");
    }

    // ------------------------------------------------------------- bootstrap

    private void OnLoad(object sender, EventArgs e)
    {
        _runtimeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CLean", "runtime");
        _powershell = ResolvePowerShell();

        Log.Append(Program.AppName + "  v" + Program.AppVersion);
        Log.Append("运行目录: " + _runtimeDir);
        Log.Append("解释器  : " + _powershell);
        Log.Append("");

        // Ask GitHub whether a newer release exists. Runs on a thread-pool thread,
        // never blocks the window, and says nothing at all when it has no news.
        StartUpdateCheck(false);

        try
        {
            int written = ExtractScripts();
            Log.Append("已释放 " + written + " 个脚本组件（可直接审计）。");
        }
        catch (Exception ex)
        {
            Log.Append("[错误] 释放运行组件失败: " + ex.Message);
            MessageBox.Show(this, "释放运行组件失败:\r\n" + ex.Message, Program.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        Log.Append("");
        Log.Append("─── 使用说明 ───────────────────────────────────────────");
        Log.Append("左侧功能栏选一个功能（可折叠），每页底部是它自己的操作区。");
        Log.Append("需要删除的功能都会先跑一遍预演（不删任何文件），预演结束后");
        Log.Append("底部会出现黄色确认条，只有点「执行删除」才会真正删除。");
        Log.Append("「隐藏项目缓存扫描」会先扫描、列表勾选，再预演、再确认。");
        Log.Append("完整输出在右上角「输出日志」按钮打开的独立窗口里。");
        Log.Append("────────────────────────────────────────────────────────");
        Log.Append("");

        RefreshSystemInfo();
        SetBusy(false, "就绪");
    }

    private void OnShown(object sender, EventArgs e)
    {
        // The handle exists from here on, which is what the DWM title-bar call needs.
        ApplyTitleBarTheme();
        _themeWatch.Start();
        _logBadge.Start();
        UpdateLogButton();
    }

    private static string ResolvePowerShell()
    {
        string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string p = Path.Combine(windir, @"System32\WindowsPowerShell\v1.0\powershell.exe");
        if (File.Exists(p)) return p;
        p = Path.Combine(windir, @"SysWOW64\WindowsPowerShell\v1.0\powershell.exe");
        if (File.Exists(p)) return p;
        return "powershell.exe";
    }

    private int ExtractScripts()
    {
        Directory.CreateDirectory(_runtimeDir);
        Assembly asm = Assembly.GetExecutingAssembly();
        string[] names = asm.GetManifestResourceNames();
        int written = 0;

        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            if (!name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) continue;

            string target = Path.Combine(_runtimeDir, name);
            Stream src = null;
            FileStream dst = null;
            try
            {
                src = asm.GetManifestResourceStream(name);
                if (src == null) continue;
                dst = new FileStream(target, FileMode.Create, FileAccess.Write);
                src.CopyTo(dst);
                written++;
            }
            finally
            {
                if (dst != null) dst.Dispose();
                if (src != null) src.Dispose();
            }
        }

        if (written == 0)
            throw new Exception("可执行文件中未找到内嵌脚本（打包时未附加 /resource？）");
        return written;
    }

    // ------------------------------------------------------------- system info

    private void RefreshSystemInfo()
    {
        bool admin = IsAdmin();

        _pillOs.SetContent(OsLabel(), PillTone.Neutral);
        if (admin)
            _pillAdmin.SetContent("管理员权限", PillTone.Success);
        else
            _pillAdmin.SetContent("普通用户", PillTone.Danger);

        _btnElevate.Visible = !admin;

        try
        {
            DriveInfo c = new DriveInfo("C");
            if (c.IsReady)
            {
                long total = c.TotalSize;
                long free = c.AvailableFreeSpace;
                long used = total - free;
                int pct = (total > 0) ? (int)Math.Round(used * 100.0 / total) : 0;

                _lblDiskName.Text = (admin ? "C: 系统盘" : "C: 系统盘 （未提权，系统目录会被跳过）");
                _lblFreeValue.Text = Gb(free) + " GB";
                _lblFreeValue.Font = Theme.FontBig;
                _lblFreeUnit.Location = new Point(_lblFreeValue.Right + 8, _lblFreeValue.Top + 16);
                _lblDiskDetail.Text = String.Format("共 {0} GB   ·   已用 {1} GB ({2}%)", Gb(total), Gb(used), pct);
                _lblDiskDetail.Location = new Point(20, 72);
                _usage.SetValue(used, total);
            }
        }
        catch (Exception ex)
        {
            _lblFreeValue.Text = "--";
            _lblDiskDetail.Text = "C: 信息不可用 (" + ex.Message + ")";
        }

        LayoutHero();
    }

    private static string Gb(long bytes)
    {
        return Math.Round(bytes / 1073741824.0, 2).ToString("0.00");
    }

    // 22000+ is Windows 11; Win11 still reports kernel 10.0 and ProductName often
    // still says "Windows 10", so the build number is the only reliable signal.
    private static string OsLabel()
    {
        Version v = Environment.OSVersion.Version;
        string name;
        if (v.Major == 10 && v.Build >= 22000) name = "Windows 11";
        else if (v.Major == 10) name = "Windows 10";
        else name = "Windows " + v.Major + "." + v.Minor;

        string edition = "";
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                       @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (k != null)
                {
                    object e = k.GetValue("EditionID");
                    if (e != null) edition = EditionZh(e.ToString());
                }
            }
        }
        catch { }

        if (edition.Length > 0) name = name + " " + edition;
        return name + " (Build " + v.Build + ")";
    }

    private static string EditionZh(string id)
    {
        switch (id)
        {
            case "Professional": return "专业版";
            case "ProfessionalN": return "专业版 N";
            case "ProfessionalEducation": return "专业教育版";
            case "ProfessionalWorkstation": return "专业工作站版";
            case "Enterprise": return "企业版";
            case "EnterpriseN": return "企业版 N";
            case "Education": return "教育版";
            case "EducationN": return "教育版 N";
            case "Core": return "家庭版";
            case "CoreN": return "家庭版 N";
            case "CoreSingleLanguage": return "家庭单语言版";
            case "CoreCountrySpecific": return "家庭中文版";
            case "ServerStandard": return "Server 标准版";
            case "ServerDatacenter": return "Server 数据中心版";
            default: return id;
        }
    }

    private static bool IsAdmin()
    {
        try
        {
            WindowsIdentity id = WindowsIdentity.GetCurrent();
            WindowsPrincipal pr = new WindowsPrincipal(id);
            return pr.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // --------------------------------------------------------- host services

    // Small surface the pages use to talk to the window.
    internal void Info(string message)
    {
        MessageBox.Show(this, message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    internal void Warn(string message)
    {
        MessageBox.Show(this, message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    internal void SetBusy(bool running, string status)
    {
        ToolPage page = CurrentPage;

        _btnRun.Enabled = !running && (page == null || page.CanRun);
        _btnCancel.Enabled = running;
        if (_railButtons != null)
            for (int i = 0; i < _railButtons.Length; i++) _railButtons[i].Enabled = !running;

        if (running)
        {
            _activity.Start();
            _elapsed.Start();
            UpdateRunStatus();
        }
        else
        {
            _activity.Stop();
            _elapsed.Stop();
            if (status != null) _lblStatus.Text = status;
        }
    }

    internal void UpdateRunButton()
    {
        ToolPage page = CurrentPage;
        if (page == null || _btnRun == null) return;
        _btnRun.Text = page.RunLabel();
        _btnRun.Enabled = (_proc == null) && !page.Busy;
    }

    // Free space may have moved after any run; a scan also updates the disk card.
    internal void ScanFinished()
    {
        RefreshSystemInfo();
    }

    internal void RevealInExplorer(string path)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"");
            psi.UseShellExecute = true;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Warn("无法打开资源管理器: " + ex.Message);
        }
    }

    // Hand a big folder over to the cleanup page: that page owns deletion, and only for
    // paths its own script accepts.
    internal void HandOffToHiddenPage(string path)
    {
        for (int i = 0; i < _pages.Length; i++)
        {
            HiddenCachePage hidden = _pages[i] as HiddenCachePage;
            if (hidden == null) continue;

            ShowPage(i);
            hidden.AcceptHandOff(path);
            return;
        }
    }

    // ------------------------------------------------------------- ui events

    private void OnRunClick(object sender, EventArgs e)
    {
        ToolPage page = CurrentPage;
        if (page == null || page.Busy || _proc != null) return;
        page.RunPrimary();
    }

    private void OnCancelClick(object sender, EventArgs e)
    {
        ToolPage page = CurrentPage;
        if (page != null && page.Busy)
        {
            page.CancelWork();
            _lblStatus.Text = "正在取消扫描...";
            return;
        }
        if (_proc == null) return;

        _cancelRequested = true;
        int pid = -1;
        try { pid = _proc.Id; } catch { }
        Log.Append("");
        Log.Append(">>> 正在取消（终止整个进程树）...");
        _btnCancel.Enabled = false;
        if (pid > 0) KillTree(pid);
    }

    private void OnRefreshClick(object sender, EventArgs e)
    {
        RefreshSystemInfo();
        if (_proc == null && (CurrentPage == null || !CurrentPage.Busy)) SetBusy(false, "就绪");
    }

    private void OnOpenReportsClick(object sender, EventArgs e)
    {
        try
        {
            string rep = Path.Combine(_runtimeDir, "reports");
            if (!Directory.Exists(rep)) Directory.CreateDirectory(rep);
            Process.Start("explorer.exe", "\"" + rep + "\"");
        }
        catch (Exception ex)
        {
            Warn("无法打开报告目录: " + ex.Message);
        }
    }

    private void OnElevateClick(object sender, EventArgs e)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            Process.Start(psi);
            Close();
        }
        catch (Exception ex)
        {
            Warn("提权启动失败: " + ex.Message);
        }
    }

    private void OnConfirmYesClick(object sender, EventArgs e)
    {
        if (_pending == null || _proc != null) return;
        PendingRun run = _pending;
        _pending = null;
        _confirmStrip.Visible = false;
        Log.Append("");
        Log.Append(">>> 用户已确认，开始真正执行（-Execute）。");
        RunScript(run, true);
    }

    private void OnConfirmNoClick(object sender, EventArgs e)
    {
        _pending = null;
        _confirmStrip.Visible = false;
        Log.Append(">>> 已取消，未删除任何文件。");
        SetBusy(false, "就绪");
    }

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (_logWindow != null && !_logWindow.IsDisposed) _logWindow.Close();
        if (CurrentPage != null) CurrentPage.Shutdown();

        if (_proc == null) return;

        DialogResult r = MessageBox.Show(this,
            "还有任务正在运行。现在退出会终止它。\n（已经删除的内容不会恢复，正在进行的目标会被打断。）\n\n确定退出？",
            Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        _cancelRequested = true;
        int pid = -1;
        try { pid = _proc.Id; } catch { }
        if (pid > 0) KillTree(pid);
    }

    // ------------------------------------------------------------- run engine

    internal void RunScript(PendingRun run, bool execute)
    {
        if (_proc != null || run == null) return;

        string script = Path.Combine(_runtimeDir, run.Script);
        if (!File.Exists(script))
        {
            Warn("缺少脚本文件:\r\n" + script);
            return;
        }

        string args = run.ArgsFor(execute);
        _running = run;
        _cancelRequested = false;
        _pending = null;
        _confirmStrip.Visible = false;
        _runStarted = DateTime.Now;
        _runSeconds = 0;

        string tag = run.ReadOnly ? "[只读]" : (execute ? "[执行]" : "[预演 dry-run]");
        SetBusy(true, null);

        _countOk = 0;
        _countWarn = 0;
        _countErr = 0;
        _lastOk = "";
        _lastErr = "";
        if (run.Owner is ScriptPage) ((ScriptPage)run.Owner).Summary.Reset(null);

        ToolPage page = run.Owner != null ? run.Owner : CurrentPage;
        if (page != null) page.Anim.BeginRun("正在运行 · " + run.Title + " " + tag);

        Log.Append("");
        Log.Append("═══════════ " + _runStarted.ToString("yyyy-MM-dd HH:mm:ss") + "  "
            + run.Title + "  " + tag + " ═══════════");
        Log.Append("> " + run.Script + (args.Length > 0 ? " " + args : ""));
        Log.Append("");

        StartProcess(script, args);
    }

    private void StartProcess(string script, string args)
    {
        // [Console]::OutputEncoding is forced to UTF-8 so the child's host output
        // survives the pipe; several scripts set it themselves, but not all of them.
        string inner = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; & '"
            + script.Replace("'", "''") + "'"
            + (args.Length > 0 ? " " + args : "");

        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = _powershell;
        psi.Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \""
            + inner + "\"";
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = _runtimeDir;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);

        Process p = new Process();
        p.StartInfo = psi;
        p.EnableRaisingEvents = true;
        p.OutputDataReceived += new DataReceivedEventHandler(OnStdOut);
        p.ErrorDataReceived += new DataReceivedEventHandler(OnStdErr);
        p.Exited += new EventHandler(OnExited);

        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            Log.Append("[错误] 无法启动 PowerShell: " + ex.Message);
            _running = null;
            SetBusy(false, "启动失败");
            return;
        }

        _proc = p;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
    }

    private void OnStdOut(object sender, DataReceivedEventArgs e)
    {
        if (e.Data == null) return;
        AppendLogAsync(e.Data);
    }

    private void OnStdErr(object sender, DataReceivedEventArgs e)
    {
        if (e.Data == null) return;
        AppendLogAsync("[stderr] " + e.Data);
    }

    private void OnExited(object sender, EventArgs e)
    {
        Process p = (Process)sender;
        int code = -1;
        try
        {
            // Guarantees the async stdout/stderr readers have drained first, so the log is
            // complete before the confirmation strip appears.
            p.WaitForExit();
            code = p.ExitCode;
        }
        catch { }

        try { BeginInvoke((MethodInvoker)delegate { FinishRun(code); }); }
        catch { /* form already gone */ }
    }

    private void FinishRun(int code)
    {
        PendingRun run = _running;
        bool cancelled = _cancelRequested;

        if (_proc != null)
        {
            try { _proc.Dispose(); } catch { }
            _proc = null;
        }
        _running = null;

        Log.Append("");
        if (cancelled)
            Log.Append("──── 已取消（退出码 " + code + "）────");
        else if (code == 0)
            Log.Append("──── 完成（退出码 0）────");
        else
            Log.Append("──── 结束，退出码 " + code + "（详见上方输出）────");

        RefreshSystemInfo();

        ToolPage page = run != null && run.Owner != null ? run.Owner : CurrentPage;

        // The page's animation reports the outcome; the page's own result card (script
        // pages) shows the tally and the last success line - never the raw output.
        if (page != null)
        {
            string caption;
            if (cancelled) caption = "已取消 · 退出码 " + code;
            else if (code == 0) caption = "完成 · 退出码 0";
            else caption = "运行结束 · 退出码 " + code;

            string detail = String.Format("用时 {0} 秒 · 成功 {1} · 警告 {2} · 失败 {3}",
                _runSeconds, _countOk, _countWarn, _countErr);
            page.Anim.EndRun(caption, detail);

            string key = _lastOk.Length > 0 ? _lastOk : _lastErr;
            page.ShowRunResult(cancelled, code, _runSeconds, _countOk, _countWarn, _countErr, key);
        }

        if (run == null)
        {
            SetBusy(false, "就绪");
            return;
        }

        // Phase 2 only ever starts from a deliberate click on the strip; a successful,
        // uncancelled, non-execute run of a destructive action is what makes it appear.
        if (!cancelled && code == 0 && !run.ReadOnly)
        {
            _pending = run;
            _lblConfirm.Text = run.Confirm.Length > 0
                ? run.Confirm
                : ("预演完成，尚未删除任何文件。现在执行「" + run.Title + "」？");
            _confirmStrip.Visible = true;
            SetBusy(false, "等待确认：预演已完成，未删除任何文件");
        }
        else if (cancelled)
        {
            Log.Append("提示: 中途取消的删除无法回滚，但脚本的净空间守卫会如实记录已发生的变化。");
            SetBusy(false, "已取消");
        }
        else
        {
            SetBusy(false, "就绪 · 上次运行 " + _runSeconds + " 秒，退出码 " + code);
        }

        if (page != null) page.OnRunFinished(run.Title, false, code);
        UpdateRunButton();
    }

    private static void KillTree(int pid)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("taskkill", "/PID " + pid + " /T /F");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi);
        }
        catch { /* the process may already be gone */ }
    }

    // ------------------------------------------------------------- plan dump

    // Written by --print-plan. Shows, per page, the command line behind the run button
    // (phase 1) and behind the confirmation strip (phase 2).
    internal string DumpPlan()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("CLean · 窗口版 -- 命令计划（默认选项下每个按钮会执行什么）");
        sb.AppendLine("生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine();
        sb.AppendLine("阶段 1 = 点页面底部主按钮执行的命令（绝不带 -Execute，只读或预演）");
        sb.AppendLine("阶段 2 = 预演成功后，点确认条上的「执行删除」执行的命令（带 -Execute）");
        sb.AppendLine();
        sb.AppendLine("实际调用方式: PowerShell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass");
        sb.AppendLine("             -Command \"<设置 UTF-8 输出编码>; & '<脚本路径>' <下列参数>\"");
        sb.AppendLine("脚本路径 = %LOCALAPPDATA%\\CLean\\runtime\\<脚本名>");
        sb.AppendLine("两个扫描页（全盘大目录 / 隐藏项目缓存）由内置 C# 扫描器完成，不经过 PowerShell；");
        sb.AppendLine("「隐藏项目缓存」的删除仍然交给 04-Clean-Hidden.ps1，并带 -Paths 与 -Execute。");
        sb.AppendLine();

        if (_pages != null)
        {
            for (int i = 0; i < _pages.Length; i++)
            {
                sb.AppendLine(String.Format("[{0}] {1}", i + 1, _pages[i].Title));
                sb.Append(_pages[i].PlanLines());
                sb.AppendLine();
            }
        }

        sb.AppendLine("说明: 阶段 2 只有 FinishRun() 在退出码为 0 且动作未被取消时才会");
        sb.AppendLine("      让确认条出现，而 OnConfirmYesClick() 是唯一带 execute=true 调用");
        sb.AppendLine("      的入口。因此在点击确认条之前，一定不会发生任何删除。");
        sb.AppendLine("      扫描页的删除按钮同样只发起阶段 1。");
        return sb.ToString();
    }

    // ------------------------------------------------------------- ui plumbing

    private void UpdateRunStatus()
    {
        if (_proc != null && _running != null)
        {
            string tag = _running.ReadOnly ? "只读" : "运行中";
            _lblStatus.Text = String.Format("运行中 · {0} [{1}] · 已用 {2} 秒",
                _running.Title, tag, _runSeconds);
            ToolPage page = _running.Owner != null ? _running.Owner : CurrentPage;
            if (page != null)
                page.Anim.SetDetail(String.Format("已用 {0} 秒 · 正在运行 {1} · 成功 {2} · 警告 {3}",
                    _runSeconds, _running.Script, _countOk, _countWarn));
            return;
        }

        ToolPage current = CurrentPage;
        if (current != null && current.Busy)
            _lblStatus.Text = String.Format("扫描中 · {0} · 已用 {1} 秒", current.Title, _runSeconds);
    }

    private void AppendLogAsync(string line)
    {
        try
        {
            // Output arrives on a background reader thread.
            BeginInvoke((MethodInvoker)delegate { Log.Append(line); });
        }
        catch { /* form closing */ }
    }
}
