using System.Reflection;
using System.Text.Json.Serialization;
using Shammy.Core.Settings;
using Shammy.Infrastructure;

namespace Shammy.Modules.Settings;

/// <summary>One setting as the card and the pipe see it. <see cref="Hint"/> is the row's one line, also searched.</summary>
internal sealed record SettingInfo(string Name, string Section, string Label, PropertyInfo Property, int Step = 1, string Hint = "")
{
    public Type Type => Property.PropertyType;
}

/// <summary>
/// A switch that lives in a module's tray toggle rather than in <see cref="AppSettings"/> (the ticker, text snippets…):
/// matched by the tray item's text, shown in Settings and the palette under its own label.
/// </summary>
internal sealed record TraySwitch(string TrayText, string Section, string Label, string Hint);

/// <summary>Something the palette can offer from Shammy's own settings: a switch it flips in place, or a row it opens.</summary>
internal sealed record SettingsSearchEntry(string Key, string Section, string Label, string Hint, Func<bool>? Get = null, Action<bool>? Set = null)
{
    public bool IsSwitch => Get is not null && Set is not null;
}

/// <summary>
/// The settings by section, and get/set by name over <see cref="AppSettings"/>'s properties (so a new setting
/// needs only a line here to appear on the card). Setting a value normalises it, saves, and raises
/// <see cref="AppSettings.Changed"/> so features pick it up.
/// </summary>
internal static class SettingsCatalog
{
    public const string General = "General", Notch = "Notch", Wheel = "Wheel", Pet = "Pet", Voice = "Voice & search",
        Notifications = "Notifications", Routines = "Routines", Clipboard = "Clipboard", Connections = "Connections",
        Units = "Units & language", Advanced = "Advanced";

    /// <summary>The Settings screen's eleven tabs, in order.</summary>
    public static readonly string[] Sections = { General, Notch, Wheel, Pet, Voice, Notifications, Routines, Clipboard, Connections, Units, Advanced };

    /// <summary>The tabs' older names (the pipe, the tour and old links may still use them).</summary>
    static readonly Dictionary<string, string> OldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Notch & pet"] = Notch,
        ["Voice"] = Voice,
        ["Search"] = Voice,
        ["Time & units"] = Units,
        ["Time"] = Routines,
        ["Units"] = Units,
        ["Language"] = Units,
        ["Clipboard & privacy"] = Clipboard,
        ["Privacy"] = Clipboard,
        ["Calendar"] = Connections,
        ["Claude Code"] = Connections,
        ["Features"] = Advanced,
    };

    /// <summary>"pet", "voice", "Notch & pet" → the tab's name (exactly, by an old name, or by a word of it); null when none fits.</summary>
    public static string? SectionFor(string arg)
    {
        var a = arg.Trim();
        if (a.Length == 0) return null;
        return Sections.FirstOrDefault(t => string.Equals(t, a, StringComparison.OrdinalIgnoreCase))
               ?? (OldNames.TryGetValue(a, out var old) ? old : null)
               ?? Sections.FirstOrDefault(t => t.Split(' ').Any(w => string.Equals(w, a, StringComparison.OrdinalIgnoreCase)));
    }

    static readonly (string Name, string Section, string Label, int Step, string Hint)[] Shown =
    {
        // General
        ("NotchSounds", General, "Notch sounds", 1, "A soft ping for notifications, a chime for timers."),
        ("NotchSoundVolume", General, "Sound volume", 10, "Pings, chimes and alarms."),
        ("NotchDisplay", General, "Show on", 1, "Which screen the notch lives on."),
        // Notch
        ("HoverSpeed", Notch, "Hover speed", 1, "How long you rest the pointer before the notch opens."),
        ("PeekEnabled", Notch, "Peek", 1, "A quick glance when you rest the pointer, before it opens."),
        ("GesturesEnabled", Notch, "Gestures", 1, "Scroll over the notch for volume, swipe sideways to skip a track."),
        ("TuckAwayEnabled", Notch, "Tuck-away tab", 1, "A small tab under the notch slides it away for a few seconds."),
        ("NowLineEnabled", Notch, "Right now line", 1, "One line at the top of the open notch with what matters this minute."),
        ("NotchSize", Notch, "Notch size", 1, "Makes everything in the notch larger or smaller."),
        ("Motion", Notch, "Motion", 1, "Calm stops the bounce, the spinning record and the pet’s extras."),
        ("Palette", Notch, "Palette", 1, "The island’s waves, buttons and highlights. Album takes the colours of the song playing."),
        ("LyricLineEnabled", Notch, "Lyric line", 1, "The line being sung shows under the record."),
        ("HiddenApps", Notch, "Hide while using", 1, "The notch stays away while these apps are in front."),
        // Wheel
        ("WheelEnabled", Wheel, "Wheel", 1, "Hold the middle mouse button: eight actions around the pointer."),
        // Pet
        ("PetEnabled", Pet, "Pet", 1, "The pet lives in the notch and reacts to what you do."),
        ("PetKind", Pet, "Pet style", 1, "Eyes, or a cat with ears, whiskers and a nose."),
        ("PetPurrs", Pet, "Purr", 1, "A soft purr when your pointer rests on the cat."),
        ("CatTheme", Pet, "Cat theme", 1, "The cat listens while you talk; paws lead the choices."),
        ("PetEyeStyle", Pet, "Eye style", 1, "Round, oval or pixel eyes."),
        ("PetEyeColor", Pet, "Eye colour", 1, "Shows in every mood."),
        ("PetName", Pet, "Name", 1, "What the pet calls itself."),
        // Voice & search
        ("MicrophoneDeviceId", Voice, "Microphone", 1, "Used when you hold Right Alt."),
        ("VoiceMode", Voice, "Language on this PC", 1, "English, or Hinglish on PCs with a graphics card."),
        ("DefaultSearchMode", Voice, "Default search", 1, "What Enter does when you type in the palette."),
        ("OnlineVoiceSpeaks", Voice, "Shammy speaks", 1, "Timers, alarms and reminders said aloud with a Gnani key."),
        ("OnlineVoiceListens", Voice, "Hinglish listening online", 1, "Your voice goes to Gnani while you hold the talk key."),
        ("OnlineVoiceName", Voice, "Online voice", 1, "Who speaks, with a Gnani key."),
        // Notifications
        ("ShowNotifications", Notifications, "Show notifications in the notch", 1, "Windows still keeps them in the Action Center."),
        ("NotificationPopupSeconds", Notifications, "Show for (s)", 1, "How long a notification stays before it tucks away."),
        ("OtpEnabled", Notifications, "Codes in the notch", 1, "A one-time code (OTP) in a message shows big with Copy."),
        ("SystemAlertsEnabled", Notifications, "Battery and Bluetooth alerts", 1, "Charging, low battery and earbuds connecting."),
        ("MutedNotificationApps", Notifications, "Muted apps", 1, "Right-click any notification to mute its app."),
        ("AlwaysHideNotificationText", Notifications, "Always hide message text", 1, "Notifications show who, never what."),
        ("LiveCricketEnabled", Notifications, "Live cricket", 1, "Shows the score while India is playing."),
        // Routines
        ("MorningBrief", Routines, "Morning brief", 1, "Weather, meetings and reminders at the first unlock of a morning."),
        ("EyeBreaksEnabled", Routines, "Eye breaks", 1, "Every 20 minutes the pet rests its eyes with you."),
        ("WaterRemindersEnabled", Routines, "Water reminders", 1, "A gentle reminder to drink water every hour and a half."),
        ("PomodoroWorkMinutes", Routines, "Pomodoro focus (min)", 5, "Focus length in minutes."),
        ("PomodoroShortBreakMinutes", Routines, "Short break (min)", 1, "Pomodoro short break."),
        ("PomodoroLongBreakMinutes", Routines, "Long break (min)", 5, "Pomodoro long break."),
        ("FocusMusic", Routines, "Focus music", 1, "Starting a focus session also plays focus music."),
        ("SnoozeMinutes", Routines, "Default snooze (min)", 5, "The “Usual” button on a ringing alarm."),
        ("ReminderCardSeconds", Routines, "Reminder card stays (s)", 5, "How long a due reminder waits before it tucks away."),
        ("AlarmGentleWake", Routines, "Gentle wake", 1, "A ringing alarm swells from quiet to full over 30 seconds."),
        ("AlarmBreaksFullscreen", Routines, "Alarms break through fullscreen", 1, "A ringing alarm shows over games, videos and slides."),
        // Clipboard
        ("ClipboardHistoryEnabled", Clipboard, "Clipboard history", 1, "Keeps what you copy so you can paste it again."),
        ("ClipboardRetentionHours", Clipboard, "Keep history (hours)", 6, "Older items clear themselves. Pinned ones stay."),
        ("CopyPreviewEnabled", Clipboard, "Copy preview", 1, "A peek of what you copy, in the notch."),
        // Connections
        ("AgentsAskInNotch", Connections, "Ask me in the notch", 1, "Allow and Deny drop from the notch when Claude Code needs an OK."),
        // Units & language
        ("Use24Hour", Units, "Clock", 1, "12-hour, 24-hour, or as Windows has it."),
        ("TemperatureUnit", Units, "Temperature", 1, "Weather and conversions use this."),
        ("HomeCurrency", Units, "Home currency", 1, "“100 usd” converts to this."),
        ("TranslateTarget", Units, "Translate English to", 1, "Selected English text translates into this."),
        // Advanced
        ("ReplaceVolumeFlyout", Advanced, "Replace the Windows volume popup", 1, "The volume keys show Shammy’s level in the notch instead."),
        ("ShowVisualizer", Advanced, "Audio visualizer", 1, "Bars that move with the music in the resting notch."),
        ("CompactResults", Advanced, "Compact search results", 1, "Google’s phone layout, which fits the notch. After a restart."),
        ("PinToScreenEdge", Advanced, "Pin to the very top edge", 1, "The notch sits on the screen’s edge, over a top taskbar."),
        ("VoiceLanguage", Advanced, "Detect voice language", 1, "Listens for Hindi and other languages on this PC. Slower."),
        ("DisabledModules", Advanced, "Features turned off (on next start)", 1, "Whole features, off from the next start."),
    };

    /// <summary>Module tray toggles that are settings too (the rest, like Live captions, are actions).</summary>
    public static readonly IReadOnlyList<TraySwitch> TraySwitches = new TraySwitch[]
    {
        new("Ticker", Advanced, "Ticker", "Cricket, stocks or crypto in the resting notch."),
        new("Text snippets", Advanced, "Text snippets", "Triggers like ;date expand as you type, in any app."),
        new("Remember songs for voice", Advanced, "Remember songs", "Songs you play help voice catch their names later."),
        new("Flash album colour on new songs", Advanced, "Album colour flash", "The notch’s edge flashes the cover’s colour on a new song."),
        new("Colour edge on events", Advanced, "Colour edge", "The animated colour edge around the notch on events."),
    };

    static readonly Lazy<List<SettingInfo>> All = new(() =>
    {
        var props = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .ToDictionary(p => p.Name);
        var list = Shown.Where(s => props.ContainsKey(s.Name))
            .Select(s => new SettingInfo(s.Name, s.Section, s.Label, props[s.Name], s.Step, s.Hint)).ToList();
        // Everything else (voice engine, shelf, ...) is still reachable from the pipe, filed under "Other".
        list.AddRange(props.Values.Where(p => list.All(i => i.Name != p.Name)).OrderBy(p => p.Name)
            .Select(p => new SettingInfo(p.Name, "Other", p.Name, p)));
        return list;
    });

    public static IReadOnlyList<SettingInfo> Settings => All.Value;

    public static IEnumerable<SettingInfo> In(string section) => All.Value.Where(s => s.Section == section);

    public static SettingInfo? Find(string name) =>
        All.Value.FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public static object? Get(AppSettings settings, SettingInfo info) => info.Property.GetValue(settings);

    public static string Describe(AppSettings settings, SettingInfo info) => $"{info.Name}={SettingValue.Format(Get(settings, info))}";

    /// <summary>Sets a value given as text; returns an error message, or null when it was set.</summary>
    public static string? TrySet(AppSettings settings, SettingInfo info, string text)
    {
        if (!SettingValue.TryParse(info.Type, text, out var value))
            return $"{info.Name} takes {SettingValue.Expected(info.Type)}, not '{text}'";
        Set(settings, info, value);
        return null;
    }

    public static void Set(AppSettings settings, SettingInfo info, object? value)
    {
        info.Property.SetValue(settings, value);
        settings.Normalize();
        settings.Save();
        settings.NotifyChanged(info.Name);
    }

    /// <summary>
    /// Shammy's own settings for the palette: every shown setting (yes/no ones flip in place, the rest open their
    /// tab), Start with Windows, and the module switches that live in the tray (<paramref name="trayToggles"/>).
    /// </summary>
    public static IReadOnlyList<SettingsSearchEntry> Searchable(AppSettings s, IEnumerable<ModuleTrayItem> trayToggles)
    {
        var list = new List<SettingsSearchEntry>
        {
            new("StartWithWindows", General, "Start with Windows", "Shammy wakes up when you sign in.",
                () =>
                {
                    try { return s.StartWithWindows; }
                    catch (Exception) { return false; }
                },
                on =>
                {
                    try { s.StartWithWindows = on; }
                    catch (Exception ex) { Log.Warn("Updating the startup entry failed", ex); }
                }),
        };
        foreach (var info in All.Value.Where(i => i.Section != "Other"))
        {
            if (info.Type == typeof(bool))
                list.Add(new(info.Name, info.Section, info.Label, info.Hint, () => Get(s, info) is true, on => Set(s, info, on)));
            else if (info.Name == nameof(AppSettings.VoiceLanguage))
                list.Add(new(info.Name, info.Section, info.Label, info.Hint, () => s.VoiceLanguage == "auto", on => Set(s, info, on ? "auto" : "en")));
            else
                list.Add(new(info.Name, info.Section, info.Label, info.Hint));
        }
        foreach (var item in trayToggles)
        {
            if (TraySwitches.FirstOrDefault(t => t.TrayText == item.Text) is not { } sw || item.IsChecked is not { } isChecked || item.SetChecked is not { } setChecked) continue;
            list.Add(new("tray:" + sw.Label, sw.Section, sw.Label, sw.Hint, isChecked, setChecked));
        }
        return list;
    }
}
