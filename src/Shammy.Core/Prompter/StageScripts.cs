using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Shammy.Core.Prompter;

/// <summary>One script Stage read before: its words, a title (its first line), how many words and when.</summary>
public sealed class RecentScript
{
    public string Text { get; set; } = "";
    public string Title { get; set; } = "";
    public int Words { get; set; }
    public DateTime SavedAt { get; set; }
}

/// <summary>
/// How a script gets onto Stage without typed commands: whether copied text looks like a script worth offering,
/// what a drop carries, and the short list of recent scripts (the last <see cref="MaxRecent"/>, newest first).
/// </summary>
public static class StageScripts
{
    /// <summary>Copied text shorter than this is a word or a name, not a script.</summary>
    public const int MinOfferChars = 20;

    /// <summary>How many recent scripts are kept.</summary>
    public const int MaxRecent = 5;

    /// <summary>The longest title, in characters.</summary>
    public const int MaxTitle = 48;

    static readonly Regex Space = new(@"\s+", RegexOptions.CultureInvariant);
    static readonly Regex DrivePath = new(@"^(?:[a-z]:[\\/]|\\\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether copied text is worth offering as a script: at least <see cref="MinOfferChars"/> characters and three
    /// words, and not a link, an email address or a file path on its own.
    /// </summary>
    public static bool LooksLikeScript(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length < MinOfferChars) return false;
        if (IsLoneLink(t)) return false;
        return PrompterText.Words(t) >= 3;
    }

    /// <summary>Whether dragged text is worth reading: any words at all, but not a link or a path on its own.</summary>
    public static bool IsDroppableText(string? text)
    {
        var t = (text ?? "").Trim();
        return t.Length > 0 && !IsLoneLink(t);
    }

    /// <summary>One token that is a web or file address, an email address or a path (no spaces in it).</summary>
    static bool IsLoneLink(string t)
    {
        if (Space.IsMatch(t))
        {
            // "C:\My Scripts\talk.txt" has spaces but is still only a path.
            return DrivePath.IsMatch(t) && !t.Contains('\n') && PrompterText.IsScriptFile(t);
        }
        if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || DrivePath.IsMatch(t)) return true;
        if (Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ftp" or "file" or "mailto") return true;
        int at = t.IndexOf('@');
        return at > 0 && t.IndexOf('.', at) > at;
    }

    /// <summary>
    /// Whether to ask "Use the text you copied?" as Stage opens: it looks like a script, isn't the script already on
    /// Stage, and isn't the copy the user already said no to (<paramref name="declinedKey"/>, see <see cref="Key"/>).
    /// </summary>
    public static bool ShouldOffer(string? copied, string? currentScript, string? declinedKey)
    {
        if (!LooksLikeScript(copied)) return false;
        var (text, _) = PrompterText.Clean(copied);
        if (text == PrompterText.Clean(currentScript).Text) return false;
        return declinedKey is null || Key(copied!) != declinedKey;
    }

    /// <summary>A short fingerprint of a script (after tidying), to remember "not this one" without keeping the words.</summary>
    public static string Key(string text)
    {
        var (clean, _) = PrompterText.Clean(text);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(clean));
        return Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>The one dropped file Stage takes: a single .txt or .md file; several files go to the shelf instead.</summary>
    public static string? ScriptFile(IReadOnlyList<string>? files) =>
        files is { Count: 1 } && !string.IsNullOrWhiteSpace(files[0]) && PrompterText.IsScriptFile(files[0].Trim()) ? files[0].Trim() : null;

    /// <summary>A script's title: its first line with words, cut at a word to <paramref name="max"/> characters.</summary>
    public static string Title(string? script, int max = MaxTitle)
    {
        var first = (script ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        first = Space.Replace(first, " ").TrimStart('#', '>', '-', '*', ' ').Trim();
        if (first.Length == 0) return "Untitled script";
        if (first.Length <= max) return first;
        int cut = first.LastIndexOf(' ', max - 1);
        return first[..(cut > max / 2 ? cut : max - 1)].TrimEnd(' ', ',', ';', ':', '.', '-') + "…";
    }

    /// <summary>
    /// The recent list after <paramref name="script"/> is read: it goes to the top (once, even if it was there
    /// already), and only the newest <paramref name="max"/> are kept.
    /// </summary>
    public static List<RecentScript> Remember(IEnumerable<RecentScript>? recent, string script, DateTime now, int max = MaxRecent)
    {
        var (text, _) = PrompterText.Clean(script);
        var list = (recent ?? Enumerable.Empty<RecentScript>()).Where(r => r is not null && r.Text.Length > 0 && r.Text != text).ToList();
        if (text.Length > 0)
            list.Insert(0, new RecentScript { Text = text, Title = Title(text), Words = PrompterText.Words(text), SavedAt = now });
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
        return list;
    }

    /// <summary>The recent list with nothing broken in it (an older or hand-edited store) and no more than <paramref name="max"/>.</summary>
    public static List<RecentScript> Tidy(IEnumerable<RecentScript>? recent, int max = MaxRecent)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<RecentScript>();
        foreach (var r in recent ?? Enumerable.Empty<RecentScript>())
        {
            if (r is null || string.IsNullOrWhiteSpace(r.Text) || !seen.Add(r.Text)) continue;
            if (string.IsNullOrWhiteSpace(r.Title)) r.Title = Title(r.Text);
            if (r.Words <= 0) r.Words = PrompterText.Words(r.Text);
            list.Add(r);
            if (list.Count == max) break;
        }
        return list;
    }

    /// <summary>When a script was read, in a word: "Today", "Yesterday", "Monday", "12 Nov", "12 Nov 2025".</summary>
    public static string When(DateTime savedAt, DateTime now)
    {
        int days = (now.Date - savedAt.Date).Days;
        if (days <= 0) return "Today";
        if (days == 1) return "Yesterday";
        if (days < 7) return savedAt.ToString("dddd", CultureInfo.InvariantCulture);
        return savedAt.Year == now.Year
            ? savedAt.ToString("d MMM", CultureInfo.InvariantCulture)
            : savedAt.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>A recent script's quiet line: "312 words · Today".</summary>
    public static string Meta(RecentScript script, DateTime now) =>
        $"{WordCount(script.Words)} · {When(script.SavedAt, now)}";

    /// <summary>"1 word", "312 words", "1,204 words".</summary>
    public static string WordCount(int words) =>
        words == 1 ? "1 word" : words.ToString("N0", CultureInfo.InvariantCulture) + " words";
}
