using System.Text;
using System.Text.RegularExpressions;

namespace Shammy.Core.Captions;

/// <summary>A caption word as shown, the same word with case and punctuation removed (what merging compares),
/// and the merge it arrived in (the newest ones are drawn bright).</summary>
public sealed record CaptionWord(string Text, string Key, int Batch);

/// <summary>Splitting what the voice model heard into words, and telling two hearings of a word apart.</summary>
public static class CaptionText
{
    // What the model writes when it is given music, hiss or a breath: never real captions on their own.
    static readonly HashSet<string> Invented = new(StringComparer.Ordinal)
    {
        "you", "thankyou", "thanksforwatching", "thankyouforwatching", "pleasesubscribe",
        "subtitlesbytheamaraorgcommunity", "thanksforwatchingpleasesubscribe",
    };

    /// <summary>Lower-case letters and digits only: "List," and "list" are the same word.</summary>
    public static string Key(string word)
    {
        var sb = new StringBuilder(word.Length);
        foreach (var ch in word)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>The words of a transcript. A stray dash or ellipsis joins the word before it rather than standing alone.</summary>
    public static List<(string Text, string Key)> Words(string? text)
    {
        var words = new List<(string Text, string Key)>();
        if (string.IsNullOrWhiteSpace(text)) return words;
        foreach (var raw in Regex.Split(text.Trim(), @"\s+"))
        {
            var key = Key(raw);
            if (key.Length > 0) words.Add((raw, key));
            else if (words.Count > 0 && raw.Length <= 3) words[^1] = (words[^1].Text + " " + raw, words[^1].Key);
        }
        return words;
    }

    /// <summary>True when the whole transcript is one of the phrases the model makes up for sound without speech.</summary>
    public static bool IsInvented(string? text) => Invented.Contains(Key(text ?? ""));

    /// <summary>
    /// Two hearings of what is probably one word: the same, or a longer word that differs by a letter
    /// ("whole" and "hole", "list" and "lists"), or by two when both are long.
    /// </summary>
    public static bool Similar(string a, string b)
    {
        if (a == b) return true;
        int shorter = Math.Min(a.Length, b.Length);
        if (shorter < 4 || Math.Abs(a.Length - b.Length) > 2) return false;
        return Distance(a, b) <= (shorter >= 8 ? 2 : 1);
    }

    static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>What the strip says it is listening in: the app's voice mode, in a word.</summary>
    public static string LanguageName(bool hinglish, string? voiceLanguage) =>
        hinglish ? "Hinglish" : string.Equals(voiceLanguage, "auto", StringComparison.OrdinalIgnoreCase) ? "Any language" : "English";
}
