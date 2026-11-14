namespace Shammy.Core.Notch;

/// <summary>
/// The behaviour of Shammy's eyes on the resting notch: when to blink, when to look around, how far to
/// follow the pointer, and when to be sleepy. The view only animates what this decides.
/// </summary>
public static class EyeLogic
{
    /// <summary>The eyes follow the pointer while it is this close (DIPs from the eyes).</summary>
    public const double FollowRadius = 300;

    /// <summary>The furthest the eyes move sideways (DIPs); a little goes a long way at 7 px wide.</summary>
    public const double MaxLook = 2.5;

    /// <summary>Pointer offset (DIPs) at which the eyes are fully turned.</summary>
    const double FullTurnAt = 140;

    /// <summary>Half-closed eyes late at night, 11 PM to 6 AM.</summary>
    public static bool IsSleepy(DateTime local) => local.Hour >= 23 || local.Hour < 6;

    /// <summary>The sideways look towards a pointer at (dx, dy) DIPs from the eyes, or null when it is too far to care.</summary>
    public static double? Follow(double dx, double dy)
    {
        if (dx * dx + dy * dy > FollowRadius * FollowRadius) return null;
        return Math.Clamp(dx / FullTurnAt * MaxLook, -MaxLook, MaxLook);
    }

    /// <summary>About every 5 s, never on a beat (a sleepy Shammy blinks slower).</summary>
    public static TimeSpan NextBlink(Random random, bool sleepy) =>
        TimeSpan.FromSeconds(sleepy ? 7 + random.NextDouble() * 4 : 3.5 + random.NextDouble() * 3);

    /// <summary>One blink in five is a quick double blink.</summary>
    public static bool DoubleBlink(Random random) => random.NextDouble() < 0.2;

    /// <summary>A glance to the side every 6-11 s.</summary>
    public static TimeSpan NextGlance(Random random) => TimeSpan.FromSeconds(6 + random.NextDouble() * 5);

    public static double GlanceDirection(Random random) => random.Next(2) == 0 ? -MaxLook : MaxLook;
}
