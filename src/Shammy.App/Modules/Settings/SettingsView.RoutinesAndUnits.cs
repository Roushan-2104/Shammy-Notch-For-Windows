using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shammy.Core.Notch;
using Shammy.Core.OnlineVoice;
using Shammy.Core.Productivity;
using Shammy.Core.Search;
using Shammy.Core.Settings;
using Shammy.Core.Updates;
using Shammy.Infrastructure;
using Shammy.Modules.Connect;
using Shammy.Modules.OnlineVoice;
using Shammy.Search;
using Shammy.Views.Screens;
using PetFace = Shammy.Views.Screens.PetFace;

namespace Shammy.Modules.Settings;

internal partial class SettingsView
{
    // ---- Routines --------------------------------------------------------------------------------

    /// <summary>The morning brief, the pet's nudges, pomodoro and focus music, snooze, reminders and alarms.</summary>
    void Routines()
    {
        SwitchRow("Morning brief", "The first unlock of a morning: weather, meetings, reminders and battery in one line, said aloud when a Gnani key is saved. Say “good morning” for it any time.",
            nameof(AppSettings.MorningBrief), "Morning brief on", "Morning brief off", words: "good morning");
        SwitchRow("Eye breaks", "Every 20 minutes, the pet rests its eyes with you for 20 seconds.", nameof(AppSettings.EyeBreaksEnabled), "Eye breaks on", "Eye breaks off",
            words: "nudge rest eyes");
        SwitchRow("Water reminders", "A gentle reminder to drink water every hour and a half.", nameof(AppSettings.WaterRemindersEnabled), "Water reminders on", "Water reminders off",
            words: "nudge drink");

        PomodoroRows();

        Section("Alarms", "");
        SwitchRow("Gentle wake", "A ringing alarm swells from quiet to full over 30 seconds. Off rings at full volume straight away.",
            nameof(AppSettings.AlarmGentleWake), "Alarms start quiet", "Alarms ring at full volume", words: "alarm volume");
        SwitchRow("Alarms break through fullscreen", "A ringing alarm shows over games, videos and slides. Off waits until fullscreen ends.",
            nameof(AppSettings.AlarmBreaksFullscreen), "Alarms show over fullscreen", "Alarms wait for fullscreen to end", words: "alarm game video");
    }

    // ---- Units & language ------------------------------------------------------------------------

    /// <summary>The clock, temperature, home currency and the language English is translated into.</summary>
    void UnitsAndLanguage()
    {
        bool?[] clocks = { null, false, true };
        TextBlock clockHint = null!;
        Row("Clock", ClockHint(_s.Use24Hour), new Segmented(new[] { "Windows", "12-hour", "24-hour" }, Array.IndexOf(clocks, _s.Use24Hour), i =>
        {
            Set(nameof(AppSettings.Use24Hour), clocks[i], $"Clock · {(i == 0 ? "follows Windows" : i == 1 ? "12-hour" : "24-hour")}");
            clockHint.Text = ClockHint(clocks[i]);
        }), out clockHint, words: "time 12 24 hour");

        var units = new[] { TemperatureUnit.Celsius, TemperatureUnit.Fahrenheit };
        var weather = HostLinks.WeatherGlance();
        Row("Temperature", weather is not null ? $"Now: {weather}" : "Weather and conversions use this.",
            new Segmented(new[] { "°C", "°F" }, Array.IndexOf(units, _s.TemperatureUnit), i =>
                Set(nameof(AppSettings.TemperatureUnit), units[i], i == 0 ? "Celsius" : "Fahrenheit")), words: "celsius fahrenheit weather");

        var currencies = Currencies.Where(c => Shammy.Core.Conversions.Currency.Supported.Contains(c.Code) || c.Code == _s.HomeCurrency).ToList();
        if (currencies.All(c => c.Code != _s.HomeCurrency)) currencies.Insert(0, (_s.HomeCurrency, _s.HomeCurrency));
        Row("Home currency", "“100 usd” converts to this.", Dropdown(currencies.Select(c => $"{c.Code} · {c.Name}").ToList(),
            currencies.FindIndex(c => c.Code == _s.HomeCurrency), 200, i =>
                Set(nameof(AppSettings.HomeCurrency), currencies[i].Code, $"Home currency · {currencies[i].Code}")), words: "money rupee dollar convert");

        var languages = Languages.ToList();
        if (languages.All(l => l.Code != _s.TranslateTarget)) languages.Insert(0, (_s.TranslateTarget, Shammy.Core.Conversions.Translation.LanguageName(_s.TranslateTarget)));
        Row("Translate English to", "Selected English text translates into this.", Dropdown(languages.Select(l => l.Name).ToList(),
            languages.FindIndex(l => l.Code == _s.TranslateTarget), 200, i =>
                Set(nameof(AppSettings.TranslateTarget), languages[i].Code, $"Translates into {languages[i].Name}")), words: "language hindi");
    }

    /// <summary>Pomodoro lengths, focus music, the usual snooze and how long a reminder's card stays.</summary>
    void PomodoroRows()
    {
        var pomo = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, name, step) in new[] { ("Focus", nameof(AppSettings.PomodoroWorkMinutes), 5), ("Short", nameof(AppSettings.PomodoroShortBreakMinutes), 1), ("Long", nameof(AppSettings.PomodoroLongBreakMinutes), 5) })
        {
            int value = (int)SettingsCatalog.Get(_s, SettingsCatalog.Find(name)!)!;
            var stepper = new Stepper(value, step == 1 ? 1 : 5, step == 1 ? 30 : 180, step, v =>
                Set(name, v, $"Pomodoro {_s.PomodoroWorkMinutes} / {_s.PomodoroShortBreakMinutes} / {_s.PomodoroLongBreakMinutes}"),
                less: $"Less {label.ToLowerInvariant()} time", more: $"More {label.ToLowerInvariant()} time");
            var caption = ScreenKit.Label(label, size: 10);
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            var col = ScreenKit.Column(3, stepper, caption);
            col.Margin = new Thickness(pomo.Children.Count > 0 ? 10 : 0, 0, 0, 0);
            pomo.Children.Add(col);
        }
        Row("Pomodoro", "Focus · short break · long break, in minutes.", pomo, words: "focus timer break");
        SwitchRow("Focus music", "Starting a focus session also plays focus music, when nothing else is playing.", nameof(AppSettings.FocusMusic), "Focus music on", "Focus music off",
            words: "pomodoro");

        int[] snoozes = { 5, 10, 15 };
        Row("Default snooze", "The “Usual” button on a ringing alarm.", new Segmented(snoozes.Select(m => $"{m} min").ToList(),
            Array.IndexOf(snoozes, _s.SnoozeMinutes), i => Set(nameof(AppSettings.SnoozeMinutes), snoozes[i], $"Snooze · {snoozes[i]} min")), words: "alarm reminder");
        Row("Reminder card stays", "How long a due reminder waits before it tucks away.",
            new Stepper(_s.ReminderCardSeconds, 5, 600, 5, v => Set(nameof(AppSettings.ReminderCardSeconds), v, $"Reminders stay {v} s"),
                v => $"{v} s", valueWidth: 44), words: "reminders");
    }

    static string ClockHint(bool? use24) => "Standup shows as " + ClockPreference.Format(10, 30, use24);

    static readonly (string Code, string Name)[] Currencies =
    {
        ("INR", "Indian rupee"), ("USD", "US dollar"), ("EUR", "Euro"), ("GBP", "British pound"), ("AED", "UAE dirham"),
        ("SGD", "Singapore dollar"), ("AUD", "Australian dollar"), ("CAD", "Canadian dollar"), ("JPY", "Japanese yen"),
        ("CNY", "Chinese yuan"), ("SAR", "Saudi riyal"), ("BDT", "Bangladeshi taka"), ("NPR", "Nepalese rupee"),
        ("LKR", "Sri Lankan rupee"), ("CHF", "Swiss franc"),
    };

    static readonly (string Code, string Name)[] Languages =
    {
        ("hi", "Hindi"), ("bn", "Bengali"), ("or", "Odia"), ("ta", "Tamil"), ("te", "Telugu"), ("mr", "Marathi"),
        ("gu", "Gujarati"), ("kn", "Kannada"), ("ml", "Malayalam"), ("pa", "Punjabi"), ("ur", "Urdu"), ("es", "Spanish"),
        ("fr", "French"), ("de", "German"), ("ja", "Japanese"), ("zh-CN", "Chinese"), ("ar", "Arabic"),
    };
}
