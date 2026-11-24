using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Shammy.Core.PhoneSend;

namespace Shammy.Core.PhoneLink;

/// <summary>
/// Copied text, links and files between the laptop and the phone (§5 `clip`, `link`, `file`, `fileChunk`, `fileEnd`):
/// the caps, and the checks a received file must pass before it's kept (names go through <see cref="SafeFileName"/>, like
/// files uploaded from the phone's browser).
/// </summary>
public static class LinkShare
{
    public const long MaxFile = 50L * 1024 * 1024;
    public const int MaxClip = 16000, MaxUrl = 2048;

    /// <summary>Pieces of a file or the app update: base64 of it stays well under a 64 KB frame.</summary>
    public const int Chunk = 36 * 1024;

    /// <summary>Copied text capped at <see cref="MaxClip"/>, never splitting an emoji in half.</summary>
    public static string CapClip(string text) =>
        text.Length <= MaxClip ? text : text[..(char.IsHighSurrogate(text[MaxClip - 1]) ? MaxClip - 1 : MaxClip)];

    /// <summary>
    /// Take it with you (Reach): a YouTube video's link with its place, so it carries on at the same second
    /// (<c>t=90s</c>, replacing any earlier one, before a #fragment); any other link, or no place, comes back unchanged.
    /// </summary>
    public static string WithTime(string url, int seconds)
    {
        if (seconds <= 0 || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        var host = u.Host.ToLowerInvariant();
        if (host != "youtube.com" && !host.EndsWith(".youtube.com", StringComparison.Ordinal) && host != "youtu.be") return url;
        var query = u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("t=", StringComparison.OrdinalIgnoreCase)).Append($"t={seconds}s");
        return new UriBuilder(u) { Query = string.Join('&', query) }.Uri.AbsoluteUri;
    }

    /// <summary>A link that may open in a browser: an absolute http(s) address up to 2048 characters with nothing hidden in it; else null.</summary>
    public static string? Url(string? url)
    {
        var u = (url ?? "").Trim();
        if (u.Length is 0 or > MaxUrl || u.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return null;
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) return null;
        return uri.AbsoluteUri;
    }

    /// <summary>The type the phone files it under (MediaStore needs one); anything else is a plain download.</summary>
    public static string Mime(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    /// <summary>The start of a file (`file`) as the sender writes it.</summary>
    public static JsonObject FileStart(string id, string name, long size) => new()
    {
        ["t"] = "file", ["id"] = id, ["name"] = SafeFileName.Sanitize(name), ["size"] = size, ["mime"] = Mime(name),
    };
}

/// <summary>
/// A file on its way in: pieces must come in order from 0 and add up to no more than the announced size, and it's kept
/// only if the size and SHA-256 at the end match. The caller writes the bytes; this only judges them.
/// </summary>
public sealed class IncomingFile : IDisposable
{
    readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    int _next;

    IncomingFile(string id, string name, long size) => (Id, Name, Size) = (id, name, size);

    public string Id { get; }

    /// <summary>The sanitized name (not yet made unique or neutralized: <see cref="SafeFileName.MoveIn"/> does that).</summary>
    public string Name { get; }

    public long Size { get; }

    public long Received { get; private set; }

    /// <summary>A `file` message as a transfer to accept, or null (no id, no size, or over 50 MB).</summary>
    public static IncomingFile? Start(JsonObject m)
    {
        var id = LinkSession.Text(m, "id");
        long size = m["size"] is JsonValue v && v.TryGetValue<long>(out long n) ? n : -1;
        if (id.Length is 0 or > 64 || size is < 0 or > LinkShare.MaxFile) return null;
        return new IncomingFile(id, SafeFileName.Sanitize(LinkSession.Text(m, "name")), size);
    }

    /// <summary>Takes piece <paramref name="i"/>; false when it's out of order or goes past the size (drop the transfer).</summary>
    public bool Take(int i, ReadOnlySpan<byte> data)
    {
        if (i != _next || Received + data.Length > Size) return false;
        _next++;
        Received += data.Length;
        _sha.AppendData(data);
        return true;
    }

    /// <summary>Whether everything announced arrived and hashes to <paramref name="sha256"/> (hex). Call once, at the end.</summary>
    public bool Complete(string sha256)
    {
        var mine = _sha.GetHashAndReset();
        byte[] theirs;
        try { theirs = Convert.FromHexString(sha256); }
        catch (FormatException) { return false; }
        return Received == Size && CryptographicOperations.FixedTimeEquals(mine, theirs);
    }

    public void Dispose() => _sha.Dispose();
}
