using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wall = Shammy.Views.Cards.CardKit;

namespace Shammy.Modules.Kit;

/// <summary>
/// The older card-builder call shapes, kept as a thin facade over the one Focus Wall kit
/// (<see cref="Shammy.Views.Cards.CardKit"/>): same tokens, same font, same quiet controls. The cards moved
/// to the Wall kit; a few smaller views (setup forms, pills, cards drawn by other modules) still use these
/// names and pick up the Focus Wall look through them. New code uses the Wall kit directly.
/// </summary>
internal static class CardKit
{

    /// <summary>A frozen brush for "#RRGGBB" or "#AARRGGBB".</summary>
    public static SolidColorBrush Brush(string hex) => Ui.Tokens.Brush(hex);

    /// <summary>A colour at an alpha (0–1).</summary>
    public static SolidColorBrush Tint(string hex, double alpha)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        return Brush(Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B).ToString());
    }

    public static readonly SolidColorBrush Text = (SolidColorBrush)Wall.Ink;
    public static readonly SolidColorBrush Text2 = (SolidColorBrush)Wall.Ink2;
    public static readonly SolidColorBrush Raised = (SolidColorBrush)Wall.PillBg;
    public static readonly SolidColorBrush RaisedStroke = (SolidColorBrush)Wall.Hairline;
    public static readonly SolidColorBrush Round = (SolidColorBrush)Wall.PillBg;
    public static readonly SolidColorBrush RoundHover = (SolidColorBrush)Wall.PillHover;
    public static readonly SolidColorBrush Track = (SolidColorBrush)Wall.TrackBg;

    public static FontFamily Font => Wall.Font;
    public static FontFamily DisplayFont => Wall.Font;
    public static FontFamily IconFont => Ui.Tokens.FontRes("IconFont");

    public static TextBlock Label(string text, double size = 13, FontWeight? weight = null, Brush? brush = null)
    {
        var t = Wall.Text(text, size, weight ?? FontWeights.SemiBold);
        if (brush is not null) t.Foreground = brush;
        return t;
    }

    /// <summary>The tiny uppercase caption ("NEXT", "CLIPBOARD"): the kit's section label (11 Bold, +0.12 em, grey).</summary>
    public static Shammy.Views.TrackedText Caption(string text, Brush? brush = null) => Wall.Label(text, brush ?? Text2);

    /// <summary>A big bold number ("10:30", "30°").</summary>
    public static TextBlock Big(string text, double size = 30) => Label(text, size, FontWeights.ExtraBold, Text);

    public static TextBlock Glyph(string glyph, double size, Brush? brush = null) => Wall.FluentGlyph(glyph, size, brush);

    public static System.Windows.Shapes.Ellipse Dot(double size, Brush fill) => Wall.Dot(size, fill);

    /// <summary>
    /// A clickable rounded element with a hover colour and the kit's press (a <see cref="Shammy.Views.Screens.Pressable"/>:
    /// a tab stop, a button to screen readers); the click doesn't also open the card under it.
    /// </summary>
    public static Border Button(UIElement content, Action onClick, Brush background, Brush hover, double height,
        double? width = null, double radius = -1, string? tip = null)
    {
        var b = new Shammy.Views.Screens.Pressable(background, hover)
        {
            Height = height,
            CornerRadius = new CornerRadius(radius < 0 ? height / 2 : radius),
            Child = content,
            ToolTip = tip,
        };
        if (width is { } w) b.Width = w;
        b.Click += onClick;
        AutomationName(b, tip);
        return b;
    }

    public static Border RoundButton(string glyph, Action onClick, double size = 36, string? tip = null, double glyphSize = 13) =>
        Button(Glyph(glyph, glyphSize), onClick, Round, RoundHover, size, size, tip: tip);

    public static Border PillButton(string text, Action onClick, double height = 26, string? glyph = null, Brush? background = null,
        Brush? hover = null, Brush? foreground = null, double fontSize = 11, string? tip = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (glyph is not null)
        {
            var g = Glyph(glyph, fontSize + 1, foreground ?? Text);
            g.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(g);
        }
        row.Children.Add(Label(text, fontSize, FontWeights.SemiBold, foreground ?? Text));
        var b = Button(row, onClick, background ?? Round, hover ?? RoundHover, height, tip: tip ?? text);
        b.Padding = new Thickness(glyph is null ? 12 : 11, 0, glyph is null ? 12 : 14, 0);
        return b;
    }

    /// <summary>The surface's one primary text button ("Join", "Connect"): the user's accent with dark text, a pill.</summary>
    public static Border PrimaryPillButton(string text, Action onClick, double height = 26, string? glyph = null, double fontSize = 11, string? tip = null) =>
        PillButton(text, onClick, height, glyph, Ui.Tokens.Accent, Ui.Tokens.AccentHover, Ui.Tokens.OnAccent, fontSize, tip);

    /// <summary>A quiet chip (no tint: Focus Wall keeps colour for artwork and meaning).</summary>
    public static Border Chip(UIElement content, string accentHex, double height = 26) => new()
    {
        Height = height,
        CornerRadius = new CornerRadius(height / 2),
        Background = Raised,
        BorderBrush = RaisedStroke,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(10, 0, 10, 0),
        Child = content,
    };

    public static Grid Progress(double fraction, Brush fill, double height = 4) => Wall.Progress(fraction, fill, height);

    public static StackPanel Row(double gap, params UIElement[] children) => Wall.Row(gap, children);

    public static bool H24 => Wall.H24;

    public static void AutomationName(DependencyObject d, string? name) => Wall.Name(d, name);
}
