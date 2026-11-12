using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Launcher;
using Shammy.Core.Palette;
using Shammy.Core.Search;
using Shammy.Interop;
using Shammy.Modules.Launcher;

namespace Shammy.Modules.Palette;

internal partial class PaletteModule
{
    // ---- Empty palette: recent picks and examples -------------------------------------------------

    int _tryIndex;

    /// <summary>
    /// Before anything is typed: the three strongest recent picks (apps, files, views, snippets, or the text of a
    /// command picked before), then a "Try:" row. Copied text and notes are never suggested.
    /// </summary>
    List<PaletteItem> Suggestions()
    {
        var items = new List<PaletteItem>();
        var now = DateTime.Now;
        foreach (var e in _frecency.Top(3, now, k => !k.StartsWith("clip:", StringComparison.Ordinal) && !k.StartsWith("note:", StringComparison.Ordinal)))
        {
            try
            {
                if (RecentItem(e) is { } item) items.Add(item with { Hint = "Recent" });
            }
            catch (Exception ex)
            {
                _ctx.Warn("A recent palette row failed", ex);
            }
        }
        items.Add(new PaletteItem("try", PaletteKind.Command, PaletteSources.TryRow, "Enter fills in an example", 0)
        {
            Hint = "Try",
            Tag = new PaletteEntry
            {
                Glyph = "\uE82F",
                Learn = false,
                Run = _ => _bar.SetText(PaletteSources.Examples[_tryIndex++ % PaletteSources.Examples.Count]),
            },
        });
        return items;
    }

    PaletteItem? RecentItem(FrecencyStore.Entry e)
    {
        var key = e.Key;
        var query = e.Queries.FirstOrDefault() ?? "";
        if (key.StartsWith("app:", StringComparison.Ordinal))
            return AppIndex.Current?.Apps.FirstOrDefault(a => string.Equals("app:" + a.Target, key, StringComparison.Ordinal)) is { } app
                ? AppItem(app, 100) with { Confident = false } : null;
        if (key.StartsWith("file:", StringComparison.Ordinal))
        {
            var path = key[5..];
            bool folder = Directory.Exists(path);
            return folder || File.Exists(path) ? FileItem(new FileHit(path, folder), 100) : null;
        }
        if (key.StartsWith("view:", StringComparison.Ordinal))
            return PaletteSources.Views.FirstOrDefault(v => "view:" + v.Id == key) is { } view && _ctx.Notch.ExpandedView.CanOpen(view.Id)
                ? ViewItem(view, 100) with { Confident = false } : null;
        if (key.StartsWith("snip:", StringComparison.Ordinal))
        {
            var trigger = key[5..];
            return Launcher.SnippetsModule.Current.TryGetValue(trigger, out var template)
                ? new PaletteItem(key, PaletteKind.Snippet, $"{trigger}  {PaletteSources.Line(template, 50)}", "Snippet \u00B7 Enter copies it", 100)
                {
                    Tag = new PaletteEntry { Glyph = "\uE8C8", Run = _ => CopySnippet(trigger, template) },
                }
                : null;
        }
        // Commands, settings, music: the same row the text it was picked for gives now (it previews afresh).
        if (query.Length == 0) return null;
        return LocalItems(query).FirstOrDefault(i => i.Key == key);
    }

    PaletteItem MediaItem(MediaControl control, int match)
    {
        var media = _ctx.Services.Media;
        var (glyph, title, phrase) = control switch
        {
            MediaControl.Next => ("\uE893", "Next track", "next song"),
            MediaControl.Previous => ("\uE892", "Previous track", "previous song"),
            _ when media.IsPlaying => ("\uE769", "Pause", "pause"),
            _ => ("\uE768", "Play", "resume"),
        };
        var playing = media.Artist.Length > 0 ? $"{media.Title} \u00B7 {media.Artist}" : media.Title;
        return new PaletteItem("media:" + control, PaletteKind.Media, title, playing, match)
        {
            Hint = "Media",
            Confident = match == 100,
            // Through the music command, so the result card is the same as typing "next song".
            Tag = new PaletteEntry { Glyph = glyph, Run = _ => _bar.Submit(phrase) },
        };
    }

    void AddSettings(List<PaletteItem> items, string text)
    {
        // Shammy's own settings first: on an equal match they rank above the Windows pages.
        try { AddShammySettings(items, text); }
        catch (Exception ex) { _ctx.Warn("Shammy setting rows failed", ex); }
        foreach (var (entry, match) in SettingsCatalog.Find(text))
        {
            var key = "setting:" + entry.Title;
            void OpenPage(bool _)
            {
                _ctx.Open(entry.Uri);
                _bar.Complete(new CommandResult("\uE713", "Opening settings", entry.Title));
            }
            bool exact = match >= 99;
            switch (entry.Control)
            {
                case SettingControl.Brightness:
                    if (BrightnessControl.Get() is not { } level) goto default;
                    items.Add(new PaletteItem(key, PaletteKind.Setting, $"Brightness \u00B7 {level}%", "", match)
                    {
                        Hint = "Settings",
                        Confident = exact,
                        Tag = BrightnessEntry(key, level, OpenPage),
                    });
                    break;
                case SettingControl.Volume:
                    if (_audio.Volume() is not { } volume) goto default;
                    items.Add(new PaletteItem(key, PaletteKind.Setting, VolumeTitle(volume.Level, volume.Muted), "", match)
                    {
                        Hint = "Sound",
                        Confident = exact,
                        Tag = VolumeEntry(key, volume.Level, volume.Muted),
                    });
                    // The output picker: one row per device, current one first.
                    foreach (var device in _audio.List().Take(3))
                    {
                        items.Add(new PaletteItem("audio:" + device.Id, PaletteKind.Setting, device.Name,
                            device.IsDefault ? "Current output" : "Switch output to this", Math.Max(0, match - 5))
                        {
                            Hint = device.IsDefault ? "\u2713 Output" : "Output",
                            Tag = new PaletteEntry
                            {
                                Glyph = device.IsDefault ? "\uE995" : "\uE7F5",
                                Learn = false,
                                Run = _ => SwitchOutput(device),
                            },
                        });
                    }
                    break;
                case SettingControl.Bluetooth:
                    _bluetooth = BluetoothRadio.Cached ?? _bluetooth;
                    items.Add(new PaletteItem(key, PaletteKind.Setting, BluetoothTitle(), "", match)
                    {
                        Hint = "Settings",
                        Confident = exact,
                        Tag = BluetoothEntry(key),
                    });
                    break;
                default:
                    items.Add(new PaletteItem(key, PaletteKind.Setting, entry.Title, "Windows Settings", match)
                    {
                        Hint = "Settings",
                        Confident = exact,
                        Tag = new PaletteEntry { Glyph = "\uE713", Run = OpenPage },
                    });
                    break;
            }
        }
    }

    // ---- Shammy's own settings ---------------------------------------------------------------------

    readonly Dictionary<string, global::Shammy.Modules.Settings.SettingsSearchEntry> _shammySettings = new();

    /// <summary>
    /// "Shammy setting" rows: switches flip in place (Enter, a click on the switch, ← →), anything else opens Shammy
    /// Settings on its tab. Added before the Windows pages, so an equal match puts Shammy's own first.
    /// </summary>
    void AddShammySettings(List<PaletteItem> items, string text)
    {
        var found = global::Shammy.Modules.Settings.SettingsCatalog.Searchable(_ctx.Settings, Infrastructure.TrayIcon.Toggles)
            .Select(e => (Entry: e, Match: ShammySettingSearch.Score(text, e.Label, e.Hint, e.Section)))
            .Where(r => r.Match > 0)
            .OrderByDescending(r => r.Match)
            .Take(3)
            .ToList();
        foreach (var (entry, match) in found)
        {
            var key = "shammy:" + entry.Key;
            _shammySettings[key] = entry;
            var subtitle = "Shammy setting · " + entry.Section;
            items.Add(entry.IsSwitch
                ? new PaletteItem(key, PaletteKind.Setting, ShammyTitle(entry), subtitle, match)
                {
                    Hint = "Shammy",
                    Confident = match >= 99,
                    Tag = ShammySwitchEntry(key, entry),
                }
                : new PaletteItem(key, PaletteKind.Setting, entry.Label, subtitle, match)
                {
                    Hint = "Shammy",
                    Confident = match >= 99,
                    Tag = new PaletteEntry
                    {
                        Glyph = "",
                        Run = _ =>
                        {
                            _bar.Complete(null);
                            global::Shammy.Modules.Settings.SettingsModule.OpenAt?.Invoke(entry.Section);
                        },
                    },
                });
        }
    }

    static string ShammyTitle(global::Shammy.Modules.Settings.SettingsSearchEntry entry)
    {
        bool on;
        try { on = entry.Get!(); }
        catch (Exception) { return entry.Label; }
        return $"{entry.Label} · {(on ? "on" : "off")}";
    }

    PaletteEntry ShammySwitchEntry(string key, global::Shammy.Modules.Settings.SettingsSearchEntry entry)
    {
        var toggle = (MiniSwitch)Control(key, () =>
        {
            var sw = new MiniSwitch();
            sw.Toggled += on => FlipShammy(key, on);
            return sw;
        });
        try { toggle.State = entry.Get!(); }
        catch (Exception) { toggle.State = null; }
        return new PaletteEntry
        {
            Glyph = "",
            Control = toggle,
            Learn = false,
            // Enter flips it in place; the palette stays open.
            Run = _ =>
            {
                if (_shammySettings.TryGetValue(key, out var e)) FlipShammy(key, !e.Get!());
            },
            EnterHint = () => "Enter on/off",
            Adjust = d => FlipShammy(key, d > 0),
        };
    }

    void FlipShammy(string key, bool on)
    {
        if (!_shammySettings.TryGetValue(key, out var entry) || !entry.IsSwitch) return;
        try
        {
            if (entry.Get!() != on) entry.Set!(on);
        }
        catch (Exception ex)
        {
            _ctx.Warn($"Switching {entry.Label} from the palette failed", ex);
        }
        if (_controls.GetValueOrDefault(key) is MiniSwitch sw)
        {
            try { sw.State = entry.Get!(); }
            catch (Exception) { sw.State = null; }
        }
        Retitle(key, ShammyTitle(entry));
    }

    static string VolumeTitle(float level, bool muted) => muted ? "Volume \u00B7 muted" : $"Volume \u00B7 {Math.Round(level * 100)}%";

    string BluetoothTitle() => _bluetooth switch
    {
        true => "Bluetooth \u00B7 on",
        false => "Bluetooth \u00B7 off",
        _ => "Bluetooth",
    };

    PaletteEntry BrightnessEntry(string key, int level, Action<bool> openPage)
    {
        var slider = (MiniSlider)Control(key, () => new MiniSlider());
        slider.Value = level / 100.0;
        void Apply(double v)
        {
            int percent = (int)Math.Round(v * 100);
            if (!BrightnessControl.Set(percent)) return;
            Retitle(key, $"Brightness \u00B7 {percent}%");
        }
        // One handler per control instance: the slider is reused across keystrokes.
        slider.ValueChanged -= _brightnessHandler;
        _brightnessHandler = Apply;
        slider.ValueChanged += _brightnessHandler;
        // No toggle makes sense for brightness, so Enter opens the Display page.
        return new PaletteEntry
        {
            Glyph = "\uE706",
            Control = slider,
            Run = openPage,
            Adjust = d => slider.Nudge(d * 0.05),
            EnterHint = () => "\u2190 \u2192 \u00B7 Enter settings",
        };
    }

    Action<double>? _brightnessHandler, _volumeHandler;

    PaletteEntry VolumeEntry(string key, float level, bool muted)
    {
        var host = (Grid)Control(key, () =>
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var s = new MiniSlider { VerticalAlignment = VerticalAlignment.Center };
            var mute = new Button
            {
                Style = Ui.Tokens.StyleRes("WallIconButton"),
                Width = 24,
                Height = 24,
                FontSize = 12,
                Margin = new Thickness(8, -4, 0, -4),
                ToolTip = "Mute",
            };
            mute.Click += (_, _) =>
            {
                _audio.ToggleMute();
                RefreshVolume(key);
            };
            Grid.SetColumn(mute, 1);
            grid.Children.Add(s);
            grid.Children.Add(mute);
            return grid;
        });
        var slider = (MiniSlider)host.Children[0];
        ((Button)host.Children[1]).Content = muted ? "\uE74F" : "\uE767";
        slider.Value = level;
        slider.ValueChanged -= _volumeHandler;
        _volumeHandler = v =>
        {
            _audio.SetVolume((float)v);
            RefreshVolume(key);
        };
        slider.ValueChanged += _volumeHandler;
        // Enter mutes or unmutes in place; the palette stays open.
        return new PaletteEntry
        {
            Glyph = "\uE767",
            Control = host,
            Learn = false,
            Run = _ =>
            {
                _audio.ToggleMute();
                RefreshVolume(key);
            },
            Adjust = d => slider.Nudge(d * 0.05),
            EnterHint = () => _audio.Volume() is { Muted: true } ? "\u2190 \u2192 \u00B7 Enter unmute" : "\u2190 \u2192 \u00B7 Enter mute",
        };
    }

    void RefreshVolume(string key)
    {
        if (_audio.Volume() is not { } v || _controls.GetValueOrDefault(key) is not Grid host) return;
        ((MiniSlider)host.Children[0]).Value = v.Level;
        ((Button)host.Children[1]).Content = v.Muted ? "\uE74F" : "\uE767";
        Retitle(key, VolumeTitle(v.Level, v.Muted));
    }

    PaletteEntry BluetoothEntry(string key)
    {
        var toggle = (MiniSwitch)Control(key, () =>
        {
            var sw = new MiniSwitch();
            sw.Toggled += on => _ = SetBluetoothAsync(key, on);
            return sw;
        });
        toggle.State = _bluetooth;
        // Only until the radio has been found once (off the UI thread); after that its state is a cached read.
        if (_bluetooth is null) _ = RefreshBluetoothAsync(key);
        return new PaletteEntry
        {
            Glyph = "\uE702",
            Control = toggle,
            Learn = false,
            // Enter switches the radio in place; the palette stays open.
            Run = ctrl =>
            {
                if (_bluetooth is { } on) _ = SetBluetoothAsync(key, !on);
            },
            EnterHint = () => "Enter on/off",
            Adjust = d =>
            {
                if (_bluetooth is not null) _ = SetBluetoothAsync(key, d > 0);
            },
        };
    }

    async Task RefreshBluetoothAsync(string key)
    {
        var state = await BluetoothRadio.GetAsync();
        _bluetooth = state;
        if (_controls.GetValueOrDefault(key) is MiniSwitch sw) sw.State = state;
        Retitle(key, BluetoothTitle());
    }

    async Task SetBluetoothAsync(string key, bool on)
    {
        if (_bluetooth == on) return;
        if (!await BluetoothRadio.SetAsync(on)) _ctx.ShowMessage("\uE702", "Couldn't switch Bluetooth", error: true);
        await RefreshBluetoothAsync(key);
    }

    void SwitchOutput(AudioOutput device)
    {
        if (!device.IsDefault && !_audio.SetDefault(device.Id))
            _ctx.ShowMessage("\uE7F5", $"Couldn't switch to {device.Name}", error: true);
        // Stay open and redraw, so the tick moves and the volume row shows the new device's level.
        Refresh();
    }

    FrameworkElement Control(string key, Func<FrameworkElement> create)
    {
        if (!_controls.TryGetValue(key, out var el)) _controls[key] = el = create();
        return el;
    }

    /// <summary>Re-reads every source for the current text, keeping the selection on the same row.</summary>
    void Refresh()
    {
        if (_query.Trim().Length == 0)
        {
            OnQuery(_query);
            return;
        }
        var selectedKey = _ranked.SelectedItem?.Key;
        _local = LocalItems(_query);
        _ranked = Rank(_local, _query);
        if (selectedKey is not null && _ranked.Items.ToList().FindIndex(i => i.Key == selectedKey) is >= 0 and var idx)
            _ranked = _ranked with { Selected = idx };
        Render();
    }

    /// <summary>A control changed something in place: update that row's title without rebuilding the list.</summary>
    void Retitle(string key, string title)
    {
        int i = _ranked.Items.ToList().FindIndex(x => x.Key == key);
        if (i < 0) return;
        var items = _ranked.Items.ToList();
        items[i] = items[i] with { Title = title };
        _ranked = _ranked with { Items = items };
        int j = _local.FindIndex(x => x.Key == key);
        if (j >= 0) _local[j] = _local[j] with { Title = title };
        _view.SetTitle(i, title);
        // Hints can depend on the state too ("Enter mute" ↔ "Enter unmute").
        _view.Select(_ranked.Selected);
    }
}
