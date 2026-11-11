using Shammy.Core.Launcher;

namespace Shammy.Core.Palette;

/// <summary>Settings the palette can change in place instead of only opening a page.</summary>
public enum SettingControl { None, Brightness, Volume, Bluetooth }

/// <summary>A Windows Settings page and the words people type for it.</summary>
public sealed record SettingEntry(string Title, string Uri, SettingControl Control, IReadOnlyList<string> Keywords);

/// <summary>
/// Settings words → ms-settings: pages. Every URI is a fixed string from this table, never built from
/// what was typed. Brightness, sound and Bluetooth get live controls in the palette.
/// </summary>
public static class SettingsCatalog
{
    static SettingEntry E(string title, string uri, params string[] keywords) => new(title, uri, SettingControl.None, keywords);
    static SettingEntry C(string title, string uri, SettingControl control, params string[] keywords) => new(title, uri, control, keywords);

    public static readonly IReadOnlyList<SettingEntry> All = new[]
    {
        C("Brightness", "ms-settings:display", SettingControl.Brightness, "brightness", "screen brightness", "dim screen", "brighter", "dimmer"),
        C("Volume", "ms-settings:sound", SettingControl.Volume, "volume", "sound", "audio", "speaker", "speakers", "headphones", "output device", "audio output", "mute"),
        C("Bluetooth", "ms-settings:bluetooth", SettingControl.Bluetooth, "bluetooth", "bt", "earbuds", "headphones", "airpods", "buds", "headset"),
        E("Wi-Fi", "ms-settings:network-wifi", "wifi", "wi-fi", "wireless", "wlan"),
        E("Network & internet", "ms-settings:network", "network", "internet", "ethernet"),
        E("Display", "ms-settings:display", "display", "screen", "resolution", "scale", "monitor", "monitors"),
        E("Night light", "ms-settings:nightlight", "night light", "blue light", "night mode"),
        E("Battery & power", "ms-settings:batterysaver", "battery", "battery saver", "power", "power mode", "energy saver", "sleep"),
        E("Notifications", "ms-settings:notifications", "notifications", "notification", "do not disturb", "dnd", "focus assist", "silence notifications"),
        E("Windows Update", "ms-settings:windowsupdate", "update", "updates", "windows update"),
        E("Installed apps", "ms-settings:appsfeatures", "installed apps", "uninstall", "apps and features", "programs"),
        E("Default apps", "ms-settings:defaultapps", "default apps", "default browser"),
        E("Startup apps", "ms-settings:startupapps", "startup apps", "startup"),
        E("Colors", "ms-settings:colors", "dark mode", "light mode", "accent color", "colors", "colours", "theme colors"),
        E("Background", "ms-settings:personalization-background", "wallpaper", "background", "desktop background"),
        E("Personalization", "ms-settings:personalization", "personalization", "personalize", "themes"),
        E("Lock screen", "ms-settings:lockscreen", "lock screen"),
        E("Taskbar", "ms-settings:taskbar", "taskbar"),
        E("Mouse", "ms-settings:mousetouchpad", "mouse", "cursor speed"),
        E("Touchpad", "ms-settings:devices-touchpad", "touchpad", "trackpad"),
        E("Keyboard", "ms-settings:keyboard", "keyboard"),
        E("Printers & scanners", "ms-settings:printers", "printer", "printers", "scanner"),
        E("Language & region", "ms-settings:regionlanguage", "language", "region", "input language"),
        E("Date & time", "ms-settings:dateandtime", "date and time", "time zone", "timezone", "clock settings"),
        E("Storage", "ms-settings:storagesense", "storage", "disk space", "storage sense"),
        E("Privacy & security", "ms-settings:privacy", "privacy", "security", "permissions"),
        E("Microphone access", "ms-settings:privacy-microphone", "microphone", "mic access", "microphone privacy"),
        E("Camera access", "ms-settings:privacy-webcam", "camera access", "webcam"),
        E("Location", "ms-settings:privacy-location", "location", "gps"),
        E("VPN", "ms-settings:network-vpn", "vpn"),
        E("Mobile hotspot", "ms-settings:network-mobilehotspot", "hotspot", "mobile hotspot", "tethering"),
        E("Airplane mode", "ms-settings:network-airplanemode", "airplane mode", "flight mode", "aeroplane mode"),
        E("Accounts", "ms-settings:yourinfo", "account", "accounts", "your info"),
        E("Sign-in options", "ms-settings:signinoptions", "sign in options", "password", "pin", "windows hello"),
        E("Clipboard", "ms-settings:clipboard", "clipboard settings", "clipboard history"),
        E("Multitasking", "ms-settings:multitasking", "multitasking", "snap", "snap windows"),
        E("Accessibility", "ms-settings:easeofaccess", "accessibility", "ease of access", "text size"),
        E("About this PC", "ms-settings:about", "about", "about pc", "pc name", "system info", "specs"),
        E("Windows Settings", "ms-settings:", "settings", "windows settings", "control panel"),
    };

    static readonly string[] Fillers = { "open ", "show ", "change ", "turn on ", "turn off " };

    /// <summary>
    /// Best entries for the text: 100 for a keyword exactly, 90 when the text starts a keyword (3+ letters),
    /// 99 for "bluetooth settings" (so a bare keyword still wins ties), 70 when it starts a later word of a keyword ("light" → night light).
    /// </summary>
    public static IReadOnlyList<(SettingEntry Entry, int Match)> Find(string text, int max = 3)
    {
        var q = AppMatcher.Normalize(text ?? "");
        foreach (var f in Fillers)
            if (q.StartsWith(f, StringComparison.Ordinal)) q = q[f.Length..];
        bool settingsSuffix = q.EndsWith(" settings", StringComparison.Ordinal) && q != "windows settings";
        if (settingsSuffix) q = q[..^" settings".Length];
        if (q.Length < 2) return Array.Empty<(SettingEntry, int)>();

        var results = new List<(SettingEntry Entry, int Match, int Order)>();
        for (int i = 0; i < All.Count; i++)
        {
            int best = 0;
            foreach (var raw in All[i].Keywords)
            {
                var k = AppMatcher.Normalize(raw);
                int s = q == k ? (settingsSuffix ? 99 : 100)
                    : q.Length >= 3 && k.StartsWith(q, StringComparison.Ordinal) ? 90
                    : q.Length >= 3 && k.Contains(" " + q, StringComparison.Ordinal) ? 70
                    : 0;
                best = Math.Max(best, s);
            }
            if (best > 0) results.Add((All[i], best, i));
        }
        return results.OrderByDescending(r => r.Match).ThenBy(r => r.Order).Take(max).Select(r => (r.Entry, r.Match)).ToList();
    }
}
