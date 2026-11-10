using System.Globalization;
using System.Text;
using Shammy.Core.Productivity;

namespace Shammy.Core.Nudges;

/// <summary>What a typed or spoken line asks the nudges to do.</summary>
public enum NudgeCommand { EyeBreaksOn, EyeBreaksOff, WaterOn, WaterOff, EyeBreakNow }

/// <summary>The nudges' words: the labels beside the pet, the countdown, and the phrases that switch them.</summary>
public static class NudgeText
{
    public const string EyeLabel = "Look away", WaterLabel = "Water?";
    public const string SkipBreak = "Skip this break", HadWater = "Had some water";

    /// <summary>How long the pet keeps its eyes closed with you.</summary>
    public static readonly TimeSpan EyeBreakLength = TimeSpan.FromSeconds(20);

    /// <summary>How long the water question stays before it leaves uncounted.</summary>
    public static readonly TimeSpan WaterStay = TimeSpan.FromSeconds(20);

    /// <summary>The eye break's time left, "0:20" down to "0:00" (and "0:00" from then on).</summary>
    public static string Countdown(DateTime startedUtc, DateTime utcNow)
    {
        var left = EyeBreakLength - (utcNow - startedUtc);
        return TimeFormat.Countdown(left > EyeBreakLength ? EyeBreakLength : left);
    }

    /// <summary>"2 today": the glasses confirmed so far.</summary>
    public static string Glasses(int today) => string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, today)} today");

    /// <summary>
    /// "eye breaks on", "turn off water reminders", "take an eye break"... or null for anything else, so a web
    /// search about eye strain or water stays a search.
    /// </summary>
    public static NudgeCommand? Parse(string text)
    {
        var words = Words(text);
        if (words.Length is < 2 or > 6) return null;
        string line = string.Join(' ', words);

        if (line is "take an eye break" or "take eye break" or "take a eye break" or "eye break now" or "eye break"
            or "start an eye break" or "start eye break")
            return NudgeCommand.EyeBreakNow;

        bool? on = Switch(ref line);
        if (on is null) return null;
        return line switch
        {
            "eye breaks" or "eye break" or "the eye breaks" => on.Value ? NudgeCommand.EyeBreaksOn : NudgeCommand.EyeBreaksOff,
            "water reminders" or "water reminder" or "the water reminders" => on.Value ? NudgeCommand.WaterOn : NudgeCommand.WaterOff,
            _ => null,
        };
    }

    /// <summary>Takes "on"/"off" off the end, or "turn on"/"turn off" off the front; null when there is neither.</summary>
    static bool? Switch(ref string line)
    {
        bool turn = line.StartsWith("turn ", StringComparison.Ordinal);
        if (turn) line = line[5..];
        foreach (var (word, value) in new[] { ("on", true), ("off", false) })
        {
            if (line.EndsWith(" " + word, StringComparison.Ordinal))
            {
                line = line[..^(word.Length + 1)];
                return value;
            }
            if (turn && line.StartsWith(word + " ", StringComparison.Ordinal))
            {
                line = line[(word.Length + 1)..];
                return value;
            }
        }
        return null;
    }

    /// <summary>Lower-case words with punctuation dropped ("Eye breaks, on." reads as "eye breaks on").</summary>
    static string[] Words(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text) sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
