using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Shammy.Core.Notch;

namespace Shammy.Views.Cards;

/// <summary>
/// The kit of the card family (the "cards redesign" mockup): glass tiles, glass capsules and circles, light
/// numbers, switches, segmented choices and rings. Every colour comes from the island's palette (<see cref="Ink"/>):
/// the accent only on a card's hero number and its main button, the palette's three colours on rings and sliders.
/// </summary>
internal static class Sheet
{
    public static readonly Brush
        GlassFill = Ui.Tokens.Brush("#0BFFFFFF"),
        GlassEdge = Ui.Tokens.Brush("#17FFFFFF"),
        CapFill = Ui.Tokens.Brush("#17FFFFFF"),
        CapHover = Ui.Tokens.Brush("#26FFFFFF"),
        Track = Ui.Tokens.Brush("#1FFFFFFF"),
        RowLine = Ui.Tokens.Brush("#0FFFFFFF");

    // ---- Words and numbers ---------------------------------------------------------------------

    /// <summary>A number in the light display face (the home's numbers).</summary>
    public static TextBlock Num(string text, double size, Brush? brush = null) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = Ui.Tokens.FontRes("ShammyDisplayFont"),
        FontWeight = FontWeights.Light,
        Foreground = brush ?? CardKit.Ink,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary>A bold word or line ("Gym", "Focus").</summary>
    public static TextBlock Word(string text, double size = 14, FontWeight? weight = null) =>
        Trim(CardKit.Text(text, size, weight ?? FontWeights.Bold));

    /// <summary>A quiet secondary line ("Ends at 1:38 PM").</summary>
    public static TextBlock Meta(string text, double size = 13) => Trim(CardKit.Text(text, size, FontWeights.Medium, secondary: true));

    static TextBlock Trim(TextBlock t)
    {
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    // ---- Surfaces ------------------------------------------------------------------------------

    /// <summary>A glass tile (4 % white, a 9 % edge): rows, list items, the hero block.</summary>
    public static Border Tile(UIElement child, Thickness? padding = null, double radius = 20, bool accent = false) => new()
    {
        Background = accent ? Ink.Tint : GlassFill,
        BorderBrush = accent ? Ink.TintEdge : GlassEdge,
        BorderThickness = new Thickness(accent ? 1.5 : 1),
        CornerRadius = new CornerRadius(radius),
        Padding = padding ?? new Thickness(14, 12, 14, 12),
        Child = child,
        SnapsToDevicePixels = true,
    };

    // ---- Controls ------------------------------------------------------------------------------

    /// <summary>A glass capsule ("+1 min", "New"); <paramref name="accent"/> makes it the card's one main button.</summary>
    public static Button Cap(string text, string name, Action onClick, string? icon = null, bool accent = false, double height = 34)
    {
        var label = CardKit.Text(text, 13, FontWeights.SemiBold);
        if (accent) label.Foreground = CardKit.OnAccent;
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null)
        {
            var glyph = CardKit.Icon(icon, accent ? CardKit.OnAccent : CardKit.Ink, 14, 2.6);
            glyph.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(glyph);
        }
        row.Children.Add(label);
        return Face(name, row, onClick, double.NaN, height, accent, new Thickness(icon is null ? 14 : 12, 0, 14, 0));
    }

    /// <summary>A round control; <paramref name="accent"/> makes it the card's one main button.</summary>
    public static Button Round(string name, string icon, Action onClick, double size = 34, bool accent = false, double iconSize = 0, bool filled = false)
    {
        double s = iconSize > 0 ? iconSize : Math.Round(size * 0.42);
        var ink = accent ? CardKit.OnAccent : CardKit.Ink;
        var glyph = filled ? CardKit.Glyph(icon, ink, s) : CardKit.Icon(icon, ink, s, 2.4);
        return Face(name, glyph, onClick, size, size, accent, new Thickness(0));
    }

    /// <summary>The card's Back: a glass circle with a chevron.</summary>
    public static Button Back(Action onClick) => Round("Back (Esc)", CardKit.Paths.Back, onClick, 34, iconSize: 15);

    static Button Face(string name, UIElement content, Action onClick, double width, double height, bool accent, Thickness padding)
    {
        Brush bg = accent ? CardKit.Accent : CapFill, hover = accent ? CardKit.AccentHover : CapHover;
        var face = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Background = bg,
            Padding = padding,
            Child = content,
            SnapsToDevicePixels = true,
        };
        if (!double.IsNaN(width)) face.Width = width;
        if (content is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Center;
            fe.VerticalAlignment = VerticalAlignment.Center;
        }
        var button = new Button { Content = face, Height = height, Cursor = Cursors.Hand, Template = Bare, ToolTip = name };
        if (!double.IsNaN(width)) button.Width = width;
        AutomationProperties.SetName(button, name);
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

    static readonly ControlTemplate Bare = MakeBare();

    static ControlTemplate MakeBare()
    {
        var t = new ControlTemplate(typeof(ButtonBase)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        t.Seal();
        return t;
    }

    /// <summary>An on/off switch (42 × 26): the accent when on. Flips itself, then calls <paramref name="set"/>.</summary>
    public static Button Switch(string name, bool on, Action<bool> set)
    {
        var knob = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(3, 0, 0, 0) };
        var track = new Border { Width = 42, Height = 26, CornerRadius = new CornerRadius(13), Child = knob, SnapsToDevicePixels = true };
        var button = new Button { Content = track, Width = 42, Height = 26, Cursor = Cursors.Hand, Template = Bare, ToolTip = name, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, name);
        Ui.FocusKit.Button(button);
        void Show()
        {
            track.Background = on ? CardKit.Accent : Ui.Tokens.Brush("#29FFFFFF");
            knob.Background = on ? CardKit.OnAccent : CardKit.Ink;
            knob.Margin = new Thickness(on ? 19 : 3, 0, 0, 0);
            AutomationProperties.SetItemStatus(button, on ? "On" : "Off");
        }
        Show();
        button.Click += (_, e) =>
        {
            on = !on;
            Show();
            set(on);
            e.Handled = true;
        };
        return button;
    }

    /// <summary>A segmented choice ("5 min · 10 min · 15 min"): the chosen one lifted.</summary>
    public static Border Segmented(IReadOnlyList<string> options, int chosen, Action<int> pick, string name)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var faces = new List<(Border Face, TextBlock Label)>();
        void Show()
        {
            for (int i = 0; i < faces.Count; i++)
            {
                faces[i].Face.Background = i == chosen ? Ui.Tokens.Brush("#24FFFFFF") : Brushes.Transparent;
                faces[i].Label.Foreground = i == chosen ? CardKit.Ink : CardKit.Ink2;
            }
        }
        for (int i = 0; i < options.Count; i++)
        {
            int index = i;
            var label = CardKit.Text(options[i], 13, FontWeights.SemiBold);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            var face = new Border { Height = 28, CornerRadius = new CornerRadius(14), Padding = new Thickness(13, 0, 13, 0), Child = label };
            var b = new Button { Content = face, Template = Bare, Cursor = Cursors.Hand, ToolTip = options[i] };
            AutomationProperties.SetName(b, $"{name}: {options[i]}");
            Ui.FocusKit.Button(b);
            b.Click += (_, e) =>
            {
                chosen = index;
                Show();
                pick(index);
                e.Handled = true;
            };
            faces.Add((face, label));
            row.Children.Add(b);
        }
        Show();
        return new Border
        {
            Background = Ui.Tokens.Brush("#0FFFFFFF"),
            BorderBrush = GlassEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(17),
            Padding = new Thickness(2),
            Child = row,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>A text box in a glass capsule with a placeholder and an optional leading icon (search, add).</summary>
    public static Border Field(TextBox box, string placeholder, string? icon = null, double height = 36)
    {
        box.Background = Brushes.Transparent;
        box.BorderThickness = new Thickness(0);
        box.Foreground = CardKit.Ink;
        box.CaretBrush = CardKit.Ink;
        box.FontFamily = CardKit.Face(FontWeights.Medium);
        box.FontSize = 13;
        box.VerticalContentAlignment = VerticalAlignment.Center;
        box.VerticalAlignment = VerticalAlignment.Center;
        box.Padding = new Thickness(0);
        if (string.IsNullOrEmpty(AutomationProperties.GetName(box))) AutomationProperties.SetName(box, placeholder);
        var hint = CardKit.Text(placeholder, 13, FontWeights.Medium, secondary: true);
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.Margin = new Thickness(2, 0, 0, 0);
        hint.TextTrimming = TextTrimming.CharacterEllipsis;
        void Hint() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Hint();
        Hint();
        var host = new Grid();
        host.Children.Add(hint);
        host.Children.Add(box);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        if (icon is not null)
        {
            var g = CardKit.Icon(icon, CardKit.Ink2, 14, 2.4);
            g.Margin = new Thickness(0, 0, 8, 0);
            g.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(g);
        }
        Grid.SetColumn(host, 1);
        grid.Children.Add(host);
        return new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Background = Ui.Tokens.Brush("#12FFFFFF"),
            BorderBrush = GlassEdge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(13, 0, 13, 0),
            Child = grid,
            Cursor = Cursors.IBeam,
        };
    }

    /// <summary>A small accent-tinted chip ("Today 6:30 PM", ";date"); <paramref name="quiet"/> uses plain glass.</summary>
    public static Border Chip(string text, bool quiet = false, double height = 24, FontFamily? font = null)
    {
        var t = CardKit.Text(text, 12, FontWeights.SemiBold);
        t.Foreground = quiet ? CardKit.Ink2 : Ink.Soft;
        if (font is not null) t.FontFamily = font;
        t.VerticalAlignment = VerticalAlignment.Center;
        return new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Background = quiet ? Ui.Tokens.Brush("#12FFFFFF") : Ink.Tint,
            Padding = new Thickness(10, 0, 10, 0),
            Child = t,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>A slim bar (6 px) filled with the palette's gradient up to <paramref name="fraction"/>.</summary>
    public static Grid Bar(double fraction, double height = 6, Brush? fill = null)
    {
        var g = new Grid { Height = height, VerticalAlignment = VerticalAlignment.Center };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(fraction, 0, 1), GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - Math.Clamp(fraction, 0, 1), GridUnitType.Star) });
        var track = new Border { Background = Track, CornerRadius = new CornerRadius(height / 2) };
        Grid.SetColumnSpan(track, 2);
        g.Children.Add(track);
        g.Children.Add(new Border { Background = fill ?? Ink.Gradient, CornerRadius = new CornerRadius(height / 2) });
        return g;
    }

    /// <summary>A row of controls with a gap.</summary>
    public static StackPanel Row(double gap, params UIElement[] children) => CardKit.Row(gap, children);

    public static StackPanel Column(double gap, params UIElement[] children) => CardKit.Column(gap, children);

    /// <summary>A column layout that puts <paramref name="left"/> first, a stretch, then <paramref name="right"/>.</summary>
    public static Grid Spread(UIElement left, UIElement right, UIElement? middle = null)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(left);
        if (middle is not null)
        {
            Grid.SetColumn(middle, 1);
            g.Children.Add(middle);
        }
        Grid.SetColumn(right, 2);
        g.Children.Add(right);
        return g;
    }
}

/// <summary>
/// The palette as brushes, live: the three wave colours lifted to read on the island (<see cref="A"/>, <see cref="B"/>,
/// <see cref="C"/>), a light accent for hero numbers (<see cref="Soft"/>), a faint accent fill and edge for the chosen
/// thing (<see cref="Tint"/>, <see cref="TintEdge"/>) and the three as one gradient. Shared and unfrozen, like
/// <see cref="Theme.AccentTheme.Brush"/>, so a new song under Album repaints cards already on screen.
/// </summary>
internal static class Ink
{
    public static SolidColorBrush A { get; private set; } = new();
    public static SolidColorBrush B { get; private set; } = new();
    public static SolidColorBrush C { get; private set; } = new();
    public static SolidColorBrush Soft { get; private set; } = new();
    public static SolidColorBrush Tint { get; private set; } = new();
    public static SolidColorBrush TintEdge { get; private set; } = new();
    public static LinearGradientBrush Gradient { get; private set; } = NewGradient();

    static Ink()
    {
        Update();
        Theme.PaletteTheme.Changed += () =>
        {
            if (Application.Current?.Dispatcher is { } d && !d.CheckAccess()) d.BeginInvoke(Update);
            else Update();
        };
    }

    static LinearGradientBrush NewGradient() => new()
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(1, 1),
        GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Colors.White, 0.55), new GradientStop(Colors.White, 1) },
    };

    static Color Lift(Core.Music.Rgb c) => Theme.PaletteTheme.ToColor(IslandPalettes.Accent(c).Accent);

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    static void Update()
    {
        var p = Theme.PaletteTheme.Colours;
        Color a = Lift(p[0]), b = Lift(p.Count > 1 ? p[1] : p[0]), c = Lift(p.Count > 2 ? p[2] : p[0]);
        if (SystemParameters.HighContrast) a = b = c = SystemColors.HighlightColor;
        Set(A, x => A = x, a);
        Set(B, x => B = x, b);
        Set(C, x => C = x, c);
        Set(Soft, x => Soft = x, Mix(a, Colors.White, 0.55));
        Set(Tint, x => Tint = x, Color.FromArgb(0x29, a.R, a.G, a.B));
        Set(TintEdge, x => TintEdge = x, Color.FromArgb(0x8C, a.R, a.G, a.B));
        if (Gradient.IsFrozen) Gradient = NewGradient();
        Gradient.GradientStops[0].Color = a;
        Gradient.GradientStops[1].Color = b;
        Gradient.GradientStops[2].Color = c;
    }

    static void Set(SolidColorBrush brush, Action<SolidColorBrush> replace, Color colour)
    {
        if (brush.IsFrozen)
        {
            brush = new SolidColorBrush();
            replace(brush);
        }
        if (brush.Color != colour) brush.Color = colour;
    }
}

/// <summary>A progress ring: a faint track and an arc in <see cref="Stroke"/> (the palette by default).</summary>
internal sealed class ArcRing : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(nameof(Fraction), typeof(double), typeof(ArcRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public double Thickness { get; init; } = 6;

    public Brush Stroke { get; init; } = Ink.Gradient;

    public ArcRing(double size, double fraction)
    {
        Width = Height = size;
        Fraction = fraction;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double r = (Math.Min(ActualWidth, ActualHeight) - Thickness) / 2;
        if (r <= 0) return;
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(Sheet.Track, Thickness), centre, r, r);
        double f = Math.Clamp(Fraction, 0, 1);
        if (f <= 0.001) return;
        var pen = new Pen(Stroke, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (f >= 0.999)
        {
            dc.DrawEllipse(null, pen, centre, r, r);
            return;
        }
        double angle = f * 2 * Math.PI;
        var start = new Point(centre.X, centre.Y - r);
        var end = new Point(centre.X + r * Math.Sin(angle), centre.Y - r * Math.Cos(angle));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, f > 0.5, SweepDirection.Clockwise, true));
        dc.DrawGeometry(null, pen, new PathGeometry(new[] { fig }));
    }
}
