using System.Text.RegularExpressions;

namespace Shammy.Core.Text;

/// <summary>
/// The tidy-up every command parser starts with: trimmed, trailing ".!?" dropped, spaces collapsed, lower case,
/// and a leading "hey Shammy," / "Shammy," and a "please" at either end taken off. Parsers add their own steps after.
/// </summary>
public static class PhraseText
{
    static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex Wake = new(@"^(?:hey shammy,? |shammy,? )", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex Please = new(@"^please |,? please$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Normalize(string? text)
    {
        var s = Spaces.Replace((text ?? "").Trim().TrimEnd('.', '!', '?').Trim(), " ").ToLowerInvariant();
        s = Wake.Replace(s, "");
        return Please.Replace(s, "");
    }
}
