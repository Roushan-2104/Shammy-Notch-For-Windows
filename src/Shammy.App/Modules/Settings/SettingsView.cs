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

/// <summary>
/// The Settings screen (the designed detail, 892×232 inside the notch's card frame): seven tabs on the left,
/// a scrolling panel of standard rows on the right (switches, segments, sliders, steppers, menus), and above it
/// a status line ("Saved · Hover speed Quick") with Done. Every control writes straight to
/// <see cref="AppSettings"/> through <see cref="SettingsCatalog"/> (saved at once); changes made elsewhere (the
/// pipe, the tray) show up while it's open. Accent only on the active tab's bar and Done.
/// </summary>
internal sealed class SettingsView : Grid
{
    readonly Action<bool> _setPet;
    readonly AppSettings _s;
    readonly Func<IReadOnlyList<string>> _modules;
    readonly Action _done;
    readonly StackPanel _tabs = new();
    readonly StackPanel _rows = new();
    readonly ScrollViewer _scroll;
    readonly TextBlock _status = ScreenKit.Text("Saved automatically", 13, Weight.Regular, ScreenKit.Fg2);
    readonly Canvas _overlay = new();
    static string _tab = SettingsCatalog.Sections[0];
    bool _changing, _hooked, _rebuildQueued;

    /// <param name="setPet">Shows or hides the pet on the notch (and saves it).</param>
    public SettingsView(AppSettings settings, Func<IReadOnlyList<string>> modules, Action done, Action<bool> setPet, string? tab = null)
    {
        _setPet = setPet;
        _s = settings;
        _modules = modules;
        _done = done;
        if (tab is not null && SettingsCatalog.Sections.Contains(tab)) _tab = tab;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(188) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        ColumnDefinitions.Add(new ColumnDefinition());

        Children.Add(_tabs);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        right.RowDefinitions.Add(new RowDefinition());
        SetColumn(right, 2);
        Children.Add(right);
        right.Children.Add(_status);
        var done2 = ScreenKit.Primary("Done", done, 32, 13);
        done2.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(done2);

        _scroll = new ScrollViewer
        {
            Content = _rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Padding = new Thickness(18, 4, 18, 8),
        };
        _scroll.ScrollChanged += (_, _) => CloseMenu();
        var panel = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = ScreenKit.Tile,
            BorderBrush = ScreenKit.Hairline,
            BorderThickness = new Thickness(1),
            Child = _scroll,
            ClipToBounds = true,
        };
        SetRow(panel, 2);
        right.Children.Add(panel);

        // Menus float over everything here; a click anywhere else closes them.
        SetColumnSpan(_overlay, 3);
        _overlay.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource == _overlay) CloseMenu();
        };
        Children.Add(_overlay);

        Loaded += (_, _) => Hook(true);
        Unloaded += (_, _) => Hook(false);
        BuildTabs();
        BuildPanel();
    }

    /// <summary>The tab on show (the pipe and the tour can open a particular one).</summary>
    public static string Tab
    {
        get => _tab;
        set
        {
            if (SettingsCatalog.Sections.Contains(value)) _tab = value;
        }
    }

    void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on)
        {
            _s.Changed += OnSettingChanged;
        }
        else
        {
            _s.Changed -= OnSettingChanged;
            CloseMenu();
        }
    }


    /// <summary>A change from somewhere else (the pipe, a voice command) rebuilds the panel; our own don't.</summary>
    void OnSettingChanged(string name)
    {
        if (_changing || _rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _rebuildQueued = false;
            if (IsLoaded) BuildPanel(keepScroll: true);
        });
    }

    /// <summary>For the pipe: the tab, the status and the rows' titles.</summary>
    public string Describe() =>
        $"tab={_tab} status='{_status.Text}' rows=" + string.Join(" | ", _rows.Children.OfType<FrameworkElement>().Select(r => r.Tag as string).Where(t => t is not null));

    // ---- Tabs ------------------------------------------------------------------------------------

    void BuildTabs()
    {
        _tabs.Children.Clear();
        foreach (var name in SettingsCatalog.Sections)
        {
            bool on = name == _tab;
            var text = ScreenKit.Text(name, 13, Weight.SemiBold, on ? ScreenKit.Fg : ScreenKit.Fg2);
            var bar = new Border
            {
                Width = 3,
                Height = 16,
                CornerRadius = new CornerRadius(2),
                Background = on ? ScreenKit.Accent : Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(-12, 0, 0, 0),
            };
            var grid = new Grid { Children = { bar, text } };
            var tab = new Pressable(on ? ScreenKit.Tile : Brushes.Transparent, ScreenKit.Tile)
            {
                Height = 30,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 0, 12, 0),
                Margin = new Thickness(0, 0, 0, 2),
                Child = grid,
                PressScale = 0.98,
            };
            tab.MouseEnter += (_, _) => text.Foreground = ScreenKit.Fg;
            tab.MouseLeave += (_, _) => text.Foreground = name == _tab ? ScreenKit.Fg : ScreenKit.Fg2;
            tab.Click += () => ShowTab(name);
            _tabs.Children.Add(tab);
        }
    }

    public void ShowTab(string name)
    {
        if (!SettingsCatalog.Sections.Contains(name)) return;
        _tab = name;
        CloseMenu();
        BuildTabs();
        BuildPanel();
        ScreenKit.Rise(_rows, 0, 6);
    }

    void BuildPanel(bool keepScroll = false)
    {
        double offset = keepScroll ? _scroll.VerticalOffset : 0;
        _rows.Children.Clear();
        switch (_tab)
        {
            case "General": General(); break;
            case "Notch & pet": NotchAndPet(); break;
            case "Voice": Voice(); break;
            case "Notifications": Notifications(); break;
            case "Time & units": TimeAndUnits(); break;
            case "Clipboard & privacy": ClipboardAndPrivacy(); break;
            case "Calendar": Calendar(); break;
        }
        // The last row has no hairline under it.
        if (_rows.Children.Count > 0 && _rows.Children[^1] is Border { Tag: string } last) last.BorderThickness = new Thickness(0);
        if (keepScroll) Dispatcher.BeginInvoke(() => _scroll.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
        else _scroll.ScrollToTop();
    }

    // ---- Saving ----------------------------------------------------------------------------------

    void Set(string name, object? value, string said)
    {
        if (SettingsCatalog.Find(name) is not { } info) return;
        _changing = true;
        try { SettingsCatalog.Set(_s, info, value); }
        finally { _changing = false; }
        Say(said);
    }

    void Say(string said) => _status.Text = said.StartsWith("Saved", StringComparison.Ordinal) ? said : "Saved · " + said;

    // ---- Rows ------------------------------------------------------------------------------------

    /// <summary>A standard row: title and a one-line hint on the left, the control on the right, a hairline under it.</summary>
    Border Row(string title, string hint, FrameworkElement? control, out TextBlock hintText, FrameworkElement? lead = null)
    {
        var grid = new Grid { MinHeight = 56 - 18 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hintText = ScreenKit.Text(hint, 12, Weight.Regular, ScreenKit.Fg2);
        hintText.TextWrapping = TextWrapping.Wrap;
        hintText.TextTrimming = TextTrimming.None;
        hintText.LineHeight = 16.8;
        hintText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        var words = ScreenKit.Column(2, ScreenKit.Text(title, 14, Weight.SemiBold), hintText);
        words.VerticalAlignment = VerticalAlignment.Center;
        if (hint.Length == 0) hintText.Visibility = Visibility.Collapsed;
        if (lead is not null)
        {
            lead.Margin = new Thickness(0, 0, 14, 0);
            lead.VerticalAlignment = VerticalAlignment.Center;
            var both = new StackPanel { Orientation = Orientation.Horizontal, Children = { lead, words } };
            grid.Children.Add(both);
        }
        else grid.Children.Add(words);
        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(20, 0, 0, 0);
            SetColumn(control, 1);
            grid.Children.Add(control);
        }
        var row = new Border
        {
            BorderBrush = ScreenKit.Hairline,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 9, 0, 9),
            Child = grid,
            Tag = title,
        };
        _rows.Children.Add(row);
        return row;
    }

    Border Row(string title, string hint, FrameworkElement? control, FrameworkElement? lead = null) => Row(title, hint, control, out _, lead);

    Border SwitchRow(string title, string hint, string setting, string saidOn, string saidOff)
    {
        bool on = (bool)(SettingsCatalog.Find(setting) is { } info ? SettingsCatalog.Get(_s, info) ?? false : false);
        return Row(title, hint, new ToggleSwitch(on, v => Set(setting, v, v ? saidOn : saidOff), title));
    }

    void Section(string label, string aside)
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 4) };
        grid.Children.Add(ScreenKit.Label(label));
        if (aside.Length > 0)
        {
            var a = ScreenKit.Text(aside, 12, Weight.Regular, ScreenKit.Fg2);
            a.HorizontalAlignment = HorizontalAlignment.Right;
            grid.Children.Add(a);
        }
        _rows.Children.Add(grid);
    }

    static void Dim(FrameworkElement e, bool on)
    {
        e.Opacity = on ? 1 : 0.4;
        e.IsHitTestVisible = on;
    }

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

    // ---- Notch & pet -------------------------------------------------------------------------------

    void NotchAndPet()
    {
        var speeds = new[] { HoverSpeed.Quick, HoverSpeed.Normal, HoverSpeed.Relaxed };
        Row("Hover speed", "How long you rest the pointer before the notch opens.",
            new Segmented(new[] { "Quick", "Normal", "Relaxed" }, Array.IndexOf(speeds, _s.HoverSpeed), i =>
                Set(nameof(AppSettings.HoverSpeed), speeds[i], $"Hover speed · {speeds[i]}")));
        SwitchRow("Peek", "A quick glance when you rest the pointer, before it opens.", nameof(AppSettings.PeekEnabled), "Peek on", "Peek off");

        // The pet: a little notch preview, the switch, then its eyes and name (dimmed while it's off).
        var dependents = new List<FrameworkElement>();
        var face = new PetFace(0.82, FaceMood.Idle);
        var preview = PetPreview(face);
        TextBlock petHint = null!;
        var petSwitch = new ToggleSwitch(_s.PetEnabled, on =>
        {
            _changing = true;
            try
            {
                _setPet(on);
                _s.NotifyChanged(nameof(AppSettings.PetEnabled));
            }
            finally { _changing = false; }
            petHint.Text = PetHint(on);
            preview.Opacity = on ? 1 : 0.4;
            foreach (var d in dependents) Dim(d, on);
            Say(on ? $"{PetLook.Name} is back" : $"{PetLook.Name} is hiding. The eyes stay as Home.");
        }, "Show the pet in the notch");
        preview.Opacity = _s.PetEnabled ? 1 : 0.4;
        Row("Pet", PetHint(_s.PetEnabled), petSwitch, out petHint, preview);

        var kinds = new[] { PetKind.Cat, PetKind.Eyes };
        Border purrRow = null!;
        var kindRow = Row("Pet style", "A cat is the same eyes with ears, whiskers and a nose. Eyes is the eyes alone.",
            new Segmented(new[] { "Cat", "Eyes" }, Array.IndexOf(kinds, _s.PetKind), i =>
            {
                Set(nameof(AppSettings.PetKind), kinds[i], kinds[i] == PetKind.Cat ? $"{PetLook.Name} is a cat" : "Just the eyes");
                face.Restyle();
                Dim(purrRow, kinds[i] == PetKind.Cat && _s.PetEnabled);
            }));
        dependents.Add(kindRow);
        purrRow = SwitchRow("Purr", "A soft purr when your pointer rests on the cat. Follows Notch sounds.", nameof(AppSettings.PetPurrs), "Purr on", "Purr off");
        dependents.Add(purrRow);

        var styles = new[] { PetEyeStyle.Round, PetEyeStyle.Oval, PetEyeStyle.Pixel };
        var styleRow = Row("Eye style", "Shapes the eyes in every mood.",
            new Segmented(new[] { "Round", "Oval", "Pixel" }, Array.IndexOf(styles, _s.PetEyeStyle), i =>
            {
                Set(nameof(AppSettings.PetEyeStyle), styles[i], $"Eyes · {styles[i]}");
                face.Mood = FaceMood.Happy;
                Dispatcher.BeginInvoke(() => Calm(face), System.Windows.Threading.DispatcherPriority.Background);
            }, icon: i => EyeIcon(styles[i])));
        dependents.Add(styleRow);

        TextBlock colourHint = null!;
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var colour in Enum.GetValues<PetEyeColor>())
        {
            var dot = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = PetLook.Solid(colour) };
            var ring = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(2),
                BorderBrush = colour == _s.PetEyeColor ? ScreenKit.Fg : Brushes.Transparent,
                Child = dot,
            };
            var swatch = new Pressable(Brushes.Transparent, Brushes.Transparent) { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Child = ring, ToolTip = $"Eye colour {colour}", Margin = new Thickness(swatches.Children.Count > 0 ? 4 : 0, 0, 0, 0) };
            swatch.Click += () =>
            {
                foreach (Pressable p in swatches.Children) ((Border)p.Child).BorderBrush = Brushes.Transparent;
                ring.BorderBrush = ScreenKit.Fg;
                colourHint.Text = $"{colour} · shows in every mood";
                Set(nameof(AppSettings.PetEyeColor), colour, $"Eyes · {colour}");
            };
            swatches.Children.Add(swatch);
        }
        var colourRow = Row("Eye colour", $"{_s.PetEyeColor} · shows in every mood", swatches, out colourHint);
        dependents.Add(colourRow);

        TextBlock nameHint = null!;
        var presets = PetLooks.NamePresets.Take(3).ToList();
        var box = NameBox(_s.PetName);
        Segmented presetSeg = null!;
        presetSeg = new Segmented(presets, presets.IndexOf(_s.PetName), i =>
        {
            box.Text = presets[i];
            SaveName(presets[i]);
        }, minWidth: 54);
        void SaveName(string name)
        {
            name = PetLooks.Name(name);
            Set(nameof(AppSettings.PetName), name, $"Hi, I’m {name}!");
            nameHint.Text = NameHint(name);
            presetSeg.Index = presets.IndexOf(name);
            if (preview.Tag is SpacedText label) label.Text = name.ToUpperInvariant();
            face.Mood = FaceMood.Happy;
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            SaveName(box.Text);
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            if (PetLooks.Name(box.Text) != _s.PetName) SaveName(box.Text);
        };
        var nameControls = new StackPanel { Orientation = Orientation.Horizontal, Children = { presetSeg, Field(box, 120) } };
        ((FrameworkElement)nameControls.Children[1]).Margin = new Thickness(8, 0, 0, 0);
        var nameRow = Row("Name", NameHint(_s.PetName), nameControls, out nameHint);
        dependents.Add(nameRow);
        foreach (var d in dependents) Dim(d, _s.PetEnabled);
        if (_s.PetKind != PetKind.Cat) Dim(purrRow, false);

        // Nudges: small reminders from the pet.
        SwitchRow("Eye breaks", "Every 20 minutes, the pet rests its eyes with you for 20 seconds.", nameof(AppSettings.EyeBreaksEnabled), "Eye breaks on", "Eye breaks off");
        SwitchRow("Water reminders", "A gentle reminder to drink water every hour and a half.", nameof(AppSettings.WaterRemindersEnabled), "Water reminders on", "Water reminders off");

        LookAndPlace();
        HiddenApps();
    }

    /// <summary>Motion, size, accent colour, which screen, and the lyric line.</summary>
    void LookAndPlace()
    {
        Section("Look", "");
        var motions = new[] { MotionStyle.Lively, MotionStyle.Calm };
        Row("Motion", "Calm stops the bounce, the spinning record and the pet’s little extras.",
            new Segmented(new[] { "Lively", "Calm" }, Array.IndexOf(motions, _s.Motion), i =>
                Set(nameof(AppSettings.Motion), motions[i], $"Motion · {motions[i]}")));

        var sizes = new[] { NotchSize.Small, NotchSize.Normal, NotchSize.Large };
        Row("Notch size", "Makes everything in the notch larger or smaller.",
            new Segmented(new[] { "Small", "Normal", "Large" }, Array.IndexOf(sizes, _s.NotchSize), i =>
                Set(nameof(AppSettings.NotchSize), sizes[i], $"Notch size · {sizes[i]}")));

        TextBlock accentHint = null!;
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var colour in Enum.GetValues<AccentColor>())
        {
            var dot = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = ScreenKit.B(NotchLook.Colours(colour).Accent) };
            var ring = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(2),
                BorderBrush = colour == _s.Accent ? ScreenKit.Fg : Brushes.Transparent,
                Child = dot,
            };
            var swatch = new Pressable(Brushes.Transparent, Brushes.Transparent) { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Child = ring, ToolTip = $"Accent colour {colour}", Margin = new Thickness(swatches.Children.Count > 0 ? 4 : 0, 0, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(swatch, $"Accent colour {colour}");
            swatch.Click += () =>
            {
                foreach (Pressable p in swatches.Children) ((Border)p.Child).BorderBrush = Brushes.Transparent;
                ring.BorderBrush = ScreenKit.Fg;
                accentHint.Text = $"{colour} · buttons and highlights";
                Set(nameof(AppSettings.Accent), colour, $"Accent · {colour}");
            };
            swatches.Children.Add(swatch);
        }
        Row("Accent colour", $"{_s.Accent} · buttons and highlights", swatches, out accentHint);

        var screens = System.Windows.Forms.Screen.AllScreens;
        var names = screens.Select(s => s.DeviceName).ToList();
        var primary = screens.FirstOrDefault(s => s.Primary)?.DeviceName ?? names.FirstOrDefault() ?? "";
        var options = NotchDisplay.Options(names, primary);
        if (options.Count < 2)
            Row("Show on", "Only one screen is connected. With a second one you can choose where the notch lives.", null);
        else
        {
            int chosen = Math.Max(0, options.ToList().FindIndex(o => o.Value.Equals(_s.NotchDisplay, StringComparison.OrdinalIgnoreCase)));
            Row("Show on", "Which screen the notch lives on.",
                new Segmented(options.Select(o => o.Label).ToList(), chosen, i =>
                    Set(nameof(AppSettings.NotchDisplay), options[i].Value, $"Showing on · {options[i].Label}"), minWidth: 54));
        }

        SwitchRow("Lyric line", "The line being sung shows under the record, for songs with synced lyrics.", nameof(AppSettings.LyricLineEnabled), "Lyric line on", "Lyric line off");
    }

    /// <summary>The apps the notch stays away from, with one to add from what is running now.</summary>
    void HiddenApps()
    {
        Section("Hide while using", "The notch stays away while these apps are in front.");
        foreach (var app in _s.HiddenApps.ToList())
        {
            var initial = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(7),
                Background = ScreenKit.Hairline,
                Child = ScreenKit.Text(app[..1].ToUpperInvariant(), 12, Weight.Bold),
            };
            ((TextBlock)initial.Child).HorizontalAlignment = HorizontalAlignment.Center;
            var remove = ScreenKit.Secondary("Remove", () =>
            {
                Set(nameof(AppSettings.HiddenApps), _s.HiddenApps.Where(a => a != app).ToList(), $"The notch shows in {app} again");
                BuildPanel(keepScroll: true);
            });
            remove.ToolTip = $"Stop hiding in {app}";
            Row(app, "The notch hides while this is in front", remove, initial);
        }

        var running = RunningApps().Where(a => !_s.HiddenApps.Contains(a)).ToList();
        if (running.Count == 0)
        {
            Row("Add an app", "Open the app first, then come back here to pick it.", null);
            return;
        }
        var labels = new List<string> { "Choose an app" };
        labels.AddRange(running);
        Row("Add an app", "Pick from the apps that are open now.", Dropdown(labels, 0, 170, i =>
        {
            if (i == 0) return;
            var app = running[i - 1];
            Set(nameof(AppSettings.HiddenApps), _s.HiddenApps.Append(app).ToList(), $"The notch hides in {app}");
            BuildPanel(keepScroll: true);
        }));
    }

    /// <summary>Apps with a window open right now, by plain name (never Shammy itself).</summary>
    static List<string> RunningApps()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                        var key = HideRules.Key(p.ProcessName);
                        if (key.Length > 0 && key != "shammy" && key != "explorer" && key != "textinputhost" && key != "applicationframehost") names.Add(key);
                    }
                    catch (Exception)
                    {
                        // A protected process: skip it.
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Listing running apps failed", ex);
        }
        return names.ToList();
    }

    static void Calm(PetFace face)
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            face.Mood = FaceMood.Idle;
        };
        t.Start();
    }

    static string PetHint(bool on) => on ? $"{PetLook.Name} lives in the notch and reacts to what you do." : "Off: the notch shows plain status, no face.";

    static string NameHint(string name) => $"Shows in the tour and here. Hi, {name}!";

    /// <summary>The mockup's mini notch: the pet's name, its face, the temperature (or the time).</summary>
    FrameworkElement PetPreview(PetFace face)
    {
        var grid = new Grid { Width = 200, Height = 34 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var name = ScreenKit.Label(PetLook.Name, size: 10);
        name.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(name);
        face.HorizontalAlignment = HorizontalAlignment.Center;
        face.VerticalAlignment = VerticalAlignment.Center;
        face.Margin = new Thickness(0, -2, 0, 0);
        SetColumn(face, 1);
        grid.Children.Add(face);
        var now = DateTime.Now;
        var value = ScreenKit.Text(AlarmText.Temperature(HostLinks.WeatherGlance()) ?? ClockPreference.Parts(now.Hour, now.Minute).Time, 13, Weight.Bold);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        SetColumn(value, 2);
        grid.Children.Add(value);
        return new Border
        {
            Width = 200,
            Height = 34,
            CornerRadius = new CornerRadius(0, 0, 17, 17),
            Background = ScreenKit.Ground,
            Padding = new Thickness(12, 0, 12, 0),
            Child = grid,
            ClipToBounds = true,
            Tag = name,
        };
    }

    static FrameworkElement EyeIcon(PetEyeStyle style)
    {
        var (w, h, r) = style switch
        {
            PetEyeStyle.Oval => (6.0, 11.0, 3.0),
            PetEyeStyle.Pixel => (7.0, 7.0, 1.0),
            _ => (7.0, 9.0, 3.5),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < 2; i++)
            row.Children.Add(new Border { Width = w, Height = h, CornerRadius = new CornerRadius(r), Background = PetLook.Solid(), Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0) });
        return row;
    }

    static TextBox NameBox(string text) => new()
    {
        Style = Application.Current?.TryFindResource("BareTextBox") as Style,
        Text = text,
        MaxLength = PetLooks.MaxNameLength,
        FontSize = 13,
        FontFamily = ScreenKit.Font(Weight.SemiBold).Family,
        FontWeight = FontWeights.SemiBold,
        Foreground = ScreenKit.Fg,
        CaretBrush = ScreenKit.Fg,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    /// <summary>A text box in the wall's black well, with a white ring while typing.</summary>
    static Border Field(TextBox box, double width)
    {
        var field = new Border
        {
            Width = width,
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
        return field;
    }

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

        bool available = HostLinks.HinglishAvailable?.Invoke() ?? false;
        bool hinglish = _s.VoiceMode == AppSettings.HinglishVoice;
        var toggle = new ToggleSwitch(hinglish, on => Set(nameof(AppSettings.VoiceMode), on ? AppSettings.HinglishVoice : AppSettings.EnglishVoice,
            on ? "Hinglish on · bolo, main sun raha hoon" : "English only"), "Hinglish voice");
        if (!available && !hinglish) toggle.Enabled = false;
        Row("Hinglish voice", available || hinglish
            ? "Understands “kal subah 7 baje alarm laga do”. Downloads 574 MB once."
            : "Needs voice on a graphics card; this PC runs it on the processor.", toggle);
        Row("Push to talk", "Hold to talk, tap to type.", ScreenKit.KeyCap("Right Alt"));
        Row("Circle to search", "Or hold Right Ctrl, then draw around anything.",
            ScreenKit.KeyCap("Copilot", icon: ScreenKit.Icon(ScreenKit.Paths.Copilot, ScreenKit.Fg, 12, 2)));
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
            Row("Gnani key", "The online voice feature is turned off (General → Features).", null);
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
                _status.Text = "Gnani key saved · " + link.MaskedKey;
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
        var listens = new ToggleSwitch(hasKey && _s.OnlineVoiceListens, on => Set(nameof(AppSettings.OnlineVoiceListens), on,
            on ? "Hinglish listening online" : "Listening stays on this PC"), "Hinglish listening online") { Enabled = hasKey };
        Row("Hinglish listening online", hasKey
            ? "Your voice is sent to Gnani while you hold the talk key. Works on PCs with no graphics card." : first, listens);

        var voices = GnaniVoices.All;
        var current = GnaniVoices.Find(_s.OnlineVoiceName);
        int chosen = Math.Max(0, voices.ToList().IndexOf(current));
        var voiceRow = Row("Voice", "Who speaks. Poorvi is the Hinglish voice.", Dropdown(voices.Select(v => v.Label).ToList(), chosen, 190, i =>
            Set(nameof(AppSettings.OnlineVoiceName), voices[i].Name, $"Voice · {voices[i].Name}")));
        Dim(voiceRow, hasKey);
    }

    // ---- Notifications ---------------------------------------------------------------------------

    void Notifications()
    {
        SwitchRow("Show notifications in the notch", "Windows still keeps them in the Action Center.", nameof(AppSettings.ShowNotifications),
            "Notifications show in the notch", "Notifications stay in Windows only");
        var seconds = new SlimSlider(Math.Min(_s.NotificationPopupSeconds, 20), 2, 20, 1);
        TextBlock hint = null!;
        Row("Show for", ShowForHint(_s.NotificationPopupSeconds), seconds, out hint);
        seconds.Changed += v => hint.Text = ShowForHint(v);
        seconds.Committed += v => Set(nameof(AppSettings.NotificationPopupSeconds), v, $"Show for {v} s");
        SwitchRow("Battery and Bluetooth alerts", "Charging, low battery and earbuds connecting.", nameof(AppSettings.SystemAlertsEnabled), "System alerts on", "System alerts off");
        SwitchRow("Live cricket", "Shows the score while India is playing.", nameof(AppSettings.LiveCricketEnabled), "Live cricket on", "Live cricket off");

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
            var initial = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(7),
                Background = ScreenKit.Hairline,
                Child = ScreenKit.Text(name[..1].ToUpperInvariant(), 12, Weight.Bold),
            };
            ((TextBlock)initial.Child).HorizontalAlignment = HorizontalAlignment.Center;
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

    // ---- Time & units ----------------------------------------------------------------------------

    void TimeAndUnits()
    {
        bool?[] clocks = { null, false, true };
        TextBlock clockHint = null!;
        Row("Clock", ClockHint(_s.Use24Hour), new Segmented(new[] { "Windows", "12-hour", "24-hour" }, Array.IndexOf(clocks, _s.Use24Hour), i =>
        {
            Set(nameof(AppSettings.Use24Hour), clocks[i], $"Clock · {(i == 0 ? "follows Windows" : i == 1 ? "12-hour" : "24-hour")}");
            clockHint.Text = ClockHint(clocks[i]);
        }), out clockHint);

        var units = new[] { TemperatureUnit.Celsius, TemperatureUnit.Fahrenheit };
        var weather = HostLinks.WeatherGlance();
        Row("Temperature", weather is not null ? $"Now: {weather}" : "Weather and conversions use this.",
            new Segmented(new[] { "°C", "°F" }, Array.IndexOf(units, _s.TemperatureUnit), i =>
                Set(nameof(AppSettings.TemperatureUnit), units[i], i == 0 ? "Celsius" : "Fahrenheit")));

        var currencies = Currencies.Where(c => Shammy.Core.Conversions.Currency.Supported.Contains(c.Code) || c.Code == _s.HomeCurrency).ToList();
        if (currencies.All(c => c.Code != _s.HomeCurrency)) currencies.Insert(0, (_s.HomeCurrency, _s.HomeCurrency));
        Row("Home currency", "“100 usd” converts to this.", Dropdown(currencies.Select(c => $"{c.Code} · {c.Name}").ToList(),
            currencies.FindIndex(c => c.Code == _s.HomeCurrency), 200, i =>
                Set(nameof(AppSettings.HomeCurrency), currencies[i].Code, $"Home currency · {currencies[i].Code}")));

        var languages = Languages.ToList();
        if (languages.All(l => l.Code != _s.TranslateTarget)) languages.Insert(0, (_s.TranslateTarget, Shammy.Core.Conversions.Translation.LanguageName(_s.TranslateTarget)));
        Row("Translate English to", "Selected English text translates into this.", Dropdown(languages.Select(l => l.Name).ToList(),
            languages.FindIndex(l => l.Code == _s.TranslateTarget), 200, i =>
                Set(nameof(AppSettings.TranslateTarget), languages[i].Code, $"Translates into {languages[i].Name}")));

        var pomo = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, name, step) in new[] { ("Focus", nameof(AppSettings.PomodoroWorkMinutes), 5), ("Short", nameof(AppSettings.PomodoroShortBreakMinutes), 1), ("Long", nameof(AppSettings.PomodoroLongBreakMinutes), 5) })
        {
            int value = (int)SettingsCatalog.Get(_s, SettingsCatalog.Find(name)!)!;
            var stepper = new Stepper(value, step == 1 ? 1 : 5, step == 1 ? 30 : 180, step, v =>
                Set(name, v, $"Pomodoro {_s.PomodoroWorkMinutes} / {_s.PomodoroShortBreakMinutes} / {_s.PomodoroLongBreakMinutes}"),
                less: $"Less {label.ToLowerInvariant()} time", more: $"More {label.ToLowerInvariant()} time");
            var caption = ScreenKit.Label(label, size: 10);
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            var col = ScreenKit.Column(3, stepper, caption);
            col.Margin = new Thickness(pomo.Children.Count > 0 ? 10 : 0, 0, 0, 0);
            pomo.Children.Add(col);
        }
        Row("Pomodoro", "Focus · short break · long break, in minutes.", pomo);

        int[] snoozes = { 5, 10, 15 };
        Row("Default snooze", "The “Usual” button on a ringing alarm.", new Segmented(snoozes.Select(m => $"{m} min").ToList(),
            Array.IndexOf(snoozes, _s.SnoozeMinutes), i => Set(nameof(AppSettings.SnoozeMinutes), snoozes[i], $"Snooze · {snoozes[i]} min")));
        Row("Reminder card stays", "How long a due reminder waits before it tucks away.",
            new Stepper(_s.ReminderCardSeconds, 5, 600, 5, v => Set(nameof(AppSettings.ReminderCardSeconds), v, $"Reminders stay {v} s"),
                v => $"{v} s", valueWidth: 44));
    }

    static string ClockHint(bool? use24) => "Standup shows as " + ClockPreference.Format(10, 30, use24);

    static readonly (string Code, string Name)[] Currencies =
    {
        ("INR", "Indian rupee"), ("USD", "US dollar"), ("EUR", "Euro"), ("GBP", "British pound"), ("AED", "UAE dirham"),
        ("SGD", "Singapore dollar"), ("AUD", "Australian dollar"), ("CAD", "Canadian dollar"), ("JPY", "Japanese yen"),
        ("CNY", "Chinese yuan"), ("SAR", "Saudi riyal"), ("BDT", "Bangladeshi taka"), ("NPR", "Nepalese rupee"),
        ("LKR", "Sri Lankan rupee"), ("CHF", "Swiss franc"),
    };

    static readonly (string Code, string Name)[] Languages =
    {
        ("hi", "Hindi"), ("bn", "Bengali"), ("or", "Odia"), ("ta", "Tamil"), ("te", "Telugu"), ("mr", "Marathi"),
        ("gu", "Gujarati"), ("kn", "Kannada"), ("ml", "Malayalam"), ("pa", "Punjabi"), ("ur", "Urdu"), ("es", "Spanish"),
        ("fr", "French"), ("de", "German"), ("ja", "Japanese"), ("zh-CN", "Chinese"), ("ar", "Arabic"),
    };

    // ---- Clipboard & privacy -----------------------------------------------------------------------

    void ClipboardAndPrivacy()
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
        SwitchRow("Copy preview", $"{PetLook.Name} shows a peek of what you copy.", nameof(AppSettings.CopyPreviewEnabled), "Copy preview on", "Copy preview off");
        SwitchRow("Always hide message text", "Notifications show who, never what. While you share your screen it’s hidden anyway.",
            nameof(AppSettings.AlwaysHideNotificationText), "Message text always hidden", "Message text shows unless you’re sharing");

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

    // ---- Menus -----------------------------------------------------------------------------------

    /// <summary>A menu button (microphone, currency, language) whose list floats over the panel.</summary>
    FrameworkElement Dropdown(IReadOnlyList<string> options, int selected, double width, Action<int> chosen)
    {
        var label = ScreenKit.Text(selected >= 0 && selected < options.Count ? options[selected] : "—", 12, Weight.SemiBold);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(label);
        var chevron = ScreenKit.Icon(ScreenKit.Paths.Chevron, ScreenKit.Fg2, 12, 2.4);
        chevron.Margin = new Thickness(8, 0, 0, 0);
        SetColumn(chevron, 1);
        grid.Children.Add(chevron);
        var button = new Pressable(ScreenKit.Ground, ScreenKit.Ground, ScreenKit.Hairline, ScreenKit.HairlineStrong)
        {
            Width = width,
            Height = 30,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 0, 10, 0),
            Child = grid,
            PressScale = 0.99,
        };
        button.Click += () =>
        {
            if (_menuOwner == button)
            {
                CloseMenu();
                return;
            }
            OpenMenu(button, options, selected, i =>
            {
                selected = i;
                label.Text = options[i];
                chosen(i);
            });
        };
        return button;
    }

    FrameworkElement? _menuOwner;

    void OpenMenu(FrameworkElement owner, IReadOnlyList<string> options, int selected, Action<int> chosen)
    {
        CloseMenu();
        _menuOwner = owner;
        var list = new StackPanel();
        for (int i = 0; i < options.Count; i++)
        {
            int at = i;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(ScreenKit.Text(options[i], 12, Weight.Medium));
            if (i == selected)
            {
                var tick = ScreenKit.Icon(ScreenKit.Paths.Check, ScreenKit.Fg, 12, 2.6);
                SetColumn(tick, 1);
                row.Children.Add(tick);
            }
            var opt = new Pressable(Brushes.Transparent, ScreenKit.Hairline) { Height = 30, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 0, 10, 0), Child = row, PressScale = 1 };
            opt.Click += () =>
            {
                CloseMenu();
                chosen(at);
            };
            list.Children.Add(opt);
        }
        var menu = new Border
        {
            Width = owner.ActualWidth,
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(10),
            Background = ScreenKit.TileHover,
            BorderBrush = ScreenKit.HairlineHover,
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 14, Direction = 270, Opacity = 0.5, Color = Colors.Black },
        };
        // Below the button when it fits, otherwise above it; long lists scroll.
        var pos = owner.TranslatePoint(new Point(0, 0), this);
        double below = ActualHeight - (pos.Y + owner.ActualHeight + 4), above = pos.Y - 4;
        double want = Math.Min(options.Count * 30 + 10, 200);
        bool down = below >= Math.Min(want, 120) || below >= above;
        menu.MaxHeight = Math.Max(60, Math.Min(want, down ? below : above));
        Canvas.SetLeft(menu, pos.X);
        if (down) Canvas.SetTop(menu, pos.Y + owner.ActualHeight + 4);
        else
        {
            menu.Measure(new Size(owner.ActualWidth, menu.MaxHeight));
            Canvas.SetTop(menu, pos.Y - 4 - menu.DesiredSize.Height);
        }
        _overlay.Children.Add(menu);
        _overlay.Background = Brushes.Transparent;
        ScreenKit.Rise(menu, 0, down ? -4 : 4);
    }

    void CloseMenu()
    {
        if (_menuOwner is null) return;
        _menuOwner = null;
        _overlay.Children.Clear();
        _overlay.Background = null;
    }
}
