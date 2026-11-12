using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Notch;

namespace Shammy.Views.Cards;

/// <summary>
/// A Focus Wall tile (168×140, 6:5, radius 16): artwork first. Three layers over the card's artwork field —
/// <see cref="Art"/> always shows (a ring, meters, the one big number or word), <see cref="Rest"/> only while the
/// tile is dimmed (a small label), <see cref="Lit"/> only while it's focused (title, one line and its primary
/// control). The notch scales, outlines, dims and moves tiles; a tile only says what it shows in each state.
/// </summary>
internal sealed class WallTile : Grid
{
    public const double W = WallRail.TileWidth, H = WallRail.TileHeight, Radius = 16, Pad = 12;

    readonly Border _field = new() { CornerRadius = new CornerRadius(Radius) };
    bool _focused;

    public WallTile()
    {
        Width = W;
        Height = H;
        ClipToBounds = true;
        Clip = new RectangleGeometry(new Rect(0, 0, W, H), Radius, Radius);
        Background = Brushes.Transparent;
        Children.Add(_field);
        Children.Add(Art);
        Children.Add(Rest);
        Lit.Visibility = Visibility.Collapsed;
        Children.Add(Lit);
    }

    /// <summary>The artwork field (album colours, sky, the date's teal, …).</summary>
    public Brush? Field
    {
        get => _field.Background;
        set => _field.Background = value;
    }

    /// <summary>Always shown: the artwork drawn on the field, and the one big number or word.</summary>
    public Grid Art { get; } = new();

    /// <summary>Only while dimmed: the quiet label under the big number.</summary>
    public Grid Rest { get; } = new();

    /// <summary>Only while focused: title, one line, the primary control.</summary>
    public Grid Lit { get; } = new();

    public bool IsLit
    {
        get => _focused;
        set
        {
            if (_focused == value) return;
            _focused = value;
            Rest.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            Lit.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            FocusChanged?.Invoke(value);
        }
    }

    /// <summary>The tile was focused or let go (rebuild anything only the focused state shows).</summary>
    public event Action<bool>? FocusChanged;

    /// <summary>A bottom-left stack of label lines (the mockup's absolute "left:12px; bottom:12px").</summary>
    public static StackPanel BottomLeft(params UIElement[] children)
    {
        var s = CardKit.Column(0, children);
        s.HorizontalAlignment = HorizontalAlignment.Left;
        s.VerticalAlignment = VerticalAlignment.Bottom;
        s.Margin = new Thickness(Pad, 0, Pad, Pad);
        return s;
    }

    /// <summary>The small "open details" primary control in the top-right corner of a focused tile.</summary>
    public static Button OpenButton(string name, Action open)
    {
        var b = CardKit.Primary(name, CardKit.Icon(CardKit.Paths.Open, CardKit.OnAccent, 12, 2.6), open, 28);
        return CardKit.At(b, HorizontalAlignment.Right, VerticalAlignment.Top, 0, 10, 10, 0);
    }

    /// <summary>The focused tile's title (15 px Bold) and line (11.5 px, a light tint of the field so it reads on it).</summary>
    public static StackPanel Words(string title, string line, out TextBlock titleText, out TextBlock lineText, Brush? lineBrush = null)
    {
        titleText = CardKit.Title(title);
        lineText = CardKit.Text(line, 11.5, FontWeights.Medium);
        lineText.Foreground = lineBrush ?? Ui.Tokens.InkOnArt;
        return CardKit.Column(0, titleText, lineText);
    }
}

/// <summary>What the hover peek shows for the focused tile: its swatch, words and one primary control.</summary>
/// <param name="Kicker">The tiny label ("Now playing", "In 10 min").</param>
/// <param name="Swatch">The tile's field, in miniature.</param>
/// <param name="Primary">The tile's primary control, or null for none.</param>
internal sealed record WallPeek(string Kicker, string Title, string Line, Brush Swatch, WallAction? Primary);

/// <summary>A primary control as data: a round play/pause glyph (<see cref="Playing"/> set) or a text pill.</summary>
/// <param name="Stays">The action changes something in place (play/pause, pause a timer), so the peek stays open; otherwise it closes.</param>
internal sealed record WallAction(string Label, Action Run, bool Stays = false, bool? Playing = null);
