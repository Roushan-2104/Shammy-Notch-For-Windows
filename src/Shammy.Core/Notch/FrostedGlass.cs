using Shammy.Core.Music;

namespace Shammy.Core.Notch;

/// <summary>Where frosted glass stands: off by choice, showing, paused for a while, or unable to run here.</summary>
public enum GlassState { Off, On, Paused, Unavailable }

/// <summary>Everything that decides whether frosted glass may show right now.</summary>
/// <param name="Available">This Windows can blur behind a window for us.</param>
/// <param name="BatteryPercent">0-100, or -1 when there's no battery (a desktop) or it can't be read.</param>
public sealed record GlassConditions(
    bool SwitchOn,
    bool Available,
    bool OnAc,
    int BatteryPercent,
    bool EnergySaver,
    bool Transparency,
    bool Fullscreen,
    bool HighContrast = false);

/// <summary>The decision and the one line Settings shows under the switch.</summary>
public readonly record struct GlassDecision(GlassState State, string Status)
{
    public bool Runs => State == GlassState.On;
}

/// <summary>
/// The rules for frosted glass (opt-in): Windows blurs whatever is behind the island. What has to be true for it to
/// show, and the line that says why when it doesn't. Anything that isn't fine falls back to the island's usual glass.
/// </summary>
public static class FrostedGlassRules
{
    /// <summary>On battery it shows only above this charge.</summary>
    public const int MinBattery = 50;

    public static GlassDecision Decide(GlassConditions c)
    {
        if (!c.SwitchOn) return new(GlassState.Off, "");
        if (!c.Available) return new(GlassState.Unavailable, "Not available on this version of Windows");
        if (c.EnergySaver) return new(GlassState.Paused, "Paused: Energy saver is on");
        if (!c.OnAc && c.BatteryPercent >= 0 && c.BatteryPercent < MinBattery) return new(GlassState.Paused, $"Paused: battery below {MinBattery}%");
        if (!c.Transparency) return new(GlassState.Paused, "Paused: Transparency effects are off in Windows");
        if (c.HighContrast) return new(GlassState.Paused, "Paused: high contrast is on");
        if (c.Fullscreen) return new(GlassState.Paused, "Paused: a full-screen app is in front");
        return new(GlassState.On, "On");
    }

    /// <summary>Under frosted glass notifications colour the waves violet; alerts (a timer or alarm going off, low battery) amber.</summary>
    public static readonly Rgb Notify = new(0xBF, 0x5A, 0xF2), Alert = new(0xFF, 0x9F, 0x0A);

    /// <summary>
    /// The colour the waves take on frosted glass for what's going on (null: the palette's). The glow ring's colours (music's
    /// album colour, focus or a running timer, a live microphone, a meeting soon) plus two of its own; most urgent wins:
    /// an alert, a live microphone, a notification, then whatever the ring shows.
    /// </summary>
    public static Rgb? Moment(bool alert, bool notify, RimState rim, Rgb album) =>
        alert ? Alert : rim == RimState.MicLive ? RimLight.MicLive : notify ? Notify : RimLight.Colour(rim, album);

    /// <summary>The waves' three colours for a moment: its colour, a lighter shade between, its colour again.</summary>
    public static Rgb[] WaveColours(Rgb c)
    {
        static byte Lift(byte v) => (byte)(v + (255 - v) * 0.3);
        return new[] { c, new Rgb(Lift(c.R), Lift(c.G), Lift(c.B)), c };
    }

    /// <summary>Windows 10 version 2004 (build 19041) or newer blurs behind a window the way frosted glass needs.</summary>
    public static bool WindowsBuildOk(int build) => build >= 19041;
}

/// <summary>A rectangle in device pixels (right and bottom exclusive).</summary>
public readonly record struct ScreenBox(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => IsEmpty ? 0 : (long)Width * Height;

    public bool Contains(ScreenBox r) => r.Left >= Left && r.Top >= Top && r.Right <= Right && r.Bottom <= Bottom;

    public ScreenBox Union(ScreenBox r) =>
        new(Math.Min(Left, r.Left), Math.Min(Top, r.Top), Math.Max(Right, r.Right), Math.Max(Bottom, r.Bottom));
}

/// <summary>
/// Where the frosted window goes. It only grows while the pieces move (so only Windows' compositor moves their
/// shapes, in step with the island), and once it is well over their size it should shrink back when they settle
/// (a wing folded away, the island moved to another screen).
/// </summary>
public static class FrostWindowFit
{
    public static (ScreenBox Window, bool ShrinkSoon) Next(ScreenBox current, ScreenBox pieces)
    {
        var window = current.IsEmpty ? pieces : current.Contains(pieces) ? current : current.Union(pieces);
        return (window, window.Area > 1.5 * pieces.Area);
    }
}
