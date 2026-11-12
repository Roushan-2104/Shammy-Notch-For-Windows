using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Shammy.Core.Wheel;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Motion;
using Shammy.Views.Screens;

namespace Shammy.Modules.Wheel;

/// <summary>
/// The cat wheel: a small window of its own around the pointer. Eight round beads burst out of the centre one after
/// another on springs; the bead you lean toward swells and drifts out, its neighbours lean in, the rest step back,
/// and a glowing line ties it to the centre, where the cat (or Shammy's plain eyes) looks at it. Letting go folds
/// everything back. It never takes a click or the keyboard, and it is hidden from screen captures and recordings.
/// In Calm motion (or with Windows' animations off) the same moves happen without overshoot.
/// </summary>
internal sealed class WheelWindow : Window
{
    // Room for the farthest bead's name pill on every side.
    public const double Size = 600, C = Size / 2;
    const double Rest = 108, Out = 128, BeadSize = 56, HubSize = 92;
    const int DotCount = 32;
    const long WS_EX_TRANSPARENT = 0x20;

    readonly Canvas _canvas = new() { Width = Size, Height = Size };
    readonly Ellipse _halo;
    readonly Line _tether;
    readonly LinearGradientBrush _tetherBrush;
    readonly Grid _hub;
    readonly PetFace _face;
    readonly Border _name;
    readonly TextBlock _nameText;
    readonly (Grid El, TextBlock Glyph, Ellipse Ring, Ellipse Pin, SpringValue R, SpringValue S, SpringValue O)[] _beads;
    readonly (Ellipse El, double A, SpringValue G)[] _dots;
    readonly SpringValue _hubS = new(0, 0.002), _hubO = new(0, 0.002), _haloS = new(0, 0.002), _nameS = new(0, 0.002);
    IDisposable? _frames;
    IntPtr _hwnd;
    int _selected = -1, _flying = -1;
    bool _open;

    /// <summary>The bead under the pointer's direction, or -1 (the centre: letting go there cancels).</summary>
    public int Selected => _selected;
    public bool IsOpen => _open;

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
        Width = Size;
        Height = Size;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;

        _halo = new Ellipse
        {
            Width = 360,
            Height = 360,
            Fill = new RadialGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(158, 0, 0, 0), 0), new(Color.FromArgb(108, 0, 0, 0), 0.42), new(Color.FromArgb(0, 0, 0, 0), 1),
            }),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0, 0),
        };
        Canvas.SetLeft(_halo, C - 180);
        Canvas.SetTop(_halo, C - 180);
        _canvas.Children.Add(_halo);

        _dots = new (Ellipse, double, SpringValue)[DotCount];
        for (int i = 0; i < DotCount; i++)
        {
            var d = new Ellipse { Width = 5, Height = 5, Fill = Brushes.White, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1) };
            _canvas.Children.Add(d);
            _dots[i] = (d, i * 360.0 / DotCount, new SpringValue(0, 0.002));
        }

        _tetherBrush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute };
        _tetherBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        _tetherBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        TintTether();
        _tether = new Line { Stroke = _tetherBrush, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = 0 };
        _canvas.Children.Add(_tether);

        _beads = new (Grid, TextBlock, Ellipse, Ellipse, SpringValue, SpringValue, SpringValue)[WheelGeometry.Beads];
        for (int i = 0; i < _beads.Length; i++)
        {
            var g = new Grid { Width = BeadSize, Height = BeadSize, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0, 0), Opacity = 0 };
            var body = new Ellipse { Fill = Brushes.Black, Stroke = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255)), StrokeThickness = 1 };
            body.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 8, Direction = 270, Opacity = 0.45, Color = Colors.Black };
            var ring = new Ellipse { StrokeThickness = 1.5, Opacity = 0 };
            ring.SetResourceReference(Shape.StrokeProperty, "WallAccent");
            var glyph = new TextBlock { FontFamily = Ui.Tokens.FontRes("IconFont"), FontSize = 20, Foreground = Ui.Tokens.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var key = Views.WallParts.Text((i + 1).ToString(), 9, FontWeights.SemiBold, "WallText2");
            key.HorizontalAlignment = HorizontalAlignment.Center;
            key.VerticalAlignment = VerticalAlignment.Bottom;
            key.Margin = new Thickness(0, 0, 0, 4);
            var pin = new Ellipse { Width = 6, Height = 6, Fill = Ui.Tokens.Paw, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 8, 0), Visibility = Visibility.Collapsed };
            g.Children.Add(body);
            g.Children.Add(ring);
            g.Children.Add(glyph);
            g.Children.Add(key);
            g.Children.Add(pin);
            _canvas.Children.Add(g);
            _beads[i] = (g, glyph, ring, pin, new SpringValue(0, 0.05), new SpringValue(0, 0.002), new SpringValue(0, 0.002));
        }

        _hub = new Grid { Width = HubSize, Height = HubSize, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0, 0), Opacity = 0 };
        _hub.Children.Add(new Ellipse
        {
            Fill = Brushes.Black,
            Stroke = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
            StrokeThickness = 1,
            Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 12, Direction = 270, Opacity = 0.5, Color = Colors.Black },
        });
        _face = new PetFace(1.25, FaceMood.Watch) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
        _hub.Children.Add(_face);
        Canvas.SetLeft(_hub, C - HubSize / 2);
        Canvas.SetTop(_hub, C - HubSize / 2);
        _canvas.Children.Add(_hub);

        _nameText = new TextBlock { FontSize = 13, FontWeight = FontWeights.Bold };
        _nameText.SetResourceReference(TextBlock.FontFamilyProperty, "WallFont");
        _nameText.SetResourceReference(TextBlock.ForegroundProperty, "WallOnAccent");
        _name = new Border { CornerRadius = new CornerRadius(11), Padding = new Thickness(11, 3, 11, 4), Child = _nameText, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0, 0) };
        _name.SetResourceReference(Border.BackgroundProperty, "WallAccent");
        _canvas.Children.Add(_name);

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

    static double Response => MotionPrefs.Lively ? 0.32 : 0.22;
    static double Bouncy => MotionPrefs.Lively ? 0.58 : 1.0;

    /// <summary>The tether is the user's accent, fading in from the hub (re-read each time the wheel opens).</summary>
    void TintTether()
    {
        var a = Ui.Tokens.Accent.Color;
        _tetherBrush.GradientStops[0].Color = Color.FromArgb(0, a.R, a.G, a.B);
        _tetherBrush.GradientStops[1].Color = Color.FromArgb(230, a.R, a.G, a.B);
    }

    /// <summary>Opens around the screen point (physical pixels) with these actions on the beads.</summary>
    public void OpenAt(int x, int y, IReadOnlyList<(string Glyph, string Name, bool Pinned)> beads, bool cat)
    {
        _open = true;
        TintTether();
        _selected = -1;
        _flying = -1;
        _face.CatOverride = cat ? null : false;
        _face.Mood = FaceMood.Watch;
        _face.Restyle();
        _face.Look(0, 0);
        for (int i = 0; i < _beads.Length; i++)
        {
            var b = _beads[i];
            b.Glyph.Text = i < beads.Count ? beads[i].Glyph : "";
            b.Pin.Visibility = i < beads.Count && beads[i].Pinned ? Visibility.Visible : Visibility.Collapsed;
            b.El.Tag = i < beads.Count ? beads[i].Name : "";
            b.R.Snap(0);
            b.S.Snap(0.3);
            b.O.Snap(0);
            // A burst: each bead a moment after the one before.
            double delay = MotionPrefs.Lively ? i * 0.028 : 0;
            b.R.AnimateTo(Rest, Response, Bouncy, delay);
            b.S.AnimateTo(1, Response, Bouncy, delay);
            b.O.AnimateTo(1, 0.18, 1, delay);
        }
        foreach (var d in _dots) d.G.Snap(0);
        _hubS.Snap(0.2);
        _hubS.AnimateTo(1, 0.28, MotionPrefs.Lively ? 0.5 : 1);
        _hubO.Snap(0);
        _hubO.AnimateTo(1, 0.15, 1);
        _haloS.Snap(0.3);
        _haloS.AnimateTo(1, 0.3, 1);
        _nameS.Snap(0);
        if (!IsVisible) Show();
        Place(x, y);
        _frames ??= Animator.Subscribe(_ => Paint());
    }

    /// <summary>Which bead the pointer leans toward (offset from the centre in this window's pixels at 100%).</summary>
    public void Point(double dx, double dy)
    {
        if (!_open) return;
        Choose(WheelGeometry.Pick(dx, dy));
    }

    public void Choose(int bead)
    {
        if (!_open || bead == _selected) return;
        _selected = bead;
        for (int i = 0; i < _beads.Length; i++)
        {
            var b = _beads[i];
            double near = bead < 0 ? 0 : Math.Cos((i - bead) * Math.PI / 4);
            bool picked = i == bead;
            b.R.AnimateTo(picked ? Out : bead < 0 ? Rest : Rest - 4 + Math.Max(0, near) * 8, Response, Bouncy);
            b.S.AnimateTo(picked ? 1.22 : bead < 0 ? 1 : near > 0.6 ? 0.98 : 0.8, Response, Bouncy);
            b.O.AnimateTo(picked || bead < 0 ? 1 : near > 0.6 ? 0.9 : 0.55, 0.2, 1);
            b.Ring.Opacity = picked ? 1 : 0;
            b.Glyph.SetResourceReference(TextBlock.ForegroundProperty, picked ? "WallAccent" : "WallText");
        }
        if (bead >= 0)
        {
            var (ux, uy) = WheelGeometry.Direction(bead);
            _face.Look(ux * 3, uy * 2);
            _nameText.Text = _beads[bead].El.Tag as string ?? "";
            _nameS.Snap(0.4);
            _nameS.AnimateTo(1, 0.24, Bouncy);
        }
        else
        {
            _face.Look(0, 0);
            _nameS.AnimateTo(0, 0.18, 1);
        }
    }

    /// <summary>Folds the wheel away. With <paramref name="ran"/>, the chosen bead swells and fades as if it went off to work.</summary>
    public void Close(bool ran)
    {
        if (!_open) return;
        _open = false;
        _flying = ran ? _selected : -1;
        if (ran) _face.Mood = FaceMood.Happy;
        for (int i = 0; i < _beads.Length; i++)
        {
            var b = _beads[i];
            if (i == _flying)
            {
                b.S.AnimateTo(1.6, 0.25, 1);
                b.O.AnimateTo(0, 0.22, 1);
                continue;
            }
            b.R.AnimateTo(0, 0.2, 1);
            b.S.AnimateTo(0.2, 0.2, 1);
            b.O.AnimateTo(0, 0.14, 1);
        }
        foreach (var d in _dots) d.G.AnimateTo(0, 0.15, 1);
        _hubS.AnimateTo(ran ? 1.08 : 0.2, 0.2, 1, ran ? 0.12 : 0);
        _hubO.AnimateTo(0, 0.16, 1, ran ? 0.16 : 0);
        _haloS.AnimateTo(0, 0.2, 1);
        _nameS.AnimateTo(0, 0.14, 1);
        _selected = -1;
    }

    void Paint()
    {
        double halo = Math.Max(0, _haloS.Value);
        ((ScaleTransform)_halo.RenderTransform).ScaleX = ((ScaleTransform)_halo.RenderTransform).ScaleY = halo;
        _halo.Opacity = Math.Min(1, halo);
        ((ScaleTransform)_hub.RenderTransform).ScaleX = ((ScaleTransform)_hub.RenderTransform).ScaleY = Math.Max(0, _hubS.Value);
        _hub.Opacity = Math.Clamp(_hubO.Value, 0, 1);
        bool anything = _open || _hubO.Value > 0.01;
        for (int i = 0; i < _beads.Length; i++)
        {
            var b = _beads[i];
            var (ux, uy) = WheelGeometry.Direction(i);
            Canvas.SetLeft(b.El, C + ux * b.R.Value - BeadSize / 2);
            Canvas.SetTop(b.El, C + uy * b.R.Value - BeadSize / 2);
            var t = (ScaleTransform)b.El.RenderTransform;
            t.ScaleX = t.ScaleY = Math.Max(0, b.S.Value);
            b.El.Opacity = Math.Clamp(b.O.Value, 0, 1);
            Panel.SetZIndex(b.El, i == _selected || i == _flying ? 2 : 1);
            anything |= b.O.Value > 0.01;
        }
        foreach (var d in _dots)
        {
            double g = 0;
            if (_open && _selected >= 0)
            {
                double diff = Math.Abs(((d.A - (-90 + _selected * 45 + 360)) % 360 + 540) % 360 - 180);
                g = Math.Max(0, 1 - diff / 40);
            }
            else if (_open) g = 0.12;
            if (Math.Abs(d.G.Target - g) > 0.01) d.G.AnimateTo(g, 0.2, 0.9);
            double a = d.A * Math.PI / 180, r = 168 + d.G.Value * 10;
            Canvas.SetLeft(d.El, C + Math.Cos(a) * r - 2.5);
            Canvas.SetTop(d.El, C + Math.Sin(a) * r - 2.5);
            d.El.Opacity = Math.Clamp(d.G.Value * 0.95 + (_open ? 0.08 * _hubO.Value : 0), 0, 1);
            ((ScaleTransform)d.El.RenderTransform).ScaleX = ((ScaleTransform)d.El.RenderTransform).ScaleY = 0.6 + d.G.Value * 1.2;
        }
        if (_open && _selected >= 0)
        {
            var b = _beads[_selected];
            var (ux, uy) = WheelGeometry.Direction(_selected);
            double x0 = C + ux * (HubSize / 2), y0 = C + uy * (HubSize / 2);
            double reach = b.R.Value - BeadSize / 2 * b.S.Value;
            double x1 = C + ux * reach, y1 = C + uy * reach;
            _tether.X1 = x0; _tether.Y1 = y0; _tether.X2 = x1; _tether.Y2 = y1;
            _tetherBrush.StartPoint = new Point(x0, y0);
            _tetherBrush.EndPoint = new Point(x1, y1);
            _tether.Opacity = Math.Clamp(b.S.Value - 0.2, 0, 1);
            // The pill sits just clear of the bead, outward: far enough that its own half-size never overlaps.
            _name.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double pw = _name.DesiredSize.Width, ph = _name.DesiredSize.Height;
            double lr = b.R.Value + BeadSize / 2 * b.S.Value + 8 + Math.Abs(ux) * pw / 2 + Math.Abs(uy) * ph / 2;
            Canvas.SetLeft(_name, C + ux * lr - pw / 2);
            Canvas.SetTop(_name, C + uy * lr - ph / 2);
        }
        else _tether.Opacity = 0;
        var nt = (ScaleTransform)_name.RenderTransform;
        nt.ScaleX = nt.ScaleY = Math.Max(0, _nameS.Value);
        _name.Opacity = Math.Clamp(_nameS.Value, 0, 1);

        // Folded away and still: stop asking for frames and hide.
        if (!anything && !_open)
        {
            _frames?.Dispose();
            _frames = null;
            Hide();
        }
    }

    /// <summary>Centres the window on a screen point, in that screen's own pixels.</summary>
    void Place(int x, int y)
    {
        if (_hwnd == IntPtr.Zero) return;
        for (int pass = 0; pass < 2; pass++)
        {
            if (!Native.GetWindowRect(_hwnd, out var now)) return;
            int left = x - now.Width / 2, top = y - now.Height / 2;
            if (now.Left == left && now.Top == top) return;
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, left, top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    /// <summary>The window's scale (physical pixels per 1/96 inch), for turning pointer offsets into the drawing's units.</summary>
    public double Scale => _hwnd == IntPtr.Zero ? 1 : VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Test pictures only: lets screen captures see the wheel (it is hidden from them otherwise).</summary>
    public void AllowCapture(bool allow)
    {
        if (_hwnd != IntPtr.Zero) Native.SetWindowDisplayAffinity(_hwnd, allow ? Native.WDA_NONE : Native.WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>For the state pipe.</summary>
    public string Describe() => $"open={_open} visible={IsVisible} selected={_selected} " +
        $"beads=[{string.Join(" | ", _beads.Select(b => (b.El.Tag as string ?? "") + (b.Pin.Visibility == Visibility.Visible ? "*" : "")))}] cat={_face.CatOverride is null}";
}
