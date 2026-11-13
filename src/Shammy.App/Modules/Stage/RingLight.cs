using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Shammy.Infrastructure;
using Shammy.Interop;

namespace Shammy.Modules.Stage;

/// <summary>
/// Stage's ring light: a warm white glow along the four edges of the screen the camera sits on (the notch's
/// screen), to light your face in a dark room. Four thin windows, one per edge, each a bright few-pixel line that
/// falls off softly towards the middle (plain gradients, nothing blurred live). They are click-through, never
/// activated, kept just under the notch so the script stays clear, and left out of screenshots, recordings and
/// screen shares, so the people on the call never see them. Made and moved on the UI thread only.
/// </summary>
internal sealed class RingLight : IDisposable
{
    /// <summary>Warm white, about 4000 K.</summary>
    static readonly Color Warm = Color.FromRgb(0xFF, 0xF1, 0xDE);

    static readonly TimeSpan KeepOrderEvery = TimeSpan.FromSeconds(15);

    readonly Dispatcher _ui;
    readonly Func<IntPtr> _monitor;
    readonly Func<IntPtr> _notch;
    readonly List<Edge> _edges = new();
    readonly DispatcherTimer _keepOrder;
    bool _listening;

    public RingLight(Dispatcher ui, Func<IntPtr> monitor, Func<IntPtr> notch)
    {
        _ui = ui;
        _monitor = monitor;
        _notch = notch;
        _keepOrder = new DispatcherTimer(KeepOrderEvery, DispatcherPriority.Background, (_, _) => KeepOrder(), ui);
        _keepOrder.Stop();
    }

    public bool IsOn => _edges.Count > 0;

    /// <summary>0.2–1.</summary>
    public double Strength { get; private set; } = Core.Stage.StageText.DefaultRing;

    public string Describe() => IsOn ? $"on strength={Strength:0.00} edges={_edges.Count} {string.Join(",", _edges.Select(e => e.Placed))}" : $"off strength={Strength:0.00}";

    public void Show(double strength)
    {
        Strength = Core.Stage.StageText.ClampRing(strength);
        if (IsOn)
        {
            Place();
            return;
        }
        try
        {
            foreach (var side in new[] { Side.Top, Side.Bottom, Side.Left, Side.Right }) _edges.Add(new Edge(side));
            foreach (var e in _edges) e.Window.Show();
            Place();
            foreach (var e in _edges) e.FadeIn();
        }
        catch (Exception ex)
        {
            Log.Warn("Stage: the ring light couldn’t be shown", ex);
            Hide();
            return;
        }
        if (!_listening)
        {
            SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
            _listening = true;
        }
        _keepOrder.Start();
    }

    public void Hide()
    {
        _keepOrder.Stop();
        if (_listening)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
            _listening = false;
        }
        foreach (var e in _edges)
        {
            try { e.Window.Close(); }
            catch (Exception ex) { Log.Warn("Stage: closing the ring light failed", ex); }
        }
        _edges.Clear();
    }

    public void Dispose() => Hide();

    // SystemEvents raises this on its own thread.
    void OnDisplaysChanged(object? sender, EventArgs e) => _ui.BeginInvoke(() =>
    {
        if (IsOn) Place();
    });

    /// <summary>Lays the four edges along the screen, sized for the strength, just under the notch.</summary>
    void Place()
    {
        var monitor = _monitor();
        var r = Native.MonitorBounds(monitor);
        if (Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) != 0 || dpi == 0) dpi = 96;
        double px = dpi / 96.0;
        // The glow reaches 36–146 px into the screen, its bright line 2–6 px, both growing with the strength.
        int depth = (int)Math.Round((36 + 110 * Strength) * px);
        int line = Math.Max(1, (int)Math.Round((2 + 4 * Strength) * px));
        depth = Math.Min(depth, Math.Min(r.Width, r.Height) / 3);
        foreach (var e in _edges)
        {
            var (x, y, w, h) = e.Side switch
            {
                Side.Top => (r.Left, r.Top, r.Width, depth),
                Side.Bottom => (r.Left, r.Bottom - depth, r.Width, depth),
                Side.Left => (r.Left, r.Top, depth, r.Height),
                _ => (r.Right - depth, r.Top, depth, r.Height),
            };
            e.Paint(Strength, line / (double)depth);
            e.Place(_notch(), x, y, w, h);
        }
    }

    /// <summary>Another topmost window may have come in between: the edges go back on top, under the notch.</summary>
    void KeepOrder()
    {
        var notch = _notch();
        foreach (var e in _edges) e.Restack(notch);
    }

    enum Side { Top, Bottom, Left, Right }

    sealed class Edge
    {
        const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;
        readonly Rectangle _glow = new() { IsHitTestVisible = false };
        IntPtr _hwnd;

        public Edge(Side side)
        {
            Side = side;
            Window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                Focusable = false,
                IsHitTestVisible = false,
                Title = "Shammy ring light",
                Width = 1,
                Height = 1,
                Left = -10000,
                Top = -10000,
                Content = _glow,
                Opacity = MotionPrefs.Lively ? 0 : 1,
            };
            Window.SourceInitialized += (_, _) =>
            {
                _hwnd = new WindowInteropHelper(Window).Handle;
                Native.SetExStyle(_hwnd, WS_EX_TRANSPARENT | WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
                // Lights the room, not the call: screenshots, recordings and shared screens never show it.
                Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
            };
        }

        public Side Side { get; }
        public Window Window { get; }
        public string Placed { get; private set; } = "-";

        /// <summary>A bright line at the screen's edge, then a soft fall-off to nothing.</summary>
        public void Paint(double strength, double lineShare)
        {
            double core = 0.62 + 0.38 * strength, glow = 0.5 * strength;
            lineShare = Math.Clamp(lineShare, 0.005, 0.3);
            var stops = new GradientStopCollection
            {
                new(WithAlpha(core), 0),
                new(WithAlpha(core), lineShare),
                new(WithAlpha(glow), lineShare + 0.02),
                new(WithAlpha(glow * 0.42), 0.32),
                new(WithAlpha(glow * 0.12), 0.64),
                new(WithAlpha(0), 1),
            };
            var (start, end) = Side switch
            {
                Side.Top => (new Point(0, 0), new Point(0, 1)),
                Side.Bottom => (new Point(0, 1), new Point(0, 0)),
                Side.Left => (new Point(0, 0), new Point(1, 0)),
                _ => (new Point(1, 0), new Point(0, 0)),
            };
            var brush = new LinearGradientBrush(stops, start, end);
            brush.Freeze();
            _glow.Fill = brush;
        }

        static Color WithAlpha(double a) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), Warm.R, Warm.G, Warm.B);

        public void Place(IntPtr notch, int x, int y, int w, int h)
        {
            if (_hwnd == IntPtr.Zero) return;
            // Right under the notch in the topmost band, so the glow never lies over the script.
            Native.SetWindowPos(_hwnd, notch != IntPtr.Zero ? notch : Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            Placed = $"{Side.ToString().ToLowerInvariant()}:{w}x{h}";
        }

        public void Restack(IntPtr notch)
        {
            if (_hwnd == IntPtr.Zero) return;
            Native.SetWindowPos(_hwnd, notch != IntPtr.Zero ? notch : Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void FadeIn()
        {
            if (!MotionPrefs.Lively)
            {
                Window.Opacity = 1;
                return;
            }
            var a = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            Window.BeginAnimation(UIElement.OpacityProperty, a);
        }
    }
}
