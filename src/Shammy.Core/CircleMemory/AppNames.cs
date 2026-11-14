namespace Shammy.Core.CircleMemory;

/// <summary>The name people call an app by ("WhatsApp", "Chrome"), from its process name and file description.</summary>
public static class AppNames
{
    static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Chrome", ["msedge"] = "Edge", ["firefox"] = "Firefox", ["brave"] = "Brave", ["opera"] = "Opera",
        ["arc"] = "Arc", ["vivaldi"] = "Vivaldi",
        ["whatsapp"] = "WhatsApp", ["whatsapp.root"] = "WhatsApp", ["telegram"] = "Telegram", ["signal"] = "Signal",
        ["discord"] = "Discord", ["slack"] = "Slack", ["teams"] = "Teams", ["ms-teams"] = "Teams", ["zoom"] = "Zoom",
        ["outlook"] = "Outlook", ["olk"] = "Outlook", ["thunderbird"] = "Thunderbird",
        ["winword"] = "Word", ["excel"] = "Excel", ["powerpnt"] = "PowerPoint", ["onenote"] = "OneNote",
        ["acrobat"] = "Acrobat", ["acrord32"] = "Acrobat Reader", ["sumatrapdf"] = "SumatraPDF",
        ["explorer"] = "File Explorer", ["code"] = "VS Code", ["devenv"] = "Visual Studio", ["notepad"] = "Notepad",
        ["notepad++"] = "Notepad++", ["spotify"] = "Spotify", ["vlc"] = "VLC", ["photos"] = "Photos",
        ["windowsterminal"] = "Terminal", ["wt"] = "Terminal", ["powershell"] = "PowerShell", ["cmd"] = "Command Prompt",
        ["instagram"] = "Instagram", ["messenger"] = "Messenger", ["notion"] = "Notion", ["obsidian"] = "Obsidian",
        ["figma"] = "Figma", ["snippingtool"] = "Snipping Tool",
    };

    /// <summary>
    /// The friendly name: a known app's usual name, else the program's own description when it reads like a name
    /// ("Google Chrome" → kept, "Microsoft Edge"), else the process name with a capital letter. Null when nothing.
    /// </summary>
    public static string? Friendly(string? processName, string? description = null)
    {
        var process = (processName ?? "").Trim();
        if (process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) process = process[..^4];
        if (Known.TryGetValue(process, out var known)) return known;
        var d = (description ?? "").Trim();
        foreach (var tail in new[] { " Application", " application", " Desktop", " for Windows", " (64-bit)", " (32-bit)" })
            if (d.EndsWith(tail, StringComparison.Ordinal)) d = d[..^tail.Length].Trim();
        if (d.Length is > 1 and <= 32 && !d.Contains('\\') && !d.Contains('.')) return d;
        if (process.Length == 0) return null;
        return char.ToUpperInvariant(process[0]) + process[1..];
    }
}
