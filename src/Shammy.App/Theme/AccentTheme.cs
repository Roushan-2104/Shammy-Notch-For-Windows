using System.Windows;
using System.Windows.Media;
using Shammy.Core.Settings;

namespace Shammy.Theme;

/// <summary>
/// The notch's own accent (Settings → Notch & pet → Accent colour): the primary control and the focus ring.
/// <see cref="Brush"/> and <see cref="Hover"/> are shared and left unfrozen, so picking a colour repaints what is
/// already on screen. They must never go into Application.Resources: WPF freezes everything stored there. The
/// "WallAccent" resources get a fresh frozen copy instead, which XAML's DynamicResource follows by itself.
/// </summary>
internal static class AccentTheme
{
    public static SolidColorBrush Brush { get; private set; } = new(Parse("#FF8A3D"));

    public static SolidColorBrush Hover { get; private set; } = new(Parse("#FFA466"));

    static AccentColor _chosen;

    /// <summary>Applies the accent again (Windows' high contrast turned on or off).</summary>
    public static void Reapply() => Apply(_chosen);

    public static void Apply(AccentColor colour)
    {
        _chosen = colour;
        // High contrast first: the text and hairline tokens follow the system colours, and so does the accent.
        Ui.Tokens.ApplyContrast();
        var (accent, hover) = NotchLook.Colours(colour);
        if (SystemParameters.HighContrast) accent = hover = SystemColors.HighlightColor.ToString();
        // Something froze a shared brush (a Pen or Drawing that was frozen with it inside): start a new one. Views
        // already built keep the old colour until they are rebuilt; everything new follows.
        if (Brush.IsFrozen) Brush = new SolidColorBrush();
        if (Hover.IsFrozen) Hover = new SolidColorBrush();
        Brush.Color = Parse(accent);
        Hover.Color = Parse(hover);
        if (Application.Current is not { } app) return;
        app.Resources["WallAccent"] = Frozen(accent);
        app.Resources["WallAccentHover"] = Frozen(hover);
    }

    static SolidColorBrush Frozen(string hex)
    {
        var b = new SolidColorBrush(Parse(hex));
        b.Freeze();
        return b;
    }

    static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
