namespace Shammy.Core.Wheel;

/// <summary>
/// Which droplet is chosen while the wheel is open. Three things choose: the mouse wheel turns one droplet per
/// notch (rolling toward you turns clockwise), pointing (a flick toward a droplet) picks it, and a key picks one
/// outright. Pointing only counts when the pointer moves into a different sector, so a droplet chosen by turning
/// stays chosen while the pointer rests where it was; moving back into the hub clears the choice (letting go then
/// cancels).
/// </summary>
public sealed class WheelDial
{
    /// <summary>One notch of a standard mouse wheel (WHEEL_DELTA); finer wheels send parts of it.</summary>
    public const int Notch = 120;

    int _pointer = -1, _rest;

    public WheelDial(int count) => Count = Math.Max(0, count);

    public int Count { get; private set; }

    /// <summary>The chosen droplet, or -1 (none: letting go cancels).</summary>
    public int Selected { get; private set; } = -1;

    public void Reset(int count)
    {
        Count = Math.Max(0, count);
        Selected = -1;
        _pointer = -1;
        _rest = 0;
    }

    /// <summary>A mouse wheel event's delta (positive = away from you). Returns the droplets turned (positive = clockwise).</summary>
    public int Scroll(int delta)
    {
        if (Count == 0 || delta == 0) return 0;
        // Turning back the other way starts a fresh notch instead of first paying back the remainder.
        if (_rest != 0 && Math.Sign(_rest) != Math.Sign(delta)) _rest = 0;
        _rest += delta;
        int notches = _rest / Notch;
        _rest -= notches * Notch;
        int steps = -notches;
        if (steps != 0) Selected = Step(Selected, steps, Count);
        return steps;
    }

    /// <summary>The pointer's offset from the hub. True when that changed the choice.</summary>
    public bool Point(double dx, double dy, double deadZone = WheelGeometry.DeadZone)
    {
        int pick = WheelGeometry.Pick(dx, dy, deadZone, Count);
        if (pick == _pointer) return false;
        _pointer = pick;
        if (pick == Selected) return false;
        Selected = pick;
        return true;
    }

    /// <summary>Chooses a droplet outright (a key, the test pipe); out of range clears the choice.</summary>
    public void Choose(int droplet) => Selected = droplet >= 0 && droplet < Count ? droplet : -1;

    /// <summary>
    /// <paramref name="steps"/> droplets on from <paramref name="selected"/>, wrapping round. From nothing chosen the
    /// first step clockwise lands on the top droplet and the first step back on the last.
    /// </summary>
    public static int Step(int selected, int steps, int count)
    {
        if (count <= 0) return -1;
        if (steps == 0) return selected;
        if (selected < 0 || selected >= count)
        {
            selected = steps > 0 ? 0 : count - 1;
            steps -= Math.Sign(steps);
        }
        return ((selected + steps) % count + count) % count;
    }
}
