using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shammy.Core.Search;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Shell;

namespace Shammy.Search;

internal partial class SearchCoordinator
{
    // ---- Hotkey ------------------------------------------------------------------------------

    /// <summary>
    /// Talk (Right Alt): hold to speak, tap to type; the screen stays live, so commands like "play …" or
    /// "5 ft in cm" don't freeze anything. Look (Copilot key / lone Right Ctrl): freeze straight away to
    /// circle, and speak at the same time if you like; a quick tap leaves the screen frozen to circle.
    /// </summary>
    // Listening with no key held (the wheel's Ask bead): it ends after a short silence once speech was heard,
    // or gives up if nothing is said.
    DispatcherTimer? _silence;
    readonly System.Diagnostics.Stopwatch _silenceClock = new();
    double _quietMs, _heardMs;
    static readonly TimeSpan SilenceCheck = TimeSpan.FromMilliseconds(100);
    const double SpeechLevel = 0.08, QuietLevel = 0.04, EndAfterQuietMs = 1100, GiveUpMs = 6000, LongestMs = 15000;

    /// <summary>Starts listening as a held Right Alt would; letting go is replaced by a short silence.</summary>
    public void ListenUntilSilence()
    {
        if (_phase == Phase.Processing) return;
        OnKeyDown(HotKey.Talk);
        _holdTimer.Stop();
        OnHoldElapsed();
        if (!_micOk) return;
        _quietMs = _heardMs = 0;
        _silenceClock.Restart();
        _silence ??= new DispatcherTimer(SilenceCheck, DispatcherPriority.Input, (_, _) => CheckSilence(), _notch.Dispatcher);
        _silence.Start();
    }

    void CheckSilence()
    {
        if (_phase != Phase.Holding || _key != HotKey.Talk)
        {
            _silence?.Stop();
            return;
        }
        double step = SilenceCheck.TotalMilliseconds;
        float level = _mic.Level;
        if (level >= SpeechLevel) { _heardMs += step; _quietMs = 0; }
        else if (level < QuietLevel) _quietMs += step;
        long elapsed = _silenceClock.ElapsedMilliseconds;
        bool spoke = _heardMs >= 200;
        if ((spoke && _quietMs >= EndAfterQuietMs) || elapsed >= LongestMs)
        {
            _silence?.Stop();
            _ = FinishHoldAsync();
        }
        else if (!spoke && elapsed >= GiveUpMs)
        {
            _silence?.Stop();
            Cancel();
        }
    }

    /// <summary>"listen-state": whether a key-less listen is running, and what it has measured.</summary>
    internal string SilenceState => $"phase={_phase} silence_timer={(_silence?.IsEnabled == true ? "on" : "off")} heard_ms={_heardMs:0} quiet_ms={_quietMs:0}";

    public void OnKeyDown(HotKey key)
    {
        if (_phase == Phase.Processing) return;
        // Start collapsing an open card right away so it's out of the way before the screen freezes.
        if (_phase != Phase.Idle) Reset(keepNotch: false);

        _session++;
        _key = key;
        _phase = Phase.Pending;
        _whisper.WarmUpSongPass();
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
        string listening = "Listening… · " + Modules.OnlineVoice.VoiceRoute.Tag(_settings, online);
        if (_key == HotKey.Talk)
        {
            // Just talking: no freeze, nothing to circle.
            if (_micOk)
            {
                var talkHint = _whisper.IsReady || online ? "Speak, then let go"
                    : _whisper.IsDownloading ? $"Voice model downloading ({_whisper.DownloadProgress:P0}) · let go to type"
                    : "Voice unavailable · let go to type";
                _notch.SearchBarView.ShowListening(() => _mic.Level, listening, talkHint, cat: _settings.CatTheme);
                // Live words come from the local model; with online listening the final words still come from Gnani.
                if (_whisper.IsReady) StartLive();
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
            _notch.SearchBarView.ShowListening(() => _mic.Level, listening, hint, cat: _settings.CatTheme);
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
    void StartLive()
    {
        _liveReads = 0;
        _liveTimer.Stop();
        _liveTimer.Start();
    }

    void StopLive()
    {
        _liveTimer.Stop();
        _live?.Cancel();
        _live = null;
    }

    /// <summary>One reading of the recording so far; shows the words and the plan if the key is still held.</summary>
    async Task ReadLiveAsync()
    {
        if (_phase != Phase.Holding || _key != HotKey.Talk)
        {
            _liveTimer.Stop();
            return;
        }
        if (_liveBusy) return;
        var samples = _mic.Snapshot();
        if (samples.Length < MicRecorder.SampleRate * 6 / 10 || !MicRecorder.HasSpeech(samples)) return;
        int session = _session;
        _liveBusy = true;
        var cts = _live = new CancellationTokenSource();
        string words = "";
        // One plain pass that never queues: if the model is busy this reading is skipped, and the command's own
        // transcription after letting go never waits behind it.
        try { words = await _whisper.TranscribeCaptionAsync(Shammy.Core.Search.AudioTrim.TrimSilence(samples, MicRecorder.SampleRate), cts.Token) ?? ""; }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Log.Warn("A live reading failed", ex); return; }
        finally
        {
            _liveBusy = false;
            if (ReferenceEquals(_live, cts)) _live = null;
            cts.Dispose();
        }
        if (session != _session || _phase != Phase.Holding || string.IsNullOrWhiteSpace(words)) return;
        _liveReads++;
        _notch.SearchBarView.ShowLive(words, PlanFor(words));
    }

    /// <summary>What letting go would do with <paramref name="words"/>, in a few words.</summary>
    internal string PlanFor(string words)
    {
        var (command, clean) = Resolve(words);
        if (command is not null)
        {
            Modules.CommandPreview? preview = null;
            try { preview = (command as Modules.ICommandPreview)?.Preview(clean); }
            catch (Exception) { }
            return "Let go to: " + (preview?.Title ?? clean);
        }
        if (Calculator.TryEvaluate(words, out _, out _)) return "Let go to work it out";
        if (ChoicesFor(words) is { } c) return "Let go to choose: " + string.Join(" · ", c.List.Take(2).Select(x => x.Title));
        return "Let go to search Google";
    }

    /// <summary>"voice-live": how many live readings the last hold showed, and the phase.</summary>
    internal string LiveState => $"phase={_phase} live_reads={_liveReads} live_busy={_liveBusy}" +
        (_phase == Phase.Choices && _choices is { } list ? $" choices=[{string.Join(" | ", list.Select(c => c.Title))} | Search Google]" : "");

    /// <summary>Test hook: picks a "Did you mean" chip as a click or a number key would (0-based; the last is Google).</summary>
    internal string PickForTest(int index)
    {
        if (_phase != Phase.Choices) return "error: no choices showing";
        PickChoice(index);
        return "picked";
    }

    async Task FinishHoldAsync()
    {
        int session = _session;
        _phase = Phase.Processing;
        StopLive();
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
}
