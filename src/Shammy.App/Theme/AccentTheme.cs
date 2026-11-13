using System.Windows;
using System.Windows.Media;
using Shammy.Core.Notch;
using Shammy.Core.Settings;

namespace Shammy.Theme;

/// <summary>
/// The app's accent: the primary control and the focus ring. It follows the active palette's first colour
/// (<see cref="PaletteTheme"/>), lifted just enough to read on the island and under the buttons' dark text.
/// <see cref="Brush"/> and <see cref="Hover"/> are shared and left unfrozen, so a new palette (or a new song under
/// Album) repaints what is already on screen. They must never go into Application.Resources: WPF freezes everything
/// stored there. The "WallAccent" resources get a fresh frozen copy instead, which XAML's DynamicResource follows.
/// </summary>
internal static class AccentTheme
{
    public static SolidColorBrush Brush { get; private set; } = new(PaletteTheme.ToColor(IslandPalettes.Accent(PaletteTheme.First).Accent));

    public static SolidColorBrush Hover { get; private set; } = new(PaletteTheme.ToColor(IslandPalettes.Accent(PaletteTheme.First).Hover));

    static bool? _contrast;

    /// <summary>Applies the accent again (Windows' high contrast turned on or off, another palette, a new album).</summary>
    public static void Reapply()
    {
        // High contrast first: the text and hairline tokens follow the system colours, and so does the accent. Only when
        // it changed (or the first time): re-setting those resources repaints everything that uses them, and the accent
        // now changes with every song under the Album palette.
        if (_contrast != SystemParameters.HighContrast)
        {
            _contrast = SystemParameters.HighContrast;
            Ui.Tokens.ApplyContrast();
        }
        var (a, h) = IslandPalettes.Accent(PaletteTheme.First);
        Color accent = PaletteTheme.ToColor(a), hover = PaletteTheme.ToColor(h);
        if (SystemParameters.HighContrast) accent = hover = SystemColors.HighlightColor;
        // Something froze a shared brush (a Pen or Drawing that was frozen with it inside): start a new one. Views
        // already built keep the old colour until they are rebuilt; everything new follows.
        if (Brush.IsFrozen) Brush = new SolidColorBrush();
        if (Hover.IsFrozen) Hover = new SolidColorBrush();
        if (Brush.Color != accent) Brush.Color = accent;
        if (Hover.Color != hover) Hover.Color = hover;
        if (Application.Current is not { } app) return;
        if (app.Resources["WallAccent"] is not SolidColorBrush { Color: var was } || was != accent) app.Resources["WallAccent"] = Ui.Tokens.Frozen(accent);
        if (app.Resources["WallAccentHover"] is not SolidColorBrush { Color: var wasHover } || wasHover != hover) app.Resources["WallAccentHover"] = Ui.Tokens.Frozen(hover);
    }

    /// <summary>
    /// Called on every settings change. The old accent setting no longer picks the colour (the palette does, and an
    /// accent chosen before palettes existed was moved to the closest palette), so this just re-applies.
    /// </summary>
    public static void Apply(AccentColor _) => Reapply();
}
