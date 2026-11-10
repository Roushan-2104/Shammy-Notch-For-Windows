namespace Shammy.Core.Settings;

/// <summary>Lively plays the notch's bounce, the spinning record and the pet's little extras; Calm holds still.</summary>
public enum MotionStyle { Lively, Calm }

public enum NotchSize { Small, Normal, Large }

/// <summary>The one accent: the primary control and the focus ring. Orange is the design's own.</summary>
public enum AccentColor { Orange, Violet, Mint, Sky, Pink }

public static class NotchLook
{
    /// <summary>How much everything in the notch is scaled.</summary>
    public static double Scale(NotchSize size) => size switch
    {
        NotchSize.Small => 0.88,
        NotchSize.Large => 1.14,
        _ => 1,
    };

    /// <summary>The accent and its hover shade. Each is light enough for the near-black text the buttons use.</summary>
    public static (string Accent, string Hover) Colours(AccentColor colour) => colour switch
    {
        AccentColor.Violet => ("#9B84FF", "#B1A0FF"),
        AccentColor.Mint => ("#3CCF91", "#62DBA8"),
        AccentColor.Sky => ("#4AA8FF", "#70BBFF"),
        AccentColor.Pink => ("#FF6F9C", "#FF8DB1"),
        _ => ("#FF8A3D", "#FFA466"),
    };
}

/// <summary>Which screen the notch lives on: the main one, the one the pointer is on, or one named screen.</summary>
public static class NotchDisplay
{
    public const string Main = "main", Pointer = "pointer";

    public static string Normalize(string? setting)
    {
        var s = (setting ?? "").Trim();
        if (s.Length == 0 || s.Equals(Main, StringComparison.OrdinalIgnoreCase)) return Main;
        return s.Equals(Pointer, StringComparison.OrdinalIgnoreCase) ? Pointer : s;
    }

    /// <summary>
    /// The screen to use now. A named screen that is no longer connected falls back to the main one (and comes
    /// back by itself when it is plugged in again, since the setting is kept).
    /// </summary>
    public static string Pick(string? setting, IReadOnlyList<string> screens, string primary, string? underPointer)
    {
        var s = Normalize(setting);
        if (s == Main) return primary;
        if (s == Pointer) return underPointer is not null && screens.Contains(underPointer) ? underPointer : primary;
        return screens.FirstOrDefault(d => d.Equals(s, StringComparison.OrdinalIgnoreCase)) ?? primary;
    }

    /// <summary>The choices to offer: only "Main screen" with one screen; otherwise also the pointer and each other screen.</summary>
    public static IReadOnlyList<(string Value, string Label)> Options(IReadOnlyList<string> screens, string primary)
    {
        var options = new List<(string, string)> { (Main, "Main screen") };
        if (screens.Count < 2) return options;
        options.Add((Pointer, "Follow pointer"));
        int fallback = 2;
        foreach (var screen in screens.Where(d => !d.Equals(primary, StringComparison.OrdinalIgnoreCase)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            options.Add((screen, $"Screen {Number(screen) ?? fallback++}"));
        return options;
    }

    /// <summary>"\\.\DISPLAY2" → 2, the number Windows shows in its own display settings.</summary>
    static int? Number(string device)
    {
        int end = device.Length;
        int start = end;
        while (start > 0 && char.IsDigit(device[start - 1])) start--;
        return start < end && int.TryParse(device[start..end], out int n) ? n : null;
    }
}

/// <summary>The apps the notch stays away from while they are in front.</summary>
public static class HideRules
{
    /// <summary>"C:\Apps\Photoshop.EXE" → "photoshop": how an app is remembered and compared.</summary>
    public static string Key(string? app)
    {
        var name = (app ?? "").Trim().Trim('"');
        int slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Trim().ToLowerInvariant();
    }

    public static bool Hides(string? foregroundApp, IEnumerable<string>? apps)
    {
        var key = Key(foregroundApp);
        return key.Length > 0 && apps is not null && apps.Any(a => Key(a) == key);
    }

    public static List<string> Clean(IEnumerable<string>? apps) =>
        (apps ?? Array.Empty<string>()).Select(Key).Where(k => k.Length > 0).Distinct().ToList();
}
