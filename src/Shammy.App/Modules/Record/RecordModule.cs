using System.Globalization;
using System.Windows.Interop;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Core.Record;
using Shammy.Infrastructure;
using Windows.Graphics.Capture;

namespace Shammy.Modules.Record;

/// <summary>
/// Screen recording from the notch. "record my screen" (or the tray) drops a start card: the whole screen the notch
/// is on or a window picked in Windows' own picker, PC sound on and the microphone off unless changed, and Start.
/// While it records the notch keeps itself out of the video, message text stays hidden (as for any screen capture),
/// and the pet wears its ninja mask with the running time and a red Stop circle. Stopping (the circle, "stop
/// recording", the tray) finishes the MP4 in Videos › Shammy and shows its card. It stops by itself at 60 minutes,
/// when the disk is nearly full, or when the window or screen goes away, keeping what was recorded.
/// Pipes: "record-state", "record-open", "record-start screen [sound=on|off] [mic=on|off] [seconds=N] [area=X,Y,W,H]",
/// "record-stop", "record-fake limit|disk|closed|failed" (ends the running recording as if that had happened;
/// "failed" shows the failure card without one).
/// </summary>
internal sealed class RecordModule : IShammyModule
{
    public const string ActivityId = "record.live";

    // Just above a screen share (66) and a call (65): your own recording is the thing to be able to stop.
    const int Priority = 67;
    const string RecordGlyph = "", StopGlyph = "";
    static readonly TimeSpan SavedCardStays = TimeSpan.FromSeconds(15), FailedCardStays = TimeSpan.FromSeconds(10);

    enum Phase { Idle, Starting, Recording, Saving }

    readonly DispatcherTimer _guard = new(DispatcherPriority.Background), _close = new(DispatcherPriority.Background);
    ModuleContext _ctx = null!;
    ScreenRecorder _recorder = null!;
    RecordContent? _content;
    RecordStartCard? _startCard;
    System.Windows.FrameworkElement? _resultCard;
    Phase _phase = Phase.Idle;
    RecordChoice _choice = RecordChoice.WholeScreen;
    bool _sound = true, _mic;
    string _source = "-", _last = "-";
    RecordResult? _lastResult;

    public string Name => "record";

    // ---- For Stage ---------------------------------------------------------------------------------

    /// <summary>The running module, for Stage's Record control (null before start-up).</summary>
    internal static RecordModule? Current { get; private set; }

    /// <summary>
    /// Set by Stage while it is open: the notch stays out of captures when a recording ends, and the saved (or failed)
    /// card waits until Stage closes instead of taking its place (see <see cref="ShowHeldCard"/>).
    /// </summary>
    internal static Func<bool>? HoldCards { get; set; }

    Action? _heldCard;

    internal bool IsRecording => _phase is Phase.Starting or Phase.Recording;
    internal bool IsSaving => _phase == Phase.Saving;
    internal TimeSpan Elapsed => _recorder.Elapsed;

    /// <summary>Starts recording the notch's whole screen with PC sound and the microphone, as the start card would.</summary>
    internal Task<string?> StartScreenWithMic()
    {
        CloseResultCard();
        return BeginAsync("screen", sound: true, mic: true, monitor: _ctx.Notch.Monitor);
    }

    /// <summary>Stops the running recording (the file is finished and its card follows). False when nothing is recording.</summary>
    internal bool StopRecording() => Stop();

    /// <summary>Shows the card a recording left while Stage was open, if any.</summary>
    internal void ShowHeldCard()
    {
        var held = _heldCard;
        _heldCard = null;
        held?.Invoke();
    }

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        Current = this;
        _recorder = new ScreenRecorder(ctx.Warn);
        ctx.AddGlance("record", () => _phase == Phase.Recording
            ? new GlanceItem("Recording · " + RecordText.Elapsed(_recorder.Elapsed), NotchPalette.Alarm, 90) : null);
        _recorder.Finished += result => ctx.Ui.BeginInvoke(() => OnFinished(result));

        // A screenshot taken meanwhile lets the notch back into captures when it is done; put that right.
        _guard.Interval = TimeSpan.FromSeconds(2);
        _guard.Tick += (_, _) =>
        {
            if (_phase is Phase.Recording or Phase.Saving) _ctx.Notch.ExcludeFromCapture(true);
            else _guard.Stop();
        };
        _close.Tick += (_, _) => CloseResultCard();

        // Fixed phrases only, ahead of broader matchers ("stop …", "start …").
        ctx.AddCommandFirst(new RecordCommand(this));
        ctx.AddTrayItem("Record the screen", OpenStartCard, () => _phase == Phase.Idle);
        ctx.AddTrayItem("Stop recording", () => Stop(), () => _phase == Phase.Recording);

        ctx.AddPipeCommand("record-state", _ => State());
        ctx.AddPipeCommand("record-open", _ =>
        {
            OpenStartCard();
            return State();
        });
        ctx.AddPipeCommand("record-start", StartFromPipe);
        ctx.AddPipeCommand("record-stop", _ => Stop() ? "stopping" : "error: nothing is recording");
        ctx.AddPipeCommand("record-fake", Fake);
    }

    public void Dispose()
    {
        _guard.Stop();
        _close.Stop();
        // Finishes the file if the app closes mid-recording.
        _recorder?.Dispose();
        PrivacyState.SetRecording(false);
    }

    // ---- The start card --------------------------------------------------------------------------

    /// <summary>Drops the start card, with the usual choices: whole screen, PC sound on, microphone off.</summary>
    void OpenStartCard()
    {
        if (_phase != Phase.Idle)
        {
            _ctx.ShowMessage(RecordGlyph, _phase == Phase.Saving ? "Saving the recording" : "Already recording");
            return;
        }
        CloseResultCard();
        _choice = RecordChoice.WholeScreen;
        _sound = true;
        _mic = false;
        _startCard = new RecordStartCard(_choice, _sound, _mic, choice => _choice = choice, on => _sound = on, on => _mic = on, StartFromCard, CloseStartCard);
        _ctx.ShowCard(_startCard, RecordStartCard.CardWidth, RecordStartCard.CardHeight, interactive: true);
    }

    void CloseStartCard()
    {
        if (_startCard is null) return;
        // Only if it's still ours on screen (clicking elsewhere already closed it, or another card took its place).
        if (_startCard.Parent is not null) _ctx.CloseCard();
        _startCard = null;
    }

    async void StartFromCard()
    {
        try
        {
            var choice = _choice;
            bool sound = _sound, mic = _mic;
            CloseStartCard();
            if (choice == RecordChoice.WholeScreen)
            {
                await BeginAsync("screen", sound, mic, monitor: _ctx.Notch.Monitor);
                return;
            }
            var item = await PickWindowAsync();
            // Cancelled: nothing happens, like closing the card.
            if (item is not null) await BeginAsync("window", sound, mic, item: item);
        }
        catch (Exception ex)
        {
            _ctx.Warn("Starting a recording from the card failed", ex);
            Reset();
            ShowFailed(RecordFailure.Unknown);
        }
    }

    /// <summary>Windows' own "choose what to share" picker, owned by the notch window. Null when cancelled or unavailable.</summary>
    async Task<GraphicsCaptureItem?> PickWindowAsync()
    {
        try
        {
            var picker = new GraphicsCapturePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, new WindowInteropHelper(_ctx.Notch).Handle);
            return await picker.PickSingleItemAsync();
        }
        catch (Exception ex)
        {
            _ctx.Warn("Windows' window picker failed", ex);
            ShowFailed(RecordFailure.NoSource);
            return null;
        }
    }

    // ---- Recording -------------------------------------------------------------------------------

    /// <summary>Starts recording now. Returns null when it is running, or the failure's code (already shown on a card).</summary>
    async Task<string?> BeginAsync(string source, bool sound, bool mic, IntPtr monitor = default, GraphicsCaptureItem? item = null,
        TimeSpan? runFor = null, (int X, int Y, int Width, int Height)? area = null)
    {
        if (_phase != Phase.Idle) return RecordFailure.Busy;
        _phase = Phase.Starting;
        _source = source;
        _last = "starting";
        string? failure;
        try
        {
            var folder = RecordNaming.Folder(VideosFolder());
            var options = new RecordOptions
            {
                Path = RecordNaming.FreePath(folder, DateTime.Now, File.Exists),
                Monitor = monitor,
                Item = item,
                Sound = sound,
                Mic = mic,
                RunFor = runFor,
                Area = area,
            };
            // From before the first frame: the notch is not in the video, and message text is hidden as for any capture.
            _ctx.Notch.ExcludeFromCapture(true);
            PrivacyState.SetRecording(true);
            failure = await _recorder.StartAsync(options);
        }
        catch (Exception ex)
        {
            _ctx.Warn("Starting the recording failed", ex);
            failure = RecordFailure.Unknown;
        }

        if (failure is not null)
        {
            _last = "failed to start: " + failure;
            // Busy means another start is already under way and owns the state.
            if (failure != RecordFailure.Busy) Reset();
            ShowFailed(failure);
            return failure;
        }
        // It may have ended already (the window closed at once): OnFinished has tidied up then.
        if (_phase != Phase.Starting) return null;
        _phase = Phase.Recording;
        _last = "recording";
        _content = new RecordContent(() => _recorder.Elapsed, () => Stop());
        _ctx.Post(new Activity(ActivityId, ActivityKind.Custom, Priority, _content));
        _guard.Start();
        _ctx.Info($"Recording the {source} (sound {(sound ? "on" : "off")}, mic {(mic ? "on" : "off")})");
        return null;
    }

    /// <summary>Stops the running recording; the saved card follows once the file is finished. False when nothing is recording.</summary>
    bool Stop(RecordStop reason = RecordStop.User)
    {
        if (_phase != Phase.Recording) return false;
        _phase = Phase.Saving;
        _last = "saving";
        if (_content is not null) _content.Saving = true;
        _ = _recorder.StopAsync(reason);
        return true;
    }

    /// <summary>Every recording that started ends here, whatever stopped it. On the UI thread.</summary>
    void OnFinished(RecordResult result)
    {
        Reset();
        _lastResult = result;
        _last = result.ToString();
        _ctx.Info("Recording finished: " + result);
        if (result.Saved && RecordText.StoppedBecause(result.Reason) is { } why) _ctx.Info(why);
        // After a "stop recording" confirmation has had its moment, not behind it.
        _ctx.Notch.AfterSearch(() =>
        {
            if (result.Saved) ShowSaved(result);
            else ShowFailed(result.Failure);
        });
    }

    /// <summary>Back to idle: the notch may be captured again, privacy mode is released, the pet lets go.</summary>
    void Reset()
    {
        _phase = Phase.Idle;
        _guard.Stop();
        // Stage hides the notch from captures for its own reasons; it stays hidden while Stage is open.
        _ctx.Notch.ExcludeFromCapture(HoldCards?.Invoke() == true);
        PrivacyState.SetRecording(false);
        _ctx.Remove(ActivityId);
        _content = null;
    }

    static string VideosFolder()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        return string.IsNullOrEmpty(videos) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos") : videos;
    }

    // ---- After it ends ---------------------------------------------------------------------------

    void ShowSaved(RecordResult result)
    {
        var path = result.Path!;
        var card = new RecordSavedCard(result, new RecordSavedCard.Actions(
            Open: () =>
            {
                if (!File.Exists(path)) return Gone();
                CloseResultCard();
                return _ctx.Open(path);
            },
            ShowInFolder: () =>
            {
                if (!File.Exists(path)) return Gone();
                CloseResultCard();
                return _ctx.Open(Interop.Native.ExplorerPath, $"/select,\"{path}\"");
            },
            SendToPhone: Connect.Links.Phone is { } phone
                ? () =>
                {
                    if (!File.Exists(path)) return Gone();
                    CloseResultCard();
                    _ = phone.SendAsync(new[] { path });
                    return true;
                }
                : null,
            AddToShelf: () =>
            {
                if (!File.Exists(path)) return Gone();
                _ctx.Services.Shelf.Add(new[] { path });
                return true;
            },
            Delete: () => Delete(path),
            Close: CloseResultCard));
        ShowResultCard(card, RecordSavedCard.CardWidth, RecordSavedCard.CardHeight, SavedCardStays);
    }

    void ShowFailed(string? failure)
    {
        CloseStartCard();
        ShowResultCard(new RecordFailedCard(failure, CloseResultCard), RecordFailedCard.CardWidth, RecordFailedCard.CardHeight, FailedCardStays);
    }

    /// <summary>A card that never takes focus (you may be typing) and folds away by itself; resting the pointer on it keeps it.</summary>
    void ShowResultCard(System.Windows.FrameworkElement card, double width, double height, TimeSpan stays)
    {
        if (HoldCards?.Invoke() == true)
        {
            // Stage is open: a short word now, the card once Stage closes.
            _heldCard = () => ShowResultCard(card, width, height, stays);
            _ctx.ShowMessage(RecordGlyph, card is RecordSavedCard ? "Recording saved to Videos › Shammy" : "The recording didn’t work", error: card is not RecordSavedCard);
            return;
        }
        _heldCard = null;
        CloseResultCard();
        _resultCard = card;
        card.MouseEnter += (_, _) => _close.Stop();
        card.MouseLeave += (_, _) =>
        {
            if (ReferenceEquals(_resultCard, card) && card.Parent is not null) _close.Start();
        };
        _ctx.ShowCard(card, width, height, interactive: false);
        _close.Stop();
        _close.Interval = stays;
        _close.Start();
    }

    void CloseResultCard()
    {
        _close.Stop();
        // Only if it's still ours on screen (an alarm may have replaced it).
        if (_resultCard?.Parent is not null) _ctx.CloseCard();
        _resultCard = null;
    }

    bool Gone()
    {
        CloseResultCard();
        _ctx.ShowMessage(RecordGlyph, "That video is gone", error: true);
        return false;
    }

    /// <summary>To the Recycle Bin, never straight to nothing: an hour of recording is one wrong click away.</summary>
    bool Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
        }
        catch (Exception ex)
        {
            _ctx.Warn("Deleting the recording failed", ex);
            _ctx.ShowMessage(RecordGlyph, "Couldn’t delete it", error: true);
            return false;
        }
        var shelved = _ctx.Services.Shelf.Items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (shelved is not null) _ctx.Services.Shelf.Remove(shelved);
        CloseResultCard();
        _ctx.ShowMessage("", "Moved to the Recycle Bin", seconds: 2.5);
        return true;
    }

    // ---- Typed and spoken commands ---------------------------------------------------------------

    CommandResult Run(RecordIntent? intent)
    {
        switch (intent)
        {
            case RecordIntent.Stop:
                if (_phase == Phase.Saving) return new CommandResult(StopGlyph, "Saving the recording");
                return Stop()
                    ? new CommandResult(StopGlyph, "Recording stopped", "Saving it to Videos › Shammy")
                    : new CommandResult(StopGlyph, "Nothing is recording");
            case RecordIntent.Open:
                if (_phase is Phase.Recording or Phase.Starting)
                    return new CommandResult(RecordGlyph, "Already recording", "Say “stop recording” to finish");
                if (_phase == Phase.Saving) return new CommandResult(RecordGlyph, "Saving the last recording", "Try again in a moment");
                // The card takes the notch once this confirmation has had its moment.
                _ctx.Ui.BeginInvoke(() => _ctx.Notch.AfterSearch(OpenStartCard), DispatcherPriority.Background);
                return new CommandResult(RecordGlyph, RecordText.Title, "Choose what to record");
            default:
                return new CommandResult(RecordGlyph, "Say record my screen or stop recording", IsError: true);
        }
    }

    CommandPreview? Preview(RecordIntent? intent) => intent switch
    {
        RecordIntent.Stop => new CommandPreview(StopGlyph, "Stop recording",
            _phase == Phase.Recording ? RecordText.Elapsed(_recorder.Elapsed) + " so far" : "Nothing is recording"),
        RecordIntent.Open => _phase == Phase.Idle
            ? new CommandPreview(RecordGlyph, RecordText.Title, "The whole screen or a window, saved to Videos")
            : new CommandPreview(RecordGlyph, "Already recording", RecordText.Elapsed(_recorder.Elapsed) + " so far"),
        _ => null,
    };

    sealed class RecordCommand(RecordModule module) : ITextCommand, ICommandPreview
    {
        public bool CanHandle(string text) => RecordPhrases.Parse(text) is not null;

        public Task<CommandResult> RunAsync(string text) => Task.FromResult(module.Run(RecordPhrases.Parse(text)));

        public CommandPreview? Preview(string text) => module.Preview(RecordPhrases.Parse(text));
    }

    // ---- Test pipe -------------------------------------------------------------------------------

    string StartFromPipe(string arg)
    {
        if (RecordPipe.ParseStart(arg, out var error) is not { } start) return error ?? RecordPipe.Usage;
        if (_phase != Phase.Idle) return "error: busy (" + _phase.ToString().ToLowerInvariant() + ")";
        var monitor = _ctx.Notch.Monitor;
        var area = start.Area;
        if (area is { } asked)
        {
            // In the screen's own pixels, from its top-left corner; kept on the screen with even sides.
            var screen = Interop.Native.MonitorBounds(monitor);
            area = RecordMaths.Area(asked.X, asked.Y, asked.Width, asked.Height, screen.Width, screen.Height);
            if (area is null) return $"error: that area is too small or off the {screen.Width}x{screen.Height} screen";
        }
        CloseStartCard();
        CloseResultCard();
        _ = BeginAsync(area is null ? "screen" : "area", start.Sound, start.Mic, monitor: monitor,
            runFor: start.Seconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, area: area);
        return "starting (see record-state)";
    }

    /// <summary>Things that are hard to make happen: an hour passing, a full disk, the window closing, a broken save.</summary>
    string Fake(string arg)
    {
        switch (arg.Trim().ToLowerInvariant())
        {
            case "limit": return Stop(RecordStop.TimeLimit) ? "stopping as if 60 minutes had passed" : "error: nothing is recording";
            case "disk": return Stop(RecordStop.DiskLow) ? "stopping as if the disk were nearly full" : "error: nothing is recording";
            case "closed": return Stop(RecordStop.SourceClosed) ? "stopping as if the window had closed" : "error: nothing is recording";
            case "failed":
                if (_phase != Phase.Idle) return "error: busy (stop the recording first)";
                ShowFailed(RecordFailure.Encoder);
                return "showing the failure card";
            default: return "error: usage record-fake limit|disk|closed|failed";
        }
    }

    string State()
    {
        string card = _startCard is { IsVisible: true } ? $"start \"{_startCard.DescribeState()}\" choice={_choice}"
            : _resultCard is { IsVisible: true } ? (_resultCard is RecordSavedCard ? "saved" : "failed")
            : "none";
        string file = _lastResult?.Path is { } path ? Path.GetFileName(path) : "-";
        return string.Create(CultureInfo.InvariantCulture,
            $"phase={_phase.ToString().ToLowerInvariant()} elapsed={RecordText.Elapsed(_recorder.Elapsed)} seconds={_recorder.Elapsed.TotalSeconds:0.0} " +
            $"source={_source} activity={(_ctx.Get(ActivityId) is null ? "hidden" : "shown")} presenting={PrivacyState.Presenting} " +
            $"card={card} file=\"{file}\" last=\"{_last}\"");
    }
}
