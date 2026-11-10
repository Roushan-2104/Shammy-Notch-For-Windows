using System.Reflection;
using System.Text.Json.Serialization;
using Shammy.Core.Settings;
using Shammy.Infrastructure;

namespace Shammy.Modules.Settings;

/// <summary>One setting as the card and the pipe see it.</summary>
internal sealed record SettingInfo(string Name, string Section, string Label, PropertyInfo Property, int Step = 1)
{
    public Type Type => Property.PropertyType;
}

/// <summary>
/// The settings by section, and get/set by name over <see cref="AppSettings"/>'s properties (so a new setting
/// needs only a line here to appear on the card). Setting a value normalises it, saves, and raises
/// <see cref="AppSettings.Changed"/> so features pick it up.
/// </summary>
internal static class SettingsCatalog
{
    /// <summary>The Settings screen's tabs, in order (the designed seven).</summary>
    public static readonly string[] Sections = { "General", "Notch & pet", "Voice", "Notifications", "Time & units", "Clipboard & privacy", "Calendar" };

    static readonly (string Name, string Section, string Label, int Step)[] Shown =
    {
        ("DefaultSearchMode", "General", "Default search", 1),
        ("GesturesEnabled", "General", "Gestures", 1),
        ("NotchSounds", "General", "Notch sounds", 1),
        ("NotchSoundVolume", "General", "Sound volume", 10),
        ("DisabledModules", "General", "Features turned off (on next start)", 1),
        ("HoverSpeed", "Notch & pet", "Hover speed", 1),
        ("PeekEnabled", "Notch & pet", "Peek", 1),
        ("PetEnabled", "Notch & pet", "Pet", 1),
        ("PetEyeStyle", "Notch & pet", "Eye style", 1),
        ("PetEyeColor", "Notch & pet", "Eye colour", 1),
        ("PetName", "Notch & pet", "Name", 1),
        ("Motion", "Notch & pet", "Motion", 1),
        ("NotchSize", "Notch & pet", "Notch size", 1),
        ("Accent", "Notch & pet", "Accent colour", 1),
        ("NotchDisplay", "Notch & pet", "Show on", 1),
        ("LyricLineEnabled", "Notch & pet", "Lyric line", 1),
        ("HiddenApps", "Notch & pet", "Hide while using", 1),
        ("EyeBreaksEnabled", "Notch & pet", "Eye breaks", 1),
        ("WaterRemindersEnabled", "Notch & pet", "Water reminders", 1),
        ("MicrophoneDeviceId", "Voice", "Microphone", 1),
        ("VoiceMode", "Voice", "Hinglish voice", 1),
        ("ShowNotifications", "Notifications", "Show notifications in the notch", 1),
        ("NotificationPopupSeconds", "Notifications", "Show for (s)", 1),
        ("SystemAlertsEnabled", "Notifications", "Battery and Bluetooth alerts", 1),
        ("LiveCricketEnabled", "Notifications", "Live cricket", 1),
        ("MutedNotificationApps", "Notifications", "Muted apps", 1),
        ("Use24Hour", "Time & units", "Clock", 1),
        ("TemperatureUnit", "Time & units", "Temperature", 1),
        ("HomeCurrency", "Time & units", "Home currency", 1),
        ("TranslateTarget", "Time & units", "Translate English to", 1),
        ("PomodoroWorkMinutes", "Time & units", "Pomodoro focus (min)", 5),
        ("PomodoroShortBreakMinutes", "Time & units", "Short break (min)", 1),
        ("PomodoroLongBreakMinutes", "Time & units", "Long break (min)", 5),
        ("SnoozeMinutes", "Time & units", "Default snooze (min)", 5),
        ("ReminderCardSeconds", "Time & units", "Reminder card stays (s)", 5),
        ("AlarmGentleWake", "Time & units", "Gentle wake", 1),
        ("AlarmBreaksFullscreen", "Time & units", "Alarms break through fullscreen", 1),
        ("ClipboardHistoryEnabled", "Clipboard & privacy", "Clipboard history", 1),
        ("ClipboardRetentionHours", "Clipboard & privacy", "Keep history (hours)", 6),
        ("CopyPreviewEnabled", "Clipboard & privacy", "Copy preview", 1),
        ("AlwaysHideNotificationText", "Clipboard & privacy", "Always hide message text", 1),
    };

    static readonly Lazy<List<SettingInfo>> All = new(() =>
    {
        var props = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .ToDictionary(p => p.Name);
        var list = Shown.Where(s => props.ContainsKey(s.Name))
            .Select(s => new SettingInfo(s.Name, s.Section, s.Label, props[s.Name], s.Step)).ToList();
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
}
