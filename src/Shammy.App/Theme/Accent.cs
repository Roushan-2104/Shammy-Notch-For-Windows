using System.Windows;
using System.Windows.Media;
using Windows.UI.ViewManagement;

namespace Shammy.Theme;

/// <summary>Pulls the Windows accent colour into the app's resources so chips, carets and orbs match the OS.</summary>
internal static class Accent
{
    static UISettings? _ui;

    public static void Apply()
    {
        try
        {
            _ui ??= new UISettings();
            // AccentLight2 reads well on pure black, like Windows' own dark-theme accent text.
            var c = _ui.GetColorValue(UIColorType.AccentLight2);
            var accent = Color.FromArgb(255, c.R, c.G, c.B);
            Application.Current.Resources["AccentBrush"] = Ui.Tokens.Frozen(accent);
            double luma = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
            Application.Current.Resources["OnAccentBrush"] = Ui.Tokens.Frozen(luma > 0.55 ? Colors.Black : Colors.White);
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Warn("Accent colour unavailable", ex);
        }
    }

    public static Color Current =>
        (Application.Current.Resources["AccentBrush"] as SolidColorBrush)?.Color ?? Color.FromRgb(0x4C, 0xC2, 0xFF);

}
