using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shammy.Core.Agents;

/// <summary>Where Shammy listens for Claude Code's hooks: always this PC, a port it picked and a secret in the path.</summary>
/// <param name="Curl">The curl program the hook commands start ("C:/Windows/System32/curl.exe").</param>
public sealed record HookEndpoint(int Port, string Token, string Curl = AgentHooks.PlainCurl)
{
    /// <summary>A port a program may listen on and a token that needs no quoting anywhere.</summary>
    public bool IsValid => Port is > 1023 and < 65536 && Token.Length is >= 16 and <= 64 && Token.All(char.IsAsciiLetterOrDigit);
}

/// <summary>One hook Shammy adds: the event, an optional matcher and the command Claude Code runs.</summary>
/// <param name="TimeoutSeconds">Claude Code's limit for the command; null keeps Claude Code's own.</param>
/// <param name="Async">Runs beside Claude Code instead of holding it up (a status ping after every tool call).</param>
public sealed record HookEntry(string Event, string? Matcher, string Command, int? TimeoutSeconds, bool Async = false);

/// <summary>What a settings file says about Shammy's hooks.</summary>
public enum HookState
{
    /// <summary>None of Shammy's hooks are in the file (or there is no file).</summary>
    NotConnected,
    /// <summary>Every hook is there and points at the current port and token.</summary>
    Connected,
    /// <summary>Some of Shammy's hooks are there, but for another port or token, or some are missing.</summary>
    Stale,
    /// <summary>The file is not valid JSON, so nothing can be said.</summary>
    Unreadable,
}

/// <summary>The outcome of editing a settings file's text. With <see cref="Ok"/> false nothing may be written.</summary>
/// <param name="Json">The whole new text of the file.</param>
/// <param name="Error">Why it was refused, in words for the user; empty when it worked.</param>
/// <param name="Changed">False when the file already says exactly this.</param>
public sealed record HookEdit(bool Ok, string Json, string Error, bool Changed);

/// <summary>
/// Shammy's entries in Claude Code's settings file, as pure functions over the file's text. Connecting merges:
/// every key and every hook already there stays exactly as written (same bytes, same order); Shammy's hooks go
/// after them. Each of Shammy's commands carries <see cref="Marker"/> in its address, which is how they are
/// found again. A file that is not valid JSON is never touched. Claude Code reads settings as strict JSON
/// (a comment or a trailing comma is an error there too), so those are refused as well.
/// </summary>
public static class AgentHooks
{
    /// <summary>The path segment every one of Shammy's hook commands contains.</summary>
    public const string Marker = "shammy-hook";

    public const string PlainCurl = "curl.exe";

    /// <summary>How long Claude Code lets the approval hook run (the hook's "timeout").</summary>
    public const int AskHookSeconds = 300;

    /// <summary>curl gives up waiting for Shammy's answer a little before Claude Code would cancel the hook.</summary>
    public const int AskCurlSeconds = 295;

    /// <summary>Shammy stops waiting for Allow or Deny a little before curl gives up, and hands the question back.</summary>
    public const int AskAnswerSeconds = 290;

    const string Tag = "/" + Marker + "/";

    /// <summary>The notification kinds worth a hook: Claude Code is waiting for an answer, or has gone idle.</summary>
    public const string NotificationMatcher = "permission_prompt|idle_prompt";

    // ---- The commands ------------------------------------------------------------------------------

    /// <summary>"http://127.0.0.1:port/shammy-hook/token/kind".</summary>
    public static string Url(HookEndpoint ep, string kind) =>
        string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{ep.Port}{Tag}{ep.Token}/{kind}");

    /// <summary>
    /// A status ping: posts the hook's JSON to Shammy and prints nothing, whatever answers. It gives up after
    /// 0.3 s if nothing is listening and always exits 0, so a closed Shammy looks like no hook at all. One line
    /// that means the same in Git Bash and in PowerShell (Claude Code uses whichever the PC has).
    /// </summary>
    public static string StatusCommand(HookEndpoint ep, int maxSeconds = 2) => string.Create(CultureInfo.InvariantCulture,
        $"{SafeCurl(ep.Curl)} -s -o NUL -m {maxSeconds} --connect-timeout 0.3 -H Content-Type:application/json -H Expect: --data-binary '@-' {Url(ep, HookRoutes.Status)}; exit 0");

    /// <summary>
    /// The approval hook: posts the request, waits for Shammy's answer and prints it (Claude Code reads the
    /// decision from there). No answer in time, or no Shammy, prints nothing: Claude Code then asks as usual.
    /// </summary>
    public static string AskCommand(HookEndpoint ep) => string.Create(CultureInfo.InvariantCulture,
        $"{SafeCurl(ep.Curl)} -s -m {AskCurlSeconds} --connect-timeout 0.3 -H Content-Type:application/json -H Expect: --data-binary '@-' {Url(ep, HookRoutes.Ask)}; exit 0");

    /// <summary>A full path to curl only when it needs no quoting in either shell; otherwise the bare name.</summary>
    public static string SafeCurl(string? path)
    {
        var p = (path ?? "").Trim().Replace('\\', '/');
        return Regex.IsMatch(p, @"^[A-Za-z]:(/[A-Za-z0-9._-]+)+/curl\.exe$", RegexOptions.CultureInvariant) ? p : PlainCurl;
    }

    /// <summary>Everything Connect adds, in the order it is written.</summary>
    public static IReadOnlyList<HookEntry> Entries(HookEndpoint ep)
    {
        string status = StatusCommand(ep);
        return new[]
        {
            new HookEntry("UserPromptSubmit", null, status, 5),
            // After every tool call: beside Claude Code, never in its way.
            new HookEntry("PostToolUse", null, status, 5, Async: true),
            // A tool that ran and failed still means the question before it was answered.
            new HookEntry("PostToolUseFailure", null, status, 5, Async: true),
            new HookEntry("PermissionRequest", null, AskCommand(ep), AskHookSeconds),
            new HookEntry("Notification", NotificationMatcher, status, 5),
            new HookEntry("Stop", null, status, 5),
            new HookEntry("StopFailure", null, status, 5),
            // Claude Code gives hooks 1.5 s as it closes; no timeout of our own, so that budget stays as it is.
            new HookEntry("SessionEnd", null, StatusCommand(ep, 1), null),
        };
    }

    // ---- Reading -----------------------------------------------------------------------------------

    /// <summary>Whether the file holds Shammy's hooks, and whether they are the ones for this endpoint.</summary>
    public static HookState Inspect(string? text, HookEndpoint? ep)
    {
        var body = Body(text, out _);
        if (string.IsNullOrWhiteSpace(body)) return HookState.NotConnected;
        if (JsonError(body) is not null) return HookState.Unreadable;
        List<(string Event, string Command)> found;
        try { found = Ours(JsonSpans.Parse(body)); }
        catch (FormatException) { return HookState.Unreadable; }
        if (found.Count == 0) return HookState.NotConnected;
        if (ep is null || !ep.IsValid) return HookState.Stale;
        static IEnumerable<string> Sorted(IEnumerable<(string Event, string Command)> list) =>
            list.Select(e => e.Event + "\n" + e.Command).OrderBy(e => e, StringComparer.Ordinal);
        return Sorted(Entries(ep).Select(e => (e.Event, e.Command))).SequenceEqual(Sorted(found)) ? HookState.Connected : HookState.Stale;
    }

    /// <summary>Shammy's hooks in the file, as "Event: command" lines (for the test pipe).</summary>
    public static IReadOnlyList<string> Found(string? text)
    {
        var body = Body(text, out _);
        if (string.IsNullOrWhiteSpace(body) || JsonError(body) is not null) return Array.Empty<string>();
        try { return Ours(JsonSpans.Parse(body)).Select(o => $"{o.Event}: {o.Command}").ToList(); }
        catch (FormatException) { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Everything in the file that is not Shammy's, piece by piece, exactly as written: each other key's value
    /// and each other hook. Connecting and disconnecting must leave this list as it was.
    /// </summary>
    public static IReadOnlyList<string> Others(string? text)
    {
        var body = Body(text, out _);
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(body)) return list;
        var root = JsonSpans.Parse(body);
        string Raw(SpanNode n) => body[n.Start..n.End];
        if (root.Kind != SpanKind.Object)
        {
            list.Add(Raw(root));
            return list;
        }
        foreach (var m in root.Members)
        {
            if (m.Key != "hooks" || m.Value.Kind != SpanKind.Object)
            {
                list.Add($"{m.Key}={Raw(m.Value)}");
                continue;
            }
            foreach (var ev in m.Value.Members)
            {
                if (ev.Value.Kind != SpanKind.Array)
                {
                    list.Add($"hooks.{ev.Key}={Raw(ev.Value)}");
                    continue;
                }
                foreach (var group in ev.Value.Items)
                {
                    if (GroupOurs(group)) continue;
                    if (group.Kind != SpanKind.Object || group.Find("hooks") is not { Value.Kind: SpanKind.Array } handlers)
                    {
                        list.Add($"hooks.{ev.Key}[]={Raw(group)}");
                        continue;
                    }
                    var sb = new StringBuilder($"hooks.{ev.Key}[]");
                    foreach (var gm in group.Members)
                    {
                        if (!ReferenceEquals(gm, handlers)) sb.Append('|').Append(gm.Key).Append('=').Append(Raw(gm.Value));
                    }
                    foreach (var h in handlers.Value.Items)
                    {
                        if (!HandlerOurs(h)) sb.Append("|hook=").Append(Raw(h));
                    }
                    list.Add(sb.ToString());
                }
            }
        }
        return list;
    }

    // ---- Editing -----------------------------------------------------------------------------------

    /// <summary>
    /// The file's text with Shammy's hooks for <paramref name="ep"/> merged in. Hooks of Shammy's from an earlier
    /// connect (another port or token) are replaced, so connecting twice changes nothing the second time.
    /// </summary>
    public static HookEdit Connect(string? text, HookEndpoint ep)
    {
        string original = text ?? "";
        if (!ep.IsValid) return Refuse(original, "Shammy has no port to listen on yet.");
        var body = Body(original, out string bom);
        if (string.IsNullOrWhiteSpace(body)) body = "{}\n";
        if (JsonError(body) is { } bad) return Refuse(original, bad);
        try
        {
            if (JsonSpans.Parse(body).Kind != SpanKind.Object) return Refuse(original, UnknownShape);
            var before = Others(body);
            string result = AddOurs(RemoveOurs(body), ep);
            if (JsonError(result) is not null || Inspect(result, ep) != HookState.Connected || !before.SequenceEqual(Others(result)))
                return Refuse(original, "Shammy couldn’t add its hooks safely, so it left Claude Code’s settings alone.");
            result = bom + result;
            return new HookEdit(true, result, "", result != original);
        }
        catch (FormatException ex)
        {
            return Refuse(original, ex is ShapeException ? ex.Message : UnknownShape);
        }
    }

    /// <summary>The file's text without Shammy's hooks. Everything else stays exactly as written.</summary>
    public static HookEdit Disconnect(string? text)
    {
        string original = text ?? "";
        var body = Body(original, out string bom);
        if (string.IsNullOrWhiteSpace(body)) return new HookEdit(true, original, "", false);
        if (JsonError(body) is { } bad) return Refuse(original, bad);
        try
        {
            var before = Others(body);
            string result = RemoveOurs(body);
            if (JsonError(result) is not null || Ours(JsonSpans.Parse(result)).Count != 0 || !before.SequenceEqual(Others(result)))
                return Refuse(original, "Shammy couldn’t remove its hooks safely, so it left Claude Code’s settings alone.");
            result = bom + result;
            return new HookEdit(true, result, "", result != original);
        }
        catch (FormatException)
        {
            return Refuse(original, UnknownShape);
        }
    }

    const string UnknownShape = "Claude Code’s settings aren’t laid out the way Shammy expects, so it left them alone.";

    sealed class ShapeException(string message) : FormatException(message);

    static HookEdit Refuse(string original, string why) => new(false, original, why, false);

    /// <summary>The text without a leading byte-order mark (kept aside so it can be put back).</summary>
    static string Body(string? text, out string bom)
    {
        text ??= "";
        bom = text.Length > 0 && text[0] == '\uFEFF' ? "\uFEFF" : "";
        return text[bom.Length..];
    }

    /// <summary>Null for valid strict JSON; otherwise the sentence to show.</summary>
    static string? JsonError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            return null;
        }
        catch (JsonException ex)
        {
            string where = ex.LineNumber is { } line ? $" (line {line + 1})" : "";
            return $"Claude Code’s settings file isn’t valid JSON{where}, so Shammy left it alone. Fix the file, then connect again.";
        }
    }

    // ---- Finding Shammy's entries ------------------------------------------------------------------

    static bool HandlerOurs(SpanNode n) =>
        n.Kind == SpanKind.Object
        && (n.Find("command") is { Value: { Kind: SpanKind.String } c } && c.Text.Contains(Tag, StringComparison.Ordinal)
            || n.Find("url") is { Value: { Kind: SpanKind.String } u } && u.Text.Contains(Tag, StringComparison.Ordinal));

    static bool GroupOurs(SpanNode n) =>
        n.Kind == SpanKind.Object && n.Find("hooks") is { Value: { Kind: SpanKind.Array, Items.Count: > 0 } hs } && hs.Items.All(HandlerOurs);

    static bool EventOurs(SpanMember m) => m.Value is { Kind: SpanKind.Array, Items.Count: > 0 } && m.Value.Items.All(GroupOurs);

    static SpanNode? HooksObject(SpanNode root) =>
        root.Kind == SpanKind.Object && root.Find("hooks") is { Value.Kind: SpanKind.Object } m ? m.Value : null;

    static List<(string Event, string Command)> Ours(SpanNode root)
    {
        var found = new List<(string, string)>();
        if (HooksObject(root) is not { } hooks) return found;
        foreach (var ev in hooks.Members)
        {
            if (ev.Value.Kind != SpanKind.Array) continue;
            foreach (var group in ev.Value.Items)
            {
                if (group.Kind != SpanKind.Object || group.Find("hooks") is not { Value.Kind: SpanKind.Array } hs) continue;
                foreach (var h in hs.Value.Items)
                {
                    if (HandlerOurs(h)) found.Add((ev.Key, h.Find("command")?.Value.Text ?? h.Find("url")?.Value.Text ?? ""));
                }
            }
        }
        return found;
    }

    /// <summary>The biggest piece that is wholly Shammy's: the hooks key, an event, a matcher group, or one hook.</summary>
    static (SpanNode Container, int Index)? NextOurs(SpanNode root)
    {
        if (HooksObject(root) is not { } hooks) return null;
        if (hooks.Members.Count > 0 && hooks.Members.All(EventOurs))
            return (root, root.Members.FindLastIndex(m => m.Key == "hooks"));
        for (int e = 0; e < hooks.Members.Count; e++)
        {
            var ev = hooks.Members[e];
            if (EventOurs(ev)) return (hooks, e);
            if (ev.Value.Kind != SpanKind.Array) continue;
            for (int g = 0; g < ev.Value.Items.Count; g++)
            {
                var group = ev.Value.Items[g];
                if (GroupOurs(group)) return (ev.Value, g);
                if (group.Kind != SpanKind.Object || group.Find("hooks") is not { Value.Kind: SpanKind.Array } hs) continue;
                int h = hs.Value.Items.FindIndex(HandlerOurs);
                if (h >= 0) return (hs.Value, h);
            }
        }
        return null;
    }

    static string RemoveOurs(string body)
    {
        for (int guard = 0; guard < 2000; guard++)
        {
            if (NextOurs(JsonSpans.Parse(body)) is not { } target) return body;
            body = RemoveChild(body, target.Container, target.Index);
        }
        throw new FormatException("Too many entries");
    }

    static string AddOurs(string body, HookEndpoint ep)
    {
        string nl = body.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var root = JsonSpans.Parse(body);
        if (root.Kind != SpanKind.Object) throw new ShapeException(UnknownShape);
        string unit = IndentUnit(body, root);

        if (root.Find("hooks") is null) body = AddChild(body, root, nl, unit, _ => "\"hooks\": {}");
        foreach (var entry in Entries(ep))
        {
            if (JsonSpans.Parse(body).Find("hooks") is not { Value.Kind: SpanKind.Object } hooks) throw new ShapeException(UnknownShape);
            if (hooks.Value.Find(entry.Event) is null)
            {
                body = AddChild(body, hooks.Value, nl, unit, _ => JsonSpans.Quote(entry.Event) + ": []");
                hooks = JsonSpans.Parse(body).Find("hooks")!;
            }
            if (hooks.Value.Find(entry.Event) is not { Value.Kind: SpanKind.Array } ev) throw new ShapeException(UnknownShape);
            body = AddChild(body, ev.Value, nl, unit, indent => GroupText(entry, indent, unit, nl));
        }
        return body;
    }

    /// <summary>One matcher group holding one command hook, written at <paramref name="indent"/>.</summary>
    static string GroupText(HookEntry e, string indent, string unit, string nl)
    {
        string i1 = indent + unit, i2 = i1 + unit, i3 = i2 + unit;
        var sb = new StringBuilder("{").Append(nl);
        if (e.Matcher is not null) sb.Append(i1).Append("\"matcher\": ").Append(JsonSpans.Quote(e.Matcher)).Append(',').Append(nl);
        sb.Append(i1).Append("\"hooks\": [").Append(nl);
        sb.Append(i2).Append('{').Append(nl);
        sb.Append(i3).Append("\"type\": \"command\",").Append(nl);
        sb.Append(i3).Append("\"command\": ").Append(JsonSpans.Quote(e.Command));
        if (e.TimeoutSeconds is { } t) sb.Append(',').Append(nl).Append(i3).Append("\"timeout\": ").Append(t.ToString(CultureInfo.InvariantCulture));
        if (e.Async) sb.Append(',').Append(nl).Append(i3).Append("\"async\": true");
        sb.Append(nl).Append(i2).Append('}').Append(nl);
        sb.Append(i1).Append(']').Append(nl);
        sb.Append(indent).Append('}');
        return sb.ToString();
    }

    // ---- Changing the text in one place ------------------------------------------------------------

    /// <summary>Adds a child after the last one. <see cref="RemoveChild"/> on it gives the text back exactly.</summary>
    static string AddChild(string body, SpanNode container, string nl, string unit, Func<string, string> child)
    {
        string own = LineIndent(body, container.Start);
        if (container.Count == 0)
        {
            string indent = own + unit;
            return body[..(container.Start + 1)] + nl + indent + child(indent) + nl + own + body[(container.End - 1)..];
        }
        int first = container.ChildStart(0);
        string inside = StartsItsLine(body, first) ? LineIndent(body, first) : own + unit;
        int at = container.ChildEnd(container.Count - 1);
        return body[..at] + "," + nl + inside + child(inside) + body[at..];
    }

    /// <summary>Removes one child and the comma that went with it; the last child leaves "{}" or "[]".</summary>
    static string RemoveChild(string body, SpanNode container, int index)
    {
        int count = container.Count;
        if (count == 1) return body[..(container.Start + 1)] + body[(container.End - 1)..];
        if (index > 0) return body[..container.ChildEnd(index - 1)] + body[container.ChildEnd(index)..];
        return body[..container.ChildStart(0)] + body[container.ChildStart(1)..];
    }

    static int LineStart(string body, int pos)
    {
        int i = pos > 0 ? body.LastIndexOf('\n', pos - 1) : -1;
        return i + 1;
    }

    /// <summary>The spaces or tabs the line holding <paramref name="pos"/> begins with.</summary>
    static string LineIndent(string body, int pos)
    {
        int start = LineStart(body, pos), end = start;
        while (end < body.Length && body[end] is ' ' or '\t') end++;
        return body[start..Math.Min(end, pos)];
    }

    static bool StartsItsLine(string body, int pos)
    {
        for (int i = LineStart(body, pos); i < pos; i++)
            if (body[i] is not (' ' or '\t')) return false;
        return true;
    }

    /// <summary>The file's own indent step (what its first key is indented by), or two spaces.</summary>
    static string IndentUnit(string body, SpanNode root)
    {
        if (root.Count > 0 && StartsItsLine(body, root.ChildStart(0)))
        {
            string indent = LineIndent(body, root.ChildStart(0));
            string own = LineIndent(body, root.Start);
            if (indent.Length > own.Length && indent.StartsWith(own, StringComparison.Ordinal)) return indent[own.Length..];
        }
        return "  ";
    }
}

/// <summary>The two things a hook can ask of Shammy's listener (the last segment of the address).</summary>
public static class HookRoutes
{
    public const string Status = "status", Ask = "ask";
}
