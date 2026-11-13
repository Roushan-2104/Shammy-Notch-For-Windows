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
/// The liquid dial. When the hold registers it blooms right at the pointer: the hub springs open from a point like a
/// drop landing (the cat, or Shammy's eyes, inside), one soft ring spreads out and fades, and six droplets bead off
/// the hub on springs and orbit it. The chosen droplet swells with an accent ring and stays joined to the hub by a
/// liquid neck; turning the mouse wheel moves the choice with a small click-stop. Letting go confirms the chosen
/// droplet where it is (it swells and fades) while everything else collapses back into the hub's point; a cancel
/// collapses with nothing confirmed.
/// <para>
/// The goo is plain geometry built each frame (circles and metaball necks from <see cref="Goo"/>), filled solid
/// black over a faint rim, so there is no blur effect to pay for. Frames run only from the bloom until the collapse
/// has finished, at most ~60 a second. The window is a small square around the pointer, never takes a click or the
/// keyboard, and is hidden from screen captures and recordings. In Calm motion (or with Windows' animations off) the
/// same happens quicker, with no overshoot and no ring.
/// </para>
/// </summary>
internal sealed class WheelWindow : Window
{
    const double Orbit = 90, DropR = 21, HubR = 32, Picked = 1.25, Confirmed = 1.6;
    // Half the window: the farthest droplet swollen, its ring and its label below it.
    const double Half = 150;
    const long WS_EX_TRANSPARENT = 0x20;

    enum Phase { Hidden, Open, Closing }

    sealed class Droplet
    {
        public required TextBlock Glyph;
        public required Border Label;
        public required TextBlock Name;
        public readonly SpringValue R = new(0, 0.05), S = new(0, 0.002);
        public double X, Y, Radius;
    }

    readonly Canvas _canvas = new() { Width = 2 * Half, Height = 2 * Half };
    readonly LiquidLayer _liquid = new() { Width = 2 * Half, Height = 2 * Half };
    readonly Grid _faceHost;
    readonly PetFace _face;
    readonly Droplet[] _drops;
    readonly SpringValue _hubR = new(0, 0.05), _spin = new(0, 0.05);
    readonly SolidColorBrush _ink = new(Colors.Black);
    readonly Pen _rim = new(new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)), 2);
    readonly SolidColorBrush _ringBrush = new(Colors.White);
    readonly Pen _ring;
    readonly SolidColorBrush _splashBrush = new(Color.FromArgb(150, 255, 255, 255));
    readonly Pen _splash;
    readonly Point _hub = new(Half, Half);
    Phase _phase = Phase.Hidden;
    IDisposable? _frames;
    IntPtr _hwnd;
    double _phaseTime, _sincePaint, _scale = 1;
    Native.RECT _rect;
    int _count, _selected = -1, _confirming = -1;
    double _confirmR;
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
        Width = 2 * Half;
        Height = 2 * Half;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        _rim.Freeze();
        _ink.Freeze();
        _ring = new Pen(_ringBrush, 1.6);
        _splash = new Pen(_splashBrush, 2);

        _canvas.Children.Add(_liquid);

        _face = new PetFace(0.85, FaceMood.Watch) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 3) };
        _faceHost = new Grid { Width = 2 * HubR, Height = 2 * HubR, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0, 0) };
        _faceHost.Children.Add(_face);
        Canvas.SetLeft(_faceHost, Half - HubR);
        Canvas.SetTop(_faceHost, Half - HubR);
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
            Panel.SetZIndex(glyph, 4);
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
    static double CloseTime => Lively ? 0.3 : 0.12;
    static double ConfirmTime => Lively ? 0.24 : 0.1;
    static double SplashTime => 0.45;

    /// <summary>Open: the choice still counts.</summary>
    public bool IsOpen => _phase == Phase.Open;

    /// <summary>The droplet shown as chosen, or -1.</summary>
    public int Selected => _selected;

    /// <summary>The window's scale (physical pixels per 1/96 inch), for turning pointer offsets into the drawing's units.</summary>
    public double Scale => _scale;

    /// <summary>Blooms at the screen point (physical pixels) with these droplets (top first, then clockwise).</summary>
    public void OpenAt(int x, int y, IReadOnlyList<(string Glyph, string Name)> droplets, bool cat, string moment)
    {
        _momentName = moment;
        _count = Math.Min(droplets.Count, _drops.Length);
        _selected = -1;
        _confirming = -1;
        _ringBrush.Color = Ui.Tokens.Accent.Color;
        _face.CatOverride = cat ? null : false;
        _face.Mood = FaceMood.Watch;
        _face.Restyle();
        _face.Look(0, 0);
        _faceHost.Opacity = 0;
        // The splash: the hub springs open from a point with a little overshoot...
        _hubR.Snap(0);
        _hubR.AnimateTo(HubR, Lively ? 0.26 : 0.1, Bouncy);
        _spin.Snap(0);
        // ...then the droplets bead off it one after another.
        for (int i = 0; i < _drops.Length; i++)
        {
            var d = _drops[i];
            d.Glyph.Text = i < _count ? droplets[i].Glyph : "";
            d.Name.Text = i < _count ? droplets[i].Name : "";
            d.R.Snap(0);
            d.S.Snap(0);
            d.Radius = 0;
            d.Glyph.Opacity = d.Label.Opacity = 0;
            if (i >= _count) continue;
            double delay = Lively ? 0.07 + i * 0.035 : 0;
            d.R.AnimateTo(Orbit, Lively ? 0.34 : 0.12, Bouncy, delay);
            d.S.AnimateTo(1, Lively ? 0.3 : 0.12, Bouncy, delay);
        }
        _phaseTime = 0;
        _sincePaint = 1;
        _phase = Phase.Open;
        if (!IsVisible) Show();
        Place(x, y);
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
        if (was >= 0) _drops[was].S.AnimateTo(1, 0.24, Bouncy);
        if (droplet >= 0)
        {
            var s = _drops[droplet].S;
            // Still beading off: it swells once it gets there (a spring can't wait for itself, so just aim higher).
            s.Snap(Math.Max(s.Value, turned != 0 && Lively ? 1.08 : Math.Min(1, s.Value)));
            s.AnimateTo(Picked, 0.26, Lively ? 0.42 : 1);
            var (ux, uy) = WheelGeometry.Direction(droplet, _count);
            _face.Look(ux * 3, uy * 2);
        }
        else _face.Look(0, 0);
    }

    /// <summary>Collapses the dial into the hub's point. With <paramref name="ran"/>, the chosen droplet confirms first: it swells and fades where it is.</summary>
    public void Close(bool ran)
    {
        if (!IsOpen) return;
        _phase = Phase.Closing;
        _phaseTime = 0;
        _confirming = ran ? _selected : -1;
        if (_confirming >= 0)
        {
            var d = _drops[_confirming];
            _confirmR = DropR * Math.Max(d.S.Value, 1);
            d.S.AnimateTo(Confirmed, ConfirmTime, 1);
            _face.Mood = FaceMood.Happy;
        }
        for (int i = 0; i < _drops.Length; i++)
        {
            if (i == _confirming) continue;
            _drops[i].R.AnimateTo(0, Lively ? 0.2 : 0.08, 1);
            _drops[i].S.AnimateTo(0.25, Lively ? 0.2 : 0.08, 1);
        }
        _hubR.AnimateTo(0, Lively ? 0.22 : 0.08, 1, Lively ? 0.08 : 0);
        _selected = -1;
    }

    // ---- Frames -------------------------------------------------------------------------------------

    void OnFrame(double dt)
    {
        _phaseTime += dt;
        _sincePaint += dt;
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

    /// <summary>How far the confirming droplet has faded (0 just chosen, 1 gone).</summary>
    double ConfirmFade => Math.Clamp(_phaseTime / ConfirmTime, 0, 1);

    /// <summary>Where everything is this frame, and the overlays (face, glyphs, labels) moved to match.</summary>
    void Layout()
    {
        double hubR = Math.Max(0, _hubR.Value);
        double faceIn = Math.Clamp((hubR - HubR * 0.5) / (HubR * 0.5), 0, 1);
        _faceHost.Opacity = _phase == Phase.Closing ? Math.Min(faceIn, Math.Max(0, 1 - _phaseTime / (CloseTime * 0.5))) : faceIn;
        var ft = (ScaleTransform)_faceHost.RenderTransform;
        ft.ScaleX = ft.ScaleY = Math.Max(0, hubR / HubR);

        for (int i = 0; i < _drops.Length; i++)
        {
            var d = _drops[i];
            if (i >= _count)
            {
                d.Radius = 0;
                d.Glyph.Opacity = d.Label.Opacity = 0;
                continue;
            }
            var (ux, uy) = Rotated(i);
            d.X = _hub.X + ux * d.R.Value;
            d.Y = _hub.Y + uy * d.R.Value;
            d.Radius = Math.Max(0, DropR * d.S.Value);
            double grown = Math.Clamp(d.Radius / DropR, 0, 1.6);
            double shown = _phase == Phase.Closing ? Math.Clamp(1 - _phaseTime / (CloseTime * 0.4), 0, 1) : Math.Clamp(d.R.Value / Orbit, 0, 1);
            var gt = (ScaleTransform)d.Glyph.RenderTransform;
            gt.ScaleX = gt.ScaleY = grown;
            Canvas.SetLeft(d.Glyph, d.X - DropR);
            Canvas.SetTop(d.Glyph, d.Y - DropR);
            d.Glyph.Opacity = i == _confirming ? 1 - ConfirmFade : Math.Min(Math.Min(grown, 1), shown);

            d.Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(d.Label, d.X - d.Label.DesiredSize.Width / 2);
            Canvas.SetTop(d.Label, d.Y + d.Radius + 5);
            d.Label.Opacity = i == _confirming ? 0 : shown * (i == _selected ? 1 : 0.7) * Math.Clamp(grown, 0, 1);
            Panel.SetZIndex(d.Label, i == _selected ? 3 : 2);
        }
    }

    (double X, double Y) Rotated(int i)
    {
        var (ux, uy) = WheelGeometry.Direction(i, _count);
        double a = _spin.Value * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        return (ux * c - uy * s, ux * s + uy * c);
    }

    // ---- The goo ------------------------------------------------------------------------------------

    void DrawLiquid(DrawingContext dc)
    {
        if (_phase == Phase.Hidden) return;
        double hubR = Math.Max(0, _hubR.Value);

        // The splash ring: one soft ring spreading out from the landing and fading (not in Calm motion).
        if (Lively && _phase == Phase.Open && _phaseTime < SplashTime)
        {
            double t = _phaseTime / SplashTime, eased = 1 - (1 - t) * (1 - t);
            dc.PushOpacity((1 - t) * 0.8);
            dc.DrawEllipse(null, _splash, _hub, HubR * (0.5 + 1.9 * eased), HubR * (0.5 + 1.9 * eased));
            dc.Pop();
        }

        var circles = new List<(Point C, double R)>(_count + 1);
        if (hubR > 0.5) circles.Add((_hub, hubR));
        var shapes = new List<Geometry>();
        for (int i = 0; i < _count; i++)
        {
            var d = _drops[i];
            if (d.Radius <= 0.5 || i == _confirming) continue;
            circles.Add((new Point(d.X, d.Y), d.Radius));
            // Necks: they stretch and snap as the droplets bead off, and hold the chosen one to the hub.
            if (hubR > 0.5 && Neck(_hub.X, _hub.Y, hubR, d.X, d.Y, d.Radius) is { } neck) shapes.Add(neck);
        }

        // A faint rim first, then the ink on top: only the outer edge of the rim stays, so it all reads as one shape.
        foreach (var s in shapes) dc.DrawGeometry(null, _rim, s);
        foreach (var (c, r) in circles) dc.DrawEllipse(null, _rim, c, r, r);
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

        // The confirm: the chosen droplet swells and fades where it is, its ring spreading with it.
        if (_confirming >= 0)
        {
            var d = _drops[_confirming];
            double fade = 1 - ConfirmFade;
            if (fade > 0.01 && d.Radius > 0.5)
            {
                var c = new Point(d.X, d.Y);
                dc.PushOpacity(fade);
                dc.DrawEllipse(null, _rim, c, d.Radius, d.Radius);
                dc.DrawEllipse(_ink, null, c, d.Radius, d.Radius);
                dc.DrawEllipse(null, _ring, c, d.Radius + 3.5 + 6 * ConfirmFade, d.Radius + 3.5 + 6 * ConfirmFade);
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

    /// <summary>Centres the window on a screen point, in that screen's own pixels (a second pass if WPF rescaled it for a new screen).</summary>
    void Place(int x, int y)
    {
        if (_hwnd == IntPtr.Zero) return;
        for (int pass = 0; pass < 3; pass++)
        {
            if (!Native.GetWindowRect(_hwnd, out var now)) return;
            int left = x - now.Width / 2, top = y - now.Height / 2;
            if (now.Left == left && now.Top == top) break;
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, left, top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        Native.GetWindowRect(_hwnd, out _rect);
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (_scale <= 0) _scale = 1;
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
        $"rect={_rect.Width}x{_rect.Height}@{_rect.Left},{_rect.Top} scale={_scale:0.##} cat={_face.CatOverride is null}";

    /// <summary>Draws whatever the window hands it; redrawn each frame by <see cref="UIElement.InvalidateVisual"/>.</summary>
    sealed class LiquidLayer : FrameworkElement
    {
        public Action<DrawingContext>? Draw;

        public LiquidLayer() => IsHitTestVisible = false;

        protected override void OnRender(DrawingContext dc) => Draw?.Invoke(dc);
    }
}
