namespace Shammy.Core.Notch;

/// <summary>
/// A bottom corner of the notch as one smooth curve instead of a quarter circle: it starts a little earlier on
/// each edge and eases into the bend, so the edge never seems to "kink" where the corner begins (the look of the
/// hardware cut-out it imitates). The bend itself passes where the circle's would, so sizes read the same.
/// </summary>
public static class SmoothCorner
{
    /// <summary>How much further than its radius the curve reaches along each edge, when there is room.</summary>
    public const double Reach = 1.28;

    /// <summary>Control handle of a quarter circle drawn as a cubic curve, as a share of its extent from the corner.</summary>
    public const double CircleHandle = 0.4477;

    /// <summary>The tighter handle of the full smooth corner; chosen so its middle meets the circle's middle.</summary>
    public const double SmoothHandle = 0.277;

    /// <summary>
    /// For a corner of <paramref name="radius"/> with <paramref name="room"/> along each edge: how far from the
    /// corner the curve starts (<c>Extent</c>) and how far from the corner its two control points sit
    /// (<c>Handle</c>). With no spare room it is exactly the old quarter circle.
    /// </summary>
    public static (double Extent, double Handle) For(double radius, double room)
    {
        if (radius <= 0) return (0, 0);
        double extent = Math.Max(radius, Math.Min(radius * Reach, room));
        double smooth = Math.Clamp((extent / radius - 1) / (Reach - 1), 0, 1);
        return (extent, extent * (CircleHandle - (CircleHandle - SmoothHandle) * smooth));
    }

    /// <summary>How far from the corner (along each axis) the middle of the curve passes.</summary>
    public static double Middle(double extent, double handle) => 0.125 * extent + 0.375 * handle;
}

/// <summary>
/// The pop-ups waiting under the one on screen. A new message goes on top and the one it covered waits
/// underneath; sending the top one away brings the last covered one back. When the top one simply times out,
/// everything under it goes too (they are all in the Alerts list).
/// </summary>
public sealed class PopupStack<T> where T : class
{
    /// <summary>At most this many wait underneath; older ones drop off the bottom.</summary>
    public const int Limit = 9;

    readonly List<T> _under = new();

    public int Count => _under.Count;

    /// <summary>
    /// A new pop-up arrived while <paramref name="shown"/> was on screen (null: nothing was). Returns how many
    /// now wait under the new one.
    /// </summary>
    public int Arrived(T? shown)
    {
        if (shown is null)
        {
            _under.Clear();
            return 0;
        }
        _under.Add(shown);
        if (_under.Count > Limit) _under.RemoveAt(0);
        return _under.Count;
    }

    /// <summary>The top one was sent away: the one to show now, or null when none waits.</summary>
    public T? Dismissed()
    {
        if (_under.Count == 0) return null;
        var next = _under[^1];
        _under.RemoveAt(_under.Count - 1);
        return next;
    }

    public void Clear() => _under.Clear();

    public int RemoveAll(Predicate<T> match) => _under.RemoveAll(match);

    /// <summary>How many cards peek out from under the top one: one or two, however many wait.</summary>
    public static int Peeking(int waiting) => Math.Clamp(waiting, 0, 2);
}

/// <summary>Sending a pop-up away by dragging it sideways.</summary>
public static class SwipeAway
{
    /// <summary>The pointer must travel this far before it is a drag and not a click.</summary>
    public const double Start = 6;

    /// <summary>Let go this far from where it started and the card leaves.</summary>
    public const double Leave = 90;

    /// <summary>A quick flick leaves from much closer.</summary>
    public const double FlickTravel = 30, FlickSpeed = 900;

    /// <summary>Whether a release at <paramref name="dx"/> pixels, moving at <paramref name="pixelsPerSecond"/>, sends it away.</summary>
    public static bool Leaves(double dx, double pixelsPerSecond)
    {
        if (Math.Abs(dx) >= Leave) return true;
        bool sameWay = Math.Sign(dx) == Math.Sign(pixelsPerSecond);
        return sameWay && Math.Abs(dx) >= FlickTravel && Math.Abs(pixelsPerSecond) >= FlickSpeed;
    }

    /// <summary>The card fades as it is dragged, but never so far that you lose sight of it.</summary>
    public static double Opacity(double dx, double cardWidth) =>
        cardWidth <= 0 ? 1 : Math.Clamp(1 - Math.Abs(dx) / (cardWidth * 0.6), 0.15, 1);
}

/// <summary>Small things the pet does when something real happens (all skipped with Calm motion).</summary>
public static class PetMoments
{
    /// <summary>It yawns and stretches as it nods off for the night.</summary>
    public static bool Yawns(PetMood from, PetMood to) => from == PetMood.Idle && to == PetMood.Sleepy;

    /// <summary>It starts awake, ears up and pupils wide, when the pointer wakes it.</summary>
    public static bool WakesUp(PetMood from, PetMood to) => from == PetMood.Sleepy && to == PetMood.Idle;

    /// <summary>Coming back to the PC (unlocking it) is greeted, but not more than once every ten minutes.</summary>
    public static readonly TimeSpan GreetEvery = TimeSpan.FromMinutes(10);

    public static bool Greets(DateTime now, DateTime? lastGreeting) => lastGreeting is not { } last || now - last >= GreetEvery;
}
