using System.Diagnostics;
using System.Windows.Threading;
using Shammy.Core.Dictation;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Modules;
using Shammy.Shell;

namespace Shammy.Search;

/// <summary>
/// Dictation: hold a key, talk, let go, and the words are typed into whatever app has the cursor. The key is a
/// setting (<see cref="DictationKey"/>): Ctrl + Win held, a smart Right Alt (dictates in a text box, stays the voice
/// command key anywhere else), or a double-tapped Right Alt (tap once more to stop). The words come from the online
/// voice (Gnani, the same key and switch as listening online); while you talk, the growing recording is sent for the
/// live words at most every 1.5 s. Hindi is typed in Roman letters unless "in Hindi" was said. The words are pasted
/// and the clipboard put back (<see cref="PasteInto"/>); nothing is kept afterwards, the audio is never saved and
/// the log only ever counts words. Pipes: "dictate-state", "dictate-clean &lt;raw&gt;", "dictate-fake &lt;words&gt;",
/// "dictate-key &lt;ctrlwin|rightalt|double|off&gt;".
/// </summary>
internal sealed class DictationController
{
    enum Phase { Idle, Recording, Typing }

    /// <summary>The talk key's press: still finding out what has the keyboard, the search's, or dictation's.</summary>
    enum TalkRoute { None, Probing, Search, Dictation, Stopping }

    /// <summary>The island waits this long before showing anything, so Ctrl+Win+D and other shortcuts don't flash it.</summary>
    const long ShowAfterMs = 180;

    /// <summary>Gnani takes 30 s a clip; stop a little before (a double-tap left running, a key held by accident).</summary>
    const long LongestMs = (long)(Core.OnlineVoice.GnaniApi.SttMaxSeconds * 1000);

    /// <summary>A Right Alt let go sooner than this was a tap (type), as for the talk key.</summary>
    const long TapMs = 180;

    static readonly TimeSpan ReleaseTail = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan DoubleTapWindow = TimeSpan.FromMilliseconds(320);
    static readonly TimeSpan TypedStays = TimeSpan.FromSeconds(4), MessageStays = TimeSpan.FromSeconds(6);

    readonly NotchWindow _notch;
    readonly GlobalKeys _keys;
    readonly SearchCoordinator _search;
    readonly AppSettings _settings;
    readonly MicRecorder _mic = new();
    readonly DispatcherTimer _tick, _tapWait, _close;
    readonly Stopwatch _clock = new();

    Phase _phase;
    int _session;
    IntPtr _target;
    string _app = "this app";
    bool _byCtrlWin, _latched, _shown, _hindi;
    string _live = "";
    LiveWordsPacer _pacer = new(0);
    int _spokeFrom;
    long _nextLookMs;
    CancellationTokenSource? _cts;
    DictationCard? _card, _onScreen;
    float[]? _retry;

    TalkRoute _route;
    bool _upEarly, _abortEarly;
    long _talkDownMs;

    public DictationController(NotchWindow notch, GlobalKeys keys, SearchCoordinator search, AppSettings settings, ModuleRegistry modules)
    {
        _notch = notch;
        _keys = keys;
        _search = search;
        _settings = settings;
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Input, (_, _) => Tick(), notch.Dispatcher) { IsEnabled = false };
        _tapWait = new DispatcherTimer(DoubleTapWindow, DispatcherPriority.Input, (timer, _) =>
        {
            // One tap and no second: Right Alt's usual tap, the typing palette.
            ((DispatcherTimer)timer!).Stop();
            _search.OpenTyping();
        }, notch.Dispatcher) { IsEnabled = false };
        _close = new DispatcherTimer(TypedStays, DispatcherPriority.Normal, (_, _) => CloseCard(), notch.Dispatcher) { IsEnabled = false };

        keys.DictatePressed += () => Start("let go to type it", byCtrlWin: true);
        keys.DictateReleased += () =>
        {
            if (_byCtrlWin) _ = StopAsync();
        };
        keys.DictateAborted += () =>
        {
            if (_byCtrlWin && _phase == Phase.Recording) Cancel();
        };
        keys.TalkTaken = OnTalk;
        ApplyKey();
        settings.Changed += name =>
        {
            if (name == nameof(AppSettings.DictationKey)) ApplyKey();
        };
        PrivacyState.Changed += _ => notch.Dispatcher.BeginInvoke(() => _card?.SetWords(_live, PrivacyState.Presenting));

        modules.PipeCommands["dictate-state"] = _ => State();
        modules.PipeCommands["dictate-clean"] = raw => DictationText.Finish(raw, DictationText.AsksForHindi(raw), light: true);
        modules.PipeCommands["dictate-fake"] = Fake;
        modules.PipeCommands["dictate-key"] = SetKeyForTest;
    }

    void ApplyKey()
    {
        _keys.DictationCtrlWin = _settings.DictationKey == DictationKey.CtrlWin;
        _tapWait.Stop();
        if (_phase == Phase.Recording && !_byCtrlWin && _settings.DictationKey is not (DictationKey.RightAlt or DictationKey.DoubleTap)) Cancel();
    }

    // ---- Right Alt (smart, double-tap) -------------------------------------------------------------------------

    /// <summary><see cref="GlobalKeys.TalkTaken"/>: true when dictation handles this Right Alt edge (it may still pass it to the search).</summary>
    bool OnTalk(TalkEdge edge) => _settings.DictationKey switch
    {
        DictationKey.RightAlt => SmartTalk(edge),
        DictationKey.DoubleTap => DoubleTapTalk(edge),
        _ => false,
    };

    bool SmartTalk(TalkEdge edge)
    {
        switch (edge)
        {
            case TalkEdge.Down:
                if (_route != TalkRoute.None) return true;
                _route = TalkRoute.Probing;
                _upEarly = _abortEarly = false;
                _talkDownMs = Environment.TickCount64;
                _ = RouteSmartAsync();
                return true;
            case TalkEdge.Up:
                switch (_route)
                {
                    case TalkRoute.Probing: _upEarly = true; return true;
                    case TalkRoute.Search: _search.OnKeyUp(HotKey.Talk); break;
                    case TalkRoute.Dictation when Environment.TickCount64 - _talkDownMs < TapMs:
                        // A tap in a text box is still "type to Shammy".
                        Cancel();
                        _search.OpenTyping();
                        break;
                    case TalkRoute.Dictation: _ = StopAsync(); break;
                }
                _route = TalkRoute.None;
                return true;
            default:
                switch (_route)
                {
                    case TalkRoute.Probing: _abortEarly = true; return true;
                    case TalkRoute.Search: _search.OnKeyAborted(HotKey.Talk); break;
                    case TalkRoute.Dictation: Cancel(); break;
                }
                _route = TalkRoute.None;
                return true;
        }
    }

    async Task RouteSmartAsync()
    {
        var focus = await FocusProbe.ReadAsync();
        if (_route != TalkRoute.Probing) return;
        if (_abortEarly)
        {
            _route = TalkRoute.None;
            return;
        }
        if (focus.Editable && !_upEarly && !ShammyInFront())
        {
            _route = TalkRoute.Dictation;
            if (!Start("let go to type it", byCtrlWin: false, focus)) _route = TalkRoute.None;
            return;
        }
        // Not a text box (or a quick tap): Right Alt as it always was.
        _route = _upEarly ? TalkRoute.None : TalkRoute.Search;
        _search.OnKeyDown(HotKey.Talk);
        if (_upEarly) _search.OnKeyUp(HotKey.Talk);
    }

    bool DoubleTapTalk(TalkEdge edge)
    {
        switch (edge)
        {
            case TalkEdge.Down:
                if (_route != TalkRoute.None) return true;
                if (_phase == Phase.Recording && _latched) _route = TalkRoute.Stopping;
                else if (_tapWait.IsEnabled)
                {
                    _tapWait.Stop();
                    _route = Start("tap Right Alt to type it", byCtrlWin: false, latched: true) ? TalkRoute.Dictation : TalkRoute.None;
                }
                else
                {
                    _route = TalkRoute.Search;
                    _search.OnKeyDown(HotKey.Talk);
                }
                return true;
            case TalkEdge.Up:
                switch (_route)
                {
                    case TalkRoute.Stopping: _ = StopAsync(); break;
                    case TalkRoute.Search when _search.IsTalkPending:
                        // A tap: wait a moment for the second one before opening the palette.
                        _search.Cancel();
                        _tapWait.Start();
                        break;
                    case TalkRoute.Search: _search.OnKeyUp(HotKey.Talk); break;
                }
                _route = TalkRoute.None;
                return true;
            default:
                switch (_route)
                {
                    case TalkRoute.Search: _search.OnKeyAborted(HotKey.Talk); break;
                    case TalkRoute.Dictation or TalkRoute.Stopping: Cancel(); break;
                }
                _route = TalkRoute.None;
                return true;
        }
    }

    // ---- Recording -------------------------------------------------------------------------------------------

    /// <summary>Starts dictating into the app in front (the microphone opens with the island); false when Shammy itself is in front.</summary>
    bool Start(string howToFinish, bool byCtrlWin, FocusProbe.Focus? focus = null, bool latched = false)
    {
        if (_phase != Phase.Idle) return false;
        CloseCard();
        _retry = null;
        // Dictation types into other apps; Shammy's own boxes have the talk key.
        if (ShammyInFront()) return false;
        _target = Native.GetForegroundWindow();
        _session++;
        _phase = Phase.Recording;
        _byCtrlWin = byCtrlWin;
        _latched = latched;
        _app = Modules.CircleMemory.FrontApp.Name() ?? "this app";
        _hindi = _shown = false;
        _live = "";
        _clock.Restart();
        _pacer = new LiveWordsPacer(0);
        _spokeFrom = 0;
        _nextLookMs = 0;
        _cts = new CancellationTokenSource();
        _card = DictationCard.Live(() => _mic.Level, _app, howToFinish);
        _tick.Start();
        if (focus is { Known: true } f) OnFocus(f, _session);
        else _ = CheckFocusAsync(_session);
        Log.Info("Dictation started");
        return true;
    }

    static bool ShammyInFront()
    {
        var front = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(front, out uint pid);
        return front == IntPtr.Zero || pid == Environment.ProcessId;
    }

    async Task CheckFocusAsync(int session)
    {
        var focus = await FocusProbe.ReadAsync();
        if (session == _session) OnFocus(focus, session);
    }

    void OnFocus(FocusProbe.Focus focus, int session)
    {
        if (!focus.Password || session != _session || _phase != Phase.Recording) return;
        // A password box: stop before anything is heard or sent, and keep nothing.
        Cancel();
        ShowMessage("Dictation is off in password boxes", "Nothing was recorded.");
    }

    void Tick()
    {
        if (_phase != Phase.Recording)
        {
            _tick.Stop();
            return;
        }
        long now = _clock.ElapsedMilliseconds;
        if (!_shown && now >= ShowAfterMs)
        {
            // The microphone opens with the island, so a quick Ctrl+Win+D never lights Windows' mic indicator.
            if (!_mic.Start(out var micError))
            {
                Cancel();
                ShowMessage("Microphone unavailable", (micError ?? "The microphone didn’t start") + " · nothing was recorded");
                return;
            }
            _shown = true;
            Show(_card!, DictationCard.LiveWidth, DictationCard.LiveHeight, stays: null);
        }
        if (now >= LongestMs)
        {
            _ = StopAsync();
            return;
        }
        // Live words cost a request each, so none while nobody can see them (the screen is shared).
        if (!_shown || PrivacyState.Presenting || now < _nextLookMs || !_pacer.Due(now, spokeSince: true)) return;
        var heard = _mic.Snapshot();
        bool spoke = MicRecorder.HasSpeech(heard.AsSpan(Math.Min(_spokeFrom, heard.Length)).ToArray());
        if (!spoke)
        {
            // Silence since the last ask: look again in a moment rather than copying the recording every tick.
            _nextLookMs = now + 400;
            return;
        }
        _spokeFrom = heard.Length;
        _pacer.Sent(now);
        _ = PartialAsync(heard, _session);
    }

    async Task PartialAsync(float[] heard, int session)
    {
        if (HostLinks.OnlineDictate is not { } dictate || _cts is not { } cts) return;
        DictateReply reply;
        try { reply = await dictate(heard, false, true, cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log.Warn("Live dictation words failed", ex);
            return;
        }
        finally
        {
            if (session == _session) _pacer.Answered();
        }
        if (session != _session || _phase != Phase.Recording || string.IsNullOrWhiteSpace(reply.Text)) return;
        // Only a hint while talking: "… in Hindi" may turn out to be mid-sentence. The final words decide.
        bool asks = DictationText.AsksForHindi(reply.Text);
        if (asks != _hindi)
        {
            _hindi = asks;
            _card?.SetScript(devanagari: asks);
        }
        _live = DictationText.Live(reply.Text);
        _card?.SetWords(_live, PrivacyState.Presenting);
    }

    /// <summary>Let go (or the stopping tap): finish listening, write it down, type it.</summary>
    async Task StopAsync()
    {
        if (_phase != Phase.Recording) return;
        int session = _session;
        _phase = Phase.Typing;
        _tick.Stop();
        if (!_shown)
        {
            // Let go before anything showed: a stray Ctrl+Win, not dictation.
            Cancel();
            return;
        }
        _card?.SetLine("Typing it in…", listening: false);
        // People let go on the last syllable; keep listening a moment longer, as the talk key does.
        await Task.Delay(ReleaseTail);
        if (session != _session) return;
        var heard = _mic.Stop();
        if (MicRecorder.IsSilentBlock(heard))
        {
            Finish();
            ShowMessage("Microphone is blocked", "Allow desktop apps in Settings › Privacy › Microphone.");
            return;
        }
        if (!MicRecorder.HasSpeech(heard))
        {
            Finish();
            ShowMessage("Didn’t hear anything", "Nothing was typed.");
            return;
        }
        await WriteDownAsync(heard, session, retried: false);
    }

    async Task WriteDownAsync(float[] heard, int session, bool retried)
    {
        if (HostLinks.OnlineDictate is not { } dictate)
        {
            Finish();
            ShowMessage("Dictation needs internet", "Turn the online voice back on in Settings › Advanced · nothing was kept.");
            return;
        }
        var ct = (_cts ??= new CancellationTokenSource()).Token;
        DictateReply reply;
        bool hindi;
        try
        {
            reply = await dictate(heard, false, false, ct);
            if (session != _session) return;
            hindi = DictationText.AsksForHindi(reply.Text);
            if (hindi)
            {
                // "in Hindi": the same audio once more, written in Devanagari. If that fails, Roman it is.
                var devanagari = await dictate(heard, true, false, ct);
                if (session != _session) return;
                if (!string.IsNullOrWhiteSpace(devanagari.Text)) reply = devanagari;
                else hindi = false;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Finish();
        if (reply.Text is null)
        {
            if (reply.CanRetry && !retried)
            {
                // Only this one recording, only until the message goes.
                _retry = heard;
                ShowMessage("Dictation needs internet", (reply.Problem ?? "Couldn’t reach Gnani") + " · nothing is kept", "Try again", Retry);
            }
            else ShowMessage("Dictation needs internet", (reply.Problem ?? "Couldn’t reach Gnani") + " · nothing was kept.");
            return;
        }
        var typed = DictationText.Finish(reply.Text, hindi, _settings.DictationCleanup);
        if (typed.Length == 0)
        {
            ShowMessage("Didn’t hear anything", "Nothing was typed.");
            return;
        }
        await TypeAsync(typed, session);
    }

    void Retry()
    {
        if (_retry is not { } heard || _phase != Phase.Idle) return;
        _retry = null;
        _session++;
        _phase = Phase.Typing;
        _cts = new CancellationTokenSource();
        ShowMessage("Trying again", "Sending the same words to Gnani once more.");
        _close.Stop();
        _ = WriteDownAsync(heard, _session, retried: true);
    }

    async Task TypeAsync(string typed, int session)
    {
        int words = DictationText.Words(typed);
        var result = await PasteInto.PasteAsync(typed, _target);
        // Counts only, never the words.
        Log.Info($"Dictation: {result}, {words} words");
        if (session != _session) return;
        switch (result)
        {
            case PasteInto.Result.Typed:
                var target = _target;
                Show(DictationCard.Typed(_app, words, () => _ = UndoAsync(target)), DictationCard.PillWidth, DictationCard.PillHeight, TypedStays);
                break;
            case PasteInto.Result.WindowChanged:
                ShowMessage("The window changed", $"Nothing was typed into {_app}.", "Copy", () =>
                {
                    CloseCard();
                    Modules.SelectionTools.ClipboardText.TrySet(typed);
                });
                break;
            default:
                ShowMessage("The clipboard is busy", "Nothing was typed · try again in a moment.");
                break;
        }
    }

    async Task UndoAsync(IntPtr target)
    {
        CloseCard();
        if (!await PasteInto.UndoAsync(target)) ShowMessage("Couldn’t undo", "That window isn’t there any more.");
    }

    /// <summary>Drops the dictation in progress: the microphone stops, a pending answer is ignored, the card goes.</summary>
    public void Cancel()
    {
        _session++;
        _tick.Stop();
        if (_mic.IsRecording) _mic.Stop();
        _cts?.Cancel();
        _cts = null;
        _phase = Phase.Idle;
        _latched = false;
        _live = "";
        CloseCard();
    }

    /// <summary>The words are on their way (or failed): back to idle, keeping the card on screen for what comes next.</summary>
    void Finish()
    {
        _phase = Phase.Idle;
        _latched = false;
        _live = "";
        _cts = null;
        _card = null;
    }

    // ---- The island ------------------------------------------------------------------------------------------

    void ShowMessage(string title, string detail, string? button = null, Action? click = null) =>
        Show(DictationCard.Message(title, detail, button, click), DictationCard.MessageWidth, DictationCard.MessageHeight, MessageStays);

    /// <summary>Shows a moment; it never takes the keyboard (you're typing elsewhere). One that stays folds away on its own unless the pointer rests on it.</summary>
    void Show(DictationCard card, double width, double height, TimeSpan? stays)
    {
        _close.Stop();
        _onScreen = card;
        card.MouseEnter += (_, _) => _close.Stop();
        card.MouseLeave += (_, _) =>
        {
            if (ReferenceEquals(_onScreen, card) && stays is not null) _close.Start();
        };
        _notch.ShowCard(card, width, height, interactive: false);
        if (stays is { } s)
        {
            _close.Interval = s;
            _close.Start();
        }
    }

    void CloseCard()
    {
        _close.Stop();
        _retry = null;
        // Only if it's still ours on screen (an alarm may have replaced it).
        if (_onScreen?.Parent is not null) _notch.CloseCard();
        _onScreen = null;
    }

    // ---- Pipes ----------------------------------------------------------------------------------------------

    string State() =>
        $"phase={_phase} key={DictationKeys.Name(_settings.DictationKey)} cleanup={(_settings.DictationCleanup ? "light" : "off")} " +
        $"ctrlwin_hook={(_keys.DictationCtrlWin ? "on" : "off")} script={(_hindi ? "devanagari" : "roman")} latched={(_latched ? "yes" : "no")} " +
        $"route={_route} live_words={DictationText.Words(_live)} retry={(_retry is null ? "no" : "yes")} " +
        $"online={(HostLinks.OnlineDictate is null ? "off" : HostLinks.OnlineListening?.Invoke() == true ? "yes" : "no")} " +
        $"card={(_onScreen?.Parent is null ? "none" : "'" + _onScreen.Describe() + "'")}";

    /// <summary>
    /// "dictate-fake [App|]&lt;words&gt;": the island's moments for these words as if heard (live words, then "Typed into …"),
    /// through the real clean-up, without the microphone, Gnani, the clipboard or a paste. Says what would be typed.
    /// </summary>
    string Fake(string arg)
    {
        if (_phase != Phase.Idle) return "error: dictation is running";
        // "App|words" names the app (README pictures); otherwise it's the one in front.
        var bar = arg.IndexOf('|');
        var heard = bar >= 0 ? arg[(bar + 1)..] : arg;
        var hindi = DictationText.AsksForHindi(heard);
        var typed = DictationText.Finish(heard, hindi, _settings.DictationCleanup);
        var app = bar > 0 ? arg[..bar].Trim() : Modules.CircleMemory.FrontApp.Name() ?? "this app";
        var card = DictationCard.Live(() => 0.35f, app, "let go to type it");
        card.SetScript(hindi);
        card.SetWords(DictationText.Live(heard), PrivacyState.Presenting);
        Show(card, DictationCard.LiveWidth, DictationCard.LiveHeight, stays: null);
        int session = ++_session;
        _ = _notch.Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(1200);
            if (session != _session || !ReferenceEquals(_onScreen, card)) return;
            card.SetLine("Typing it in…", listening: false);
            await Task.Delay(500);
            if (session != _session || !ReferenceEquals(_onScreen, card)) return;
            if (typed.Length == 0) ShowMessage("Didn’t hear anything", "Nothing was typed.");
            else Show(DictationCard.Typed(app, DictationText.Words(typed), CloseCard), DictationCard.PillWidth, DictationCard.PillHeight, TypedStays);
        });
        return $"would type '{typed}' into {app} ({DictationText.Words(typed)} words, {(hindi ? "Devanagari" : "Roman")}, clean-up {(_settings.DictationCleanup ? "light" : "off")}) · nothing pasted";
    }

    string SetKeyForTest(string arg)
    {
        if (DictationKeys.Parse(arg) is not { } key) return "error: usage dictate-key ctrlwin|rightalt|double|off";
        if (Modules.Settings.SettingsCatalog.Find(nameof(AppSettings.DictationKey)) is not { } info) return "error: no such setting";
        Modules.Settings.SettingsCatalog.Set(_settings, info, key);
        return $"DictationKey={DictationKeys.Name(_settings.DictationKey)} ctrlwin_hook={(_keys.DictationCtrlWin ? "on" : "off")}";
    }
}
