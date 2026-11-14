using System.Text.RegularExpressions;
using Shammy.Core.CircleMemory;
using Shammy.Core.Search;

namespace Shammy.Core.Clipboard;

/// <summary>What a copied text looks like, for the clipboard card's tile and preview.</summary>
public enum ClipShape { Text, Link, Colour, Code, Files, Image }

/// <summary>Labels for the v2 clipboard card: the kind of thing, where it came from, and a short preview.</summary>
public static class ClipCardText
{
    static readonly Regex CodeHints = new(
        @"(^\s*(def|class|function|import|from|using|public|private|const|let|var|return|if|for|while|#include|SELECT|<\w+)\b)|[{};]\s*$|=>|\)\s*\{",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public static ClipShape Shape(ClipEntry e)
    {
        if (e.Kind == ClipKind.Image) return ClipShape.Image;
        if (e.Kind == ClipKind.Files) return ClipShape.Files;
        var t = e.Text.Trim();
        if (IsLink(t)) return ClipShape.Link;
        if (ColourOf(t) is not null) return ClipShape.Colour;
        if (LooksLikeCode(e.Text)) return ClipShape.Code;
        return ClipShape.Text;
    }

    /// <summary>A single web address with no spaces, read as Circle to Search reads one ("github.com/x" too).</summary>
    public static bool IsLink(string text) => LinkUrl(text) is not null;

    /// <summary>The full address to open for a copied link ("https://github.com/x" for "github.com/x"); null when it isn't one.</summary>
    public static string? LinkUrl(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Any(char.IsWhiteSpace)) return null;
        return CircleSense.Link(t)?.Value;
    }

    /// <summary>"github.com/Roushan/shammy" from "https://www.github.com/Roushan/shammy/".</summary>
    public static string LinkLabel(string text)
    {
        var t = LinkUrl(text) ?? text.Trim();
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri)) return text.Trim();
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        var path = uri.AbsolutePath.TrimEnd('/');
        return host + (path.Length > 0 ? Uri.UnescapeDataString(path) : "");
    }

    /// <summary>
    /// The colour a "#FF8A3D", "#f80", "rgb(255, 138, 61)" or "hsl(…)" text names, as (r, g, b); else null. Read as
    /// Circle to Search reads it, so eight hex digits are #RRGGBBAA as in CSS.
    /// </summary>
    public static (byte R, byte G, byte B)? ColourOf(string text) =>
        CircleSense.ColourText(text.Trim())?.Colour is { } c ? (c.R, c.G, c.B) : null;

    /// <summary>A rough guess: two or more lines that look like source code. Never true for a sentence.</summary>
    public static bool LooksLikeCode(string text)
    {
        var lines = text.Split('\n').Where(l => l.Trim().Length > 0).Take(20).ToList();
        if (lines.Count < 2) return false;
        int hits = lines.Count(l => CodeHints.IsMatch(l));
        bool indented = lines.Skip(1).Any(l => l.StartsWith("  ") || l.StartsWith('\t'));
        return hits >= 2 || (hits >= 1 && indented);
    }

    /// <summary>The card's uppercase kind label: "Link", "Text", "Code", "Colour", "Image", "Files".</summary>
    public static string KindLabel(ClipShape shape, int files = 1) => shape switch
    {
        ClipShape.Link => "Link",
        ClipShape.Colour => "Colour",
        ClipShape.Code => "Code",
        ClipShape.Image => "Image",
        ClipShape.Files => files == 1 ? "File" : "Files",
        _ => "Text",
    };

    /// <summary>A friendly app name for a process name ("msedge" → "Edge"); unknown names are title-cased.</summary>
    public static string SourceName(string? processName)
    {
        var name = AppNames.Friendly(processName) ?? "";
        return name.Length > 24 ? name[..24] : name;
    }

    /// <summary>"Chrome · 2m ago", "18m ago", "now".</summary>
    public static string Footer(ClipEntry e, DateTime now)
    {
        var age = ClipText.Relative(e.Time, now);
        var when = age == "now" ? "now" : age.EndsWith('m') || age.EndsWith('h') ? age + " ago" : age;
        return e.Source.Length > 0 ? $"{e.Source} · {when}" : when;
    }
}
