using System.ComponentModel;
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

    /// <summary>The hex behind the text tokens, for code that wants a string (NotchPalette, glance items).</summary>
    public const string TextHex = "#F5F5F7", Text2Hex = "#A3A3AB", HairlineHex = "#26282E", GroundHex = "#0B0B0D",
        InkOnArtHex = "#E4E4EA", OkHex = "#30D158", PawHex = "#FF9DB0", BlushHex = "#FF6B8A";

    /// <summary>Corner radius of a section inside a detail or pop-up (a rounded tile-coloured panel).</summary>
    public const double DetailRadius = 14;

    /// <summary>The width of a pop-up card under the notch (reminders, approvals, screenshots, downloads…).</summary>
    public const double PopupWidth = 460;

    /// <summary>A pop-up card's title: 17 px ExtraBold, tracked −0.015 em.</summary>
    public const double PopupTitleSize = 17, PopupTitleTracking = -0.015;

    /// <summary>The kit's close (×) and back (‹) controls.</summary>
    public const double CloseSize = 24, BackSize = 32;

    /// <summary>
    /// The text and hairline tokens. Not frozen: with Windows' high contrast on they turn into the system's own
    /// colours, live (see <see cref="ApplyContrast"/>), so everything already on screen follows.
    /// </summary>
    public static readonly SolidColorBrush
        Text = new(Color(TextHex)),
        Text2 = new(Color(Text2Hex)),
        Hairline = new(Color(HairlineHex)),
        OnAccent = new(Color(GroundHex));

    public static readonly SolidColorBrush
        Ground = Brush(GroundHex),
        Tile = Brush("#16161A"),
        TileHover = Brush("#1E1E24"),
        HairlineHover = Brush("#3A3C44"),
        HairlineStrong = Brush("#4A4C55"),
        // Secondary text laid over artwork (a song's artist, a weather line): a little softer than white.
        InkOnArt = Brush(InkOnArtHex),
        // Meaning only: something is fine, charged or done.
        Ok = Brush(OkHex),
        // The cat's paw pads and the pin dots on the wheel.
        Paw = Brush(PawHex),
        Alarm = Brush("#FF453A"),
        Pill = Brush("#1E1E23"),
        PillHover = Brush("#26262C"),
        Track = Brush("#2A2A31"),
        RingTrack = Brush("#2E2F36"),
        Row = Brush("#1A1A1F"),
        QuietBg = Brush("#29F5F5F7"),
        QuietHover = Brush("#42F5F5F7"),
        Blush = Brush(BlushHex);

    /// <summary>The accent the user picked (orange by default); it changes live.</summary>
    public static SolidColorBrush Accent => Theme.AccentTheme.Brush;

    public static SolidColorBrush AccentHover => Theme.AccentTheme.Hover;

    // ---- High contrast ---------------------------------------------------------------------------

    static bool _watching;

    /// <summary>Whether Windows' high contrast is on (the tokens then follow its colours).</summary>
    public static bool HighContrast => SystemParameters.HighContrast;

    /// <summary>
    /// Maps the text, hairline and accent tokens (and the theme's WallText / WallText2 / WallHairline resources) to
    /// the system colours while high contrast is on, and back when it goes off. Runs on the UI thread the first
    /// time the accent is applied, then follows the Windows setting by itself.
    /// </summary>
    public static void ApplyContrast()
    {
        if (!_watching)
        {
            _watching = true;
            SystemParameters.StaticPropertyChanged += OnSystemChanged;
        }
        bool hc = SystemParameters.HighContrast;
        // The notch stays black (it stands in for a hardware notch), so text takes whichever of the theme's pair
        // reads on black: WindowText in a dark theme, Window in a light one (High Contrast White).
        var ink = Lighter(SystemColors.WindowTextColor, SystemColors.WindowColor);
        Set(Text, hc ? ink : Color(TextHex));
        Set(Text2, hc ? ink : Color(Text2Hex));
        Set(Hairline, hc ? ink : Color(HairlineHex));
        Set(OnAccent, hc ? SystemColors.HighlightTextColor : Color(GroundHex));
        if (Application.Current is not { } app) return;
        app.Resources["WallText"] = Frozen(Text.Color);
        app.Resources["WallText2"] = Frozen(Text2.Color);
        app.Resources["WallHairline"] = Frozen(Hairline.Color);
        app.Resources["WallOnAccent"] = Frozen(OnAccent.Color);
    }

    static Color Lighter(Color a, Color b) => Luma(a) >= Luma(b) ? a : b;

    static double Luma(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    static void Set(SolidColorBrush brush, Color color)
    {
        if (brush.IsFrozen || brush.Color == color) return;
        brush.Color = color;
    }

    static void OnSystemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.HighContrast)) return;
        Application.Current?.Dispatcher.BeginInvoke(() => Theme.AccentTheme.Reapply());
    }
}
