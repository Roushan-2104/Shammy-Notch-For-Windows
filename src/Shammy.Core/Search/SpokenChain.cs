using System.Text.RegularExpressions;

namespace Shammy.Core.Search;

/// <summary>
/// One sentence, several requests: "take a screenshot and send it to my phone", "screenshot le lo aur phone pe
/// bhej", "start a 25 minute timer then pause the music". <see cref="Split"/> offers the pieces; the caller keeps
/// them only when every piece is a command on its own, so "remind me to call mom and dad" or "play salt and
/// pepper" stay one request.
/// </summary>
public static class SpokenChain
{
    public const int MaxSteps = 4;

    // "and then", "then", "and", "aur phir", "aur", "phir/fir", "uske baad", "after that", ", then".
    static readonly Regex Joiner = new(@"\s*,?\s+(?:and\s+then|and\s+after\s+that|after\s+that|then|and|aur\s+(?:phir|fir)|aur\s+uske\s+baad|uske\s+baad|aur|phir|fir)\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The pieces of <paramref name="text"/> split at its joining words, or empty when there is only one.</summary>
    public static IReadOnlyList<string> Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var parts = Joiner.Split(text.Trim().TrimEnd('.', '!', '?', '।'))
            .Select(p => p.Trim(' ', ',', '.'))
            .Where(p => p.Length > 0)
            .ToList();
        return parts.Count is >= 2 and <= MaxSteps ? parts : Array.Empty<string>();
    }

    /// <summary>
    /// A later piece that says "it" or "this" means what the step before made ("send it to my phone" after a
    /// screenshot): spelled out as the commands expect.
    /// </summary>
    public static string Carry(string piece) =>
        Regex.Replace(piece, @"^(?:send|bhej(?:o|do)?)\s+(?:it|that|this|ise|isko|use|usko)\b", "send this", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
