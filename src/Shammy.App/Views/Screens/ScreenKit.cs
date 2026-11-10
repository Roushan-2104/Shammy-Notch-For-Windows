using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Shammy.Views.Screens;

internal enum Weight { Regular, Medium, SemiBold, Bold, ExtraBold }

/// <summary>
/// The Focus Wall's standard controls for the designed screens (alarm ringing, Alarms, Settings, the tour):
/// the tokens, Schibsted Grotesk text (with real letter-spacing and tabular figures, which WPF's TextBlock
/// lacks), and code-built switches, segments, steppers, day toggles and buttons with the mockup's sizes and
/// hover states. Everything is monochrome; the accent is only for the one primary control.
/// </summary>
internal static class ScreenKit
{
    static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static SolidColorBrush B(string hex)
    {
        if (Cache.TryGetValue(hex, out var b)) return b;
        b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return Cache[hex] = b;
    }

    public static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static readonly SolidColorBrush Ground = B("#0B0B0D"), Tile = B("#16161A"), TileHover = B("#1E1E24"),
        Hairline = B("#26282E"), HairlineHover = B("#3A3C44"), HairlineStrong = B("#4A4C55"), Fg = B("#F5F5F7"),
        Fg2 = B("#A3A3AB"), OnAccent = B("#0B0B0D"),
        Blush = B("#FF6B8A");

    /// <summary>Animations snap when Windows' "Animation effects" are off.</summary>
    /// <summary>The accent the user picked (orange by default); it changes live, see <see cref="Theme.AccentTheme"/>.</summary>
    public static SolidColorBrush Accent => Theme.AccentTheme.Brush;

    public static SolidColorBrush AccentHover => Theme.AccentTheme.Hover;

    public static bool ReducedMotion => !Infrastructure.MotionPrefs.Lively;

    // ---- Type ------------------------------------------------------------------------------------

    /// <summary>Each weight is its own embedded family (asking one family for a weight can pick the wrong file).</summary>
    public static (FontFamily Family, FontWeight Weight) Font(Weight w)
    {
        (string key, FontWeight weight) = w switch
        {
            Weight.Medium => ("WallFontMedium", FontWeights.Medium),
            Weight.SemiBold => ("WallFontSemiBold", FontWeights.SemiBold),
            Weight.Bold => ("WallFont", FontWeights.Bold),
            Weight.ExtraBold => ("WallFontExtraBold", FontWeights.ExtraBold),
            _ => ("WallFont", FontWeights.Normal),
        };
        var family = Application.Current?.TryFindResource(key) as FontFamily ?? new FontFamily("Segoe UI");
        return (family, weight);
    }

    /// <summary>Plain text in the wall's face, the right embedded file per weight.</summary>
    public static TextBlock Text(string text, double size, Weight w = Weight.Regular, Brush? brush = null)
    {
        var (family, weight) = Font(w);
        var t = new TextBlock
        {
            Text = text,
            FontFamily = family,
            FontWeight = weight,
            FontSize = size,
            Foreground = brush ?? ScreenKit.Fg,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return t;
    }

    /// <summary>Wrapping body text (the tour's sentences, hints).</summary>
    public static TextBlock Para(string text, double size, Brush? brush = null, double lineHeight = 0)
    {
        var t = Text(text, size, Weight.Regular, brush ?? Fg2);
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        if (lineHeight > 0)
        {
            t.LineHeight = lineHeight;
            t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }
        return t;
    }

    /// <summary>The design's small uppercase label: 11 px semibold, tracked 0.12 em, secondary grey.</summary>
    public static SpacedText Label(string text, Brush? brush = null, double size = 11) =>
        new() { Text = text.ToUpperInvariant(), FontSize = size, Weight = Weight.SemiBold, Foreground = brush ?? Fg2, Tracking = 0.12 };

    /// <summary>A big tight display line (times, titles): letter-spacing in em, usually negative.</summary>
    public static SpacedText Display(string text, double size, Weight w = Weight.ExtraBold, double tracking = -0.03, Brush? brush = null) =>
        new() { Text = text, FontSize = size, Weight = w, Foreground = brush ?? ScreenKit.Fg, Tracking = tracking };

    // ---- Icons -----------------------------------------------------------------------------------

    /// <summary>A stroked 24×24 icon (the mockup's inline SVGs) at <paramref name="size"/>.</summary>
    public static FrameworkElement Icon(string data, Brush stroke, double size = 14, double strokeWidth = 2.2, Brush? fill = null)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data),
            Stroke = stroke,
            StrokeThickness = strokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = fill,
        };
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
    }

    public static class Paths
    {
        public const string Back = "M15 5l-7 7 7 7";
        public const string Plus = "M12 5v14M5 12h14";
        public const string Chevron = "M6 9l6 6 6-6";
        public const string Check = "M5 12.5 9.5 17 19 7.5";
        public const string Arrow = "M5 12h14M13 6l6 6-6 6";
        public const string BellOff = "M8.7 4.3A6 6 0 0 1 18 9.5V14l1.5 2.5H8M5.6 8.2A6 6 0 0 0 6 9.5V14l-1.5 2.5H8M10 20h4M3 3l18 18";
        public const string Bell = "M6 9.5a6 6 0 0 1 12 0V14l1.5 2.5h-15L6 14z M10 20h4";
        public const string Trash = "M5 7h14M10 7V5h4v2M7 7l1 12h8l1-12";
        public const string Copilot = "M12 3a9 9 0 1 0 9 9M15 3h6v6";
    }

    // ---- Buttons ---------------------------------------------------------------------------------

    /// <summary>The wall's filled primary button (accent, dark text): one per screen.</summary>
    public static Pressable Primary(string text, Action click, double height = 32, double fontSize = 13, string? icon = null, double padding = 18)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (icon is not null)
        {
            var i = Icon(icon, OnAccent, fontSize + 1, 2.6);
            i.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(i);
        }
        row.Children.Add(Text(text, fontSize, Weight.Bold, OnAccent));
        var p = new Pressable(Accent, AccentHover)
        {
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Padding = new Thickness(padding, 0, padding, 0),
            Child = row,
        };
        p.Click += click;
        return p;
    }

    /// <summary>A quiet secondary button: notch-black with a hairline ring that brightens on hover.</summary>
    public static Pressable Secondary(string text, Action click, double height = 30, double radius = 8, double fontSize = 12, Brush? background = null)
    {
        var p = new Pressable(background ?? Ground, TileHover, Hairline, HairlineStrong)
        {
            Height = height,
            CornerRadius = new CornerRadius(radius),
            Padding = new Thickness(12, 0, 12, 0),
            Child = Centered(Text(text, fontSize, Weight.SemiBold)),
        };
        p.Click += click;
        return p;
    }

    /// <summary>A 32 px round icon button on the tile colour (Back, the snoozed pill's "turn off").</summary>
    public static Pressable Round(string icon, Action click, double size = 32, double iconSize = 14, Brush? background = null, string? tip = null)
    {
        var p = new Pressable(background ?? Tile, TileHover, Hairline, HairlineHover)
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Child = Centered(Icon(icon, Fg, iconSize, 2.4)),
            ToolTip = tip,
        };
        p.Click += click;
        return p;
    }

    static UIElement Centered(FrameworkElement e)
    {
        e.HorizontalAlignment = HorizontalAlignment.Center;
        e.VerticalAlignment = VerticalAlignment.Center;
        return e;
    }

    /// <summary>A key cap ("Right Alt").</summary>
    public static Border KeyCap(string text, double height = 26, double fontSize = 12, FrameworkElement? icon = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null)
        {
            icon.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(icon);
        }
        row.Children.Add(Text(text, fontSize, Weight.Bold));
        bool big = height > 30;
        double pad = big ? 12 : 9;
        // The key's lower lip: a darker band along the bottom edge, inside the hairline.
        var lip = new Border { Height = big ? 3 : 2, Background = Hairline, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(-pad, 0, -pad, 0) };
        var grid = new Grid();
        grid.Children.Add(lip);
        grid.Children.Add(row);
        return new Border
        {
            Height = height,
            Padding = new Thickness(pad, 0, pad, 0),
            CornerRadius = new CornerRadius(big ? 8 : 6),
            Background = big ? Tile : Ground,
            BorderBrush = HairlineHover,
            BorderThickness = new Thickness(1),
            Child = grid,
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    // ---- Layout helpers --------------------------------------------------------------------------

    public static StackPanel Row(double gap, params UIElement[] children) => Stack(Orientation.Horizontal, gap, children);

    public static StackPanel Column(double gap, params UIElement[] children) => Stack(Orientation.Vertical, gap, children);

    static StackPanel Stack(Orientation o, double gap, UIElement[] children)
    {
        var panel = new StackPanel { Orientation = o };
        for (int i = 0; i < children.Length; i++)
        {
            if (i > 0 && children[i] is FrameworkElement fe)
            {
                var m = fe.Margin;
                fe.Margin = o == Orientation.Horizontal ? new Thickness(m.Left + gap, m.Top, m.Right, m.Bottom) : new Thickness(m.Left, m.Top + gap, m.Right, m.Bottom);
            }
            panel.Children.Add(children[i]);
        }
        return panel;
    }

    /// <summary>A soft fade-and-rise for content that just appeared (the mockup's "rise"); snaps with reduced motion.</summary>
    public static void Rise(FrameworkElement e, double delayMs = 0, double distance = 8)
    {
        if (ReducedMotion) return;
        var shift = new TranslateTransform();
        e.RenderTransform = shift;
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var end = begin + TimeSpan.FromMilliseconds(350);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        // Held hidden until its turn, then eased in; FillBehavior.Stop hands back the resting values (1, 0).
        DoubleAnimationUsingKeyFrames Track(double from, double to)
        {
            var a = new DoubleAnimationUsingKeyFrames { Duration = end, FillBehavior = FillBehavior.Stop };
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(begin)));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(end), ease));
            return a;
        }
        e.BeginAnimation(UIElement.OpacityProperty, Track(0, 1));
        shift.BeginAnimation(TranslateTransform.YProperty, Track(distance, 0));
    }
}

/// <summary>
/// A clickable surface with the wall's hover and press: background (and optional hairline ring) brighten on
/// hover, a press scales it to 0.96. Handles its own mouse so the notch behind never sees the click.
/// </summary>
internal class Pressable : Border
{
    readonly Brush _bg, _bgHover;
    readonly Brush? _ring, _ringHover;
    readonly ScaleTransform _press = new(1, 1);
    bool _down, _enabled = true;

    public event Action? Click;

    public Pressable(Brush background, Brush hover, Brush? ring = null, Brush? ringHover = null)
    {
        _bg = background;
        _bgHover = hover;
        _ring = ring;
        _ringHover = ringHover ?? ring;
        Background = background;
        if (ring is not null)
        {
            BorderBrush = ring;
            BorderThickness = new Thickness(1);
        }
        Cursor = Cursors.Hand;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _press;
        SnapsToDevicePixels = true;
        MouseEnter += (_, _) => Paint();
        MouseLeave += (_, _) =>
        {
            Paint();
            SetPressed(false);
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (!_enabled) return;
            e.Handled = true;
            _down = true;
            CaptureMouse();
            SetPressed(true);
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!_down) return;
            e.Handled = true;
            _down = false;
            ReleaseMouseCapture();
            SetPressed(false);
            if (_enabled && IsMouseOver) Click?.Invoke();
        };
        // Wheel and right-clicks still belong to whatever's behind; only left clicks are ours.
    }

    /// <summary>Overrides the resting background (a selected segment, a switched-on switch).</summary>
    public Brush? Fill { get; set; }

    /// <summary>A dimmed, unclickable state (rows that depend on a switch that's off).</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Opacity = value ? 1 : 0.4;
            Cursor = value ? Cursors.Hand : null;
        }
    }

    public double PressScale { get; set; } = 0.96;

    public void Paint()
    {
        bool hover = IsMouseOver && _enabled;
        Background = Fill ?? (hover ? _bgHover : _bg);
        if (_ring is not null) BorderBrush = hover ? _ringHover : _ring;
    }

    void SetPressed(bool down)
    {
        double s = down ? PressScale : 1;
        if (ScreenKit.ReducedMotion)
        {
            _press.ScaleX = _press.ScaleY = s;
            return;
        }
        var a = new DoubleAnimation(s, TimeSpan.FromMilliseconds(down ? 90 : 160)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        _press.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        _press.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }
}

/// <summary>The design's 38×22 switch: grey track and knob off; white track, dark knob slid right on.</summary>
internal sealed class ToggleSwitch : Pressable
{
    readonly Ellipse _knob = new() { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    readonly TranslateTransform _slide = new();
    bool _on;

    public event Action<bool>? Toggled;

    public ToggleSwitch(bool on, Action<bool>? toggled = null, string? tip = null) : base(ScreenKit.Hairline, ScreenKit.HairlineHover)
    {
        Width = 38;
        Height = 22;
        CornerRadius = new CornerRadius(11);
        PressScale = 1;
        ToolTip = tip;
        _knob.Margin = new Thickness(3, 0, 0, 0);
        _knob.RenderTransform = _slide;
        Child = _knob;
        _on = on;
        Apply(animate: false);
        if (toggled is not null) Toggled += toggled;
        Click += () =>
        {
            _on = !_on;
            Apply(animate: true);
            Toggled?.Invoke(_on);
        };
    }

    public bool IsOn
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            Apply(animate: true);
        }
    }

    void Apply(bool animate)
    {
        Fill = _on ? ScreenKit.Fg : null;
        _knob.Fill = _on ? ScreenKit.Ground : ScreenKit.Fg2;
        Paint();
        double x = _on ? 16 : 0;
        if (!animate || ScreenKit.ReducedMotion)
        {
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _slide.X = x;
            return;
        }
        // The mockup's cubic-bezier(.34,1.56,.64,1): a quick slide with a little overshoot.
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut },
        });
    }
}

/// <summary>A segmented choice: black well with a hairline, the chosen segment a raised grey with white text.</summary>
internal sealed class Segmented : Border
{
    readonly List<(Pressable Button, TextBlock Text)> _items = new();
    readonly Action<int> _chosen;
    int _index;

    public Segmented(IReadOnlyList<string> labels, int index, Action<int> chosen, double minWidth = 64, double height = 26,
        double fontSize = 12, Func<int, FrameworkElement?>? icon = null, bool stretch = false)
    {
        _chosen = chosen;
        _index = index;
        Background = ScreenKit.Ground;
        BorderBrush = ScreenKit.Hairline;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(height > 28 ? 12 : 10);
        Padding = new Thickness(2);
        var grid = new Grid();
        for (int i = 0; i < labels.Count; i++)
        {
            int at = i;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = stretch ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            var text = ScreenKit.Text(labels[i], fontSize, Weight.SemiBold);
            var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (icon?.Invoke(i) is { } ic)
            {
                ic.Margin = new Thickness(0, 0, 6, 0);
                ic.VerticalAlignment = VerticalAlignment.Center;
                content.Children.Add(ic);
            }
            content.Children.Add(text);
            var button = new Pressable(Brushes.Transparent, Brushes.Transparent)
            {
                Height = height,
                MinWidth = minWidth,
                CornerRadius = new CornerRadius(height > 28 ? 9 : 7),
                Padding = new Thickness(10, 0, 10, 0),
                Child = content,
                PressScale = 0.97,
            };
            button.MouseEnter += (_, _) => Paint();
            button.MouseLeave += (_, _) => Paint();
            button.Click += () =>
            {
                if (_index == at) return;
                _index = at;
                Paint();
                _chosen(at);
            };
            Grid.SetColumn(button, i);
            grid.Children.Add(button);
            _items.Add((button, text));
        }
        Child = grid;
        Paint();
    }

    public int Index
    {
        get => _index;
        set
        {
            _index = value;
            Paint();
        }
    }

    void Paint()
    {
        for (int i = 0; i < _items.Count; i++)
        {
            var (button, text) = _items[i];
            bool on = i == _index;
            button.Fill = on ? ScreenKit.Hairline : Brushes.Transparent;
            button.Paint();
            text.Foreground = on || button.IsMouseOver ? ScreenKit.Fg : ScreenKit.Fg2;
        }
    }
}

/// <summary>A −/value/+ stepper in a black well (pomodoro minutes, the alarm editor's hour and minute).</summary>
internal sealed class Stepper : Border
{
    readonly TextBlock _value;
    readonly Func<int, string> _format;
    readonly int _min, _max, _step;
    readonly bool _wrap;
    int _v;

    public event Action<int>? Changed;

    public Stepper(int value, int min, int max, int step, Action<int>? changed = null, Func<int, string>? format = null,
        double valueWidth = 26, double fontSize = 13, double height = 30, bool wrap = false, string? less = null, string? more = null)
    {
        _min = min;
        _max = max;
        _step = step;
        _wrap = wrap;
        _format = format ?? (v => v.ToString(CultureInfo.InvariantCulture));
        _v = value;
        if (changed is not null) Changed += changed;
        Background = ScreenKit.Ground;
        BorderBrush = ScreenKit.Hairline;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Height = height;
        _value = ScreenKit.Text(_format(value), fontSize, Weight.Bold);
        _value.MinWidth = valueWidth;
        _value.TextAlignment = TextAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Step("−", -1, less));
        row.Children.Add(_value);
        row.Children.Add(Step("+", +1, more));
        Child = row;
        // The wheel over a stepper nudges it, like a time picker.
        MouseWheel += (_, e) =>
        {
            e.Handled = true;
            Bump((e.Delta > 0 ? 1 : -1) * (WheelStep > 0 ? WheelStep : _step));
        };
    }

    public int Value => _v;

    /// <summary>What one wheel notch moves (the buttons move the step); 0 = the step.</summary>
    public int WheelStep { get; set; }

    Pressable Step(string glyph, int dir, string? tip)
    {
        var p = new Pressable(Brushes.Transparent, ScreenKit.TileHover)
        {
            Width = 28,
            CornerRadius = new CornerRadius(7),
            Child = new TextBlock
            {
                Text = glyph,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = ScreenKit.Fg,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = ScreenKit.Font(Weight.SemiBold).Family,
            },
            ToolTip = tip,
        };
        p.Click += () => Bump(dir * _step);
        return p;
    }

    void Bump(int delta)
    {
        int next = _v + delta;
        if (_wrap)
        {
            int span = _max - _min + 1;
            next = ((next - _min) % span + span) % span + _min;
        }
        else next = Math.Clamp(next, _min, _max);
        if (next == _v) return;
        _v = next;
        _value.Text = _format(_v);
        Changed?.Invoke(_v);
    }
}

/// <summary>A round day toggle (M T W T F S S): filled white when on.</summary>
internal sealed class DayToggle : Pressable
{
    readonly TextBlock _letter;
    bool _on;

    public DayToggle(string letter, bool on, Action<bool> toggled, string? tip = null, double size = 24)
        : base(Brushes.Transparent, Brushes.Transparent, ScreenKit.Hairline, ScreenKit.HairlineStrong)
    {
        Width = Height = size;
        CornerRadius = new CornerRadius(size / 2);
        ToolTip = tip;
        _letter = ScreenKit.Text(letter, 11, Weight.SemiBold);
        _letter.HorizontalAlignment = HorizontalAlignment.Center;
        _letter.TextAlignment = TextAlignment.Center;
        Child = _letter;
        _on = on;
        PaintDay();
        MouseEnter += (_, _) => PaintDay();
        MouseLeave += (_, _) => PaintDay();
        Click += () =>
        {
            _on = !_on;
            PaintDay();
            toggled(_on);
        };
    }

    void PaintDay()
    {
        Fill = _on ? ScreenKit.Fg : null;
        Paint();
        if (_on) BorderBrush = ScreenKit.Fg;
        var (family, weight) = ScreenKit.Font(_on ? Weight.Bold : Weight.SemiBold);
        _letter.FontFamily = family;
        _letter.FontWeight = weight;
        _letter.Foreground = _on ? ScreenKit.Ground : IsMouseOver ? ScreenKit.Fg : ScreenKit.Fg2;
    }
}

/// <summary>An 18 px checkbox: a hairline square, filled white with a dark tick when on.</summary>
internal sealed class CheckBox18 : Pressable
{
    bool _on;

    public CheckBox18(bool on, Action<bool> toggled) : base(Brushes.Transparent, Brushes.Transparent, ScreenKit.HairlineStrong, ScreenKit.Fg2)
    {
        Width = Height = 18;
        CornerRadius = new CornerRadius(5);
        BorderThickness = new Thickness(1.5);
        _on = on;
        PaintBox();
        Click += () =>
        {
            _on = !_on;
            PaintBox();
            toggled(_on);
        };
    }

    void PaintBox()
    {
        Fill = _on ? ScreenKit.Fg : null;
        Paint();
        Child = _on ? ScreenKit.Icon(ScreenKit.Paths.Check, ScreenKit.Ground, 11, 3.4) : null;
    }
}

/// <summary>A thin slider: grey track, white fill and knob; drag or click anywhere on it, wheel nudges.</summary>
internal sealed class SlimSlider : Grid
{
    readonly Border _fill = new() { Height = 4, CornerRadius = new CornerRadius(2), Background = ScreenKit.Fg, HorizontalAlignment = HorizontalAlignment.Left };
    readonly Ellipse _knob = new() { Width = 14, Height = 14, Fill = ScreenKit.Fg, HorizontalAlignment = HorizontalAlignment.Left };
    readonly int _min, _max, _step;
    int _v;

    public event Action<int>? Changed;

    /// <summary>Raised when a drag ends (or after a click / wheel step): the moment to save.</summary>
    public event Action<int>? Committed;

    public SlimSlider(int value, int min, int max, int step, double width = 180)
    {
        _min = min;
        _max = max;
        _step = step;
        _v = Math.Clamp(value, min, max);
        Width = width;
        Height = 22;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = ScreenKit.Hairline });
        Children.Add(_fill);
        Children.Add(_knob);
        SizeChanged += (_, _) => Layout();
        MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            CaptureMouse();
            SetFrom(e.GetPosition(this).X);
        };
        MouseMove += (_, e) =>
        {
            if (IsMouseCaptured) SetFrom(e.GetPosition(this).X);
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!IsMouseCaptured) return;
            e.Handled = true;
            ReleaseMouseCapture();
            Committed?.Invoke(_v);
        };
        MouseWheel += (_, e) =>
        {
            e.Handled = true;
            Set(_v + (e.Delta > 0 ? _step : -_step));
            Committed?.Invoke(_v);
        };
    }

    public int Value => _v;

    void SetFrom(double x)
    {
        double usable = Math.Max(1, ActualWidth - 14);
        double t = Math.Clamp((x - 7) / usable, 0, 1);
        int raw = _min + (int)Math.Round(t * (_max - _min) / _step) * _step;
        Set(raw);
    }

    void Set(int v)
    {
        v = Math.Clamp(v, _min, _max);
        if (v == _v) return;
        _v = v;
        Layout();
        Changed?.Invoke(v);
    }

    void Layout()
    {
        double usable = Math.Max(0, ActualWidth - 14);
        double t = _max == _min ? 0 : (double)(_v - _min) / (_max - _min);
        _knob.Margin = new Thickness(t * usable, 0, 0, 0);
        _fill.Width = t * usable + 7;
    }
}

/// <summary>
/// Text drawn glyph by glyph so it can carry the design's letter-spacing (tracked labels at +0.12 em, big
/// numbers at −0.03 to −0.05 em) and tabular digits. Cheap: a handful of FormattedText runs, re-made only
/// when the text or style changes.
/// </summary>
internal sealed class SpacedText : FrameworkElement
{
    string _text = "";
    double _size = 13, _tracking;
    Weight _weight = Weight.Regular;
    Brush _brush = Brushes.White;
    List<(FormattedText Text, double X)>? _runs;
    double _width, _height, _baseline;

    public SpacedText()
    {
        IsHitTestVisible = false;
        VerticalAlignment = VerticalAlignment.Center;
        SnapsToDevicePixels = true;
    }

    public string Text { get => _text; set { _text = value ?? ""; Invalidate(); } }
    public double FontSize { get => _size; set { _size = value; Invalidate(); } }
    public Weight Weight { get => _weight; set { _weight = value; Invalidate(); } }
    public Brush Foreground { get => _brush; set { _brush = value; _runs = null; InvalidateVisual(); } }

    /// <summary>Letter-spacing in em.</summary>
    public double Tracking { get => _tracking; set { _tracking = value; Invalidate(); } }

    /// <summary>Equal-width digits for live values (a countdown) so they never jiggle; off for static text.</summary>
    public bool Tabular { get; set; }

    /// <summary>Distance from the top to the baseline, for lining up a big number with its small suffix.</summary>
    public double Baseline
    {
        get
        {
            Build();
            return _baseline;
        }
    }

    void Invalidate()
    {
        _runs = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    void Build()
    {
        if (_runs is not null) return;
        var (family, weight) = ScreenKit.Font(_weight);
        var face = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Make(string s) => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, _size, _brush, dip);

        double digit = 0;
        if (Tabular && _text.Any(char.IsAsciiDigit))
            for (char c = '0'; c <= '9'; c++) digit = Math.Max(digit, Make(c.ToString()).WidthIncludingTrailingWhitespace);
        double space = _tracking * _size;
        var runs = new List<(FormattedText, double)>();
        double x = 0;
        for (int i = 0; i < _text.Length; i++)
        {
            var ft = Make(_text[i].ToString());
            double w = ft.WidthIncludingTrailingWhitespace;
            double cell = Tabular && char.IsAsciiDigit(_text[i]) ? digit : w;
            runs.Add((ft, x + (cell - w) / 2));
            x += cell + (i < _text.Length - 1 ? space : 0);
        }
        var whole = Make(_text.Length > 0 ? _text : " ");
        _height = whole.Height;
        _baseline = whole.Baseline;
        _width = Math.Max(0, x);
        _runs = runs;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Build();
        return new Size(Math.Ceiling(_width), Math.Ceiling(_height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        Build();
        foreach (var (text, x) in _runs!) dc.DrawText(text, new Point(x, 0));
    }
}
