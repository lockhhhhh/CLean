// CLean -- single-file launcher.
//
// Build:  see src/Build.ps1  (uses the in-box csc.exe, no SDK required)
// Target: any Windows 10 / 11 with .NET Framework 4.x (present by default).
//
// The launcher embeds every .ps1 as a managed resource, extracts them to
// %LOCALAPPDATA%\CLean\runtime on first run (so they stay readable and
// auditable), and drives them through a console menu.
//
// SAFETY CONTRACT (matches the scripts' own design):
//   * every destructive action is run once as a DRY RUN, its plan is shown, and
//     the user must answer "y" before the same action is re-run with -Execute;
//   * the extracted scripts re-verify every path against their own allowlist,
//     so nothing outside it can be deleted even if the menu is misused.
//
// Deliberately written in conservative C# 5 syntax: the in-box csc.exe on older
// Windows builds may not accept newer language versions.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;

internal static class Program
{
    private const string AppName = "CLean";
    private const string AppVersion = "2.0";

    private static string _runtimeDir;
    private static string _powershell;

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); }
        catch { /* redirected or unsupported console */ }
        try { Console.Title = AppName + " v" + AppVersion; }
        catch { }

        _runtimeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CLean", "runtime");
        _powershell = ResolvePowerShell();

        try
        {
            ExtractScripts();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(String.Format("  释放运行组件失败: {0}", ex.Message));
            Pause();
            return 1;
        }

        try
        {
            return RunMenu();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(String.Format("  运行出错: {0}", ex.Message));
            Pause();
            return 1;
        }
    }

    // ------------------------------------------------------------- bootstrap

    private static string ResolvePowerShell()
    {
        string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string p = Path.Combine(windir, @"System32\WindowsPowerShell\v1.0\powershell.exe");
        if (File.Exists(p)) return p;
        p = Path.Combine(windir, @"SysWOW64\WindowsPowerShell\v1.0\powershell.exe");
        if (File.Exists(p)) return p;
        return "powershell.exe";
    }

    private static void ExtractScripts()
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
    }

    // ------------------------------------------------------------- process

    private static int RunScript(string scriptName, string extraArgs)
    {
        string script = Path.Combine(_runtimeDir, scriptName);
        if (!File.Exists(script))
        {
            Console.WriteLine(String.Format("  缺少脚本: {0}", script));
            return 2;
        }

        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = _powershell;
        psi.Arguments =
            "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"" +
            (String.IsNullOrEmpty(extraArgs) ? "" : " " + extraArgs);
        psi.UseShellExecute = false;
        psi.WorkingDirectory = _runtimeDir;
        // stdout/stderr are NOT redirected: the child inherits this console, so
        // CJK output survives without an encoding round-trip.

        using (Process p = Process.Start(psi))
        {
            p.WaitForExit();
            return p.ExitCode;
        }
    }

    // ------------------------------------------------------------- ui helpers

    // Console.Clear() throws IOException when stdout is redirected (piped, or
    // captured by a terminal/log tool). Clearing is purely cosmetic, so this
    // degrades quietly instead of killing the menu.
    private static void SafeClear()
    {
        try { Console.Clear(); }
        catch { }
    }

    // Windows 11 still reports kernel version 10.0, and its ProductName
    // registry value frequently still reads "Windows 10 ..." (a long-standing
    // Microsoft quirk). The build number is the only reliable discriminator:
    // 22000 and above is Windows 11.
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
        catch { /* registry unavailable: fall back to the bare version name */ }

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

    private static void Pause()
    {
        Console.WriteLine();
        Console.Write("  按回车返回主菜单...");
        Console.ReadLine();
    }

    private static bool Confirm(string what)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  即将执行: " + what);
        Console.WriteLine("  上面显示的是预演(dry-run)结果，目前还没有删除任何文件。");
        Console.ResetColor();
        Console.Write("  确认执行？输入 y 回车确认，直接回车取消: ");
        string a = Console.ReadLine();
        if (a == null) return false;
        a = a.Trim().ToLowerInvariant();
        return a == "y" || a == "yes";
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

    private static string Gb(long bytes)
    {
        return Math.Round(bytes / 1073741824.0, 2).ToString("0.00");
    }

    private static void DrawHeader()
    {
        SafeClear();
        Console.WriteLine("============================================================");
        Console.WriteLine("  " + AppName + "   v" + AppVersion);
        Console.WriteLine("------------------------------------------------------------");

        Console.WriteLine(String.Format("  {0}      管理员: {1}",
            OsLabel(), IsAdmin() ? "是" : "否"));

        try
        {
            DriveInfo c = new DriveInfo("C");
            if (c.IsReady)
            {
                Console.WriteLine(String.Format("  C: 可用 {0} GB / 共 {1} GB",
                    Gb(c.AvailableFreeSpace), Gb(c.TotalSize)));
            }
        }
        catch { }

        if (!IsAdmin())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  [!] 未以管理员运行：Windows 更新缓存 / Windows\\Temp / 传递优化缓存会被跳过。");
            Console.ResetColor();
        }
        Console.WriteLine("============================================================");
    }

    // ------------------------------------------------------------- menu

    private static int RunMenu()
    {
        while (true)
        {
            DrawHeader();
            Console.WriteLine("  [1] 空间分析               只读，含目录级缓存清单");
            Console.WriteLine("  [2] 系统缓存清理           点到点删除 + 净空间保证（越清不会越大）");
            Console.WriteLine("  [3] 无用缓存发现           只读，扫描未列入清单的缓存目录");
            Console.WriteLine("  [4] 显卡着色器缓存         自动识别 N 卡 / A 卡，无显卡则跳过");
            Console.WriteLine("  [5] C 盘根目录残留         HLS 缓存 / 酷狗 P2P / 升级回滚数据");
            Console.WriteLine("  [6] 开发环境缓存           npm / pnpm / pip / yarn / go");
            Console.WriteLine("  [7] 全盘大目录扫描         只读，定位占空间大户");
            Console.WriteLine("  [8] 隐藏项目缓存扫描       只读，找 node_modules 等");
            Console.WriteLine("  [9] 空间反升诊断           只读，量化「影子副本/占用」等反升原因");
            Console.WriteLine("  [r] 打开报告目录");
            Console.WriteLine("  [0] 退出");
            Console.WriteLine();
            Console.Write("  请选择 [0-9/r]: ");

            string choice = Console.ReadLine();
            if (choice == null) return 0;
            choice = choice.Trim().ToLowerInvariant();

            switch (choice)
            {
                case "1": ActAnalyze(); break;
                case "2": ActSystem(); break;
                case "3": ActDiscover(); break;
                case "4": ActGpu(); break;
                case "5": ActRootLeftovers(); break;
                case "6": ActDev(); break;
                case "7": ActScan(); break;
                case "8": ActHiddenScan(); break;
                case "9": ActDiagnose(); break;
                case "r": OpenReports(); break;
                case "0": return 0;
                default: break;
            }
        }
    }

    private static void ActAnalyze()
    {
        SafeClear();
        Console.WriteLine("  [1] 空间分析 —— 只读，不会删除任何文件\n");
        RunScript("01-Analyze.ps1", "-Tag before");
        Pause();
    }

    private static void ActSystem()
    {
        SafeClear();
        Console.WriteLine("  [2] 系统缓存清理 —— 点到点删除，并保证 C 盘已用空间只降不升\n");
        Console.WriteLine("  清理范围（全为标准清单，逐条精确匹配文件，不再整目录整棵树扫）:");
        Console.WriteLine("    %TEMP% / Windows\\Temp / Windows\\SystemTemp / 更新下载缓存 / 传递优化");
        Console.WriteLine("    各类日志 / 崩溃转储 / WER 报告 / 缩略图 / INetCache / 字体缓存");
        Console.WriteLine("    浏览器各配置的 Cache、Code Cache、GPUCache（不含登录态与历史）");
        Console.WriteLine();
        Console.WriteLine("  清理深度:");
        Console.WriteLine("    [1] 标准   只删「最后修改早于 1 天」的临时文件  —— 安全，推荐");
        Console.WriteLine("    [2] 深度   连同今天产生的临时文件一并删除");
        Console.WriteLine("               可能影响正在运行的程序；被占用的文件仍会自动跳过\n");
        Console.Write("  请选择 [1/2]，直接回车 = 标准: ");

        string depth = Console.ReadLine();
        if (depth != null) depth = depth.Trim();

        string depthArg = "";
        string depthLabel = "标准（仅 1 天前）";
        if (depth == "2")
        {
            depthArg = " -TempAgeDays 0";
            depthLabel = "深度（含今天）";
        }

        Console.WriteLine();
        Console.WriteLine("  清理档位:");
        Console.WriteLine("    [1] 保守   仅绝对无争议的垃圾（临时文件、日志、转储）");
        Console.WriteLine("    [2] 标准   再加浏览器缓存等可再生成的缓存   —— 推荐");
        Console.WriteLine("    [3] 完整   再加应用缓存发现、搜索索引等重建代价高的项\n");
        Console.Write("  请选择 [1/2/3]，直接回车 = 标准: ");

        string prof = Console.ReadLine();
        if (prof != null) prof = prof.Trim();
        string profArg = " -Profile standard";
        string profLabel = "标准";
        if (prof == "1") { profArg = " -Profile safe"; profLabel = "保守"; }
        else if (prof == "3") { profArg = " -Profile aggressive"; profLabel = "完整"; }

        Console.WriteLine();
        Console.WriteLine("  第一步：预演（不删除任何文件）");
        Console.WriteLine("  将打印每个目标的「可能释放」，并标出被占用、无法真正释放空间的文件。\n");
        RunScript("02-Clean-System.ps1", depthArg + profArg);

        if (Confirm("系统缓存清理 [深度=" + depthLabel + " 档位=" + profLabel + "]（更新服务会临时停止，结束后自动重启）"))
        {
            SafeClear();
            Console.WriteLine("  第二步：执行（逐目标实测净空间；一旦空间反降立即中止）\n");
            RunScript("02-Clean-System.ps1", depthArg + profArg + " -Execute");
            Console.WriteLine();
            Console.WriteLine("  若报告「GUARANTEE BROKEN」或净释放远小于逻辑删除量，");
            Console.WriteLine("  请回主菜单运行 [9] 空间反升诊断 查看具体原因。");
        }
        else
        {
            Console.WriteLine("\n  已取消，未执行任何删除。");
        }
        Pause();
    }

    private static void ActDiscover()
    {
        SafeClear();
        Console.WriteLine("  [3] 无用缓存发现 —— 只读，不会删除任何文件\n");
        Console.WriteLine("  扫描 %LOCALAPPDATA% / %APPDATA% / ProgramData 中名为 Cache、Code Cache、");
        Console.WriteLine("  GPUCache、CacheStorage、Logs、Temp 等的目录，按体积排序，");
        Console.WriteLine("  并列出本工具不碰的大块残留（$WinREAgent、Windows.old、HLS 缓存）。\n");
        RunScript("02-Clean-System.ps1", "-Discover -DiscoverMinMB 20");
        Pause();
    }

    private static void ActDiagnose()
    {
        SafeClear();
        Console.WriteLine("  [9] 空间反升诊断 —— 只读，量化「为什么清完反而更满」\n");
        Console.WriteLine("  逐项测量: 卷影副本(系统还原)写时复制 / 硬链接 / 缓存再生 /");
        Console.WriteLine("            页面文件与休眠文件 / 逻辑大小与实占差异\n");
        RunScript("08-Diagnose-SpaceGrowth.ps1", "");
        Pause();
    }

    private static void ActGpu()
    {
        SafeClear();
        Console.WriteLine("  [4] 显卡着色器缓存 —— 第一步：识别显卡并预演\n");
        RunScript("07-Clean-GpuShaderCache.ps1", "-ShowAll");

        if (Confirm("清理上表所列的显卡着色器缓存（游戏下次启动需重新编译，可能短暂卡顿）"))
        {
            SafeClear();
            Console.WriteLine("  [4] 显卡着色器缓存 —— 正在执行\n");
            RunScript("07-Clean-GpuShaderCache.ps1", "-Execute -ShowAll");
        }
        else
        {
            Console.WriteLine("\n  已取消，未执行任何删除。");
        }
        Pause();
    }

    private static void ActRootLeftovers()
    {
        SafeClear();
        Console.WriteLine("  [5] C 盘根目录残留 —— 第一步：预演\n");
        Console.WriteLine("  说明: $WinREAgent 是上次系统更新的回滚数据，删除后无法回滚该更新。\n");
        RunScript("06-Clean-RootLeftovers.ps1", "-WinREAgent -HlsCache -KuGouCache");

        if (Confirm("清理 HLS 视频分片缓存 / 酷狗 P2P 缓存 / $WinREAgent 回滚数据"))
        {
            SafeClear();
            Console.WriteLine("  [5] C 盘根目录残留 —— 正在执行\n");
            RunScript("06-Clean-RootLeftovers.ps1", "-WinREAgent -HlsCache -KuGouCache -Execute");
        }
        else
        {
            Console.WriteLine("\n  已取消，未执行任何删除。");
        }
        Pause();
    }

    private static void ActDev()
    {
        SafeClear();
        Console.WriteLine("  [6] 开发环境缓存 —— 第一步：预演\n");
        RunScript("03-Clean-Dev.ps1", "");

        if (Confirm("清理开发工具缓存（npm / pnpm / pip / yarn / go）"))
        {
            SafeClear();
            Console.WriteLine("  [6] 开发环境缓存 —— 正在执行\n");
            RunScript("03-Clean-Dev.ps1", "-Execute");
        }
        else
        {
            Console.WriteLine("\n  已取消，未执行任何删除。");
        }
        Pause();
    }

    private static void ActScan()
    {
        SafeClear();
        Console.WriteLine("  [7] 全盘大目录扫描 —— 只读，耗时约数分钟\n");
        RunScript("05-Scan-LargeDirs.ps1", "-Top 40 -Tag largedirs-menu");
        Pause();
    }

    private static void ActHiddenScan()
    {
        SafeClear();
        Console.WriteLine("  [8] 隐藏项目缓存扫描 —— 只读，不会删除任何文件\n");
        Console.WriteLine("  如需删除扫描出的目录，请用命令行显式指定路径：");
        Console.WriteLine("    .\\04-Clean-Hidden.ps1 -Execute -Paths '<确切路径>'\n");
        RunScript("04-Clean-Hidden.ps1", "");
        Pause();
    }

    private static void OpenReports()
    {
        string rep = Path.Combine(_runtimeDir, "reports");
        try
        {
            if (!Directory.Exists(rep)) Directory.CreateDirectory(rep);
            Process.Start("explorer.exe", "\"" + rep + "\"");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  无法打开目录: " + ex.Message);
            Pause();
        }
    }
}
