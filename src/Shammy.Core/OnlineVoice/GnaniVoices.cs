namespace Shammy.Core.OnlineVoice;

/// <param name="Language">The voice's own language code, sent with every request ("hi-en" is Hinglish).</param>
/// <param name="Speaks">The language as the picker names it.</param>
public sealed record GnaniVoice(string Name, string Language, string Speaks)
{
    public string Label => $"{Name} · {Speaks}";
}

/// <summary>
/// The few Timbre v2.5 voices the picker offers, from Gnani's voice catalogue: the one Hinglish voice, and
/// friendly or conversational Hindi and English ones. Each is used with its own language, as the catalogue asks.
/// </summary>
public static class GnaniVoices
{
    public const string Default = "Poorvi";

    public static readonly IReadOnlyList<GnaniVoice> All = new GnaniVoice[]
    {
        new("Poorvi", "hi-en", "Hinglish"),
        new("Nalini", "hi-IN", "Hindi"),
        new("Bhavna", "hi-IN", "Hindi"),
        new("Vikrant", "hi-IN", "Hindi"),
        new("Deepak", "hi-IN", "Hindi"),
        new("Kaveri", "en-IN", "English"),
        new("Pranav", "en-IN", "English"),
        new("Shlok", "en-IN", "English"),
    };

    /// <summary>The voice by name; the Hinglish one for a name that isn't on the list (a hand-edited setting).</summary>
    public static GnaniVoice Find(string? name) =>
        All.FirstOrDefault(v => string.Equals(v.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static string Normalize(string? name) => Find(name).Name;

    /// <summary>
    /// The language to send for a text: the voice's own, except Hindi letters handed to an English voice, where
    /// the service is left to tell from the script ("auto").
    /// </summary>
    public static string LanguageFor(GnaniVoice voice, string text) =>
        voice.Language == "en-IN" && text.Any(c => c is >= 'ऀ' and <= 'ॿ') ? "auto" : voice.Language;
}
