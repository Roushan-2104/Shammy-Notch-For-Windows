using System.Text;
using Shammy.Core.Productivity;

namespace Shammy.Core.Agents;

/// <summary>The Settings row for Claude Code: its status line, the button's word and whether it can be pressed.</summary>
public sealed record AgentConnectView(string Status, string Button, bool Enabled);

/// <summary>The words the notch uses for Claude Code sessions, and the answer a hook prints for Claude Code.</summary>
public static class AgentText
{
    /// <summary>The pet's label beside its face (at most 12 characters).</summary>
    public const string PetLabel = "Claude";

    public const string Hidden = "Hidden while you share your screen";

    /// <summary>How many characters of a command or path fit the card's one-line box.</summary>
    public const int BoxChars = 56;

    // ---- What it wants -----------------------------------------------------------------------------

    /// <summary>"Wants to run a command", "Wants to edit a file", "Wants to use &lt;tool&gt;".</summary>
    public static string Wants(string? toolName)
    {
        string tool = (toolName ?? "").Trim();
        switch (tool)
        {
            case "Bash" or "PowerShell" or "Monitor": return "Wants to run a command";
            case "Edit" or "Write" or "MultiEdit" or "NotebookEdit": return "Wants to edit a file";
            case "Read": return "Wants to read a file";
            case "WebFetch": return "Wants to open a web page";
            case "WebSearch": return "Wants to search the web";
            case "": return "Wants to use a tool";
        }
        // "mcp__github__create_issue" reads as "create issue (github)".
        if (tool.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var parts = tool[5..].Split("__", 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) tool = $"{parts[1].Replace('_', ' ')} ({parts[0]})";
            else if (parts.Length == 1) tool = parts[0].Replace('_', ' ');
        }
        return "Wants to use " + MiddleTrim(OneLine(tool), 36);
    }

    /// <summary>The one thing to show about the request: the command, the file, the address. Empty when there is none.</summary>
    public static string Detail(AgentEvent e)
    {
        string[] order = e.ToolName switch
        {
            "Bash" or "PowerShell" or "Monitor" => new[] { "command" },
            "Edit" or "Write" or "MultiEdit" or "Read" => new[] { "file_path" },
            "NotebookEdit" => new[] { "notebook_path", "file_path" },
            "WebFetch" => new[] { "url" },
            "WebSearch" => new[] { "query" },
            _ => new[] { "command", "file_path", "notebook_path", "url", "path", "pattern", "query", "skill", "description" },
        };
        foreach (var name in order)
        {
            var value = OneLine(e.Field(name));
            if (value.Length > 0) return value;
        }
        return "";
    }

    /// <summary>What the card's box shows: one line, cut in the middle if long, or nothing private while the screen is shared.</summary>
    public static string Box(string detail, bool presenting, int max = BoxChars) =>
        presenting ? Hidden : MiddleTrim(OneLine(detail), max);

    /// <summary>Line breaks and runs of spaces become single spaces, so a script fits on one line.</summary>
    public static string OneLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        bool gap = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                gap = sb.Length > 0;
                continue;
            }
            if (gap) sb.Append(' ');
            gap = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Keeps the start and the end ("dotnet test tests/…Tests --filter Agents"), since both say something.</summary>
    public static string MiddleTrim(string text, int max)
    {
        if (text.Length <= max) return text;
        if (max < 3) return text[..Math.Max(0, max)];
        int head = max / 2, tail = max - 1 - head;
        // Never cut a pair of code units in half.
        if (char.IsHighSurrogate(text[head - 1])) head--;
        int tailStart = text.Length - tail;
        if (char.IsLowSurrogate(text[tailStart])) tailStart++;
        return text[..head] + "…" + text[tailStart..];
    }

    // ---- Where and how long ------------------------------------------------------------------------

    /// <summary>The folder's own name ("shammy" for C:\dev\shammy); empty when there is no folder.</summary>
    public static string Folder(string? cwd)
    {
        string path = (cwd ?? "").Trim().TrimEnd('\\', '/');
        int cut = path.LastIndexOfAny(new[] { '\\', '/' });
        return MiddleTrim(cut >= 0 ? path[(cut + 1)..] : path, 28);
    }

    /// <summary>The card's small label: "Claude Code · shammy".</summary>
    public static string CardLabel(string? cwd)
    {
        // Short enough to share its line with "waiting 0:12".
        string folder = MiddleTrim(Folder(cwd), 18);
        return folder.Length == 0 ? "Claude Code" : $"Claude Code · {folder}";
    }

    /// <summary>"waiting 0:12".</summary>
    public static string WaitingFor(TimeSpan waited) => "waiting " + TimeFormat.Elapsed(waited);

    /// <summary>The pet's value: the time working, "Needs you", "Done" or "Stopped" (at most 9 characters).</summary>
    public static string PetValue(AgentSession s, DateTime nowUtc) => s.State switch
    {
        AgentState.Working => TimeFormat.Elapsed(nowUtc - s.TurnStarted),
        AgentState.Waiting => "Needs you",
        _ => s.Failed ? "Stopped" : "Done",
    };

    /// <summary>A session's line in the Coding agents card: "Working · 2:14", "Waiting for you · 0:12", "Done · 3 min ago".</summary>
    public static string StateLine(AgentSession s, DateTime nowUtc) => s.State switch
    {
        AgentState.Working => $"Working · {TimeFormat.Elapsed(nowUtc - s.TurnStarted)}",
        AgentState.Waiting => $"Waiting for you · {TimeFormat.Elapsed(nowUtc - s.WaitingSince)}",
        _ => $"{(s.Failed ? "Stopped on an error" : "Done")} · {Ago(nowUtc - s.FinishedAt)}",
    };

    /// <summary>"just now", "3 min ago", "1 h ago".</summary>
    public static string Ago(TimeSpan since)
    {
        if (since < TimeSpan.FromMinutes(1)) return "just now";
        if (since < TimeSpan.FromHours(1)) return $"{(int)since.TotalMinutes} min ago";
        return $"{(int)since.TotalHours} h ago";
    }

    /// <summary>The card's headline: "2 sessions", "1 session", "No sessions".</summary>
    public static string Sessions(int count) => count switch
    {
        0 => "No sessions",
        1 => "1 session",
        _ => $"{count} sessions",
    };

    /// <summary>The card's one line under the headline: who is doing what.</summary>
    public static string Summary(int working, int waiting, int done)
    {
        var parts = new List<string>();
        if (waiting > 0) parts.Add(waiting == 1 ? "1 waiting for you" : $"{waiting} waiting for you");
        if (working > 0) parts.Add($"{working} working");
        if (done > 0) parts.Add($"{done} done");
        return parts.Count == 0 ? "Nothing running" : string.Join(" · ", parts);
    }

    // ---- Settings ----------------------------------------------------------------------------------

    /// <summary>
    /// The Settings row. <paramref name="found"/>: Claude Code's settings folder exists. <paramref name="listening"/>:
    /// Shammy's port is open (it can be taken by another program after a restart).
    /// </summary>
    public static AgentConnectView ConnectView(bool found, HookState state, bool listening, int sessionsToday) => state switch
    {
        _ when !found => new("Claude Code not found", "Connect", false),
        HookState.Unreadable => new("Claude Code’s settings can’t be read", "Connect", true),
        HookState.NotConnected => new("Not connected", "Connect", true),
        HookState.Stale => new("Needs reconnecting", "Reconnect", true),
        _ when !listening => new("Connected, but Shammy’s port is taken", "Reconnect", true),
        _ => new($"Connected · {(sessionsToday == 0 ? "no sessions" : sessionsToday == 1 ? "1 session" : $"{sessionsToday} sessions")} today", "Disconnect", true),
    };

    public const string ConnectHint = "Adds Shammy’s hooks to Claude Code’s settings. Your other hooks stay as they are.";

    // ---- The answer --------------------------------------------------------------------------------

    /// <summary>What the approval hook prints so Claude Code runs the tool, this once.</summary>
    public const string AllowJson = "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"allow\"}}}";

    /// <summary>What it prints so Claude Code doesn't; the message is what Claude is told.</summary>
    public const string DenyJson =
        "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"deny\",\"message\":\"Denied from the Shammy notch.\"}}}";

    /// <summary>Nothing printed: no decision, so Claude Code asks in its own window as if there were no hook.</summary>
    public const string NoDecision = "";

    /// <summary>
    /// Tools whose prompt is a question only the terminal can answer (a plain "allow" is ignored for them), so
    /// the notch says the session needs you but offers no buttons.
    /// </summary>
    public static bool CanAnswer(string? toolName) => toolName is not ("AskUserQuestion" or "ExitPlanMode") && !string.IsNullOrWhiteSpace(toolName);
}
