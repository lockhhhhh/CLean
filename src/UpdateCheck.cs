// CLean -- "is there a newer release?" client.
//
// Asks the GitHub Releases API whether a newer CLean has been published. Shared
// by the console launcher and the WinForms one, so it must not depend on
// anything beyond System.dll.
//
// DESIGN RULES -- this tool is a cleaner that people run offline, and a status
// check must never get in the way of that:
//   * synchronous and self-contained; the caller decides which thread to use;
//   * 5 second timeout, and every failure is swallowed into UpdateInfo.Error.
//     An unreachable network is normal, not something to shout about;
//   * a successful answer is cached in %LOCALAPPDATA%\CLean\update.txt for 24
//     hours, so a normal launch does at most one request a day. The "is it
//     newer" verdict is always recomputed against the running binary, never
//     cached, so upgrading silently clears the notice;
//   * TLS 1.2 is enabled explicitly: .NET Framework 4.x does not always offer
//     it by default and api.github.com refuses anything below it;
//   * a User-Agent header is mandatory -- GitHub answers 403 without one;
//   * one GET of a public JSON document. No query string, no identifiers, no
//     telemetry of any kind.
//
// Deliberately written in conservative C# 5 syntax: the in-box csc.exe on older
// Windows builds may not accept newer language versions.

using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

internal sealed class UpdateInfo
{
    internal string Latest;         // "1.0.1" (the leading "v" is stripped)
    internal string ReleaseName;    // human label, may be null
    internal string AssetUrl;       // direct link to the Setup exe, may be null
    internal bool   IsNewer;        // Latest > the running version
    internal bool   NoRelease;      // the project has not published anything yet
    internal bool   FromCache;
    internal string Error;          // null when the check itself succeeded

    internal bool Ok { get { return Error == null; } }

    // The page a human should be sent to (works even for a stale cache).
    internal string PageUrl
    {
        get { return UpdateCheck.ReleasesUrl; }
    }

    internal string Display
    {
        get
        {
            if (Error != null) return "检查更新失败: " + Error;
            if (NoRelease) return "作者尚未发布任何版本";
            if (IsNewer) return "发现新版本 v" + Latest;
            return "已是最新版本 v" + UpdateCheck.RunningVersion;
        }
    }
}

internal static class UpdateCheck
{
    internal const string RepoSlug    = "lockhhhhh/CLean";
    internal const string ApiUrl      = "https://api.github.com/repos/" + RepoSlug + "/releases/latest";
    internal const string ReleasesUrl = "https://github.com/" + RepoSlug + "/releases/latest";
    internal const int    CacheHours  = 24;
    internal const int    TimeoutMs   = 5000;

    // Set by each shell at startup so Display() can name the running build.
    internal static string RunningVersion = "0.0.0";

    // ---------------------------------------------------------------- public

    // Whatever we already know without touching the network. Returns null when
    // there is no fresh cache entry.
    internal static UpdateInfo FromCache()
    {
        try
        {
            string path = CachePath();
            if (!File.Exists(path)) return null;

            string latest = null, name = null, asset = null;
            long ts = 0;
            using (StreamReader r = new StreamReader(path, Encoding.UTF8))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    if (k == "ts") ts = ParseLong(v);
                    else if (k == "latest") latest = v;
                    else if (k == "name") name = v;
                    else if (k == "asset") asset = v;
                }
            }
            if (String.IsNullOrEmpty(latest)) return null;

            double ageHours = (UnixNow() - ts) / 3600.0;
            if (ageHours < 0 || ageHours > CacheHours) return null;

            UpdateInfo info = new UpdateInfo();
            info.Latest = latest;
            info.ReleaseName = name;
            info.AssetUrl = asset;
            info.IsNewer = IsNewer(latest, RunningVersion);
            info.FromCache = true;
            return info;
        }
        catch
        {
            return null;   // an unreadable cache is simply "no cache"
        }
    }

    // force == false uses a fresh cache entry when there is one.
    internal static UpdateInfo Check(bool force)
    {
        if (!force)
        {
            UpdateInfo cached = FromCache();
            if (cached != null) return cached;
        }
        return Fetch();
    }

    // Always goes to the network (unless the network cannot be reached, in which
    // case a stale cache entry still beats saying nothing).
    internal static UpdateInfo Fetch()
    {
        UpdateInfo info = new UpdateInfo();
        try
        {
            EnableTls12();

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ApiUrl);
            // GitHub rejects requests without a User-Agent (HTTP 403).
            req.UserAgent = "CLean/" + RunningVersion + " (+" + ReleasesUrl + ")";
            req.Accept = "application/vnd.github+json";
            req.Method = "GET";
            req.Timeout = TimeoutMs;
            req.ReadWriteTimeout = TimeoutMs;
            req.AllowAutoRedirect = true;
            req.AutomaticDecompression = DecompressionMethods.GZip;
            req.Proxy = WebRequest.DefaultWebProxy;   // honour the machine's proxy

            string body;
            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                {
                    body = r.ReadToEnd();
                }
            }
            catch (WebException wex)
            {
                HttpWebResponse err = wex.Response as HttpWebResponse;
                if (err != null && (int)err.StatusCode == 404)
                {
                    // No release has ever been published. That is not a failure.
                    info.NoRelease = true;
                    return info;
                }
                info.Error = Describe(wex);
                return info;
            }

            string tag = FirstGroup(body, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            if (String.IsNullOrEmpty(tag))
            {
                info.Error = "返回内容里没有 tag_name";
                return info;
            }
            info.Latest = TrimVersion(tag);
            info.ReleaseName = FirstGroup(body, "\"name\"\\s*:\\s*\"([^\"]*)\"");
            info.AssetUrl = FirstGroup(body, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"");
            info.IsNewer = IsNewer(info.Latest, RunningVersion);

            WriteCache(info);
            return info;
        }
        catch (Exception ex)
        {
            info.Error = Describe(ex);
            return info;
        }
    }

    // ----------------------------------------------------------------- helpers

    internal static string CachePath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CLean");
        return Path.Combine(dir, "update.txt");
    }

    // Removes a leading "v"/"V" and anything from the first "-" on, so both
    // "v1.2.3" and "1.2.3-beta.1" compare as 1.2.3.
    internal static string TrimVersion(string s)
    {
        if (s == null) return "";
        s = s.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s.Substring(1);
        int dash = s.IndexOf('-');
        if (dash >= 0) s = s.Substring(0, dash);
        return s.Trim();
    }

    // -1 / 0 / 1, comparing dot-separated numeric parts. Non-numeric parts are
    // treated as 0, so a malformed tag can never look newer by accident.
    internal static int CompareVersions(string a, string b)
    {
        string[] pa = TrimVersion(a).Split('.');
        string[] pb = TrimVersion(b).Split('.');
        int n = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < n; i++)
        {
            int va = i < pa.Length ? ParseInt(pa[i]) : 0;
            int vb = i < pb.Length ? ParseInt(pb[i]) : 0;
            if (va != vb) return va < vb ? -1 : 1;
        }
        return 0;
    }

    internal static bool IsNewer(string latest, string current)
    {
        if (String.IsNullOrEmpty(latest)) return false;
        return CompareVersions(latest, current) > 0;
    }

    // Touching this file is best-effort: a read-only profile must not break the
    // check, it only means we ask again next launch.
    private static void WriteCache(UpdateInfo info)
    {
        try
        {
            string path = CachePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            StringBuilder sb = new StringBuilder();
            sb.Append("ts=").Append(UnixNow()).Append('\n');
            sb.Append("latest=").Append(Safe(info.Latest)).Append('\n');
            sb.Append("name=").Append(Safe(info.ReleaseName)).Append('\n');
            sb.Append("asset=").Append(Safe(info.AssetUrl)).Append('\n');
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    private static string Safe(string s)
    {
        if (String.IsNullOrEmpty(s)) return "";
        return s.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static void EnableTls12()
    {
        try
        {
            // 3072 == SecurityProtocolType.Tls12. The literal is used so this
            // still compiles against a machine that only has .NET 4.0's
            // reference assemblies, where the enum member does not exist.
            ServicePointManager.SecurityProtocol =
                ServicePointManager.SecurityProtocol | (SecurityProtocolType)3072;
        }
        catch { }
    }

    private static string FirstGroup(string body, string pattern)
    {
        Match m = Regex.Match(body, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static long UnixNow()
    {
        return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }

    private static long ParseLong(string s)
    {
        long v;
        return Int64.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
    }

    private static int ParseInt(string s)
    {
        int v;
        return Int32.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
    }

    // Short, human-readable cause. Full exception text is for the log window.
    private static string Describe(Exception ex)
    {
        WebException wex = ex as WebException;
        if (wex != null)
        {
            if (wex.Status == WebExceptionStatus.Timeout) return "网络超时";
            if (wex.Status == WebExceptionStatus.NameResolutionFailure) return "无法解析 api.github.com";
            if (wex.Status == WebExceptionStatus.ConnectFailure) return "无法连接 api.github.com";
            if (wex.Status == WebExceptionStatus.TrustFailure) return "TLS 证书校验失败";
            HttpWebResponse r = wex.Response as HttpWebResponse;
            if (r != null) return "HTTP " + (int)r.StatusCode;
        }
        return ex.Message;
    }
}
