using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Shammy.Core.Search;
using Shammy.Interop;
using Shammy.Motion;

namespace Shammy.Search;

/// <summary>
/// The frozen screen you circle on. It shows a still of the monitor (so the page can't scroll away
/// mid-gesture), a slowly rotating accent glow around the edge, and the glowing ink stroke.
/// It never takes keyboard focus, so whatever you were typing in stays focused.
/// </summary>
public partial class SearchOverlay : Window
{
    readonly ScreenShot _shot;
    readonly List<Point> _points = new();
    readonly SpringValue _dim = new(0, 0.002), _glow = new(0, 0.002), _hint = new(0, 0.002), _outside = new(0, 0.002);
    readonly RotateTransform _rotation = new() { CenterX = 0.5, CenterY = 0.5 };
    IDisposable? _frames;
    IntPtr _hwnd;
    bool _drawing, _closing;
    double _angle, _turnWait;

    /// <summary>The glow turns at about 30 fps: each turn repaints a blurred full-screen border.</summary>
    const double TurnEvery = 1.0 / 30;

    internal SearchOverlay(ScreenShot shot)
    {
        _shot = shot;
        InitializeComponent();
        Shot.Source = shot.Image;

        // The island's own palette (Album follows the song), not a fixed rainbow: circling is the island's light.
        var (a, b, c) = Palette();
        var glow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), RelativeTransform = _rotation };
        glow.GradientStops.Add(new GradientStop(a, 0));
        glow.GradientStops.Add(new GradientStop(b, 0.36));
        glow.GradientStops.Add(new GradientStop(c, 0.68));
        glow.GradientStops.Add(new GradientStop(a, 1));
        GlowSoft.BorderBrush = GlowEdge.BorderBrush = glow;

        // The ink: the colours run along the stroke's own bounds; the core is near-white with a breath of colour.
        var ink = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(a, 0), new GradientStop(b, 0.5), new GradientStop(c, 1),
        }, new Point(0, 0), new Point(1, 1));
        var core = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Toward(a, Colors.White, 0.6), 0), new GradientStop(Toward(b, Colors.White, 0.6), 0.5),
            new GradientStop(Toward(c, Colors.White, 0.6), 1),
        }, new Point(0, 0), new Point(1, 1));
        StrokeGlow.Stroke = StrokeMid.Stroke = ink;
        StrokeCore.Stroke = core;
        Highlight.BorderBrush = core;
        HighlightHalo.BorderBrush = ink;
        Highlight.Background = new SolidColorBrush(Color.FromArgb(0x16, a.R, a.G, a.B));

        var drop = new RadialGradientBrush(new GradientStopCollection
        {
            new GradientStop(Colors.White, 0), new GradientStop(Toward(a, Colors.White, 0.35), 0.35),
            new GradientStop(Color.FromArgb(0xC0, b.R, b.G, b.B), 0.75), new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
        });
        Bead.Fill = BeadTail1.Fill = BeadTail2.Fill = drop;
        foreach (var brush in new Brush[] { ink, core, drop, Highlight.Background }) brush.Freeze();

        SourceInitialized += OnSourceInitialized;
        ContentRendered += (_, _) => PlaceOnMonitor();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(PlaceOnMonitor);
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, _) => Cancelled?.Invoke();
        Closed += (_, _) => _frames?.Dispose();
    }

    /// <summary>Raised when a stroke or tap finishes. The points are in screen-image pixels.</summary>
    internal event Action<IReadOnlyList<Pt>>? SelectionMade;
    internal event Action? Cancelled;

    /// <summary>The Screenshot button: save the whole frozen screen.</summary>
    internal event Action? FullScreenshotRequested;

    /// <summary>A snip rectangle was dragged (in screen-image pixels).</summary>
    internal event Action<Box>? SnipMade;

    bool _snipMode;
    Point _snipStart;

    void OnFullScreenshot(object sender, RoutedEventArgs e) => FullScreenshotRequested?.Invoke();

    void OnSnipMode(object sender, RoutedEventArgs e)
    {
        _snipMode = !_snipMode;
        SnipLabel.Text = _snipMode ? "Drag an area…" : "Snip";
        SetHint(_snipMode ? "Drag a rectangle to save that area · Esc to cancel" : "Circle anything to search · Esc to cancel");
        _hint.AnimateTo(1, 0.3, 1);
    }

    public bool HasSelection { get; private set; }

    internal IReadOnlyList<Pt> SelectionPixels
    {
        get
        {
            double sx = _shot.Width / Math.Max(1, Root.ActualWidth), sy = _shot.Height / Math.Max(1, Root.ActualHeight);
            return _points.Select(p => new Pt(p.X * sx, p.Y * sy)).ToList();
        }
    }

    public void SetHint(string text) => HintText.Text = text;

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.SetExStyle(_hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// The window is created off-screen and only moved over the monitor after its first frame has
    /// rendered, otherwise you'd see a black flash before the screenshot appears.
    /// </summary>
    void PlaceOnMonitor()
    {
        if (_closing) return;
        var b = _shot.Bounds;
        // One pixel short of the full monitor: an exactly-fullscreen topmost window makes Windows and
        // game overlays (NVIDIA, Xbox Game Bar) think a game just launched.
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height - 1,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        if (_frames is not null) return;
        _dim.AnimateTo(0.16, 0.35, 1);
        _glow.AnimateTo(1, 0.5, 1);
        _hint.AnimateTo(1, 0.5, 0.8, 0.12);
        _frames = Animator.Subscribe(OnFrame);
        AppearedOnScreen?.Invoke();
    }

    internal event Action? AppearedOnScreen;

    static (Color A, Color B, Color C) Palette()
    {
        var colours = Theme.PaletteTheme.Colours;
        return (Theme.PaletteTheme.ToColor(colours[0]), Theme.PaletteTheme.ToColor(colours[1]), Theme.PaletteTheme.ToColor(colours[2]));
    }

    static Color Toward(Color c, Color to, double t) => Color.FromRgb(
        (byte)Math.Round(c.R + (to.R - c.R) * t), (byte)Math.Round(c.G + (to.G - c.G) * t), (byte)Math.Round(c.B + (to.B - c.B) * t));

    void OnFrame(double dt)
    {
        // The edge light drifts slowly round, ~30 times a second; with Calm motion (or Windows' animations off) it holds still.
        if (Infrastructure.MotionPrefs.Lively)
        {
            _angle = (_angle + dt * 24) % 360;
            _turnWait += dt;
            if (_turnWait >= TurnEvery)
            {
                _turnWait = 0;
                _rotation.Angle = _angle;
            }
        }
        if (_flow is not null) StepFlow(dt);
        Dim.Opacity = _dim.Value;
        GlowSoft.Opacity = _glow.Value * 0.9;
        GlowEdge.Opacity = _glow.Value;
        Hint.Opacity = _hint.Value;
        HintShift.Y = (1 - _hint.Value) * 24;
        Tools.Opacity = _glow.Value;
        Outside.Opacity = _outside.Value;
        if (_closing && _dim.IsSettled && _glow.IsSettled) Close();
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_snipMode)
        {
            _drawing = true;
            _snipStart = e.GetPosition(Root);
            System.Windows.Controls.Canvas.SetLeft(SnipRect, _snipStart.X);
            System.Windows.Controls.Canvas.SetTop(SnipRect, _snipStart.Y);
            SnipRect.Width = SnipRect.Height = 0;
            SnipRect.Visibility = Visibility.Visible;
            CaptureMouse();
            return;
        }
        _drawing = true;
        _points.Clear();
        _points.Add(e.GetPosition(Root));
        Highlight.Visibility = HighlightHalo.Visibility = Visibility.Collapsed;
        _selection = null;
        _outside.AnimateTo(0, 0.2, 1);
        CaptureMouse();
        Redraw(closed: false);
        _hint.AnimateTo(0, 0.25, 1);
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (!_drawing) return;
        var p = e.GetPosition(Root);
        if (_snipMode)
        {
            var r = new Rect(_snipStart, p);
            System.Windows.Controls.Canvas.SetLeft(SnipRect, r.X);
            System.Windows.Controls.Canvas.SetTop(SnipRect, r.Y);
            SnipRect.Width = r.Width;
            SnipRect.Height = r.Height;
            return;
        }
        var last = _points[^1];
        if ((p - last).LengthSquared < 4) return;
        _points.Add(p);
        Redraw(closed: false);
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing) return;
        _drawing = false;
        ReleaseMouseCapture();
        if (_snipMode)
        {
            var r = new Rect(_snipStart, e.GetPosition(Root));
            if (r.Width < 6 || r.Height < 6)
            {
                SnipRect.Visibility = Visibility.Collapsed;
                return;
            }
            double sx = _shot.Width / Math.Max(1, Root.ActualWidth), sy = _shot.Height / Math.Max(1, Root.ActualHeight);
            SnipMade?.Invoke(new Box(r.X * sx, r.Y * sy, r.Width * sx, r.Height * sy));
            return;
        }
        HasSelection = _points.Count > 0;
        bool tap = _points.Count < 3;
        if (!tap) Redraw(closed: true);
        else StrokeCore.Data = StrokeMid.Data = StrokeGlow.Data = null;
        SelectionMade?.Invoke(SelectionPixels);
    }

    /// <summary>
    /// Test hook: draws a stroke through <paramref name="fractions"/> (0–1 of the screen, densified to look hand-drawn)
    /// over <paramref name="seconds"/>, then lets go exactly as the pointer would.
    /// </summary>
    internal void DrawForTest(IReadOnlyList<Point> fractions, double seconds)
    {
        var dense = new List<Point>();
        for (int i = 0; i + 1 < fractions.Count; i++)
        {
            Point a = fractions[i], b = fractions[i + 1];
            for (int k = 0; k < 8; k++)
            {
                double t = k / 8.0;
                dense.Add(new Point((a.X + (b.X - a.X) * t) * Root.ActualWidth, (a.Y + (b.Y - a.Y) * t) * Root.ActualHeight));
            }
        }
        dense.Add(new Point(fractions[^1].X * Root.ActualWidth, fractions[^1].Y * Root.ActualHeight));
        _points.Clear();
        Highlight.Visibility = HighlightHalo.Visibility = Visibility.Collapsed;
        _selection = null;
        _hint.AnimateTo(0, 0.25, 1);
        int next = 0;
        double elapsed = 0;
        // On the animation frames (a low-priority timer would be starved by them): each frame adds the points due by then.
        IDisposable? sub = null;
        sub = Animator.Subscribe(dt =>
        {
            if (_closing)
            {
                sub?.Dispose();
                return;
            }
            // The first frame after idle reports the whole gap; a frame is never more than 1/20 s of drawing.
            elapsed += Math.Min(dt, 0.05);
            int due = Math.Min(dense.Count, (int)Math.Ceiling(dense.Count * elapsed / Math.Max(0.05, seconds)));
            while (next < due) _points.Add(dense[next++]);
            if (_points.Count < 2) return;
            Redraw(closed: false);
            if (next < dense.Count) return;
            sub?.Dispose();
            HasSelection = true;
            Redraw(closed: true);
            SelectionMade?.Invoke(SelectionPixels);
        });
    }

    void Redraw(bool closed)
    {
        StrokeGlow.Opacity = 0.8;
        StrokeMid.Opacity = 0.95;
        StrokeCore.Opacity = 1;
        if (_points.Count < 2)
        {
            StrokeCore.Data = StrokeMid.Data = StrokeGlow.Data = null;
            return;
        }
        bool loop = closed && LooksClosed();
        _loop = loop;
        // Let go, the hand-drawn line relaxes into a smooth loop, like a drop of liquid finding its shape.
        var points = closed ? Smooth(_points, loop, 2) : _points;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(points[0], false, loop);
            ctx.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        g.Freeze();
        StrokeCore.Data = StrokeMid.Data = StrokeGlow.Data = g;
    }

    /// <summary>Chaikin corner cutting: each pass rounds every corner a little; two passes take the shake out of a hand-drawn loop.</summary>
    static List<Point> Smooth(IReadOnlyList<Point> pts, bool loop, int passes)
    {
        var cur = pts.ToList();
        for (int p = 0; p < passes && cur.Count > 2; p++)
        {
            var next = new List<Point>(cur.Count * 2);
            if (!loop) next.Add(cur[0]);
            int n = loop ? cur.Count : cur.Count - 1;
            for (int i = 0; i < n; i++)
            {
                Point a = cur[i], b = cur[(i + 1) % cur.Count];
                next.Add(new Point(a.X * 0.75 + b.X * 0.25, a.Y * 0.75 + b.Y * 0.25));
                next.Add(new Point(a.X * 0.25 + b.X * 0.75, a.Y * 0.25 + b.Y * 0.75));
            }
            if (!loop) next.Add(cur[^1]);
            cur = next;
        }
        return cur;
    }

    bool LooksClosed()
    {
        var b = new Rect(_points[0], _points[0]);
        foreach (var p in _points) b.Union(p);
        return (_points[0] - _points[^1]).Length < 0.45 * Math.Max(b.Width, b.Height);
    }

    /// <summary>Spotlight the region that will be searched (given in screen-image pixels).</summary>
    internal void ShowSelection(Box pixels)
    {
        double sx = Root.ActualWidth / _shot.Width, sy = Root.ActualHeight / _shot.Height;
        var r = new Rect(pixels.X * sx, pixels.Y * sy, Math.Max(8, pixels.W * sx), Math.Max(8, pixels.H * sy));
        Outside.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight)),
            new RectangleGeometry(r, 14, 14));
        _outside.AnimateTo(0.35, 0.35, 1);
        System.Windows.Controls.Canvas.SetLeft(Highlight, r.X);
        System.Windows.Controls.Canvas.SetTop(Highlight, r.Y);
        Highlight.Width = r.Width;
        Highlight.Height = r.Height;
        System.Windows.Controls.Canvas.SetLeft(HighlightHalo, r.X - 8);
        System.Windows.Controls.Canvas.SetTop(HighlightHalo, r.Y - 8);
        HighlightHalo.Width = r.Width + 16;
        HighlightHalo.Height = r.Height + 16;
        // A drawn loop is already the selection's light; the lit frame is for a tap or a highlighter stroke.
        Highlight.Visibility = HighlightHalo.Visibility = _loop && StrokeCore.Data is not null ? Visibility.Collapsed : Visibility.Visible;
        _selection = r;
    }

    Rect? _selection;
    bool _loop;

    // ---- The answer flows into the island ---------------------------------------------------------------------

    sealed class Flow
    {
        public Point From, To;
        public double T;
        public Action? Done;
    }

    Flow? _flow;

    /// <summary>The time the drop takes from the selection to the island.</summary>
    const double FlowSeconds = 0.46;

    /// <summary>
    /// The answer leaves the selection as a drop of light and flies up into the island, then the screen unfreezes;
    /// <paramref name="done"/> runs as it lands (show the answer there). With Calm motion, no selection, or the island
    /// on another screen, it just unfreezes and <paramref name="done"/> runs at once.
    /// </summary>
    internal void FlowToIsland(Action done)
    {
        var island = Shell.IslandBounds.Screen;
        var b = _shot.Bounds;
        double cx = island.IsEmpty ? 0 : island.X + island.Width / 2;
        bool sameScreen = !island.IsEmpty && cx >= b.Left && cx <= b.Left + b.Width && island.Y >= b.Top - 40 && island.Y <= b.Top + b.Height;
        if (_closing || _frames is null || _selection is not { } sel || !sameScreen || !Infrastructure.MotionPrefs.Lively)
        {
            done();
            Dismiss();
            return;
        }
        double sx = Root.ActualWidth / Math.Max(1, b.Width), sy = Root.ActualHeight / Math.Max(1, b.Height);
        var to = new Point((cx - b.Left) * sx, (island.Y + island.Height - b.Top) * sy - 6);
        // A beat with the selection lit (the loop gathering into it) before the drop leaves.
        _flow = new Flow { From = new Point(sel.X + sel.Width / 2, sel.Y + sel.Height / 2), To = to, Done = done, T = -HoldBeat };
        StepFlow(0);
    }

    /// <summary>How long the lit selection holds before the drop leaves, as a fraction of the flight.</summary>
    const double HoldBeat = 0.45;

    void StepFlow(double dt)
    {
        var f = _flow!;
        bool leaving = f.T < 0 && f.T + dt / FlowSeconds >= 0;
        f.T = Math.Min(1, f.T + dt / FlowSeconds);
        if (f.T < 0) return;
        if (leaving || BeadTail2.Visibility != Visibility.Visible)
        {
            _outside.AnimateTo(0, 0.3, 1);
            Bead.Visibility = BeadTail1.Visibility = BeadTail2.Visibility = Visibility.Visible;
        }
        // The loop melts into the drop as it leaves.
        double ink = Math.Max(0, 1 - f.T / 0.35);
        StrokeGlow.Opacity = 0.8 * ink;
        StrokeMid.Opacity = 0.95 * ink;
        StrokeCore.Opacity = ink;
        if (ink <= 0) StrokeCore.Data = StrokeMid.Data = StrokeGlow.Data = null;
        // The selection melts away as the drop leaves it.
        Highlight.Opacity = Math.Max(0, 1 - f.T * 2.2);
        HighlightHalo.Opacity = 0.55 * Highlight.Opacity;
        Place(Bead, f, Ease(f.T), 1 - 0.35 * f.T);
        Place(BeadTail1, f, Ease(Math.Max(0, f.T - 0.07)), 0.9 - 0.4 * f.T);
        Place(BeadTail2, f, Ease(Math.Max(0, f.T - 0.14)), 0.8 - 0.45 * f.T);
        if (f.T < 1) return;
        _flow = null;
        Bead.Visibility = BeadTail1.Visibility = BeadTail2.Visibility = Visibility.Collapsed;
        try { f.Done?.Invoke(); }
        finally { Dismiss(); }
    }

    /// <summary>Eases in and out: it gathers itself, then is pulled up into the island.</summary>
    static double Ease(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    static void Place(FrameworkElement e, Flow f, double t, double scale)
    {
        double x = f.From.X + (f.To.X - f.From.X) * t;
        // A gentle arc sideways, so it reads as thrown, not slid.
        double arc = Math.Sin(t * Math.PI) * Math.Clamp((f.To.X - f.From.X) * 0.12, -60, 60);
        double y = f.From.Y + (f.To.Y - f.From.Y) * t;
        // Stretched a little along its path in mid-flight, like a drop.
        double s = Math.Max(0.2, scale);
        e.RenderTransform = new ScaleTransform(s, s * (1 + 0.35 * Math.Sin(t * Math.PI)), e.Width / 2, e.Height / 2);
        System.Windows.Controls.Canvas.SetLeft(e, x - arc - e.Width / 2);
        System.Windows.Controls.Canvas.SetTop(e, y - e.Height / 2);
    }

    internal void BringToTop() => Native.BringToTopmost(_hwnd);

    /// <summary>Fade the dim and glow out, then close; the live screen underneath is identical, so it feels like the screen unfreezes.</summary>
    internal void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        IsHitTestVisible = false;
        if (_frames is null)
        {
            Close();
            return;
        }
        StrokeCore.Data = StrokeMid.Data = StrokeGlow.Data = null;
        Highlight.Visibility = HighlightHalo.Visibility = Visibility.Collapsed;
        Bead.Visibility = BeadTail1.Visibility = BeadTail2.Visibility = Visibility.Collapsed;
        _flow = null;
        SnipRect.Visibility = Visibility.Collapsed;
        _dim.AnimateTo(0, 0.22, 1);
        _glow.AnimateTo(0, 0.22, 1);
        _hint.AnimateTo(0, 0.15, 1);
        _outside.AnimateTo(0, 0.22, 1);
    }
}
