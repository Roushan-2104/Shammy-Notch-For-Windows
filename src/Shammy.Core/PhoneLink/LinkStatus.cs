using System.Text.Json.Nodes;

namespace Shammy.Core.PhoneLink;

/// <summary>The phone's battery and ringer (§5 `status` from the phone). <see cref="Ringer"/> is "sound", "vibrate", "silent" or "".</summary>
public sealed record PhoneStatus(int Battery, bool Charging, string Ringer, bool Dnd, bool CanRinger)
{
    public static readonly string[] Ringers = ["sound", "vibrate", "silent"];

    /// <summary>The message read safely; null without a battery level.</summary>
    public static PhoneStatus? From(JsonObject m)
    {
        if (m["battery"] is not JsonValue b || !b.TryGetValue<int>(out int battery) || battery is < 0 or > 100) return null;
        var ringer = LinkSession.Text(m, "ringer");
        return new PhoneStatus(battery, Flag(m, "charging"), Ringers.Contains(ringer) ? ringer : "", Flag(m, "dnd"), Flag(m, "canRinger"));
    }

    internal static bool Flag(JsonObject m, string field) => m[field] is JsonValue v && v.TryGetValue<bool>(out bool on) && on;
}

/// <summary>What the phone plays (§5 `phoneMedia`). <see cref="Art"/> is base64 JPEG, sent only when the song changes.</summary>
public sealed record PhoneMedia(bool Has, string Title, string Artist, bool Playing, double Volume, string Art)
{
    public static PhoneMedia From(JsonObject m)
    {
        double volume = m["volume"] is JsonValue v && v.TryGetValue<double>(out double d) && d is >= 0 and <= 1 ? d : -1;
        return new PhoneMedia(PhoneStatus.Flag(m, "has"), LinkSession.Text(m, "title"), LinkSession.Text(m, "artist"),
            PhoneStatus.Flag(m, "playing"), volume, m["art"] is JsonValue a && a.TryGetValue<string>(out var art) && art.Length <= 64 * 1024 ? art : "");
    }

    /// <summary>The same song as <paramref name="other"/> (so its cover, sent once, still applies).</summary>
    public bool SameSong(PhoneMedia? other) => other is not null && other.Title == Title && other.Artist == Artist;
}

/// <summary>Battery both ways: when the laptop tells the phone, and when the phone's level is worth a nudge.</summary>
public static class LinkStatus
{
    /// <summary>The phone nudges at these levels, once each per discharge (lowest first).</summary>
    public static readonly IReadOnlyList<int> NudgeLevels = [10, 20];

    /// <summary>
    /// The level to nudge about now, or null: the lowest of 20 % / 10 % the battery is at or below that wasn't nudged
    /// since the charger came off (<paramref name="lastNudged"/> above 100 for none). Never while charging.
    /// </summary>
    public static int? Nudge(int percent, bool charging, int lastNudged)
    {
        if (charging) return null;
        foreach (var level in NudgeLevels)
            if (percent <= level && level < lastNudged) return level;
        return null;
    }

    /// <summary>Whether the laptop's battery moved enough to tell the phone again: a whole 5 % step, or charging changed.</summary>
    public static bool Moved((int Percent, bool Charging)? sent, int percent, bool charging) =>
        sent is not { } s || s.Charging != charging || Math.Abs(s.Percent - percent) >= 5;

    /// <summary>The laptop's `status`: its battery (-1 when it has none) and whether it charges.</summary>
    public static JsonObject Laptop(bool hasBattery, int percent, bool charging) => new()
    {
        ["t"] = "status", ["battery"] = hasBattery ? Math.Clamp(percent, 0, 100) : -1, ["charging"] = hasBattery && charging,
    };
}
