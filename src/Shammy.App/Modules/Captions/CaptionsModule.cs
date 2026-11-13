using System.Globalization;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Core.Captions;
using Shammy.Core.Settings;
using Shammy.Shell;
using Shammy.Views;

namespace Shammy.Modules.Captions;

/// <summary>
/// The latest live caption, for any module that wants it (the Stage feature shows the other person's words under
/// the camera). Updated on the UI thread while captions are on; empty and quiet while they are off.
/// </summary>
internal static class CaptionsFeed
{
    /// <summary>Captions are running.</summary>
    public static bool IsOn { get; internal set; }

    /// <summary>The last line or two of what is being said (Live Captions' own last lines), or "".</summary>
    public static string Latest { get; private set; } = "";

    /// <summary>Raised on the UI thread with <see cref="Latest"/> whenever it changes ("" when captions turn off).</summary>
    public static event Action<string>? Changed;

    internal static void Set(string text)
    {
        if (string.Equals(text, Latest, StringComparison.Ordinal)) return;
        Latest = text;
        Changed?.Invoke(text);
    }
}

/// <summary>
/// Live captions for whatever the PC is playing (a lecture, a video, a call), from Windows Live Captions
/// (Windows 11 22H2+), which captions on the PC itself. Shammy starts it if needed, keeps its window minimised and
/// out of the way while captions are on (putting it back after), reads its words through UI Automation about
/// twice a second (<see cref="LiveCaptionsReader"/>) and shows them in a two-line strip under the notch
/// (<see cref="CaptionsStrip"/>). Older Windows says plainly that captions need Windows 11 22H2 or later.
/// Everything here (the reader, the timer, the window) exists only while captions are on.
/// Pipes: "captions-state", "captions-on", "captions-off", "captions-feed &lt;text&gt;" (as if Live Captions had
/// shown it).
/// </summary>
internal sealed class CaptionsModule : IShammyModule
{
    public const string ActivityId = "captions.on";

    // Just above music (20) and keep-awake (22): a song steps aside for it, everything else comes first.
    const int Priority = 23;
    const string Glyph = "";
    const string Starting = "Starting Windows Live Captions…", Listening = "Listening for speech…",
        Setup = "Finish setting up Windows Live Captions in its window";
    const string NeedsWindows11 = "Live captions need Windows 11 (22H2 or newer)";

    /// <summary>How long the strip stays after the last new words.</summary>
    static readonly TimeSpan Linger = TimeSpan.FromSeconds(6);

    /// <summary>Words of Live Captions' transcript handed to the merger each time it changes.</summary>
    const int TailWords = 40;

    ModuleContext _ctx = null!;
    Session? _session;
    string _lastRun = "never on";

    public string Name => "captions";

    public bool IsOn => _session is not null;

    /// <summary>Everything one stretch of captions owns; made when they turn on and gone when they turn off.</summary>
    sealed class Session
    {
        public required CaptionsStrip Strip { get; init; }
        public required CaptionLines Lines { get; init; }
        public CaptionMerger Merger { get; } = new();
        public LiveCaptionsReader? Reader;
        public DispatcherTimer Timer = null!;

        /// <summary>Watches for the notch changing shape; see <see cref="TurnOn"/>.</summary>
        public EventHandler? NotchMoved;
        public NotchMode ModeSeen;

        /// <summary>A plain line shown instead of a caption, until words arrive.</summary>
        public string? Message;

        /// <summary>When the strip last had something new to show.</summary>
        public DateTime ShownAt;

        /// <summary>The newest text from Live Captions not yet shown (written off the UI thread).</summary>
        public string? Pending;
        public int Updates, Fed;
        public string LastText = "";
    }

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        // Shammy stopped last time with Live Captions minimised: put its window back.
        _ = Task.Run(LiveCaptionsReader.RecoverStranded);
        // Fixed phrases only, and first so "stop captions" is never taken for a music or timer command.
        ctx.AddCommandFirst(new CaptionsCommand(this));
        ctx.AddTrayToggle("Live captions", () => IsOn, on => OnUi(() =>
        {
            if (!on) TurnOff();
            else if (TurnOn() is { } why) Explain(why);
        }));

        ctx.AddPipeCommand("captions-state", _ => State());
        ctx.AddPipeCommand("captions-on", _ => TurnOn() is { } why ? "error: " + why : "ok " + State());
        ctx.AddPipeCommand("captions-off", _ => TurnOff() ? "ok " + State() : "ok: captions weren't on");
        ctx.AddPipeCommand("captions-feed", Feed);
    }

    void OnUi(Action action)
    {
        if (_ctx.Ui.CheckAccess()) action();
        else _ctx.Ui.BeginInvoke(action);
    }

    /// <summary>Why captions couldn't start, in the notch; the Windows version one opens Windows' captions settings on click.</summary>
    void Explain(string why)
    {
        if (why != NeedsWindows11)
        {
            _ctx.ShowMessage(Glyph, why, error: true);
            return;
        }
        _ctx.Post(new Activity($"{Name}.message", ActivityKind.Message, 50, new MessagePayload(Glyph, why + " · click for settings", true),
            DateTime.UtcNow.AddSeconds(6)) { Click = LiveCaptionsReader.OpenSettings });
    }

    // ---- On and off ---------------------------------------------------------------------------------

    /// <summary>Turns captions on; null when they are on now, otherwise why they could not start.</summary>
    internal string? TurnOn() => TurnOn(withReader: true);

    string? TurnOn(bool withReader)
    {
        if (_session is not null) return null;
        if (withReader && !LiveCaptionsReader.IsSupported()) return NeedsWindows11;

        CaptionsStrip strip;
        try
        {
            strip = new CaptionsStrip();
        }
        catch (Exception ex)
        {
            _ctx.Warn("Making the caption strip failed", ex);
            return "Couldn’t show captions";
        }
        var s = new Session
        {
            Strip = strip,
            Lines = new CaptionLines(CaptionsStrip.LineWidth, strip.Measure),
        };
        _session = s;
        CaptionsFeed.IsOn = true;
        if (withReader)
        {
            s.Reader = new LiveCaptionsReader(text => OnText(s, text), (message, ex) => _ctx.Warn(message, ex));
            s.Reader.Start();
        }
        Say(s, withReader ? Starting : Listening);
        _ctx.Post(new Activity(ActivityId, ActivityKind.Custom, Priority, new CaptionsContent(() => TurnOff())));
        // Once a second: is there room under the notch, has the strip had nothing new for a while, and is Live
        // Captions waiting for its setup?
        s.Timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(s), _ctx.Ui);
        s.Timer.Start();
        // The notch has no "mode changed" event, but it always lays out when it changes shape. Looking at the mode
        // then (one comparison) gets the strip out of the way the moment a card drops, not up to a second later.
        s.ModeSeen = _ctx.Notch.Mode;
        s.NotchMoved = (_, _) =>
        {
            var mode = _ctx.Notch.Mode;
            if (mode == s.ModeSeen) return;
            s.ModeSeen = mode;
            _ctx.Ui.BeginInvoke(() =>
            {
                if (ReferenceEquals(_session, s)) UpdateStrip(s);
            });
        };
        _ctx.Notch.LayoutUpdated += s.NotchMoved;
        UpdateStrip(s);
        _ctx.Info(withReader ? "Captions on (Windows Live Captions)" : "Captions on (fed only)");
        return null;
    }

    /// <summary>Stops everything captions run; false if they weren't on.</summary>
    internal bool TurnOff() => TurnOff(waitForWindow: false);

    /// <param name="waitForWindow">Put Live Captions' window back before returning (Shammy is closing).</param>
    bool TurnOff(bool waitForWindow)
    {
        if (_session is not { } s) return false;
        _session = null;
        s.Timer.Stop();
        _ctx.Notch.LayoutUpdated -= s.NotchMoved;
        var reader = s.Reader;
        s.Reader = null;
        Close(s.Strip);
        _ctx.Remove(ActivityId);
        CaptionsFeed.IsOn = false;
        CaptionsFeed.Set("");
        _lastRun = string.Create(CultureInfo.InvariantCulture,
            $"updates={s.Updates} fed={s.Fed} reads={reader?.Reads ?? 0} live_captions_started_by_shammy={reader?.StartedIt ?? false}");
        // Putting Live Captions' window back waits for the last read to finish: off the UI thread, unless Shammy
        // is closing (then it must happen before the process ends).
        if (reader is not null)
        {
            void Stop()
            {
                try { reader.Dispose(); }
                catch (Exception ex) { _ctx.Warn("Stopping Live Captions failed", ex); }
            }
            if (waitForWindow) Stop();
            else _ = Task.Run(Stop);
        }
        _ctx.Info($"Captions off ({_lastRun})");
        return true;
    }

    void Close(CaptionsStrip strip)
    {
        try
        {
            strip.Close();
        }
        catch (Exception ex)
        {
            _ctx.Warn("Closing the caption strip failed", ex);
        }
    }

    // ---- New words ------------------------------------------------------------------------------------

    /// <summary>Live Captions' text changed (background thread): keep only the newest, and show it once per UI turn.</summary>
    void OnText(Session s, string raw)
    {
        bool scheduled = Interlocked.Exchange(ref s.Pending, raw) is not null;
        if (scheduled) return;
        _ctx.Ui.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var text = Interlocked.Exchange(ref s.Pending, null);
            if (text is null || !ReferenceEquals(_session, s)) return;
            s.Updates++;
            CaptionsFeed.Set(LiveCaptionsText.LastLines(text));
            Heard(s, LiveCaptionsText.Tail(text, TailWords));
        });
    }

    /// <summary>Merge the newest words into the caption and show it.</summary>
    void Heard(Session s, string text)
    {
        if (!ReferenceEquals(_session, s) || text.Length == 0) return;
        s.LastText = text;
        var merge = s.Merger.Merge(text, overlaps: true);
        if (merge.Changed)
        {
            s.Message = null;
            s.Strip.SetView(s.Lines.Layout(s.Merger.Words, s.Merger.Dropped), s.Merger.Batch);
            if (merge.Added > 0) s.ShownAt = DateTime.UtcNow;
        }
        UpdateStrip(s);
    }

    void Say(Session s, string message)
    {
        s.Message = message;
        s.ShownAt = DateTime.UtcNow;
        s.Strip.SetMessage(message);
    }

    void Tick(Session s)
    {
        if (!ReferenceEquals(_session, s)) return;
        if (s.Reader is { Problem: { } problem })
        {
            // Live Captions couldn't be started or read: say so once, then step back.
            if (s.Message != problem) Say(s, problem);
        }
        else if (s.Reader is { } reader && s.Merger.Words.Count == 0)
        {
            // Nothing heard yet: say what Live Captions is doing (starting, waiting for its setup, or ready).
            var waiting = reader.WindowState == "shown for setup" ? Setup : reader.Reading ? Listening : Starting;
            if (s.Message != waiting) Say(s, waiting);
        }
        UpdateStrip(s);
    }

    /// <summary>
    /// The strip shows only while the notch is at rest or showing its compact pill (anything taller would sit on
    /// top of it), and only while it has had something new in the last few seconds or is still starting.
    /// </summary>
    void UpdateStrip(Session s)
    {
        try
        {
            var mode = _ctx.Notch.Mode;
            bool room = mode is NotchMode.Hidden or NotchMode.Idle or NotchMode.Compact;
            bool something = s.Message is Starting or Setup || DateTime.UtcNow - s.ShownAt < Linger;
            if (room && something)
            {
                s.Strip.LanguageName = "Windows Live Captions";
                s.Strip.Place(_ctx.Notch.Monitor, _ctx.Settings.PinToScreenEdge, Below(mode));
                if (s.Strip.IsVisible) return;
                s.Strip.Show();
                s.Strip.StayOnTop();
            }
            else if (s.Strip.IsVisible)
            {
                s.Strip.Hide();
            }
        }
        catch (Exception ex)
        {
            _ctx.Warn("Placing the caption strip failed", ex);
        }
    }

    /// <summary>How far under the top of the notch's area the strip starts: just below the compact pill.</summary>
    double Below(NotchMode mode)
    {
        // The notch is tucked away (a fullscreen video): sit near the top edge.
        if (mode == NotchMode.Hidden) return 10;
        // The pet's pill is 38 tall, and 62 while a lyric line hangs under it.
        bool lyric = _ctx.Settings.LyricLineEnabled && _ctx.Services.Media.IsPlaying
                     && !string.IsNullOrEmpty(HostLinks.CurrentLyric?.Invoke());
        return (lyric ? ActivityView.TallHeight : ActivityView.ViewHeight) * NotchLook.Scale(_ctx.Settings.NotchSize) + 8;
    }

    // ---- Typed and spoken commands --------------------------------------------------------------------

    CommandResult Run(bool on)
    {
        if (!on)
        {
            return TurnOff()
                ? new CommandResult(Glyph, "Live captions are off")
                : new CommandResult(Glyph, "Captions weren’t on");
        }
        if (IsOn) return new CommandResult(Glyph, "Captions are already on", "Say “captions off” to stop them");
        if (TurnOn() is { } why)
        {
            if (why == NeedsWindows11) LiveCaptionsReader.OpenSettings();
            return new CommandResult(Glyph, why, why == NeedsWindows11 ? "Opening Windows’ captions settings" : null, IsError: true);
        }
        return new CommandResult(Glyph, "Live captions are on", "They show under the notch when someone speaks");
    }

    CommandPreview Preview(bool on)
    {
        if (!on) return new CommandPreview(Glyph, "Turn off live captions", IsOn ? null : "They aren’t on");
        return IsOn
            ? new CommandPreview(Glyph, "Live captions are on", "Say “captions off” to stop them")
            : new CommandPreview(Glyph, "Turn on live captions",
                LiveCaptionsReader.IsSupported() ? "From Windows Live Captions · made on this PC" : NeedsWindows11);
    }

    sealed class CaptionsCommand(CaptionsModule module) : ITextCommand, ICommandPreview
    {
        public bool CanHandle(string text) => CaptionPhrases.Parse(text) is not null;

        public Task<CommandResult> RunAsync(string text) =>
            Task.FromResult(CaptionPhrases.Parse(text) is bool on ? module.Run(on) : new CommandResult(Glyph, "Say captions on or captions off", IsError: true));

        public CommandPreview? Preview(string text) => CaptionPhrases.Parse(text) is bool on ? module.Preview(on) : null;
    }

    // ---- Test pipe -----------------------------------------------------------------------------------

    string State()
    {
        string memory = Search.MemoryState.Line();
        if (_session is not { } s) return $"on=false supported={LiveCaptionsReader.IsSupported()} {_lastRun} {memory}";
        var r = s.Reader;
        return string.Create(CultureInfo.InvariantCulture,
            $"on=true source={(r is null ? "fed" : "live_captions")} attached={r?.Attached ?? false} reading={r?.Reading ?? false} " +
            $"window={(r?.WindowState ?? "-").Replace(' ', '_')} started_by_shammy={r?.StartedIt ?? false} reads={r?.Reads ?? 0} changes={r?.Changes ?? 0} " +
            $"updates={s.Updates} fed={s.Fed} strip={(s.Strip.IsVisible ? "visible" : "hidden")} mode={_ctx.Notch.Mode} " +
            $"words={s.Merger.Words.Count + s.Merger.Dropped} last=\"{s.LastText}\" latest=\"{CaptionsFeed.Latest}\" lines=\"{s.Strip.Shown}\" {memory}");
    }

    /// <summary>"captions-feed and that's why": as if Live Captions had shown this, so the merging and the strip can
    /// be watched without any sound. Turns captions on without Live Captions if they were off (any Windows).</summary>
    string Feed(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return "error: usage captions-feed <text>";
        if (_session is null && TurnOn(withReader: false) is { } why) return "error: " + why;
        var s = _session!;
        s.Fed++;
        CaptionsFeed.Set(arg.Trim());
        Heard(s, arg.Trim());
        return State();
    }

    public void Dispose()
    {
        if (_ctx is not null) TurnOff(waitForWindow: true);
    }
}
