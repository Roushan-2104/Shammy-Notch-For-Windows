using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Shammy.Views;

/// <summary>
/// Code-built pieces in the Focus Wall look for the surfaces outside the open notch (typing bar, pop-up cards,
/// drop targets): Schibsted Grotesk at the right weight, tracked labels, glyphs and the three button kinds.
/// </summary>
internal static class WallParts
{
    public const double Radius = 16, SmallRadius = 12;

    public static Brush Res(string key) => Ui.Tokens.Res(key);

    /// <summary>
    /// The embedded face for a weight. Regular and Bold share the "Schibsted Grotesk" family; the others are their
    /// own families, so asking one family for SemiBold would get a synthesised bold instead of the real cut.
    /// </summary>
    public static FontFamily Family(FontWeight weight)
    {
        int w = weight.ToOpenTypeWeight();
        string key = w switch
        {
            >= 750 => "WallFontExtraBold",
            >= 650 => "WallFont",
            >= 550 => "WallFontSemiBold",
            >= 450 => "WallFontMedium",
            _ => "WallFont",
        };
        return Ui.Tokens.FontRes(key);
    }

    /// <summary>
    /// Puts a text element on the Wall face at <paramref name="weight"/>. <paramref name="tabular"/> for live counters of
    /// plain digits (a percentage) so they don't jitter; off otherwise, since this face's tabular set also widens . , :
    /// into gaps ("46 . 0 MB").
    /// </summary>
    public static T Face<T>(T element, FontWeight weight, bool tabular = false) where T : FrameworkElement
    {
        element.SetValue(TextElement.FontFamilyProperty, Family(weight));
        element.SetValue(TextElement.FontWeightProperty, weight);
        element.SetValue(Typography.NumeralAlignmentProperty, tabular ? FontNumeralAlignment.Tabular : FontNumeralAlignment.Normal);
        return element;
    }

    public static TextBlock Text(string text, double size, FontWeight weight, string brush = "WallText", bool tabular = false) => Face(new TextBlock
    {
        Text = text,
        FontSize = size,
        Foreground = Res(brush),
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    }, weight, tabular);

    /// <summary>A pop-up card's title as a TextBlock (for titles that wrap): 17 px ExtraBold, the kit's one title style.</summary>
    public static TextBlock PopupTitle(string text, bool wrap = false)
    {
        var t = Text(text, Ui.Tokens.PopupTitleSize, FontWeights.ExtraBold);
        if (wrap)
        {
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        return t;
    }

    /// <summary>The small tracked uppercase label (11 px, 700, 0.12em).</summary>
    public static TrackedText Label(string text, string brush = "WallText2", double size = 11) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Res(brush),
    };

    public static TextBlock Glyph(string glyph, double size, string brush = "WallText") => new()
    {
        Text = glyph,
        FontFamily = Ui.Tokens.FontRes("IconFont"),
        FontSize = size,
        Foreground = Res(brush),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The surface's one primary control (accent fill). Never takes keyboard focus, like the rest of the notch.</summary>
    public static Button Primary(string label, Action onClick, double height = 32) => TextButton("WallPrimaryButton", label, onClick, height);

    /// <summary>A secondary action: raised pill with a hairline.</summary>
    public static Button Quiet(string label, Action onClick, double height = 32) => TextButton("WallQuietButton", label, onClick, height);

    /// <summary>A pill with a glyph before its text ("Copy", "Open"); <paramref name="primary"/> makes it the accent one.</summary>
    public static Button GlyphButton(string glyph, string label, Action onClick, bool primary, out TextBlock icon, out TextBlock text, double height = 30)
    {
        string ink = primary ? "WallOnAccent" : "WallText";
        icon = Glyph(glyph, 12, ink);
        text = Text(label, 12, primary ? FontWeights.Bold : FontWeights.SemiBold, ink);
        text.Margin = new Thickness(7, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(text);
        var b = new Button
        {
            Style = Ui.Tokens.StyleRes(primary ? "WallPrimaryButton" : "WallQuietButton"),
            Height = height,
            Padding = new Thickness(11, 0, 12, 0),
            Content = row,
        };
        AutomationProperties.SetName(b, label);
        b.Click += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return b;
    }

    /// <summary>A round glyph button that stays quiet until hovered (close, toolbar actions).</summary>
    public static Button Icon(string glyph, string name, Action onClick, double size = 28, double glyphSize = 11)
    {
        var b = new Button
        {
            Style = Ui.Tokens.StyleRes("WallIconButton"),
            Content = glyph,
            Width = size,
            Height = size,
            FontSize = glyphSize,
            ToolTip = name,
        };
        AutomationProperties.SetName(b, name);
        b.Click += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return b;
    }

    static Button TextButton(string style, string label, Action onClick, double height)
    {
        var b = new Button { Style = Ui.Tokens.StyleRes(style), Height = height, Content = label };
        AutomationProperties.SetName(b, label);
        b.Click += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return b;
    }

    /// <summary>The artwork square on a pop-up card (bell, power, app icon): the tile surface, the glyph in white.</summary>
    public static Border ArtTile(UIElement content, double size = 44) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(SmallRadius),
        Background = Res("WallTile"),
        BorderBrush = Res("WallHairline"),
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = content,
    };
}
