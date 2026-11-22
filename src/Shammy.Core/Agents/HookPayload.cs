using System.Text.Json;

namespace Shammy.Core.Agents;

/// <summary>What a hook from Claude Code means to the notch.</summary>
public enum AgentEventKind
{
    /// <summary>A prompt was sent: a turn begins (UserPromptSubmit).</summary>
    Prompt,
    /// <summary>A tool call finished (PostToolUse): the session is alive and working.</summary>
    ToolDone,
    /// <summary>Claude Code is about to ask whether a tool may run (PermissionRequest); this one can be answered.</summary>
    Permission,
    /// <summary>Claude Code says it is waiting on the user (a Notification about a prompt or a question).</summary>
    NeedsInput,
    /// <summary>Claude Code says it finished a while ago and nothing was typed since (Notification idle_prompt).</summary>
    Idle,
    /// <summary>The turn is over (Stop).</summary>
    Stop,
    /// <summary>The turn ended on an error (StopFailure).</summary>
    StopFailed,
    /// <summary>The session closed (SessionEnd).</summary>
    SessionEnd,
    /// <summary>Anything else: proof of life only.</summary>
    Other,
}

/// <summary>One hook payload, reduced to what the notch needs.</summary>
/// <param name="Cwd">The folder Claude Code is working in; empty when the payload had none.</param>
/// <param name="ToolName">For tool events ("Bash", "Edit", "mcp__github__create_issue"); empty otherwise.</param>
/// <param name="Fields">The tool input's plain text fields ("command", "file_path", …), each cut to a sane length.</param>
public sealed record AgentEvent(AgentEventKind Kind, string SessionId, string Cwd, string HookEvent, string ToolName,
    IReadOnlyDictionary<string, string> Fields)
{
    public string Field(string name) => Fields.TryGetValue(name, out var v) ? v : "";

    /// <summary>Claude Code's id for this tool call (ties a PreToolUse to its PostToolUse); empty when there was none.</summary>
    public string ToolUseId { get; init; } = "";

    /// <summary>
    /// The tool's whole input as JSON, kept only for the tools whose input is a structure the wing reads (Claude's
    /// questions, its task list); empty otherwise.
    /// </summary>
    public string Input { get; init; } = "";

    /// <summary>The text of Claude's final reply (Stop's "last_assistant_message"), cut to a sane length.</summary>
    public string LastMessage { get; init; } = "";
}

/// <summary>
/// Reads the JSON Claude Code sends a hook. Only a handful of short fields matter, so it reads forwards and
/// stops caring where the text ends: a payload cut off in the middle of a big file's content still yields its
/// session, event and tool, as long as those came first (they do).
/// </summary>
public static class HookPayload
{
    /// <summary>Longest value kept from any one field.</summary>
    public const int MaxField = 4000;

    const int MaxId = 128;

    static readonly HashSet<string> ToolFields = new(StringComparer.Ordinal)
    {
        "command", "file_path", "notebook_path", "path", "url", "pattern", "query", "description", "skill", "subagent_type",
    };

    /// <summary>Tools whose whole input is kept (<see cref="AgentEvent.Input"/>): small structures the wing reads.</summary>
    public static readonly IReadOnlySet<string> KeepInput = new HashSet<string>(StringComparer.Ordinal)
    {
        "AskUserQuestion", "TodoWrite", "TaskCreate", "TaskUpdate",
    };

    /// <summary>Largest kept input (a question list or a task list is far smaller).</summary>
    public const int MaxInput = 64 * 1024;

    /// <summary>Null when it isn't a hook payload (no JSON object, or no usable session id).</summary>
    public static AgentEvent? Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF) utf8 = utf8[3..];
        string session = "", cwd = "", hookEvent = "", tool = "", notification = "", toolUseId = "", input = "", lastMessage = "";
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var reader = new Utf8JsonReader(utf8, isFinalBlock: false, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            string? name = null, toolField = null;
            bool inToolInput = false;
            while (reader.Read())
            {
                int depth = reader.CurrentDepth;
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName when depth == 1:
                        name = reader.GetString();
                        break;
                    case JsonTokenType.PropertyName when depth == 2 && inToolInput:
                        toolField = reader.GetString();
                        break;
                    case JsonTokenType.StartObject when depth == 1 && name == "tool_input" && KeepInput.Contains(tool):
                    {
                        // The whole object, as text: the wing reads its structure. A cut-off one is left out.
                        long from = reader.TokenStartIndex;
                        if (reader.TrySkip() && reader.BytesConsumed - from <= MaxInput)
                            input = System.Text.Encoding.UTF8.GetString(utf8[(int)from..(int)reader.BytesConsumed]);
                        break;
                    }
                    case JsonTokenType.StartObject when depth == 1:
                        inToolInput = name == "tool_input";
                        break;
                    case JsonTokenType.EndObject when depth == 1:
                        inToolInput = false;
                        break;
                    case JsonTokenType.String when depth == 1:
                        switch (name)
                        {
                            case "session_id": session = reader.GetString() ?? ""; break;
                            case "cwd": cwd = Cut(reader.GetString()); break;
                            case "hook_event_name": hookEvent = Cut(reader.GetString(), 64); break;
                            case "tool_name": tool = Cut(reader.GetString(), 200); break;
                            case "notification_type": notification = Cut(reader.GetString(), 64); break;
                            case "tool_use_id": toolUseId = Cut(reader.GetString(), MaxId); break;
                            case "last_assistant_message": lastMessage = Cut(reader.GetString()); break;
                        }
                        break;
                    case JsonTokenType.String when depth == 2 && inToolInput && toolField is not null && ToolFields.Contains(toolField):
                        fields[toolField] = Cut(reader.GetString());
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Cut off or damaged further on: what was read so far still counts.
        }
        catch (InvalidOperationException)
        {
            // A string that isn't valid text: same.
        }

        if (session.Length is 0 or > MaxId || session.Any(char.IsControl) || hookEvent.Length == 0) return null;
        return new AgentEvent(Kind(hookEvent, notification), session, cwd, hookEvent, tool, fields)
        {
            ToolUseId = toolUseId,
            Input = input,
            LastMessage = lastMessage,
        };
    }

    static string Cut(string? value, int max = MaxField)
    {
        value ??= "";
        if (value.Length <= max) return value;
        // Never leave half of a pair behind.
        int end = char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return value[..end];
    }

    public static AgentEventKind Kind(string hookEvent, string notificationType = "") => hookEvent switch
    {
        "UserPromptSubmit" => AgentEventKind.Prompt,
        "PostToolUse" or "PostToolUseFailure" or "PreToolUse" => AgentEventKind.ToolDone,
        "PermissionRequest" => AgentEventKind.Permission,
        "Notification" => notificationType switch
        {
            "permission_prompt" or "elicitation_dialog" or "elicitation_url_dialog" or "agent_needs_input" => AgentEventKind.NeedsInput,
            "idle_prompt" => AgentEventKind.Idle,
            _ => AgentEventKind.Other,
        },
        "Stop" => AgentEventKind.Stop,
        "StopFailure" => AgentEventKind.StopFailed,
        "SessionEnd" => AgentEventKind.SessionEnd,
        _ => AgentEventKind.Other,
    };
}
