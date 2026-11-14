using System.Globalization;
using System.Text.RegularExpressions;
using Shammy.Core.CircleMemory;

namespace Shammy.Core.Clipboard;

/// <summary>What a copied text looks like, for the clipboard card's tile and preview.</summary>
public enum ClipShape { Text, Link, Colour, Code, Files, Image }

/// <summary>Labels for the v2 clipboard card: the kind of thing, where it came from, and a short preview.</summary>
public static class ClipCardText
{
    static readonly Regex Hex = new(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$");
    static readonly Regex Rgb = new(@"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*[\d.]+\s*)?\)$", RegexOptions.IgnoreCase);
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

    /// <summary>A single http(s) or www. address with no spaces.</summary>
    public static bool IsLink(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Any(char.IsWhiteSpace)) return false;
        if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) t = "https://" + t;
        return Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Contains('.');
    }

    /// <summary>"github.com/Roushan/shammy" from "https://www.github.com/Roushan/shammy/".</summary>
    public static string LinkLabel(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) t = "https://" + t;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri)) return text.Trim();
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        var path = uri.AbsolutePath.TrimEnd('/');
        return host + (path.Length > 0 ? Uri.UnescapeDataString(path) : "");
    }

    /// <summary>The colour a "#FF8A3D", "#f80" or "rgb(255, 138, 61)" text names, as (r, g, b); else null.</summary>
    public static (byte R, byte G, byte B)? ColourOf(string text)
    {
        var t = text.Trim();
        if (Hex.IsMatch(t))
        {
            var h = t[1..];
            if (h.Length == 3) h = string.Concat(h.Select(c => $"{c}{c}"));
            if (h.Length == 8) h = h[2..]; // #AARRGGBB, as WPF writes it
            int v = int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }
        var m = Rgb.Match(t);
        if (!m.Success) return null;
        var parts = new[] { m.Groups[1], m.Groups[2], m.Groups[3] }.Select(g => int.Parse(g.Value, CultureInfo.InvariantCulture)).ToArray();
        if (parts.Any(p => p > 255)) return null;
        return ((byte)parts[0], (byte)parts[1], (byte)parts[2]);
    }

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
