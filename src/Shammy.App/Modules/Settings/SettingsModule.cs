using System.Windows;
using System.Text;
using Shammy.Infrastructure;
using Shammy.Search;

namespace Shammy.Modules.Settings;

/// <summary>
/// The Settings card (a chip in the open notch; its detail is the designed <see cref="SettingsView"/>) and the
/// settings pipe commands: "settings-get [name]", "settings-set &lt;name&gt; &lt;value&gt;", "settings-open [tab]",
/// "settings-tab &lt;tab&gt;", "settings-state", "settings-search &lt;words&gt;", "notifications-unmute &lt;appId|all&gt;"
/// and "mic-devices".
/// Never disabled by DisabledModules, since it's how modules are turned back on.
/// </summary>
internal sealed class SettingsModule : IShammyModule
{
    public const string ModuleName = "settings";

    ModuleContext _ctx = null!;
    SettingsView? _view;

    public string Name => ModuleName;

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        ctx.AddCard(new NotchCard
        {
            Id = "settings",
            Title = "Settings",
            Width = 176,
            Order = 90,
            Chip = true,
            IsRelevant = () => false,
            Compact = SettingsTile.Build(() => ctx.OpenCard("settings")),
            Detail = BuildDetail,
        });
        // A change from the pipe shows on the card if it's open.
        ctx.Settings.Changed += OnChanged;

        ctx.AddPipeCommand("settings-get", Get);
        ctx.AddPipeCommand("settings-set", SetFromPipe);
        ctx.AddPipeCommand("settings-open", arg =>
        {
            if (arg.Trim().Length > 0) SettingsView.Tab = Tab(arg);
            ctx.OpenCard("settings");
            return "ok tab=" + SettingsView.Tab;
        });
        ctx.AddPipeCommand("settings-tab", arg =>
        {
            var tab = Tab(arg);
            if (!SettingsCatalog.Sections.Contains(tab)) return "error: tabs are " + string.Join(", ", SettingsCatalog.Sections);
            if (_view is { IsVisible: true } v) v.ShowTab(tab);
            else SettingsView.Tab = tab;
            return "ok tab=" + tab;
        });
        ctx.AddPipeCommand("settings-state", _ => _view is { IsVisible: true } v ? v.Describe() : $"closed tab={SettingsView.Tab}");
        // "settings-search eye br": types into the open card's search box.
        ctx.AddPipeCommand("settings-search", arg =>
        {
            if (_view is not { IsVisible: true } v) return "error: settings is closed";
            v.Search(arg);
            return v.Describe();
        });
        OpenAt = section => ctx.Ui.BeginInvoke(() => Open(section));
        ctx.AddPipeCommand("notifications-unmute", Unmute);
        ctx.AddPipeCommand("notifications-muted", _ => $"muted={string.Join(",", ctx.Settings.MutedNotificationApps)}");
        ctx.AddPipeCommand("mic-devices", _ =>
        {
            var names = MicRecorder.DeviceNames();
            int chosen = Shammy.Core.Settings.DeviceMatch.Find(names, ctx.Settings.MicrophoneDeviceId);
            return $"chosen={(chosen < 0 ? "default" : chosen.ToString(System.Globalization.CultureInfo.InvariantCulture))} " +
                   string.Join(" | ", names.Select((n, i) => $"{i}:{n}"));
        });
    }

    public void Dispose()
    {
        if (_ctx is not null) _ctx.Settings.Changed -= OnChanged;
    }

    void OnChanged(string name)
    {
        // The pet switch acts through the notch (it hides the face at once); from the pipe too.
        if (name == nameof(AppSettings.PetEnabled)) _ctx.Ui.BeginInvoke(() => _ctx.Notch.SetPetEnabled(_ctx.Settings.PetEnabled));
    }

    FrameworkElement BuildDetail()
    {
        // A card sheet: the island takes its width and the height of the tab (capped; tall tabs scroll).
        _view = new SettingsView(_ctx.Settings, () => _ctx.ModuleNames, () => _ctx.OpenCard("home"), _ctx.Notch.SetPetEnabled);
        return _view;
    }

    /// <summary>"pet", "voice", "Notch & pet" → the tab's name (exactly, by an old name, or by a word of it, any case).</summary>
    static string Tab(string arg) => SettingsCatalog.SectionFor(arg) ?? arg.Trim();

    /// <summary>Opens Settings (on a tab, when one is named): from the tray and the palette.</summary>
    public static Action<string?>? OpenAt { get; private set; }

    void Open(string? section)
    {
        if (section is not null && SettingsCatalog.SectionFor(section) is { } tab)
        {
            if (_view is { IsVisible: true } v) v.ShowTab(tab);
            else SettingsView.Tab = tab;
        }
        _ctx.OpenCard("settings");
    }

    /// <summary>"settings-get" lists every setting as Name=value by section; "settings-get HoverSpeed" just that one.</summary>
    string Get(string arg)
    {
        var s = _ctx.Settings;
        if (arg.Trim().Length > 0)
            return SettingsCatalog.Find(arg) is { } info ? SettingsCatalog.Describe(s, info) : $"error: no setting '{arg.Trim()}'";
        var sb = new StringBuilder();
        foreach (var group in SettingsCatalog.Settings.GroupBy(i => i.Section))
            sb.Append('[').Append(group.Key).Append("] ").Append(string.Join(" ", group.Select(i => SettingsCatalog.Describe(s, i)))).Append(' ');
        return sb.ToString().TrimEnd();
    }

    /// <summary>"settings-set Use24Hour on", "settings-set HoverSpeed relaxed", "settings-set DisabledModules music,mirror".</summary>
    string SetFromPipe(string arg)
    {
        var parts = arg.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "error: usage settings-set <name> <value>";
        if (SettingsCatalog.Find(parts[0]) is not { } info) return $"error: no setting '{parts[0]}'";
        if (SettingsCatalog.TrySet(_ctx.Settings, info, parts[1]) is { } error) return "error: " + error;
        return "ok " + SettingsCatalog.Describe(_ctx.Settings, info);
    }

    string Unmute(string arg)
    {
        var app = arg.Trim();
        if (app.Length == 0) return "error: usage notifications-unmute <appId|all>";
        var s = _ctx.Settings;
        if (app == "all")
        {
            int n = s.MutedNotificationApps.Count;
            s.MutedNotificationApps.Clear();
            s.Save();
            s.NotifyChanged(nameof(AppSettings.MutedNotificationApps));
            return $"ok unmuted {n}";
        }
        if (!s.UnmuteApp(app)) return $"error: '{app}' isn't muted";
        s.NotifyChanged(nameof(AppSettings.MutedNotificationApps));
        return "ok";
    }
}
