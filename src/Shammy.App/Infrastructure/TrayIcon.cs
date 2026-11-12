using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Shammy.Core.Updates;
using Shammy.Interop;

namespace Shammy.Infrastructure;

/// <summary>
/// The tray icon and its menu: the only chrome Shammy has outside the notch. Settings… and Guide come first; every
/// switch here is also in Settings (General, Voice &amp; search, Notifications or Advanced), and both stay in step:
/// a switch saves and announces the change, and the change (from here, Settings, the palette or the pipe) is what
/// applies it.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon _icon;
    readonly Icon _image;
    readonly AppSettings _settings;
    readonly Action<string> _onChanged;

    /// <summary>The modules' tray switches (the ticker, text snippets…), so Settings and the palette can show them too.</summary>
    public static IReadOnlyList<Modules.ModuleTrayItem> Toggles { get; private set; } = Array.Empty<Modules.ModuleTrayItem>();

    /// <summary>The guide's own tray item, replaced by "Guide" at the top.</summary>
    const string OldGuideItem = "What Shammy can do";

    public TrayIcon(AppSettings settings, Action onQuit, Action<bool> onReplaceVolumeChanged, Action<bool> onVisualizerChanged,
        Action onPlacementChanged, Action onVoiceLanguageChanged, Func<bool> hinglishAvailable, Action onVoiceModeChanged,
        Action onNotificationSettingsChanged,
        IReadOnlyList<Modules.ModuleTrayItem> moduleItems, UpdateService updates, Action onRestartForUpdate)
    {
        _settings = settings;
        _image = DrawIcon();
        Toggles = moduleItems.Where(i => i.IsChecked is not null && i.SetChecked is not null).ToList();
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add(new ToolStripLabel("Shammy") { Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, System.Drawing.FontStyle.Bold) });

        var openSettings = new ToolStripMenuItem("Settings…");
        openSettings.Click += (_, _) => Modules.Settings.SettingsModule.OpenAt?.Invoke(null);
        var guide = new ToolStripMenuItem("Guide") { ToolTipText = "Everything Shammy does, in one place" };
        guide.Click += (_, _) => Modules.Guide.GuideLinks.OpenFeature?.Invoke("");
        menu.Opening += (_, _) =>
        {
            openSettings.Available = Modules.Settings.SettingsModule.OpenAt is not null;
            guide.Available = Modules.Guide.GuideLinks.OpenFeature is not null;
        };
        menu.Items.Add(openSettings);
        menu.Items.Add(guide);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripLabel("Right Alt: hold to talk · tap to type") { ForeColor = Color.Gray });
        menu.Items.Add(new ToolStripLabel("Copilot key or hold Right Ctrl: circle to search") { ForeColor = Color.Gray });
        menu.Items.Add(new ToolStripSeparator());

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = settings.StartWithWindows, CheckOnClick = true };
        // It can also be turned on from the first-run question or Settings; show the real state each time.
        menu.Opening += (_, _) =>
        {
            try { startup.Checked = settings.StartWithWindows; }
            catch (Exception) { }
        };
        startup.CheckedChanged += (_, _) =>
        {
            try
            {
                if (settings.StartWithWindows != startup.Checked) settings.StartWithWindows = startup.Checked;
            }
            catch (Exception ex) { Log.Warn("Updating startup entry failed", ex); }
        };

        // A switch over one yes/no setting: shows the setting as the menu opens, and a click saves and announces it.
        ToolStripMenuItem Switch(string text, Func<bool> get, Action<bool> set, string name)
        {
            var item = new ToolStripMenuItem(text) { Checked = get(), CheckOnClick = true };
            menu.Opening += (_, _) => item.Checked = get();
            item.CheckedChanged += (_, _) =>
            {
                if (item.Checked == get()) return;
                set(item.Checked);
                settings.Save();
                settings.NotifyChanged(name);
            };
            return item;
        }

        var volume = Switch("Replace Windows volume popup", () => settings.ReplaceVolumeFlyout, v => settings.ReplaceVolumeFlyout = v, nameof(AppSettings.ReplaceVolumeFlyout));
        var viz = Switch("Audio visualizer", () => settings.ShowVisualizer, v => settings.ShowVisualizer = v, nameof(AppSettings.ShowVisualizer));
        var compact = Switch("Compact (mobile) results layout", () => settings.CompactResults, v => settings.CompactResults = v, nameof(AppSettings.CompactResults));
        compact.ToolTipText = "Applies after restarting Shammy";
        var pin = Switch("Pin to the very top edge (over a top taskbar)", () => settings.PinToScreenEdge, v => settings.PinToScreenEdge = v, nameof(AppSettings.PinToScreenEdge));
        var autoLang = Switch("Detect voice language (Hindi etc., slower)", () => settings.VoiceLanguage == "auto",
            v => settings.VoiceLanguage = v ? "auto" : "en", nameof(AppSettings.VoiceLanguage));

        // Opt-in Hinglish model. Graphics-card PCs only: on the CPU it would take several seconds per command.
        const string HinglishText = "Hinglish voice (Hindi + English)";
        var hinglish = Switch(HinglishText, () => settings.VoiceMode == AppSettings.HinglishVoice,
            v => settings.VoiceMode = v ? AppSettings.HinglishVoice : AppSettings.EnglishVoice, nameof(AppSettings.VoiceMode));
        menu.Opening += (_, _) =>
        {
            bool available = hinglishAvailable() || hinglish.Checked;
            hinglish.Enabled = available;
            hinglish.Text = available ? HinglishText : HinglishText + " - needs a graphics card";
            hinglish.ToolTipText = available
                ? "A voice model that understands Hindi mixed with English (one-time 574 MB download)"
                : "This PC runs voice on the processor, where the Hinglish model would be too slow";
        };

        var notifications = Switch("Show notifications in the notch", () => settings.ShowNotifications, v => settings.ShowNotifications = v, nameof(AppSettings.ShowNotifications));
        // Only Windows can hide its own pop-ups; this opens the page with Do Not Disturb and per-app banners.
        var windowsPopups = new ToolStripMenuItem("Turn off Windows pop-ups and sounds…")
        {
            ToolTipText = "Turn on Do Not Disturb, or turn off \"Allow notifications to play sounds\"; Shammy keeps showing notifications with its own ping",
        };
        windowsPopups.Click += (_, _) => Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });
        var unmute = new ToolStripMenuItem("Unmute all apps") { Enabled = settings.MutedNotificationApps.Count > 0 };
        unmute.Click += (_, _) =>
        {
            settings.MutedNotificationApps.Clear();
            settings.Save();
            settings.NotifyChanged(nameof(AppSettings.MutedNotificationApps));
        };
        menu.Opening += (_, _) => unmute.Enabled = settings.MutedNotificationApps.Count > 0;

        // VoiceMode and ShowNotifications are applied by the host when they change; these by the tray's owner.
        _onChanged = name =>
        {
            void Ui(Action a)
            {
                if (System.Windows.Application.Current?.Dispatcher is { } d) d.BeginInvoke(a);
                else a();
            }
            switch (name)
            {
                case nameof(AppSettings.ReplaceVolumeFlyout): Ui(() => onReplaceVolumeChanged(settings.ReplaceVolumeFlyout)); break;
                case nameof(AppSettings.ShowVisualizer): Ui(() => onVisualizerChanged(settings.ShowVisualizer)); break;
                case nameof(AppSettings.PinToScreenEdge): Ui(onPlacementChanged); break;
                case nameof(AppSettings.VoiceLanguage): Ui(onVoiceLanguageChanged); break;
            }
        };
        settings.Changed += _onChanged;

        menu.Items.Add(startup);
        menu.Items.Add(pin);
        menu.Items.Add(autoLang);
        menu.Items.Add(hinglish);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(notifications);
        menu.Items.Add(windowsPopups);
        menu.Items.Add(unmute);
        menu.Items.Add(volume);
        menu.Items.Add(viz);
        menu.Items.Add(compact);
        var shown = moduleItems.Where(i => i.Text != OldGuideItem).ToList();
        if (shown.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            foreach (var item in shown)
            {
                var mi = new ToolStripMenuItem(item.Text);
                if (item.IsChecked is { } isChecked)
                {
                    mi.CheckOnClick = true;
                    mi.Checked = isChecked();
                    mi.CheckedChanged += (_, _) =>
                    {
                        if (mi.Checked != isChecked()) item.SetChecked?.Invoke(mi.Checked);
                    };
                    menu.Opening += (_, _) => mi.Checked = isChecked();
                }
                else
                {
                    mi.Click += (_, _) => item.OnClick?.Invoke();
                }
                if (item.IsVisible is { } isVisible)
                {
                    mi.Available = isVisible();
                    menu.Opening += (_, _) => mi.Available = isVisible();
                }
                menu.Items.Add(mi);
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        var update = new ToolStripMenuItem("Check for updates") { Enabled = updates.IsInstalled };
        if (!updates.IsInstalled) update.ToolTipText = "Install Shammy with Setup to get automatic updates";
        update.Click += (_, _) =>
        {
            if (updates.ReadyVersion is not null) onRestartForUpdate();
            else _ = updates.CheckAsync();
        };
        // The label follows the checker, including the background checks, and a check always ends (see UpdateService).
        void ShowUpdateStatus()
        {
            if (!updates.IsInstalled) return;
            update.Text = UpdateText.TrayLabel(updates.LastResult);
            update.Enabled = !UpdateText.IsBusy(updates.LastResult);
        }
        updates.Changed += ShowUpdateStatus;
        menu.Opening += (_, _) => ShowUpdateStatus();
        menu.Items.Add(new ToolStripLabel($"Version {updates.CurrentVersion}") { ForeColor = Color.Gray });
        menu.Items.Add(update);
        menu.Items.Add("Open logs folder", null, (_, _) => Process.Start(new ProcessStartInfo(Paths.Logs) { UseShellExecute = true }));
        menu.Items.Add("Quit Shammy", null, (_, _) => onQuit());

        _icon = new NotifyIcon { Icon = _image, Text = "Shammy", ContextMenuStrip = menu, Visible = true };
    }

    /// <summary>A small notch glyph drawn at runtime, so there's no binary asset to keep in sync.</summary>
    static Icon DrawIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var path = new GraphicsPath();
            var r = new RectangleF(3, 9, 26, 14);
            float d = 14;
            path.AddArc(r.X, r.Y, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(255, 18, 18, 20));
            using var pen = new Pen(Color.White, 2f);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
            using var dot = new SolidBrush(Color.FromArgb(255, 76, 194, 255));
            g.FillEllipse(dot, 19, 13, 6, 6);
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        Native.DestroyIcon(h);
        return icon;
    }

    public void Dispose()
    {
        _settings.Changed -= _onChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
