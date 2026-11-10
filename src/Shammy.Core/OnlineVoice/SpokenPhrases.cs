using System.Text.RegularExpressions;

namespace Shammy.Core.OnlineVoice;

/// <summary>
/// What Shammy says aloud when a timer, a reminder or an alarm goes off: the notch's own words, made into a
/// short sentence. A timer called "Tea" is "Tea's ready!"; one with no name is "Time's up!".
/// </summary>
public static class SpokenPhrases
{
    /// <summary>Long enough for a reminder, short enough to be a second or two of speech (and of credits).</summary>
    public const int MaxLength = 160;

    /// <summary>What the Test button says.</summary>
    public const string Hello = "Hi, I'm Shammy";

    /// <summary>The same hello from a pet that was given another name.</summary>
    public static string HelloFrom(string? petName) => string.IsNullOrWhiteSpace(petName) ? Hello : $"Hi, I'm {petName.Trim()}";

    static readonly Regex Link = new(@"\b(?:https?://|www\.)\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    // The pet's own labels for a clock that ran out (see the timers module); everything else is the user's name for it.
    static readonly Dictionary<string, string> Fixed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Time's up"] = "Time's up!",
        ["Time’s up"] = "Time's up!",
        ["Break time"] = "Break time!",
        ["Long break"] = "Time for a long break!",
        ["Back to focus"] = "Back to focus!",
        ["Focus done"] = "Focus done!",
    };

    /// <summary>
    /// A timer, focus session or pomodoro phase that ran out, from the label the pet shows for it ("Tea",
    /// "Time's up", "Break time").
    /// </summary>
    public static string Clock(string? label)
    {
        label = Clean(label);
        if (label.Length == 0) return "Time's up!";
        if (Fixed.TryGetValue(label, out var phrase)) return phrase;
        // "Tea's ready!" for one plain word; a longer name ("call the bank") just gets announced.
        bool oneWord = !label.Contains(' ') && label.All(char.IsLetter) && !label.EndsWith("s", StringComparison.OrdinalIgnoreCase);
        return oneWord ? $"{Capital(label)}'s ready!" : $"Time's up: {label}.";
    }

    public static string Reminder(string? text)
    {
        text = Clean(text);
        return text.Length == 0 ? "You have a reminder." : $"Reminder: {text}";
    }

    /// <param name="time">The alarm's time as the notch writes it ("7:00 AM", "07:00").</param>
    public static string Alarm(string? time, string? label)
    {
        time = Clean(time);
        label = Clean(label);
        string said = time.Length > 0 ? $"It's {time}." : "Your alarm.";
        return label.Length > 0 ? $"{said} {Capital(label)}." : said;
    }

    /// <summary>
    /// Text fit to be spoken: one line, links left out (nobody wants a URL read aloud), and cut at a word once it
    /// passes <see cref="MaxLength"/>.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        text = Spaces.Replace(Link.Replace(text, " "), " ").Trim();
        if (text.Length <= MaxLength) return text;
        int cut = text.LastIndexOf(' ', MaxLength);
        return text[..(cut > MaxLength / 2 ? cut : MaxLength)].TrimEnd(' ', ',', ';', ':', '-');
    }

    /// <summary>The words beside the talking pet: only when they fit its nine characters, otherwise nothing.</summary>
    public static string PetValue(string? text)
    {
        text = text?.Trim() ?? "";
        return text.Length <= 9 ? text : "";
    }

    static string Capital(string text) => text.Length == 0 || char.IsUpper(text[0]) ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
