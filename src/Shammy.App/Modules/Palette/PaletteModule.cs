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
internal sealed partial class PaletteModule : IShammyModule
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
        // Several commands in one line ("timer 5 minutes and pause the music"): one row that runs them in order.
        if (command is null && HostLinks.ChainPlan?.Invoke(clean) is { } plan)
        {
            items.Add(new PaletteItem("chain", PaletteKind.Command, plan, "Shammy · one after another", 100)
            {
                Hint = "Command",
                Confident = true,
                Tag = new PaletteEntry { Glyph = "", Run = _ => _bar.Submit(text) },
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

}
