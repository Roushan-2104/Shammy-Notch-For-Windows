using System.Diagnostics;
using System.Windows.Threading;
using Shammy.Core.Wheel;
using Shammy.Infrastructure;
using Shammy.Interop;

namespace Shammy.Modules.Wheel;

/// <summary>What the wheel keeps: the six beads (with pins), when each action was used, and its two switches.</summary>
internal sealed class WheelStore
{
    public List<WheelSlot>? Slots { get; set; }
    public Dictionary<string, List<DateTime>> Uses { get; set; } = new();

    /// <summary>"Change the wheel with what you're doing" (Settings › Wheel); on unless switched off.</summary>
    public bool FollowMoment { get; set; } = true;

    /// <summary>A soft click for each notch the mouse wheel turns the dial; off unless switched on.</summary>
    public bool ClickSound { get; set; }
}

/// <summary>The running wheel, for Settings › Wheel; null while the feature is off.</summary>
internal static class WheelLink
{
    public static WheelModule? Current { get; set; }
}

/// <summary>
/// The cat wheel, a liquid dial. Hold the middle mouse button still for a quarter of a second and the island pours
/// down to the pointer, where six droplets bead off around it; lean toward one, or turn the mouse wheel a notch at a
/// time, and let go to run it (it flies up to the island), or press 1–6; let go over the centre or press Esc to
/// cancel. The droplets follow the moment: call tools in a call, music controls with music, writing tools in an
/// editor, and otherwise your own beads. A quick middle click, or a press that moves at once (a drag, browser
/// auto-scroll), still goes to the app. Nothing is held back while the wheel is switched off or a fullscreen app (a
/// game, a video) is in front. Unpinned beads follow what you use; pins are set in Settings › Wheel.
/// Pipes: wheel-state, wheel-open, wheel-choose, wheel-turn, wheel-release, wheel-key, wheel-close, wheel-moment,
/// wheel-capture, wheel-use, wheel-run, wheel-pin, wheel-reset.
/// </summary>
internal sealed partial class WheelModule : IShammyModule
{
    const int MaxUsesKept = 40;

    /// <summary>The icon for an action (Segoe Fluent Icons).</summary>
    public static string GlyphFor(string id) => Glyphs.TryGetValue(id, out var g) ? g : "";

    static readonly IReadOnlyDictionary<string, string> Glyphs = new Dictionary<string, string>
    {
        ["screenshot"] = "", ["record"] = "", ["timer"] = "", ["music"] = "", ["notes"] = "",
        ["ask"] = "", ["phone"] = "", ["awake"] = "", ["mirror"] = "", ["captions"] = "",
        ["clipboard"] = "", ["calendar"] = "", ["weather"] = "", ["mixer"] = "", ["prompter"] = "",
        ["settings"] = "", ["guide"] = "", ["focus"] = "",
        // The moment's droplets: Stage borrows the prompter's page; Play/pause shows what it would do (see GlyphNow).
        ["stage"] = "", ["mute"] = "", ["share"] = "", ["playpause"] = "", ["next"] = "",
        ["previous"] = "", ["lyrics"] = "", ["musiccard"] = "", ["snippets"] = "", ["translate"] = "",
    };

    readonly WheelGesture _gesture = new();
    readonly WheelDial _dial = new(WheelGeometry.Beads);
    readonly Stopwatch _clock = Stopwatch.StartNew();
    ModuleContext _ctx = null!;
    WheelStore _store = new();
    MouseHook? _hook;
    WheelWindow? _window;
    DispatcherTimer _holdTimer = null!, _guard = null!;
    IDisposable? _follow;
    IReadOnlyList<WheelSlot> _slots = Array.Empty<WheelSlot>();
    // What the droplets hold while the wheel is open, and the moment that chose them.
    IReadOnlyList<string> _shown = Array.Empty<string>();
    WheelMoment _moment = WheelMoment.Usual;
    WheelMoment? _forcedMoment;
    string _momentWhy = "";
    volatile bool _active;
    bool _keyPicked;
    int _opens, _replays, _runs, _holdStarts, _holdTicks, _turns;
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
            if (_window is not { IsOpen: true } w) return "error: the wheel isn't open";
            _dial.Choose(int.TryParse(arg.Trim(), out var n) ? n : -1);
            w.Select(_dial.Selected);
            return "ok";
        });
        // "wheel-turn 1" turns one notch clockwise (as rolling the mouse wheel toward you), "wheel-turn -2" two back.
        ctx.AddPipeCommand("wheel-turn", arg =>
        {
            if (_window is not { IsOpen: true }) return "error: the wheel isn't open";
            int n = int.TryParse(arg.Trim(), out var k) ? k : 1;
            for (int i = 0; i < Math.Abs(n); i++) Turn(n > 0 ? -WheelDial.Notch : WheelDial.Notch);
            return $"selected={_dial.Selected} {(_dial.Selected >= 0 && _dial.Selected < _shown.Count ? _shown[_dial.Selected] : "-")}";
        });
        // "wheel-moment call|music|writing|usual" fixes the moment for the next opens; "wheel-moment auto" decides again.
        ctx.AddPipeCommand("wheel-moment", arg =>
        {
            var a = arg.Trim();
            if (a.Length == 0) return $"moment={WheelMoments.Name(DecideMoment(out var why))} ({why}) forced={(_forcedMoment is { } f ? WheelMoments.Name(f) : "no")}";
            if (a.Equals("auto", StringComparison.OrdinalIgnoreCase)) _forcedMoment = null;
            else if (WheelMoments.Parse(a) is { } m) _forcedMoment = m;
            else return "error: wheel-moment <call|music|writing|usual|auto>";
            return $"moment={WheelMoments.Name(DecideMoment(out var said))} ({said})";
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
            if (WheelSlots.Find(arg.Trim()) is null) return "error: no such action";
            Run(arg.Trim());
            return "ran " + arg.Trim();
        });
        ctx.AddPipeCommand("wheel-pin", arg =>
        {
            var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var i)) return "error: wheel-pin <bead 0-5> <action|off>";
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

    // ---- Open, follow, release ----------------------------------------------------------------------

    void Open(int x, int y, bool follow)
    {
        _slots = Arrange();
        _window ??= new WheelWindow();
        _keyPicked = false;
        _opens++;
        _moment = DecideMoment(out _momentWhy);
        _shown = _moment == WheelMoment.Usual ? _slots.Take(WheelGeometry.Beads).Select(s => s.ActionId).ToList() : WheelMoments.Droplets(_moment);
        _dial.Reset(_shown.Count);
        var droplets = _shown.Select(id => (GlyphNow(id), WheelSlots.Find(id)?.Name ?? id)).ToList();
        var (ix, iy) = IslandPoint(x, y);
        _window.OpenAt(x, y, ix, iy, droplets, _ctx.Settings.CatTheme, WheelMoments.Name(_moment));
        HostLinks.WheelKeys?.Invoke(true, OnKey);
        if (_hook is not null) _hook.WatchWheel = true;
        _follow?.Dispose();
        _follow = follow ? Motion.Animator.Subscribe(_ => Follow(x, y)) : null;
    }

    /// <summary>
    /// Where the island is, in screen pixels, for the pour to start from: the notch's pill when the pointer is on
    /// the notch's screen, otherwise the top centre of the pointer's own screen (the island comes to you there).
    /// The one place to switch to Shell's own island position once it offers one.
    /// </summary>
    (int X, int Y) IslandPoint(int x, int y)
    {
        var monitor = Native.MonitorFromPoint(new Native.POINT(x, y), Native.MONITOR_DEFAULTTONEAREST);
        double scale = Native.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
        int down = (int)Math.Round(23 * scale);
        // The island's own rectangle when it is on this screen: pour from the middle of the pill.
        var island = Shammy.Shell.IslandBounds.Screen;
        if (!island.IsEmpty && Shammy.Shell.IslandBounds.Monitor == monitor)
            return ((int)Math.Round(island.Left + island.Width / 2), (int)Math.Round(island.Top + island.Height / 2));
        try
        {
            var notch = new System.Windows.Interop.WindowInteropHelper(_ctx.Notch).Handle;
            if (notch != IntPtr.Zero && _ctx.Notch.IsVisible && Native.MonitorFromWindow(notch, Native.MONITOR_DEFAULTTONEAREST) == monitor &&
                Native.GetWindowRect(notch, out var r) && r.Width > 0)
                return (r.Left + r.Width / 2, r.Top + down);
        }
        catch (Exception ex)
        {
            _ctx.Warn("Finding the island for the wheel failed", ex);
        }
        var screen = Native.MonitorBounds(monitor);
        return (screen.Left + screen.Width / 2, screen.Top + down);
    }

    /// <summary>The moment now: forced by the test pipe, the plain beads when the setting is off, or what is going on.</summary>
    WheelMoment DecideMoment(out string why)
    {
        if (_forcedMoment is { } forced)
        {
            why = "set by wheel-moment";
            return forced;
        }
        if (!_store.FollowMoment)
        {
            why = "following the moment is off";
            return WheelMoment.Usual;
        }
        bool call = QuietState.Describe().Split('+').Contains("call");
        string? process = null, title = null;
        try
        {
            var front = Native.GetForegroundWindow();
            if (front != IntPtr.Zero)
            {
                title = MeetingSystem.MeetingSystemNative.WindowTitle(front);
                Native.GetWindowThreadProcessId(front, out var pid);
                if (pid != 0)
                {
                    using var p = Process.GetProcessById((int)pid);
                    process = p.ProcessName;
                }
            }
        }
        catch (Exception)
        {
            // The app in front closed as we looked, or it runs elevated: decide without it.
        }
        bool playing = _ctx.Services.Media.IsPlaying;
        why = $"call={call} front={process ?? "-"} music={playing}";
        return WheelMoments.Decide(new MomentSignals(call, process, title, playing));
    }

    void Follow(int x, int y)
    {
        if (_window is not { IsOpen: true } w) return;
        if (!Native.GetCursorPos(out var p)) return;
        double scale = w.Scale;
        if (_dial.Point((p.X - x) / scale, (p.Y - y) / scale)) w.Select(_dial.Selected);
    }

    /// <summary>The mouse wheel moved while the dial is open: one droplet per notch, with a click-stop (and a click, if on).</summary>
    void Turn(int delta)
    {
        if (_window is not { IsOpen: true } w) return;
        int steps = _dial.Scroll(delta);
        if (steps == 0) return;
        _turns++;
        w.Select(_dial.Selected, Math.Sign(steps));
        if (_store.ClickSound) DialClick.Play();
    }

    /// <summary>
    /// The middle button came up (or the test pipe): run what is chosen, or cancel when nothing is (the pointer back
    /// over the hub, or never turned or pointed).
    /// </summary>
    void Release()
    {
        if (_keyPicked || _window is not { IsOpen: true })
        {
            CloseWheel(ran: false);
            return;
        }
        int bead = _dial.Selected;
        string? id = bead >= 0 && bead < _shown.Count ? _shown[bead] : null;
        CloseWheel(ran: id is not null);
        if (id is not null) RunSoon(id);
    }

    /// <summary>1–6 while the wheel is open runs that droplet; 0 is Esc. 7 and 8 (no droplet there) do nothing.</summary>
    void OnKey(int n)
    {
        if (_window is not { IsOpen: true } w) return;
        if (n > _shown.Count) return;
        if (n < 1)
        {
            CloseWheel(ran: false);
            _keyPicked = true;
            return;
        }
        _dial.Choose(n - 1);
        w.Select(n - 1);
        var id = _shown[n - 1];
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
        if (_hook is not null) _hook.WatchWheel = false;
        HostLinks.WheelKeys?.Invoke(false, null);
        _window?.Close(ran);
    }

    /// <summary>Runs once the droplet has flown up to the island, so a screenshot or a card never meets the wheel on the way out.</summary>
    void RunSoon(string id)
    {
        if (WheelSlots.ById(id) is not null) RecordUse(id);
        var t = new DispatcherTimer(TimeSpan.FromMilliseconds(MotionPrefs.Lively ? 290 : 110), DispatcherPriority.Normal, (s, _) =>
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
                case "focus": RunText($"start focus for {(int)Core.Productivity.Pomodoro.FocusLength.TotalMinutes} minutes"); break;
                // The moment's droplets.
                case "stage": RunText("open the teleprompter"); break; // no stage view yet: the prompter is the closest
                case "mute": RunText("toggle mic"); break;
                case "share": RunText("send this to my phone"); break;
                case "playpause": _ctx.Services.Media.TogglePlayPause(); break;
                case "next": _ctx.Services.Media.Next(); break;
                case "previous": _ctx.Services.Media.Previous(); break;
                case "lyrics": RunText("show lyrics"); break;
                case "musiccard": _ctx.OpenCard("music"); break;
                case "snippets": _ctx.OpenCard("snippets"); break;
                case "translate": Translate(); break;
            }
        }
        catch (Exception ex)
        {
            _ctx.Warn($"Wheel action {id} failed", ex);
        }
    }

    static void RunText(string text) => HostLinks.RunText?.Invoke(text);

    /// <summary>Translates the copied text (into the home language, or English when it isn't English), in the results card.</summary>
    void Translate()
    {
        string text = "";
        try
        {
            if (System.Windows.Clipboard.ContainsText()) text = System.Windows.Clipboard.GetText().Trim();
        }
        catch (Exception)
        {
            // Another app holds the clipboard open; treat it as empty.
        }
        if (text.Length == 0)
        {
            _ctx.ShowMessage("", "Copy some text, then pick Translate");
            return;
        }
        if (text.Length > 500) text = text[..500];
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var language = Core.Conversions.Translation.LanguageName(Core.Conversions.Translation.DefaultTarget(text));
        RunText($"translate {text} to {language}");
    }

    /// <summary>A droplet's icon as it opens: Play/pause shows pause while music plays.</summary>
    string GlyphNow(string id) => id == "playpause" && _ctx.Services.Media.IsPlaying ? "" : GlyphFor(id);

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

    /// <summary>Settings › Wheel: the droplets change with what you're doing (a call, music, writing).</summary>
    public bool FollowMoment
    {
        get => _store.FollowMoment;
        set
        {
            _store.FollowMoment = value;
            _ctx.Save(_store);
        }
    }

    /// <summary>Settings › Wheel: a soft click for each notch the mouse wheel turns.</summary>
    public bool ClickSound
    {
        get => _store.ClickSound;
        set
        {
            _store.ClickSound = value;
            _ctx.Save(_store);
            if (value) DialClick.Play();
        }
    }

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

}
