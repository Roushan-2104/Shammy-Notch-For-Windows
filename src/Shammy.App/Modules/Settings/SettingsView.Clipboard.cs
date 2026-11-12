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
    // ---- Clipboard -------------------------------------------------------------------------------

    /// <summary>History and how long it's kept, the copy preview, and clearing it.</summary>
    void ClipboardPage()
    {
        Border? keepRow = null;
        Row("Clipboard history", "Keeps what you copy so you can paste it again.", new ToggleSwitch(_s.ClipboardHistoryEnabled, on =>
        {
            Set(nameof(AppSettings.ClipboardHistoryEnabled), on, on ? "Clipboard history on" : "Clipboard history off");
            if (keepRow is not null) Dim(keepRow, on);
        }, "Clipboard history"));
        int[] hours = { 1, 24, 24 * 7 };
        keepRow = Row("Keep history for", "Older items clear themselves. Pinned ones stay.",
            new Segmented(new[] { "1 hour", "24 hours", "7 days" }, Array.IndexOf(hours, _s.ClipboardRetentionHours), i =>
                Set(nameof(AppSettings.ClipboardRetentionHours), hours[i], $"Keeps clipboard for {(i == 0 ? "1 hour" : i == 1 ? "24 hours" : "7 days")}")));
        Dim(keepRow, _s.ClipboardHistoryEnabled);
        SwitchRow("Copy preview", $"{PetLook.Name} shows a peek of what you copy.", nameof(AppSettings.CopyPreviewEnabled), "Copy preview on", "Copy preview off",
            words: "copied");

        if (Links.Clipboard is { } clip)
        {
            int count = clip.Items.Count, pinned = clip.Items.Count(i => i.Pinned);
            TextBlock clipHint = null!;
            var clear = ScreenKit.Secondary("Clear", () =>
            {
                int before = clip.Items.Count(i => !i.Pinned);
                bool cleared = clip.Clear();
                Say(cleared ? $"Cleared {before} items{(pinned > 0 ? $", kept {pinned} pinned" : "")}" : "Already empty");
                clipHint.Text = ClipHint(clip.Items.Count, clip.Items.Count(i => i.Pinned));
            });
            Row("Clear clipboard history", ClipHint(count, pinned), clear, out clipHint);
        }
    }

    static string ClipHint(int count, int pinned) =>
        count == 0 ? "Empty." : count == pinned ? $"Only {pinned} pinned {(pinned == 1 ? "item" : "items")}, kept." :
        $"{count} {(count == 1 ? "item" : "items")}{(pinned > 0 ? $", {pinned} pinned" : "")}.";
}
