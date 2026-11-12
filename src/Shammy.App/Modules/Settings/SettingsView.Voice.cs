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
    // ---- Voice -----------------------------------------------------------------------------------

    void Voice()
    {
        var names = new List<string?> { null };
        try { names.AddRange(MicRecorder.DeviceNames()); }
        catch (Exception ex) { Log.Warn("Listing microphones failed", ex); }
        int chosen = names.FindIndex(n => n is not null && string.Equals(n, _s.MicrophoneDeviceId, StringComparison.OrdinalIgnoreCase));
        if (chosen < 0) chosen = 0;
        var labels = names.Select(n => n ?? "Windows default").ToList();
        Row("Microphone", "Used when you hold Right Alt.", Dropdown(labels, chosen, 250, i =>
            Set(nameof(AppSettings.MicrophoneDeviceId), names[i], $"Microphone · {labels[i]}")));

        // What listens when you talk, said plainly first: online (Gnani) or this PC, and in which language.
        Row("Listening now", VoiceRoute.Describe(_s, OnlineVoiceLink.Current?.HasKey == true), null);

        bool available = HostLinks.HinglishAvailable?.Invoke() ?? false;
        bool hinglish = _s.VoiceMode == AppSettings.HinglishVoice;
        if (available || hinglish)
            Row("Language on this PC", "English is quickest. Hinglish also understands “kal subah 7 baje alarm laga do” and plain English; it downloads 574 MB once.",
                new Segmented(new[] { "English", "Hinglish" }, hinglish ? 1 : 0, i =>
                {
                    Set(nameof(AppSettings.VoiceMode), i == 1 ? AppSettings.HinglishVoice : AppSettings.EnglishVoice,
                        i == 1 ? "On this PC: Hinglish · bolo, main sun raha hoon" : "On this PC: English");
                    BuildPanel(keepScroll: true);
                }));
        else
            Row("Language on this PC", "English. Hinglish on this PC needs a graphics card; with a Gnani key, Hinglish works online instead.", null);
        Row("Push to talk", "Hold to talk, tap to type.", ScreenKit.KeyCap("Right Alt"));
        Row("Circle to search", "Or hold Right Ctrl, then draw around anything.",
            ScreenKit.KeyCap("Copilot", icon: ScreenKit.Icon(ScreenKit.Paths.Copilot, ScreenKit.Fg, 12, 2)), words: "lens copilot");

        var modes = new[] { SearchMode.Ai, SearchMode.Web };
        Row("Default search", "What Enter does when you type in the palette.",
            new Segmented(new[] { "AI Mode", "Web" }, Array.IndexOf(modes, _s.DefaultSearchMode), i =>
                Set(nameof(AppSettings.DefaultSearchMode), modes[i], $"Enter now searches with {(i == 0 ? "AI Mode" : "Web")}")),
            words: "google ai web palette enter");
        OnlineVoice();
    }

    /// <summary>
    /// Online voice (Gnani): the user's own key (typed as dots, shown as "••••1234" once saved), the two switches
    /// it unlocks, the voice, and a Test that says hello or names the problem.
    /// </summary>
    void OnlineVoice()
    {
        Section("Online voice (Gnani)", "Optional. Your own key; nothing is sent while both switches are off.");
        var link = OnlineVoiceLink.Current;
        if (link is null)
        {
            Row("Gnani key", "The online voice feature is turned off (Settings › Advanced).", null);
            return;
        }
        bool hasKey = link.HasKey;
        if (hasKey)
        {
            var masked = ScreenKit.Text(link.MaskedKey, 13, Weight.SemiBold);
            masked.VerticalAlignment = VerticalAlignment.Center;
            Pressable test = null!;
            test = ScreenKit.Secondary("Test", () =>
            {
                test.Enabled = false;
                _status.Text = "Asking Gnani…";
                link.Test(problem =>
                {
                    test.Enabled = true;
                    _status.Text = problem ?? "Gnani works";
                });
            });
            test.ToolTip = "Say hello aloud";
            var remove = ScreenKit.Secondary("Remove", () =>
            {
                _changing = true;
                try { link.RemoveKey(); }
                finally { _changing = false; }
                _status.Text = "Gnani key removed";
                BuildPanel(keepScroll: true);
            });
            Row("Gnani key", "Saved on this PC, for your Windows account only.", ScreenKit.Row(8, masked, test, remove));
        }
        else
        {
            // Dots even while pasting: a key is a password, and the screen may be shared.
            var box = new PasswordBox
            {
                MaxLength = KeyMask.MaxLength,
                PasswordChar = '•',
                FontSize = 13,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Foreground = ScreenKit.Fg,
                CaretBrush = ScreenKit.Fg,
                FocusVisualStyle = null,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Paste your key",
            };
            var field = new Border
            {
                Width = 190,
                Height = 30,
                CornerRadius = new CornerRadius(8),
                Background = ScreenKit.Ground,
                BorderBrush = ScreenKit.Hairline,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 0, 9, 0),
                Child = box,
            };
            box.GotKeyboardFocus += (_, _) => field.BorderBrush = ScreenKit.Fg;
            box.LostKeyboardFocus += (_, _) => field.BorderBrush = ScreenKit.Hairline;
            void SaveKey()
            {
                if (box.Password.Trim().Length == 0) return;
                if (!link.SaveKey(box.Password))
                {
                    _status.Text = "That doesn’t look like a key";
                    return;
                }
                box.Clear();
                _status.Text = "Gnani key saved · " + link.MaskedKey + " · listening online is on";
                BuildPanel(keepScroll: true);
            }
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                SaveKey();
            };
            Row("Gnani key", "Paste your key from app.gnani.ai. It is kept on this PC only.", ScreenKit.Row(8, field, ScreenKit.Secondary("Save", SaveKey)));
        }

        const string first = "Paste your Gnani key first";
        var speaks = new ToggleSwitch(hasKey && _s.OnlineVoiceSpeaks, on => Set(nameof(AppSettings.OnlineVoiceSpeaks), on,
            on ? "Shammy speaks" : "Shammy stays quiet"), "Shammy speaks") { Enabled = hasKey };
        Row("Shammy speaks", hasKey ? "Timers, alarms and reminders are said aloud. The words are sent to Gnani." : first, speaks);
        var listens = new ToggleSwitch(hasKey && _s.OnlineVoiceListens, on =>
        {
            Set(nameof(AppSettings.OnlineVoiceListens), on, on ? "Hinglish listening online" : "Listening stays on this PC");
            BuildPanel(keepScroll: true);
        }, "Hinglish listening online") { Enabled = hasKey };
        Row("Hinglish listening online", hasKey
            ? "Your voice is sent to Gnani while you hold the talk key. Works on PCs with no graphics card." : first, listens);

        var voices = GnaniVoices.All;
        var current = GnaniVoices.Find(_s.OnlineVoiceName);
        int chosen = Math.Max(0, voices.ToList().IndexOf(current));
        var voiceRow = Row("Voice", "Who speaks. Poorvi is the Hinglish voice.", Dropdown(voices.Select(v => v.Label).ToList(), chosen, 190, i =>
            Set(nameof(AppSettings.OnlineVoiceName), voices[i].Name, $"Voice · {voices[i].Name}")));
        Dim(voiceRow, hasKey);
    }
}
