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
    // ---- Connections -----------------------------------------------------------------------------

    /// <summary>What Shammy connects to: Google Calendar and Claude Code.</summary>
    void Connections()
    {
        Section("Google Calendar", "");
        CalendarRows();
        ClaudeCodeRows();
    }

    /// <summary>Claude Code: the connection (the one button that edits Claude Code's settings) and the Allow/Deny cards.</summary>
    void ClaudeCodeRows()
    {
        Section("Claude Code", "");
        if (Shammy.Modules.Agents.AgentsLink.View is not { } view || Shammy.Modules.Agents.AgentsLink.Press is not { } press)
        {
            Row("Claude Code", "The Claude Code feature is turned off (Settings › Advanced).", null);
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
        var row = Row(view().Status, connectHint, button, out hint, words: "claude code connect agent coding");
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
            nameof(AppSettings.AgentsAskInNotch), "Asking in the notch", "Status only", words: "claude allow deny");
    }

    void CalendarRows()
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
        string status = link is null ? "The calendar feature is turned off (Settings › Advanced)."
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
