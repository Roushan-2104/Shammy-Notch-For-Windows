using System.Text;
using System.Text.RegularExpressions;
using Shammy.Core.Clipboard;

namespace Shammy.Core.PhoneSend;

/// <summary>What the "send to phone" choice sheet can offer.</summary>
public enum PhoneChoiceKind
{
    /// <summary>A copied link: straight into the QR code, so the phone opens it.</summary>
    CopiedLink,
    /// <summary>Copied text: in the QR code when short, else served as a .txt file.</summary>
    CopiedText,
    CopiedImage,
    CopiedFiles,
    Screenshot,
    Shelf,
    BrowserPage,
    PcFile,
}

/// <summary>One row of the sheet. <see cref="Id"/> is stable for the test pipe ("copied-link", "shelf", …).</summary>
public sealed record PhoneChoice(PhoneChoiceKind Kind, string Title, string Detail)
{
    public string Id => PhoneChoices.IdOf(Kind);
}

/// <summary>What exists right now, gathered by the app (clipboard, Screenshots, shelf, the browser in front).</summary>
public sealed class PhoneChoiceInputs
{
    public DateTime Now { get; init; }

    /// <summary>Copied text (null or blank: none). Never set for copies marked private by a password manager.</summary>
    public string? CopiedText { get; init; }

    public bool CopiedImage { get; init; }
    public int CopiedImageWidth { get; init; }
    public int CopiedImageHeight { get; init; }

    /// <summary>Copied files (Explorer's Ctrl+C) that still exist.</summary>
    public IReadOnlyList<string> CopiedFiles { get; init; } = Array.Empty<string>();

    /// <summary>When the current copy was made, if the clipboard history knows; null = unknown (treated as fresh).</summary>
    public DateTime? CopiedAt { get; init; }

    /// <summary>The newest screenshot (any age; <see cref="PhoneChoices.ScreenshotRecent"/> decides).</summary>
    public DateTime? ScreenshotAt { get; init; }

    /// <summary>The screenshot came from a snip on the clipboard (Win+Shift+S), so it may be the copied image too.</summary>
    public bool ScreenshotFromClipboard { get; init; }

    /// <summary>The screenshot's size in pixels when known (0 otherwise); a copied image of the same size is the same capture.</summary>
    public int ScreenshotWidth { get; init; }
    public int ScreenshotHeight { get; init; }

    /// <summary>Names of the files on the shelf, oldest first (as the shelf stores them).</summary>
    public IReadOnlyList<string> ShelfNames { get; init; } = Array.Empty<string>();

    /// <summary>The page in the browser (already normalised by <see cref="PhoneText.FromAddressBar"/>), or null.</summary>
    public string? PageUrl { get; init; }

    /// <summary>The browser is the app in front, not just a window further back.</summary>
    public bool BrowserInFront { get; init; }
}

/// <summary>Decides which choices the sheet shows, best first, and what each row says.</summary>
public static class PhoneChoices
{
    /// <summary>A screenshot older than this isn't offered: "your last screenshot" means one you just took.</summary>
    public static readonly TimeSpan ScreenshotRecent = TimeSpan.FromMinutes(30);

    /// <summary>A copy older than this sinks below a fresh screenshot, the shelf and the page in front.</summary>
    public static readonly TimeSpan CopyStale = TimeSpan.FromMinutes(20);

    /// <summary>A snip and the clipboard image this close together are one capture.</summary>
    static readonly TimeSpan SameCapture = TimeSpan.FromSeconds(10);

    public const int DetailMax = 46;

    public static string IdOf(PhoneChoiceKind kind) => kind switch
    {
        PhoneChoiceKind.CopiedLink => "copied-link",
        PhoneChoiceKind.CopiedText => "copied-text",
        PhoneChoiceKind.CopiedImage => "copied-image",
        PhoneChoiceKind.CopiedFiles => "copied-files",
        PhoneChoiceKind.Screenshot => "screenshot",
        PhoneChoiceKind.Shelf => "shelf",
        PhoneChoiceKind.BrowserPage => "page",
        _ => "pc-file",
    };

    /// <summary>The rows to show, best first. "A file from your PC" is always there, always last.</summary>
    public static IReadOnlyList<PhoneChoice> Build(PhoneChoiceInputs input)
    {
        var scored = new List<(PhoneChoice Choice, int Score, int Order)>();
        void Add(PhoneChoice c, int score) => scored.Add((c, score, scored.Count));

        bool copyStale = input.CopiedAt is { } copied && input.Now - copied > CopyStale;
        bool screenshotFresh = input.ScreenshotAt is { } shot && input.Now - shot <= ScreenshotRecent && input.Now - shot >= -TimeSpan.FromMinutes(1);
        string? copiedLink = null;

        // What you copied: files, then text, then an image (the order apps put them on the clipboard).
        PhoneChoice? copy = null;
        if (input.CopiedFiles.Count > 0)
        {
            copy = new PhoneChoice(PhoneChoiceKind.CopiedFiles, "What you copied", ClipText.FilesTitle(input.CopiedFiles));
        }
        else if (!string.IsNullOrWhiteSpace(input.CopiedText))
        {
            copiedLink = PhoneText.AsLink(input.CopiedText);
            copy = copiedLink is not null
                ? new PhoneChoice(PhoneChoiceKind.CopiedLink, "What you copied", PhoneText.LinkPreview(copiedLink, DetailMax))
                : new PhoneChoice(PhoneChoiceKind.CopiedText, "What you copied", PhoneText.TextPreview(input.CopiedText, DetailMax));
        }
        else if (input.CopiedImage)
        {
            // A Win+Shift+S snip (or "take a screenshot", which also copies it) is on the clipboard and is also "your
            // last screenshot": offer it once, as the screenshot.
            bool closeInTime = input.CopiedAt is not { } at || input.ScreenshotAt is not { } s || (at - s).Duration() <= SameCapture;
            bool sameSize = input.ScreenshotWidth > 0 && input.ScreenshotWidth == input.CopiedImageWidth &&
                            input.ScreenshotHeight == input.CopiedImageHeight;
            bool sameAsShot = screenshotFresh && closeInTime && (input.ScreenshotFromClipboard || sameSize);
            if (!sameAsShot)
            {
                var size = input.CopiedImageWidth > 0 && input.CopiedImageHeight > 0
                    ? " · " + ClipText.Dimensions(input.CopiedImageWidth, input.CopiedImageHeight)
                    : "";
                copy = new PhoneChoice(PhoneChoiceKind.CopiedImage, "What you copied", "Image" + size);
            }
        }
        if (copy is not null) Add(copy, copyStale ? 60 : 100);

        if (screenshotFresh)
        {
            var at = input.ScreenshotAt!.Value;
            // A screenshot taken after the copy is the newer thing on your mind.
            bool newerThanCopy = copy is not null && input.CopiedAt is { } c && at > c;
            Add(new PhoneChoice(PhoneChoiceKind.Screenshot, "Your last screenshot", "Taken " + Ago(at, input.Now)), newerThanCopy ? 110 : 90);
        }

        if (input.ShelfNames.Count > 0)
        {
            var newestFirst = input.ShelfNames.Reverse().ToList();
            var title = input.ShelfNames.Count == 1 ? "The file on the shelf" : $"Files on the shelf · {input.ShelfNames.Count}";
            Add(new PhoneChoice(PhoneChoiceKind.Shelf, title, ClipText.FilesTitle(newestFirst)), 80);
        }

        if (input.PageUrl is { } page && (copiedLink is null || !PhoneText.SameUrl(copiedLink, page)))
            Add(new PhoneChoice(PhoneChoiceKind.BrowserPage, "The page in your browser", PhoneText.LinkPreview(page, DetailMax)),
                input.BrowserInFront ? 85 : 70);

        var list = scored.OrderByDescending(s => s.Score).ThenBy(s => s.Order).Select(s => s.Choice).ToList();
        list.Add(new PhoneChoice(PhoneChoiceKind.PcFile, "A file from your PC", "Choose one in the Windows picker"));
        return list;
    }

    /// <summary>"just now", "4 min ago", "1 h ago".</summary>
    public static string Ago(DateTime then, DateTime now)
    {
        var age = now - then;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        return $"{(int)age.TotalHours} h ago";
    }

    /// <summary>The test pipe's lines: "1. copied-link | What you copied". Kinds and titles only, never the content.</summary>
    public static string Describe(IReadOnlyList<PhoneChoice> choices) =>
        string.Join("\n", choices.Select((c, i) => $"{i + 1}. {c.Id} | {c.Title}"));
}

/// <summary>How a piece of text travels to the phone.</summary>
public enum PhoneTextRoute
{
    /// <summary>A link, encoded straight into the QR code: the phone's camera opens it.</summary>
    QrLink,
    /// <summary>Short text, in the QR code itself: the phone's camera shows it to copy.</summary>
    QrText,
    /// <summary>Long text, served as a .txt file like any other transfer.</summary>
    File,
}

/// <summary>Link vs text, previews, and reading a browser's address bar.</summary>
public static class PhoneText
{
    /// <summary>Text up to this many UTF-8 bytes goes in the QR code (a version-10ish code, easy to scan off a screen).</summary>
    public const int QrTextMaxBytes = 300;

    /// <summary>Links up to this many bytes go in the QR code (still scannable off a laptop screen); longer ones go as a .txt file.</summary>
    public const int QrLinkMaxBytes = 1000;

    static readonly Regex Spaces = new(@"\s+");

    // A host: dot-separated labels ending in a letters-only top-level domain.
    static readonly Regex DomainHost = new(@"^(?=.{4,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,24}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly Regex Ipv4Host = new(@"^\d{1,3}(?:\.\d{1,3}){3}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The text as a link, or null when it's ordinary text. A link is one token: an http(s) address, or one starting
    /// "www.", or a bare domain with a path ("example.com/page"). A bare "report.pdf" or "example.com" stays text.
    /// </summary>
    public static string? AsLink(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().Trim('<', '>', '"', '\'');
        if (t.Length == 0 || t.Any(char.IsWhiteSpace)) return null;
        if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Web(t);
        if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return Web("https://" + t);
        int slash = t.IndexOf('/');
        if (slash > 0 && slash < t.Length - 1 && DomainHost.IsMatch(t[..slash])) return Web("https://" + t);
        return null;
    }

    /// <summary>
    /// The page address from a browser's address bar, or null when it isn't a page a phone can open. Chromium browsers
    /// hide "https://" (and sometimes "www."), so a bare "github.com/x" becomes "https://github.com/x". Internal pages
    /// (edge://, chrome://, about:), local files, this PC's own addresses and half-typed searches are left out.
    /// </summary>
    public static string? FromAddressBar(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var t = value.Trim();
        if (t.Any(char.IsWhiteSpace)) return null;
        string candidate;
        if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            candidate = t;
        }
        else
        {
            if (t.Contains("://", StringComparison.Ordinal) || t.Contains(':') && !HostPort.IsMatch(t)) return null;
            int end = t.IndexOfAny(new[] { '/', '?', '#' });
            var hostPort = end < 0 ? t : t[..end];
            var host = hostPort.Split(':')[0];
            if (!DomainHost.IsMatch(host) && !Ipv4Host.IsMatch(host)) return null;
            candidate = (Ipv4Host.IsMatch(host) ? "http://" : "https://") + t;
        }
        return Web(candidate);
    }

    // "example.com:8080/x": a colon only as a port.
    static readonly Regex HostPort = new(@"^[^/:?#]+:\d{1,5}(?:[/?#].*)?$", RegexOptions.CultureInvariant);

    /// <summary>An absolute http(s) URL a phone can reach (not localhost or 127.x), normalised; else null.</summary>
    static string? Web(string candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        var host = uri.IdnHost;
        if (host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || host.StartsWith("127.", StringComparison.Ordinal) ||
            host is "0.0.0.0" or "[::1]" or "::1") return null;
        if (!DomainHost.IsMatch(host) && !Ipv4Host.IsMatch(host)) return null;
        return uri.AbsoluteUri;
    }

    /// <summary>Two addresses for the same page, ignoring scheme, "www.", case of the host and a trailing slash.</summary>
    public static bool SameUrl(string a, string b) =>
        string.Equals(Key(a), Key(b), StringComparison.Ordinal);

    static string Key(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url.Trim();
        var host = u.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        var rest = u.PathAndQuery.TrimEnd('/');
        return host + (u.IsDefaultPort ? "" : ":" + u.Port) + rest + u.Fragment;
    }

    /// <summary>"github.com/user/repo" for a row: no scheme, no "www.", no trailing slash, cut to <paramref name="max"/>.</summary>
    public static string LinkPreview(string url, int max = PhoneChoices.DetailMax)
    {
        var s = url.Trim();
        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        s = s.TrimEnd('/');
        return Cut(s, max);
    }

    /// <summary>The text on one line in quotes, cut to <paramref name="max"/>: “Meet at 6 by the…”.</summary>
    public static string TextPreview(string text, int max = PhoneChoices.DetailMax)
    {
        var one = Spaces.Replace(text, " ").Trim();
        return "“" + Cut(one, Math.Max(4, max - 2)) + "”";
    }

    /// <summary>How the text should travel: a link or short text in the QR code, anything longer as a .txt file.</summary>
    public static PhoneTextRoute Route(string text)
    {
        if (AsLink(text) is { } link && Encoding.UTF8.GetByteCount(link) <= QrLinkMaxBytes) return PhoneTextRoute.QrLink;
        return Encoding.UTF8.GetByteCount(text.Trim()) <= QrTextMaxBytes ? PhoneTextRoute.QrText : PhoneTextRoute.File;
    }

    /// <summary>"Text · 42 characters" for the QR card.</summary>
    public static string TextSize(string text)
    {
        int n = text.Trim().Length;
        return n == 1 ? "Text · 1 character" : $"Text · {n} characters";
    }

    static string Cut(string s, int max) => s.Length <= max ? s : s[..Math.Max(0, max - 1)].TrimEnd() + "…";
}
