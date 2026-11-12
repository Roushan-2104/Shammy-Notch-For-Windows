using System.Text.RegularExpressions;

namespace Shammy.Core.Quick;

/// <summary>Windows 11's power mode (Settings › Power › Power mode): the overlay on top of the Balanced plan.</summary>
public enum PowerModeKind { Efficiency, Balanced, Performance }

public static class PowerMode
{
    // The overlay schemes Windows uses for the three positions of the power-mode slider.
    public static readonly Guid EfficiencyId = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid BalancedId = Guid.Empty;
    public static readonly Guid PerformanceId = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static Guid IdOf(PowerModeKind kind) => kind switch
    {
        PowerModeKind.Efficiency => EfficiencyId,
        PowerModeKind.Performance => PerformanceId,
        _ => BalancedId,
    };

    /// <summary>The mode for an overlay scheme; an unknown one (an OEM's own) reads as Balanced.</summary>
    public static PowerModeKind From(Guid id) =>
        id == EfficiencyId ? PowerModeKind.Efficiency : id == PerformanceId ? PowerModeKind.Performance : PowerModeKind.Balanced;

    /// <summary>The tile steps Balanced → Performance → Efficiency → Balanced.</summary>
    public static PowerModeKind Next(PowerModeKind kind) => kind switch
    {
        PowerModeKind.Balanced => PowerModeKind.Performance,
        PowerModeKind.Performance => PowerModeKind.Efficiency,
        _ => PowerModeKind.Balanced,
    };

    public static string Name(PowerModeKind kind) => kind switch
    {
        PowerModeKind.Efficiency => "Best battery",
        PowerModeKind.Performance => "Best performance",
        _ => "Balanced",
    };

    // The whole phrase must be a power-mode request: "performance mode", "best battery", "switch to balanced
    // mode", "power mode performance". A bare "performance" or "battery saver" (Windows' own, different switch)
    // is not one, and nor is a question ("how to change power mode").
    static readonly Regex Phrase = new(
        @"^(?:(?:turn on|switch to|switch on|set|use|enable|go to|change to|put (?:it |the pc |my pc |my laptop |the laptop )?(?:in|on|to)) )?" +
        @"(?:(?:the )?power mode (?:to )?)?(?<mode>best performance|high performance|performance|best battery|best power efficiency|battery saver|" +
        @"battery saving|power saving|power saver|efficiency|balanced|normal)(?<tail> (?:power )?mode)?(?: on)?$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// "performance mode", "best battery", "balanced mode", "power mode performance", "battery saver mode" → the
    /// mode; null for anything else. Strict on purpose: the Laptop commands run before every other command.
    /// </summary>
    public static PowerModeKind? Parse(string? text)
    {
        var t = Text.PhraseText.Normalize(text).Replace("power-mode", "power mode");
        var m = Phrase.Match(t);
        if (!m.Success) return null;
        var mode = m.Groups["mode"].Value;
        // Without "mode" or "power mode" only the slider's own names count ("best battery", "best performance").
        if (!m.Groups["tail"].Success && !t.Contains("power mode", StringComparison.Ordinal) && !mode.StartsWith("best ", StringComparison.Ordinal))
            return null;
        if (mode.Contains("performance", StringComparison.Ordinal)) return PowerModeKind.Performance;
        if (mode is "balanced" or "normal") return PowerModeKind.Balanced;
        return PowerModeKind.Efficiency;
    }
}
