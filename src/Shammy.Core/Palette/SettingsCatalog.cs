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

/// <summary>
/// Matching typed words against Shammy's own settings (a row's label, its one-line hint, its tab): the Settings
/// screen's search box filters with <see cref="Matches"/>, the palette ranks with <see cref="Score"/>.
/// </summary>
public static class ShammySettingSearch
{
    static readonly string[] Fillers = { "open ", "show ", "change ", "turn on ", "turn off ", "switch on ", "switch off ", "shammy " };

    static string Clean(string? text)
    {
        var q = AppMatcher.Normalize(text ?? "");
        foreach (var f in Fillers)
            if (q.StartsWith(f, StringComparison.Ordinal)) q = q[f.Length..];
        if (q.EndsWith(" settings", StringComparison.Ordinal)) q = q[..^" settings".Length];
        else if (q.EndsWith(" setting", StringComparison.Ordinal)) q = q[..^" setting".Length];
        return q.Trim();
    }

    /// <summary>Every word typed begins a word of <paramref name="text"/> ("eye br" matches "Eye breaks").</summary>
    public static bool Matches(string? query, string? text)
    {
        var words = AppMatcher.Normalize(query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;
        var hay = AppMatcher.Normalize(text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.All(w => hay.Any(h => h.StartsWith(w, StringComparison.Ordinal)));
    }

    /// <summary>
    /// How well the text means this setting, 0–100: 100 its label exactly, 90 the start of its label (3+ letters),
    /// 85 every word begins a word of the label, 70 every word begins a word of the label, hint or tab; else 0.
    /// "turn off peek" and "peek settings" count as "peek".
    /// </summary>
    public static int Score(string? query, string label, string hint = "", string section = "")
    {
        var q = Clean(query);
        if (q.Length < 2) return 0;
        var l = AppMatcher.Normalize(label);
        if (q == l) return 100;
        if (q.Length >= 3 && l.StartsWith(q, StringComparison.Ordinal)) return 90;
        // A letter or two is too loose to mean a setting.
        if (q.Replace(" ", "").Length < 3) return 0;
        if (Matches(q, l)) return 85;
        if (Matches(q, l + " " + hint + " " + section)) return 70;
        return 0;
    }
}
