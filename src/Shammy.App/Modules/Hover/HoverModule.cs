using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Shammy.Core.Hover;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Search;

namespace Shammy.Modules.Hover;

/// <summary>
/// Hover to understand: hold Left Alt about half a second and point at anything. The words under the pointer are read
/// on this PC (a small strip, Windows OCR) and a small bubble by the pointer answers: a translation, a price in rupees,
/// a measurement in the other system, a time in your zone, or what an English word means. Let go of Alt and it's gone.
/// Nothing runs until Alt is held: no timer, no capture, no browser. Hovers are never stored and never logged.
/// </summary>
internal sealed class HoverModule : IShammyModule
{
    const int VK_LMENU = 0xA4;

    /// <summary>The pointer must rest this long on a new spot before it is read.</summary>
    static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    /// <summary>At most about six reads a second, however the pointer moves.</summary>
    static readonly TimeSpan MinBetweenReads = TimeSpan.FromMilliseconds(166);

    static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(30);

    ModuleContext _ctx = null!;
    GlobalKeys? _keys;
    HoverAnswers _answers = null!;
    HoverBubbleWindow? _bubble;
    EnglishCheck? _english;

    // One hold: alive from HoverPressed to HoverReleased.
    DispatcherTimer? _tick;
    bool _held;
    int _session;
    Native.POINT _pos, _readPos = new(int.MinValue, int.MinValue);
    DateTime _movedAt, _readAt;
    bool _reading;
    Rect? _wordRect;
    HoverTarget? _current;
    CancellationTokenSource? _answerCts;
    HoverKind? _lastKind;
    DispatcherTimer? _showTimer;

    public string Name => "hover";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _answers = new HoverAnswers(ctx);
        _keys = GlobalKeys.Current;
        if (_keys is not null)
        {
            _keys.HoverAllowed = () => !_ctx.Notch.IsFullscreen;
            _keys.HoverPressed += OnHold;
            _keys.HoverReleased += OnRelease;
        }
        Apply();
        ctx.Settings.Changed += OnSettingChanged;

        ctx.AddPipeCommand("hover-test", HoverTestPipe);
        ctx.AddPipeCommand("hover-state", _ => State());
        ctx.AddPipeCommand("hover-show", HoverShowPipe);
    }

    void OnSettingChanged(string name)
    {
        if (name is nameof(AppSettings.HoverToUnderstand) or "") Apply();
    }

    void Apply()
    {
        bool on = _ctx.Settings.HoverToUnderstand;
        if (_keys is not null) _keys.HoverEnabled = on;
        if (!on && _held) End();
    }

    // ---- One hold ----------------------------------------------------------------------------------------------

    void OnHold()
    {
        if (!_ctx.Settings.HoverToUnderstand || _held) return;
        _held = true;
        _session++;
        StopShowTimer();
        Native.GetCursorPos(out _pos);
        _movedAt = DateTime.UtcNow - Debounce; // the first spot is read straight away
        _readPos = new Native.POINT(int.MinValue, int.MinValue);
        _wordRect = null;
        _current = null;
        _tick ??= new DispatcherTimer(Tick, DispatcherPriority.Input, (_, _) => OnTick(), _ctx.Ui);
        _tick.Start();
        OnTick();
    }

    void OnRelease()
    {
        if (_held) End();
    }

    /// <summary>The hold is over: the bubble goes, the lookups stop, the spelling checker and every buffer are let go.</summary>
    void End()
    {
        _held = false;
        _session++;
        _tick?.Stop();
        _tick = null;
        CancelAnswer();
        _bubble?.Clear();
        _english?.Dispose();
        _english = null;
        _wordRect = null;
        _current = null;
        _reading = false;
    }

    void OnTick()
    {
        if (!_held) return;
        // A release the hook missed (a secure desktop, a lost hook): Alt isn't down any more.
        if (Native.GetAsyncKeyState(VK_LMENU) >= 0)
        {
            End();
            return;
        }
        Native.GetCursorPos(out var p);
        var now = DateTime.UtcNow;
        if (p.X != _pos.X || p.Y != _pos.Y)
        {
            _pos = p;
            _movedAt = now;
            _bubble?.Follow(p.X, p.Y);
        }

        // Still on the word the bubble is about: nothing new to read.
        if (_wordRect is { } rect && rect.Contains(p.X, p.Y)) return;
        if (_wordRect is not null)
        {
            // Off that word: its bubble goes at once, and the new spot is read once the pointer settles.
            _wordRect = null;
            _current = null;
            CancelAnswer();
            _bubble?.Clear();
        }
        if (_reading || now - _movedAt < Debounce || now - _readAt < MinBetweenReads) return;
        if (p.X == _readPos.X && p.Y == _readPos.Y) return; // read here already and found nothing
        _ = ReadAtAsync(p, _session);
    }

    async Task ReadAtAsync(Native.POINT p, int session)
    {
        _reading = true;
        _readAt = DateTime.UtcNow;
        _readPos = p;
        HoverTarget? target = null;
        Rect rect = Rect.Empty;
        try
        {
            var read = await HoverReader.ReadAsync(p.X, p.Y);
            if (session != _session) return;
            if (read is not null)
            {
                _english ??= new EnglishCheck();
                target = HoverSense.Pick(read.Words, p.X - read.Left, p.Y - read.Top, DateTime.Now, _english.IsEnglish);
                if (target is not null)
                {
                    var b = target.Bounds;
                    rect = new Rect(read.Left + b.X - 3, read.Top + b.Y - 3, b.W + 6, b.H + 6);
                }
            }
        }
        catch (Exception ex)
        {
            _ctx.Warn($"Reading under the pointer failed ({ex.GetType().Name})");
        }
        finally
        {
            if (session == _session) _reading = false;
        }
        if (session != _session || target is null) return;
        // The pointer moved off it while OCR ran: the next tick reads where it is now.
        Native.GetCursorPos(out var nowAt);
        if (!rect.Contains(nowAt.X, nowAt.Y)) return;
        _wordRect = rect;
        _current = target;
        _lastKind = target.Kind;
        await AnswerAsync(target, nowAt, session);
    }

    async Task AnswerAsync(HoverTarget target, Native.POINT at, int session)
    {
        CancelAnswer();
        var cts = new CancellationTokenSource();
        _answerCts = cts;
        if (_answers.IsOnline(target)) Bubble().Show(HoverAnswers.Waiting(target), at.X, at.Y);
        var bubble = await _answers.AnswerAsync(target, cts.Token);
        if (cts.IsCancellationRequested || session != _session || !ReferenceEquals(_current, target)) return;
        Native.GetCursorPos(out var p);
        if (bubble is null) _bubble?.Clear();
        else Bubble().Show(bubble, p.X, p.Y);
    }

    void CancelAnswer()
    {
        var cts = _answerCts;
        _answerCts = null;
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
    }

    HoverBubbleWindow Bubble() => _bubble ??= new HoverBubbleWindow();

    // ---- Test pipes ------------------------------------------------------------------------------------------------

    /// <summary>"hover-test Wochenende" → "translation: weekend"; "hello world foo" → "none". No OCR, no keys.</summary>
    string HoverTestPipe(string arg)
    {
        var text = arg.Trim();
        if (text.Length == 0) return "error: hover-test <text>";
        using var english = new EnglishCheck();
        var target = HoverSense.Classify(text, DateTime.Now, english.IsEnglish);
        if (target is null) return "none";
        _lastKind = target.Kind;
        var bubble = WaitPumping(_answers.AnswerAsync(target, CancellationToken.None, patient: true), TimeSpan.FromSeconds(25));
        return bubble?.TestLine ?? $"none ({Short(target.Kind)}: no answer)";
    }

    /// <summary>"hover-show 640 400": the whole path at that screen point (physical pixels), as if Left Alt were held there.</summary>
    string HoverShowPipe(string arg)
    {
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            return "error: hover-show <x> <y>";
        if (!_ctx.Settings.HoverToUnderstand) return "off: Hover to understand is switched off in Settings";
        var read = WaitPumping(HoverReader.ReadAsync(x, y), TimeSpan.FromSeconds(10));
        if (read is null) return "none: OCR unavailable";
        if (read.Words.Count == 0) return "none: no words there";
        using var english = new EnglishCheck();
        var target = HoverSense.Pick(read.Words, x - read.Left, y - read.Top, DateTime.Now, english.IsEnglish);
        if (target is null) return $"none: nothing to answer ({read.Words.Count} words read)";
        _lastKind = target.Kind;
        var bubble = WaitPumping(_answers.AnswerAsync(target, CancellationToken.None, patient: true), TimeSpan.FromSeconds(25));
        if (bubble is null) return $"none: {Short(target.Kind)} \"{target.Text}\" had no answer";
        // Shown for a few seconds, as a hold would, unless a real hold is running.
        if (!_held)
        {
            Bubble().Show(bubble, x, y);
            StopShowTimer();
            _showTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Normal, (_, _) =>
            {
                StopShowTimer();
                if (!_held) _bubble?.Clear();
            }, _ctx.Ui);
            _showTimer.Start();
        }
        return $"shown: {bubble.TestLine} | label={bubble.Label} | text={target.Text}";
    }

    void StopShowTimer()
    {
        _showTimer?.Stop();
        _showTimer = null;
    }

    string State() =>
        $"enabled={_ctx.Settings.HoverToUnderstand} hook={(_keys?.IsInstalled == true ? "on" : "off")} held={_held} " +
        $"showing={(_bubble?.IsVisible == true)} bubble={_bubble?.Describe ?? "-"} last={(_lastKind is { } k ? Short(k) : "-")} " +
        $"cache={_answers.CacheCount} ocr={(OcrService.IsAvailable ? "yes" : "no")}";

    static string Short(HoverKind kind) => kind switch
    {
        HoverKind.Translation => "translation",
        HoverKind.Price => "price",
        HoverKind.Measure => "measure",
        HoverKind.Time => "time",
        _ => "meaning",
    };

    /// <summary>
    /// Waits for a task on the UI thread while still running the UI's messages, so the hidden page (which lives on
    /// this thread) can load. Only the test pipes use it: they must answer in one call.
    /// </summary>
    static T? WaitPumping<T>(Task<T> task, TimeSpan limit)
    {
        var frame = new DispatcherFrame();
        var until = DateTime.UtcNow + limit;
        var check = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (s, _) =>
        {
            if (task.IsCompleted || DateTime.UtcNow > until) frame.Continue = false;
        }, Dispatcher.CurrentDispatcher);
        check.Start();
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        check.Stop();
        return task.IsCompletedSuccessfully ? task.Result : default;
    }

    public void Dispose()
    {
        if (_keys is not null)
        {
            _keys.HoverEnabled = false;
            _keys.HoverPressed -= OnHold;
            _keys.HoverReleased -= OnRelease;
            _keys.HoverAllowed = null;
        }
        if (_ctx is not null) _ctx.Settings.Changed -= OnSettingChanged;
        End();
        StopShowTimer();
        _bubble?.Close();
        _bubble = null;
    }
}
