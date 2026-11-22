using System.Text.Json;

namespace Shammy.Core.Agents;

/// <summary>
/// Claude Code's own sign-in, from the outside: where its command line usually lives, and what
/// "claude auth status --json" says. Shammy never sees or keeps a token; it only asks Claude Code whether it's signed
/// in, and starts "claude auth login", which opens the browser to sign in.
/// </summary>
public static class ClaudeAuth
{
    /// <summary>Signed in (true), signed out (false), or no clear answer (null).</summary>
    public static bool? SignedIn(string? statusJson)
    {
        if (string.IsNullOrWhiteSpace(statusJson)) return null;
        // The JSON is the whole output; anything printed before it (a warning) is skipped.
        int start = statusJson.IndexOf('{');
        if (start < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(statusJson[start..]);
            return doc.RootElement.TryGetProperty("loggedIn", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where the claude command may be, most likely first: the native installer's folder, npm's global folder, then
    /// each folder on the PATH (an .exe, else npm's .cmd).
    /// </summary>
    public static IReadOnlyList<string> Candidates(string userProfile, string appData, string? path)
    {
        var list = new List<string>();
        if (userProfile.Length > 0) list.Add(System.IO.Path.Combine(userProfile, ".local", "bin", "claude.exe"));
        if (appData.Length > 0) list.Add(System.IO.Path.Combine(appData, "npm", "claude.cmd"));
        foreach (var dir in (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string d = dir.Trim('"');
            if (d.Length == 0 || !System.IO.Path.IsPathFullyQualified(d)) continue;
            list.Add(System.IO.Path.Combine(d, "claude.exe"));
            list.Add(System.IO.Path.Combine(d, "claude.cmd"));
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
