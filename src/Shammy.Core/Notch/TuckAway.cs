namespace Shammy.Core.Notch;

/// <summary>
/// Tells a deliberate push up into the notch from a pointer that only reached the top of the screen. The pointer
/// stops at the edge, but the mouse keeps reporting the hand's movement; upward movement that goes on, without a
/// pause, for <see cref="Hold"/> while the pointer sits on the notch at the very top is a push. Moving down, a
/// pause, or leaving the edge starts it over. One push per <see cref="Cooldown"/>.
/// </summary>
public sealed class PushDetector
{
    /// <summary>How long the push has to go on.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(300);

    /// <summary>A longer pause between upward movements ends the push.</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromMilliseconds(120);

    /// <summary>After a push, the next one counts only after this long.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(1);

    /// <summary>Upward movement it needs in all, in the mouse's own counts (keeps a resting hand's jitter out).</summary>
    public const int MinTravel = 30;

    TimeSpan? _start, _last, _quietUntil;
    int _travel;

    /// <summary>A push is under way: the notch should not peek or open meanwhile.</summary>
    public bool InProgress(TimeSpan now) => _last is { } last && now - last <= MaxGap;

    public void Reset()
    {
        _start = _last = null;
        _travel = 0;
    }

    /// <summary>
    /// One movement report: <paramref name="dy"/> is the vertical movement (negative is up), <paramref name="atEdge"/>
    /// whether the pointer is on the notch at the very top of the screen. True once, when the push is complete.
    /// </summary>
    public bool Add(int dy, bool atEdge, TimeSpan now)
    {
        if (!atEdge || dy > 0)
        {
            Reset();
            return false;
        }
        // Sideways only: neither part of the push nor a reason to stop it.
        if (dy == 0) return false;
        if (_quietUntil is { } quiet && now < quiet) return false;
        if (_last is not { } last || now - last > MaxGap || now < last)
        {
            _start = now;
            _travel = 0;
        }
        _last = now;
        _travel += -dy;
        if (now - _start!.Value < Hold || _travel < MinTravel) return false;
        Reset();
        _quietUntil = now + Cooldown;
        return true;
    }
}

/// <summary>How long the notch stays tucked away once pushed.</summary>
public static class TuckAway
{
    public static readonly TimeSpan Away = TimeSpan.FromSeconds(5);
}
