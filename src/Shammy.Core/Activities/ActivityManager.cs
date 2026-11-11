namespace Shammy.Core.Activities;

public enum ActivityKind
{
    NowPlaying,
    Hud,
    Charging,
    LowBattery,
    Bluetooth,
    Downloading,
    DownloadComplete,
    Progress,
    Message,
    Notification,

    /// <summary>Drawn by a module (timers, tickers...); its payload renders itself.</summary>
    Custom,
}

/// <summary>Something the notch can show in its compact state. Higher priority wins the pill.</summary>
public sealed record Activity(string Id, ActivityKind Kind, int Priority, object? Payload = null, DateTime? ExpiresAt = null)
{
    public Action? Click { get; init; }

    /// <summary>
    /// Shown even over a fullscreen app (a finished timer, a due alarm): something you asked to be
    /// interrupted for. Everything else waits until fullscreen ends.
    /// </summary>
    public bool BreaksFullscreen { get; init; }
}

/// <summary>What the notch may show while a game, video or presentation fills the primary monitor.</summary>
public static class FullscreenPolicy
{
    /// <summary>Volume/brightness feedback (you just pressed the key) and activities marked to break through.</summary>
    public static bool ShowsOver(Activity? current) => current is { Kind: ActivityKind.Hud } or { BreaksFullscreen: true };
}

/// <summary>
/// Decides which activity owns the compact notch. The winner is the highest priority; ties go to the
/// most recently posted. A long-running activity that loses is offered as <see cref="Secondary"/> so the
/// shell can show it in a detached bubble, the way Dynamic Island splits.
/// </summary>
public sealed class ActivityManager
{
    readonly Dictionary<string, (Activity Activity, long Order)> _items = new();
    long _order;

    public event Action? Changed;

    public Activity? Current { get; private set; }
    public Activity? Secondary { get; private set; }

    /// <summary>Every live activity, most important first (the pet's costume and its waiting dot are picked from these).</summary>
    public IReadOnlyList<Activity> Ranked { get; private set; } = Array.Empty<Activity>();

    public IReadOnlyList<Activity> All => _items.Values.Select(v => v.Activity).ToList();

    /// <summary>When the soonest activity expires (UTC), or null when none does: the host's timer wakes only then.</summary>
    public DateTime? NextExpiryUtc
    {
        get
        {
            DateTime? next = null;
            foreach (var (activity, _) in _items.Values)
            {
                if (activity.ExpiresAt is not { } at) continue;
                var utc = at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : at;
                if (next is null || utc < next) next = utc;
            }
            return next;
        }
    }

    public void Post(Activity activity)
    {
        _items[activity.Id] = (activity, ++_order);
        Recalculate();
    }

    public bool Remove(string id)
    {
        if (!_items.Remove(id)) return false;
        Recalculate();
        return true;
    }

    public Activity? Get(string id) => _items.TryGetValue(id, out var v) ? v.Activity : null;

    public void Tick(DateTime now)
    {
        // Callers may stamp expiry in local or UTC time; compare like with like so a local stamp
        // in India doesn't silently last 5½ hours longer.
        var nowUtc = now.Kind == DateTimeKind.Local ? now.ToUniversalTime() : now;
        var expired = _items.Values
            .Where(v => v.Activity.ExpiresAt is { } at && (at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : at) <= nowUtc)
            .Select(v => v.Activity.Id)
            .ToList();
        if (expired.Count == 0) return;
        foreach (var id in expired) _items.Remove(id);
        Recalculate();
    }

    void Recalculate()
    {
        var ordered = _items.Values
            .OrderByDescending(v => v.Activity.Priority)
            .ThenByDescending(v => v.Order)
            .Select(v => v.Activity)
            .ToList();

        Ranked = ordered;
        Current = ordered.Count > 0 ? ordered[0] : null;
        Secondary = Current is null
            ? null
            : ordered.Skip(1).FirstOrDefault(a => a.Kind != Current.Kind && IsLongRunning(a.Kind));
        Changed?.Invoke();
    }

    static bool IsLongRunning(ActivityKind kind) =>
        kind is ActivityKind.NowPlaying or ActivityKind.Downloading or ActivityKind.Progress or ActivityKind.Custom;
}
