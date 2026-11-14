namespace Shammy.Core.CircleMemory;

/// <summary>
/// The remembered circles, newest first, kept to the last <see cref="KeepDays"/> days and at most
/// <see cref="MaxCount"/> (the oldest go first). Every change returns the circles it dropped, so the caller can
/// delete their pictures too.
/// </summary>
public sealed class CircleLog
{
    public const int MaxCount = 500, KeepDays = 30;

    readonly List<RememberedCircle> _circles;

    public CircleLog(IEnumerable<RememberedCircle>? circles = null) =>
        _circles = (circles ?? Enumerable.Empty<RememberedCircle>()).OrderByDescending(c => c.At).ToList();

    /// <summary>Newest first.</summary>
    public IReadOnlyList<RememberedCircle> All => _circles;

    public int Count => _circles.Count;

    public RememberedCircle? Find(string id) => _circles.FirstOrDefault(c => c.Id == id);

    /// <summary>Adds a circle (newest first), then drops what is too old or over the cap; returns what was dropped.</summary>
    public IReadOnlyList<RememberedCircle> Add(RememberedCircle circle, DateTime now)
    {
        int at = _circles.FindIndex(c => c.At <= circle.At);
        _circles.Insert(at < 0 ? _circles.Count : at, circle);
        return Prune(now);
    }

    /// <summary>Drops circles older than <see cref="KeepDays"/> days and any past <see cref="MaxCount"/>; returns them.</summary>
    public IReadOnlyList<RememberedCircle> Prune(DateTime now)
    {
        var cutoff = now - TimeSpan.FromDays(KeepDays);
        var dropped = new List<RememberedCircle>();
        for (int i = _circles.Count - 1; i >= 0; i--)
        {
            if (_circles[i].At >= cutoff && i < MaxCount) continue;
            dropped.Add(_circles[i]);
            _circles.RemoveAt(i);
        }
        return dropped;
    }

    public RememberedCircle? Remove(string id)
    {
        int i = _circles.FindIndex(c => c.Id == id);
        if (i < 0) return null;
        var c = _circles[i];
        _circles.RemoveAt(i);
        return c;
    }

    /// <summary>Forgets everything; returns what was there.</summary>
    public IReadOnlyList<RememberedCircle> Clear()
    {
        var all = _circles.ToList();
        _circles.Clear();
        return all;
    }

    /// <summary>The circles in one of the card's filters, newest first.</summary>
    public IEnumerable<RememberedCircle> In(CircleGroup group) =>
        group == CircleGroup.All ? _circles : _circles.Where(c => CircleKinds.GroupOf(c.Kind) == group);
}
