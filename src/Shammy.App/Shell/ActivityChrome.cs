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

    // The kit's tokens (read each time, so high contrast is followed); Warm is the action circle's own.
    static Color Ink => Ui.Tokens.Text.Color;
    static Color Ink2 => Ui.Tokens.Text2.Color;
    static Color Hairline => Ui.Tokens.Hairline.Color;
    static Color HoverRing => Ui.Tokens.HairlineStrong.Color;
    static Color Ground => Ui.Tokens.OnAccent.Color;
    static Color RecRed => Ui.Tokens.Alarm.Color;
    static readonly Color Warm = Color.FromRgb(0xF0, 0xA2, 0x7A);

    // A soft gradient pool under the notch rather than a blur effect: effects are re-rasterised on every repaint
    // of the (software-rendered, layered) window, a gradient is nearly free.
    readonly Rectangle _glow = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed, Height = GlowHeight };
    // Brightest on the notch's bottom edge and gone before it reaches the box's own edges, so the glow has no
    // outline of its own and never shows beside the notch.
    readonly RadialGradientBrush _glowBrush = new() { Center = new Point(0.5, GlowUnder / GlowHeight), GradientOrigin = new Point(0.5, GlowUnder / GlowHeight), RadiusX = 0.5, RadiusY = 1 - GlowUnder / GlowHeight };

    /// <summary>The glow's box, and how much of it hides behind the notch (the rest spills below its bottom edge).</summary>
    const double GlowHeight = 30, GlowUnder = 13;

    /// <summary>The glow stays this far inside each side of the notch.</summary>
    const double GlowInset = 8;
    readonly Path _bridge, _dotBridge;

    // The island and its action circle as one drop of liquid: the circle plus a metaball neck to the pill's end,
    // filled with the island's own glass lined up pixel for pixel, so the surface flows on into the circle.
    readonly Path _liquid = new() { Opacity = 0 };
    readonly Brush _plainFill;
    Brush _surface;
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
        _plainFill = _surface = notchFill;
        _liquid.Fill = notchFill;
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
        // Screen readers see the circle and the dot as buttons (named by their tooltips); Invoke runs the click.
        _circle = new Ui.InvokableBorder
        {
            Invoked = () =>
            {
                if (_spec is { Enabled: true } spec) CircleClicked?.Invoke(spec.Action);
            },
            // Transparent (still clickable): the liquid surface under it is the circle's fill.
            Width = CircleSize, Height = CircleSize, CornerRadius = new CornerRadius(CircleSize / 2), Background = Brushes.Transparent,
            RenderTransformOrigin = new Point(0, 0.5), RenderTransform = _circleScale, Child = _disc, Cursor = Cursors.Hand, Opacity = 0,
        };
        _circle.MouseEnter += (_, _) => _disc.BorderBrush = _spec is { Enabled: true } ? Ui.Tokens.Frozen(HoverRing) : Brushes.Transparent;
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
        _dotLight = new Ellipse { Width = 6, Height = 6, Fill = Ui.Tokens.Frozen(Ink) };
        _dotInner = new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = notchFill,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = _dotLight,
        };
        _dot = new Ui.InvokableBorder
        {
            Invoked = () =>
            {
                if (_dotId is { } id) DotClicked?.Invoke(id);
            },
            Width = 20, Height = 20, Background = Brushes.Transparent, Child = _dotInner, Cursor = Cursors.Hand,
            RenderTransformOrigin = new Point(0, 0.5), RenderTransform = _dotScale, Opacity = 0,
        };
        _dot.MouseLeave += (_, _) => PointerLeft?.Invoke();
        _dot.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_dotId is { } id) DotClicked?.Invoke(id);
        };

        foreach (var e in new UIElement[] { _liquid, _bridge, _circle, _dotBridge, _dot }) root.Children.Add(e);
        Hide(snap: true);
    }

    /// <summary>The circle was clicked (only while its action is enabled).</summary>
    public event Action<CircleAction>? CircleClicked;

    /// <summary>The pointer left the circle or the dot (maybe onto the notch, which raises no new MouseEnter).</summary>
    public event Action? PointerLeft;

    /// <summary>The dot was clicked: give this activity the pet and the circle.</summary>
    public event Action<string>? DotClicked;

    public bool IsMouseOver => _circle.IsMouseOver || _dot.IsMouseOver || _liquid.IsMouseOver || _dotBridge.IsMouseOver;

    /// <summary>
    /// What the liquid is filled with: the island's glass (the same image at the same place, in Root's units), or the
    /// plain island colour when there is no glass.
    /// </summary>
    public void SetSurface(ImageSource? glass, Rect glassRect)
    {
        if (glass is null)
        {
            _surface = _plainFill;
        }
        else
        {
            // The same layers as the island: an opaque base, then the glass, at the same place.
            var base_ = _plainFill is SolidColorBrush solid ? solid.Color : Color.FromRgb(0x08, 0x08, 0x0A);
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(base_), null, new RectangleGeometry(glassRect)));
            drawing.Children.Add(new ImageDrawing(glass, glassRect));
            var brush = new DrawingBrush(drawing) { ViewportUnits = BrushMappingMode.Absolute, Viewport = glassRect, Stretch = Stretch.Fill, TileMode = TileMode.None };
            brush.Freeze();
            _surface = brush;
        }
        _liquid.Fill = _surface;
    }

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
            if (_dotLight.Fill is not SolidColorBrush { Color: var was } || was != DotColour(dot)) _dotLight.Fill = Ui.Tokens.Frozen(DotColour(dot));
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
        var accent = Ui.Tokens.Res("WallAccent");
        _disc.Background = spec.Tone switch
        {
            CircleTone.Accent => accent,
            CircleTone.Red => Ui.Tokens.Frozen(RecRed),
            _ => Brushes.Transparent,
        };
        _circle.Cursor = spec.Enabled ? Cursors.Hand : null;
        if (!spec.Enabled) _disc.BorderBrush = Brushes.Transparent;
        AutomationProperties.SetName(_circle, $"{spec.Description}, {Describe(owner)}");
        _circle.ToolTip = spec.Description;

        // Dark marks on the accent disc, light ones elsewhere.
        var onAccent = Ui.Tokens.Frozen(Ground);
        _ring = null;
        _icon.Content = spec.Action switch
        {
            CircleAction.Mute => Icon(15, Stroke("M 9,6 A 3,3 0 0 1 15,6 V 11 A 3,3 0 0 1 9,11 Z M 5,11 A 7,7 0 0 0 19,11 M 12,18 V 21", Ui.Tokens.Frozen(Ink), 2.2)),
            CircleAction.Unmute => Icon(15, Stroke("M 9,6 A 3,3 0 0 1 15,6 V 11 A 3,3 0 0 1 9,11 Z M 5,11 A 7,7 0 0 0 19,11 M 12,18 V 21 M 4,4 L 20,20", Brushes.White, 2.2)),
            CircleAction.Pause => Icon(12, FillPath("M 6.5,4 H 8.5 A 1.5,1.5 0 0 1 10,5.5 V 18.5 A 1.5,1.5 0 0 1 8.5,20 H 6.5 A 1.5,1.5 0 0 1 5,18.5 V 5.5 A 1.5,1.5 0 0 1 6.5,4 Z M 15.5,4 H 17.5 A 1.5,1.5 0 0 1 19,5.5 V 18.5 A 1.5,1.5 0 0 1 17.5,20 H 15.5 A 1.5,1.5 0 0 1 14,18.5 V 5.5 A 1.5,1.5 0 0 1 15.5,4 Z", Ui.Tokens.Frozen(Ink))),
            CircleAction.Play or CircleAction.Resume => Icon(12, FillPath("M 7,4.5 V 19.5 A 1,1 0 0 0 8.5,20.36 L 20.5,12.86 A 1,1 0 0 0 20.5,11.14 L 8.5,3.64 A 1,1 0 0 0 7,4.5 Z", Ui.Tokens.Frozen(Ink))),
            CircleAction.Stop => Icon(12, new Rectangle { Width = 16, Height = 16, RadiusX = 4, RadiusY = 4, Fill = spec.Tone == CircleTone.Red ? Brushes.White : Ui.Tokens.Frozen(RecRed), Margin = new Thickness(4) }),
            CircleAction.Open => Icon(14, Stroke("M 7,17 L 17,7 M 9,7 H 17 V 15", spec.Tone == CircleTone.Accent ? onAccent : Ui.Tokens.Frozen(Ink), 2.6)),
            CircleAction.Dismiss => Icon(14, Stroke("M 5,12.5 L 9.5,17 L 19,7.5", spec.Tone == CircleTone.Accent ? onAccent : Ui.Tokens.Frozen(Ink), 2.8)),
            CircleAction.OpenWhenDone => DownloadWait(owner.State.Progress ?? 0),
            CircleAction.Skip => Icon(14, Stroke("M 6.5,6.5 L 17.5,17.5 M 17.5,6.5 L 6.5,17.5", spec.Tone == CircleTone.Accent ? onAccent : Ui.Tokens.Frozen(Ink), 2.6)),
            // Two overlapping sheets: copy.
            CircleAction.Copy => Icon(14, Stroke("M 9,9 H 18 A 1.5,1.5 0 0 1 19.5,10.5 V 19 A 1.5,1.5 0 0 1 18,20.5 H 10.5 A 1.5,1.5 0 0 1 9,19 Z M 15,9 V 6 A 1.5,1.5 0 0 0 13.5,4.5 H 6 A 1.5,1.5 0 0 0 4.5,6 V 13.5 A 1.5,1.5 0 0 0 6,15 H 9", spec.Tone == CircleTone.Accent ? onAccent : Ui.Tokens.Frozen(Ink), 2.2)),
            CircleAction.Restart => Icon(14, Stroke("M 19,12 A 7,7 0 1 1 16.8,6.9 M 17.5,3 V 7.5 H 13", spec.Tone == CircleTone.Accent ? onAccent : Ui.Tokens.Frozen(Ink), 2.6)),
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
        var file = Icon(13, Stroke("M 6,3 H 14 L 19,8 V 21 H 6 Z M 14,3 V 8 H 19", Ui.Tokens.Frozen(Ink2), 2.2));
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
                new(Color.FromArgb(0x78, colour.R, colour.G, colour.B), 0),
                new(Color.FromArgb(0x30, colour.R, colour.G, colour.B), 0.5),
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
            // Spills from under the notch's bottom edge only, narrower than the notch itself.
            _glow.Width = Math.Max(0, w - GlowInset * 2);
            Canvas.SetLeft(_glow, cx - _glow.Width / 2);
            Canvas.SetTop(_glow, h - GlowUnder);
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
        // The old fixed bridge gives way to the liquid neck.
        _bridge.Opacity = 0;
        _bridge.IsHitTestVisible = false;
        _liquid.Opacity = circle ? Math.Clamp(p * 1.8, 0, 1) : 0;
        _liquid.IsHitTestVisible = circle;
        if (circle) _liquid.Data = Liquid(right, rowH, h, p);

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

    /// <summary>
    /// The circle (as it is drawn this frame: scaled from its left middle) plus a soft neck to the island's rounded
    /// end, as one shape. Cheap: one ellipse and one four-curve path per frame, only while the circle shows.
    /// </summary>
    Geometry Liquid(double right, double rowH, double h, double p)
    {
        double scale = 0.25 + 0.75 * p;
        double left = right + Gap - 16 * (1 - p);
        double top = Math.Max(0, (rowH - CircleSize) / 2);
        double r2 = CircleSize / 2 * scale, x2 = left + CircleSize / 2 * scale, y2 = top + CircleSize / 2;
        double r1 = Math.Min(rowH, h) / 2, x1 = right - r1, y1 = y2;
        Geometry circle = new EllipseGeometry(new Point(x2, y2), r2, r2);
        if (Core.Wheel.Goo.Bridge(x1, y1, r1, x2, y2, r2, v: 0.62, handle: 2.4, reach: 3) is not { } n)
        {
            circle.Freeze();
            return circle;
        }
        var figure = new PathFigure { StartPoint = new Point(n.P1.X, n.P1.Y), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new BezierSegment(new Point(n.H1.X, n.H1.Y), new Point(n.H3.X, n.H3.Y), new Point(n.P3.X, n.P3.Y), true));
        figure.Segments.Add(new LineSegment(new Point(n.P4.X, n.P4.Y), true));
        figure.Segments.Add(new BezierSegment(new Point(n.H4.X, n.H4.Y), new Point(n.H2.X, n.H2.Y), new Point(n.P2.X, n.P2.Y), true));
        // A true union: the neck runs into the circle with the opposite winding, which a plain group would cut as a
        // hole where they overlap.
        var liquid = new CombinedGeometry(GeometryCombineMode.Union, circle, new PathGeometry(new[] { figure }));
        liquid.Freeze();
        return liquid;
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
            dc.DrawEllipse(null, new Pen(Ui.Tokens.Frozen(Hairline), t), c, r, r);
            if (_value <= 0) return;
            var pen = new Pen(Ui.Tokens.Frozen(Ink), t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
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
