using System.Security.Cryptography;
using System.Text;

namespace Shammy.Core.PhoneSend;

/// <summary>
/// The secret path segment of a transfer URL. Anyone on the Wi-Fi can reach the port, so the token is
/// what keeps the file private: 128 random bits, base64url (22 chars, no padding).
/// </summary>
public static class TransferToken
{
    public static string New()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Constant-time comparison so response timing doesn't leak how much of a guess was right.</summary>
    public static bool Matches(string expected, string? candidate)
    {
        if (candidate is null) return false;
        var a = Encoding.ASCII.GetBytes(expected);
        var b = Encoding.ASCII.GetBytes(candidate);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

public static class MimeTypes
{
    static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".heic"] = "image/heic", [".heif"] = "image/heif", [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml", [".ico"] = "image/x-icon", [".tif"] = "image/tiff", [".tiff"] = "image/tiff",
        [".mp4"] = "video/mp4", [".m4v"] = "video/mp4", [".mov"] = "video/quicktime", [".webm"] = "video/webm",
        [".mkv"] = "video/x-matroska", [".avi"] = "video/x-msvideo",
        [".mp3"] = "audio/mpeg", [".m4a"] = "audio/mp4", [".aac"] = "audio/aac", [".wav"] = "audio/wav",
        [".flac"] = "audio/flac", [".ogg"] = "audio/ogg", [".opus"] = "audio/opus",
        [".pdf"] = "application/pdf", [".txt"] = "text/plain; charset=utf-8", [".md"] = "text/markdown; charset=utf-8",
        [".csv"] = "text/csv; charset=utf-8", [".json"] = "application/json", [".xml"] = "application/xml",
        [".html"] = "text/html; charset=utf-8", [".htm"] = "text/html; charset=utf-8",
        [".zip"] = "application/zip", [".7z"] = "application/x-7z-compressed", [".rar"] = "application/vnd.rar",
        [".gz"] = "application/gzip", [".tar"] = "application/x-tar",
        [".apk"] = "application/vnd.android.package-archive", [".epub"] = "application/epub+zip",
        [".doc"] = "application/msword", [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel", [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint", [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".ics"] = "text/calendar", [".vcf"] = "text/vcard",
    };

    public static string ForFile(string fileName) =>
        Map.TryGetValue(Path.GetExtension(fileName), out var type) ? type : "application/octet-stream";
}

public static class ByteSize
{
    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => FormattableString.Invariant($"{bytes / (double)(1L << 30):0.0} GB"),
        >= 1L << 20 => FormattableString.Invariant($"{bytes / (double)(1L << 20):0.0} MB"),
        >= 1L << 10 => FormattableString.Invariant($"{bytes / 1024.0:0} KB"),
        _ => $"{bytes} B",
    };
}
