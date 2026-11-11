using System.Windows;
using System.Windows.Media;

namespace Shammy.Ui;

/// <summary>
/// The one place Shammy's colours and brushes come from in code: the named tokens of the Focus Wall look, frozen
/// brushes made from a hex string or a colour (cached), and the theme's own resources by key. The older kits
/// (Views/Cards/CardKit, Views/Screens/ScreenKit, Modules/Kit/CardKit) hand these out under their own names.
/// </summary>
internal static class Tokens
{
    static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly object Gate = new();

    /// <summary>A frozen brush for "#RRGGBB" or "#AARRGGBB", made once.</summary>
    public static SolidColorBrush Brush(string hex)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(hex, out var b)) return b;
            b = Frozen(Color(hex));
            Cache[hex] = b;
            return b;
        }
    }

    /// <summary>A frozen brush of <paramref name="color"/> (not cached: for colours made at run time).</summary>
    public static SolidColorBrush Frozen(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    public static Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>A brush from the theme (Theme.xaml, WallControls.xaml, the live accent) by key.</summary>
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static FontFamily FontRes(string key) => (FontFamily)Application.Current.Resources[key];

    public static Style StyleRes(string key) => (Style)Application.Current.Resources[key];

    // ---- The Focus Wall tokens --------------------------------------------------------------------

    public static readonly SolidColorBrush
        Ground = Brush("#0B0B0D"),
        Tile = Brush("#16161A"),
        TileHover = Brush("#1E1E24"),
        Hairline = Brush("#26282E"),
        HairlineHover = Brush("#3A3C44"),
        HairlineStrong = Brush("#4A4C55"),
        Text = Brush("#F5F5F7"),
        Text2 = Brush("#A3A3AB"),
        OnAccent = Brush("#0B0B0D"),
        Alarm = Brush("#FF453A"),
        Pill = Brush("#1E1E23"),
        PillHover = Brush("#26262C"),
        Track = Brush("#2A2A31"),
        RingTrack = Brush("#2E2F36"),
        Row = Brush("#1A1A1F"),
        QuietBg = Brush("#29F5F5F7"),
        QuietHover = Brush("#42F5F5F7"),
        Blush = Brush("#FF6B8A");

    /// <summary>The accent the user picked (orange by default); it changes live.</summary>
    public static SolidColorBrush Accent => Theme.AccentTheme.Brush;

    public static SolidColorBrush AccentHover => Theme.AccentTheme.Hover;
}
