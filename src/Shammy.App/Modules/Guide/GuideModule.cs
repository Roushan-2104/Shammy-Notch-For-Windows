using System.Windows;
using Shammy.Core.Guide;
using Shammy.Core.Notch;
using Shammy.Modules.Settings;
using Shammy.Views.Cards;

namespace Shammy.Modules.Guide;

/// <summary>What the guide remembers: which features have been used on this PC. It never leaves the PC.</summary>
internal sealed class GuideData
{
    public List<string> Used { get; set; } = new();
}

/// <summary>Lets other modules open the guide without knowing about it (null until the guide has started).</summary>
internal static class GuideLinks
{
    /// <summary>Opens the guide filtered to what is new in this version.</summary>
    public static Action? OpenNew { get; set; }

    /// <summary>Opens the guide searched for a feature's name.</summary>
    public static Action<string>? OpenFeature { get; set; }
}

/// <summary>
/// The feature guide: one view in the open notch that lists everything Shammy does (<see cref="FeatureGuide"/>),
/// with a finder that takes everyday words, suggestions for the moment, Try it and each feature's switch.
/// Ways in: "help", "features", "what can you do"; a feature's own short name ("mixer"); a question about
/// something Shammy does ("how do I record my screen"); the Guide chip; the tray; the note after an update.
/// It remembers which features were used (a typed or spoken command, or Try it) to mark the rest "not tried yet".
/// Pipes: "guide-state", "guide-open [new|untried|off] [words]", "guide-find &lt;words&gt;", "guide-ask &lt;text&gt;",
/// "guide-try &lt;id&gt; [run]" (says what it would start; "run" starts it), "guide-switch &lt;id&gt; on|off",
/// "guide-used [id|clear]", "guide-moment".
/// </summary>
internal sealed class GuideModule : IShammyModule
{
    public const string CardId = "guide";
    const string Glyph = "";

    ModuleContext _ctx = null!;
    GuideData _data = new();
    GuideView? _view;
    string _query = "";
    GuideFilter _filter;

    public string Name => "guide";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _data = ctx.Load<GuideData>();
        ctx.AddCard(new NotchCard
        {
            Id = CardId,
            Title = "Guide",
            Width = 176,
            Order = 89,
            Chip = true,
            IsRelevant = () => false,
            Compact = Tile(),
            Detail = BuildDetail,
        });
        // "help" used to start the tour; it now opens the guide (the tour is still "tour"), so this one goes first.
        ctx.AddCommandFirst(new Command(this, wholeGuideOnly: true));
        // Short names and questions come last: a real command for the same words always wins.
        ctx.AddCommand(new Command(this, wholeGuideOnly: false));
        ctx.AddTrayItem("What Shammy can do", () => Open("", GuideFilter.All));
        HostLinks.CommandRan += OnCommandRan;
        GuideLinks.OpenNew = () => Open("", GuideFilter.New);
        GuideLinks.OpenFeature = name => Open(name, GuideFilter.All);

        ctx.AddPipeCommand("guide-state", _ => State());
        ctx.AddPipeCommand("guide-open", arg =>
        {
            var words = arg.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var filter = GuideFilter.All;
            string query = arg.Trim();
            if (words.Length > 0 && Enum.TryParse<GuideFilter>(words[0], true, out var f) && !int.TryParse(words[0], out _))
            {
                filter = f;
                query = words.Length > 1 ? words[1] : "";
            }
            Open(query, filter);
            return State();
        });
        ctx.AddPipeCommand("guide-find", arg => string.Join(", ", FeatureFinder.Find(arg).Take(4).Select(m => $"{m.Feature.Id}:{m.Score}")) is { Length: > 0 } s ? s : "none");
        ctx.AddPipeCommand("guide-ask", arg => GuidePhrases.Parse(arg) is { } r ? $"guide query=\"{r.Query}\" feature={r.Feature?.Id ?? "-"}" : "none");
        ctx.AddPipeCommand("guide-try", arg =>
        {
            var words = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || FeatureGuide.ById(words[0]) is not { } feature) return "error: usage guide-try <feature id> [run]";
            return words.Length > 1 && words[1] == "run" ? Try(feature) : "would: " + Plan(feature);
        });
        ctx.AddPipeCommand("guide-switch", arg =>
        {
            var words = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length != 2 || FeatureGuide.ById(words[0]) is not { } feature || words[1] is not ("on" or "off")) return "error: usage guide-switch <feature id> on|off";
            string said = SetOn(feature, words[1] == "on");
            _view?.Refresh();
            return $"{said} | on={IsOn(feature)}";
        });
        ctx.AddPipeCommand("guide-used", arg =>
        {
            string a = arg.Trim();
            if (a == "clear")
            {
                _data.Used.Clear();
                ctx.Save(_data);
                _view?.Refresh();
            }
            else if (a.Length > 0)
            {
                if (FeatureGuide.ById(a) is null) return "error: no such feature";
                Mark(a);
            }
            return "used=" + string.Join(",", _data.Used);
        });
        ctx.AddPipeCommand("guide-moment", _ =>
        {
            var (moment, suggestions) = Suggest();
            return $"{moment}: " + string.Join(", ", suggestions.Select(s => s.Feature.Id));
        });
    }

    public void Dispose()
    {
        HostLinks.CommandRan -= OnCommandRan;
        GuideLinks.OpenNew = null;
        GuideLinks.OpenFeature = null;
    }

    string State() =>
        $"features={FeatureGuide.All.Count} used={_data.Used.Count} view={(_view is { IsVisible: true } v ? v.Describe() : "closed")} notch={_ctx.Notch.Mode}";

    // ---- Opening ----------------------------------------------------------------------------------

    void Open(string query, GuideFilter filter)
    {
        // Already on screen: search in place rather than building the view again.
        if (_view is { IsVisible: true } view)
        {
            view.Show(query, filter);
            return;
        }
        _query = query;
        _filter = filter;
        _ctx.OpenCard(CardId);
    }

    FrameworkElement BuildDetail()
    {
        _view = new GuideView(new GuideView.Host(
            () => HostLinks.Version?.Invoke(),
            id => _data.Used.Contains(id),
            IsOn,
            SetOn,
            Try,
            Suggest), _query, _filter)
        { Height = 276 };
        // The next plain open starts clean.
        _query = "";
        _filter = GuideFilter.All;
        return _view;
    }

    static FrameworkElement Tile()
    {
        var tile = new WallTile { Field = Fields.List() };
        var big = CardKit.Poster("Guide", 40);
        var label = CardKit.Label("Shammy");
        label.Margin = new Thickness(0, 6, 0, 0);
        tile.Art.Children.Add(CardKit.At(CardKit.Column(0, big, label), HorizontalAlignment.Left, VerticalAlignment.Top, WallTile.Pad, WallTile.Pad));
        tile.Rest.Children.Add(WallTile.BottomLeft(CardKit.Label("Guide")));
        CardKit.Name(tile, "Guide. Everything Shammy does");
        return tile;
    }

    // ---- What the view asks for --------------------------------------------------------------------

    bool IsOn(GuideFeature f)
    {
        switch (f.Switch)
        {
            case GuideSwitch.Module:
                return !_ctx.Settings.DisabledModules.Contains(f.SwitchName, StringComparer.OrdinalIgnoreCase);
            case GuideSwitch.Setting:
                return SettingsCatalog.Find(f.SwitchName) is { } info && SettingsCatalog.Get(_ctx.Settings, info) is true;
            default:
                return true;
        }
    }

    /// <summary>Switches a feature; returns what to tell the user.</summary>
    string SetOn(GuideFeature f, bool on)
    {
        try
        {
            switch (f.Switch)
            {
                case GuideSwitch.Module:
                    if (SettingsCatalog.Find(nameof(Infrastructure.AppSettings.DisabledModules)) is not { } modules) return "Couldn’t change that";
                    var list = _ctx.Settings.DisabledModules.Where(m => !string.Equals(m, f.SwitchName, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (!on) list.Add(f.SwitchName);
                    SettingsCatalog.Set(_ctx.Settings, modules, list);
                    return $"{f.Name} {(on ? "on" : "off")} after a restart";
                case GuideSwitch.Setting:
                    if (SettingsCatalog.Find(f.SwitchName) is not { } info) return "Couldn’t change that";
                    if (f.SwitchName == nameof(Infrastructure.AppSettings.PetEnabled))
                    {
                        _ctx.Notch.SetPetEnabled(on);
                        _ctx.Settings.NotifyChanged(f.SwitchName);
                    }
                    else SettingsCatalog.Set(_ctx.Settings, info, on);
                    return $"{f.Name} {(on ? "on" : "off")}";
                default:
                    return f.Name + " is always on";
            }
        }
        catch (Exception ex)
        {
            _ctx.Warn($"Switching {f.Id} from the guide failed", ex);
            return "Couldn’t change that";
        }
    }

    /// <summary>What Try it would do, in words (the pipe's dry run).</summary>
    string Plan(GuideFeature f) =>
        f.Try.Length == 0 ? "nothing to start"
        : !IsOn(f) ? "say it is switched off"
        : f.Try.StartsWith("card:", StringComparison.Ordinal) ? "open the " + f.Try[5..] + " card"
        : "run \"" + f.Try + "\"";

    /// <summary>Starts the feature the way its own words would; returns what to tell the user.</summary>
    string Try(GuideFeature f)
    {
        if (f.Try.Length == 0) return f.Name + " has nothing to start";
        if (!IsOn(f)) return f.Name + " is switched off. Switch it on first";
        Mark(f.Id);
        if (f.Try.StartsWith("card:", StringComparison.Ordinal))
        {
            if (!_ctx.OpenCard(f.Try[5..])) return "Couldn’t open " + f.Name;
        }
        else if (HostLinks.RunText is { } run) run(f.Try);
        else return "Couldn’t start " + f.Name;
        return "Started: " + f.Name;
    }

    (string, IReadOnlyList<GuideSuggestion>) Suggest()
    {
        bool playing = false;
        try { playing = _ctx.Services.Media.IsPlaying; }
        catch (Exception ex) { _ctx.Warn("Reading the music state for the guide failed", ex); }
        var now = new GuideMoment(playing, _ctx.Get("meeting.mic") is not null, EyeLogic.IsSleepy(DateTime.Now), _ctx.Get("agents.main") is not null);
        return GuideSuggestions.For(now, id => _data.Used.Contains(id), IsOn);
    }

    // ---- Remembering what was used -----------------------------------------------------------------

    void OnCommandRan(string text)
    {
        if (FeatureFinder.UsedBy(text) is { } feature) Mark(feature.Id);
    }

    void Mark(string id)
    {
        if (_data.Used.Contains(id)) return;
        _data.Used.Add(id);
        _ctx.Save(_data);
    }

    // ---- Typed and spoken ---------------------------------------------------------------------------

    CommandResult Run(GuideRequest request)
    {
        Open(request.Feature?.Name ?? request.Query, GuideFilter.All);
        return request.Feature is { } f
            ? new CommandResult(Glyph, f.Name, "Say or type: " + f.Phrase)
            : new CommandResult(Glyph, "Guide", $"{FeatureGuide.All.Count} features, in one place");
    }

    bool AnotherCommandTakes(string text) => _ctx.Commands.Any(c =>
    {
        if (c is Command) return false;
        try { return c.CanHandle(text); }
        catch (Exception) { return false; }
    });

    sealed class Command(GuideModule module, bool wholeGuideOnly) : ITextCommand, ICommandPreview
    {
        GuideRequest? Parse(string text)
        {
            var request = GuidePhrases.Parse(text);
            if (request is null) return null;
            // One command answers "help" and friends ahead of everything; the other answers the rest, after everything.
            return (request.Feature is null && request.Query.Length == 0) == wholeGuideOnly ? request : null;
        }

        // The short-name fallback ("stage", "dial") steps aside for any real command for the same words, whatever order
        // the modules started in: typing "stage" opens Stage, not Stage's entry in the guide.
        public bool CanHandle(string text) => Parse(text) is not null && (wholeGuideOnly || !module.AnotherCommandTakes(text));

        public Task<CommandResult> RunAsync(string text) =>
            Task.FromResult(Parse(text) is { } request ? module.Run(request) : new CommandResult(Glyph, "Guide"));

        public CommandPreview? Preview(string text) => Parse(text) switch
        {
            { Feature: { } f } => new(Glyph, f.Name, "In Shammy’s guide · " + f.Line),
            { } => new(Glyph, "Guide", $"{FeatureGuide.All.Count} features, in one place"),
            _ => null,
        };
    }
}
