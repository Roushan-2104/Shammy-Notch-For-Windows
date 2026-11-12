using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Shammy.Views.Cards;

/// <summary>
/// The one card kit: Focus Wall (see FOCUSWALL / Wall.dc.html). A deep neutral ground, tiles that are artwork
/// fields, white type in Schibsted Grotesk (big tight posters, small tracked uppercase labels, tabular figures)
/// and ONE accent, used only for focus and the single primary control. Every card, tile and detail is built
/// from these pieces in code, so nothing costs anything while it isn't on screen. (Shammy.Modules.Kit.CardKit
/// is a thin older facade over the same tokens for views that haven't moved yet.)
/// </summary>
internal static class CardKit
{
    // ---- Tokens ----------------------------------------------------------------------------------

    public static Brush Hex(string hex) => Ui.Tokens.Brush(hex);

    public static Color Color(string hex) => Ui.Tokens.Color(hex);

    public static Brush Res(string key) => Ui.Tokens.Res(key);

    /// <summary>The notch ground, a tile's surface, the hairline, the two text colours and the accent.</summary>
    public static readonly Brush Ground = Ui.Tokens.Ground, Surface = Ui.Tokens.Tile, Hairline = Ui.Tokens.Hairline,
        Ink = Ui.Tokens.Text, Ink2 = Ui.Tokens.Text2, OnAccent = Ui.Tokens.OnAccent;

    /// <summary>The accent the user picked (orange by default); it changes live, see <see cref="Theme.AccentTheme"/>.</summary>
    public static Brush Accent => Theme.AccentTheme.Brush;

    public static Brush AccentHover => Theme.AccentTheme.Hover;

    /// <summary>Meaning colours (never decoration): red for recording, muted, alarms and anything wrong.</summary>
    public static readonly Brush Alarm = Ui.Tokens.Alarm;

    /// <summary>Quiet controls: a translucent white circle over artwork (Wall's .ib) and a dark pill with a hairline (.qb).</summary>
    public static readonly Brush QuietBg = Ui.Tokens.QuietBg, QuietHover = Ui.Tokens.QuietHover, PillBg = Ui.Tokens.Pill, PillHover = Ui.Tokens.PillHover;

    /// <summary>Tracks under meters, rings and scrubbers, and the hairline between list rows.</summary>
    public static readonly Brush TrackBg = Ui.Tokens.Track, RingTrack = Ui.Tokens.RingTrack, Divider = Hairline;

    /// <summary>Older names some views still use.</summary>
    public static readonly Brush ControlBg = PillBg, ControlHover = PillHover, RowBg = Ui.Tokens.Row;

    public static readonly Thickness CompactPadding = new(12), DetailPadding = new(0);

    // ---- Type ------------------------------------------------------------------------------------

    /// <summary>Schibsted Grotesk at Regular/Bold. Medium, SemiBold and ExtraBold are their own embedded families:
    /// use <see cref="Face"/> (asking this family for SemiBold renders Regular).</summary>
    public static FontFamily Font => Ui.Tokens.FontRes("WallFont");

    /// <summary>The embedded cut for a weight (see <see cref="WallParts.Family"/>).</summary>
    public static FontFamily Face(FontWeight weight) => WallParts.Family(weight);

    static FontFamily IconFont => Ui.Tokens.FontRes("IconFont");

    /// <summary>
    /// Body text: white, or the secondary grey (≥ 4.5:1 on tiles). Proportional figures (this face's tabular set
    /// widens ":" and "."); live numbers that tick use <see cref="Big"/>, which keeps only the digits even.
    /// </summary>
    public static TextBlock Text(string text, double size, FontWeight weight, bool secondary = false) => new()
    {
        Text = text,
        FontFamily = Face(weight),
        FontSize = size,
        FontWeight = weight,
        Foreground = secondary ? Ink2 : Ink,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A run inside a mixed TextBlock, on the right cut for its weight.</summary>
    public static System.Windows.Documents.Run Run(string text, FontWeight weight, Brush brush) =>
        new(text) { FontFamily = Face(weight), FontWeight = weight, Foreground = brush };

    /// <summary>The small tracked uppercase label (11 px Bold, +0.12 em), white unless given a brush.</summary>
    public static TrackedText Label(string text, Brush? brush = null, double size = 11) => new()
    {
        Text = text,
        Uppercase = true,
        FontSize = size,
        FontFamily = Face(FontWeights.Bold),
        FontWeight = FontWeights.Bold,
        Tracking = 0.12,
        LineFactor = 1.25,
        Foreground = brush ?? Ink,
    };

    /// <summary>The section label (the kit's one style): 11 px Bold, uppercase, +0.12 em, secondary grey.</summary>
    public static TrackedText SectionLabel(string text) => Label(text, Ink2);

    /// <summary>
    /// An empty list's words, the same everywhere: what isn't there (13.5 SemiBold, white) and, under it, how it
    /// gets there (12, grey). Screen readers hear both.
    /// </summary>
    public static FrameworkElement Empty(string title, string? hint = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var head = Text(title, 13.5, FontWeights.SemiBold);
        head.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(head);
        if (!string.IsNullOrEmpty(hint))
        {
            var line = Text(hint, 12, FontWeights.Normal, secondary: true);
            line.HorizontalAlignment = HorizontalAlignment.Left;
            line.Margin = new Thickness(0, 3, 0, 0);
            panel.Children.Add(line);
        }
        AutomationProperties.SetName(panel, string.IsNullOrEmpty(hint) ? title : $"{title}. {hint}");
        return panel;
    }

    /// <summary>A poster: the big tight ExtraBold title or number (−0.035 em, line-height about 1), even digits.</summary>
    public static TrackedText Poster(string text, double size) => new()
    {
        Text = text,
        Uppercase = false,
        FontSize = size,
        FontFamily = Face(FontWeights.ExtraBold),
        FontWeight = FontWeights.ExtraBold,
        Tracking = -0.035,
        LineFactor = 1.0,
        TabularDigits = true,
        Foreground = Ink,
    };

    /// <summary>A big number that isn't a poster (ring centres, meters): ExtraBold, a little less tight.</summary>
    public static TrackedText Big(string text, double size)
    {
        var t = Poster(text, size);
        t.Tracking = -0.02;
        t.LineFactor = 1.15;
        return t;
    }

    /// <summary>A tile or detail title: 15–17 px Bold, a hair tight.</summary>
    public static TextBlock Title(string text, double size = 15) => Text(text, size, FontWeights.Bold);

    // ---- Icons -----------------------------------------------------------------------------------

    /// <summary>The mockup's icons (24×24 viewBox). Filled glyphs for media, stroked ones for everything else.</summary>
    public static class Paths
    {
        public const string Play = "M7 4 20 12 7 20Z";
        public const string Pause = "M6.5 4h3a1.5 1.5 0 0 1 1.5 1.5v13a1.5 1.5 0 0 1-1.5 1.5h-3A1.5 1.5 0 0 1 5 18.5v-13A1.5 1.5 0 0 1 6.5 4Z M15.5 4h3a1.5 1.5 0 0 1 1.5 1.5v13a1.5 1.5 0 0 1-1.5 1.5h-3a1.5 1.5 0 0 1-1.5-1.5v-13A1.5 1.5 0 0 1 15.5 4Z";
        public const string Next = "M5 5 15 12 5 19Z M17 5h1a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1h-1a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1Z";
        public const string Previous = "M6 5h1a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1Z M19 5 9 12 19 19Z";
        public const string Stop = "M7 4h10a3 3 0 0 1 3 3v10a3 3 0 0 1-3 3H7a3 3 0 0 1-3-3V7a3 3 0 0 1 3-3Z";
        // Stroked.
        public const string Open = "M7 17 17 7 M9 7h8v8";
        public const string Back = "M15 5l-7 7 7 7";
        public const string Search = "M11 4a7 7 0 1 1 0 14a7 7 0 1 1 0-14z M20 20l-3.5-3.5";
        public const string Plus = "M12 5v14 M5 12h14";
        public const string Close = "M6 6l12 12 M18 6 6 18";
        public const string Check = "M5 12.5l4.5 4.5L19 7.5";
        public const string Volume = "M11 5 6 9H3v6h3l5 4V5z M16 9.5a3.5 3.5 0 0 1 0 5";
        public const string Bolt = "M13 2L4 14h7l-1 8 9-12h-7z";
        public const string Laptop = "M4 5h16v11H4z M2 19h20";
        public const string Bluetooth = "M7 7l10 10-5 5V2l5 5L7 17";
        public const string Stopwatch = "M12 7a7 7 0 1 1 0 14a7 7 0 1 1 0-14z M12 11v3 M10 3h4 M18 6l1.5-1.5";
        public const string Timer = "M12 5a8 8 0 1 1 0 16a8 8 0 1 1 0-16z M12 9v4l2.5 2 M9 2h6";
        public const string Chip = "M12 3a9 9 0 1 1 0 18a9 9 0 1 1 0-18z M12 8.5a3.5 3.5 0 1 1 0 7a3.5 3.5 0 1 1 0-7z";
        public const string Pin = "M9 4h6l-1 6 3 3v2H7v-2l3-3z M12 15v5";
        public const string Copy = "M9 9h10v10H9z M5 15V5h10";
        public const string Link = "M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1 M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1";
        public const string Note = "M6 4h9l3 3v13H6z M9 11h6 M9 15h6";
        public const string Flip = "M12 3v18 M8 7 4 12l4 5 M16 7l4 5-4 5";
        public const string Minus = "M5 12h14";
        public const string Camera = "M4 7h3l2-2h6l2 2h3v12H4z M12 10a3 3 0 1 1 0 6a3 3 0 1 1 0-6z";
        public const string Video = "M3 7h12v10H3z M15 10l6-3v10l-6-3";
        public const string Folder = "M3 6h6l2 2h10v11H3z";
        public const string Bell = "M6 16V11a6 6 0 0 1 12 0v5l2 2H4z M10 20h4";
    }

    /// <summary>A stroked 24×24 icon (the mockup's inline SVGs), scaled to <paramref name="size"/>.</summary>
    public static FrameworkElement Icon(string data, Brush stroke, double size = 16, double strokeWidth = 2, Brush? fill = null)
    {
        var path = new System.Windows.Shapes.Path
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
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false };
    }

    /// <summary>A filled 24×24 glyph (play, pause, next…).</summary>
    public static FrameworkElement Glyph(string data, Brush fill, double size = 14) => Icon(data, Brushes.Transparent, size, 0, fill);

    /// <summary>A Segoe Fluent glyph, for the few places the system's own symbols read best (weather kinds come from WeatherIcon).</summary>
    public static TextBlock FluentGlyph(string glyph, double size, Brush? brush = null) => new()
    {
        Text = glyph,
        FontFamily = IconFont,
        FontSize = size,
        Foreground = brush ?? Ink,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The play/pause glyph for a primary control.</summary>
    public static FrameworkElement PlayPause(bool playing, double size) => Glyph(playing ? Paths.Pause : Paths.Play, OnAccent, size);

    // ---- Controls --------------------------------------------------------------------------------

    /// <summary>
    /// THE primary control (one per tile, peek or detail): an accent circle with a dark glyph. Real buttons,
    /// named for screen readers, that don't open the tile they sit on.
    /// </summary>
    public static Button Primary(string name, FrameworkElement icon, Action onClick, double size = 28) =>
        Make(name, icon, onClick, size, size, size / 2, Accent, AccentHover, null);

    /// <summary>A primary text control ("Join", "Pause"): an accent pill.</summary>
    public static Button PrimaryPill(string text, string name, Action onClick, double height = 28, double fontSize = 12)
    {
        var label = Text(text, fontSize, FontWeights.Bold);
        label.Foreground = OnAccent;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var b = Make(name, label, onClick, double.NaN, height, height / 2, Accent, AccentHover, null);
        ((Border)b.Content).Padding = new Thickness(Math.Round(height * 0.46), 0, Math.Round(height * 0.46), 0);
        return b;
    }

    /// <summary>A quiet round control (Wall's .ib): translucent white, for secondary actions on artwork or a detail.</summary>
    public static Button Quiet(string name, FrameworkElement icon, Action onClick, double size = 28) =>
        Make(name, icon, onClick, size, size, size / 2, QuietBg, QuietHover, null);

    /// <summary>A quiet text pill (Wall's .qb): "+1 min", "Lyrics", "Clear".</summary>
    public static Button Pill(string text, string name, Action onClick, double height = 32, double fontSize = 12)
    {
        var label = Text(text, fontSize, FontWeights.SemiBold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var b = Make(name, label, onClick, double.NaN, height, height / 2, PillBg, PillHover, Hairline);
        ((Border)b.Content).Padding = new Thickness(13, 0, 13, 0);
        return b;
    }

    /// <summary>THE Back control (32 px): a dark translucent circle with a chevron, over artwork or on the ground.</summary>
    public static Button Back(Action onClick, string name = "Back") =>
        Make(name, Icon(Paths.Back, Ink, 15, 2.4), onClick, Ui.Tokens.BackSize, Ui.Tokens.BackSize, Ui.Tokens.BackSize / 2, Hex("#9E0B0B0D"), Hex("#C40B0B0D"), null);

    /// <summary>Older name for <see cref="Back"/>.</summary>
    public static Button BackButton(Action onClick) => Back(onClick);

    /// <summary>
    /// THE close (×) control of pop-up cards and list rows (24 px, quiet translucent circle). <paramref name="name"/>
    /// says what it closes or removes ("Close", "Delete :sig").
    /// </summary>
    public static Button CloseButton(Action onClick, string name = "Close") =>
        Make(name, Icon(Paths.Close, Ink, 10, 2.4), onClick, Ui.Tokens.CloseSize, Ui.Tokens.CloseSize, Ui.Tokens.CloseSize / 2, QuietBg, QuietHover, null);

    /// <summary>Older call shape: a 36 px round control; with an accent it is the primary one.</summary>
    public static Button RoundButton(string name, FrameworkElement icon, Action onClick, Brush? accent = null, double size = 36) =>
        accent is null ? Quiet(name, icon, onClick, size) : Primary(name, Recolour(icon, OnAccent), onClick, size);

    /// <summary>Older call shape: a text chip in the quiet-pill style.</summary>
    public static Button Chip(string text, string name, Action onClick, double height = 32, double fontSize = 12, double radius = 16) =>
        Pill(text, name, onClick, height, fontSize);

    static FrameworkElement Recolour(FrameworkElement icon, Brush brush)
    {
        if (icon is Viewbox { Child: Canvas { Children.Count: > 0 } c } && c.Children[0] is System.Windows.Shapes.Path p)
        {
            if (p.Fill is not null && p.Fill != Brushes.Transparent) p.Fill = brush;
            if (p.StrokeThickness > 0) p.Stroke = brush;
        }
        return icon;
    }

    static Button Make(string name, UIElement content, Action onClick, double width, double height, double radius, Brush bg, Brush hover, Brush? stroke)
    {
        var face = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(radius),
            Background = bg,
            BorderBrush = stroke,
            BorderThickness = new Thickness(stroke is null ? 0 : 1),
            Child = content,
            SnapsToDevicePixels = true,
        };
        if (!double.IsNaN(width)) face.Width = width;
        if (content is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Center;
            fe.VerticalAlignment = VerticalAlignment.Center;
        }
        var button = new Button
        {
            Content = face,
            Height = height,
            Cursor = Cursors.Hand,
            Template = BareTemplate,
            ToolTip = name,
        };
        if (!double.IsNaN(width)) button.Width = width;
        AutomationProperties.SetName(button, name);
        // A tab stop with the accent ring once the notch has the keyboard; the shared press.
        Ui.FocusKit.Button(button);
        Ui.Press.Attach(button);
        button.MouseEnter += (_, _) => face.Background = hover;
        button.MouseLeave += (_, _) => face.Background = bg;
        button.Click += (_, e) =>
        {
            onClick();
            e.Handled = true;
        };
        return button;
    }

    /// <summary>A button template that is just its content (the face draws hover itself).</summary>
    static readonly ControlTemplate BareTemplate = MakeBareTemplate();

    static ControlTemplate MakeBareTemplate()
    {
        var t = new ControlTemplate(typeof(ButtonBase));
        t.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        t.Seal();
        return t;
    }

    // ---- Shapes ----------------------------------------------------------------------------------

    /// <summary>A rounded bar (charts, meters).</summary>
    public static Border Bar(Brush fill, double width, double height, double radius) => new()
    {
        Width = width,
        Height = height,
        CornerRadius = new CornerRadius(radius),
        Background = fill,
    };

    /// <summary>A tall meter (the system tile's battery and earbuds): a translucent well filled from the bottom.</summary>
    public static Grid Meter(double fraction, double width, double height, double radius)
    {
        var g = new Grid { Width = width, Height = height, ClipToBounds = true };
        g.Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
        g.Children.Add(new Border { Background = Hex("#1FF5F5F7") });
        g.Children.Add(new Border
        {
            Height = Math.Round(height * Math.Clamp(fraction, 0, 1)),
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Hex("#D1F5F5F7"),
        });
        return g;
    }

    /// <summary>A thin progress line (scrubbers, quiet progress): track + white fill.</summary>
    public static Grid Progress(double fraction, Brush? fill = null, double height = 3, Brush? track = null)
    {
        var g = new Grid { Height = height, VerticalAlignment = VerticalAlignment.Center };
        double f = double.IsNaN(fraction) ? 0 : Math.Clamp(fraction, 0, 1);
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(f, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - f, GridUnitType.Star) });
        var t = new Border { CornerRadius = new CornerRadius(height / 2), Background = track ?? TrackBg };
        Grid.SetColumnSpan(t, 2);
        g.Children.Add(t);
        if (f > 0) g.Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = fill ?? Ink });
        return g;
    }

    public static Ellipse Dot(double size, Brush fill) => new() { Width = size, Height = size, Fill = fill, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A hairline between list rows.</summary>
    public static Border Rule() => new() { Height = 1, Background = Hairline, SnapsToDevicePixels = true };

    /// <summary>A rounded #16161A panel with a hairline (a section inside a detail).</summary>
    public static Border Section(UIElement child, double padding = 14) => new()
    {
        Background = Surface,
        BorderBrush = Hairline,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Ui.Tokens.DetailRadius),
        Padding = new Thickness(padding),
        Child = child,
    };

    /// <summary>A whole detail that isn't a <see cref="WallDetail"/> (it fills what the notch gives it).</summary>
    public static Border DetailPanel(UIElement child) => new() { Padding = DetailPadding, Child = child };

    /// <summary>A hairline column divider with an 18 px gutter after it.</summary>
    public static Border VerticalDivider() => new() { Width = 1, Background = Hairline, Margin = new Thickness(18, 0, 18, 0) };

    /// <summary>A soft static glow in a colour: a radial gradient, no blur effect to render.</summary>
    public static Ellipse Glow(Color color, double size, double strength)
    {
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb((byte)(255 * strength), color.R, color.G, color.B), 0));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, color.R, color.G, color.B), 1));
        brush.Freeze();
        return new Ellipse { Width = size, Height = size, Fill = brush, IsHitTestVisible = false };
    }

    /// <summary>Depth with an offset and a soft blur (never a zero-offset halo).</summary>
    public static Effect Shadow(double blur = 24, double depth = 8, double opacity = 0.45) =>
        new DropShadowEffect { Color = Colors.Black, BlurRadius = blur, ShadowDepth = depth, Direction = 270, Opacity = opacity, RenderingBias = RenderingBias.Performance };

    /// <summary>Older call shape; the colour is ignored (Focus Wall has no coloured glows outside artwork).</summary>
    public static Effect Shadow(Color color, double blur, double opacity) => Shadow(blur, blur / 3, opacity * 0.8);

    // ---- Layout ----------------------------------------------------------------------------------

    /// <summary>Lays children out left to right with a fixed gap.</summary>
    public static StackPanel Row(double gap, params UIElement[] children) => Stack(Orientation.Horizontal, gap, children);

    /// <summary>Lays children out top to bottom with a fixed gap.</summary>
    public static StackPanel Column(double gap, params UIElement[] children) => Stack(Orientation.Vertical, gap, children);

    static StackPanel Stack(Orientation orientation, double gap, UIElement[] children)
    {
        var panel = new StackPanel { Orientation = orientation };
        for (int i = 0; i < children.Length; i++)
        {
            if (i > 0 && children[i] is FrameworkElement fe)
            {
                var m = fe.Margin;
                fe.Margin = orientation == Orientation.Horizontal ? new Thickness(m.Left + gap, m.Top, m.Right, m.Bottom) : new Thickness(m.Left, m.Top + gap, m.Right, m.Bottom);
            }
            panel.Children.Add(children[i]);
        }
        return panel;
    }

    /// <summary>Places <paramref name="child"/> in a corner of a Grid cell with a margin (the mockup's absolute positions).</summary>
    public static T At<T>(T child, HorizontalAlignment h, VerticalAlignment v, double left = 0, double top = 0, double right = 0, double bottom = 0) where T : FrameworkElement
    {
        child.HorizontalAlignment = h;
        child.VerticalAlignment = v;
        child.Margin = new Thickness(left, top, right, bottom);
        return child;
    }

    /// <summary>Calls <paramref name="tick"/> every <paramref name="interval"/> while <paramref name="element"/> is on screen, and never otherwise.</summary>
    public static void TickWhileVisible(FrameworkElement element, TimeSpan interval, Action tick)
    {
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = interval };
        timer.Tick += (_, _) => tick();
        element.IsVisibleChanged += (_, _) =>
        {
            if (element.IsVisible)
            {
                tick();
                timer.Start();
            }
            else timer.Stop();
        };
        element.Unloaded += (_, _) => timer.Stop();
    }

    /// <summary>Whether to write times on a 24-hour clock (the Use24Hour setting, else the Windows region).</summary>
    public static bool H24 => Shammy.Core.Productivity.ClockPreference.Is24Hour;

    public static void Name(DependencyObject d, string? name)
    {
        if (!string.IsNullOrEmpty(name)) AutomationProperties.SetName(d, name);
    }

    /// <summary>Takes an element out of whatever it was in, so it can be shown somewhere else.</summary>
    public static void Detach(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case Panel p: p.Children.Remove(element); break;
            case Decorator d: d.Child = null; break;
            case ContentControl c: c.Content = null; break;
        }
    }
}

/// <summary>
/// A progress ring: a quiet track and a round-capped arc from 12 o'clock, clockwise (white by default: on the
/// Focus Wall the ring is the timer's artwork, not an accent). Drawn directly, so a new value is one cheap re-render.
/// </summary>
internal sealed class Ring : FrameworkElement
{
    double _value;
    Brush _fill = CardKit.Ink;
    double _thickness = 6;

    public double Value
    {
        get => _value;
        set
        {
            var v = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
            if (Math.Abs(v - _value) < 0.0005) return;
            _value = v;
            InvalidateVisual();
        }
    }

    public Brush Fill
    {
        get => _fill;
        set
        {
            _fill = value;
            InvalidateVisual();
        }
    }

    public double Thickness
    {
        get => _thickness;
        set
        {
            _thickness = value;
            InvalidateVisual();
        }
    }

    public Brush Track { get; set; } = CardKit.Hex("#2EF5F5F7");

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        double r = (size - _thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(Track, _thickness), c, r, r);
        if (_value <= 0) return;
        var pen = new Pen(_fill, _thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
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
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>
/// A progress track with a white fill and an optional knob (the music scrubber, the volume slider). Click or
/// drag to set; with the keyboard, Left/Right step by <see cref="KeyStep"/> and Home/End jump to the ends.
/// <see cref="Changed"/> reports the new fraction.
/// </summary>
internal sealed class CardTrack : FrameworkElement
{
    double _value;
    bool _dragging;

    public CardTrack()
    {
        Cursor = Cursors.Hand;
        Ui.FocusKit.Focusable(this);
        FocusVisualStyle = null; // the knob wears the ring (see OnRender) rather than a pill around the whole track
        IsKeyboardFocusedChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>How far one Left/Right press moves it (Page Up/Down move four times as far).</summary>
    public double KeyStep { get; init; } = 0.05;

    public double TrackHeight { get; init; } = 4;
    public double KnobSize { get; init; } = 8;
    public Brush Fill { get; set; } = CardKit.Ink;
    public Brush Track { get; set; } = CardKit.TrackBg;
    public Brush KnobHalo { get; set; } = Brushes.Transparent;
    public bool Interactive { get; init; } = true;

    /// <summary>Raised while dragging and on click with the new value, 0..1.</summary>
    public event Action<double>? Changed;

    /// <summary>Raised when a drag or click ends with the final value, 0..1.</summary>
    public event Action<double>? Committed;

    public bool IsDragging => _dragging;

    public double Value
    {
        get => _value;
        set
        {
            if (_dragging) return; // the pointer owns it until release
            var v = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
            if (Math.Abs(v - _value) < 0.0005) return;
            _value = v;
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size available) => new(0, Math.Max(TrackHeight, KnobSize) + 8);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0) return;
        // Transparent hit area over the full height, so the thin track is easy to grab.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        double y = (h - TrackHeight) / 2, rad = TrackHeight / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, y, w, TrackHeight), rad, rad);
        double x = w * _value;
        if (x > 0) dc.DrawRoundedRectangle(Fill, null, new Rect(0, y, Math.Max(TrackHeight, x), TrackHeight), rad, rad);
        var knob = new Point(Math.Clamp(x, Math.Max(KnobSize, 6) / 2, w - Math.Max(KnobSize, 6) / 2), h / 2);
        if (KnobSize > 0)
        {
            if (KnobHalo != Brushes.Transparent) dc.DrawEllipse(KnobHalo, null, knob, KnobSize / 2 + 3, KnobSize / 2 + 3);
            dc.DrawEllipse(CardKit.Ink, null, knob, KnobSize / 2, KnobSize / 2);
        }
        // Reached with the keyboard: the accent ring around the knob (or where it would be).
        if (IsKeyboardFocused && Interactive && InputManager.Current.MostRecentInputDevice is KeyboardDevice)
        {
            double r = Math.Max(KnobSize, 6) / 2 + 3;
            dc.DrawEllipse(null, new Pen(Ui.Tokens.Accent, 1.5), knob, r, r);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!Interactive) return;
        double? to = e.Key switch
        {
            Key.Left or Key.Down => _value - KeyStep,
            Key.Right or Key.Up => _value + KeyStep,
            Key.PageDown => _value - KeyStep * 4,
            Key.PageUp => _value + KeyStep * 4,
            Key.Home => 0,
            Key.End => 1,
            _ => null,
        };
        if (to is not { } v) return;
        e.Handled = true;
        _value = Math.Clamp(v, 0, 1);
        InvalidateVisual();
        Changed?.Invoke(_value);
        Committed?.Invoke(_value);
    }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new TrackPeer(this);

    /// <summary>Screen readers see a slider with its value in percent.</summary>
    sealed class TrackPeer(CardTrack owner) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner),
        System.Windows.Automation.Provider.IRangeValueProvider
    {
        CardTrack Track => (CardTrack)Owner;

        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
            System.Windows.Automation.Peers.AutomationControlType.Slider;

        protected override string GetClassNameCore() => nameof(CardTrack);

        public override object? GetPattern(System.Windows.Automation.Peers.PatternInterface p) =>
            p == System.Windows.Automation.Peers.PatternInterface.RangeValue ? this : base.GetPattern(p);

        public double Value => Math.Round(Track._value * 100);
        public bool IsReadOnly => !Track.Interactive;
        public double Maximum => 100;
        public double Minimum => 0;
        public double LargeChange => Track.KeyStep * 400;
        public double SmallChange => Track.KeyStep * 100;

        public void SetValue(double value)
        {
            if (IsReadOnly) throw new ElementNotEnabledException();
            Track._value = Math.Clamp(value / 100, 0, 1);
            Track.InvalidateVisual();
            Track.Changed?.Invoke(Track._value);
            Track.Committed?.Invoke(Track._value);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!Interactive) return;
        _dragging = CaptureMouse();
        Set(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) Set(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        Committed?.Invoke(_value);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Committed?.Invoke(_value);
    }

    void Set(double x)
    {
        _value = ActualWidth > 0 ? Math.Clamp(x / ActualWidth, 0, 1) : 0;
        InvalidateVisual();
        Changed?.Invoke(_value);
    }
}
