// CLean · 窗口版 —— 动效令牌与补间引擎.
//
// WHY THIS FILE EXISTS: before it, every "movement" in the GUI was a scattered magic
// number (33ms here, 28ms there, 7px per tick) with no shared curves and no respect for
// the system "show animations" switch. Every new animation invented its own values, so the
// program never felt like one thing. This is the one place durations, curves and the
// reduced-motion rule live; see plans/README.md before adding another one.
//
// Deliberately conservative C# 5 syntax (in-box csc on older Windows builds): no ?.,
// no nameof, no string interpolation, no expression-bodied members. Like every other
// source here it sits in the global namespace and is UTF-8 without BOM.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

/// <summary>Which curve a channel is driven by. Values come from the motion audit.</summary>
internal enum Ease
{
    /// <summary>cubic-bezier(0.23, 1, 0.32, 1) - entering and leaving, and the default.</summary>
    Out,
    /// <summary>cubic-bezier(0.77, 0, 0.175, 1) - something travelling across the screen.</summary>
    InOut,
    /// <summary>cubic-bezier(0.34, 1.56, 0.64, 1) - a light overshoot, for press and success only.</summary>
    OutBack,
    /// <summary>A straight line - marquees and progress, never UI state changes.</summary>
    Linear
}

/// <summary>Durations, curves and the reduced-motion rule. Nothing here allocates.</summary>
internal static class Motion
{
    // Durations, in milliseconds. UI stays under 300ms; DurData is the one documented
    // exception, and only because a disk-usage bar is a data visualisation, not feedback.
    internal const int DurPress = 110;   // press feedback (100-160ms band)
    internal const int DurHover = 140;   // hover and colour changes
    internal const int DurToggle = 170;  // check and select
    internal const int DurRail = 190;    // the rail travelling across the screen
    internal const int DurPanel = 210;   // result cards, confirm strip
    internal const int DurPage = 200;    // page cross-fade
    internal const int DurData = 420;    // data bars re-scaling

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    internal static readonly bool OsAnimations;

    static Motion()
    {
        // SPI_GETCLIENTAREAANIMATION is what the Windows 10/11 "Show animations in
        // Windows" toggle writes. .NET does not expose it, so ask directly; if the call
        // fails (older Windows) fall back to the SystemInformation proxy.
        int enabled = 0;
        bool ok = false;
        try { ok = SystemParametersInfo(0x1042, 0, ref enabled, 0); }
        catch (Exception) { ok = false; }
        OsAnimations = ok ? (enabled != 0) : SystemInformation.UIEffectsEnabled;
    }

    /// <summary>Monotonic milliseconds. Stopwatch because Environment.TickCount wraps.</summary>
    internal static double Now
    {
        get { return Clock.Elapsed.TotalMilliseconds; }
    }

    private static bool _forceReduced;
    private static bool _forceFull;

    /// <summary>Test hook: forces the reduced-motion path regardless of the OS setting.</summary>
    internal static bool ForceReduced
    {
        get { return _forceReduced; }
        set { _forceReduced = value; }
    }

    /// <summary>
    /// Diagnostic hook: ignores the OS setting and animates anyway. Needed on machines where
    /// "Show animations in Windows" is off - there the reduced path is the only one that can
    /// otherwise be observed, so neither the visuals nor the frame timings could be checked.
    /// </summary>
    internal static bool ForceFull
    {
        get { return _forceFull; }
        set { _forceFull = value; }
    }

    // "auto" follows Windows; "on" and "off" are the appearance menu's override. Windows
    // ships machines with "Show animations in Windows" already off (a de-bloat script or a
    // performance tweak is enough), and on such a machine every movement here would snap
    // with no way for the user to tell why - so the menu can say "animate anyway", and the
    // reverse is there too for anyone who wants the reduced behaviour with Windows animating.
    internal static string Preference = "auto";

    internal static void LoadPreference()
    {
        Preference = Settings.MotionPreference;
    }

    internal static void SetPreference(string value)
    {
        Preference = (value == "on" || value == "off") ? value : "auto";
        Settings.MotionPreference = Preference;
    }

    /// <summary>The menu caption for one preference value, Windows state included.</summary>
    internal static string Label(string value)
    {
        if (value == "on") return "始终开启";
        if (value == "off") return "始终关闭";
        return "跟随系统（当前" + (OsAnimations ? "开启" : "关闭") + "）";
    }

    /// <summary>
    /// Reduced motion means gentler, not zero: movement is dropped entirely, while colour
    /// and fades stay (shorter) because they explain what changed.
    /// </summary>
    internal static bool Reduced
    {
        get
        {
            if (Preference == "off") return true;
            if (Preference == "on") return false;
            return ForceReduced || (!ForceFull && !OsAnimations);
        }
    }

    /// <summary>Duration for a movement channel: snaps under reduced motion.</summary>
    internal static int Dur(int ms)
    {
        return Reduced ? 0 : ms;
    }

    /// <summary>Duration for a colour or fade channel: kept, but shortened.</summary>
    internal static int Soft(int ms)
    {
        return Reduced ? Math.Min(ms, 90) : ms;
    }

    internal static double Clamp01(double t)
    {
        if (t < 0.0) return 0.0;
        if (t > 1.0) return 1.0;
        return t;
    }

    internal static double Apply(Ease ease, double t)
    {
        t = Clamp01(t);
        switch (ease)
        {
            case Ease.InOut: return Solve(t, 0.77, 0.0, 0.175, 1.0);
            case Ease.OutBack: return Solve(t, 0.34, 1.56, 0.64, 1.0);
            case Ease.Linear: return t;
            default: return Solve(t, 0.23, 1.0, 0.32, 1.0);
        }
    }

    /// <summary>Colour at <paramref name="t"/> between two colours. Forwards to Theme.Mix.</summary>
    internal static Color Blend(Color a, Color b, double t)
    {
        return Theme.Mix(a, b, (float)Clamp01(t));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);

    // ---- cubic-bezier solving -------------------------------------------------
    // Real solving, not a 1-pow(1-t,3) stand-in: the audit's curves were chosen for their
    // shape, and a different shape is a different animation.

    private static double Solve(double t, double x1, double y1, double x2, double y2)
    {
        if (t <= 0.0) return 0.0;
        if (t >= 1.0) return 1.0;

        // Newton first - it is exact for these curves in a handful of steps.
        double u = t;
        for (int i = 0; i < 8; i++)
        {
            double x = Curve(u, x1, x2) - t;
            if (Math.Abs(x) < 1e-6) return Curve(u, y1, y2);
            double d = Slope(u, x1, x2);
            if (Math.Abs(d) < 1e-9) break;
            double next = u - x / d;
            if (next < 0.0 || next > 1.0) break;
            u = next;
        }

        // Bisection fallback - cannot fail, only slower.
        double lo = 0.0, hi = 1.0;
        u = t;
        for (int i = 0; i < 30; i++)
        {
            double x = Curve(u, x1, x2) - t;
            if (Math.Abs(x) < 1e-7) break;
            if (x > 0.0) hi = u; else lo = u;
            u = (lo + hi) / 2.0;
        }
        return Curve(u, y1, y2);
    }

    /// <summary>One axis of cubic-bezier(p1, p2) with implicit endpoints 0 and 1.</summary>
    private static double Curve(double u, double a1, double a2)
    {
        double v = 1.0 - u;
        return 3.0 * v * v * u * a1 + 3.0 * v * u * u * a2 + u * u * u;
    }

    private static double Slope(double u, double a1, double a2)
    {
        double v = 1.0 - u;
        return 3.0 * a1 * v * (1.0 - 3.0 * u) + 3.0 * a2 * u * (2.0 - 3.0 * u) + 3.0 * u * u;
    }
}

/// <summary>
/// One animated 0..1 value. To() starts from the value on screen right now, so a channel
/// can be re-targeted or reversed mid-flight instead of replaying from zero - the
/// difference between a transition and a keyframe.
/// </summary>
internal sealed class Channel
{
    private double _value;
    private double _from;
    private double _to;
    private double _start;
    private int _duration;
    private Ease _ease;
    private bool _running;

    internal double Value
    {
        get { return _value; }
    }

    internal bool Running
    {
        get { return _running; }
    }

    /// <summary>Jump to a value with no animation (initial state, reduced motion).</summary>
    internal void SnapTo(double value)
    {
        _value = value;
        _from = value;
        _to = value;
        _running = false;
    }

    internal void To(double target, int milliseconds, Ease ease)
    {
        if (milliseconds <= 0 || Math.Abs(target - _value) < 0.0005)
        {
            SnapTo(target);
            return;
        }
        _from = _value;                 // <- the retarget: current position, not the old start
        _to = target;
        _start = Motion.Now;
        _duration = milliseconds;
        _ease = ease;
        _running = true;
    }

    /// <summary>
    /// Advance to the current time. Returns true while still travelling; the frame that
    /// returns false has already landed exactly on the target.
    /// </summary>
    internal bool Advance()
    {
        if (!_running) return false;
        double t = (_duration <= 0) ? 1.0 : (Motion.Now - _start) / _duration;
        if (t >= 1.0)
        {
            _value = _to;
            _running = false;
            return false;
        }
        _value = _from + (_to - _from) * Motion.Apply(_ease, t);
        return true;
    }
}

/// <summary>
/// Drives a control's channels on a 16ms timer: runs only while something is actually
/// moving, so an idle window pays nothing.
/// </summary>
internal sealed class MotionDriver : IDisposable
{
    private readonly Control _owner;
    private readonly Channel[] _channels;
    private readonly Timer _timer;
    private bool _disposed;

    /// <summary>Raised after every step, with the owner already invalidated.</summary>
    internal event EventHandler Tick;

    internal MotionDriver(Control owner, int channels)
    {
        if (channels < 1) channels = 1;
        _owner = owner;
        _channels = new Channel[channels];
        for (int i = 0; i < channels; i++) _channels[i] = new Channel();

        _timer = new Timer();
        _timer.Interval = 16;
        _timer.Tick += delegate { Step(); };
    }

    internal Channel Ch(int index)
    {
        if (index < 0 || index >= _channels.Length) return _channels[0];
        return _channels[index];
    }

    /// <summary>True while the timer is running. Idle means false.</summary>
    internal bool Busy
    {
        get { return _timer.Enabled; }
    }

    /// <summary>Animate one channel to a target. The caller picks the reduced-motion rule.</summary>
    internal void To(int index, double target, int milliseconds, Ease ease)
    {
        if (index < 0 || index >= _channels.Length) return;
        _channels[index].To(target, milliseconds, ease);
        if (_channels[index].Running) _timer.Start();
        Step();   // reflect the new (possibly snapped) value immediately
    }

    /// <summary>
    /// A colour/fade channel: kept under reduced motion, just shorter - these explain what
    /// changed, so removing them loses information instead of sparing the user motion.
    /// </summary>
    internal void Feedback(int index, double target, int milliseconds, Ease ease)
    {
        To(index, target, Motion.Soft(milliseconds), ease);
    }

    /// <summary>A movement channel: snaps under reduced motion.</summary>
    internal void Move(int index, double target, int milliseconds, Ease ease)
    {
        To(index, target, Motion.Dur(milliseconds), ease);
    }

    internal void Snap(int index, double value)
    {
        if (index < 0 || index >= _channels.Length) return;
        _channels[index].SnapTo(value);
        Step();
    }

    private void Step()
    {
        if (_disposed) return;
        bool running = false;
        for (int i = 0; i < _channels.Length; i++)
        {
            if (_channels[i].Advance()) running = true;
        }
        EventHandler handler = Tick;
        if (handler != null) handler(this, EventArgs.Empty);
        if (_owner != null && !_owner.IsDisposed) _owner.Invalidate();
        if (!running) _timer.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
    }
}
