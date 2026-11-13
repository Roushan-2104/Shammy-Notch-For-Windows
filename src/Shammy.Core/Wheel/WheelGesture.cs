namespace Shammy.Core.Wheel;

/// <summary>What the mouse hook should do with a middle-button event, and what follows from it.</summary>
public enum WheelStep
{
    /// <summary>Let the event through untouched.</summary>
    Pass,
    /// <summary>Swallow it; nothing else happens yet.</summary>
    Swallow,
    /// <summary>Swallow it and send the app the middle click it would have had (a quick click).</summary>
    SwallowAndReplayClick,
    /// <summary>Let this move through, after sending the app the middle-button press held back (a drag, auto-scroll).</summary>
    ReplayDownThenPass,
    /// <summary>Swallow it: the wheel was open and the button came up, so run what is chosen.</summary>
    SwallowAndRelease,
}

/// <summary>
/// The middle-button hold, decided event by event so the low-level hook never waits: a press is held back; let go
/// within <see cref="Hold"/> and the app gets its click after all; move more than <see cref="Slop"/> first and it
/// gets the press (a drag or browser auto-scroll); keep it still past <see cref="Hold"/> and the wheel opens, and
/// the release is Shammy's. Thread-safe: the hook thread and the UI thread's timer share it.
/// </summary>
public sealed class WheelGesture
{
    public static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(250);
    public const int Slop = 6;

    public enum Phase { Idle, Pending, Dragging, Open }

    readonly object _gate = new();
    Phase _phase = Phase.Idle;
    int _x, _y;
    long _downMs;

    public Phase Current { get { lock (_gate) return _phase; } }

    /// <summary>Where the press happened (screen pixels).</summary>
    public (int X, int Y) DownAt { get { lock (_gate) return (_x, _y); } }

    /// <summary>The middle button went down. <paramref name="enabled"/> false (switched off, a fullscreen game) lets it through.</summary>
    public WheelStep Down(int x, int y, long ms, bool enabled)
    {
        lock (_gate)
        {
            if (!enabled)
            {
                _phase = Phase.Idle;
                return WheelStep.Pass;
            }
            _phase = Phase.Pending;
            _x = x;
            _y = y;
            _downMs = ms;
            return WheelStep.Swallow;
        }
    }

    /// <summary>The pointer moved (any time). Only a move while the press is held back changes anything.</summary>
    public WheelStep Move(int x, int y)
    {
        lock (_gate)
        {
            if (_phase != Phase.Pending) return WheelStep.Pass;
            if (Math.Abs(x - _x) <= Slop && Math.Abs(y - _y) <= Slop) return WheelStep.Pass;
            _phase = Phase.Dragging;
            return WheelStep.ReplayDownThenPass;
        }
    }

    /// <summary>The middle button came up.</summary>
    public WheelStep Up(long ms)
    {
        lock (_gate)
        {
            var was = _phase;
            _phase = Phase.Idle;
            return was switch
            {
                // Still held back (the wheel never showed, even if the timer was late): the app gets its click.
                Phase.Pending => WheelStep.SwallowAndReplayClick,
                Phase.Open => WheelStep.SwallowAndRelease,
                _ => WheelStep.Pass,
            };
        }
    }

    /// <summary>The hold time ran out (from a timer). True when the wheel should open now.</summary>
    public bool HoldElapsed(long ms)
    {
        lock (_gate)
        {
            if (_phase != Phase.Pending || ms - _downMs < Hold.TotalMilliseconds - 5) return false;
            _phase = Phase.Open;
            return true;
        }
    }

    /// <summary>The wheel closed some other way (Esc, a key, it was switched off).</summary>
    public void Reset()
    {
        lock (_gate) _phase = Phase.Idle;
    }
}

/// <summary>Which of the droplets a pointer offset points at: 0 is straight up, then clockwise; -1 near the centre.</summary>
public static class WheelGeometry
{
    /// <summary>How many droplets the wheel shows (and beads it keeps).</summary>
    public const int Beads = 6;
    public const double DeadZone = 30;

    public static int Pick(double dx, double dy, double deadZone = DeadZone, int count = Beads)
    {
        if (count <= 0 || Math.Sqrt(dx * dx + dy * dy) < deadZone) return -1;
        double degrees = Math.Atan2(dy, dx) * 180 / Math.PI + 90;
        int i = (int)Math.Round(degrees / (360.0 / count));
        return ((i % count) + count) % count;
    }

    /// <summary>The droplet's direction on screen (0 = up, clockwise) as a unit vector.</summary>
    public static (double X, double Y) Direction(int bead, int count = Beads)
    {
        double a = (-90 + bead * 360.0 / Math.Max(1, count)) * Math.PI / 180;
        return (Math.Cos(a), Math.Sin(a));
    }
}
