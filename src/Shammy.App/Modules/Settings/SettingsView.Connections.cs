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

    /// <summary>What Shammy connects to: Google Calendar, Claude Code and your phone.</summary>
    void Connections()
    {
        Section("Google Calendar", "");
        CalendarRows();
        ClaudeCodeRows();
        Section("Your phone", "");
        SwitchRow("Share clipboard with your phone", "Text you copy here is on your linked phone too. Skips what password managers mark private, and nothing goes while you share your screen.",
            nameof(AppSettings.ShareClipboardWithPhone), "Clipboard shared with your phone", "Clipboard stays on this laptop", words: "phone island universal clipboard copy");
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
        TextBlock hint = null!;
        string problem = "";
        Pressable button = null!;
        void Show()
        {
            var now = view();
            // Named like the Google Calendar row; how it stands goes first in the grey line.
            hint.Text = $"{now.Status} · {(problem.Length > 0 ? problem : connectHint)}";
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
        var row = Row("Claude Code", connectHint, button, out hint, words: "claude code connect agent coding");
        Show();
        row.Loaded += (_, _) =>
        {
            Shammy.Modules.Agents.AgentsLink.Changed -= Show;
            Shammy.Modules.Agents.AgentsLink.Changed += Show;
            Show();
        };
        row.Unloaded += (_, _) => Shammy.Modules.Agents.AgentsLink.Changed -= Show;
        ClaudeSignInRow();
        SwitchRow("Ask me in the notch", "Allow and Deny drop from the notch when Claude Code needs an OK. Off shows status only.",
            nameof(AppSettings.AgentsAskInNotch), "Asking in the notch", "Status only", words: "claude allow deny");
        SwitchRow("Claude's steps beside the notch", "While you're in another app, a small wing shows Claude Code's task list and last few steps, and its questions pop out there.",
            nameof(AppSettings.AgentsStepsWing), "Showing the steps", "Off", words: "claude steps progress wing questions");
    }

    /// <summary>
    /// Shows only when Claude Code is installed but signed out: Sign in starts Claude Code's own sign-in, which opens the
    /// browser. Shammy only asks Claude Code whether it's signed in; the account and its token stay Claude Code's.
    /// </summary>
    void ClaudeSignInRow()
    {
        const string SignedOut = "Claude Code is signed out, so it can’t work. Sign in opens your browser.";
        TextBlock hint = null!;
        string? exe = null;
        Pressable button = null!;
        Border row = null!;
        button = ScreenKit.Secondary("Sign in", () =>
        {
            if (exe is null || Shammy.Modules.Agents.ClaudeCli.SignIn is not null) return;
            if (Shammy.Modules.Agents.ClaudeCli.StartSignIn(exe) is not { } signIn)
            {
                SetHint("Claude Code’s sign-in couldn’t be opened. Run “claude auth login” in a terminal.");
                return;
            }
            _ = Follow(signIn);
        });
        button.MinWidth = 96;
        System.Windows.Automation.AutomationProperties.SetName(button, "Sign in to Claude Code");
        row = Row("Claude Code sign-in", "", button, out hint, words: "claude code sign in login account");
        row.Visibility = Visibility.Collapsed;

        void SetHint(string text)
        {
            hint.Text = text;
            hint.Visibility = Visibility.Visible;
        }

        // Settings rebuilds its rows often (a tab, a search, a switch): a sign-in already under way carries on in
        // the new row instead of offering a second one.
        row.Loaded += async (_, _) =>
        {
            exe = Shammy.Modules.Agents.ClaudeCli.Find();
            if (exe is null) return;
            if (Shammy.Modules.Agents.ClaudeCli.SignIn is { } running)
            {
                row.Visibility = Visibility.Visible;
                await Follow(running);
                return;
            }
            if (await Shammy.Modules.Agents.ClaudeCli.SignedInAsync(exe) != false || !row.IsLoaded) return;
            SetHint(SignedOut);
            Dim(button, true);
            row.Visibility = Visibility.Visible;
        };

        async Task Follow(Task<bool> signIn)
        {
            Dim(button, false);
            SetHint("Finish in your browser…");
            bool ok = await signIn;
            if (!row.IsLoaded) return;
            if (ok)
            {
                SetHint("Signed in to Claude Code.");
                button.Visibility = Visibility.Collapsed;
                return;
            }
            Dim(button, true);
            SetHint("Not signed in yet. Press Sign in to try again.");
        }
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
            CornerRadius = new CornerRadius(14),
            Background = Views.Cards.Sheet.GlassFill,
            BorderBrush = Views.Cards.Sheet.GlassEdge,
            BorderThickness = new Thickness(1),
            Child = ScreenKit.Column(0,
                Center(ScreenKit.Label(now.ToString("MMM", CultureInfo.InvariantCulture), Views.Cards.Ink.Soft, 9)),
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
        // The same width as Claude Code's button, so the two line up.
        if (action is Pressable p) p.MinWidth = 96;
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
