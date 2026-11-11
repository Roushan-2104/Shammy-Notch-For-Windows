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
    // ---- General ---------------------------------------------------------------------------------

    void General()
    {
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
        }, "Start with Windows"));

        var modes = new[] { SearchMode.Ai, SearchMode.Web };
        Row("Default search", "What Enter does when you type in the palette.",
            new Segmented(new[] { "AI Mode", "Web" }, Array.IndexOf(modes, _s.DefaultSearchMode), i =>
                Set(nameof(AppSettings.DefaultSearchMode), modes[i], $"Enter now searches with {(i == 0 ? "AI Mode" : "Web")}")));
        SwitchRow("Gestures", "Wheel over the notch for volume, swipe sideways to skip a track.", nameof(AppSettings.GesturesEnabled), "Gestures on", "Gestures off");
        SwitchRow("Notch sounds", "A soft ping for notifications, a chime for timers.", nameof(AppSettings.NotchSounds), "Sounds on", "Sounds off");

        var volume = new SlimSlider(_s.NotchSoundVolume, 0, 100, 10);
        TextBlock volumeHint = null!;
        Row("Sound volume", VolumeHint(_s.NotchSoundVolume), volume, out volumeHint);
        volume.Changed += v => volumeHint.Text = VolumeHint(v);
        volume.Committed += v =>
        {
            Set(nameof(AppSettings.NotchSoundVolume), v, $"Volume {v}%");
            if (_s.NotchSounds) NotchSound.Play(Shammy.Core.Audio.PingKind.Notification);
        };

        ClaudeCodeRows();

        Section("Features", "Turned-off features stay off from the next start.");
        foreach (var name in _modules().Where(m => m != SettingsModule.ModuleName).Order())
        {
            bool off = _s.DisabledModules.Contains(name, StringComparer.OrdinalIgnoreCase);
            Row(FeatureName(name), FeatureHint(name), new ToggleSwitch(!off, on =>
            {
                var list = _s.DisabledModules.Where(m => !string.Equals(m, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (!on) list.Add(name);
                Set(nameof(AppSettings.DisabledModules), list, $"{FeatureName(name)} {(on ? "on" : "off")} after a restart");
            }, FeatureName(name)));
        }

        UpdateRow();
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
        Row($"Version {HostLinks.Version?.Invoke() ?? "?"}", UpdateText.Hint(Status().Result, Status().Installed), slot, out hint);
        Show();
        slot.Loaded += (_, _) =>
        {
            HostLinks.UpdateChanged += Show;
            Show();
        };
        slot.Unloaded += (_, _) => HostLinks.UpdateChanged -= Show;
    }

    /// <summary>Claude Code: the connection (the one button that edits Claude Code's settings) and the Allow/Deny cards.</summary>
    void ClaudeCodeRows()
    {
        Section("Claude Code", "");
        if (Shammy.Modules.Agents.AgentsLink.View is not { } view || Shammy.Modules.Agents.AgentsLink.Press is not { } press)
        {
            Row("Not available", "The Claude Code feature is turned off (see Features below).", null);
            return;
        }
        const string connectHint = Shammy.Core.Agents.AgentText.ConnectHint;
        TextBlock hint = null!, title = null!;
        string problem = "";
        Pressable button = null!;
        void Show()
        {
            var now = view();
            title.Text = now.Status;
            hint.Text = problem.Length > 0 ? problem : connectHint;
            ((TextBlock)button.Child).Text = now.Button;
            Dim(button, now.Enabled);
        }
        button = ScreenKit.Secondary(view().Button, () =>
        {
            var (ok, said) = press();
            problem = ok ? "" : said;
            if (ok) Say(said);
            Show();
        });
        button.MinWidth = 96;
        var row = Row(view().Status, connectHint, button, out hint);
        title = (TextBlock)((StackPanel)hint.Parent).Children[0];
        Show();
        row.Loaded += (_, _) =>
        {
            Shammy.Modules.Agents.AgentsLink.Changed -= Show;
            Shammy.Modules.Agents.AgentsLink.Changed += Show;
            Show();
        };
        row.Unloaded += (_, _) => Shammy.Modules.Agents.AgentsLink.Changed -= Show;
        SwitchRow("Ask me in the notch", "Allow and Deny drop from the notch when Claude Code needs an OK. Off shows status only.",
            nameof(AppSettings.AgentsAskInNotch), "Asking in the notch", "Status only");
    }

    static string VolumeHint(int v) => v == 0 ? "0% · alarms still ring at 20%" : $"{v}% · pings, chimes and alarms";

    static readonly Dictionary<string, (string Name, string Hint)> Features = new(StringComparer.OrdinalIgnoreCase)
    {
        ["agents"] = ("Claude Code", "Sessions at work, done, or waiting for your OK."),
        ["ambient"] = ("Weather and pet", "The sky, the ticker and the pet’s moods."),
        ["whatsnew"] = ("What’s new", "A short note after each update."),
        ["calendar"] = ("Calendar", "Your Google Calendar in the notch."),
        ["captions"] = ("Live captions", "Captions for anything playing, made on this PC."),
        ["clipboard"] = ("Clipboard", "History, screenshots, copy previews."),
        ["connect"] = ("Connect", "Downloads and the links between features."),
        ["cricket"] = ("Cricket scores", "Live scores in the ticker."),
        ["crypto"] = ("Crypto prices", "Prices in the ticker."),
        ["dropactions"] = ("Drop actions", "Drag files onto the notch to convert or share."),
        ["guide"] = ("Feature guide", "Everything Shammy does, in one place."),
        ["launcher"] = ("App launcher", "“open spotify” from the palette."),
        ["levels"] = ("Volume and brightness", "The notch’s own volume and brightness HUD."),
        ["meeting"] = ("Meetings", "Mic and camera pills during calls."),
        ["mirror"] = ("Mirror", "A quick camera check."),
        ["mixer"] = ("Volume mixer", "A volume slider for each app."),
        ["music"] = ("Music", "Now playing, lyrics, play commands."),
        ["nudges"] = ("Eye breaks and water", "Gentle nudges from the pet."),
        ["onlinevoice"] = ("Online voice (Gnani)", "Speaking and Hinglish listening with your own Gnani key."),
        ["palette"] = ("Palette", "Search apps, files and settings as you type."),
        ["phonesend"] = ("Send to phone", "Files to your phone over Wi-Fi."),
        ["productivity"] = ("Timers, alarms and notes", "Timers, pomodoro, reminders, alarms, notes."),
        ["prompter"] = ("Teleprompter", "Your script under the camera."),
        ["quick"] = ("Quick actions", "Keep awake, lock, sleep and mute in one place."),
        ["record"] = ("Screen recording", "Record the screen from the notch."),
        ["screenshot"] = ("Screenshots", "Saves and offers what you capture."),
        ["selection"] = ("Selection tools", "Convert prices, read QR codes."),
        ["snippets"] = ("Snippets", "Text that expands as you type."),
        ["stocks"] = ("Stock prices", "Prices in the ticker."),
        ["system"] = ("System", "Battery, CPU and memory."),
    };

    static string FeatureName(string module) => Features.TryGetValue(module, out var f) ? f.Name : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(module);

    static string FeatureHint(string module) => Features.TryGetValue(module, out var f) ? f.Hint : "";
}
