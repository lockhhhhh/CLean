// CLean · 窗口版 -- 每个功能自己的动画 + 无日志的结果卡。
//
// WHY THIS FILE (v0.5): 每个页面原来显示的是脚本的原始输出（尾行窗），现在全部去掉：
// 页面在运行时显示该功能**自己的动画**，结束后显示一张结果卡（状态 / 用时 / 计数 /
// 关键结论），原始输出只留在右上角「输出日志」那个独立窗口里（需要时才看）。
//
// 十个动画都从 AnimBase 派生，只实现 DrawMotif：动画始终在跑（空闲时慢、运行时亮），
// 所以每个页面一眼就能看出这个功能在做什么。两个扫描页的动画还直接吃实时扫描数据
// （大目录页＝实时柱状图，隐藏缓存页＝实时瓷贴），扫描过程中就能看到成果一个个出现。
//
// ASCII-only? No: this file carries Chinese strings like the rest of the GUI sources, and
// csc is invoked with /codepage:65001, so UTF-8 without BOM is correct here.
// Deliberately conservative C# 5 syntax (in-box csc on older Windows builds).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

// ============================================================== animation base

internal abstract class AnimBase : Control, IThemed
{
    private readonly Timer _tick = new Timer();

    internal string Caption = "就绪";
    internal string Detail = "";
    internal bool Running;
    internal int Frame;

    internal AnimBase()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 92;
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
        _tick.Interval = 33;
        _tick.Tick += delegate { Frame++; Invalidate(); };
    }

    // Only the page on screen animates: ten pages each keeping a 30 fps timer awake would
    // be pure waste (and every page is built up front).
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) _tick.Start();
        else _tick.Stop();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _tick.Stop();
        base.OnHandleDestroyed(e);
    }

    internal void SetIdle(string caption, string detail)
    {
        Running = false;
        Caption = caption;
        Detail = detail;
        Publish();
        Invalidate();
    }

    internal void BeginRun(string caption)
    {
        Running = true;
        Caption = caption;
        Detail = "正在运行…";
        Publish();
        Invalidate();
    }

    internal void SetDetail(string detail)
    {
        Detail = detail;
        Publish();
        Invalidate();
    }

    internal void EndRun(string caption, string detail)
    {
        Running = false;
        Caption = caption;
        Detail = detail;
        Publish();
        Invalidate();
    }

    // The panel is entirely custom-drawn, so its visible text is also put into the
    // control's window text: that is what accessibility (and UI automation) reads, and it
    // is the only way a screen reader can see the status at all.
    private void Publish()
    {
        Text = Detail.Length > 0 ? (Caption + " · " + Detail) : Caption;
    }

    // Scan pages push live hits in; motifs that draw found items override this.
    internal virtual void FeedLive(List<ScanHit> hits) { }

    public void ApplyPalette()
    {
        Invalidate();
    }

    protected static Color Fade(Color c, int alpha)
    {
        return Color.FromArgb(alpha, c);
    }

    protected static void Dot(Graphics g, int x, int y, int d, Color c)
    {
        if (d < 1) return;
        using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, x, y, d, d);
    }

    protected static void Sq(Graphics g, Rectangle r, int radius, Color c)
    {
        Theme.FillRound(g, r, radius, c);
    }

    // The square the motif is drawn in, so all ten animations share one geometry.
    protected abstract void DrawMotif(Graphics g, Rectangle box, int frame, bool running);

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Card);

        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.FillRound(g, r, 10, Theme.ConsoleBg);

        Rectangle box = new Rectangle(14, (Height - 68) / 2, 68, 68);
        DrawMotif(g, box, Frame, Running);

        int textX = box.Right + 18;
        int textW = Width - textX - 16;
        if (textW < 60) textW = 60;

        // status dot: accent while working, green when finished, faint when idle
        Color dot = Running ? Theme.Accent : (Frame >= 0 && Detail.Length > 0 ? Theme.Success : Theme.TextFaint);
        Dot(g, textX, (Height / 2) - 22, 8, Running ? Fade(dot, 240) : Fade(dot, 180));

        TextRenderer.DrawText(g, Caption, Theme.FontUIB,
            new Rectangle(textX + 16, (Height / 2) - 30, textW - 16, 22),
            Running ? Theme.ConsoleFg : Fade(Theme.ConsoleFg, 200), Theme.LeftFlags);

        if (Detail.Length > 0)
        {
            TextRenderer.DrawText(g, Detail, Theme.FontSmall,
                new Rectangle(textX + 16, (Height / 2) - 6, textW - 16, 18),
                Theme.ConsoleMuted, Theme.LeftFlags);
        }

        if (Running)
        {
            int w = Math.Max(40, Width / 4);
            int x = (Frame * Math.Max(1, (Width + w) / 160)) % (Width + w) - w;
            if (x < 0) x = 0;
            Rectangle bar = new Rectangle(x, Height - 4, Math.Min(w, Width - x), 3);
            if (bar.Width > 2) Theme.FillRound(g, bar, 1, Theme.Accent);
        }
    }
}

// ==================================================== 1. 空间分析：磁盘环 + 柱图

internal sealed class AnalyzeAnim : AnimBase
{
    protected override void DrawMotif(Graphics g, Rectangle box, int frame, bool running)
    {
        int cx = box.X + 34;
        int cy = box.Y + 28;
        int d = 42;
        using (Pen p = new Pen(Fade(Theme.ConsoleFg, 55), 6f))
            g.DrawEllipse(p, cx - d / 2, cy - d / 2, d, d);

        int sweep = 50 + (frame * 2) % 260;
        using (Pen p = new Pen(Fade(Theme.Accent, running ? 255 : 170), 6f))
        {
            p.StartCap = LineCap.Round;
            g.DrawArc(p, cx - d / 2, cy - d / 2, d, d, -90, sweep);
        }
        double rad = (-90 + sweep) * Math.PI / 180.0;
        using (Pen p = new Pen(Fade(Theme.Accent, 220), 1.6f))
            g.DrawLine(p, cx, cy, cx + (int)(Math.Cos(rad) * 17), cy + (int)(Math.Sin(rad) * 17));

        for (int i = 0; i < 4; i++)
        {
            int h = 5 + ((frame / 3 + i * 5) % 13);
            Sq(g, new Rectangle(box.X + 5 + i * 10, box.Bottom - 6 - h, 7, h), 2, Fade(Theme.Accent, 130));
        }
    }
}

// ============================================ 2. 系统缓存清理：扫掠擦除 +

internal sealed class CacheCleanAnim : AnimBase
{
    protected override void DrawMotif(Graphics g, Rectangle box, int frame, bool running)
    {
        const int cols = 6, rows = 5, gap = 3;
        int cw = (box.Width - 10 - (cols - 1) * gap) / cols;
        int ch = (box.Height - 10 - (rows - 1) * gap) / rows;
        int sweepX = box.X + 5 + (frame * 2) % Math.Max(1, box.Width - 10);

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                Rectangle cell = new Rectangle(box.X + 5 + x * (cw + gap), box.Y + 5 + y * (ch + gap), cw, ch);
                bool cleaned = cell.Right < sweepX;
                if (cleaned) Sq(g, cell, 2, Fade(Theme.ConsoleBd, 170));
                else Sq(g, cell, 2, Fade(Theme.Accent, 90 + ((x * rows + y + frame / 8) % 3) * 45));
            }
        }
        using (Pen p = new Pen(Fade(Theme.Accent, running ? 255 : 190), 2f))
            g.DrawLine(p, sweepX, box.Y + 3, sweepX, box.Bottom - 3);
    }
}

// ========================================== 3. 无用缓存发现：放大镜巡格子

internal sealed class DiscoverAnim : AnimBase
{
    protected override void DrawMotif(Graphics g, Rectangle box, int frame, bool running)
    {
        const int grid = 6, pitch = 10;
        double t = frame * 0.045;
        int lx = box.X + 26 + (int)(Math.Sin(t) * 14);
        int ly = box.Y + 26 + (int)(Math.Cos(t * 1.3) * 12);

        for (int y = 0; y < grid; y++)
        {
            for (int x = 0; x < grid; x++)
            {
                int px = box.X + 8 + x * pitch;
                int py = box.Y + 8 + y * pitch;
                double dist = Math.Sqrt((px - lx) * (px - lx) + (py - ly) * (py - ly));
                bool inside = dist < 15.0;
                int size = inside ? 5 : 3;
                Dot(g, px - size / 2, py - size / 2, size,
                    inside ? Fade(Theme.Accent, 240) : Fade(Theme.ConsoleFg, 70));
            }
        }
        using (Pen p = new Pen(Fade(Theme.Accent, running ? 255 : 200), 2.4f))
            g.DrawEllipse(p, lx - 12, ly - 12, 24, 24);
        using (Pen p = new Pen(Fade(Theme.Accent, 220), 3f))
            g.DrawLine(p, lx + 9, ly + 9, lx + 18, ly + 18);
    }
}

// =========================================== 4. 清单/列表：纸张逐行打印出来

internal sealed class CatalogAnim : AnimBase
{
    protected override void DrawMotif(Graphics g, Rectangle box, int frame, bool running)
    {
        Rectangle page = new Rectangle(box.X + 13, box.Y + 4, box.Width - 26, box.Height - 8);
        Sq(g, page, 4, Fade(Theme.ConsoleBd, 210));

        const int lines = 6;
        int reveal = (frame / 7) % (lines + 6);
        for (int i = 0; i < lines && i < reveal; i++)
        {
            int w = page.Width - 12 - ((i % 3) * 8);
            bool current = (i == reveal - 1);
            Sq(g, new Rectangle(page.X + 6, page.Y + 6 + i * 9, Math.Max(6, w), 4), 1,
                Fade(Theme.Accent, current ? 240 : 120));
        }
        if (reveal < lines)
        {
            int y = page.Y + 6 + reveal * 9;
            Sq(g, new Rectangle(page.X + 6, y, 5, 4), 1, Fade(Theme.Accent, 255));
        }
    }
}

// ====================================== 5. 显卡着色器缓存：旋转线框 + 顶点脉冲

internal sealed class GpuAnim : AnimBase
{
    protected override void DrawMotif(Graphics g, Rectangle box, int frame, bool running)
    {
        int cx = box.X + 34;
        int cy = box.Y + 32;
        double rot = frame * 0.045;
        int r = 24;

        Point[] pts = new Point[3];
        for (int i = 0; i < 3; i++)
        {
            double a = rot + i * 2.0 * Math.PI / 3.0;
            pts[i] = new Point(cx + (int)(Math.Cos(a) * r), cy + (int)(Math.Sin(a) * r * 0.85));
        }

        using (Pen p = new Pen(Fade(Theme.Accent, running ? 240 : 170), 1.8f))
            g.DrawPolygon(p, pts);
        using (Pen p = new Pen(Fade(Theme.ConsoleFg, 60), 1f))
            g.DrawEllipse(p, cx - 11, cy - 9, 22, 18);

        for (int i = 0; i < 3; i++)
        {
            int pulse = 4 + (int)(2.5 * (1 + Math.Sin(frame * 0.12 + i)));
            Dot(g, pts[i].X - pulse / 2, pts[i].Y - pulse / 2, pulse, Fade(Theme.Accent, 235));
        }
        Sq(g, new Rectangle(box.X + 6, box.Bottom - 10, 10, 5), 1, Fade(Theme.Success, 180));
    }
}

// ========================================= 6. 根目录残留：枝叶逐个被剪掉

internal sealed class RootsAnim : AnimBase
{
    protected override void DrawMotif(Graphics g, Rectangle box, int frame, bool running)
    {
        int rootX = box.X + 34;
        int rootY = box.Y + 9;
        Dot(g, rootX - 4, rootY - 4, 9, Fade(Theme.Accent, 240));

        int[] xs = { box.X + 13, box.X + 34, box.X + 55 };
        using (Pen p = new Pen(Fade(Theme.ConsoleFg, 80), 1f))
            g.DrawLine(p, box.X + 9, box.Y + 36, box.Right - 10, box.Y + 36);

        for (int i = 0; i < 3; i++)
        {
            using (Pen p = new Pen(Fade(Theme.ConsoleFg, 110), 1.2f))
                g.DrawLine(p, rootX, rootY + 5, xs[i], box.Y + 34);

            int phase = (frame + i * 22) % 66;
            int y = box.Y + 38 + phase / 2;
            int alpha = Math.Max(0, 230 - phase * 4);
            Sq(g, new Rectangle(xs[i] - 5, Math.Min(y, box.Bottom - 9), 10, 8), 2, Fade(Theme.Warn, alpha));
        }
    }
}

// ====================================== 7. 开发环境缓存：包裹堆叠 + 清理箭头

internal sealed class DevAnim : AnimBase
{
    protected override void DrawMotif(Graphics g, Rectangle box, int frame, bool running)
    {
        int bob = (int)(Math.Sin(frame * 0.07) * 2);
        for (int i = 0; i < 3; i++)
        {
            Rectangle face = new Rectangle(box.X + 14 + i * 3, box.Bottom - 18 - i * 14 + bob, 34, 11);
            Sq(g, face, 3, Fade(Theme.Accent, 110 + i * 50));
            using (Pen p = new Pen(Fade(Theme.Accent, 230), 1.2f))
                g.DrawRectangle(p, face);
        }

        int ay = box.Y + 8 + (frame % 28) / 2;
        using (Pen p = new Pen(Fade(Theme.Success, running ? 240 : 180), 2f))
        {
            g.DrawLine(p, box.Right - 15, ay, box.Right - 15, ay + 9);
            g.DrawLine(p, box.Right - 19, ay + 5, box.Right - 15, ay + 9);
            g.DrawLine(p, box.Right - 11, ay + 5, box.Right - 15, ay + 9);
        }
    }
}

// ============================== 8. 全盘大目录：实时柱状图（吃扫描结果）

internal sealed class LargeDirsAnim : AnimBase
{
    private readonly List<ScanHit> _live = new List<ScanHit>();

    internal override void FeedLive(List<ScanHit> hits)
    {
        _live.Clear();
        if (hits != null)
        {
            for (int i = 0; i < hits.Count && i < 7; i++) _live.Add(hits[i]);
        }
        Invalidate();
    }

    protected override void DrawMotif(Graphics g, Rectangle box, int frame, bool running)
    {
        using (Pen p = new Pen(Fade(Theme.ConsoleFg, 55), 1f))
            g.DrawLine(p, box.X + 4, box.Bottom - 6, box.Right - 4, box.Bottom - 6);

        int count = _live.Count > 0 ? _live.Count : 6;
        long max = 1;
        for (int i = 0; i < _live.Count; i++) if (_live[i].Bytes > max) max = _live[i].Bytes;

        int slot = Math.Max(6, (box.Width - 12) / Math.Max(1, count));
        int barW = Math.Max(4, slot - 2);
        for (int i = 0; i < count; i++)
        {
            double frac;
            if (_live.Count > 0) frac = (double)_live[i].Bytes / max;
            else frac = 0.22 + ((i * 7 + frame / 4) % 10) / 13.0;   // idle breathing bars

            int h = (int)(frac * (box.Height - 18));
            if (h < 3) h = 3;
            Color c = i == 0
                ? Fade(Theme.Accent, running ? 255 : 200)
                : Fade(Theme.Accent, 200 - i * 22);
            Sq(g, new Rectangle(box.X + 5 + i * slot, box.Bottom - 6 - h, barW, h), 2, c);
        }

        if (running)
        {
            int x = box.X + 4 + (frame * 3) % Math.Max(1, box.Width - 8);
            using (Pen p = new Pen(Fade(Theme.ConsoleFg, 120), 1f))
                g.DrawLine(p, x, box.Y + 4, x, box.Bottom - 6);
        }
    }
}

// ============================== 9. 隐藏项目缓存：实时瓷贴（吃扫描结果）

internal sealed class CacheTilesAnim : AnimBase
{
    private readonly List<string> _paths = new List<string>();
    private readonly List<long> _sizes = new List<long>();
    private readonly Dictionary<string, int> _born = new Dictionary<string, int>();

    internal override void FeedLive(List<ScanHit> hits)
    {
        if (hits == null) return;
        for (int i = 0; i < hits.Count && i < 9; i++)
        {
            string p = hits[i].Path;
            if (_born.ContainsKey(p)) continue;
            _born[p] = Frame;
            _paths.Add(p);
            _sizes.Add(hits[i].Bytes);
        }
        Invalidate();
    }

    internal void ClearTiles()
    {
        _paths.Clear();
        _sizes.Clear();
        _born.Clear();
        Invalidate();
    }

    protected override void DrawMotif(Graphics g, Rectangle box, int frame, bool running)
    {
        const int cols = 3, rows = 3, gap = 3;
        int tw = (box.Width - 8 - (cols - 1) * gap) / cols;
        int th = (box.Height - 8 - (rows - 1) * gap) / rows;

        long max = 1;
        for (int i = 0; i < _sizes.Count; i++) if (_sizes[i] > max) max = _sizes[i];

        for (int i = 0; i < cols * rows; i++)
        {
            Rectangle cell = new Rectangle(
                box.X + 4 + (i % cols) * (tw + gap),
                box.Y + 4 + (i / cols) * (th + gap), tw, th);

            if (i >= _paths.Count)
            {
                // empty slot
                Theme.DrawRound(g, cell, 3, Fade(Theme.ConsoleFg, 45), 1f);
                continue;
            }

            int age = frame - _born[_paths[i]];
            if (age < 0) age = 0;
            double grow = Math.Min(1.0, age / 8.0);
            int inset = (int)((1.0 - grow) * th / 2);
            Rectangle tile = new Rectangle(cell.X, cell.Y + inset, cell.Width, Math.Max(3, cell.Height - inset * 2));
            Sq(g, tile, 3, Fade(Theme.Accent, 60 + (i % 3) * 40));

            // mini size bar inside the tile
            int w = (int)((tile.Width - 6) * ((double)_sizes[i] / max));
            if (w < 2) w = 2;
            Sq(g, new Rectangle(tile.X + 3, tile.Bottom - 4, w, 2), 1, Fade(Theme.Accent, 235));
        }
    }
}

// ================================== 10. 空间反升诊断：波形 + 卷影区柱体

internal sealed class DiagnoseAnim : AnimBase
{
    protected override void DrawMotif(Graphics g, Rectangle box, int frame, bool running)
    {
        int h = 10 + (frame / 2) % 42;
        Sq(g, new Rectangle(box.X + 6, box.Bottom - 8 - h, 11, h), 2, Fade(Theme.Warn, 175));

        int n = box.Width - 30;
        Point[] pts = new Point[n];
        for (int i = 0; i < n; i++)
        {
            double x = frame * 0.10 + i * 0.36;
            double v = Math.Sin(x) * 0.6 + Math.Sin(x * 2.3) * 0.35;
            pts[i] = new Point(box.X + 24 + i, box.Y + 34 - (int)(v * 15));
        }
        using (Pen p = new Pen(Fade(Theme.Accent, running ? 240 : 180), 1.6f))
            g.DrawLines(p, pts);
        using (Pen p = new Pen(Fade(Theme.ConsoleFg, 60), 1f))
            g.DrawLine(p, box.X + 22, box.Y + 34, box.Right - 5, box.Y + 34);
    }
}

// ============================================================= result summary

// 无日志的结果卡：脚本类页面运行结束后显示它，而不是把原始输出铺在页面上。
// 数字来自 Log 的行前缀统计（[+] / [!] / [x]），关键结论取最后一条 [+]，因此
// 页面上看不到日志流，但"这次到底成没成、拿到了什么结论"一目了然。
internal class SummaryView : Control, IThemed
{
    internal string State = "尚未运行";
    internal string Meta = "";
    internal string Counts = "";
    internal string Key = "";
    internal string Hint = "原始输出在右上角「输出日志」窗口里，页面不再铺日志。";
    internal Color Tone = Theme.TextFaint;

    internal SummaryView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
    }

    internal void Reset(string hint)
    {
        State = "尚未运行";
        Meta = "";
        Counts = "";
        Key = "";
        if (hint != null) Hint = hint;
        Tone = Theme.TextFaint;
        Publish();
        Invalidate();
    }

    internal void ShowResult(string state, string meta, string counts, string key, Color tone)
    {
        State = state;
        Meta = meta;
        Counts = counts;
        Key = key;
        Tone = tone;
        Publish();
        Invalidate();
    }

    // Same reason as AnimBase.Publish: the card is drawn by hand, so its text has to be
    // published as window text for accessibility (and for anything automating the UI).
    private void Publish()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append(State);
        if (Meta.Length > 0) sb.Append(" · ").Append(Meta);
        if (Counts.Length > 0) sb.Append(" · ").Append(Counts);
        if (Key.Length > 0) sb.Append(" · ").Append(Key);
        Text = sb.ToString();
    }

    public void ApplyPalette()
    {
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Card);

        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.FillRound(g, r, 10, Theme.Hover);

        int x = 18;
        int w = Width - x - 18;
        if (w < 60) w = 60;

        Dot(g, x, 20, 10, Tone);
        TextRenderer.DrawText(g, State, Theme.FontAction,
            new Rectangle(x + 20, 14, w - 20, 28), Theme.Text, Theme.LeftFlags);

        int y = 48;
        if (Meta.Length > 0)
        {
            TextRenderer.DrawText(g, Meta, Theme.FontUI,
                new Rectangle(x, y, w, 20), Theme.TextMuted, Theme.LeftFlags);
            y += 24;
        }
        if (Counts.Length > 0)
        {
            TextRenderer.DrawText(g, Counts, Theme.FontUI,
                new Rectangle(x, y, w, 20), Theme.TextMuted, Theme.LeftFlags);
            y += 24;
        }
        if (Key.Length > 0)
        {
            TextRenderer.DrawText(g, Key, Theme.FontMono,
                new Rectangle(x, y, w, Math.Max(40, Height - y - 34)), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak |
                TextFormatFlags.EndEllipsis);
        }

        TextRenderer.DrawText(g, Hint, Theme.FontSmall,
            new Rectangle(x, Height - 26, w, 18), Theme.TextFaint, Theme.LeftFlags);
    }

    private static void Dot(Graphics g, int x, int y, int d, Color c)
    {
        using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, x, y, d, d);
    }
}
