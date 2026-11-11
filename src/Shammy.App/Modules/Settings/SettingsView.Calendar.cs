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
    // ---- Calendar --------------------------------------------------------------------------------

    void Calendar()
    {
        var link = Links.Calendar;
        bool connected = link?.Connected == true;
        var now = DateTime.Now;
        var tile = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(10),
            Background = new LinearGradientBrush(ScreenKit.C("#17383A"), ScreenKit.C("#10211F"), 60),
            Child = ScreenKit.Column(0,
                Center(ScreenKit.Label(now.ToString("MMM", CultureInfo.InvariantCulture), ScreenKit.B("#BFE3DC"), 9)),
                Center(ScreenKit.Display(now.Day.ToString(CultureInfo.InvariantCulture), 16, Weight.ExtraBold, 0))),
        };
        ((FrameworkElement)tile.Child).VerticalAlignment = VerticalAlignment.Center;
        string status = link is null ? "The calendar feature is turned off (General → Features)."
            : !connected ? "Not connected. Meetings won’t show in the notch."
            : link.SyncProblem ?? (link.FetchedAt is { } at ? $"Connected · updated {ClockPreference.Format(at)}" : "Connected · syncing…");
        FrameworkElement? action = link is null ? null : connected
            ? ScreenKit.Secondary("Disconnect", () =>
            {
                link.Disconnect();
                Say("Calendar disconnected");
                BuildPanel(keepScroll: true);
            })
            : ScreenKit.Secondary("Connect", () =>
            {
                link.OpenSetup();
                Say("Paste your calendar’s secret address");
            });
        Row("Google Calendar", status, action, tile);
        Row("Meetings in the notch", connected
            ? "The next meeting shows as you hover, with a nudge before it starts and Join for Meet links."
            : "Connect to see the next meeting, a nudge before it starts and Join for Meet links.", null);
    }

    static FrameworkElement Center(FrameworkElement e)
    {
        e.HorizontalAlignment = HorizontalAlignment.Center;
        return e;
    }
}
