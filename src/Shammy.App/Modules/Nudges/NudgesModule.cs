using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Core.Nudges;
using Shammy.Infrastructure;
using Shammy.Modules.Settings;
using Shammy.Shell;

namespace Shammy.Modules.Nudges;

/// <summary>
/// Small wellbeing nudges from the pet. Eye breaks: after every 20 minutes of active screen time the pet closes
/// its eyes with you for 20 seconds. Water: every 90 active minutes it asks, and the check counts a glass for
/// today. Neither shows during a call, a meeting, a focus session, a shared screen, a fullscreen app or while
/// another card is open; a nudge due then waits up to ten minutes for a free moment. No sound, ever.
/// Commands: "eye breaks on/off", "water reminders on/off", "take an eye break".
/// Pipes: "nudges-state", "nudges-fire eye|water", "nudges-advance &lt;minutes&gt;", "nudges-idle &lt;seconds&gt;",
/// "nudges-check".
/// </summary>
internal sealed class NudgesModule : IShammyModule
{
    const string EyeId = "nudges.eye", WaterId = "nudges.water";
    const string EyeGlyph = "", WaterGlyph = "";

    /// <summary>Between messages (50) and downloads (40): a call, a ringing timer or a notification keeps the pet.</summary>
    const int Priority = 45;

    /// <summary>The countdown reads 0:00 for a moment before the pet opens its eyes.</summary>
    static readonly TimeSpan EyeBreakStays = NudgeText.EyeBreakLength + TimeSpan.FromSeconds(1);

    static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly NudgeScheduler _scheduler = new();
    readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = CheckEvery };
    ModuleContext _ctx = null!;
    WaterTally _water = new();
    TimeSpan? _pretendIdle;
    TimeSpan _lastIdle;
    bool _idleWarned;

    public string Name => "nudges";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _water = ctx.Load<WaterTally>();
        _timer.Tick += (_, _) => Check();

        ctx.Settings.Changed += OnSettingChanged;
        QuietState.Changed += OnQuietChanged;
        PrivacyState.Changed += OnPresentingChanged;

        ctx.AddCommand(new NudgeCommands(this));
        ctx.AddPipeCommand("nudges-state", _ => State());
        ctx.AddPipeCommand("nudges-fire", arg =>
        {
            switch (arg.Trim().ToLowerInvariant())
            {
                case "eye": ShowNow(NudgeKind.EyeBreak); break;
                case "water": ShowNow(NudgeKind.Water); break;
                default: return "error: usage nudges-fire eye|water";
            }
            return State();
        });
        ctx.AddPipeCommand("nudges-advance", arg =>
        {
            if (!double.TryParse(arg.Trim(), NumberStyles.Float, Inv, out double minutes) || minutes is < 0 or > 1440)
                return "error: usage nudges-advance <minutes>";
            _scheduler.Advance(TimeSpan.FromMinutes(minutes));
            Check();
            return State();
        });
        ctx.AddPipeCommand("nudges-idle", arg =>
        {
            if (!double.TryParse(arg.Trim(), NumberStyles.Float, Inv, out double seconds) || seconds is < 0 or > 86400)
                return "error: usage nudges-idle <seconds>";
            _pretendIdle = TimeSpan.FromSeconds(seconds);
            return State();
        });
        ctx.AddPipeCommand("nudges-check", _ =>
        {
            Check();
            return State();
        });

        Apply();
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_ctx is null) return;
        _ctx.Settings.Changed -= OnSettingChanged;
        QuietState.Changed -= OnQuietChanged;
        PrivacyState.Changed -= OnPresentingChanged;
    }

    bool EyeBreaksOn => _ctx.Settings.EyeBreaksEnabled;
    bool WaterOn => _ctx.Settings.WaterRemindersEnabled;

    // ---- Checking ---------------------------------------------------------------------------------

    /// <summary>Every 30 s while either nudge is on: count the time, and show whichever fell due if the moment is free.</summary>
    void Check()
    {
        try
        {
            var idle = _pretendIdle ?? LastInput.Idle() ?? IdleUnknown();
            _pretendIdle = null;
            _lastIdle = idle;
            if (_scheduler.Check(DateTime.UtcNow, idle, Busy() is null, EyeBreaksOn, WaterOn) is { } kind) Show(kind);
        }
        catch (Exception ex)
        {
            _ctx.Warn("Checking the nudges failed", ex);
        }
    }

    /// <summary>Windows wouldn't say when the last input was: count the time as used, so the breaks still come.</summary>
    TimeSpan IdleUnknown()
    {
        if (!_idleWarned) _ctx.Warn("Couldn't read the last input time; counting all time as active");
        _idleWarned = true;
        return TimeSpan.Zero;
    }

    /// <summary>Why a nudge can't show right now, or null when the moment is free.</summary>
    string? Busy()
    {
        if (QuietState.IsQuiet) return "quiet:" + QuietState.Describe().Replace(' ', '_');
        if (PrivacyState.Presenting) return "presenting";
        var mode = _ctx.Notch.Mode;
        if (_ctx.Notch.IsFullscreen || mode == NotchMode.Hidden) return "fullscreen";
        // Anything open or in the way: a card, the open notch, the peek, a notification, typing, a search, a drop.
        return mode is NotchMode.Idle or NotchMode.Compact ? null : "notch:" + mode;
    }

    // ---- Showing ----------------------------------------------------------------------------------

    void Show(NudgeKind kind)
    {
        var now = DateTime.UtcNow;
        if (kind == NudgeKind.EyeBreak)
        {
            _ctx.Remove(WaterId);
            _ctx.Post(new Activity(EyeId, ActivityKind.Custom, Priority,
                new EyeBreakContent(now, () => _ctx.Remove(EyeId)), now + EyeBreakStays));
        }
        else
        {
            _ctx.Remove(EyeId);
            _ctx.Post(new Activity(WaterId, ActivityKind.Custom, Priority,
                new WaterContent(() => _water.Today(DateTime.Now), Drank), now + NudgeText.WaterStay));
        }
    }

    /// <summary>Asked for by hand (a command, the pipe): shows whatever else is going on, and its count starts again.</summary>
    void ShowNow(NudgeKind kind)
    {
        _scheduler.Shown(kind, DateTime.UtcNow);
        Show(kind);
    }

    void Drank()
    {
        _water.Add(DateTime.Now);
        _ctx.Save(_water);
        _ctx.Remove(WaterId);
    }

    void Hide()
    {
        _ctx.Remove(EyeId);
        _ctx.Remove(WaterId);
    }

    // ---- Settings and the states that silence nudges ------------------------------------------------

    void OnSettingChanged(string name)
    {
        if (name is nameof(AppSettings.EyeBreaksEnabled) or nameof(AppSettings.WaterRemindersEnabled)) _ctx.Ui.BeginInvoke(Apply);
    }

    /// <summary>A switch takes effect at once: a nudge turned off leaves the notch and forgets its count.</summary>
    void Apply()
    {
        if (!EyeBreaksOn) _ctx.Remove(EyeId);
        if (!WaterOn) _ctx.Remove(WaterId);
        // Also starts the clock for one just turned on, and clears the count of one turned off.
        Check();
        _timer.IsEnabled = EyeBreaksOn || WaterOn;
    }

    // A call, a meeting or a focus session started while a nudge was up.
    void OnQuietChanged(bool quiet)
    {
        if (quiet) Hide();
    }

    // Raised on whichever thread noticed the screen being shared.
    void OnPresentingChanged(bool presenting)
    {
        if (presenting) _ctx.Ui.BeginInvoke(Hide);
    }

    void Set(string setting, bool on)
    {
        if (SettingsCatalog.Find(setting) is { } info) SettingsCatalog.Set(_ctx.Settings, info, on);
    }

    // ---- Commands ---------------------------------------------------------------------------------

    CommandResult Run(NudgeCommand command)
    {
        switch (command)
        {
            case NudgeCommand.EyeBreaksOn:
                Set(nameof(AppSettings.EyeBreaksEnabled), true);
                return new CommandResult(EyeGlyph, "Eye breaks on", "Every 20 minutes, for 20 seconds");
            case NudgeCommand.EyeBreaksOff:
                Set(nameof(AppSettings.EyeBreaksEnabled), false);
                return new CommandResult(EyeGlyph, "Eye breaks off");
            case NudgeCommand.WaterOn:
                Set(nameof(AppSettings.WaterRemindersEnabled), true);
                return new CommandResult(WaterGlyph, "Water reminders on", "Every hour and a half");
            case NudgeCommand.WaterOff:
                Set(nameof(AppSettings.WaterRemindersEnabled), false);
                return new CommandResult(WaterGlyph, "Water reminders off");
            default:
                // Once this confirmation has closed, so the 20 seconds don't start behind it.
                _ctx.Ui.BeginInvoke(() => _ctx.Notch.AfterSearch(() => ShowNow(NudgeKind.EyeBreak)), DispatcherPriority.Background);
                return new CommandResult(EyeGlyph, "Eye break", "Look away for 20 seconds");
        }
    }

    static CommandPreview Preview(NudgeCommand command) => command switch
    {
        NudgeCommand.EyeBreaksOn => new(EyeGlyph, "Turn eye breaks on", "Every 20 minutes, for 20 seconds"),
        NudgeCommand.EyeBreaksOff => new(EyeGlyph, "Turn eye breaks off"),
        NudgeCommand.WaterOn => new(WaterGlyph, "Turn water reminders on", "Every hour and a half"),
        NudgeCommand.WaterOff => new(WaterGlyph, "Turn water reminders off"),
        _ => new(EyeGlyph, "Take an eye break", "Look away for 20 seconds"),
    };

    sealed class NudgeCommands(NudgesModule module) : ITextCommand, ICommandPreview
    {
        public bool CanHandle(string text) => NudgeText.Parse(text) is not null;

        public Task<CommandResult> RunAsync(string text) =>
            Task.FromResult(NudgeText.Parse(text) is { } command
                ? module.Run(command)
                : new CommandResult(EyeGlyph, "That didn't work", IsError: true));

        public CommandPreview? Preview(string text) => NudgeText.Parse(text) is { } command ? NudgesModule.Preview(command) : null;
    }

    // ---- Pipe ---------------------------------------------------------------------------------------

    string State()
    {
        var s = _scheduler;
        var now = DateTime.Now;
        string busy = Busy() ?? "-";
        string showing = _ctx.Get(EyeId) is not null ? "eye" : _ctx.Get(WaterId) is not null ? "water" : "-";
        return string.Create(Inv,
            $"eye={OnOff(EyeBreaksOn)} water={OnOff(WaterOn)} " +
            $"eyeActiveMin={s.EyeActive.TotalMinutes:0.0} waterActiveMin={s.WaterActive.TotalMinutes:0.0} " +
            $"eyeNext={Next(EyeBreaksOn, s.EyeLeft, s.EyeDueSince, now)} waterNext={Next(WaterOn, s.WaterLeft, s.WaterDueSince, now)} " +
            $"glasses={_water.Today(now)} eyeWaiting={Waiting(s.EyeWait, busy)} waterWaiting={Waiting(s.WaterWait, busy)} " +
            $"busy={busy} skipped={s.EyeSkipped}/{s.WaterSkipped} showing={showing} " +
            $"idleSec={_lastIdle.TotalSeconds:0} pretendIdle={(_pretendIdle is { } p ? p.TotalSeconds.ToString("0", Inv) : "-")} " +
            $"timer={OnOff(_timer.IsEnabled)} notch={_ctx.Notch.Mode}");
    }

    static string OnOff(bool on) => on ? "on" : "off";

    /// <summary>"off", "due@14:20:05" while it waits, or the local time it falls due if the PC stays in use.</summary>
    static string Next(bool on, TimeSpan left, DateTime? dueSinceUtc, DateTime now) =>
        !on ? "off"
        : dueSinceUtc is { } due ? "due@" + due.ToLocalTime().ToString("HH:mm:ss", Inv)
        : (now + left).ToString("HH:mm:ss", Inv);

    static string Waiting(NudgeWait wait, string busy) => wait switch
    {
        NudgeWait.Busy => busy == "-" ? "busy" : busy,
        NudgeWait.TooSoon => "too-soon-after-another",
        NudgeWait.EyeBreakFirst => "eye-break-first",
        _ => "-",
    };

    /// <summary>How long ago the last key press or mouse move was, for the whole session.</summary>
    static class LastInput
    {
        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        public static TimeSpan? Idle()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return null;
            // Both are the 32-bit tick count, which wraps every 49 days; the unsigned difference stays right.
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
        }
    }
}
