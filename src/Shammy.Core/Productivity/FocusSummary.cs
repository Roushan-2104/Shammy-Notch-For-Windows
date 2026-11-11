namespace Shammy.Core.Productivity;

/// <summary>What a focus session held back, said at its end: "3 messages waited · WhatsApp, Gmail".</summary>
public static class FocusSummary
{
    public static string? Waited(int messages, IEnumerable<string> apps)
    {
        if (messages <= 0) return null;
        var names = apps.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        var count = messages == 1 ? "1 message waited" : $"{messages} messages waited";
        return names.Count == 0 ? count : $"{count} · {string.Join(", ", names)}";
    }
}
