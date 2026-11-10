using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Core.Quick;
using Shammy.Infrastructure;
using Shammy.Modules.MeetingSystem;
using Shammy.Views.Cards;

namespace Shammy.Modules.Quick;

/// <summary>
/// Quick actions: a card of six tiles (keep awake, lock, sleep, night light, do not disturb, mute mic) reached from
/// its chip, and the same actions typed or spoken ("keep awake for 2 hours", "lock my pc", "put the pc to sleep").
/// Keep-awake always has an end time; while it runs the pet shows "Awake" with the time left and a Stop circle.
/// Night light and Do not disturb have no supported switch in Windows, so their tiles open the Settings page.
/// Pipes: "quick-state", "quick-keepawake &lt;minutes|off&gt;", "quick-advance &lt;minutes&gt;", "quick-parse &lt;text&gt;",
/// "quick-run lock|sleep|nightlight|dnd|mic [--dry]", "quick-dry on|off", "quick-open".
/// </summary>
internal sealed class QuickModule : IShammyModule
{
    public const string CardId = "quick", ActivityId = "quick.keepawake";
    public const string NightLightPage = "ms-settings:nightlight", DoNotDisturbPage = "ms-settings:quiethours";

    // Just above music (20): a song steps aside for it, everything else comes first.
    const int Priority = 22;
    const string CupGlyph = "", LockGlyph = "", MoonGlyph = "", BoltGlyph = "", MicGlyph = "";

    // Long enough for the notch to close (and a command's confirmation to be read) before the screen goes.
    static readonly TimeSpan LockDelay = TimeSpan.FromMilliseconds(600), SleepDelay = TimeSpan.FromMilliseconds(1400);

    readonly KeepAwakeClock _clock = new();
    ModuleContext _ctx = null!;
    DispatcherTimer? _tick, _pending;
    KeepAwakeContent? _content;
    MicMute? _mic;
    QuickView? _view;
    bool _held, _micTried, _dry;
    string _last = "-";

    public string Name => "quick";

    /// <summary>Keep-awake started, stopped or lost a minute, or the microphone changed: redraw the tiles.</summary>
    public event Action? Changed;

    public bool KeepAwakeOn => _clock.IsOn;
    public int MinutesLeft => _clock.MinutesLeft(DateTime.UtcNow);
    public int StartedMinutes => _clock.StartedMinutes;

    /// <summary>The length the tile starts with (an hour until another is picked).</summary>
    public int Pick { get; private set; } = KeepAwakeClock.DefaultMinutes;

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _tick = new DispatcherTimer(DispatcherPriority.Background, ctx.Ui);
        _tick.Tick += (_, _) => Tick();

        ctx.AddCard(new NotchCard
        {
            Id = CardId,
            Title = "Quick actions",
            Width = 176,
            Order = 90,
            Chip = true,
            // Opened on purpose from its chip; while keep-awake runs the pet already says so.
            IsRelevant = () => false,
            Compact = QuickView.BuildCompact(() => ctx.OpenCard(CardId)),
            Detail = () => _view = new QuickView(this),
        });
        // Fixed phrases only, and ahead of the launcher so "open quick actions" isn't an app search.
        ctx.AddCommandFirst(new QuickTextCommand(this));

        ctx.AddPipeCommand("quick-state", _ => State());
        ctx.AddPipeCommand("quick-keepawake", KeepAwakeFromPipe);
        ctx.AddPipeCommand("quick-advance", Advance);
        ctx.AddPipeCommand("quick-parse", arg => Describe(QuickPhrases.Parse(arg)));
        ctx.AddPipeCommand("quick-run", RunFromPipe);
        ctx.AddPipeCommand("quick-dry", arg =>
        {
            _dry = arg.Trim().ToLowerInvariant() is not ("off" or "false" or "0");
            return "ok dry=" + (_dry ? "on" : "off");
        });
        ctx.AddPipeCommand("quick-open", _ => ctx.OpenCard(CardId) ? "ok" : "error: no quick card");
    }

    // ---- Keep awake --------------------------------------------------------------------------------

    /// <summary>Starts (or restarts) keep-awake; returns the minutes it will run, or null if Windows refused.</summary>
    internal int? StartKeepAwake(int minutes)
    {
        if (!Hold(true)) return null;
        int runs = _clock.Start(DateTime.UtcNow, minutes);
        _content ??= new KeepAwakeContent(_clock, () => StopKeepAwake());
        if (_ctx.Get(ActivityId) is null)
            _ctx.Post(new Activity(ActivityId, ActivityKind.Custom, Priority, _content) { Click = () => _ctx.OpenCard(CardId) });
        _content.Refresh();
        Arm();
        Changed?.Invoke();
        _ctx.Info($"Keep awake on for {runs} min");
        return runs;
    }

    /// <summary>Stops it and lets the PC sleep again; false if it wasn't on.</summary>
    internal bool StopKeepAwake()
    {
        if (!_clock.IsOn) return false;
        _clock.Stop();
        End();
        _ctx.Info("Keep awake off");
        return true;
    }

    /// <summary>The tile: off starts the picked length, on stops it.</summary>
    internal void ToggleKeepAwake()
    {
        if (StopKeepAwake()) return;
        if (StartKeepAwake(Pick) is null) _ctx.ShowMessage(CupGlyph, "Couldn’t keep the PC awake", error: true);
    }

    /// <summary>The tile's length: remembered for the next start, and applied now if it is running.</summary>
    internal void PickLength(int minutes)
    {
        Pick = KeepAwakeClock.Clamp(minutes);
        if (_clock.IsOn) StartKeepAwake(Pick);
        else Changed?.Invoke();
    }

    void End()
    {
        Hold(false);
        _tick?.Stop();
        _ctx.Remove(ActivityId);
        _content = null;
        Changed?.Invoke();
    }

    void Tick()
    {
        if (_clock.Expire(DateTime.UtcNow))
        {
            End();
            _ctx.Info("Keep awake ended");
            if (!QuietState.IsQuiet) _ctx.ShowMessage(CupGlyph, "Keep awake ended");
            return;
        }
        if (!_clock.IsOn)
        {
            _tick?.Stop();
            return;
        }
        _content?.Refresh();
        Arm();
        Changed?.Invoke();
    }

    /// <summary>Looks again when the minutes left change or it ends: one wake-up a minute, nothing faster.</summary>
    void Arm()
    {
        if (_tick is null) return;
        _tick.Stop();
        _tick.Interval = _clock.NextCheck(DateTime.UtcNow);
        _tick.Start();
    }

    /// <summary>
    /// Asks Windows to keep the PC and the display on (or lets go). The request belongs to the thread that made
    /// it, so it is always made and cleared on the UI thread.
    /// </summary>
    bool Hold(bool on)
    {
        if (!_ctx.Ui.CheckAccess())
        {
            try { return _ctx.Ui.Invoke(() => Hold(on)); }
            catch (Exception ex)
            {
                _ctx.Warn("Reaching the UI thread for keep-awake failed", ex);
                return false;
            }
        }
        if (_held == on) return true;
        try
        {
            uint flags = on ? QuickNative.Continuous | QuickNative.SystemRequired | QuickNative.DisplayRequired : QuickNative.Continuous;
            if (QuickNative.SetThreadExecutionState(flags) == 0)
            {
                _ctx.Warn($"Windows refused keep-awake {(on ? "on" : "off")}");
                return false;
            }
            _held = on;
            return true;
        }
        catch (Exception ex)
        {
            _ctx.Warn("Keep-awake failed", ex);
            return false;
        }
    }

    // ---- Lock, sleep, settings pages, microphone ----------------------------------------------------

    /// <summary>Locks the PC once the notch has closed. In a dry run it only says what it would do.</summary>
    internal string Lock(bool dry = false)
    {
        if (dry || _dry) return WouldDo("would lock: LockWorkStation", LockGlyph, "Would lock this PC", dry);
        _ctx.Notch.Collapse();
        After(LockDelay, () =>
        {
            try
            {
                if (!QuickNative.LockWorkStation()) Failed("Couldn’t lock this PC", LockGlyph, Marshal.GetLastWin32Error());
            }
            catch (Exception ex)
            {
                _ctx.Warn("Locking failed", ex);
                _ctx.ShowMessage(LockGlyph, "Couldn’t lock this PC", error: true);
            }
        });
        return _last = "lock";
    }

    /// <summary>Puts the PC to sleep once the notch has closed. In a dry run it only says what it would do.</summary>
    internal string Sleep(bool dry = false)
    {
        if (dry || _dry)
            return WouldDo($"would sleep: SetSuspendState(hibernate: false){(_clock.IsOn ? ", after stopping keep-awake" : "")}",
                MoonGlyph, "Would put the PC to sleep", dry);
        // Asking for sleep ends keep-awake: it must not still be holding the PC on when it wakes.
        StopKeepAwake();
        _ctx.Notch.Collapse();
        After(SleepDelay, () => Task.Run(() =>
        {
            // Off the UI thread: the call only returns once the PC has woken up again.
            try
            {
                if (!QuickNative.SetSuspendState(false, false, false))
                {
                    int error = Marshal.GetLastWin32Error();
                    _ctx.Ui.BeginInvoke(() => Failed("Couldn’t put the PC to sleep", MoonGlyph, error));
                }
            }
            catch (Exception ex)
            {
                _ctx.Warn("Sleep failed", ex);
                _ctx.Ui.BeginInvoke(() => _ctx.ShowMessage(MoonGlyph, "Couldn’t put the PC to sleep", error: true));
            }
        }));
        return _last = "sleep";
    }

    /// <summary>A dry run: remembers what would have happened, and says so in the notch when a tile or a command asked.</summary>
    string WouldDo(string what, string glyph, string message, bool fromPipe)
    {
        if (!fromPipe) _ctx.ShowMessage(glyph, message + " (test mode)");
        return _last = what;
    }

    void Failed(string message, string glyph, int error)
    {
        _ctx.Warn($"{message} (Windows error {error})");
        _ctx.ShowMessage(glyph, message, error: true);
    }

    /// <summary>One thing a moment from now; a second request replaces the first.</summary>
    void After(TimeSpan delay, Action run)
    {
        _pending?.Stop();
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _ctx.Ui) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_pending, timer)) _pending = null;
            run();
        };
        _pending = timer;
        timer.Start();
    }

    /// <summary>Opens one of the two fixed Windows Settings pages (night light, do not disturb).</summary>
    internal string OpenSettings(string page, bool dry = false)
    {
        if (page is not (NightLightPage or DoNotDisturbPage)) return "error: unknown page";
        if (dry) return _last = "would open: " + page;
        if (!_ctx.Open(page))
        {
            _ctx.ShowMessage("", "Couldn’t open Settings", error: true);
            return _last = "error: couldn't open " + page;
        }
        _ctx.Notch.Collapse();
        return _last = "opened " + page;
    }

    /// <summary>
    /// The same default microphone the "mute mic" command switches. Bound the first time the card or the pipe
    /// asks, so a PC that never opens Quick actions never pays for it.
    /// </summary>
    MicMute? Mic()
    {
        if (_mic is not null || _micTried) return _mic;
        _micTried = true;
        try
        {
            var mic = new MicMute(_ctx.Ui, _ctx.Warn);
            mic.Changed += OnMicChanged;
            mic.Start();
            _mic = mic;
        }
        catch (Exception ex)
        {
            _ctx.Warn("Microphone unavailable for quick actions", ex);
        }
        return _mic;
    }

    void OnMicChanged() => Changed?.Invoke();

    /// <summary>True muted, false live, null when there is no microphone.</summary>
    internal bool? MicMuted => Mic() is { HasDevice: true } mic ? mic.Muted : null;

    internal void ToggleMic()
    {
        if (Mic()?.Toggle() is not bool muted)
        {
            _ctx.ShowMessage(MicGlyph, "No microphone found", error: true);
            return;
        }
        // The microphone's own change notice redraws the tile.
        _last = muted ? "mic muted" : "mic live";
    }

    // ---- Typed and spoken commands -----------------------------------------------------------------

    static string Until(int minutes) =>
        "Until " + DateTime.Now.AddMinutes(minutes).ToString(CardKit.H24 ? "HH:mm" : "h:mm tt", CultureInfo.InvariantCulture);

    CommandResult Run(QuickCommand? command)
    {
        if (command is null) return new CommandResult(BoltGlyph, "Say keep awake, lock or sleep my PC", IsError: true);
        switch (command.Kind)
        {
            case QuickKind.KeepAwake:
            {
                int asked = command.Minutes ?? KeepAwakeClock.DefaultMinutes;
                if (StartKeepAwake(asked) is not int runs) return new CommandResult(CupGlyph, "Couldn’t keep the PC awake", IsError: true);
                return new CommandResult(CupGlyph, $"Keeping awake for {KeepAwakeText.Long(runs)}",
                    asked > runs ? $"{KeepAwakeText.Long(runs)} is the longest it goes" : Until(runs));
            }
            case QuickKind.StopKeepAwake:
                return StopKeepAwake()
                    ? new CommandResult(CupGlyph, "Keep awake is off", "The PC can sleep again")
                    : new CommandResult(CupGlyph, "Keep awake wasn’t on");
            case QuickKind.Lock:
                return Lock().StartsWith("would", StringComparison.Ordinal)
                    ? new CommandResult(LockGlyph, "Would lock this PC", "Test mode: nothing happened")
                    : new CommandResult(LockGlyph, "Locking this PC");
            case QuickKind.Sleep:
                return Sleep().StartsWith("would", StringComparison.Ordinal)
                    ? new CommandResult(MoonGlyph, "Would put the PC to sleep", "Test mode: nothing happened")
                    : new CommandResult(MoonGlyph, "Going to sleep");
            case QuickKind.Open:
                // Opens once this confirmation has closed.
                _ctx.OpenCard(CardId);
                return new CommandResult(BoltGlyph, "Quick actions", "Keep awake, lock, sleep and more");
            default:
                return new CommandResult(BoltGlyph, "Quick actions");
        }
    }

    CommandPreview? Preview(QuickCommand? command)
    {
        if (command is null) return null;
        switch (command.Kind)
        {
            case QuickKind.KeepAwake:
            {
                int runs = KeepAwakeClock.Clamp(command.Minutes ?? KeepAwakeClock.DefaultMinutes);
                return new CommandPreview(CupGlyph, $"Keep awake for {KeepAwakeText.Long(runs)}", Until(runs));
            }
            case QuickKind.StopKeepAwake:
                return new CommandPreview(CupGlyph, "Stop keeping awake", _clock.IsOn ? $"{KeepAwakeText.Long(Math.Max(1, MinutesLeft))} left" : "It isn’t on");
            case QuickKind.Lock: return new CommandPreview(LockGlyph, "Lock this PC");
            case QuickKind.Sleep: return new CommandPreview(MoonGlyph, "Put the PC to sleep");
            case QuickKind.Open: return new CommandPreview(BoltGlyph, "Open quick actions", "Keep awake, lock, sleep and more");
            default: return null;
        }
    }

    sealed class QuickTextCommand(QuickModule module) : ITextCommand, ICommandPreview
    {
        public bool CanHandle(string text) => QuickPhrases.Parse(text) is not null;

        public Task<CommandResult> RunAsync(string text) => Task.FromResult(module.Run(QuickPhrases.Parse(text)));

        public CommandPreview? Preview(string text) => module.Preview(QuickPhrases.Parse(text));
    }

    // ---- Test pipe ---------------------------------------------------------------------------------

    static string Describe(QuickCommand? c) => c switch
    {
        null => "none",
        { Kind: QuickKind.KeepAwake, Minutes: int m } => string.Create(CultureInfo.InvariantCulture, $"keepawake minutes={m} runs={KeepAwakeClock.Clamp(m)}"),
        { Kind: QuickKind.KeepAwake } => string.Create(CultureInfo.InvariantCulture, $"keepawake minutes=default runs={KeepAwakeClock.DefaultMinutes}"),
        { Kind: QuickKind.StopKeepAwake } => "keepawake off",
        { Kind: QuickKind.Lock } => "lock",
        { Kind: QuickKind.Sleep } => "sleep",
        _ => "open",
    };

    string State()
    {
        var now = DateTime.UtcNow;
        string mic = MicMuted switch { true => "muted", false => "live", null => "none" };
        string ends = _clock.EndsUtc is { } e ? e.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "-";
        string card = _view is { IsVisible: true } v ? "open " + v.Describe() : "closed";
        return string.Create(CultureInfo.InvariantCulture,
            $"keepawake={(_clock.IsOn ? "on" : "off")} minutes={_clock.MinutesLeft(now)} started={_clock.StartedMinutes} ends={ends} " +
            $"held={_held} activity={(_ctx.Get(ActivityId) is null ? "hidden" : "shown")} pick={Pick} mic={mic} dry={(_dry ? "on" : "off")} " +
            $"last=\"{_last}\" card={card}");
    }

    /// <summary>"quick-keepawake 60", "quick-keepawake off"; with nothing, what it is doing.</summary>
    string KeepAwakeFromPipe(string arg)
    {
        var a = arg.Trim().ToLowerInvariant();
        if (a.Length == 0) return State();
        if (a is "off" or "stop") return StopKeepAwake() ? "ok off" : "ok: wasn't on";
        if (!int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) || minutes < 1)
            return "error: usage quick-keepawake <minutes|off>";
        return StartKeepAwake(minutes) is int runs
            ? string.Create(CultureInfo.InvariantCulture, $"ok on minutes={runs}")
            : "error: Windows refused";
    }

    /// <summary>"quick-advance 20": as if 20 minutes had passed (fractions allowed), so the end can be watched without waiting.</summary>
    string Advance(string arg)
    {
        if (!double.TryParse(arg.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double minutes) || minutes <= 0)
            return "error: usage quick-advance <minutes>";
        if (!_clock.IsOn) return "error: keep-awake is off";
        _clock.Advance(TimeSpan.FromMinutes(minutes));
        Tick();
        return State();
    }

    /// <summary>
    /// "quick-run lock --dry", "quick-run sleep --dry": what it would do, without doing it. The pipe never really
    /// locks or sleeps, so those two refuse without --dry. "nightlight", "dnd" and "mic" run for real without it.
    /// </summary>
    string RunFromPipe(string arg)
    {
        var words = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToList();
        bool dry = words.RemoveAll(w => w is "--dry" or "dry") > 0;
        if (words.Count != 1) return "error: usage quick-run lock|sleep|nightlight|dnd|mic [--dry]";
        switch (words[0])
        {
            case "lock": return dry ? Lock(dry: true) : "error: the pipe never really locks; add --dry";
            case "sleep": return dry ? Sleep(dry: true) : "error: the pipe never really sleeps; add --dry";
            case "nightlight": return OpenSettings(NightLightPage, dry);
            case "dnd": return OpenSettings(DoNotDisturbPage, dry);
            case "mic":
                if (dry) return MicMuted switch { true => "would unmute the mic", false => "would mute the mic", null => "no microphone" };
                ToggleMic();
                return "mic=" + (MicMuted switch { true => "muted", false => "live", null => "none" });
            default: return "error: usage quick-run lock|sleep|nightlight|dnd|mic [--dry]";
        }
    }

    public void Dispose()
    {
        _tick?.Stop();
        _pending?.Stop();
        _clock.Stop();
        // Never leave the PC held awake by a notch that has gone.
        if (_ctx is not null)
        {
            Hold(false);
            _ctx.Remove(ActivityId);
        }
        if (_mic is not null)
        {
            _mic.Changed -= OnMicChanged;
            _mic.Dispose();
        }
    }
}
