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
    // ---- Notch & pet -------------------------------------------------------------------------------

    void NotchAndPet()
    {
        var speeds = new[] { HoverSpeed.Quick, HoverSpeed.Normal, HoverSpeed.Relaxed };
        Row("Hover speed", "How long you rest the pointer before the notch opens.",
            new Segmented(new[] { "Quick", "Normal", "Relaxed" }, Array.IndexOf(speeds, _s.HoverSpeed), i =>
                Set(nameof(AppSettings.HoverSpeed), speeds[i], $"Hover speed · {speeds[i]}")));
        SwitchRow("Peek", "A quick glance when you rest the pointer, before it opens.", nameof(AppSettings.PeekEnabled), "Peek on", "Peek off");
        SwitchRow("Morning brief", "The first unlock of a morning: weather, meetings, reminders and battery in one line, said aloud when a Gnani key is saved. Say “good morning” for it any time.",
            nameof(AppSettings.MorningBrief), "Morning brief on", "Morning brief off");
        SwitchRow("Right now line", "One line at the top of the open notch with what matters this minute: a meeting soon, Claude waiting, a timer, new messages.",
            nameof(AppSettings.NowLineEnabled), "Right now line on", "Right now line off");
        SwitchRow("Tuck-away tab", "A small tab under the notch. Click it and the notch slides away for a few seconds, so you can click what is under it.",
            nameof(AppSettings.TuckAwayEnabled), "Tuck-away tab on", "Tuck-away tab off");

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

        var kinds = new[] { PetKind.Eyes, PetKind.Cat };
        Border purrRow = null!;
        var kindRow = Row("Pet style", "Eyes is Shammy as it always was. Cat adds ears, whiskers and a nose to the same eyes.",
            new Segmented(new[] { "Eyes", "Cat" }, Array.IndexOf(kinds, _s.PetKind), i =>
            {
                Set(nameof(AppSettings.PetKind), kinds[i], kinds[i] == PetKind.Cat ? $"{PetLook.Name} is a cat" : "Just the eyes");
                face.Restyle();
                Dim(purrRow, kinds[i] == PetKind.Cat && _s.PetEnabled);
            }));
        dependents.Add(kindRow);
        purrRow = SwitchRow("Purr", "A soft purr when your pointer rests on the cat. Follows Notch sounds.", nameof(AppSettings.PetPurrs), "Purr on", "Purr off");
        dependents.Add(purrRow);
        SwitchRow("Cat theme", "Cat touches around the notch: the cat listens while you talk, and paws lead the “Did you mean” choices.",
            nameof(AppSettings.CatTheme), "Cat theme on", "Cat theme off");

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
}
