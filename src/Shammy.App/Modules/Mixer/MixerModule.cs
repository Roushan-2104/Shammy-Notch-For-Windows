using Shammy.Core.Mixer;
using Shammy.Modules.Palette;
using Shammy.SystemInfo;

namespace Shammy.Modules.Mixer;

/// <summary>
/// Volume for each app: a "Volume" card in the open notch with the output's own level first and then a slider
/// for every app that is making sound, plus a switcher for the output. Opened from its chip or by "volume
/// mixer", "mixer", "app volume", "app volumes" ("volume 50", "volume up" and "mute" stay the level commands').
/// Nothing runs while the view is closed.
///
/// Pipes: "mixer-state" (one line: output, master, then each row as name=level[,muted]), "mixer-set &lt;name&gt;
/// &lt;0-100&gt;", "mixer-mute &lt;name&gt; on|off", "mixer-open", and "mixer-fake &lt;rows&gt;|off", which swaps the
/// real apps for stand-ins (written as the state writes them) so the view can be tried without touching the
/// PC's volumes.
/// </summary>
internal sealed class MixerModule : IShammyModule
{
    public const string CardId = "mixer";

    ModuleContext _ctx = null!;
    LiveMixer _live = null!;
    FakeMixer? _fake;
    IconLoader? _icons;
    MixerView? _view;

    public string Name => "mixer";

    /// <summary>Where rows come from right now: Windows, or the test's stand-ins.</summary>
    internal IMixerSource Source => _fake is { } fake ? fake : _live;

    /// <summary>The app's own master-volume service; null if audio never started.</summary>
    internal VolumeService? Volume => _ctx.Services.Volume;

    /// <summary>App icons, read in the background; made the first time the view opens.</summary>
    internal IconLoader Icons => _icons ??= new IconLoader(_ctx.Ui);

    /// <summary>What the switcher showed last, so it is there at once the next time the view opens.</summary>
    internal IReadOnlyList<MixerOutput> LastOutputs { get; set; } = Array.Empty<MixerOutput>();

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _live = new LiveMixer(ctx.Ui, () => ctx.Services.Volume, ctx.Warn);

        // Chip-only: opened on purpose, never pushed onto the home row.
        ctx.AddCard(new NotchCard
        {
            Id = CardId,
            Title = "Volume mixer",
            Width = 176,
            Order = 88,
            Chip = true,
            IsRelevant = () => false,
            Compact = MixerTile.Build(() => ctx.OpenCard(CardId)),
            Detail = () => _view = new MixerView(this),
        });
        // Before the launcher and the palette, which know Windows' own "Volume mixer".
        ctx.AddCommandFirst(new MixerCommand(ctx));

        ctx.AddPipeCommand("mixer-state", _ => Look());
        ctx.AddPipeCommand("mixer-open", _ => ctx.OpenCard(CardId) ? "ok" : "error: no volume card");
        ctx.AddPipeCommand("mixer-set", arg => MixerPhrase.ParseSet(arg) is var (name, percent)
            ? Change(name, (source, row) => source.SetLevel(row.Key, percent / 100f), (view, row) => view.SetLevel(row.Key, percent / 100f))
            : "error: usage mixer-set <app name> <0-100>");
        ctx.AddPipeCommand("mixer-mute", arg => MixerPhrase.ParseMute(arg) is var (name, muted)
            ? Change(name, (source, row) => source.SetMuted(row.Key, muted), (view, row) => view.SetMuted(row.Key, muted))
            : "error: usage mixer-mute <app name> on|off");
        ctx.AddPipeCommand("mixer-fake", Fake);
    }

    sealed class MixerCommand(ModuleContext ctx) : ITextCommand, ICommandPreview
    {
        const string Glyph = "\uE767";

        public bool CanHandle(string text) => MixerPhrase.IsOpen(text);

        public Task<CommandResult> RunAsync(string text)
        {
            ctx.OpenCard(CardId);
            return Task.FromResult(new CommandResult(Glyph, "Volume mixer", "A slider for each app"));
        }

        public CommandPreview? Preview(string text) => new(Glyph, "Open the volume mixer", "A slider for each app");
    }

    /// <summary>A short message in the notch when something couldn't be done.</summary>
    internal void Say(string text) => _ctx.ShowMessage("\uE7F5", text, error: true);

    // ---- The pipe --------------------------------------------------------------------------------

    bool ViewOpen => _view is { IsOn: true };

    string Look()
    {
        if (_view is { IsOn: true } view) return Line(view.RefreshNow(), view.OutputLabel);
        var source = Source;
        source.Start();
        try
        {
            return Line(Rows(new MixerBoard(), source.ReadNow()), CurrentOutput());
        }
        finally
        {
            source.Stop();
        }
    }

    /// <summary>
    /// Finds a row by name and changes it: through the view when it is open (so it behaves as a click or a drag
    /// would), straight at the source when it is closed. Answers with the state afterwards.
    /// </summary>
    string Change(string name, Action<IMixerSource, MixerRow> direct, Action<MixerView, MixerRow> onView)
    {
        if (_view is { IsOn: true } view)
        {
            if (MixerBoard.Find(view.RefreshNow(), name) is not { } shown) return NoRow(name, view.Rows);
            onView(view, shown);
            return Line(view.RefreshNow(), view.OutputLabel);
        }
        var source = Source;
        source.Start();
        try
        {
            var board = new MixerBoard();
            var rows = Rows(board, source.ReadNow());
            if (MixerBoard.Find(rows, name) is not { } row) return NoRow(name, rows);
            board.Keep(row.Key);
            direct(source, row);
            return Line(Rows(board, source.ReadNow()), CurrentOutput());
        }
        finally
        {
            source.Stop();
        }
    }

    static IReadOnlyList<MixerRow> Rows(MixerBoard board, MixerReading? reading) =>
        reading is null ? Array.Empty<MixerRow>() : board.Update(reading.MasterLevel, reading.MasterMuted, reading.Sources);

    string Line(IReadOnlyList<MixerRow> rows, string? output) =>
        $"{MixerPhrase.State(output, rows)} [view {(ViewOpen ? "open" : "closed")}{(_fake is null ? "" : ", stand-in apps")}]";

    static string NoRow(string name, IReadOnlyList<MixerRow> rows) =>
        $"error: no row called '{name}' (rows: {(rows.Count == 0 ? "none" : string.Join(", ", rows.Select(r => r.Name)))})";

    /// <summary>The output's short name while the view (and its switcher) is closed; waits for Windows' list.</summary>
    string? CurrentOutput()
    {
        try
        {
            using var outputs = new AudioOutputs();
            var listed = outputs.ListNow();
            return MixerOutputs.Choose(listed.Select(d => (d.Id, d.Name, d.IsDefault))).FirstOrDefault(o => o.Current)?.Label;
        }
        catch (Exception ex)
        {
            _ctx.Warn("Reading the sound outputs failed", ex);
            return null;
        }
    }

    string Fake(string arg)
    {
        arg = arg.Trim();
        if (arg.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            _fake = null;
            _view?.Restart();
            return "ok: real apps";
        }
        if (MixerPhrase.ParseRows(arg) is not { } entries)
            return "error: usage mixer-fake Everything=64; YouTube Music=80; Google Meet=100; Game=35,muted | mixer-fake off";
        // The open view lets go of the real audio (it stops the source it started) and begins again on these.
        _fake = new FakeMixer(entries);
        _view?.Restart();
        return Look();
    }

    public void Dispose()
    {
        _view?.Shutdown();
        _live?.Shutdown();
        _icons?.Dispose();
    }
}
