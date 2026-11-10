using Shammy.Core.Ambient;
using Shammy.Core.Conversions;
using Shammy.Core.Productivity;
using Shammy.Search;
using Shammy.Views.Screens;

namespace Shammy.Infrastructure;

/// <summary>
/// Pushes the settings that features read from static knobs (12/24-hour, temperature unit, home currency and
/// language, pomodoro lengths, sound volume, microphone). Run at start-up and after every change; settings
/// read live where they're used (hover speed, gestures, clipboard, notifications) need nothing here.
/// </summary>
internal static class SettingsApplier
{
    public static void Apply(AppSettings s)
    {
        ClockPreference.Use24Hour = s.Use24Hour;
        WeatherText.Unit = s.TemperatureUnit;
        Currency.HomeCurrency = s.HomeCurrency;
        Translation.HomeLanguage = s.TranslateTarget;
        Pomodoro.FocusLength = TimeSpan.FromMinutes(s.PomodoroWorkMinutes);
        Pomodoro.ShortBreakLength = TimeSpan.FromMinutes(s.PomodoroShortBreakMinutes);
        Pomodoro.LongBreakLength = TimeSpan.FromMinutes(s.PomodoroLongBreakMinutes);
        NotchSound.Enabled = s.NotchSounds;
        NotchSound.Volume = s.NotchSoundVolume;
        MicRecorder.PreferredDevice = s.MicrophoneDeviceId;
        PetLook.Apply(s);
        MotionPrefs.Calm = s.Motion == Shammy.Core.Settings.MotionStyle.Calm;
        Theme.AccentTheme.Apply(s.Accent);
    }
}
