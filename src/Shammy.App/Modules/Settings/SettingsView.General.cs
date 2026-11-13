using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Shammy.Core.Settings;
using Shammy.Core.Updates;
using Shammy.Infrastructure;
using Shammy.Search;
using Shammy.Views.Screens;

namespace Shammy.Modules.Settings;

internal partial class SettingsView
{
    // ---- General ---------------------------------------------------------------------------------

    /// <summary>The version and updates, start with Windows, sounds and their volume, and which screen.</summary>
    void General()
    {
        UpdateRow();

        bool startup;
        try { startup = _s.StartWithWindows; }
        catch (Exception) { startup = false; }
        Row("Start with Windows", "Shammy wakes up when you sign in.", new ToggleSwitch(startup, on =>
        {
            try
            {
                _s.StartWithWindows = on;
                Say(on ? "Starts with Windows" : "Won’t start with Windows");
            }
            catch (Exception ex)
            {
                Log.Warn("Updating the startup entry failed", ex);
                Say("Couldn’t change the startup entry");
            }
        }, "Start with Windows"), words: "startup sign in boot login");

        SwitchRow("Notch sounds", "A soft ping for notifications, a chime for timers.", nameof(AppSettings.NotchSounds), "Sounds on", "Sounds off",
            words: "ping chime sound audio");

        var volume = new SlimSlider(_s.NotchSoundVolume, 0, 100, 10);
        TextBlock volumeHint = null!;
        Row("Sound volume", VolumeHint(_s.NotchSoundVolume), volume, out volumeHint, words: "loud quiet sounds");
        volume.Changed += v => volumeHint.Text = VolumeHint(v);
        volume.Committed += v =>
        {
            Set(nameof(AppSettings.NotchSoundVolume), v, $"Volume {v}%");
            if (_s.NotchSounds) NotchSound.Play(Shammy.Core.Audio.PingKind.Notification);
        };

        ScreenRow();
    }

    /// <summary>Which screen the notch lives on (a choice only with two or more).</summary>
    void ScreenRow()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var names = screens.Select(s => s.DeviceName).ToList();
        var primary = screens.FirstOrDefault(s => s.Primary)?.DeviceName ?? names.FirstOrDefault() ?? "";
        var options = NotchDisplay.Options(names, primary);
        if (options.Count < 2)
            Row("Show on", "Only one screen is connected. With a second one you can choose where the notch lives.", null, words: "screen monitor display");
        else
        {
            int chosen = Math.Max(0, options.ToList().FindIndex(o => o.Value.Equals(_s.NotchDisplay, StringComparison.OrdinalIgnoreCase)));
            Row("Show on", "Which screen the notch lives on.",
                new Segmented(options.Select(o => o.Label).ToList(), chosen, i =>
                    Set(nameof(AppSettings.NotchDisplay), options[i].Value, $"Showing on · {options[i].Label}"), minWidth: 54),
                words: "screen monitor display");
        }
    }

    /// <summary>The version, where updates stand, and the one button that makes sense right now.</summary>
    void UpdateRow()
    {
        (string Result, bool Installed) Status() => HostLinks.UpdateStatus?.Invoke() ?? (UpdateText.NotChecked, false);
        TextBlock hint = null!;
        var slot = new Border();
        Pressable check = ScreenKit.Secondary("Check now", async () =>
        {
            if (HostLinks.CheckForUpdates is not { } checkNow) return;
            string result;
            try { result = await checkNow(); }
            catch (Exception ex)
            {
                Log.Warn("Update check from Settings failed", ex);
                result = UpdateText.Failed(ex.GetType().Name);
            }
            Say(UpdateText.Said(result));
        });
        Pressable restart = ScreenKit.Secondary("Restart to update", () => HostLinks.RestartForUpdate?.Invoke());
        void Show()
        {
            var (result, installed) = Status();
            hint.Text = UpdateText.Hint(result, installed);
            slot.Child = !installed || UpdateText.IsBusy(result) ? null : UpdateText.IsReady(result) ? restart : check;
        }
        Row($"Version {HostLinks.Version?.Invoke() ?? "?"}", UpdateText.Hint(Status().Result, Status().Installed), slot, out hint, words: "update updates version check");
        Show();
        slot.Loaded += (_, _) =>
        {
            HostLinks.UpdateChanged += Show;
            Show();
        };
        slot.Unloaded += (_, _) => HostLinks.UpdateChanged -= Show;
    }

    static string VolumeHint(int v) => v == 0 ? "0% · alarms still ring at 20%" : $"{v}% · pings, chimes and alarms";

    // ---- Advanced --------------------------------------------------------------------------------

    /// <summary>The switches that used to live only in the tray menu, then every feature that can be turned off.</summary>
    void Advanced()
    {
        Section("Switches", "");
        SwitchRow("Replace the Windows volume popup", "The volume keys show Shammy’s level in the notch instead.",
            nameof(AppSettings.ReplaceVolumeFlyout), "Volume keys show in the notch", "Volume keys show Windows’ popup", words: "flyout hud");
        SwitchRow("Audio visualizer", "Bars that move with the music in the resting notch.", nameof(AppSettings.ShowVisualizer),
            "Visualizer on", "Visualizer off", words: "bars music");
        SwitchRow("Compact search results", "Google’s phone layout, which fits the notch. After a restart.", nameof(AppSettings.CompactResults),
            "Compact results after a restart", "Full results after a restart", words: "mobile layout google");
        SwitchRow("Pin to the very top edge", "The notch sits on the screen’s edge, over a top taskbar.", nameof(AppSettings.PinToScreenEdge),
            "Pinned to the top edge", "Below a top taskbar", words: "taskbar position place");
        Row("Detect voice language", "Listens for Hindi and other languages on this PC. Slower than English only.",
            new ToggleSwitch(_s.VoiceLanguage == "auto", on =>
                Set(nameof(AppSettings.VoiceLanguage), on ? "auto" : "en", on ? "Voice detects the language" : "Voice listens for English"), "Detect voice language"),
            words: "hindi language auto whisper");
        foreach (var sw in SettingsCatalog.TraySwitches)
        {
            if (Infrastructure.TrayIcon.Toggles.FirstOrDefault(t => t.Text == sw.TrayText) is not { IsChecked: { } isChecked, SetChecked: { } setChecked }) continue;
            var label = sw.Label;
            Row(label, sw.Hint, new ToggleSwitch(isChecked(), on =>
            {
                _changing = true;
                try { setChecked(on); }
                catch (Exception ex) { Log.Warn($"Switching {label} failed", ex); }
                finally { _changing = false; }
                Say($"{label} {(on ? "on" : "off")}");
            }, label));
        }

        Section("Features", "Turned-off features stay off from the next start.");
        foreach (var name in _modules().Where(m => m != SettingsModule.ModuleName).OrderBy(FeatureName, StringComparer.CurrentCultureIgnoreCase))
        {
            bool off = _s.DisabledModules.Contains(name, StringComparer.OrdinalIgnoreCase);
            // A feature with its own switch elsewhere shows here only while it is off, so it can be turned back on.
            if (HasOwnSwitch.Contains(name) && !off) continue;
            Row(FeatureName(name), FeatureHint(name), new ToggleSwitch(!off, on =>
            {
                var list = _s.DisabledModules.Where(m => !string.Equals(m, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (!on) list.Add(name);
                Set(nameof(AppSettings.DisabledModules), list, $"{FeatureName(name)} {(on ? "on" : "off")} after a restart");
            }, FeatureName(name)), words: "feature module");
        }
    }

    /// <summary>Features switched elsewhere: Live cricket, Morning brief, Codes, the Wheel, Eye breaks and water, Claude Code, Online voice, Text snippets.</summary>
    static readonly HashSet<string> HasOwnSwitch = new(StringComparer.OrdinalIgnoreCase)
    {
        "cricket", "brief", "otp", "wheel", "nudges", "agents", "onlinevoice", "snippets",
    };

    static readonly Dictionary<string, (string Name, string Hint)> Features = new(StringComparer.OrdinalIgnoreCase)
    {
        ["agents"] = ("Claude Code", "Sessions at work, done, or waiting for your OK."),
        ["ambient"] = ("Weather and pet moods", "The sky, the ticker and the pet’s moods."),
        ["answers"] = ("Answers on your PC", "Battery, time, date and new messages, answered without a search."),
        ["brief"] = ("Morning brief", "Weather, meetings and reminders at the first unlock of a morning."),
        ["whatsnew"] = ("What’s new", "A short note after each update."),
        ["calendar"] = ("Calendar", "Your Google Calendar in the notch."),
        ["captions"] = ("Live captions", "Captions for anything playing, made on this PC."),
        ["clipboard"] = ("Clipboard", "History, screenshots, copy previews."),
        ["connect"] = ("Downloads and links", "Downloads, and the links between features."),
        ["cricket"] = ("Live cricket", "Live scores in the ticker."),
        ["crypto"] = ("Crypto prices", "Prices in the ticker."),
        ["dropactions"] = ("Shelf", "Park files on the notch, then convert or share them."),
        ["guide"] = ("Guide", "Everything Shammy does, in one place."),
        ["health"] = ("Health", "Storage, a careful cleanup and a daily check of this laptop."),
        ["launcher"] = ("App launcher", "“open spotify” from the palette."),
        ["levels"] = ("Volume and brightness", "The notch’s own volume and brightness levels."),
        ["meeting"] = ("Meetings", "Mic and camera pills during calls."),
        ["mirror"] = ("Mirror", "A quick camera check."),
        ["mixer"] = ("Volume mixer", "A volume slider for each app."),
        ["music"] = ("Music", "Now playing, lyrics, play commands."),
        ["nudges"] = ("Eye breaks and water", "Gentle nudges from the pet."),
        ["onlinevoice"] = ("Online voice (Gnani)", "Speaking and Hinglish listening with your own Gnani key."),
        ["otp"] = ("Codes in the notch", "One-time codes from messages, big, with Copy."),
        ["palette"] = ("Palette", "Search apps, files and settings as you type."),
        ["phonesend"] = ("Send to phone", "Files to your phone over Wi-Fi."),
        ["productivity"] = ("Timers, alarms and notes", "Timers, pomodoro, reminders, alarms, notes."),
        ["prompter"] = ("Stage", "Your script under the camera, paced by your voice."),
        ["quick"] = ("Laptop", "Wi-Fi, Bluetooth, power mode, keep awake, lock and sleep."),
        ["record"] = ("Screen recording", "Record the screen from the notch."),
        ["screenshot"] = ("Screenshots", "Saves and offers what you capture."),
        ["selection"] = ("Selection tools", "Convert prices, read QR codes."),
        ["snippets"] = ("Text snippets", "Text that expands as you type."),
        ["stocks"] = ("Stock prices", "Prices in the ticker."),
        ["system"] = ("System", "Battery, CPU and memory."),
        ["tour"] = ("Welcome tour", "The first-run tour; say “tour” to see it again."),
        ["wheel"] = ("Wheel", "Eight actions around the pointer on a held middle button."),
    };

    static string FeatureName(string module) => Features.TryGetValue(module, out var f) ? f.Name : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(module);

    static string FeatureHint(string module) => Features.TryGetValue(module, out var f) ? f.Hint : "";
}
