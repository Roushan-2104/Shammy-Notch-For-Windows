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

/// <summary>How the app runs a palette row; kept in <see cref="PaletteItem.Tag"/>.</summary>
internal sealed class PaletteEntry
{
    public string Glyph { get; init; } = "\uE721";

    /// <summary>A file or shell path to draw the icon from (apps and files); null keeps the glyph.</summary>
    public string? IconPath { get; init; }

    /// <summary>Enter or a click; the flag is Ctrl.</summary>
    public Action<bool>? Run { get; init; }

    /// <summary>Left/Right on the row (sliders, switches).</summary>
    public Action<int>? Adjust { get; init; }

    public bool Adjustable => Adjust is not null;

    /// <summary>The chosen row's hint when Enter does something other than "run it" ("Enter mute").</summary>
    public Func<string>? EnterHint { get; init; }

    public FrameworkElement? Control { get; init; }

    /// <summary>Whether picking it should teach the ranker (web and in-place controls don't).</summary>
    public bool Learn { get; init; } = true;
}

/// <summary>
/// The typing bar's live palette: as you type, ranked rows for commands (with a preview of what they'll
/// do), settings (brightness, volume and Bluetooth right in the row), apps, files from the Windows Search
/// index, music and media keys, and "Search Google" last. Up/Down to choose, Enter to run, Ctrl+Enter
/// opens a file's folder, Esc closes. With nothing chosen, Enter does exactly what it always did.
/// </summary>
internal sealed class PaletteModule : IShammyModule
{
    const int FileDebounceMs = 90;

    /// <summary>What's saved: the frecency entries (DPAPI-encrypted by ModuleContext like other module data).</summary>
    public sealed class Saved
    {
        public List<FrecencyStore.Entry> Picks { get; set; } = new();
    }

    ModuleContext _ctx = null!;
    Views.SearchBarView _bar = null!;
    readonly PaletteView _view = new();
    readonly FileSearch _files = new();
    readonly AudioOutputs _audio = new();
    IconLoader _icons = null!;
    readonly Dictionary<string, FrameworkElement> _controls = new();
    readonly string _profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    FrecencyStore _frecency = new();
    DispatcherTimer _fileTimer = null!;

    string _query = "";
    int _generation;
    List<PaletteItem> _local = new();
    RankedPalette _ranked = RankedPalette.Empty;
    string? _pendingSql;
    bool? _bluetooth;
    readonly Queue<double> _localTimes = new();
    long _lastFileMs = -1;
    int _lastFileCount = -1;

    public string Name => "palette";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _bar = ctx.Notch.SearchBarView;
        _frecency = new FrecencyStore(ctx.Load<Saved>().Picks);
        _fileTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(FileDebounceMs), DispatcherPriority.Input, (_, _) => SubmitFileQuery(), ctx.Ui)
        {
            IsEnabled = false,
        };

        _bar.QueryChanged += OnQuery;
        HostLinks.HasChoices = HasChoices;
        HostLinks.Choices = ChoicesFor;
        _bar.PaletteKeys = OnKey;
        _icons = new IconLoader(ctx.Ui);
        _icons.Loaded += OnIconLoaded;
        // A fresh device list arrived in the background: redraw if the sound rows are showing.
        _audio.ListChanged += () =>
        {
            if (_local.Any(i => i.Key.StartsWith("audio:", StringComparison.Ordinal) || i.Key == "setting:Volume")) Refresh();
        };
        _view.RowInvoked += (i, ctrl) => Invoke(i, ctrl);
        _view.RowHovered += i =>
        {
            if (i < _ranked.Items.Count) SetSelected(i);
        };

        ctx.AddPipeCommand("palette-query", PipeQuery);
        ctx.AddPipeCommand("palette-type", text =>
        {
            _bar.SetText(text);
            return PipeState("");
        });
        ctx.AddPipeCommand("palette-key", PipeKey);
        ctx.AddPipeCommand("palette-state", PipeState);
        ctx.AddPipeCommand("palette-files", PipeFiles);
        ctx.AddPipeCommand("palette-audio", PipeAudio);
        ctx.AddPipeCommand("palette-brightness", arg =>
        {
            if (int.TryParse(arg.Trim(), out var n)) return BrightnessControl.Set(n) ? $"set {n}" : "error: couldn't set brightness";
            return BrightnessControl.Get() is { } b ? $"brightness={b}" : "brightness=unavailable";
        });
        ctx.AddPipeCommand("palette-bluetooth", arg =>
        {
            arg = arg.Trim();
            if (arg is "on" or "off") return Task.Run(() => BluetoothRadio.SetAsync(arg == "on")).GetAwaiter().GetResult() ? $"bluetooth {arg}" : "error: couldn't switch";
            var state = Task.Run(() => BluetoothRadio.GetAsync()).GetAwaiter().GetResult();
            return state is null ? "bluetooth=unavailable" : $"bluetooth={(state.Value ? "on" : "off")}";
        });
        ctx.AddPipeCommand("palette-frecency", _ => string.Join(" | ", _frecency.Entries
            .OrderByDescending(e => e.LastUsed).Take(10)
            .Select(e => $"{e.Key} score={e.Score:0.0} q=[{string.Join(",", e.Queries)}]")) is { Length: > 0 } s ? s : "empty");
    }

    // ---- Query → rows ---------------------------------------------------------------------------

    void OnQuery(string text)
    {
        _query = text;
        _generation++;
        _fileTimer.Stop();
        _files.CancelPending();
        _pendingSql = null;
        if (text.Trim().Length == 0)
        {
            // Nothing typed: recent picks and a "Try:" row, none chosen (Enter on an empty box does nothing).
            _local = Suggestions();
            _ranked = new RankedPalette(_local, -1);
            _view.ResetPointer();
            Render();
            // The box just opened (or was cleared): warm the slow sources now, so typing "sound" or
            // "bluetooth" only reads caches.
            _audio.RefreshIfStale();
            BluetoothRadio.Prefetch();
            return;
        }

        var sw = Stopwatch.StartNew();
        _local = LocalItems(text);
        _ranked = Rank(_local, text);
        Render();
        Remember(sw.Elapsed.TotalMilliseconds);

        if (WantsFiles(text, _local) is { } sql)
        {
            _pendingSql = sql;
            _fileTimer.Start();
        }
    }

    RankedPalette Rank(IEnumerable<PaletteItem> items, string text)
    {
        var now = DateTime.Now;
        return PaletteRanker.Rank(items, i => i.Kind == PaletteKind.Web ? 0 : _frecency.Boost(i.Key, text, now));
    }

    /// <summary>Everything that answers instantly. Each source fails soft: a broken module never empties the palette.</summary>
    List<PaletteItem> LocalItems(string text)
    {
        var items = new List<PaletteItem>();
        var clean = text.Trim().TrimEnd('.', '!', '?').Trim();

        // 1. The command Enter would run today (first one that recognises the text), with a preview; when another
        //    module also understands the text, its reading comes second (picking it runs that module's command).
        bool musicCommand = false;
        // Everyday wordings count too ("grab my screen", "screenshot le lo"): the first wording a command takes.
        foreach (var candidate in SpokenRewrite.Candidates(clean))
        {
            if (!_ctx.Commands.Any(c => Handles(c, candidate))) continue;
            clean = candidate;
            break;
        }
        var commands = _ctx.Commands.Where(c => Handles(c, clean)).Take(2).ToList();
        var command = commands.FirstOrDefault();
        for (int n = 0; n < commands.Count; n++)
        {
            var cmd = commands[n];
            CommandPreview? preview = null;
            try { preview = (cmd as ICommandPreview)?.Preview(clean); }
            catch (Exception ex) { _ctx.Warn($"Preview of {cmd.GetType().Name} failed", ex); }
            musicCommand |= cmd.GetType().Name.Contains("Music", StringComparison.Ordinal);
            bool first = n == 0;
            items.Add(new PaletteItem("cmd:" + cmd.GetType().Name, PaletteKind.Command,
                preview?.Title ?? clean, preview?.Detail ?? "Shammy command", first ? 100 : 90)
            {
                Hint = "Command",
                Confident = first,
                Tag = new PaletteEntry
                {
                    Glyph = preview?.Glyph ?? "\uE756",
                    // The first is exactly what Enter always did; the second runs its own module directly.
                    Run = first ? _ => _bar.Submit(text) : ctrl => { _ = RunCommandAsync(cmd, clean); },
                },
            });
        }
        if (command is null && Calculator.TryEvaluate(text, out var value, out _))
        {
            items.Add(new PaletteItem("calc", PaletteKind.Command, "= " + Calculator.Format(value), Calculator.DisplayForm(text), 100)
            {
                Hint = "Calculator",
                Confident = true,
                Tag = new PaletteEntry { Glyph = "\uE8EF", Run = _ => _bar.Submit(text) },
            });
        }

        // 2. Settings, with live controls for brightness, sound and Bluetooth.
        try { AddSettings(items, text); }
        catch (Exception ex) { _ctx.Warn("Settings rows failed", ex); }

        // 3. Apps from the launcher's index. When "open X" already is the launcher's command row, that row is the app.
        if (command is not LauncherModule)
        {
            try { AddApps(items, text); }
            catch (Exception ex) { _ctx.Warn("App rows failed", ex); }
        }

        // 3b. Shammy's own views, notes and reminders, snippets, copied text (ranked below apps, above files).
        try { AddRemembered(items, text); }
        catch (Exception ex) { _ctx.Warn("View, note and clipboard rows failed", ex); }

        // 4. Media keys and music.
        var media = _ctx.Services.Media;
        if (!musicCommand && media.HasSession)
        {
            foreach (var (control, match) in PaletteQuery.MediaControls(text))
                items.Add(MediaItem(control, match));
        }
        // Only with a play intent the music command doesn't cover ("kesariya chalao"); plain words aren't songs,
        // and a text another command already understood ("5 minute ka timer chala do") isn't one either.
        if (command is null && PaletteQuery.MusicQuery(text) is { } song)
        {
            items.Add(new PaletteItem("music:youtube", PaletteKind.Music, $"Play \u201C{song}\u201D", "YouTube Music", 60)
            {
                Hint = "Music",
                Tag = new PaletteEntry { Glyph = "\uEC4F", Run = _ => _bar.Submit($"play {song} on youtube music") },
            });
        }

        // 4b. Shammy features in everyday words ("grab my screen" → Screenshot), when no command took the text.
        if (command is null)
        {
            try { AddFeatures(items, text); }
            catch (Exception ex) { _ctx.Warn("Feature rows failed", ex); }
        }

        // 5. Always last: the web, exactly as Enter used to do it.
        items.Add(new PaletteItem("web", PaletteKind.Web, $"Search Google for \u201C{text.Trim()}\u201D", "Web", 0)
        {
            Hint = "Web",
            Tag = new PaletteEntry { Glyph = "\uE721", Run = _ => _bar.SearchWeb(text.Trim()), Learn = false },
        });
        return items;
    }

    static bool Handles(ITextCommand c, string text)
    {
        try { return c.CanHandle(text); }
        catch (Exception) { return false; }
    }

    /// <summary>The guide's features that match the words well (a word of the name, or two of its own words).</summary>
    void AddFeatures(List<PaletteItem> items, string text)
    {
        var words = Shammy.Core.Guide.FeatureFinder.Words(text);
        if (words.Count == 0) return;
        // Strong enough, and about the request as a whole: "history of the eiffel tower" isn't Clipboard history.
        foreach (var m in Shammy.Core.Guide.FeatureFinder.Find(text)
                     .Where(m => m.Score >= FeatureChoiceScore && words.Count(w => Shammy.Core.Guide.FeatureFinder.Score(m.Feature, new[] { w }) > 0) * 2 >= words.Count)
                     .Take(2))
        {
            var f = m.Feature;
            items.Add(new PaletteItem("feature:" + f.Id, PaletteKind.View, f.Name, "Shammy · " + f.Line, Math.Min(85, 50 + m.Score * 5))
            {
                Hint = "Shammy",
                Tag = new PaletteEntry
                {
                    Glyph = "",
                    Run = _ =>
                    {
                        _bar.Complete(null);
                        _ctx.Ui.BeginInvoke(() => Guide.GuideLinks.OpenFeature?.Invoke(f.Name));
                    },
                },
            });
        }
    }

    /// <summary>A feature counts as a choice from this score up (a name word is 3, one of its own words 2).</summary>
    const int FeatureChoiceScore = 3;

    /// <summary>The lowest match at which a setting, app or view counts as something the words could mean.</summary>
    const int ChoiceMatch = 60;

    /// <summary>
    /// <see cref="HostLinks.HasChoices"/>: whether anything in Shammy could be what <paramref name="text"/> means (a
    /// command, a feature, a setting, a view, an app, music). When so, voice opens the palette with the words, to
    /// pick from; when not, it is a plain question and goes straight to the web.
    /// </summary>
    bool HasChoices(string text) => ChoicesFor(text).Count > 0;

    static bool IsChoice(PaletteItem i) => i.Kind switch
    {
        PaletteKind.Command => true,
        PaletteKind.View when i.Key.StartsWith("feature:", StringComparison.Ordinal) => true,
        PaletteKind.Setting or PaletteKind.View or PaletteKind.App or PaletteKind.Music or PaletteKind.Media => i.Match >= ChoiceMatch,
        _ => false,
    };

    /// <summary><see cref="HostLinks.Choices"/>: the best three rows that count as choices, each running as a palette pick would.</summary>
    IReadOnlyList<Choice> ChoicesFor(string text)
    {
        if (text.Trim().Length == 0) return Array.Empty<Choice>();
        try
        {
            var ranked = Rank(LocalItems(text), text);
            return ranked.Items.Where(IsChoice).Take(3).Select(item =>
            {
                var entry = item.Tag as PaletteEntry;
                return new Choice(item.Title, item.Subtitle, entry?.Glyph ?? "", () =>
                {
                    if (entry?.Run is null) return;
                    if (entry.Learn)
                    {
                        _frecency.Record(item.Key, text, DateTime.Now);
                        _ctx.Save(new Saved { Picks = _frecency.Entries.ToList() });
                    }
                    _ctx.Info($"Choice pick: {item.Kind}");
                    try { entry.Run(false); }
                    catch (Exception ex)
                    {
                        _ctx.Warn("Running a choice failed", ex);
                        _bar.Complete(new CommandResult("", "That didn't work", ex.Message, IsError: true));
                    }
                });
            }).ToList();
        }
        catch (Exception ex)
        {
            _ctx.Warn("Working out choices failed", ex);
            return Array.Empty<Choice>();
        }
    }

    void AddApps(List<PaletteItem> items, string text)
    {
        var apps = AppIndex.Current?.Apps;
        if (apps is null || apps.Count == 0) return;
        var request = LaunchResolver.Parse(text);
        var query = request is { Kind: LaunchKind.App } ? request.Target : text.Trim();
        foreach (var m in AppMatcher.Rank(query, apps, 4))
        {
            if (m.Score < AppMatcher.LaunchScore) continue;
            items.Add(AppItem(m.App, m.Score));
        }
    }

    PaletteItem AppItem(AppEntry app, int score) =>
        new("app:" + app.Target, PaletteKind.App, app.Name, "Application", score)
        {
            Hint = "App",
            Confident = score >= PaletteRanker.ConfidentAppMatch,
            Tag = new PaletteEntry
            {
                Glyph = "\uECAA",
                IconPath = app.Target,
                Run = _ =>
                {
                    if (_ctx.Open(app.Target)) _bar.Complete(new CommandResult("\uECAA", "Opening", app.Name));
                    else
                    {
                        AppIndex.Current?.Refresh();
                        _bar.Complete(new CommandResult("\uE783", $"Couldn't open {app.Name}", "It may have been uninstalled", IsError: true));
                    }
                },
            },
        };

    // ---- Shammy's views, notes, snippets, clipboard ---------------------------------------------------

    /// <summary>
    /// Views ("calendar" opens the Calendar card), then what Shammy remembers: open notes and reminders, snippets,
    /// copied text. Notes and copied text never show while the screen is shared.
    /// </summary>
    void AddRemembered(List<PaletteItem> items, string text)
    {
        bool presenting = Infrastructure.PrivacyState.Presenting;
        foreach (var (view, match) in PaletteSources.FindViews(text, id => _ctx.Notch.ExpandedView.CanOpen(id)))
            items.Add(ViewItem(view, match));

        if (PaletteSources.Allowed(PaletteKind.Note, presenting) && Connect.Links.Notes is { } notes)
        {
            var reminders = notes.Reminders.Where(r => !r.Done).ToDictionary(r => "r:" + r.Id);
            var texts = notes.Reminders.Where(r => !r.Done).OrderBy(r => r.Due).Select(r => new PaletteText("r:" + r.Id, r.Title))
                .Concat(notes.Notes.Where(n => !n.Done).OrderByDescending(n => n.Created).Select(n => new PaletteText("n:" + n.Id, n.Text)))
                .ToList();
            var now = DateTime.Now;
            foreach (var (t, match) in PaletteSources.FindTexts(text, texts, 2))
            {
                var due = reminders.TryGetValue(t.Key, out var r) ? r.Due : (DateTime?)null;
                items.Add(new PaletteItem("note:" + t.Key, PaletteKind.Note, PaletteSources.Line(t.Text),
                    due is { } d ? $"Reminder \u00B7 {Shammy.Core.Productivity.TimeFormat.When(d, now)}" : "Note", match)
                {
                    Hint = due is null ? "Note" : "Reminder",
                    Tag = new PaletteEntry { Glyph = due is null ? "\uE70B" : "\uEA8F", Learn = false, Run = _ => OpenCard("notes") },
                });
            }
        }

        var snippets = Launcher.SnippetsModule.Current;
        if (snippets.Count > 0)
        {
            var texts = snippets.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new PaletteText(s.Key, s.Value, s.Key));
            foreach (var (t, match) in PaletteSources.FindTexts(text, texts, 2))
            {
                items.Add(new PaletteItem("snip:" + t.Trigger, PaletteKind.Snippet, $"{t.Trigger}  {PaletteSources.Line(t.Text, 50)}",
                    "Snippet \u00B7 Enter copies it", match)
                {
                    Hint = "Snippet",
                    Tag = new PaletteEntry { Glyph = "\uE8C8", Run = _ => CopySnippet(t.Trigger, t.Text) },
                });
            }
        }

        if (PaletteSources.Allowed(PaletteKind.Clip, presenting) && Connect.Links.Clipboard is { } clip)
        {
            var entries = clip.Items.Where(e => e.Kind == Shammy.Core.Clipboard.ClipKind.Text).OrderByDescending(e => e.Time).ToList();
            var byKey = entries.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.First());
            var now = DateTime.Now;
            foreach (var (t, match) in PaletteSources.FindTexts(text, entries.Select(e => new PaletteText(e.Key, e.Text)), 2))
            {
                if (!byKey.TryGetValue(t.Key, out var entry)) continue;
                items.Add(new PaletteItem("clip:" + t.Key, PaletteKind.Clip, PaletteSources.Line(t.Text),
                    $"Clipboard \u00B7 {Shammy.Core.Clipboard.ClipText.Relative(entry.Time, now)}", match)
                {
                    Hint = "Copy",
                    Tag = new PaletteEntry
                    {
                        Glyph = "\uE77F",
                        Learn = false,
                        Run = _ =>
                        {
                            clip.CopyBack(entry);
                            _bar.Complete(null);
                        },
                    },
                });
            }
        }
    }

    PaletteItem ViewItem(ShammyView view, int match) =>
        new("view:" + view.Id, PaletteKind.View, view.Title, "Open in the notch", match)
        {
            Hint = "Shammy",
            Confident = match >= 100,
            Tag = new PaletteEntry { Glyph = view.Glyph, Run = _ => OpenCard(view.Id) },
        };

    /// <summary>Closes the palette, then opens the card in the notch (once the typing bar has gone).</summary>
    void OpenCard(string id)
    {
        _bar.Complete(null);
        _ctx.Ui.BeginInvoke(() =>
        {
            if (!Connect.Links.OpenCard(_ctx, id)) _ctx.ShowMessage("\uE783", "That view isn't available", error: true);
        });
    }

    void CopySnippet(string trigger, string template)
    {
        string? ClipboardText()
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (System.Runtime.InteropServices.ExternalException) { return null; }
        }
        var expanded = SnippetText.Expand(template, DateTime.Now, ClipboardText);
        try
        {
            Clipboard.SetText(expanded);
            _bar.Complete(new CommandResult("\uE8C8", "Snippet copied", trigger));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _bar.Complete(new CommandResult("\uE783", "Clipboard is busy", "Try again in a moment", IsError: true));
        }
    }

    /// <summary>A second command's row: runs that module's command and shows its result like Enter would.</summary>
    async Task RunCommandAsync(ITextCommand cmd, string clean)
    {
        CommandResult result;
        try
        {
            result = await cmd.RunAsync(clean);
            HostLinks.RaiseCommandRan(clean);
        }
        catch (Exception ex)
        {
            _ctx.Warn($"Command {cmd.GetType().Name} failed", ex);
            result = new CommandResult("\uE783", "That didn't work", ex.Message, IsError: true);
        }
        if (result.SearchInstead is { Length: > 0 } search) _bar.SearchWeb(search);
        else _bar.Complete(result);
    }

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
                Style = (Style)Application.Current.Resources["WallIconButton"],
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

    // ---- Files (async) ----------------------------------------------------------------------------

    /// <summary>The index query for the text, or null when files aren't worth asking for.</summary>
    string? WantsFiles(string text, List<PaletteItem> local)
    {
        if (_files.Available == false) return null;
        bool search = FileResults.ShouldSearch(text,
            hasCommand: local.Any(i => i.Kind == PaletteKind.Command && i.Key != "calc"),
            isCalculation: local.Any(i => i.Key == "calc"),
            exactSetting: local.Any(i => i.Kind == PaletteKind.Setting && i.Match >= 99));
        return search ? WindowsSearchSql.Build(text, _profile, 30) : null;
    }

    void SubmitFileQuery()
    {
        _fileTimer.Stop();
        if (_pendingSql is not { } sql) return;
        _pendingSql = null;
        int generation = _generation;
        string query = _query;
        _files.Submit(new FileSearch.Request(generation, sql, (id, hits, ms) => _ctx.Ui.BeginInvoke(() =>
        {
            if (id != _generation || hits is null) return;
            _lastFileMs = ms;
            var ranked = FileResults.Rank(query, hits, _profile);
            _lastFileCount = ranked.Count;
            if (ranked.Count == 0) return;
            _local.AddRange(ranked.Select(r => FileItem(r.Hit, r.Match)));
            _ranked = PaletteRanker.Stabilize(_ranked, Rank(_local, query));
            Render();
        })));
    }

    PaletteItem FileItem(FileHit hit, int match)
    {
        var name = Path.GetFileName(hit.Path.TrimEnd('\\'));
        var folder = Path.GetDirectoryName(hit.Path) ?? "";
        return new PaletteItem("file:" + hit.Path, PaletteKind.File, name, DisplayFolder(folder), match)
        {
            Hint = hit.IsFolder ? "Folder" : "File",
            Tag = new PaletteEntry
            {
                Glyph = hit.IsFolder ? "\uE8B7" : "\uE8A5",
                IconPath = hit.Path,
                Run = ctrl => OpenFile(hit, ctrl),
            },
        };
    }

    void OpenFile(FileHit hit, bool showInFolder)
    {
        var name = Path.GetFileName(hit.Path.TrimEnd('\\'));
        bool exists = hit.IsFolder ? Directory.Exists(hit.Path) : File.Exists(hit.Path);
        if (!exists)
        {
            _bar.Complete(new CommandResult("\uE783", $"{name} is gone", "It was moved or deleted", IsError: true));
            return;
        }
        bool ok = showInFolder
            ? _ctx.Open(Native.ExplorerPath, $"/select,\"{hit.Path}\"")
            : _ctx.Open(hit.Path);
        _bar.Complete(ok
            ? new CommandResult(hit.IsFolder ? "\uE8B7" : "\uE8A5", showInFolder ? "Showing in folder" : "Opening", name)
            : new CommandResult("\uE783", $"Couldn't open {name}", null, IsError: true));
    }

    /// <summary>"Documents › Work" instead of "C:\Users\me\Documents\Work".</summary>
    string DisplayFolder(string folder)
    {
        var shown = folder;
        if (_profile.Length > 0 && folder.StartsWith(_profile, StringComparison.OrdinalIgnoreCase))
            shown = folder[_profile.Length..].TrimStart('\\');
        return shown.Length == 0 ? "Home" : shown.Replace("\\", " \u203A ");
    }

    // ---- Keys, selection, running ----------------------------------------------------------------

    bool OnKey(KeyEventArgs e)
    {
        if (_ranked.Items.Count == 0) return false;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Down:
                SetSelected(Math.Min(_ranked.Selected + 1, _ranked.Items.Count - 1));
                return true;
            case Key.Up:
                SetSelected(Math.Max(_ranked.Selected - 1, -1));
                return true;
            case Key.Enter:
                // Nothing chosen: let the box submit as it always has (commands, sums, web).
                if (_ranked.Selected < 0) return false;
                Invoke(_ranked.Selected, ctrl);
                return true;
            case Key.Left or Key.Right when _ranked.SelectedItem?.Tag is PaletteEntry { Adjust: { } adjust }:
                adjust(e.Key == Key.Right ? 1 : -1);
                return true;
            // Nothing typed and the recent picks are a rail of tiles: Left and Right walk along it.
            case Key.Left or Key.Right when _view.RailCount > 0 && _ranked.Selected < _view.RailCount && _query.Trim().Length == 0:
                SetSelected(e.Key == Key.Right ? Math.Min(_ranked.Selected + 1, _view.RailCount - 1) : Math.Max(_ranked.Selected - 1, 0));
                return true;
            default:
                return false;
        }
    }

    void SetSelected(int index)
    {
        if (index == _ranked.Selected) return;
        _ranked = _ranked with { Selected = index };
        _view.Select(index);
    }

    void Invoke(int index, bool ctrl)
    {
        if (index < 0 || index >= _ranked.Items.Count) return;
        var item = _ranked.Items[index];
        if (item.Tag is not PaletteEntry entry || entry.Run is null) return;
        if (entry.Learn)
        {
            _frecency.Record(item.Key, _query, DateTime.Now);
            _ctx.Save(new Saved { Picks = _frecency.Entries.ToList() });
        }
        _ctx.Info($"Palette pick: {item.Kind}");
        try { entry.Run(ctrl); }
        catch (Exception ex)
        {
            _ctx.Warn("Running a palette row failed", ex);
            _bar.Complete(new CommandResult("\uE783", "That didn't work", ex.Message, IsError: true));
        }
    }

    // ---- Drawing -----------------------------------------------------------------------------------

    void Render()
    {
        if (_ranked.Items.Count == 0)
        {
            _bar.SetPalette(null, 0);
            return;
        }
        var rows = _ranked.Items.Select(i =>
        {
            var entry = i.Tag as PaletteEntry;
            return new PaletteRowView(i, entry?.Glyph ?? "\uE721", entry?.IconPath is { } p ? _icons.Get(p, i.Kind == PaletteKind.File) : null, entry?.Control);
        }).ToList();
        double height = _view.Render(rows, _ranked.Selected);
        _bar.SetPalette(_view, height);
    }

    /// <summary>An icon arrived from the loader: put it into every row showing that path, without a rebuild.</summary>
    void OnIconLoaded(string path, ImageSource icon)
    {
        for (int i = 0; i < _ranked.Items.Count; i++)
            if (_ranked.Items[i].Tag is PaletteEntry { IconPath: { } p } && string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
                _view.SetIcon(i, icon);
    }

    void Remember(double ms)
    {
        _localTimes.Enqueue(ms);
        while (_localTimes.Count > 50) _localTimes.Dequeue();
    }

    // ---- Test pipe -------------------------------------------------------------------------------

    static string Describe(RankedPalette r) => r.Items.Count == 0
        ? "no rows"
        : string.Join(" | ", r.Items.Select((i, n) =>
            $"{(n == r.Selected ? "*" : "")}{n + 1}. [{i.Kind}] {i.Title}{(i.Subtitle.Length > 0 ? " \u2014 " + i.Subtitle : "")} ({i.Hint}, {i.Match})"));

    /// <summary>"palette-query spotify": what the palette would show (files included), without touching the UI.</summary>
    string PipeQuery(string text)
    {
        if (text.Trim().Length == 0) return "suggestions || " + Describe(new RankedPalette(Suggestions(), -1));
        var sw = Stopwatch.StartNew();
        var local = LocalItems(text);
        var ranked = Rank(local, text);
        double localMs = sw.Elapsed.TotalMilliseconds;
        string files = "files skipped";
        if (WantsFiles(text, local) is { } sql)
        {
            var done = new ManualResetEventSlim();
            IReadOnlyList<FileHit>? hits = null;
            long ms = -1;
            _files.Submit(new FileSearch.Request(-1, sql, (_, h, t) =>
            {
                hits = h;
                ms = t;
                done.Set();
            }));
            if (!done.Wait(TimeSpan.FromSeconds(3))) files = "files timed out";
            else if (hits is null) files = $"files unavailable ({_files.LastError})";
            else
            {
                var fileRows = FileResults.Rank(text, hits, _profile);
                files = $"files {ms} ms ({hits.Count} hits, {fileRows.Count} shown)";
                ranked = PaletteRanker.Stabilize(ranked, Rank(local.Concat(fileRows.Select(r => FileItem(r.Hit, r.Match))), text));
            }
        }
        return $"local {localMs.ToString("0.0", CultureInfo.InvariantCulture)} ms \u00B7 {files} \u00B7 selected {ranked.Selected + 1} || {Describe(ranked)}";
    }

    string PipeState(string _)
    {
        var times = _localTimes.Count == 0 ? "-" :
            $"avg {_localTimes.Average().ToString("0.0", CultureInfo.InvariantCulture)} ms, max {_localTimes.Max().ToString("0.0", CultureInfo.InvariantCulture)} ms over {_localTimes.Count}";
        return $"text=\u201C{_bar.Text}\u201D notch={_ctx.Notch.Mode} palette={_bar.PaletteHeight:0}px selected={_ranked.Selected + 1} " +
               $"local=[{times}] lastFiles={_lastFileMs} ms/{_lastFileCount} index={_files.Available?.ToString() ?? "unknown"} || {Describe(_ranked)}";
    }

    string PipeKey(string arg)
    {
        var a = arg.Trim().ToLowerInvariant();
        switch (a)
        {
            case "down": SetSelected(Math.Min(_ranked.Selected + 1, _ranked.Items.Count - 1)); break;
            case "up": SetSelected(Math.Max(_ranked.Selected - 1, -1)); break;
            case "enter" or "ctrl+enter":
                if (_ranked.Selected < 0) _bar.Submit(_bar.Text.Trim());
                else Invoke(_ranked.Selected, a == "ctrl+enter");
                return "ok";
            case "left" or "right":
                if (_ranked.SelectedItem?.Tag is PaletteEntry { Adjust: { } adjust }) adjust(a == "right" ? 1 : -1);
                break;
            default: return "error: down|up|enter|ctrl+enter|left|right";
        }
        return PipeState("");
    }

    string PipeFiles(string text)
    {
        var sql = WindowsSearchSql.Build(text, _profile, 30);
        if (sql is null) return "no query for that text";
        var done = new ManualResetEventSlim();
        IReadOnlyList<FileHit>? hits = null;
        long ms = -1;
        _files.Submit(new FileSearch.Request(-1, sql, (_, h, t) =>
        {
            hits = h;
            ms = t;
            done.Set();
        }));
        if (!done.Wait(TimeSpan.FromSeconds(3))) return "timed out";
        if (hits is null) return $"unavailable ({_files.LastError ?? "retrying later"})";
        return $"{ms} ms, {hits.Count} hits || " + string.Join(" | ", hits.Take(10).Select(h => (h.IsFolder ? "[dir] " : "") + h.Path));
    }

    string PipeAudio(string arg)
    {
        var devices = _audio.ListNow();
        var parts = arg.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["set", var n] && int.TryParse(n, out var i) && i >= 1 && i <= devices.Count)
            return _audio.SetDefault(devices[i - 1].Id) ? $"default is now {devices[i - 1].Name}" : "error: switch failed";
        var vol = _audio.Volume();
        return $"volume={(vol is { } v ? $"{Math.Round(v.Level * 100)}%{(v.Muted ? " muted" : "")}" : "none")} || " +
               string.Join(" | ", devices.Select((d, k) => $"{k + 1}. {d.Name}{(d.IsDefault ? " (default)" : "")}"));
    }

    public void Dispose()
    {
        _files.Dispose();
        _audio.Dispose();
        _icons?.Dispose();
    }
}
