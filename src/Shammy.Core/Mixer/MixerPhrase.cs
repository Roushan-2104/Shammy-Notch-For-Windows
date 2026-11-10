using System.Globalization;
using System.Text.RegularExpressions;

namespace Shammy.Core.Mixer;

/// <summary>A row written as text: "Game=35,muted".</summary>
public sealed record MixerEntry(string Name, int Percent, bool Muted);

/// <summary>
/// The words around the volume view: the phrases that open it, and the lines the test pipe speaks. The phrases
/// are a fixed list, because the command runs before every other module's: "volume mixer" opens this view, while
/// "volume 50", "volume up" and "mute" stay with the level commands.
/// </summary>
public static class MixerPhrase
{
    static readonly HashSet<string> Opens = new(StringComparer.Ordinal)
    {
        "mixer", "the mixer", "open mixer", "open the mixer", "show mixer", "show the mixer",
        "volume mixer", "the volume mixer", "open volume mixer", "open the volume mixer", "show volume mixer", "show the volume mixer",
        "app volume", "app volumes", "open app volume", "open app volumes", "show app volume", "show app volumes",
    };

    /// <summary>"mixer", "volume mixer", "app volumes": open the volume view.</summary>
    public static bool IsOpen(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 60) return false;
        var s = Regex.Replace(text.Trim().TrimEnd('.', '!', '?').Trim(), @"\s+", " ").ToLowerInvariant();
        s = Regex.Replace(s, @"^(?:hey shammy,? |shammy,? )", "");
        s = Regex.Replace(s, @"^please |,? please$", "");
        return Opens.Contains(s);
    }

    /// <summary>"YouTube Music 40": the row's name, then a level from 0 to 100. Null when it isn't that.</summary>
    public static (string Name, int Percent)? ParseSet(string? arg)
    {
        if (Split(arg) is not var (name, last)) return null;
        last = last.TrimEnd('%');
        return int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out int percent) && percent is >= 0 and <= 100
            ? (name, percent)
            : null;
    }

    /// <summary>"YouTube Music on": the row's name, then on (muted) or off. Null when it isn't that.</summary>
    public static (string Name, bool Muted)? ParseMute(string? arg)
    {
        if (Split(arg) is not var (name, last)) return null;
        return last.ToLowerInvariant() switch
        {
            "on" => (name, true),
            "off" => (name, false),
            _ => null,
        };
    }

    /// <summary>Everything before the last word, and the last word.</summary>
    static (string Name, string Last)? Split(string? arg)
    {
        var s = Regex.Replace((arg ?? "").Trim(), @"\s+", " ");
        int at = s.LastIndexOf(' ');
        return at <= 0 ? null : (s[..at], s[(at + 1)..]);
    }

    /// <summary>
    /// The state in one line: the output, the output's level, then every app row, e.g.
    /// "output=Speakers; master=64; YouTube Music=80; Game=35,muted". A dash stands for what isn't there.
    /// </summary>
    public static string State(string? output, IReadOnlyList<MixerRow> rows)
    {
        var master = rows.FirstOrDefault(r => r.Kind == MixerRowKind.Everything);
        var parts = new List<string>
        {
            "output=" + (string.IsNullOrWhiteSpace(output) ? "-" : output.Trim()),
            "master=" + (master is null ? "-" : Level(master)),
        };
        parts.AddRange(rows.Where(r => r.Kind != MixerRowKind.Everything).Select(r => $"{r.Name}={Level(r)}"));
        return string.Join("; ", parts);
    }

    static string Level(MixerRow row) => row.Percent.ToString(CultureInfo.InvariantCulture) + (row.Muted ? ",muted" : "");

    /// <summary>
    /// Rows written the way <see cref="State"/> writes them ("Everything=64; YouTube Music=80; Game=35,muted"),
    /// for standing in for real apps in a test. Null when any part isn't "name=level" or "name=level,muted".
    /// </summary>
    public static IReadOnlyList<MixerEntry>? ParseRows(string? text)
    {
        var entries = new List<MixerEntry>();
        foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int at = part.LastIndexOf('=');
            if (at <= 0) return null;
            var value = part[(at + 1)..].Split(',', StringSplitOptions.TrimEntries);
            if (value.Length > 2 || (value.Length == 2 && !value[1].Equals("muted", StringComparison.OrdinalIgnoreCase))) return null;
            if (!int.TryParse(value[0], NumberStyles.None, CultureInfo.InvariantCulture, out int percent) || percent is < 0 or > 100) return null;
            entries.Add(new MixerEntry(part[..at].Trim(), percent, value.Length == 2));
        }
        return entries.Count == 0 ? null : entries;
    }
}
