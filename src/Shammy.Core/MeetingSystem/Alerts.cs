namespace Shammy.Core.MeetingSystem;

/// <summary>Lets each kind of alert through at most once per cooldown.</summary>
public sealed class AlertCooldown
{
    readonly TimeSpan _cooldown;
    readonly Dictionary<string, DateTime> _last = new(StringComparer.OrdinalIgnoreCase);

    public AlertCooldown(TimeSpan cooldown) => _cooldown = cooldown;

    public bool TryFire(string kind, DateTime now)
    {
        if (_last.TryGetValue(kind, out var at) && now - at < _cooldown) return false;
        _last[kind] = now;
        return true;
    }
}

/// <summary>
/// True once the same subject (e.g. one app hogging the CPU) has been present continuously for the
/// duration. A different subject or none restarts the clock.
/// </summary>
public sealed class SustainedCondition
{
    readonly TimeSpan _duration;
    string? _subject;
    DateTime _since;

    public SustainedCondition(TimeSpan duration) => _duration = duration;

    public bool Update(string? subject, DateTime now)
    {
        if (subject is null)
        {
            _subject = null;
            return false;
        }
        if (!string.Equals(subject, _subject, StringComparison.OrdinalIgnoreCase))
        {
            _subject = subject;
            _since = now;
        }
        return now - _since >= _duration;
    }

    public bool Update(bool active, DateTime now) => Update(active ? "" : null, now);
}
