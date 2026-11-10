namespace Shammy.Core.OnlineVoice;

/// <summary>The pasted key: tidied before it's saved, and never shown again except as "••••1234".</summary>
public static class KeyMask
{
    const string Dots = "••••";

    /// <summary>Longer than any key, short enough that a pasted page isn't taken for one.</summary>
    public const int MaxLength = 512;

    /// <summary>The key as shown once saved: four dots and its last four characters (dots alone for a short one).</summary>
    public static string Mask(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        key = key.Trim();
        return key.Length >= 12 ? Dots + key[^4..] : Dots;
    }

    /// <summary>
    /// The key out of whatever was pasted: spaces, quotes and a copied "X-API-Key-ID:" in front are dropped.
    /// Null when nothing usable is left (empty, several words, not plain characters, far too long).
    /// </summary>
    public static string? Clean(string? pasted)
    {
        if (pasted is null) return null;
        var key = pasted.Trim();
        int colon = key.IndexOf(':');
        if (colon > 0 && key[..colon].Trim().Equals(GnaniApi.KeyHeader, StringComparison.OrdinalIgnoreCase)) key = key[(colon + 1)..];
        key = key.Trim().Trim('"', '\'', '`', '<', '>').Trim();
        if (key.Length is 0 or > MaxLength) return null;
        // A header value: printable ASCII with no spaces. Anything else was not a key.
        return key.All(c => c is > ' ' and < '\u007F') ? key : null;
    }
}
