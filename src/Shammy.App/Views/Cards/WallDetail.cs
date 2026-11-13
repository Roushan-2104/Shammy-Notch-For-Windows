using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Shammy.Views.Cards;

/// <summary>
/// A card opened in place (Wall.dc.html's detail, 924×248): the tile's artwork field grown into a 300 px art
/// panel on the left (the notch puts Back on it and morphs it out of the tile), then a tracked kicker, a bold
/// title, one metadata line and the card's controls. <see cref="Corner"/> holds a top-right primary
/// control (Join); <see cref="Aside"/> a full-height right column (lyrics, the 5-day list).
/// </summary>
internal class WallDetail : Grid
{
    public const double W = 924, H = 248, ArtWidth = 300, Radius = 22;

    readonly Border _field = new();
    readonly Grid _info = new() { Margin = new Thickness(26, 18, 24, 18) };
    readonly ContentControl _lineSlot = new() { Focusable = false, Margin = new Thickness(0, 7, 0, 0) };
    UIElement? _corner, _aside;

    public WallDetail(Brush field, string kicker, string title, string line = "")
    {
        Width = W;
        Height = H;
        // The island's glass, like the home's big moment: the waves show through, a hairline edge holds it.
        Background = CardKit.Glass;
        var edge = new Border { BorderBrush = CardKit.GlassEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(Radius), IsHitTestVisible = false };
        SetColumnSpan(edge, 2);
        Panel.SetZIndex(edge, 10);
        Children.Add(edge);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ArtWidth) });
        ColumnDefinitions.Add(new ColumnDefinition());

        _field.Background = field;
        Art.Children.Add(_field);
        Art.ClipToBounds = true;
        Children.Add(Art);

        Kicker = CardKit.SectionLabel(kicker);
        Kicker.HorizontalAlignment = HorizontalAlignment.Left;
        // Words are bold (the home's big moment title); numbers in the art are light (see CardKit.Poster).
        TitleText = new TrackedText
        {
            Text = title, Uppercase = false, FontSize = 28, FontFamily = CardKit.Face(FontWeights.Bold), FontWeight = FontWeights.Bold,
            Tracking = -0.015, LineFactor = 1.1, Foreground = CardKit.Ink,
        };
        TitleText.HorizontalAlignment = HorizontalAlignment.Left;
        TitleText.Margin = new Thickness(0, 6, 0, 0);
        Line = CardKit.Text(line, 13, FontWeights.Medium, secondary: true);
        _lineSlot.Content = Line;

        _info.ColumnDefinitions.Add(new ColumnDefinition());
        _info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < 3; i++) _info.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _info.RowDefinitions.Add(new RowDefinition());
        _info.Children.Add(Kicker);
        SetRow(TitleText, 1);
        _info.Children.Add(TitleText);
        SetRow(_lineSlot, 2);
        _info.Children.Add(_lineSlot);
        Body.Margin = new Thickness(0, 14, 0, 0);
        SetRow(Body, 3);
        _info.Children.Add(Body);
        Info.Children.Add(_info);
        SetColumn(Info, 1);
        Children.Add(Info);
    }

    /// <summary>The left art panel (300×248); the field is its first child. The notch adds Back at its top-left.</summary>
    public Grid Art { get; } = new();

    /// <summary>Everything right of the art panel (the notch fades it in after the morph).</summary>
    public Grid Info { get; } = new();

    public Brush? Field
    {
        get => _field.Background;
        set => _field.Background = value;
    }

    public TrackedText Kicker { get; }
    public TrackedText TitleText { get; }
    public TextBlock Line { get; }

    /// <summary>The card's controls under the line (about 574 × 118, less an aside).</summary>
    public Grid Body { get; } = new();

    /// <summary>Replaces the line's text with something richer (filters, a search field).</summary>
    public UIElement LineContent
    {
        set => _lineSlot.Content = value;
    }

    /// <summary>A top-right control (the primary "Join", a search field), level with the kicker and title.</summary>
    public UIElement? Corner
    {
        get => _corner;
        set
        {
            if (_corner is not null) _info.Children.Remove(_corner);
            _corner = value;
            if (value is null) return;
            if (value is FrameworkElement fe)
            {
                fe.HorizontalAlignment = HorizontalAlignment.Right;
                fe.VerticalAlignment = VerticalAlignment.Top;
                fe.Margin = new Thickness(16, 2, 0, 0);
            }
            SetRowSpan(value, 3);
            _info.Children.Add(value);
        }
    }

    /// <summary>A right-hand column the full height of the info area (lyrics, the forecast's days).</summary>
    public UIElement? Aside
    {
        get => _aside;
        set
        {
            if (_aside is not null) _info.Children.Remove(_aside);
            _aside = value;
            if (value is null) return;
            if (value is FrameworkElement fe) fe.Margin = new Thickness(26, 0, 0, 0);
            SetColumn(value, 1);
            SetRowSpan(value, 4);
            _info.Children.Add(value);
        }
    }

    /// <summary>Adds artwork to the art panel at its bottom-left (the poster and label), 18 px in.</summary>
    public StackPanel ArtBottomLeft(params UIElement[] children)
    {
        var s = CardKit.Column(0, children);
        s.HorizontalAlignment = HorizontalAlignment.Left;
        s.VerticalAlignment = VerticalAlignment.Bottom;
        s.Margin = new Thickness(18);
        Art.Children.Add(s);
        return s;
    }

    /// <summary>A plain list row (Wall's list detail): a dot, the name, its metadata on the right, a hairline above.</summary>
    public static Grid ListRow(UIElement lead, UIElement name, UIElement? meta, double height = 33)
    {
        var g = new Grid { Height = height, Background = Brushes.Transparent };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var rule = CardKit.Rule();
        rule.VerticalAlignment = VerticalAlignment.Top;
        SetColumnSpan(rule, 3);
        g.Children.Add(rule);
        if (lead is FrameworkElement l)
        {
            l.Margin = new Thickness(0, 0, 12, 0);
            l.VerticalAlignment = VerticalAlignment.Center;
        }
        g.Children.Add(lead);
        SetColumn(name, 1);
        g.Children.Add(name);
        if (meta is not null)
        {
            if (meta is FrameworkElement m)
            {
                m.Margin = new Thickness(12, 0, 0, 0);
                m.VerticalAlignment = VerticalAlignment.Center;
            }
            SetColumn(meta, 2);
            g.Children.Add(meta);
        }
        return g;
    }

    /// <summary>The list's quiet bullet.</summary>
    public static FrameworkElement Bullet() => new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = CardKit.Ink2 };

    /// <summary>A vertically scrolling list for <see cref="Body"/> (hidden scrollbar, wheel scrolls).</summary>
    public static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false,
    };
}
