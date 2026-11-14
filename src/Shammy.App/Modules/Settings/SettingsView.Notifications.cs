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
    // ---- Notifications ---------------------------------------------------------------------------

    void Notifications()
    {
        SwitchRow("Show notifications in the notch", "Windows still keeps them in the Action Center.", nameof(AppSettings.ShowNotifications),
            "Notifications show in the notch", "Notifications stay in Windows only", words: "messages alerts whatsapp");
        var seconds = new SlimSlider(Math.Min(_s.NotificationPopupSeconds, 20), 2, 20, 1);
        TextBlock hint = null!;
        Row("Show for", ShowForHint(_s.NotificationPopupSeconds), seconds, out hint, words: "how long seconds notifications");
        seconds.Changed += v => hint.Text = ShowForHint(v);
        seconds.Committed += v => Set(nameof(AppSettings.NotificationPopupSeconds), v, $"Show for {v} s");
        SwitchRow("Codes in the notch", "A one-time code (OTP) in a message shows big with Copy for two minutes. Hidden while you share your screen; never logged.",
            nameof(AppSettings.OtpEnabled), "Codes show in the notch", "Codes stay in the message", words: "otp verification");
        SwitchRow("Battery and Bluetooth alerts", "Charging, low battery and earbuds connecting.", nameof(AppSettings.SystemAlertsEnabled), "System alerts on", "System alerts off");
        SwitchRow("Always hide message text", "Notifications show who, never what. While you share your screen it’s hidden anyway.",
            nameof(AppSettings.AlwaysHideNotificationText), "Message text always hidden", "Message text shows unless you’re sharing", words: "privacy");
        SwitchRow("Live cricket", "Shows the score while India is playing.", nameof(AppSettings.LiveCricketEnabled), "Live cricket on", "Live cricket off", words: "score match");

        Section("Muted apps", "Right-click any notification to mute its app.");
        if (_s.MutedNotificationApps.Count == 0)
        {
            var none = ScreenKit.Text("No muted apps. Everything can reach the notch.", 13, Weight.Regular, ScreenKit.Fg2);
            none.Margin = new Thickness(0, 10, 0, 10);
            _rows.Children.Add(none);
            return;
        }
        foreach (var app in _s.MutedNotificationApps.ToList())
        {
            var name = AppName(app);
            var initial = Initial(name);
            var unmute = ScreenKit.Secondary("Unmute", () =>
            {
                _changing = true;
                try
                {
                    _s.UnmuteApp(app);
                    _s.NotifyChanged(nameof(AppSettings.MutedNotificationApps));
                }
                finally { _changing = false; }
                Say($"{name} unmuted");
                BuildPanel(keepScroll: true);
            });
            unmute.ToolTip = $"Unmute {name}";
            Row(name, "Muted", unmute, initial);
        }
    }

    static string ShowForHint(int s) => $"{s} seconds, then it tucks away.";

    /// <summary>"Microsoft.Teams_8wekyb3d8bbwe!App" → "Teams"; "Chrome" stays.</summary>
    internal static string AppName(string appId)
    {
        var name = appId.Split('!')[0];
        int underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        if (name.Contains('\\')) name = Path.GetFileNameWithoutExtension(name);
        int dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        name = name.Trim();
        if (name.Length == 0) name = appId;
        return name.Length > 28 ? name[..28] + "…" : name;
    }
}
