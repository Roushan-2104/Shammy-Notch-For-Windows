using Shammy.Core.Wheel;

namespace Shammy.Core.Notch;

/// <summary>One thing on the island's second page: a wheel action to run, or a card to open.</summary>
public sealed record Essential(string Id, string Name, bool IsCard);

/// <summary>
/// The island's second page ("everything, without searching"): the features you use most as big tiles, and every
/// other feature below, A to Z. Usage is the wheel's own record (picks and commands, fading with its two-week
/// half-life), so the page and the wheel learn from the same uses.
/// </summary>
public static class Essentials
{
    /// <summary>How many big tiles the top row holds.</summary>
    public const int TopCount = 6;

    // Moment-only droplets that would only repeat something already on the page, or only make sense mid-moment.
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "playpause", "next", "previous", "stage", "musiccard", "share" };

    /// <summary>
    /// Every feature once: the wheel's actions and the island's cards. A card and an action with the same id are one
    /// entry, the card (opening it is what a launcher should do; its uses still count).
    /// </summary>
    public static IReadOnlyList<Essential> Catalog(IEnumerable<(string Id, string Name)> cards)
    {
        var byId = new Dictionary<string, Essential>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in WheelSlots.All.Concat(WheelSlots.MomentOnly))
        {
            if (Skipped.Contains(a.Id) || byId.ContainsKey(a.Id)) continue;
            byId[a.Id] = new Essential(a.Id, a.Name, false);
        }
        foreach (var (id, name) in cards)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
            byId[id] = new Essential(id, name.Trim(), true);
        }
        return byId.Values.ToList();
    }

    /// <summary>
    /// The page: the <paramref name="top"/> most used (with any use at all, most first; topped up from the wheel's
    /// defaults while there is little history), then everything else A to Z.
    /// </summary>
    public static (IReadOnlyList<Essential> Most, IReadOnlyList<Essential> Others) Arrange(IReadOnlyList<Essential> all,
        IReadOnlyDictionary<string, IReadOnlyList<DateTime>> uses, DateTime now, int top = TopCount)
    {
        double Score(Essential e) => uses.TryGetValue(e.Id, out var times) ? times.Sum(t => WheelSlots.Weight(t, now)) : 0;
        int DefaultRank(Essential e)
        {
            int i = IndexOf(WheelSlots.Defaults, e.Id);
            return i < 0 ? int.MaxValue : i;
        }

        var most = all.Select(e => (e, s: Score(e))).Where(x => x.s > 0)
            .OrderByDescending(x => x.s).ThenBy(x => DefaultRank(x.e)).ThenBy(x => x.e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.e).Take(top).ToList();
        foreach (var e in all.OrderBy(DefaultRank).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (most.Count >= top) break;
            if (DefaultRank(e) == int.MaxValue) break;
            if (!most.Contains(e)) most.Add(e);
        }
        var rest = all.Where(e => !most.Contains(e)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return (most, rest);
    }

    static int IndexOf(IReadOnlyList<string> list, string id)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}

/// <summary>
/// Turns a stream of sideways scroll (a touchpad swipe, a tilt wheel, Shift + wheel) into whole page moves: one
/// move per gesture, however many small deltas the swipe sends, and nothing again until the fingers have rested.
/// </summary>
public sealed class PageSwipe
{
    /// <summary>Sideways scroll needed for one page (a mouse notch is 120).</summary>
    public const int Threshold = 120;

    /// <summary>Quiet time that ends a gesture.</summary>
    public static readonly TimeSpan Rest = TimeSpan.FromMilliseconds(350);

    int _sum;
    bool _spent;
    DateTime _last = DateTime.MinValue;

    /// <summary>Adds a delta (positive = towards the right); +1 or −1 when it completes a page move, else 0.</summary>
    public int Add(int delta, DateTime now)
    {
        if (now - _last > Rest)
        {
            _sum = 0;
            _spent = false;
        }
        _last = now;
        if (_spent) return 0;
        _sum += delta;
        if (Math.Abs(_sum) < Threshold) return 0;
        _spent = true;
        return Math.Sign(_sum);
    }
}
