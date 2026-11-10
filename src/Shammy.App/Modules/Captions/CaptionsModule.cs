using System.Globalization;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Core.Captions;
using Shammy.Core.Settings;
using Shammy.Search;
using Shammy.Shell;
using Shammy.Views;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Shammy.Modules.Captions;

/// <summary>
/// Live captions for whatever the PC is playing (a lecture, a video, a call), written by the offline voice model
/// Shammy already has, so nothing leaves the PC. While on, the last few seconds of system sound are transcribed
/// every couple of seconds and merged into a two-line strip under the notch (<see cref="CaptionsStrip"/>).
/// The model is shared with voice commands, and commands come first: captions never wait in line for it, stop
/// asking the moment the talk key goes down, and give up a pass that is already running.
/// Everything here (the capture, the worker, the timer, the window) exists only while captions are on.
/// Pipes: "captions-state", "captions-on", "captions-off", "captions-feed &lt;text&gt;" (as if the model had heard
/// it), "captions-file &lt;path&gt;" (a sound file in place of the speakers), "captions-loading on|off" (as if the
/// voice model were not loaded yet).
/// </summary>
internal sealed class CaptionsModule : IShammyModule
{
    public const string ActivityId = "captions.on";

    // Just above music (20) and keep-awake (22): a song steps aside for it, everything else comes first.
    const int Priority = 23;
    const string Glyph = "\uE7F0";
    const string GettingReady = "Getting the voice model ready…", Unavailable = "The voice model couldn’t be loaded",
        Listening = "Listening for speech…";

    /// <summary>How long the strip stays after the last new words.</summary>
    static readonly TimeSpan Linger = TimeSpan.FromSeconds(6);

    /// <summary>How often the worker looks for a chunk, and how often a running pass checks for the talk key.</summary>
    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(150), Watch = TimeSpan.FromMilliseconds(60);

    ModuleContext _ctx = null!;
    Session? _session;
    bool _pretendLoading;
    string _lastRun = "never on";

    public string Name => "captions";

    public bool IsOn => _session is not null;

    /// <summary>Everything one stretch of captions owns; made when they turn on and gone when they turn off.</summary>
    sealed class Session
    {
        public required WhisperService Voice { get; init; }
        public required CaptionsStrip Strip { get; init; }
        public required CaptionLines Lines { get; init; }
        public CaptionAudio Audio { get; } = new();
        public CaptionMerger Merger { get; } = new();
        public CancellationTokenSource Off { get; } = new();
        public CaptionsCapture Capture = null!;
        public DispatcherTimer Timer = null!;

        /// <summary>Watches for the notch changing shape; see <see cref="TurnOn"/>.</summary>
        public EventHandler? NotchMoved;
        public NotchMode ModeSeen;
        public Task Worker = Task.CompletedTask;

        /// <summary>A plain line shown instead of a caption, until words arrive.</summary>
        public string? Message;

        /// <summary>When the strip last had something new to show.</summary>
        public DateTime ShownAt;

        public volatile bool Paused, ModelReady;
        public int Chunks, Fed, Failures;
        public double Seconds;
        public string LastText = "";
    }

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        // Fixed phrases only, and first so "stop captions" is never taken for a music or timer command.
        ctx.AddCommandFirst(new CaptionsCommand(this));
        ctx.AddTrayToggle("Live captions", () => IsOn, on => OnUi(() =>
        {
            if (!on) TurnOff();
            else if (TurnOn() is { } why) ctx.ShowMessage(Glyph, why, error: true);
        }));

        ctx.AddPipeCommand("captions-state", _ => State());
        ctx.AddPipeCommand("captions-on", _ => TurnOn() is { } why ? "error: " + why : "ok " + State());
        ctx.AddPipeCommand("captions-off", _ => TurnOff() ? "ok " + State() : "ok: captions weren't on");
        ctx.AddPipeCommand("captions-feed", Feed);
        ctx.AddPipeCommand("captions-file", PlayFile);
        ctx.AddPipeCommand("captions-loading", arg =>
        {
            _pretendLoading = arg.Trim().ToLowerInvariant() is not ("off" or "false" or "0");
            return "ok loading=" + (_pretendLoading ? "pretend" : "real");
        });
    }

    void OnUi(Action action)
    {
        if (_ctx.Ui.CheckAccess()) action();
        else _ctx.Ui.BeginInvoke(action);
    }

    // ---- On and off ---------------------------------------------------------------------------------

    /// <summary>Turns captions on; null when they are on now, otherwise why they could not start.</summary>
    internal string? TurnOn()
    {
        if (_session is not null) return null;
        if (HostLinks.Voice is not { } voice) return "Captions need the voice model";

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
            Voice = voice,
            Strip = strip,
            Lines = new CaptionLines(CaptionsStrip.LineWidth, strip.Measure),
        };
        s.Capture = new CaptionsCapture(s.Audio, (message, ex) => _ctx.Warn(message, ex));
        if (!s.Capture.Start())
        {
            s.Capture.Dispose();
            Close(strip);
            return "Couldn’t hear what this PC is playing";
        }
        s.Capture.FileEnded += () => _ctx.Ui.BeginInvoke(() =>
        {
            if (ReferenceEquals(_session, s)) s.Capture.Restart();
        });

        _session = s;
        s.ModelReady = Ready(s);
        // A model that failed to load earlier (no internet on first run) gets another try now it is wanted.
        if (!voice.IsReady && voice.Failure is not null && !voice.IsDownloading) _ = voice.InitAsync();
        Say(s, s.ModelReady ? Listening : GettingReady);
        _ctx.Post(new Activity(ActivityId, ActivityKind.Custom, Priority, new CaptionsContent(() => TurnOff())));
        // Once a second: is there room under the notch, and has the strip had nothing new for a while?
        s.Timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(s), _ctx.Ui);
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
        s.Worker = Task.Run(() => WorkAsync(s));
        UpdateStrip(s);
        _ctx.Info($"Captions on ({voice.ActiveModel})");
        return null;
    }

    /// <summary>Stops everything captions run; false if they weren't on.</summary>
    internal bool TurnOff()
    {
        if (_session is not { } s) return false;
        _session = null;
        s.Timer.Stop();
        _ctx.Notch.LayoutUpdated -= s.NotchMoved;
        s.Off.Cancel();
        s.Capture.Dispose();
        Close(s.Strip);
        _ctx.Remove(ActivityId);
        _lastRun = string.Create(CultureInfo.InvariantCulture, $"chunks={s.Chunks} avg={Average(s):0.00} last=\"{s.LastText}\"");
        // When the last pass has let go of the model, free what captions set up on it.
        var voice = s.Voice;
        _ = s.Worker.ContinueWith(_ =>
        {
            try
            {
                voice.ReleaseCaptions();
            }
            catch (Exception ex)
            {
                _ctx.Warn("Freeing the caption model state failed", ex);
            }
            s.Off.Dispose();
        }, TaskScheduler.Default);
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

    // ---- Transcribing (off the UI thread) -------------------------------------------------------------

    /// <summary>
    /// The talk key is down, or Shammy is still working out what was said: the model belongs to the command.
    /// Read from the worker thread; the notch's mode is a plain field, and a stale read only costs one poll.
    /// </summary>
    bool VoiceBusy() => _ctx.Notch.Mode is NotchMode.Listening or NotchMode.Circling or NotchMode.Thinking;

    bool Ready(Session s) => !_pretendLoading && s.Voice.IsReady;

    async Task WorkAsync(Session s)
    {
        var off = s.Off.Token;
        bool warmed = false;
        long restUntil = 0;
        try
        {
            while (!off.IsCancellationRequested)
            {
                await Task.Delay(Poll, off).ConfigureAwait(false);
                if (s.Capture.LookIn())
                {
                    _ = _ctx.Ui.BeginInvoke(() =>
                    {
                        if (ReferenceEquals(_session, s)) s.Capture.Restart();
                    });
                }
                bool busy = VoiceBusy();
                s.Paused = busy;
                if (busy) continue;
                bool ready = Ready(s);
                s.ModelReady = ready;
                if (!ready) continue;
                if (!warmed)
                {
                    // The first pass sets the model up for caption-sized windows, which is slow on a graphics
                    // card: spend it on two seconds of quiet rather than on the first words.
                    warmed = await DecodeAsync(s, new float[CaptionAudio.SampleRate * 2], off).ConfigureAwait(false) is not null;
                    continue;
                }
                if (Stopwatch.GetTimestamp() < restUntil || !s.Audio.TryTake(out var chunk)) continue;

                var clock = Stopwatch.StartNew();
                var text = await DecodeAsync(s, chunk.Samples, off).ConfigureAwait(false);
                if (text is null)
                {
                    // The model was busy, or the pass was given up for a command: the same sound is tried again.
                    s.Audio.Undo();
                    continue;
                }
                double seconds = clock.Elapsed.TotalSeconds;
                // A PC that needs longer than a second for a chunk rests as long again before the next one, so
                // captions fall a little further behind instead of keeping it flat out.
                restUntil = seconds > 1 ? Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency) : 0;
                _ = _ctx.Ui.BeginInvoke(() => Heard(s, text, chunk.Overlaps, seconds));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _ctx.Warn("Captions stopped: transcribing kept failing", ex);
            _ = _ctx.Ui.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_session, s)) return;
                TurnOff();
                _ctx.ShowMessage(Glyph, "Captions stopped · the voice model had a problem", error: true, seconds: 4);
            });
        }
    }

    /// <summary>One pass over a chunk; null when it was not transcribed (model busy, or given up for a command).</summary>
    async Task<string?> DecodeAsync(Session s, float[] samples, CancellationToken off)
    {
        using var mine = CancellationTokenSource.CreateLinkedTokenSource(off);
        try
        {
            var pass = s.Voice.TranscribeCaptionAsync(samples, mine.Token);
            // The talk key went down mid-pass: give the model up now, a command must not wait behind a caption.
            while (await Task.WhenAny(pass, Task.Delay(Watch)).ConfigureAwait(false) != pass)
            {
                if (!VoiceBusy() || mine.IsCancellationRequested) continue;
                s.Paused = true;
                mine.Cancel();
            }
            var text = await pass.ConfigureAwait(false);
            s.Failures = 0;
            return text;
        }
        catch (Exception ex)
        {
            off.ThrowIfCancellationRequested();
            if (ex is OperationCanceledException || mine.IsCancellationRequested) return null;
            if (++s.Failures == 1) _ctx.Warn("A caption chunk failed", ex);
            if (s.Failures >= 5) throw;
            await Task.Delay(1000, off).ConfigureAwait(false);
            return null;
        }
    }

    // ---- Showing (UI thread) -------------------------------------------------------------------------

    /// <summary>The model heard this in the newest window: merge it into the caption and show it.</summary>
    void Heard(Session s, string text, bool overlaps, double seconds)
    {
        if (!ReferenceEquals(_session, s)) return;
        s.Chunks++;
        s.Seconds += seconds;
        s.LastText = text;
        var merge = s.Merger.Merge(text, overlaps);
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
        bool ready = Ready(s);
        if (ready)
        {
            if (s.Message is GettingReady or Unavailable) Say(s, Listening);
        }
        else if (s.Merger.Words.Count == 0)
        {
            // Still loading or downloading keeps the strip up; a model that failed says so once and steps back.
            var waiting = s.Voice.Failure is not null && !s.Voice.IsDownloading && !_pretendLoading ? Unavailable : GettingReady;
            if (s.Message != waiting) Say(s, waiting);
        }
        UpdateStrip(s);
    }

    /// <summary>
    /// The strip shows only while the notch is at rest or showing its compact pill (anything taller would sit on
    /// top of it), and only while it has had something new in the last few seconds or is waiting for the model.
    /// </summary>
    void UpdateStrip(Session s)
    {
        try
        {
            var mode = _ctx.Notch.Mode;
            bool room = mode is NotchMode.Hidden or NotchMode.Idle or NotchMode.Compact;
            bool something = s.Message == GettingReady || DateTime.UtcNow - s.ShownAt < Linger;
            if (room && something)
            {
                s.Strip.LanguageName = CaptionText.LanguageName(s.Voice.HinglishActive, s.Voice.Language);
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

    /// <summary>What captions cost while on: the graphics card where the voice model runs on one, else the processor.</summary>
    static string Cost()
    {
        var model = HostLinks.Voice?.ActiveModel ?? "";
        return model.Contains("(CPU)", StringComparison.Ordinal) && !model.Contains("GPU", StringComparison.Ordinal)
            ? "Made on this PC · uses the processor while on"
            : "Made on this PC · uses the graphics card while on";
    }

    CommandResult Run(bool on)
    {
        if (!on)
        {
            return TurnOff()
                ? new CommandResult(Glyph, "Live captions are off")
                : new CommandResult(Glyph, "Captions weren’t on");
        }
        if (IsOn) return new CommandResult(Glyph, "Captions are already on", "Say “captions off” to stop them");
        if (TurnOn() is { } why) return new CommandResult(Glyph, why, IsError: true);
        return new CommandResult(Glyph, "Live captions are on",
            _session is { ModelReady: false } ? GettingReady : "They show under the notch when someone speaks");
    }

    CommandPreview Preview(bool on)
    {
        if (!on) return new CommandPreview(Glyph, "Turn off live captions", IsOn ? null : "They aren’t on");
        return IsOn
            ? new CommandPreview(Glyph, "Live captions are on", "Say “captions off” to stop them")
            : new CommandPreview(Glyph, "Turn on live captions", Cost());
    }

    sealed class CaptionsCommand(CaptionsModule module) : ITextCommand, ICommandPreview
    {
        public bool CanHandle(string text) => CaptionPhrases.Parse(text) is not null;

        public Task<CommandResult> RunAsync(string text) =>
            Task.FromResult(CaptionPhrases.Parse(text) is bool on ? module.Run(on) : new CommandResult(Glyph, "Say captions on or captions off", IsError: true));

        public CommandPreview? Preview(string text) => CaptionPhrases.Parse(text) is bool on ? module.Preview(on) : null;
    }

    // ---- Test pipe -----------------------------------------------------------------------------------

    /// <summary>Seconds the model took per chunk, over the chunks it really transcribed (fed text is not timed).</summary>
    static double Average(Session s) => s.Chunks > s.Fed ? s.Seconds / (s.Chunks - s.Fed) : 0;

    string State()
    {
        if (_session is not { } s) return $"on=false {_lastRun}";
        string model = Ready(s) ? "ready:" + s.Voice.ActiveModel.Replace(' ', '_') : s.Voice.Failure is not null ? "failed" : "loading";
        return string.Create(CultureInfo.InvariantCulture,
            $"on=true model={model} paused={s.Paused} source={(s.Capture.FromFile ? "file" : "speakers")} listening={s.Capture.IsListening} " +
            $"heard={s.Audio.Total / (double)CaptionAudio.SampleRate:0.0}s chunks={s.Chunks} fed={s.Fed} avg={Average(s):0.00} " +
            $"strip={(s.Strip.IsVisible ? "visible" : "hidden")} mode={_ctx.Notch.Mode} language={s.Strip.LanguageName.Replace(' ', '_')} " +
            $"words={s.Merger.Words.Count + s.Merger.Dropped} last=\"{s.LastText}\" lines=\"{s.Strip.Shown}\"");
    }

    /// <summary>"captions-feed and that's why": as if the model had heard this in the next window, so the merging
    /// and the strip can be watched without any sound.</summary>
    string Feed(string arg)
    {
        if (_session is not { } s) return "error: captions are off (send captions-on first)";
        if (string.IsNullOrWhiteSpace(arg)) return "error: usage captions-feed <text>";
        s.Fed++;
        Heard(s, arg.Trim(), overlaps: true, seconds: 0);
        return State();
    }

    /// <summary>"captions-file C:\clip.wav": plays the file through the real capture path in place of the
    /// speakers (turning captions on first if needed), at its real speed.</summary>
    string PlayFile(string arg)
    {
        var path = arg.Trim().Trim('"');
        if (path.Length == 0) return "error: usage captions-file <path to a wav>";
        if (!File.Exists(path)) return "error: no such file";
        if (TurnOn() is { } why) return "error: " + why;
        try
        {
            var length = _session!.Capture.PlayFile(path);
            return string.Create(CultureInfo.InvariantCulture, $"ok playing {length.TotalSeconds:0.0}s {State()}");
        }
        catch (Exception ex)
        {
            _ctx.Warn("Opening the caption test file failed", ex);
            return "error: couldn't read that file (" + ex.GetType().Name + ")";
        }
    }

    public void Dispose()
    {
        if (_ctx is not null) TurnOff();
    }
}
