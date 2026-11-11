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

    /// <summary>"power mode performance", "battery saver mode", "balanced mode" → the mode; null otherwise.</summary>
    public static PowerModeKind? Parse(string? text)
    {
        var t = Text.PhraseText.Normalize(text);
        if (!t.Contains("mode") && !t.StartsWith("power ", StringComparison.Ordinal)) return null;
        if (t.Contains("performance") || t.Contains("fast") || t.Contains("gaming")) return PowerModeKind.Performance;
        if (t.Contains("efficien") || t.Contains("battery") || t.Contains("saver") || t.Contains("save power")) return PowerModeKind.Efficiency;
        if (t.Contains("balanced") || t.Contains("normal")) return PowerModeKind.Balanced;
        return null;
    }
}
