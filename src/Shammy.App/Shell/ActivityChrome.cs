using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Shammy.Core.Notch;
using Shammy.Modules;
using Shammy.Motion;

namespace Shammy.Shell;

/// <summary>
/// What the pet-centred compact notch draws outside its own shape (the Activities board): the action circle, a
/// ~32 px droplet that bubbles off the notch's right edge on a short bridge and holds the live activity's one
/// direct action; the small dot beyond it for a second live activity (click to swap); and, for music, a soft glow
/// under the notch in the album's colour. The window positions it every frame next to the morphing shape.
/// </summary>
internal sealed class ActivityChrome
{
    const double CircleSize = 32, DiscSize = 26, Gap = 8, BridgeLeft = 12;

    static readonly Color Ink = Color.FromRgb(0xF5, 0xF5, 0xF7), Ink2 = Color.FromRgb(0xA3, 0xA3, 0xAB),
        Hairline = Color.FromRgb(0x26, 0x28, 0x2E), HoverRing = Color.FromRgb(0x4A, 0x4C, 0x55), Ground = Color.FromRgb(0x0B, 0x0B, 0x0D),
        Warm = Color.FromRgb(0xF0, 0xA2, 0x7A), RecRed = Color.FromRgb(0xFF, 0x45, 0x3A);

    // A soft gradient pool under the notch rather than a blur effect: effects are re-rasterised on every repaint
    // of the (software-rendered, layered) window, a gradient is nearly free.
    readonly Rectangle _glow = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed, Height = 30 };
    readonly RadialGradientBrush _glowBrush = new() { Center = new Point(0.5, 0), GradientOrigin = new Point(0.5, 0), RadiusX = 0.55, RadiusY = 1 };
    readonly Path _bridge, _dotBridge;
    readonly Border _circle, _disc, _dot, _dotInner;
    readonly Ellipse _dotLight;
    readonly ContentControl _icon = new() { Focusable = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly ScaleTransform _circleScale = new(0.25, 0.25), _dotScale = new(0.25, 0.25), _bridgeScale = new(0, 1), _dotBridgeScale = new(0, 1);
    readonly SpringValue _pop = new(0, 0.002), _dotPop = new(0, 0.002);

    CircleSpec? _spec;
    string? _ownerId, _dotId;
    bool _glowOn;
    Color _glowColor;
    ProgressArc? _ring;

    public ActivityChrome(Canvas root, UIElement shape, Brush notchFill)
    {
        _glow.Fill = _glowBrush;
        root.Children.Insert(root.Children.IndexOf(shape), _glow);

        _bridge = new Path
        {
            Data = Geometry.Parse("M 0,1 C 10,1 13,12 21,12 H 30 V 26 H 21 C 13,26 10,37 0,37 Z"),
            Fill = notchFill,
            RenderTransform = _bridgeScale,
            Opacity = 0,
        };
        _disc = new Border
        {
            Width = DiscSize, Height = DiscSize, CornerRadius = new CornerRadius(DiscSize / 2),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1.5),
            BorderBrush = Brushes.Transparent,
            Child = _icon,
        };
        _circle = new Border
        {
            Width = CircleSize, Height = CircleSize, CornerRadius = new CornerRadius(CircleSize / 2), Background = notchFill,
            RenderTransformOrigin = new Point(0, 0.5), RenderTransform = _circleScale, Child = _disc, Cursor = Cursors.Hand, Opacity = 0,
        };
        _circle.MouseEnter += (_, _) => _disc.BorderBrush = _spec is { Enabled: true } ? Solid(HoverRing) : Brushes.Transparent;
        _circle.MouseLeave += (_, _) =>
        {
            _disc.BorderBrush = Brushes.Transparent;
            PointerLeft?.Invoke();
        };
        _circle.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_spec is { Enabled: true } spec) CircleClicked?.Invoke(spec.Action);
        };

        _dotBridge = new Path
        {
            Data = Geometry.Parse("M 0,13 C 5,13 7,16.5 12,16.5 H 16 V 21.5 H 12 C 7,21.5 5,25 0,25 Z"),
            Fill = notchFill,
            RenderTransform = _dotBridgeScale,
            Opacity = 0,
        };
        _dotLight = new Ellipse { Width = 6, Height = 6, Fill = Solid(Ink) };
        _dotInner = new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = notchFill,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = _dotLight,
        };
        _dot = new Border
        {
            Width = 20, Height = 20, Background = Brushes.Transparent, Child = _dotInner, Cursor = Cursors.Hand,
            RenderTransformOrigin = new Point(0, 0.5), RenderTransform = _dotScale, Opacity = 0,
        };
        _dot.MouseLeave += (_, _) => PointerLeft?.Invoke();
        _dot.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_dotId is { } id) DotClicked?.Invoke(id);
        };

        foreach (var e in new UIElement[] { _bridge, _circle, _dotBridge, _dot }) root.Children.Add(e);
        Hide(snap: true);
    }

    /// <summary>The circle was clicked (only while its action is enabled).</summary>
    public event Action<CircleAction>? CircleClicked;

    /// <summary>The pointer left the circle or the dot (maybe onto the notch, which raises no new MouseEnter).</summary>
    public event Action? PointerLeft;

    /// <summary>The dot was clicked: give this activity the pet and the circle.</summary>
    public event Action<string>? DotClicked;

    public bool IsMouseOver => _circle.IsMouseOver || _dot.IsMouseOver || _bridge.IsMouseOver || _dotBridge.IsMouseOver;

    public CircleSpec? Circle => _spec;
    public string? DotId => _dotId;
    public bool Showing => _pop.Target > 0 || _dotPop.Target > 0 || _glowOn;

    static bool Reduced => !Infrastructure.MotionPrefs.Lively;

    /// <summary>Show the circle for <paramref name="owner"/> and the dot for <paramref name="dot"/> (either may be absent).</summary>
    public void Set(PetLive? owner, PetLive? dot, bool glow)
    {
        var spec = owner?.Circle;
        bool newOwner = owner?.Activity.Id != _ownerId;
        bool changed = newOwner || spec != _spec;
        _ownerId = owner?.Activity.Id;
        _spec = spec;
        if (spec is not null)
        {
            // Called every second while live: only redraw the icon when the action changed.
            if (changed) ApplyCircle(spec, owner!);
            else Refresh(owner!);
            // A new activity replays the bubble.
            if (newOwner || _pop.Target == 0) Bubble(_pop);
        }
        else
        {
            Sink(_pop);
        }
        _circle.IsHitTestVisible = spec is not null;

        bool newDot = dot?.Activity.Id != _dotId;
        _dotId = dot?.Activity.Id;
        if (dot is not null)
        {
            // Called every second: only a new colour repaints the dot.
            if (_dotLight.Fill is not SolidColorBrush { Color: var was } || was != DotColour(dot)) _dotLight.Fill = Solid(DotColour(dot));
            AutomationProperties.SetName(_dot, $"Also live: {dot.State.Label}. Give it the circle");
            _dot.ToolTip = $"{Describe(dot)} · click to swap";
            if (newDot || _dotPop.Target == 0) Bubble(_dotPop, delay: 0.25);
        }
        else
        {
            Sink(_dotPop);
        }
        _dot.IsHitTestVisible = dot is not null;

        SetGlow(glow && owner?.Art is not null, owner?.Art ?? Colors.Transparent);
    }

    public void Hide(bool snap = false)
    {
        // Called on every state change away from the pet: nothing to do when it's already gone.
        if (!snap && _spec is null && _dotId is null && _pop.Target == 0 && _dotPop.Target == 0 && !_glowOn) return;
        _spec = null;
        _ownerId = _dotId = null;
        _circle.IsHitTestVisible = _dot.IsHitTestVisible = false;
        if (snap)
        {
            _pop.Snap(0);
            _dotPop.Snap(0);
        }
        else
        {
            Sink(_pop);
            Sink(_dotPop);
        }
        SetGlow(false, Colors.Transparent);
    }

    static string Describe(PetLive live) =>
        live.State.Value.Length > 0 ? $"{live.State.Label} · {live.State.Value}" : live.State.Label;

    static Color DotColour(PetLive live) => live.State.Costume switch
    {
        PetCostume.Music => live.Art is { } art && art != Colors.White ? Mix(art, Colors.White, 0.35) : Warm,
        PetCostume.Recording => RecRed,
        PetCostume.Call when live.State.Muted => RecRed,
        _ => Ink,
    };

    void Bubble(SpringValue spring, double delay = 0.05)
    {
        if (Reduced)
        {
            spring.Snap(1);
            return;
        }
        if (spring.Value > 0.9) spring.Snap(0);
        spring.AnimateTo(1, 0.5, 0.62, delay);
    }

    void Sink(SpringValue spring)
    {
        if (Reduced) spring.Snap(0);
        else spring.AnimateTo(0, 0.3, 1);
    }

    void ApplyCircle(CircleSpec spec, PetLive owner)
    {
        var accent = (Brush)Application.Current.Resources["WallAccent"];
        _disc.Background = spec.Tone switch
        {
            CircleTone.Accent => accent,
            CircleTone.Red => Solid(RecRed),
            _ => Brushes.Transparent,
        };
        _circle.Cursor = spec.Enabled ? Cursors.Hand : null;
        if (!spec.Enabled) _disc.BorderBrush = Brushes.Transparent;
        AutomationProperties.SetName(_circle, $"{spec.Description}, {Describe(owner)}");
        _circle.ToolTip = spec.Description;

        // Dark marks on the accent disc, light ones elsewhere.
        var onAccent = Solid(Ground);
        _ring = null;
        _icon.Content = spec.Action switch
        {
            CircleAction.Mute => Icon(15, Stroke("M 9,6 A 3,3 0 0 1 15,6 V 11 A 3,3 0 0 1 9,11 Z M 5,11 A 7,7 0 0 0 19,11 M 12,18 V 21", Solid(Ink), 2.2)),
            CircleAction.Unmute => Icon(15, Stroke("M 9,6 A 3,3 0 0 1 15,6 V 11 A 3,3 0 0 1 9,11 Z M 5,11 A 7,7 0 0 0 19,11 M 12,18 V 21 M 4,4 L 20,20", Brushes.White, 2.2)),
            CircleAction.Pause => Icon(12, FillPath("M 6.5,4 H 8.5 A 1.5,1.5 0 0 1 10,5.5 V 18.5 A 1.5,1.5 0 0 1 8.5,20 H 6.5 A 1.5,1.5 0 0 1 5,18.5 V 5.5 A 1.5,1.5 0 0 1 6.5,4 Z M 15.5,4 H 17.5 A 1.5,1.5 0 0 1 19,5.5 V 18.5 A 1.5,1.5 0 0 1 17.5,20 H 15.5 A 1.5,1.5 0 0 1 14,18.5 V 5.5 A 1.5,1.5 0 0 1 15.5,4 Z", Solid(Ink))),
            CircleAction.Play or CircleAction.Resume => Icon(12, FillPath("M 7,4.5 V 19.5 A 1,1 0 0 0 8.5,20.36 L 20.5,12.86 A 1,1 0 0 0 20.5,11.14 L 8.5,3.64 A 1,1 0 0 0 7,4.5 Z", Solid(Ink))),
            CircleAction.Stop => Icon(12, new Rectangle { Width = 16, Height = 16, RadiusX = 4, RadiusY = 4, Fill = Solid(RecRed), Margin = new Thickness(4) }),
            CircleAction.Open => Icon(14, Stroke("M 7,17 L 17,7 M 9,7 H 17 V 15", spec.Tone == CircleTone.Accent ? onAccent : Solid(Ink), 2.6)),
            CircleAction.Dismiss => Icon(14, Stroke("M 5,12.5 L 9.5,17 L 19,7.5", spec.Tone == CircleTone.Accent ? onAccent : Solid(Ink), 2.8)),
            CircleAction.OpenWhenDone => DownloadWait(owner.State.Progress ?? 0),
            CircleAction.Skip => Icon(14, Stroke("M 6.5,6.5 L 17.5,17.5 M 17.5,6.5 L 6.5,17.5", spec.Tone == CircleTone.Accent ? onAccent : Solid(Ink), 2.6)),
            CircleAction.Restart => Icon(14, Stroke("M 19,12 A 7,7 0 1 1 16.8,6.9 M 17.5,3 V 7.5 H 13", spec.Tone == CircleTone.Accent ? onAccent : Solid(Ink), 2.6)),
            _ => null,
        };
    }

    /// <summary>Keeps the circle's live parts current (a download's ring) without replaying the bubble.</summary>
    public void Refresh(PetLive owner)
    {
        if (_ring is not null) _ring.Value = owner.State.Progress ?? 0;
    }

    FrameworkElement DownloadWait(double progress)
    {
        var grid = new Grid { Width = DiscSize, Height = DiscSize };
        _ring = new ProgressArc { Width = 28, Height = 28, Margin = new Thickness(-2.5), Value = progress };
        grid.Children.Add(_ring);
        var file = Icon(13, Stroke("M 6,3 H 14 L 19,8 V 21 H 6 Z M 14,3 V 8 H 19", Solid(Ink2), 2.2));
        file.HorizontalAlignment = HorizontalAlignment.Center;
        file.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(file);
        return grid;
    }

    static FrameworkElement Icon(double size, UIElement content)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(content);
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false };
    }

    static Path Stroke(string data, Brush brush, double thickness) => new()
    {
        Data = Geometry.Parse(data), Stroke = brush, StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
    };

    static Path FillPath(string data, Brush brush) => new() { Data = Geometry.Parse(data), Fill = brush };

    static SolidColorBrush Solid(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    // ---- Album glow ----------------------------------------------------------------------------------

    void SetGlow(bool on, Color colour)
    {
        if (on && colour != _glowColor)
        {
            _glowColor = colour;
            _glowBrush.GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(0xC0, colour.R, colour.G, colour.B), 0),
                new(Color.FromArgb(0x55, colour.R, colour.G, colour.B), 0.45),
                new(Color.FromArgb(0, colour.R, colour.G, colour.B), 1),
            };
        }
        if (on == _glowOn) return;
        _glowOn = on;
        // Still, not breathing: a breathing glow would repaint the window continuously.
        _glow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Per frame ---------------------------------------------------------------------------------

    /// <summary>Place everything against the notch's current shape (called every frame by the window).</summary>
    public void Apply(double cx, double w, double h, double r)
    {
        if (_glowOn)
        {
            // Spills from under the notch's lower half.
            _glow.Width = w + 24;
            Canvas.SetLeft(_glow, cx - w / 2 - 12);
            Canvas.SetTop(_glow, h * 0.55);
        }

        double right = cx + w / 2;
        double p = _pop.Value, q = _dotPop.Value;
        bool circle = p > 0.001;

        // The droplet: grows from a speck beside the edge, overshoots a touch and settles.
        _circle.Opacity = Math.Clamp(p * 1.8, 0, 1);
        _circleScale.ScaleX = _circleScale.ScaleY = 0.25 + 0.75 * p;
        Canvas.SetLeft(_circle, right + Gap - 16 * (1 - p));
        // Beside the row, also when the notch has grown a line under it.
        double rowH = Math.Min(h, Views.ActivityView.ViewHeight);
        Canvas.SetTop(_circle, Math.Max(0, (rowH - CircleSize) / 2));
        _bridge.Opacity = circle ? 1 : 0;
        _bridgeScale.ScaleX = Math.Clamp(p, 0, 1.05);
        _bridgeScale.ScaleY = Math.Clamp(h / 38, 0, 1);
        Canvas.SetLeft(_bridge, right - BridgeLeft);
        Canvas.SetTop(_bridge, 0);

        // The dot sits beyond the circle (or beside the notch when there's no circle).
        double dotBase = _pop.Target > 0 ? right + Gap + CircleSize + 4 : right + Gap;
        _dot.Opacity = Math.Clamp(q * 1.8, 0, 1);
        _dotScale.ScaleX = _dotScale.ScaleY = 0.25 + 0.75 * q;
        Canvas.SetLeft(_dot, dotBase - 12 * (1 - q));
        Canvas.SetTop(_dot, Math.Max(0, (rowH - 20) / 2));
        _dotBridge.Opacity = q > 0.001 ? 1 : 0;
        _dotBridgeScale.ScaleX = Math.Clamp(q, 0, 1.05);
        Canvas.SetLeft(_dotBridge, dotBase - 8 - (_pop.Target > 0 ? 0 : 4));
        Canvas.SetTop(_dotBridge, Math.Max(0, (h - 38) / 2));
    }

    public string Describe() =>
        $"circle={(_spec is null ? "none" : $"{_spec.Action}/{_spec.Tone}{(_spec.Enabled ? "" : "/disabled")}")} " +
        $"dot={_dotId ?? "-"} glow={(_glowOn ? _glowColor.ToString() : "off")} pop={_pop.Value:0.00}";

    /// <summary>A thin ring that fills clockwise from the top (a download's progress around its waiting circle).</summary>
    sealed class ProgressArc : FrameworkElement
    {
        double _value;

        public double Value
        {
            get => _value;
            set
            {
                double v = Math.Clamp(value, 0, 1);
                if (Math.Abs(v - _value) < 0.001) return;
                _value = v;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double t = 2, r = (Width - t) / 2 - 0.5;
            var c = new Point(Width / 2, Height / 2);
            dc.DrawEllipse(null, new Pen(Solid(Hairline), t), c, r, r);
            if (_value <= 0) return;
            var pen = new Pen(Solid(Ink), t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (_value >= 0.999)
            {
                dc.DrawEllipse(null, pen, c, r, r);
                return;
            }
            double a = -Math.PI / 2 + _value * 2 * Math.PI;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c.X, c.Y - r), false, false);
                ctx.ArcTo(new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a)), new Size(r, r), 0, _value > 0.5, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(null, pen, g);
        }
    }
}
