using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Shammy.Core.Agents;

/// <summary>What sort of thing a step is (its little icon in the wing).</summary>
public enum StepKind { Read, Edit, Run, Find, Web, Helper, Ask, Tool }

/// <summary>One thing Claude Code did, in plain words: "Reading MusicModule.cs", "Running tests".</summary>
public sealed record AgentStep(string SessionId, string ToolUseId, StepKind Kind, string Text, DateTime AtUtc)
{
    /// <summary>The tool reported a failure (PostToolUseFailure).</summary>
    public bool Failed { get; set; }

    /// <summary>The tool finished (or failed); false while it still runs.</summary>
    public bool Finished { get; set; }
}

/// <summary>
/// The wing's words for a step, made on this PC from the hook's own fields: the action and the file, never the
/// file's contents. Tools that only keep Claude's task list (TodoWrite, TaskCreate, TaskUpdate) aren't steps.
/// </summary>
public static class StepWords
{
    public const int MaxText = 60;

    static readonly HashSet<string> TaskTools = new(StringComparer.Ordinal) { "TodoWrite", "TaskCreate", "TaskUpdate", "TaskList", "TaskGet" };

    /// <summary>Null for a tool that isn't a step worth showing.</summary>
    public static (StepKind Kind, string Text)? Describe(string? toolName, Func<string, string> field)
    {
        string tool = (toolName ?? "").Trim();
        if (tool.Length == 0 || TaskTools.Contains(tool)) return null;
        string file = FileName(field("file_path"));
        switch (tool)
        {
            case "Read": return (StepKind.Read, file.Length > 0 ? "Reading " + file : "Reading a file");
            case "Edit" or "MultiEdit": return (StepKind.Edit, file.Length > 0 ? "Editing " + file : "Editing a file");
            case "Write": return (StepKind.Edit, file.Length > 0 ? "Writing " + file : "Writing a file");
            case "NotebookEdit":
                string nb = FileName(field("notebook_path"));
                return (StepKind.Edit, nb.Length > 0 ? "Editing " + nb : "Editing a notebook");
            case "Bash" or "PowerShell" or "Monitor": return (StepKind.Run, Command(field("command"), field("description")));
            case "Grep" or "Glob":
                string pattern = AgentText.OneLine(field("pattern"));
                return (StepKind.Find, pattern.Length > 0 ? $"Searching for “{Trim(pattern, 28)}”" : "Searching the code");
            case "WebFetch": return (StepKind.Web, Host(field("url")) is { Length: > 0 } host ? "Opening " + host : "Opening a web page");
            case "WebSearch":
                string q = AgentText.OneLine(field("query"));
                return (StepKind.Web, q.Length > 0 ? $"Searching the web for “{Trim(q, 26)}”" : "Searching the web");
            case "Task" or "Agent":
                string what = AgentText.OneLine(field("description"));
                return (StepKind.Helper, what.Length > 0 ? "Helper: " + what : "Starting a helper");
            case "Skill":
                string skill = AgentText.OneLine(field("skill"));
                return (StepKind.Tool, skill.Length > 0 ? "Using the " + skill + " skill" : "Using a skill");
            case "AskUserQuestion": return (StepKind.Ask, "Asking you");
            case "ExitPlanMode": return (StepKind.Ask, "Showing you its plan");
        }
        if (tool.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var parts = tool[5..].Split("__", 2, StringSplitOptions.RemoveEmptyEntries);
            string name = parts.Length == 2 ? $"{parts[1].Replace('_', ' ')} ({parts[0]})" : parts.FirstOrDefault()?.Replace('_', ' ') ?? tool;
            return (StepKind.Tool, Fit("Using " + name));
        }
        return (StepKind.Tool, Fit("Using " + tool));
    }

    /// <summary>"dotnet test …" → "Running tests"; "npm run build" → "Building"; else "Running dotnet".</summary>
    static string Command(string command, string description)
    {
        string c = AgentText.OneLine(command).ToLowerInvariant();
        if (c.Length == 0) return "Running a command";
        if (Regex.IsMatch(c, @"(^|[\s;&|])(dotnet\s+test|npm\s+(run\s+)?test|pnpm\s+test|yarn\s+test|pytest|jest|vitest|cargo\s+test|go\s+test|mvn\s+test|gradle\w*\s+test)\b"))
            return "Running tests";
        if (Regex.IsMatch(c, @"(^|[\s;&|])(dotnet\s+build|npm\s+run\s+build|pnpm\s+build|yarn\s+build|cargo\s+build|go\s+build|msbuild|make)\b")) return "Building";
        if (Regex.IsMatch(c, @"(^|[\s;&|])(npm|pnpm|yarn)\s+(install|i|add)\b|pip\s+install|dotnet\s+(add|restore)\b")) return "Installing packages";
        if (Regex.IsMatch(c, @"^\s*git\s+commit\b")) return "Committing";
        if (Regex.IsMatch(c, @"^\s*git\s+push\b")) return "Pushing";
        if (Regex.IsMatch(c, @"^\s*git\b")) return "Using git";
        // Claude Code's own short description, when it gave one, says it best ("List the files").
        string said = AgentText.OneLine(description);
        if (said.Length is > 0 and <= 48) return said;
        string first = c.TrimStart('&', ' ', '.', '(').Split(' ', 2)[0];
        first = FileName(first.Trim('"', '\''));
        return first.Length is > 0 and <= 24 ? "Running " + first : "Running a command";
    }

    /// <summary>"C:\dev\shammy\src\MusicModule.cs" → "MusicModule.cs".</summary>
    public static string FileName(string? path)
    {
        var p = AgentText.OneLine(path).TrimEnd('/', '\\');
        int cut = p.LastIndexOfAny(new[] { '/', '\\' });
        return Fit(cut >= 0 ? p[(cut + 1)..] : p);
    }

    static string Host(string url) => Uri.TryCreate(AgentText.OneLine(url), UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "";

    static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    static string Fit(string s) => Trim(s, MaxText);
}

/// <summary>A task in Claude's own list ("Find the cause", "Fix it"), as the wing shows it.</summary>
public enum TaskMark { Todo, Now, Done }

public sealed record AgentTask(string Id, string Text, TaskMark Mark);

/// <summary>
/// Claude's task list for each session, from the tools that keep it: TodoWrite (the whole list each time) and
/// TaskCreate / TaskUpdate (one task at a time; tasks are numbered 1, 2, 3 … in the order they were made).
/// </summary>
public sealed class AgentTaskLists
{
    public const int MaxTasks = 30;
    readonly Dictionary<string, List<AgentTask>> _lists = new(StringComparer.Ordinal);

    public IReadOnlyList<AgentTask> For(string sessionId) => _lists.TryGetValue(sessionId, out var l) ? l : Array.Empty<AgentTask>();

    /// <summary>The sessions that have a list.</summary>
    public IReadOnlyCollection<string> Sessions => _lists.Keys;

    public void Forget(string sessionId) => _lists.Remove(sessionId);

    public void Clear() => _lists.Clear();

    /// <summary>Applies a task tool's input (JSON); true when the list changed.</summary>
    public bool Apply(string sessionId, string toolName, string inputJson)
    {
        JsonNode? input;
        try { input = JsonNode.Parse(inputJson); }
        catch (JsonException) { return false; }
        if (input is not JsonObject o) return false;
        switch (toolName)
        {
            case "TodoWrite":
                if (o["todos"] is not JsonArray todos) return false;
                var list = new List<AgentTask>();
                foreach (var t in todos.OfType<JsonObject>().Take(MaxTasks))
                {
                    string text = Str(t, "content");
                    if (text.Length == 0) continue;
                    var mark = Mark(Str(t, "status"));
                    // While it's being done, Claude's "activeForm" ("Fixing the card") reads better.
                    if (mark == TaskMark.Now && Str(t, "activeForm") is { Length: > 0 } active) text = active;
                    list.Add(new AgentTask((list.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Clean(text), mark));
                }
                _lists[sessionId] = list;
                return true;
            case "TaskCreate":
            {
                string text = Str(o, "subject");
                if (text.Length == 0) text = Str(o, "description");
                if (text.Length == 0) return false;
                if (!_lists.TryGetValue(sessionId, out var l)) _lists[sessionId] = l = new List<AgentTask>();
                if (l.Count >= MaxTasks) return false;
                l.Add(new AgentTask((l.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Clean(text), TaskMark.Todo));
                return true;
            }
            case "TaskUpdate":
            {
                string id = Str(o, "taskId");
                if (id.Length == 0 && o["taskId"] is JsonValue v && v.TryGetValue(out int n)) id = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!_lists.TryGetValue(sessionId, out var l)) return false;
                int i = l.FindIndex(t => t.Id == id);
                if (i < 0) return false;
                string status = Str(o, "status");
                if (status == "deleted")
                {
                    l.RemoveAt(i);
                    return true;
                }
                var task = l[i];
                string subject = Str(o, "subject");
                l[i] = task with { Text = subject.Length > 0 ? Clean(subject) : task.Text, Mark = status.Length > 0 ? Mark(status) : task.Mark };
                return true;
            }
        }
        return false;
    }

    static TaskMark Mark(string status) => status switch
    {
        "completed" => TaskMark.Done,
        "in_progress" => TaskMark.Now,
        _ => TaskMark.Todo,
    };

    static string Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";

    static string Clean(string s)
    {
        s = AgentText.OneLine(s);
        return s.Length <= StepWords.MaxText ? s : s[..(StepWords.MaxText - 1)] + "…";
    }
}

/// <summary>The steps of every session, newest first, plus each turn's count for "Done · 12 steps · 4 min".</summary>
public sealed class AgentFeed
{
    public const int Keep = 40;
    readonly List<AgentStep> _steps = new();
    readonly Dictionary<string, int> _turnSteps = new(StringComparer.Ordinal);

    /// <summary>The newest <paramref name="count"/> steps across sessions.</summary>
    public IReadOnlyList<AgentStep> Recent(int count) => _steps.Take(count).ToList();

    public int TurnSteps(string sessionId) => _turnSteps.TryGetValue(sessionId, out var n) ? n : 0;

    /// <summary>A new turn: its count starts over.</summary>
    public void NewTurn(string sessionId) => _turnSteps[sessionId] = 0;

    /// <summary>
    /// A tool started (PreToolUse) or finished (PostToolUse / PostToolUseFailure). The start adds the step; the end
    /// marks it finished (or failed), and adds it if the start was never heard. Returns the step, or null.
    /// </summary>
    public AgentStep? Note(AgentEvent e, DateTime nowUtc)
    {
        bool start = e.HookEvent == "PreToolUse", failed = e.HookEvent == "PostToolUseFailure";
        if (e.ToolUseId.Length > 0 && _steps.FirstOrDefault(s => s.ToolUseId == e.ToolUseId && s.SessionId == e.SessionId) is { } known)
        {
            if (!start)
            {
                known.Finished = true;
                known.Failed |= failed;
            }
            return known;
        }
        if (StepWords.Describe(e.ToolName, e.Field) is not { } words) return null;
        var step = new AgentStep(e.SessionId, e.ToolUseId, words.Kind, words.Text, nowUtc) { Finished = !start, Failed = failed };
        _steps.Insert(0, step);
        if (_steps.Count > Keep) _steps.RemoveRange(Keep, _steps.Count - Keep);
        _turnSteps[e.SessionId] = TurnSteps(e.SessionId) + 1;
        return step;
    }

    public void Forget(string sessionId)
    {
        _steps.RemoveAll(s => s.SessionId == sessionId);
        _turnSteps.Remove(sessionId);
    }

    public void Clear()
    {
        _steps.Clear();
        _turnSteps.Clear();
    }
}

/// <summary>One of the questions in Claude Code's AskUserQuestion: the question, its short header and the choices.</summary>
public sealed record AgentQuestion(string Question, string Header, IReadOnlyList<(string Label, string Description)> Options, bool MultiSelect);

/// <summary>
/// Claude Code's own questions (the AskUserQuestion tool). They arrive in a PermissionRequest hook; the answer goes
/// back as "allow" with the tool's input repeated plus "answers" (question → chosen label), which is how Claude Code
/// documents a hook answering them.
/// </summary>
public static class AgentQuestions
{
    public const int MaxQuestions = 4, MaxOptions = 8;

    /// <summary>The questions in the tool's input; empty when it holds none Shammy can show.</summary>
    public static IReadOnlyList<AgentQuestion> Parse(string inputJson)
    {
        var result = new List<AgentQuestion>();
        JsonNode? input;
        try { input = JsonNode.Parse(inputJson); }
        catch (JsonException) { return result; }
        if (input is not JsonObject o || o["questions"] is not JsonArray qs) return result;
        foreach (var q in qs.OfType<JsonObject>().Take(MaxQuestions))
        {
            string question = Str(q, "question");
            if (question.Length == 0) continue;
            var options = new List<(string, string)>();
            if (q["options"] is JsonArray opts)
            {
                foreach (var opt in opts.Take(MaxOptions))
                {
                    if (opt is JsonObject oo && Str(oo, "label") is { Length: > 0 } label) options.Add((label, Str(oo, "description")));
                    else if (opt is JsonValue ov && ov.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s)) options.Add((s, ""));
                }
            }
            bool multi = q["multiSelect"] is JsonValue m && m.TryGetValue(out bool b) && b;
            result.Add(new AgentQuestion(question, Str(q, "header"), options, multi));
        }
        return result;
    }

    /// <summary>
    /// What the hook prints to answer: the original input with "answers" added. <paramref name="answers"/> maps each
    /// question's text to the chosen label (several labels joined by ", " for a multiple choice) or the typed answer.
    /// </summary>
    public static string AnswerJson(string inputJson, IReadOnlyDictionary<string, string> answers)
    {
        var input = JsonNode.Parse(inputJson) as JsonObject ?? new JsonObject();
        var map = new JsonObject();
        foreach (var (q, a) in answers) map[q] = a;
        input["answers"] = map;
        var output = new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PermissionRequest",
                ["decision"] = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input },
            },
        };
        return output.ToJsonString();
    }

    static string Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue(out string? s) ? (s ?? "").Trim() : "";
}

/// <summary>
/// A plain question at the end of Claude's reply ("Shall I commit it?"). Hooks can't answer those, so the wing shows
/// the question and takes you to the window to reply.
/// </summary>
public static class EndQuestion
{
    /// <summary>The question Claude ended its reply on, or empty when it ended on something else.</summary>
    public static string Find(string? lastMessage)
    {
        var text = (lastMessage ?? "").Trim();
        if (text.Length == 0) return "";
        // Only the last paragraph counts: a question earlier in the reply was already answered by what follows.
        int para = text.LastIndexOf("\n\n", StringComparison.Ordinal);
        string last = AgentText.OneLine(para >= 0 ? text[(para + 2)..] : text).Trim('*', '_', ' ', '#', '>');
        if (!last.EndsWith('?')) return "";
        // The sentence holding the question mark: back to the previous sentence's end.
        int start = 0;
        for (int i = last.Length - 2; i >= 0; i--)
        {
            if (last[i] is '.' or '!' or '?' or ':' && i + 1 < last.Length && last[i + 1] == ' ')
            {
                start = i + 2;
                break;
            }
        }
        string q = last[start..].Trim();
        return q.Length <= 140 ? q : q[..139] + "…";
    }
}

/// <summary>A small helper to build the hook's tool input as text again (for tests and the test pipe).</summary>
public static class AgentJson
{
    public static string Questions(params (string Question, string Header, string[] Options, bool Multi)[] questions)
    {
        var sb = new StringBuilder("{\"questions\":[");
        for (int i = 0; i < questions.Length; i++)
        {
            var (q, h, opts, multi) = questions[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"question\":").Append(JsonSerializer.Serialize(q)).Append(",\"header\":").Append(JsonSerializer.Serialize(h))
              .Append(",\"multiSelect\":").Append(multi ? "true" : "false").Append(",\"options\":[");
            sb.Append(string.Join(",", opts.Select(o => "{\"label\":" + JsonSerializer.Serialize(o) + ",\"description\":\"\"}")));
            sb.Append("]}");
        }
        return sb.Append("]}").ToString();
    }
}
