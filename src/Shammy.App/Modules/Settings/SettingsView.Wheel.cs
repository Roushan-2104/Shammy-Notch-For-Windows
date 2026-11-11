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
    // ---- Wheel -----------------------------------------------------------------------------------

    static readonly string[] BeadPlaces = { "Top", "Top right", "Right", "Bottom right", "Bottom", "Bottom left", "Left", "Top left" };

    /// <summary>The wheel's switch, then its eight beads: what each holds, and a pin to keep it there.</summary>
    void WheelPage()
    {
        SwitchRow("Wheel", "Hold the middle mouse button still for a moment: eight actions open around the pointer. Lean toward one and let go, or press 1–8. A quick middle click still goes to your apps.",
            nameof(AppSettings.WheelEnabled), "Wheel on", "Wheel off");
        var link = Wheel.WheelLink.Current;
        if (link is null)
        {
            Row("Beads", "The wheel feature is turned off (General → Features).", null);
            return;
        }
        Section("Beads", "Pinned beads stay where they are. The others follow what you use most.");
        var actions = Shammy.Core.Wheel.WheelSlots.All;
        var names = actions.Select(a => a.Name).ToList();
        var slots = link.Slots;
        for (int i = 0; i < slots.Count && i < BeadPlaces.Length; i++)
        {
            int bead = i;
            var slot = slots[i];
            int chosen = actions.ToList().FindIndex(a => a.Id == slot.ActionId);
            var pick = Dropdown(names, chosen, 170, k =>
            {
                link.Pin(bead, actions[k].Id);
                _status.Text = $"{BeadPlaces[bead]} bead: {actions[k].Name}, pinned";
                BuildPanel(keepScroll: true);
            });
            var pin = new ToggleSwitch(slot.Pinned, on =>
            {
                if (on) link.Pin(bead, slot.ActionId);
                else link.Unpin(bead);
                _status.Text = on ? $"{BeadPlaces[bead]} bead pinned" : $"{BeadPlaces[bead]} bead learns from what you use";
                BuildPanel(keepScroll: true);
            }, $"Pin the {BeadPlaces[bead].ToLowerInvariant()} bead");
            Row($"{i + 1} · {BeadPlaces[i]}", slot.Pinned ? "Pinned" : "Learns from what you use", ScreenKit.Row(10, pick, pin));
        }
    }
}
