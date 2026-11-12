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

        var glow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), RelativeTransform = _rotation };
        var accent = Theme.Accent.Current;
        glow.GradientStops.Add(new GradientStop(accent, 0));
        glow.GradientStops.Add(new GradientStop(Color.FromRgb(0xA7, 0x8B, 0xFA), 0.3));
        glow.GradientStops.Add(new GradientStop(Color.FromRgb(0xF4, 0x72, 0xB6), 0.55));
        glow.GradientStops.Add(new GradientStop(Color.FromRgb(0x22, 0xD3, 0xEE), 0.8));
        glow.GradientStops.Add(new GradientStop(accent, 1));
        GlowSoft.BorderBrush = GlowEdge.BorderBrush = StrokeGlow.Stroke = Highlight.BorderBrush = glow;

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

    void OnFrame(double dt)
    {
        // A functional indicator (searching is live), so it keeps turning with Calm on, but only ~30 times a second.
        _angle = (_angle + dt * 40) % 360;
        _turnWait += dt;
        if (_turnWait >= TurnEvery)
        {
            _turnWait = 0;
            _rotation.Angle = _angle;
        }
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
        Highlight.Visibility = Visibility.Collapsed;
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
        else StrokeCore.Data = StrokeGlow.Data = null;
        SelectionMade?.Invoke(SelectionPixels);
    }

    void Redraw(bool closed)
    {
        if (_points.Count < 2)
        {
            StrokeCore.Data = StrokeGlow.Data = null;
            return;
        }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(_points[0], false, closed && LooksClosed());
            ctx.PolyLineTo(_points.Skip(1).ToList(), true, true);
        }
        g.Freeze();
        StrokeCore.Data = StrokeGlow.Data = g;
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
        Highlight.Visibility = Visibility.Visible;
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
        StrokeCore.Data = StrokeGlow.Data = null;
        Highlight.Visibility = Visibility.Collapsed;
        SnipRect.Visibility = Visibility.Collapsed;
        _dim.AnimateTo(0, 0.22, 1);
        _glow.AnimateTo(0, 0.22, 1);
        _hint.AnimateTo(0, 0.15, 1);
        _outside.AnimateTo(0, 0.22, 1);
    }
}
