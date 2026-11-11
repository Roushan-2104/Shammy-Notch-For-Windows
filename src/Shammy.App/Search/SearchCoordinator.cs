using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shammy.Core.Search;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Shell;

namespace Shammy.Search;

/// <summary>
/// The hotkey flows:
/// <list type="bullet">
/// <item>Right Alt tap (under 180 ms): a type-to-search box drops from the notch;</item>
/// <item>Right Alt hold: the mic listens (the screen stays live); release to run the command or search;</item>
/// <item>Copilot key / lone Right Ctrl: the screen freezes, the mic listens, and you can circle anything;
/// release to search, or release without speaking or circling to circle at leisure.</item>
/// </list>
/// Results come from Google AI Mode, the plain Web tab, or Lens for images, rendered in the notch.
/// </summary>
internal sealed class SearchCoordinator
{
    enum Phase { Idle, Pending, Holding, Circling, Processing, Typing, Showing, Message }

    static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(180);
    static readonly TimeSpan ReleaseTail = TimeSpan.FromMilliseconds(250);

    readonly NotchWindow _notch;
    readonly GlobalKeys _keys;
    readonly WhisperService _whisper;
    readonly AppSettings _settings;
    readonly Modules.ModuleRegistry _modules;
    readonly MicRecorder _mic = new();
    readonly DispatcherTimer _holdTimer;

    Phase _phase;
    int _session;
    bool _micOk;
    bool _listenOnline; // this hold's recording goes online (the listening view said so)
    string? _micError;
    ScreenShot? _shot;
    SearchOverlay? _overlay;
    Task<IReadOnlyList<OcrWord>>? _ocr;
    Context? _ctx;
    CancellationTokenSource? _transcribe;
    HotKey _key;

    sealed record Context(string Query, string DisplayQuery, SearchMode Mode, SearchFilter Filters, byte[]? Png, BitmapSource? Thumb)
    {
        public Modules.SelectionInfo? Selection { get; init; }

        /// <summary>A page to show instead of a search (a command's translation); a mode or filter chip searches as usual.</summary>
        public Uri? Page { get; init; }
    }

    public SearchCoordinator(NotchWindow notch, GlobalKeys keys, WhisperService whisper, AppSettings settings, Modules.ModuleRegistry modules)
    {
        _notch = notch;
        _keys = keys;
        _whisper = whisper;
        _settings = settings;
        _modules = modules;
        _holdTimer = new DispatcherTimer(HoldThreshold, DispatcherPriority.Input, (_, _) => OnHoldElapsed(), notch.Dispatcher) { IsEnabled = false };

        keys.Pressed += OnKeyDown;
        keys.Released += OnKeyUp;
        keys.Aborted += key =>
        {
            // AltGr was used to type a character: quietly undo the listening it had started.
            if (key == _key && _phase is Phase.Pending or Phase.Holding) Cancel();
        };
        keys.EscapePressed += Cancel;
        notch.InteractiveDeactivated += () =>
        {
            if (_phase is Phase.Showing or Phase.Typing) Cancel();
        };
        notch.SearchBarView.Submitted += text => _ = ProcessTextAsync(text);
        // Palette rows (Modules/Palette): the web row skips commands; rows that acted themselves show a confirmation.
        notch.SearchBarView.WebSearchRequested += text =>
        {
            if (_phase == Phase.Typing) _ = SearchWebAsync(text);
        };
        notch.SearchBarView.ActionCompleted += result =>
        {
            if (_phase != Phase.Typing) return;
            if (result is null) Cancel();
            else ShowCommandResult(result);
        };
        notch.ResultView.CloseRequested += Cancel;
        notch.ResultView.ModeChanged += mode =>
        {
            if (_ctx is null) return;
            // An image with no words in or with it has nothing to search as text ("Selected area" isn't a query).
            if (mode != SearchMode.Lens && _ctx.Query.Length == 0)
            {
                notch.ResultView.SetModeChips(_ctx.Mode);
                return;
            }
            _ctx = _ctx with { Mode = mode, Page = null };
            _ = NavigateAsync(_ctx);
        };
        notch.ResultView.FiltersChanged += filters =>
        {
            if (_ctx is null) return;
            _ctx = _ctx with { Filters = filters, Mode = _ctx.Mode == SearchMode.Lens ? _settings.DefaultSearchMode : _ctx.Mode, Page = null };
            _ = NavigateAsync(_ctx);
        };
        notch.AnswerView.SearchWebRequested += query => _ = ShowResultAsync(new Context(query, query, _settings.DefaultSearchMode, SearchFilter.None, null, null));
    }

    public bool IsActive => _phase != Phase.Idle;

    // ---- Hotkey ------------------------------------------------------------------------------

    /// <summary>
    /// Talk (Right Alt): hold to speak, tap to type; the screen stays live, so commands like "play …" or
    /// "5 ft in cm" don't freeze anything. Look (Copilot key / lone Right Ctrl): freeze straight away to
    /// circle, and speak at the same time if you like; a quick tap leaves the screen frozen to circle.
    /// </summary>
    public void OnKeyDown(HotKey key)
    {
        if (_phase == Phase.Processing) return;
        // Start collapsing an open card right away so it's out of the way before the screen freezes.
        if (_phase != Phase.Idle) Reset(keepNotch: false);

        _session++;
        _key = key;
        _phase = Phase.Pending;
        _keys.InterceptEscape = true;
        _micOk = _mic.Start(out _micError);
        if (key == HotKey.Look) OnHoldElapsed();
        else _holdTimer.Start();
    }

    void OnHoldElapsed()
    {
        _holdTimer.Stop();
        if (_phase != Phase.Pending) return;
        _phase = Phase.Holding;

        _notch.SetSearchMode(NotchMode.Listening);
        // Online listening (the user's own key): the view says so, because the recording leaves the PC.
        bool online = _listenOnline = _micOk && Modules.HostLinks.OnlineListening?.Invoke() == true;
        string listening = online ? "Listening… · online" : "Listening…";
        if (_key == HotKey.Talk)
        {
            // Just talking: no freeze, nothing to circle.
            if (_micOk)
            {
                var talkHint = _whisper.IsReady || online ? "Speak, then let go"
                    : _whisper.IsDownloading ? $"Voice model downloading ({_whisper.DownloadProgress:P0}) · let go to type"
                    : "Voice unavailable · let go to type";
                _notch.SearchBarView.ShowListening(() => _mic.Level, listening, talkHint);
            }
            else
            {
                _notch.SearchBarView.ShowMessage("", "Microphone unavailable", $"{_micError} · let go to type");
            }
            return;
        }
        if (_micOk)
        {
            var hint = _whisper.IsReady || online ? "Speak, circle anything, then let go"
                : _whisper.IsDownloading ? $"Voice model downloading ({_whisper.DownloadProgress:P0}) · circle instead"
                : "Voice unavailable · circle anything instead";
            _notch.SearchBarView.ShowListening(() => _mic.Level, listening, hint);
        }
        else
        {
            _notch.SearchBarView.ShowCircling();
            _notch.SearchBarView.ShowMessage("", "Circle anything", $"{_micError} · voice is off");
        }
        BeginFreeze();
    }

    public void OnKeyUp(HotKey key)
    {
        // Letting go of the other hotkey (e.g. a Right Ctrl shortcut while talking) changes nothing.
        if (key != _key) return;
        if (_phase == Phase.Pending)
        {
            _holdTimer.Stop();
            _mic.Stop();
            ShowTyping();
            return;
        }
        if (_phase != Phase.Holding) return;
        _ = FinishHoldAsync();
    }

    /// <summary>
    /// People let go of the key on the last syllable, so keep listening a moment longer before stopping:
    /// cutting the mic at the key-up was clipping last words ("…call mom" → "…call").
    /// </summary>
    async Task FinishHoldAsync()
    {
        int session = _session;
        _phase = Phase.Processing;
        if (_micOk) await Task.Delay(ReleaseTail);
        if (session != _session) return;

        var samples = _mic.Stop();
        bool silentBlock = _micOk && MicRecorder.IsSilentBlock(samples);
        bool speech = _micOk && !silentBlock && MicRecorder.HasSpeech(samples);
        bool circled = _overlay?.HasSelection == true;

        if (!speech && !circled)
        {
            if (_overlay is null)
            {
                // Nothing to circle on (capture failed) and nothing said: fall back to typing.
                Reset(keepNotch: true);
                ShowTyping();
                return;
            }
            _phase = Phase.Circling;
            _notch.SetSearchMode(NotchMode.Circling);
            _notch.SearchBarView.ShowCircling();
            if (silentBlock)
                _notch.SearchBarView.ShowMessage("", "Microphone is blocked", "Allow desktop apps in Settings › Privacy › Microphone");
            _overlay.SetHint("Circle anything to search · Esc to cancel");
            return;
        }

        _ = ProcessAsync(speech ? samples : null, circled ? _overlay!.SelectionPixels : null);
    }

    void BeginFreeze()
    {
        try
        {
            _notch.ExcludeFromCapture(true);
            _shot = ScreenCapture.CaptureMonitorAtCursor();
        }
        catch (Exception ex)
        {
            Log.Error("Screen capture failed", ex);
            _shot = null;
            return;
        }
        finally
        {
            _notch.ExcludeFromCapture(false);
        }
        _ocr = OcrService.RecognizeAsync(_shot);
        _overlay = new SearchOverlay(_shot);
        _overlay.SetHint("Keep holding · speak or circle · let go to search");
        _overlay.SelectionMade += OnSelectionMade;
        _overlay.Cancelled += Cancel;
        _overlay.FullScreenshotRequested += () => SaveFromFreeze(null);
        _overlay.SnipMade += region => SaveFromFreeze(region);
        var shownAt = Environment.TickCount64;
        _overlay.AppearedOnScreen += () =>
        {
            _notch.BringToFront();
            Log.Info($"Freeze overlay on screen after {Environment.TickCount64 - shownAt} ms");
        };
        _overlay.Show();
    }

    /// <summary>The overlay's Screenshot / Snip tools: save what's frozen and end the search.</summary>
    void SaveFromFreeze(Box? region)
    {
        var shot = _shot;
        if (shot is null) return;
        var image = region is { } r ? shot.Crop(r) : shot.Image;
        Reset(keepNotch: false);
        ScreenshotSaver.Save(image, region is null ? "screenshot" : "snip");
    }

    void OnSelectionMade(IReadOnlyList<Pt> stroke)
    {
        // While the key is held the selection waits for release (so speech can join it).
        if (_phase == Phase.Circling) _ = ProcessAsync(null, stroke);
    }

    // ---- Processing ----------------------------------------------------------------------------

    async Task ProcessAsync(float[]? speechSamples, IReadOnlyList<Pt>? stroke)
    {
        int session = _session;
        _phase = Phase.Processing;
        _notch.SetSearchMode(NotchMode.Thinking);

        string spoken = "";
        if (speechSamples is not null)
        {
            // Online listening first, when the user turned it on; null or nothing heard means the local model.
            string? heardOnline = null;
            if (_listenOnline && Modules.HostLinks.OnlineTranscribe is { } transcribeOnline)
            {
                _notch.SearchBarView.ShowThinking("Transcribing…");
                _transcribe = new CancellationTokenSource();
                try { heardOnline = await transcribeOnline(speechSamples, _transcribe.Token); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Log.Warn("Online transcription failed", ex); }
                if (session != _session) return;
            }
            if (!string.IsNullOrWhiteSpace(heardOnline))
            {
                spoken = heardOnline;
                Log.Info($"Heard online: {spoken.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} words");
            }
            else if (_whisper.IsReady)
            {
                _notch.SearchBarView.ShowThinking("Transcribing…");
                // Esc or a new search cancels this, so an abandoned transcription doesn't hold the model.
                _transcribe = new CancellationTokenSource();
                try { spoken = await _whisper.TranscribeAsync(speechSamples, _transcribe.Token); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Log.Error("Transcription failed", ex); }
                if (session != _session) return;
                // Never log what was said; the log isn't a transcript archive.
                Log.Info($"Heard: {spoken.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} words");
            }
            else if (stroke is null)
            {
                // The model failed earlier (e.g. no internet on first run): asking for voice retries it.
                if (_whisper.Failure is not null && !_whisper.IsDownloading)
                {
                    _ = _whisper.InitAsync();
                    await ShowMessageThenCancel(session, "", "Getting the voice model", "Downloading now · tap Right Alt to type meanwhile");
                    return;
                }
                await ShowMessageThenCancel(session, "",
                    _whisper.IsDownloading ? "Voice model is still downloading" : "Voice isn't available",
                    _whisper.IsDownloading ? $"{_whisper.DownloadProgress:P0} done · tap Right Alt to type instead" : _whisper.Failure ?? "Tap Right Alt to type instead");
                return;
            }
        }

        var (query, filters) = QueryBuilder.ExtractSpokenFilters(spoken);

        Selection? selection = null;
        if (stroke is { Count: > 0 } && _shot is not null)
        {
            _notch.SearchBarView.ShowThinking(query.Length > 0 ? $"“{query}”" : "Reading the selection…");
            var words = _ocr is null ? Array.Empty<OcrWord>() : await _ocr;
            if (session != _session) return;
            selection = SelectionAnalyzer.Analyze(words, stroke);
            selection = selection with { Region = selection.Region.Clamp(_shot.Width, _shot.Height) };
            Log.Info($"Selection: {stroke.Count} pts, {words.Count} OCR words on screen -> {selection.Kind}, {selection.Text.Length} chars");
            if (selection.Region.W < 4 || selection.Region.H < 4) selection = null;
            else _overlay?.ShowSelection(selection.Region);
        }

        // Modules get first look at a circle (a QR code opens its link, an amount converts...).
        Modules.SelectionInfo? info = null;
        if (selection is not null)
        {
            info = new Modules.SelectionInfo(selection.Text, _shot!.EncodePng(selection.Region), _shot.Crop(selection.Region),
                selection.Kind == SelectionKind.Image);
            foreach (var handler in _modules.SelectionHandlers)
            {
                Modules.CommandResult? taken = null;
                try { taken = await handler.TryTakeOverAsync(info); }
                catch (Exception ex) { Log.Warn($"Selection handler {handler.GetType().Name} failed", ex); }
                if (session != _session) return;
                if (taken is null) continue;
                ShowCommandResult(taken);
                return;
            }
        }

        // Spoken commands ("play …", "timer 10 minutes") run instead of a web search.
        if (selection is null && spoken.Length > 0 && await TryRunCommandAsync(spoken)) return;

        if (selection is null && query.Length == 0)
        {
            await ShowMessageThenCancel(session, "", "Didn't catch that", "Try again, or tap Right Alt to type");
            return;
        }

        if (selection is null && Calculator.TryEvaluate(query, out var value, out _))
        {
            ShowAnswer(query, Calculator.DisplayForm(query), Calculator.Format(value));
            return;
        }

        Context ctx;
        if (selection is { Kind: SelectionKind.Image })
        {
            var png = _shot!.EncodePng(selection.Region);
            ctx = new Context(query, query.Length > 0 ? query : "Selected area", SearchMode.Lens, filters, png, _shot.Crop(selection.Region));
        }
        else if (selection is not null)
        {
            var combined = QueryBuilder.Combine(query, selection.Text);
            ctx = new Context(combined, combined, _settings.DefaultSearchMode, filters, null, _shot!.Crop(selection.Region));
        }
        else
        {
            ctx = new Context(query, query, _settings.DefaultSearchMode, filters, null, null);
        }

        // An image selection can also be searched as text if OCR found words in it.
        if (ctx.Mode == SearchMode.Lens && selection is not null && selection.Text.Length > 0)
            ctx = ctx with { Query = QueryBuilder.Combine(query, selection.Text) };

        await ShowResultAsync(ctx with { Selection = info });
    }

    /// <summary>Runs the first module command that recognises the text. Returns false if none did.</summary>
    async Task<bool> TryRunCommandAsync(string text)
    {
        int session = _session;
        var clean = text.Trim().TrimEnd('.', '!', '?', '।').Trim();
        var command = Find(clean);
        // Hinglish timers and reminders ("5 minute ka timer laga do") run as the English the commands know.
        if (command is null && HinglishCommands.ToEnglish(clean) is { } english && Find(english) is { } translated)
        {
            command = translated;
            clean = english;
        }
        if (command is null) return false;

        _phase = Phase.Processing;
        // A command never uses the frozen screen; let it go now so a command that looks at the screen (a
        // screenshot) sees the live one rather than the dimmed freeze.
        DismissOverlay();
        _notch.SetSearchMode(NotchMode.Thinking);
        _notch.SearchBarView.ShowThinking($"“{clean}”");
        Modules.CommandResult result;
        try
        {
            result = await command.RunAsync(clean);
            Modules.HostLinks.RaiseCommandRan(clean);
        }
        catch (Exception ex)
        {
            Log.Error($"Command {command.GetType().Name} failed", ex);
            result = new Modules.CommandResult("", "That didn't work", ex.Message, IsError: true);
        }
        if (session != _session) return true;
        // The command found out it can't answer after all (an unknown place): search as if it never claimed the text.
        if (result.SearchInstead is { Length: > 0 } search) await SearchWebAsync(search);
        // A translation opens in the results card, the way the Translate chip shows it.
        else if (result.OpenInResults is { } page)
            await ShowResultAsync(new Context(page.Query, page.Query, _settings.DefaultSearchMode, SearchFilter.None, null, null) { Page = page.Url });
        else ShowCommandResult(result);
        return true;
    }

    Modules.ITextCommand? Find(string text) => _modules.Commands.FirstOrDefault(c =>
    {
        try { return c.CanHandle(text); }
        catch (Exception ex)
        {
            Log.Warn($"Command {c.GetType().Name} threw in CanHandle", ex);
            return false;
        }
    });

    /// <summary>A short confirmation card that closes by itself.</summary>
    void ShowCommandResult(Modules.CommandResult result)
    {
        DismissOverlay();
        _phase = Phase.Showing;
        _notch.AnswerView.ShowCommand(result);
        _notch.SetInteractive(false);
        _notch.SetSearchMode(NotchMode.Answer);
        Log.Info($"Command result ({(result.IsError ? "error" : "ok")})");
        int session = _session;
        // An answer with a Copy button stays long enough to reach it.
        _ = Task.Delay(result.IsError ? 4000 : result.CopyText is not null ? 5000 : 2600).ContinueWith(_ =>
        {
            if (session == _session) Cancel();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    async Task ProcessTextAsync(string text)
    {
        if (await TryRunCommandAsync(text)) return;
        if (Calculator.TryEvaluate(text, out var value, out _))
        {
            ShowAnswer(text, Calculator.DisplayForm(text), Calculator.Format(value));
            return;
        }
        var (query, filters) = QueryBuilder.ExtractSpokenFilters(text);
        if (query.Length == 0) query = text;
        await ShowResultAsync(new Context(query, query, _settings.DefaultSearchMode, filters, null, null));
    }

    Task SearchWebAsync(string text)
    {
        var (query, filters) = QueryBuilder.ExtractSpokenFilters(text);
        if (query.Length == 0) query = text;
        return ShowResultAsync(new Context(query, query, _settings.DefaultSearchMode, filters, null, null));
    }

    void ShowAnswer(string query, string expression, string result)
    {
        DismissOverlay();
        _phase = Phase.Showing;
        _notch.AnswerView.Show(query, expression, result);
        _notch.SetSearchMode(NotchMode.Answer);
        _notch.SetInteractive(true);
    }

    async Task ShowResultAsync(Context ctx)
    {
        int session = _session;
        _ctx = ctx;
        _phase = Phase.Showing;
        DismissOverlay();
        _keys.InterceptEscape = true;
        _notch.ResultView.SetContext(ctx.DisplayQuery, ctx.Thumb, ctx.Png is not null, ctx.Mode, ctx.Filters);
        var selection = ctx.Selection;
        _notch.ResultView.SetExtraChips(_modules.ResultChips
            .Where(c =>
            {
                try { return c.IsVisible(selection); }
                catch (Exception) { return false; }
            })
            .Select(c => (c.Label, (Action)(() => c.OnClick(selection, _notch.ResultView))))
            .ToList());
        _notch.SetSearchMode(NotchMode.Result);
        _notch.SetInteractive(true);
        Log.Info($"Search [{ctx.Mode}] {ctx.Query.Length} chars");
        try
        {
            await NavigateAsync(ctx);
        }
        catch (Exception ex)
        {
            if (session == _session) Log.Error("Showing results failed", ex);
        }
    }

    Task NavigateAsync(Context ctx)
    {
        if (ctx.Page is { } page) return _notch.ResultView.NavigateAsync(page);
        if (ctx.Mode == SearchMode.Lens && ctx.Png is not null) return _notch.ResultView.NavigateLensAsync(ctx.Png);
        var mode = ctx.Mode == SearchMode.Lens ? SearchMode.Ai : ctx.Mode;
        var q = ctx.Query.Length > 0 ? ctx.Query : ctx.DisplayQuery;
        var url = QueryBuilder.BuildUrl(QueryBuilder.Apply(q, ctx.Filters, mode, DateTime.Today), mode);
        return _notch.ResultView.NavigateAsync(url);
    }

    void ShowTyping()
    {
        _phase = Phase.Typing;
        _keys.InterceptEscape = true;
        _notch.SearchBarView.ShowTyping();
        _notch.SetSearchMode(NotchMode.Typing);
        _notch.SetInteractive(true);
        _notch.Dispatcher.BeginInvoke(_notch.SearchBarView.FocusInput, DispatcherPriority.Input);
    }

    async Task ShowMessageThenCancel(int session, string glyph, string primary, string secondary)
    {
        _notch.SetSearchMode(NotchMode.Thinking);
        _notch.SearchBarView.ShowMessage(glyph, primary, secondary);
        DismissOverlay();
        // Not Processing: the hint often says "tap Right Alt to type", and that tap must work right away.
        _phase = Phase.Message;
        await Task.Delay(2200);
        if (session == _session) Cancel();
    }

    // ---- Teardown ------------------------------------------------------------------------------

    public void Cancel()
    {
        if (_phase == Phase.Idle) return;
        Reset(keepNotch: false);
    }

    void Reset(bool keepNotch)
    {
        _session++;
        _holdTimer.Stop();
        if (_mic.IsRecording) _mic.Stop();
        DismissOverlay();
        // Teardown must always finish: a throw here used to leave Escape swallowed system-wide.
        try
        {
            if (_phase == Phase.Showing) _notch.ResultView.Release();
            else _notch.ResultView.CancelPending();
        }
        catch (Exception ex)
        {
            Log.Warn("Releasing the results view failed", ex);
        }
        _transcribe?.Cancel();
        _transcribe = null;
        _shot = null;
        _ocr = null;
        _ctx = null;
        _phase = Phase.Idle;
        _keys.InterceptEscape = false;
        _notch.SetInteractive(false);
        if (!keepNotch) _notch.SetSearchMode(null);
    }

    void DismissOverlay()
    {
        var o = _overlay;
        _overlay = null;
        o?.Dismiss();
    }

    /// <summary>Entry points for the command channel and tests.</summary>
    public Task SearchTextAsync(string text)
    {
        Reset(keepNotch: true);
        _session++;
        return ProcessTextAsync(text);
    }

    public Task SearchImageAsync(byte[] png)
    {
        Reset(keepNotch: true);
        _session++;
        var thumb = new BitmapImage();
        thumb.BeginInit();
        thumb.StreamSource = new MemoryStream(png);
        thumb.CacheOption = BitmapCacheOption.OnLoad;
        thumb.EndInit();
        thumb.Freeze();
        return ShowResultAsync(new Context("", "Image", SearchMode.Lens, SearchFilter.None, png, thumb));
    }

    /// <summary>Test hook: show the results card without a search (used by the browser-hardening test).</summary>
    public void ShowCardForTest()
    {
        Reset(keepNotch: true);
        _phase = Phase.Showing;
        _keys.InterceptEscape = true;
        _notch.ResultView.SetContext("Security test page", null, false, SearchMode.Ai, SearchFilter.None);
        _notch.SetSearchMode(NotchMode.Result);
    }

    public void OpenTyping()
    {
        Reset(keepNotch: true);
        ShowTyping();
    }
}
