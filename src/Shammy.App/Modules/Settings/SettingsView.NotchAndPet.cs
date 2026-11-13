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
    // ---- Notch ------------------------------------------------------------------------------------

    /// <summary>How the notch opens and moves, how it looks, and the apps it stays away from.</summary>
    void NotchPage()
    {
        var speeds = new[] { HoverSpeed.Quick, HoverSpeed.Normal, HoverSpeed.Relaxed };
        Row("Hover speed", "How long you rest the pointer before the notch opens.",
            new Segmented(new[] { "Quick", "Normal", "Relaxed" }, Array.IndexOf(speeds, _s.HoverSpeed), i =>
                Set(nameof(AppSettings.HoverSpeed), speeds[i], $"Hover speed · {speeds[i]}")), words: "open delay pointer");
        SwitchRow("Peek", "A quick glance when you rest the pointer, before it opens.", nameof(AppSettings.PeekEnabled), "Peek on", "Peek off");
        SwitchRow("Gestures", "Scroll over the notch for volume, swipe sideways to skip a track.", nameof(AppSettings.GesturesEnabled), "Gestures on", "Gestures off",
            words: "swipe scroll wheel volume");
        SwitchRow("Tuck-away tab", "A small tab under the notch. Click it and the notch slides away for a few seconds, so you can click what is under it.",
            nameof(AppSettings.TuckAwayEnabled), "Tuck-away tab on", "Tuck-away tab off", words: "hide");
        SwitchRow("Right now line", "One line at the top of the open notch with what matters this minute: a meeting soon, Claude waiting, a timer, new messages.",
            nameof(AppSettings.NowLineEnabled), "Right now line on", "Right now line off");

        LookAndPlace();
        HiddenApps();
    }

    // ---- Pet -------------------------------------------------------------------------------------

    /// <summary>The pet: a little notch preview with its switch, then its style, purr, cat theme, eyes and name (dimmed while it's off).</summary>
    void PetPage()
    {
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
        dependents.Add(SwitchRow("Cat theme", "Cat touches around the notch: the cat listens while you talk, and paws lead the “Did you mean” choices.",
            nameof(AppSettings.CatTheme), "Cat theme on", "Cat theme off", words: "paws"));

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
    }

    /// <summary>Size, motion, the palette and the lyric line.</summary>
    void LookAndPlace()
    {
        Section("Look", "");
        var sizes = new[] { NotchSize.Small, NotchSize.Normal, NotchSize.Large };
        Row("Notch size", "Makes everything in the notch larger or smaller.",
            new Segmented(new[] { "Small", "Normal", "Large" }, Array.IndexOf(sizes, _s.NotchSize), i =>
                Set(nameof(AppSettings.NotchSize), sizes[i], $"Notch size · {sizes[i]}")), words: "scale bigger smaller");

        var motions = new[] { MotionStyle.Lively, MotionStyle.Calm };
        Row("Motion", "Calm stops the bounce, the spinning record and the pet’s little extras.",
            new Segmented(new[] { "Lively", "Calm" }, Array.IndexOf(motions, _s.Motion), i =>
                Set(nameof(AppSettings.Motion), motions[i], $"Motion · {motions[i]}")), words: "animation calm lively bounce");

        PalettePicker();

        SwitchRow("Lyric line", "The line being sung shows under the record, for songs with synced lyrics.", nameof(AppSettings.LyricLineEnabled), "Lyric line on", "Lyric line off", words: "lyrics song music");
    }

    /// <summary>
    /// The island's palette: one small swatch per palette (its three colours side by side). Its first colour is the
    /// accent too, so this replaces the old accent swatches.
    /// </summary>
    void PalettePicker()
    {
        TextBlock hint = null!;
        string current = IslandPalettes.Normalize(_s.Palette);
        static string Hint(IslandPalette p) => p.Id == IslandPalettes.Album
            ? "Album · the colours of the song playing (Monsoon’s when nothing plays)"
            : $"{p.Name} · the island’s waves, buttons and highlights";
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var palette in IslandPalettes.All)
        {
            var stripes = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 3 };
            foreach (var c in palette.Colours) stripes.Children.Add(new Border { Background = Ui.Tokens.Frozen(Theme.PaletteTheme.ToColor(c)) });
            var chip = new Border
            {
                Width = 26,
                Height = 16,
                Child = stripes,
                Clip = new RectangleGeometry(new Rect(0, 0, 26, 16), 8, 8),
            };
            UIElement face = chip;
            if (palette.Id == IslandPalettes.Album)
            {
                // Album shows its stand-in colours under a small note, so it reads as "from the music".
                var note = ScreenKit.Text("♪", 11, Weight.Bold);
                note.HorizontalAlignment = HorizontalAlignment.Center;
                note.VerticalAlignment = VerticalAlignment.Center;
                note.Foreground = Brushes.White;
                face = new Grid { Children = { chip, note } };
            }
            var ring = new Border
            {
                Width = 34,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(2),
                BorderBrush = palette.Id == current ? ScreenKit.Fg : Brushes.Transparent,
                Child = face,
            };
            var swatch = new Pressable(Brushes.Transparent, Brushes.Transparent)
            {
                Width = 34,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Child = ring,
                ToolTip = Hint(palette),
                Margin = new Thickness(swatches.Children.Count > 0 ? 4 : 0, 0, 0, 0),
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, $"Palette {palette.Name}");
            swatch.Click += () =>
            {
                foreach (Pressable p in swatches.Children) ((Border)p.Child).BorderBrush = Brushes.Transparent;
                ring.BorderBrush = ScreenKit.Fg;
                hint.Text = Hint(palette);
                Set(nameof(AppSettings.Palette), palette.Id, $"Palette · {palette.Name}");
                Theme.PaletteTheme.Choose(palette.Id);
            };
            swatches.Children.Add(swatch);
        }
        Row("Palette", Hint(IslandPalettes.Find(current)), swatches, out hint, words: "color colour theme accent palette album waves");
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

        var slot = new Border();
        TextBlock addHint = null!;
        Row("Add an app", "Looking at the apps that are open…", slot, out addHint);
        void Fill(IReadOnlyList<string> apps)
        {
            var running = apps.Where(a => !_s.HiddenApps.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();
            if (running.Count == 0)
            {
                addHint.Text = "Open the app first, then come back here to pick it.";
                slot.Child = null;
                return;
            }
            addHint.Text = "Pick from the apps that are open now.";
            var labels = new List<string> { "Choose an app" };
            labels.AddRange(running);
            slot.Child = Dropdown(labels, 0, 170, i =>
            {
                if (i == 0) return;
                var app = running[i - 1];
                Set(nameof(AppSettings.HiddenApps), _s.HiddenApps.Append(app).ToList(), $"The notch hides in {app}");
                BuildPanel(keepScroll: true);
            });
        }
        if (OpenApps.Fresh is { } known) Fill(known);
        else
        {
            _ = OpenApps.ListAsync().ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) Dispatcher.BeginInvoke(() => Fill(t.Result));
            }, TaskScheduler.Default);
        }
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
