namespace Shammy.Core.Nudges;

public enum NudgeKind { EyeBreak, Water }

/// <summary>Why a nudge that is due hasn't shown yet.</summary>
public enum NudgeWait
{
    /// <summary>Not due, or it just showed.</summary>
    None,
    /// <summary>The moment isn't free (a call, a shared screen, a fullscreen app, another card).</summary>
    Busy,
    /// <summary>Another nudge showed less than two minutes ago.</summary>
    TooSoon,
    /// <summary>Water waits for the eye break that is due at the same time.</summary>
    EyeBreakFirst,
}

/// <summary>
/// When the pet's small nudges show. Time counts only while the PC is being used: each check says how long ago
/// the last key or mouse input was, and a check credits the time since the one before it unless that input is
/// more than two minutes old. Five minutes without input is a break already taken, so the eye-break count starts
/// again. A nudge that falls due at a bad moment waits for the first free check, for up to ten minutes, and is
/// then skipped until its next round. Everything is driven by the caller's clock, so it can be tested.
/// </summary>
public sealed class NudgeScheduler
{
    /// <summary>Active screen time between eye breaks.</summary>
    public static readonly TimeSpan EyeEvery = TimeSpan.FromMinutes(20);

    /// <summary>Active time between water reminders.</summary>
    public static readonly TimeSpan WaterEvery = TimeSpan.FromMinutes(90);

    /// <summary>Input older than this pauses the count.</summary>
    public static readonly TimeSpan IdlePause = TimeSpan.FromMinutes(2);

    /// <summary>Input this old (or older) is a break taken.</summary>
    public static readonly TimeSpan IdleBreak = TimeSpan.FromMinutes(5);

    /// <summary>How long a due nudge waits for a free moment before its round is skipped.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);

    /// <summary>The least time between two nudges.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The most one check credits. Checks come every 30 s; a much longer gap means the PC slept or the app was
    /// held up, and that time wasn't spent looking at the screen.
    /// </summary>
    public static readonly TimeSpan MaxCredit = TimeSpan.FromMinutes(1);

    DateTime? _lastCheck, _lastShown;

    /// <summary>Active time counted toward the next eye break (stops at <see cref="EyeEvery"/> while it waits).</summary>
    public TimeSpan EyeActive { get; private set; }

    /// <summary>Active time counted toward the next water reminder.</summary>
    public TimeSpan WaterActive { get; private set; }

    /// <summary>When the eye break fell due, while it waits to show; null otherwise.</summary>
    public DateTime? EyeDueSince { get; private set; }

    public DateTime? WaterDueSince { get; private set; }

    public NudgeWait EyeWait { get; private set; }

    public NudgeWait WaterWait { get; private set; }

    /// <summary>Rounds given up because no free moment came within <see cref="MaxWait"/>.</summary>
    public int EyeSkipped { get; private set; }

    public int WaterSkipped { get; private set; }

    /// <summary>Active time still to go before the eye break is due.</summary>
    public TimeSpan EyeLeft => Left(EyeEvery, EyeActive);

    public TimeSpan WaterLeft => Left(WaterEvery, WaterActive);

    /// <summary>
    /// One check. <paramref name="idle"/> is the time since the last input, <paramref name="free"/> whether a
    /// nudge may show right now. Returns the nudge to show, or null. At most one per check, the eye break first.
    /// </summary>
    public NudgeKind? Check(DateTime utcNow, TimeSpan idle, bool free, bool eyeBreaks, bool water)
    {
        var elapsed = _lastCheck is { } last && utcNow > last ? utcNow - last : TimeSpan.Zero;
        _lastCheck = utcNow;

        if (!eyeBreaks) ClearEye();
        if (!water) ClearWater();

        // No input for five minutes, or no check for that long (the PC slept): the eyes have had their rest.
        bool away = idle >= IdleBreak || elapsed >= IdleBreak;
        if (away) ClearEye();
        else if (idle <= IdlePause)
        {
            var credit = elapsed < MaxCredit ? elapsed : MaxCredit;
            if (eyeBreaks) EyeActive = Capped(EyeActive + credit, EyeEvery);
            if (water) WaterActive = Capped(WaterActive + credit, WaterEvery);
        }

        if (eyeBreaks && EyeDueSince is null && EyeActive >= EyeEvery) EyeDueSince = utcNow;
        if (water && WaterDueSince is null && WaterActive >= WaterEvery) WaterDueSince = utcNow;

        bool tooSoon = _lastShown is { } shown && utcNow - shown < Gap;
        EyeWait = WaterWait = NudgeWait.None;

        if (EyeDueSince is { } eyeDue)
        {
            if (free && !tooSoon)
            {
                Shown(NudgeKind.EyeBreak, utcNow);
                // Water that is due too waits its two minutes.
                if (WaterDueSince is not null) WaterWait = NudgeWait.TooSoon;
                return NudgeKind.EyeBreak;
            }
            if (!free && utcNow - eyeDue >= MaxWait)
            {
                ClearEye();
                EyeSkipped++;
            }
            else EyeWait = free ? NudgeWait.TooSoon : NudgeWait.Busy;
        }

        if (WaterDueSince is { } waterDue)
        {
            bool eyeFirst = EyeDueSince is not null;
            if (free && !tooSoon && !eyeFirst)
            {
                Shown(NudgeKind.Water, utcNow);
                return NudgeKind.Water;
            }
            // Only a busy moment uses up the wait; the two minutes after another nudge always end.
            if (!free && utcNow - waterDue >= MaxWait)
            {
                ClearWater();
                WaterSkipped++;
            }
            else WaterWait = !free ? NudgeWait.Busy : eyeFirst ? NudgeWait.EyeBreakFirst : NudgeWait.TooSoon;
        }
        return null;
    }

    /// <summary>A nudge was put on screen (by a check, or by hand): its count starts again.</summary>
    public void Shown(NudgeKind kind, DateTime utcNow)
    {
        if (kind == NudgeKind.EyeBreak) ClearEye();
        else ClearWater();
        _lastShown = utcNow;
    }

    /// <summary>Counts <paramref name="active"/> more active time at once (a test hook: "20 minutes passed").</summary>
    public void Advance(TimeSpan active)
    {
        if (active <= TimeSpan.Zero) return;
        EyeActive = Capped(EyeActive + active, EyeEvery);
        WaterActive = Capped(WaterActive + active, WaterEvery);
    }

    void ClearEye()
    {
        EyeActive = TimeSpan.Zero;
        EyeDueSince = null;
        EyeWait = NudgeWait.None;
    }

    void ClearWater()
    {
        WaterActive = TimeSpan.Zero;
        WaterDueSince = null;
        WaterWait = NudgeWait.None;
    }

    static TimeSpan Capped(TimeSpan value, TimeSpan max) => value < max ? value : max;

    static TimeSpan Left(TimeSpan every, TimeSpan counted) => counted < every ? every - counted : TimeSpan.Zero;
}
