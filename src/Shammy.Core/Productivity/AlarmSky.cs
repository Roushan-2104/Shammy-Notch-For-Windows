namespace Shammy.Core.Productivity;

/// <summary>The sky an alarm rings in, from the hour it rings: the sky outside at that time.</summary>
public enum Sky { Dawn, Day, Sunset, Night }

/// <summary>
/// The ringing alarm's sky follows the clock (dawn 5–9 AM, day 9 AM–4 PM, sunset 4–7 PM, night 7 PM–5 AM), rises over
/// its first 30 seconds, and warms the resting island for the two minutes before it rings.
/// </summary>
public static class AlarmSky
{
    /// <summary>How long the sky takes to come up once the alarm rings.</summary>
    public static readonly TimeSpan RiseTime = TimeSpan.FromSeconds(30);

    /// <summary>How long before an alarm the resting island starts to glow.</summary>
    public static readonly TimeSpan PreGlow = TimeSpan.FromMinutes(2);

    public static Sky At(int hour) => hour switch
    {
        >= 5 and < 9 => Sky.Dawn,
        >= 9 and < 16 => Sky.Day,
        >= 16 and < 19 => Sky.Sunset,
        _ => Sky.Night,
    };

    /// <summary>How far the sky has come up (0 to 1) after ringing this long.</summary>
    public static double Risen(TimeSpan ringing) => Math.Clamp(ringing / RiseTime, 0, 1);

    /// <summary>The earliest upcoming ring within <see cref="PreGlow"/> of now (the island should glow), else null.</summary>
    public static DateTime? Glowing(IEnumerable<DateTime?> upcomingUtc, DateTime nowUtc) =>
        upcomingUtc.Where(t => t is { } at && at > nowUtc && at - nowUtc <= PreGlow).Min();
}
