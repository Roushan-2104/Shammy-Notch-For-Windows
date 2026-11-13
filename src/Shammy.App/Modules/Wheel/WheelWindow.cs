using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Shammy.Core.Wheel;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Motion;
using Shammy.Views.Screens;

namespace Shammy.Modules.Wheel;

/// <summary>
/// The liquid dial: the island comes to you. When the hold registers, a black stream pours from the notch down to
/// the pointer and pools into a hub (the cat, or Shammy's eyes, inside); six droplets bead off the hub on springs
/// and orbit it. The chosen droplet swells with an accent ring; turning the mouse wheel moves the choice with a
/// small click-stop. Letting go sends the chosen droplet flying back up to the island while the others fall back
/// into the hub and the stream drains up after it; a cancel drains everything with nothing flying.
/// <para>
/// The goo is plain geometry built each frame (circles, metaball necks and the stream's outline from
/// <see cref="Goo"/>), filled solid black over a faint rim, so there is no blur effect to pay for. Frames run only
/// from opening until the last drop has drained, at most ~60 a second. The window covers just the path from the
/// island to the dial, never takes a click or the keyboard, and is hidden from screen captures and recordings.
/// In Calm motion (or with Windows' animations off) the same happens quicker, with no overshoot or wobble.
/// </para>
/// </summary>
internal sealed class WheelWindow : Window
{
    const double Orbit = 90, DropR = 21, HubR = 32, HeadR = 12, IslandR = 13, Picked = 1.25;
    // Room around the dial for the droplets, their swell, the ring and the labels; and above the island point.
    const double DialPad = 168, IslandPad = 40;
    const long WS_EX_TRANSPARENT = 0x20;

    enum Phase { Hidden, Pouring, Open, Closing }

    sealed class Droplet
    {
        public required TextBlock Glyph;
        public required Border Label;
        public required TextBlock Name;
        public readonly SpringValue R = new(0, 0.05), S = new(0, 0.002);
        public double X, Y, Radius;
    }

    readonly Canvas _canvas = new();
    readonly LiquidLayer _liquid = new();
    readonly Grid _faceHost;
    readonly PetFace _face;
    readonly Droplet[] _drops;
    readonly SpringValue _hubR = new(0, 0.05), _thick = new(0, 0.02), _spin = new(0, 0.05);
    readonly SolidColorBrush _ink = new(Colors.Black);
    readonly Pen _rim = new(new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)), 2);
    readonly SolidColorBrush _ringBrush = new(Colors.White);
    readonly Pen _ring;
    Phase _phase = Phase.Hidden;
    IDisposable? _frames;
    IntPtr _hwnd;
    double _time, _phaseTime, _sincePaint, _scale = 1;
    Point _island, _hub;
    Native.RECT _rect;
    int _count, _selected = -1, _flying = -1;
    Point _flyFrom;
    double _flyR, _hubAtClose;
    string _momentName = "usual";

    public WheelWindow()
    {
        Title = "Shammy wheel";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Width = 2 * DialPad;
        Height = 2 * DialPad;
        WindowStartupLocation = WindowStartupLocation.Manual;
        _rim.Freeze();
        _ink.Freeze();
        _ring = new Pen(_ringBrush, 1.6);

        _canvas.Children.Add(_liquid);

        _face = new PetFace(0.85, FaceMood.Watch) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 3) };
        _faceHost = new Grid { Width = 2 * HubR, Height = 2 * HubR, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0, 0) };
        _faceHost.Children.Add(_face);
        _canvas.Children.Add(_faceHost);

        _drops = new Droplet[WheelGeometry.Beads];
        for (int i = 0; i < _drops.Length; i++)
        {
            var glyph = new TextBlock
            {
                FontFamily = Ui.Tokens.FontRes("IconFont"),
                FontSize = 17,
                Foreground = Brushes.White,
                Width = 2 * DropR,
                Height = 2 * DropR,
                TextAlignment = TextAlignment.Center,
                Padding = new Thickness(0, DropR - 10, 0, 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
                Opacity = 0,
            };
            var name = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
            name.SetResourceReference(TextBlock.FontFamilyProperty, "WallFont");
            var label = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(222, 0, 0, 0)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 2, 7, 3),
                Child = name,
                Opacity = 0,
            };
            _canvas.Children.Add(label);
            _canvas.Children.Add(glyph);
            _drops[i] = new Droplet { Glyph = glyph, Label = label, Name = name };
        }

        _liquid.Draw = DrawLiquid;
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.SetExStyle(_hwnd, WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            // Never in a screenshot or a recording: the wheel is how you start one.
            Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        };
        new WindowInteropHelper(this).EnsureHandle();
    }

    static bool Lively => MotionPrefs.Lively;
    static double Bouncy => Lively ? 0.55 : 1.0;
    static double PourTime => Lively ? 0.26 : 0.1;
    static double CloseTime => Lively ? 0.36 : 0.13;
    static double FlyTime => Lively ? 0.26 : 0.1;

    /// <summary>Open (pouring or settled): the choice still counts.</summary>
    public bool IsOpen => _phase is Phase.Pouring or Phase.Open;

    /// <summary>The droplet shown as chosen, or -1.</summary>
    public int Selected => _selected;

    /// <summary>The window's scale (physical pixels per 1/96 inch), for turning pointer offsets into the drawing's units.</summary>
    public double Scale => _scale;

    /// <summary>
    /// Pours the island at (<paramref name="ix"/>, <paramref name="iy"/>) down to the dial at (<paramref name="x"/>,
    /// <paramref name="y"/>), screen pixels both, with these droplets (top first, then clockwise).
    /// </summary>
    public void OpenAt(int x, int y, int ix, int iy, IReadOnlyList<(string Glyph, string Name)> droplets, bool cat, string moment)
    {
        _momentName = moment;
        _count = Math.Min(droplets.Count, _drops.Length);
        _selected = -1;
        _flying = -1;
        _ringBrush.Color = Ui.Tokens.Accent.Color;
        _face.CatOverride = cat ? null : false;
        _face.Mood = FaceMood.Watch;
        _face.Restyle();
        _face.Look(0, 0);
        for (int i = 0; i < _drops.Length; i++)
        {
            var d = _drops[i];
            d.Glyph.Text = i < _count ? droplets[i].Glyph : "";
            d.Name.Text = i < _count ? droplets[i].Name : "";
            d.R.Snap(0);
            d.S.Snap(0);
            d.Radius = 0;
            d.Glyph.Opacity = d.Label.Opacity = 0;
        }
        _faceHost.Opacity = 0;
        _hubR.Snap(0);
        _thick.Snap(Lively ? 9 : 6);
        _spin.Snap(0);
        _time = _phaseTime = 0;
        _sincePaint = 1;
        _phase = Phase.Pouring;
        if (!IsVisible) Show();
        Place(x, y, ix, iy);
        _frames ??= Animator.Subscribe(OnFrame);
    }

    /// <summary>Shows <paramref name="droplet"/> as chosen (-1: none). <paramref name="turned"/> is ±1 when the mouse wheel moved it, for the click-stop.</summary>
    public void Select(int droplet, int turned = 0)
    {
        if (!IsOpen) return;
        if (droplet >= _count) droplet = -1;
        if (turned != 0 && Lively)
        {
            // The click-stop: the ring jumps a few degrees the way it turned and springs back past its rest.
            _spin.Snap(turned * 7);
            _spin.AnimateTo(0, 0.24, 0.42);
        }
        if (droplet == _selected) return;
        int was = _selected;
        _selected = droplet;
        if (_phase == Phase.Open)
        {
            if (was >= 0) _drops[was].S.AnimateTo(1, 0.24, Bouncy);
            if (droplet >= 0)
            {
                _drops[droplet].S.Snap(Math.Max(_drops[droplet].S.Value, turned != 0 && Lively ? 1.08 : 1));
                _drops[droplet].S.AnimateTo(Picked, 0.26, Lively ? 0.42 : 1);
            }
        }
        if (droplet >= 0)
        {
            var (ux, uy) = WheelGeometry.Direction(droplet, _count);
            _face.Look(ux * 3, uy * 2);
        }
        else _face.Look(0, 0);
    }

    /// <summary>Drains the dial back up to the island. With <paramref name="ran"/>, the chosen droplet flies up first.</summary>
    public void Close(bool ran)
    {
        if (!IsOpen) return;
        if (_phase == Phase.Pouring) Arrive(); // let go mid-pour: drain from wherever it is
        _phase = Phase.Closing;
        _phaseTime = 0;
        _flying = ran ? _selected : -1;
        if (_flying >= 0)
        {
            // From where it is now (worked out afresh: no frame may have been drawn yet if a key was that quick).
            var d = _drops[_flying];
            var (ux, uy) = Rotated(_flying);
            _flyFrom = new Point(_hub.X + ux * d.R.Value, _hub.Y + uy * d.R.Value);
            _flyR = Math.Max(DropR * d.S.Value, DropR * 0.6);
            _face.Mood = FaceMood.Happy;
        }
        for (int i = 0; i < _drops.Length; i++)
        {
            if (i == _flying) continue;
            _drops[i].R.AnimateTo(0, Lively ? 0.2 : 0.1, 1);
            _drops[i].S.AnimateTo(0.25, Lively ? 0.2 : 0.1, 1);
        }
        _hubAtClose = Math.Max(_hubR.Value, HeadR);
        _thick.AnimateTo(Lively ? 5 : 3, 0.18, 1);
        _selected = -1;
    }

    // ---- Frames -------------------------------------------------------------------------------------

    void OnFrame(double dt)
    {
        _time += dt;
        _phaseTime += dt;
        _sincePaint += dt;
        if (_phase == Phase.Pouring && _phaseTime >= PourTime) Arrive();
        if (_phase == Phase.Closing && _phaseTime >= CloseTime)
        {
            _phase = Phase.Hidden;
            _frames?.Dispose();
            _frames = null;
            Hide();
            return;
        }
        // At most ~60 paints a second, whatever the display's rate.
        if (_sincePaint < 1 / 61.0) return;
        _sincePaint = 0;
        Layout();
        _liquid.InvalidateVisual();
    }

    /// <summary>The pour reached the pointer: the head pools into the hub and the droplets bead off it.</summary>
    void Arrive()
    {
        _phase = Phase.Open;
        _phaseTime = 0;
        _hubR.Snap(HeadR);
        _hubR.AnimateTo(HubR, Lively ? 0.3 : 0.12, Bouncy);
        _thick.AnimateTo(3.2, Lively ? 0.4 : 0.12, 1);
        for (int i = 0; i < _count; i++)
        {
            double delay = Lively ? 0.04 + i * 0.035 : 0;
            _drops[i].R.AnimateTo(Orbit, Lively ? 0.34 : 0.12, Bouncy, delay);
            _drops[i].S.AnimateTo(i == _selected ? Picked : 1, Lively ? 0.3 : 0.12, Bouncy, delay);
        }
    }

    /// <summary>Where everything is this frame (canvas units), and the overlays (face, glyphs, labels) moved to match.</summary>
    void Layout()
    {
        double hubR = HubCentre(out var hub);
        _faceHost.Opacity = Math.Clamp((hubR - HeadR) / (HubR - HeadR), 0, 1) * (_phase == Phase.Closing ? Math.Max(0, 1 - _phaseTime / (CloseTime * 0.5)) : 1);
        var ft = (ScaleTransform)_faceHost.RenderTransform;
        ft.ScaleX = ft.ScaleY = Math.Max(0, hubR / HubR);
        Canvas.SetLeft(_faceHost, hub.X - HubR);
        Canvas.SetTop(_faceHost, hub.Y - HubR);

        for (int i = 0; i < _drops.Length; i++)
        {
            var d = _drops[i];
            if (i >= _count)
            {
                d.Radius = 0;
                d.Glyph.Opacity = d.Label.Opacity = 0;
                continue;
            }
            if (i == _flying)
            {
                double t = Math.Clamp(_phaseTime / FlyTime, 0, 1), e = Goo.EaseIn(t);
                d.X = _flyFrom.X + (_island.X - _flyFrom.X) * e;
                d.Y = _flyFrom.Y + (_island.Y - _flyFrom.Y) * e;
                d.Radius = t >= 1 ? 0 : _flyR * (1 - 0.45 * t);
            }
            else
            {
                var (ux, uy) = Rotated(i);
                d.X = hub.X + ux * d.R.Value;
                d.Y = hub.Y + uy * d.R.Value;
                d.Radius = Math.Max(0, DropR * d.S.Value);
            }
            double grown = Math.Clamp(d.Radius / DropR, 0, 1.4);
            double shown = _phase == Phase.Closing ? Math.Clamp(1 - _phaseTime / (CloseTime * 0.4), 0, 1) : Math.Clamp(d.R.Value / Orbit, 0, 1);
            var gt = (ScaleTransform)d.Glyph.RenderTransform;
            gt.ScaleX = gt.ScaleY = grown;
            Canvas.SetLeft(d.Glyph, d.X - DropR);
            Canvas.SetTop(d.Glyph, d.Y - DropR);
            d.Glyph.Opacity = i == _flying ? Math.Clamp(1 - _phaseTime / (FlyTime * 0.7), 0, 1) : Math.Min(grown, shown);

            d.Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(d.Label, d.X - d.Label.DesiredSize.Width / 2);
            Canvas.SetTop(d.Label, d.Y + d.Radius + 5);
            d.Label.Opacity = i == _flying ? 0 : shown * (i == _selected ? 1 : 0.7) * Math.Clamp(grown, 0, 1);
            Panel.SetZIndex(d.Label, i == _selected ? 3 : 2);
            Panel.SetZIndex(d.Glyph, 4);
        }
    }

    /// <summary>The hub's centre and radius now: under the pointer while open, sliding back up the stream while it drains.</summary>
    double HubCentre(out Point hub)
    {
        switch (_phase)
        {
            case Phase.Pouring:
            {
                double e = Goo.EaseIn(_phaseTime / PourTime);
                hub = Lerp(_island, _hub, e);
                return HeadR;
            }
            case Phase.Closing:
            {
                double e = 1 - DrainAt();
                hub = Lerp(_island, _hub, e);
                return 5 + (_hubAtClose - 5) * e;
            }
            default:
                hub = _hub;
                return Math.Max(0, _hubR.Value);
        }
    }

    /// <summary>How far the drain has gone (0 just let go, 1 all back in the island); it waits a moment for the droplets to fall in.</summary>
    double DrainAt()
    {
        double wait = Lively ? 0.08 : 0.02;
        return Goo.EaseIn((_phaseTime - wait) / (CloseTime - wait));
    }

    (double X, double Y) Rotated(int i)
    {
        var (ux, uy) = WheelGeometry.Direction(i, _count);
        double a = _spin.Value * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        return (ux * c - uy * s, ux * s + uy * c);
    }

    static Point Lerp(Point a, Point b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    // ---- The goo ------------------------------------------------------------------------------------

    void DrawLiquid(DrawingContext dc)
    {
        if (_phase == Phase.Hidden) return;
        double hubR = HubCentre(out var hub);
        double reach = _phase switch
        {
            Phase.Pouring => Goo.EaseIn(_phaseTime / PourTime),
            Phase.Closing => 1 - DrainAt(),
            _ => 1,
        };
        double wobble = Lively ? 1 : 0;
        var stream = Goo.Stream(_island.X, _island.Y, _hub.X, _hub.Y, 0, reach, Math.Max(0.5, _thick.Value), 22, _time, wobble);
        var circles = new List<(Point C, double R)>(_count + 3);
        if (hubR > 0.5) circles.Add((hub, hubR));
        for (int i = 0; i < _count; i++)
            if (_drops[i].Radius > 0.5) circles.Add((new Point(_drops[i].X, _drops[i].Y), _drops[i].Radius));

        var shapes = new List<Geometry>();
        if (stream.Count > 2)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(P(stream[0]), true, true);
                c.PolyLineTo(stream.Skip(1).Select(P).ToList(), true, true);
            }
            g.Freeze();
            shapes.Add(g);
        }
        // Necks: hub to each droplet (they snap off as the droplets reach their orbit), and the flying droplet to the island.
        if (hubR > 0.5)
            for (int i = 0; i < _count; i++)
            {
                var d = _drops[i];
                if (d.Radius <= 0.5 || i == _flying) continue;
                if (Neck(hub.X, hub.Y, hubR, d.X, d.Y, d.Radius) is { } neck) shapes.Add(neck);
            }
        if (_flying >= 0 && _drops[_flying].Radius > 0.5 && Neck(_island.X, _island.Y, IslandR, _drops[_flying].X, _drops[_flying].Y, _drops[_flying].Radius) is { } up)
            shapes.Add(up);

        // A faint rim first, then the ink on top: only the outer edge of the rim stays, so the whole pour reads as one shape.
        foreach (var s in shapes) dc.DrawGeometry(null, _rim, s);
        foreach (var (c, r) in circles) dc.DrawEllipse(null, _rim, c, r, r);
        dc.DrawEllipse(_ink, null, _island, IslandR, IslandR);
        foreach (var s in shapes) dc.DrawGeometry(_ink, null, s);
        foreach (var (c, r) in circles) dc.DrawEllipse(_ink, null, c, r, r);

        if (_selected >= 0 && _selected < _count && _phase == Phase.Open)
        {
            var d = _drops[_selected];
            double glow = Math.Clamp((d.S.Value - 1) / (Picked - 1), 0, 1);
            if (glow > 0.02)
            {
                dc.PushOpacity(glow);
                dc.DrawEllipse(null, _ring, new Point(d.X, d.Y), d.Radius + 3.5, d.Radius + 3.5);
                dc.Pop();
            }
        }
    }

    static Geometry? Neck(double x1, double y1, double r1, double x2, double y2, double r2)
    {
        if (Goo.Bridge(x1, y1, r1, x2, y2, r2) is not { } b) return null;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(P(b.P1), true, true);
            c.BezierTo(P(b.H1), P(b.H3), P(b.P3), true, true);
            c.LineTo(P(b.P4), true, true);
            c.BezierTo(P(b.H4), P(b.H2), P(b.P2), true, true);
        }
        g.Freeze();
        return g;
    }

    static Point P(GooPoint p) => new(p.X, p.Y);

    // ---- Placement ----------------------------------------------------------------------------------

    /// <summary>
    /// Covers the path from the island to the dial (screen pixels) with room around the dial, kept on the pointer's
    /// screen, and works out where both are in the drawing's units.
    /// </summary>
    void Place(int x, int y, int ix, int iy)
    {
        if (_hwnd == IntPtr.Zero) return;
        var monitor = Native.MonitorFromPoint(new Native.POINT(x, y), Native.MONITOR_DEFAULTTONEAREST);
        var screen = Native.MonitorBounds(monitor);
        double s = Native.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
        int pad = (int)Math.Ceiling(DialPad * s), ipad = (int)Math.Ceiling(IslandPad * s);
        var want = new Native.RECT
        {
            Left = Math.Max(screen.Left, Math.Min(x - pad, ix - ipad)),
            Top = Math.Max(screen.Top, Math.Min(y - pad, iy - ipad)),
            Right = Math.Min(screen.Right, Math.Max(x + pad, ix + ipad)),
            Bottom = Math.Min(screen.Bottom, Math.Max(y + pad, iy + ipad)),
        };
        // Moving onto a screen with another scale makes WPF resize the window to suit it: set the rect again after.
        for (int pass = 0; pass < 3; pass++)
        {
            if (Native.GetWindowRect(_hwnd, out var now) && now.Left == want.Left && now.Top == want.Top && now.Width == want.Width && now.Height == want.Height) break;
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, want.Left, want.Top, want.Width, want.Height, Native.SWP_NOACTIVATE);
        }
        Native.GetWindowRect(_hwnd, out _rect);
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (_scale <= 0) _scale = s;
        _canvas.Width = Width = _rect.Width / _scale;
        _canvas.Height = Height = _rect.Height / _scale;
        _liquid.Width = _canvas.Width;
        _liquid.Height = _canvas.Height;
        _hub = new Point((x - _rect.Left) / _scale, (y - _rect.Top) / _scale);
        _island = new Point((ix - _rect.Left) / _scale, (iy - _rect.Top) / _scale);
    }

    /// <summary>Test pictures only: lets screen captures see the wheel (it is hidden from them otherwise).</summary>
    public void AllowCapture(bool allow)
    {
        if (_hwnd != IntPtr.Zero) Native.SetWindowDisplayAffinity(_hwnd, allow ? Native.WDA_NONE : Native.WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>For the state pipe.</summary>
    public string Describe() =>
        $"open={IsOpen} phase={_phase.ToString().ToLowerInvariant()} visible={IsVisible} moment={_momentName} selected={_selected} " +
        $"droplets=[{string.Join(" | ", _drops.Take(_count).Select(d => d.Name.Text))}] " +
        $"rect={_rect.Width}x{_rect.Height}@{_rect.Left},{_rect.Top} scale={_scale:0.##} " +
        $"dial={_hub.X:0},{_hub.Y:0} island={_island.X:0},{_island.Y:0} cat={_face.CatOverride is null}";

    /// <summary>Draws whatever the window hands it; redrawn each frame by <see cref="UIElement.InvalidateVisual"/>.</summary>
    sealed class LiquidLayer : FrameworkElement
    {
        public Action<DrawingContext>? Draw;

        public LiquidLayer() => IsHitTestVisible = false;

        protected override void OnRender(DrawingContext dc) => Draw?.Invoke(dc);
    }
}
