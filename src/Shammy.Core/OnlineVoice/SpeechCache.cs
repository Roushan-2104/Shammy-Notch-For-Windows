using System.Security.Cryptography;
using System.Text;

namespace Shammy.Core.OnlineVoice;

/// <summary>One cached utterance on disk.</summary>
public sealed record CacheEntry(string Name, long Bytes, DateTime LastUsedUtc);

/// <summary>
/// The rules of the speech cache: the same words in the same voice are fetched (and paid for) once, and the
/// folder never grows past 20 MB, the least recently said going first.
/// </summary>
public static class SpeechCache
{
    public const long MaxBytes = 20L * 1024 * 1024;

    /// <summary>
    /// The file name for an utterance: a hash, so the words themselves are never in a file name. Exactly the same
    /// text, voice, language, model and sample rate give the same name; any difference gives another.
    /// </summary>
    public static string Key(string text, string voice, string language, string model = GnaniApi.TtsModel, int sampleRate = GnaniApi.TtsSampleRate)
    {
        var id = string.Join('\n', model, voice, language, sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), text);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)), 0, 16).ToLowerInvariant();
    }

    /// <summary>
    /// The entries to delete so the rest fit in <paramref name="maxBytes"/>: the least recently used first (ties
    /// by name, so the order is always the same). <paramref name="keep"/> is never deleted (the one just written).
    /// </summary>
    public static IReadOnlyList<string> Evict(IEnumerable<CacheEntry> entries, long maxBytes = MaxBytes, string? keep = null)
    {
        var all = entries.ToList();
        long total = all.Sum(e => e.Bytes);
        var gone = new List<string>();
        foreach (var e in all.OrderBy(e => e.LastUsedUtc).ThenBy(e => e.Name, StringComparer.Ordinal))
        {
            if (total <= maxBytes) break;
            if (e.Name == keep) continue;
            gone.Add(e.Name);
            total -= e.Bytes;
        }
        return gone;
    }
}
