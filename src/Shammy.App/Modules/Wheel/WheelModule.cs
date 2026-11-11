using System.Diagnostics;
using System.Windows.Threading;
using Shammy.Core.Wheel;
using Shammy.Infrastructure;
using Shammy.Interop;

namespace Shammy.Modules.Wheel;

/// <summary>What the wheel keeps: the eight beads (with pins) and when each action was used.</summary>
internal sealed class WheelStore
{
    public List<WheelSlot>? Slots { get; set; }
    public Dictionary<string, List<DateTime>> Uses { get; set; } = new();
}

/// <summary>The running wheel, for Settings › Wheel; null while the feature is off.</summary>
internal static class WheelLink
{
    public static WheelModule? Current { get; set; }
}

/// <summary>
/// The cat wheel. Hold the middle mouse button still for a quarter of a second and eight actions open around the
/// pointer; lean toward one and let go to run it, or press 1–8; let go over the centre or press Esc to cancel. A
/// quick middle click, or a press that moves at once (a drag, browser auto-scroll), still goes to the app. Nothing
/// is held back while the wheel is switched off or a fullscreen app (a game, a video) is in front. Unpinned beads
/// follow what you use; pins are set in Settings › Wheel.
/// Pipes: wheel-state, wheel-open, wheel-choose, wheel-release, wheel-key, wheel-close, wheel-use, wheel-run, wheel-pin.
/// </summary>
internal sealed class WheelModule : IShammyModule
{
    const int MaxUsesKept = 40;

    /// <summary>The icon for an action (Segoe Fluent Icons).</summary>
    public static string GlyphFor(string id) => Glyphs.TryGetValue(id, out var g) ? g : "";

    /// <summary>Actions that open in the notch itself; the rest close the open notch first (a screenshot mustn't catch it).</summary>
    static readonly HashSet<string> OpensInNotch = new() { "timer", "notes", "mirror", "clipboard", "calendar", "weather", "mixer", "settings" };

    /// <summary>A dock key was clicked: counts as a use, like a wheel pick.</summary>
    public void RunFromDock(string id)
    {
        if (WheelSlots.ById(id) is null) return;
        RecordUse(id);
        if (OpensInNotch.Contains(id))
        {
            Run(id);
            return;
        }
        _ctx.Notch.Collapse();
        var t = new DispatcherTimer(TimeSpan.FromMilliseconds(220), DispatcherPriority.Normal, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            Run(id);
        }, _ctx.Ui);
        t.Start();
    }

    static readonly IReadOnlyDictionary<string, string> Glyphs = new Dictionary<string, string>
    {
        ["screenshot"] = "", ["record"] = "", ["timer"] = "", ["music"] = "", ["notes"] = "",
        ["ask"] = "", ["phone"] = "", ["awake"] = "", ["mirror"] = "", ["captions"] = "",
        ["clipboard"] = "", ["calendar"] = "", ["weather"] = "", ["mixer"] = "", ["prompter"] = "",
        ["settings"] = "", ["guide"] = "",
    };

    readonly WheelGesture _gesture = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    ModuleContext _ctx = null!;
    WheelStore _store = new();
    MouseHook? _hook;
    WheelWindow? _window;
    DispatcherTimer _holdTimer = null!, _guard = null!;
    IDisposable? _follow;
    IReadOnlyList<WheelSlot> _slots = Array.Empty<WheelSlot>();
    volatile bool _active;
    bool _keyPicked;
    int _opens, _replays, _runs, _holdStarts, _holdTicks;
    string _lastRun = "nothing yet";

    public string Name => "wheel";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _store = ctx.Load<WheelStore>();
        _slots = Arrange();
        WheelLink.Current = this;
        _holdTimer = new DispatcherTimer(WheelGesture.Hold, DispatcherPriority.Input, (_, _) => OnHoldElapsed(), ctx.Ui) { IsEnabled = false };
        // Fullscreen apps come and go; checking once a second costs nothing and keeps games' middle button theirs.
        _guard = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateActive(), ctx.Ui) { IsEnabled = false };
        ctx.Settings.Changed += OnSettingsChanged;
        HostLinks.CommandRan += OnCommandRan;
        ApplySetting();

        ctx.AddPipeCommand("wheel-state", _ => State());
        ctx.AddPipeCommand("wheel-open", arg =>
        {
            var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y)) Open(x, y, follow: false);
            else
            {
                Native.GetCursorPos(out var p);
                Open(p.X, p.Y, follow: false);
            }
            return "ok";
        });
        ctx.AddPipeCommand("wheel-choose", arg =>
        {
            _window?.Choose(int.TryParse(arg.Trim(), out var n) ? n : -1);
            return "ok";
        });
        ctx.AddPipeCommand("wheel-release", _ =>
        {
            Release();
            return "ok";
        });
        ctx.AddPipeCommand("wheel-key", arg =>
        {
            OnKey(int.TryParse(arg.Trim(), out var n) ? n : 0);
            return "ok";
        });
        ctx.AddPipeCommand("wheel-capture", arg =>
        {
            _window ??= new WheelWindow();
            _window.AllowCapture(arg.Trim() == "on");
            return "ok";
        });
        ctx.AddPipeCommand("wheel-reset", _ =>
        {
            _store = new WheelStore();
            _slots = Arrange();
            ctx.Save(_store);
            return Describe();
        });
        ctx.AddPipeCommand("wheel-close", _ =>
        {
            CloseWheel(ran: false);
            return "ok";
        });
        ctx.AddPipeCommand("wheel-use", arg =>
        {
            var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || WheelSlots.ById(parts[0]) is null) return "error: wheel-use <action> [count]";
            int n = parts.Length > 1 && int.TryParse(parts[1], out var c) ? c : 1;
            for (int i = 0; i < n; i++) RecordUse(parts[0]);
            return Describe();
        });
        ctx.AddPipeCommand("wheel-run", arg =>
        {
            if (WheelSlots.ById(arg.Trim()) is null) return "error: no such action";
            Run(arg.Trim());
            return "ran " + arg.Trim();
        });
        ctx.AddPipeCommand("wheel-pin", arg =>
        {
            var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var i)) return "error: wheel-pin <bead 0-7> <action|off>";
            if (parts[1] == "off") Unpin(i);
            else if (!Pin(i, parts[1])) return "error: no such action";
            return Describe();
        });
    }

    public void Dispose()
    {
        _ctx.Settings.Changed -= OnSettingsChanged;
        HostLinks.CommandRan -= OnCommandRan;
        if (WheelLink.Current == this) WheelLink.Current = null;
        _guard.Stop();
        _holdTimer.Stop();
        _hook?.Dispose();
        _hook = null;
        _follow?.Dispose();
        _window?.Close();
    }

    // ---- On and off -------------------------------------------------------------------------------

    void OnSettingsChanged(string name)
    {
        if (name is nameof(AppSettings.WheelEnabled) or "") ApplySetting();
    }

    void ApplySetting()
    {
        bool on = _ctx.Settings.WheelEnabled;
        if (on && _hook is null)
        {
            _hook = new MouseHook(Filter);
            _ctx.Info(_hook.IsInstalled ? "Wheel ready (hold the middle mouse button)" : "Wheel: the mouse hook could not be installed");
        }
        else if (!on && _hook is not null)
        {
            _hook.Dispose();
            _hook = null;
            CloseWheel(ran: false);
            _gesture.Reset();
        }
        _guard.IsEnabled = on;
        UpdateActive();
    }

    void UpdateActive() => _active = _ctx.Settings.WheelEnabled && !_ctx.Notch.IsFullscreen;

    // ---- The hook (its own thread: decide at once, post the rest) ---------------------------------

    bool Filter(MouseHook.Kind kind, int x, int y, uint time)
    {
        long ms = _clock.ElapsedMilliseconds;
        switch (kind)
        {
            case MouseHook.Kind.MiddleDown:
            {
                var step = _gesture.Down(x, y, ms, _active);
                if (step != WheelStep.Swallow) return false;
                _hook!.WatchMoves = true;
                _ctx.Ui.BeginInvoke(() =>
                {
                    _holdStarts++;
                    _holdTimer.Stop();
                    _holdTimer.Interval = WheelGesture.Hold;
                    _holdTimer.Start();
                });
                return true;
            }
            case MouseHook.Kind.Move:
            {
                if (_gesture.Move(x, y) != WheelStep.ReplayDownThenPass) return false;
                _hook!.WatchMoves = false;
                Interlocked.Increment(ref _replays);
                ThreadPool.QueueUserWorkItem(_ => MouseHook.SendMiddle(down: true, up: false));
                return false;
            }
            case MouseHook.Kind.MiddleUp:
            {
                _hook!.WatchMoves = false;
                var step = _gesture.Up(ms);
                switch (step)
                {
                    case WheelStep.SwallowAndReplayClick:
                        Interlocked.Increment(ref _replays);
                        ThreadPool.QueueUserWorkItem(_ => MouseHook.SendMiddle(down: true, up: true));
                        return true;
                    case WheelStep.SwallowAndRelease:
                        _ctx.Ui.BeginInvoke(Release);
                        return true;
                    default:
                        return false;
                }
            }
        }
        return false;
    }

    void OnHoldElapsed()
    {
        _holdTicks++;
        _holdTimer.Stop();
        if (!_gesture.HoldElapsed(_clock.ElapsedMilliseconds))
        {
            // Windows timers can fire a tick early: still held and still, so look again in a moment.
            if (_gesture.Current == WheelGesture.Phase.Pending)
            {
                _holdTimer.Interval = TimeSpan.FromMilliseconds(20);
                _holdTimer.Start();
            }
            return;
        }
        _holdTimer.Interval = WheelGesture.Hold;
        if (_hook is not null) _hook.WatchMoves = false;
        var (x, y) = _gesture.DownAt;
        Open(x, y, follow: true);
    }

    // ---- Open, follow, release ----------------------------------------------------------------------

    void Open(int x, int y, bool follow)
    {
        _slots = Arrange();
        _window ??= new WheelWindow();
        _keyPicked = false;
        _opens++;
        var beads = _slots.Select(s => (Glyphs.TryGetValue(s.ActionId, out var g) ? g : "", WheelSlots.ById(s.ActionId)?.Name ?? s.ActionId, s.Pinned)).ToList();
        _window.OpenAt(x, y, beads, _ctx.Settings.CatTheme);
        HostLinks.WheelKeys?.Invoke(true, OnKey);
        _follow?.Dispose();
        _follow = follow ? Motion.Animator.Subscribe(_ => Follow(x, y)) : null;
    }

    void Follow(int x, int y)
    {
        if (_window is not { IsOpen: true } w) return;
        if (!Native.GetCursorPos(out var p)) return;
        double scale = w.Scale;
        w.Point((p.X - x) / scale, (p.Y - y) / scale);
    }

    /// <summary>The middle button came up (or the test pipe): run what is chosen, or cancel over the centre.</summary>
    void Release()
    {
        if (_keyPicked || _window is not { IsOpen: true } w)
        {
            CloseWheel(ran: false);
            return;
        }
        int bead = w.Selected;
        string? id = bead >= 0 && bead < _slots.Count ? _slots[bead].ActionId : null;
        CloseWheel(ran: id is not null);
        if (id is not null) RunSoon(id);
    }

    /// <summary>1–8 while the wheel is open runs that bead; 0 is Esc.</summary>
    void OnKey(int n)
    {
        if (_window is not { IsOpen: true } w) return;
        if (n is < 1 or > 8 || n > _slots.Count)
        {
            CloseWheel(ran: false);
            _keyPicked = true;
            return;
        }
        w.Choose(n - 1);
        var id = _slots[n - 1].ActionId;
        CloseWheel(ran: true);
        // The button is probably still held: its release must not do anything else.
        _keyPicked = true;
        RunSoon(id);
    }

    void CloseWheel(bool ran)
    {
        _holdTimer.Stop();
        _follow?.Dispose();
        _follow = null;
        HostLinks.WheelKeys?.Invoke(false, null);
        _window?.Close(ran);
    }

    /// <summary>Runs after the wheel has folded, so a screenshot or a card never meets it on the way out.</summary>
    void RunSoon(string id)
    {
        RecordUse(id);
        var t = new DispatcherTimer(TimeSpan.FromMilliseconds(MotionPrefs.Lively ? 200 : 60), DispatcherPriority.Normal, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            Run(id);
        }, _ctx.Ui);
        t.Start();
    }

    // ---- The actions --------------------------------------------------------------------------------

    void Run(string id)
    {
        _runs++;
        _lastRun = id;
        _ctx.Info($"Wheel: {id}");
        try
        {
            switch (id)
            {
                case "screenshot": RunText("screenshot"); break;
                case "record": RunText("record my screen"); break;
                case "timer": _ctx.OpenCard("timer"); break;
                case "music": RunText(_ctx.Services.Media.IsPlaying ? "pause" : "play music"); break;
                case "notes": _ctx.OpenCard("notes"); break;
                case "ask":
                    if (HostLinks.ListenUntilSilence is { } listen) listen();
                    else _ctx.ShowMessage("", "Voice isn't ready", error: true);
                    break;
                case "phone": RunText("send this to my phone"); break;
                case "awake": RunText("keep my pc awake"); break;
                case "mirror": _ctx.OpenCard("mirror"); break;
                case "captions": RunText("turn on captions"); break;
                case "clipboard": _ctx.OpenCard("clipboard"); break;
                case "calendar": _ctx.OpenCard("calendar"); break;
                case "weather": _ctx.OpenCard("weather"); break;
                case "mixer": _ctx.OpenCard("mixer"); break;
                case "prompter": RunText("open the teleprompter"); break;
                case "settings": _ctx.OpenCard(Settings.SettingsModule.ModuleName); break;
                case "guide": RunText("what can you do"); break;
            }
        }
        catch (Exception ex)
        {
            _ctx.Warn($"Wheel action {id} failed", ex);
        }
    }

    static void RunText(string text) => HostLinks.RunText?.Invoke(text);

    // ---- Learning and pins --------------------------------------------------------------------------

    void OnCommandRan(string text)
    {
        if (WheelSlots.ActionFor(text) is { } id) RecordUse(id);
    }

    void RecordUse(string id)
    {
        if (!_store.Uses.TryGetValue(id, out var list)) _store.Uses[id] = list = new List<DateTime>();
        list.Add(DateTime.Now);
        if (list.Count > MaxUsesKept) list.RemoveRange(0, list.Count - MaxUsesKept);
        _slots = Arrange();
        _ctx.Save(_store);
    }

    IReadOnlyList<WheelSlot> Arrange()
    {
        var uses = _store.Uses.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<DateTime>)kv.Value);
        var slots = WheelSlots.Arrange(_store.Slots, uses, DateTime.Now);
        _store.Slots = slots.ToList();
        return slots;
    }

    /// <summary>The beads now, for Settings.</summary>
    public IReadOnlyList<WheelSlot> Slots => _slots;

    /// <summary>Pins <paramref name="actionId"/> on bead <paramref name="bead"/>; if it was on another bead, the two swap.</summary>
    public bool Pin(int bead, string actionId)
    {
        if (bead is < 0 or >= WheelGeometry.Beads || WheelSlots.ById(actionId) is null) return false;
        var slots = Arrange().ToList();
        int was = slots.FindIndex(s => s.ActionId == actionId);
        if (was >= 0 && was != bead) slots[was] = new WheelSlot(slots[bead].ActionId, slots[was].Pinned);
        slots[bead] = new WheelSlot(actionId, true);
        _store.Slots = slots;
        _slots = Arrange();
        _ctx.Save(_store);
        return true;
    }

    public void Unpin(int bead)
    {
        if (bead is < 0 or >= WheelGeometry.Beads) return;
        var slots = Arrange().ToList();
        slots[bead] = slots[bead] with { Pinned = false };
        _store.Slots = slots;
        _slots = Arrange();
        _ctx.Save(_store);
    }

    // ---- Pipes --------------------------------------------------------------------------------------

    string Describe() => string.Join(" | ", _slots.Select(s => s.ActionId + (s.Pinned ? "*" : "")));

    string State() =>
        $"enabled={_ctx.Settings.WheelEnabled} active={_active} hook={(_hook?.IsInstalled == true ? "on" : "off")} gesture={_gesture.Current} " +
        $"opens={_opens} replays={_replays} runs={_runs} holds={_holdStarts}/{_holdTicks} last_run={_lastRun} slots=[{Describe()}] " +
        (_window is null ? "window=none" : _window.Describe());
}
