using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Shammy.Core.Agents;

/// <summary>The first lines of a request to Shammy's hook listener.</summary>
/// <param name="HeadBytes">How many bytes the request line and headers took, blank line included.</param>
/// <param name="ContentLength">-1 when the request didn't say.</param>
public sealed record HookRequestHead(string Method, string Path, int HeadBytes, long ContentLength, bool ExpectsContinue);

/// <summary>
/// The little HTTP the hook listener speaks, as pure functions: reading a request's head, deciding whether its
/// address carries the right secret, and writing a reply. Anything odd is refused; nothing here can throw on
/// bad input.
/// </summary>
public static class HookHttp
{
    /// <summary>A request line plus headers longer than this is refused.</summary>
    public const int MaxHead = 8 * 1024;

    /// <summary>How much of a body is kept and read as JSON (the fields that matter come first).</summary>
    public const int KeepBody = 256 * 1024;

    /// <summary>Bodies up to this size are taken (and thrown away past <see cref="KeepBody"/>); bigger ones are refused.</summary>
    public const long MaxBody = 64L * 1024 * 1024;

    /// <summary>The index just past the blank line that ends the headers, or -1 if it hasn't arrived yet.</summary>
    public static int HeadEnd(ReadOnlySpan<byte> data)
    {
        int i = data.IndexOf("\r\n\r\n"u8);
        return i < 0 ? -1 : i + 4;
    }

    /// <summary>Null for anything that isn't a well-formed request head.</summary>
    public static HookRequestHead? ParseHead(ReadOnlySpan<byte> data)
    {
        int end = HeadEnd(data);
        if (end < 0 || end > MaxHead) return null;
        string[] lines = Encoding.ASCII.GetString(data[..(end - 4)]).Split("\r\n");
        string[] first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || first[1].Length == 0 || first[1][0] != '/') return null;
        long length = -1;
        bool expects = false;
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) return null;
            string name = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out length)) return null;
            }
            else if (name.Equals("Expect", StringComparison.OrdinalIgnoreCase))
                expects = value.Equals("100-continue", StringComparison.OrdinalIgnoreCase);
            // A body sent in chunks has no length to cap; the hooks never send one.
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) return null;
        }
        return new HookRequestHead(first[0], first[1], end, length, expects);
    }

    /// <summary>
    /// "status" or "ask" when the address is exactly /shammy-hook/&lt;token&gt;/&lt;kind&gt; with the right token;
    /// null otherwise. The token is compared in constant time.
    /// </summary>
    public static string? Route(string method, string path, string token)
    {
        if (method != "POST" || token.Length == 0) return null;
        int query = path.IndexOf('?');
        if (query >= 0) path = path[..query];
        string[] parts = path.Split('/');
        if (parts.Length != 4 || parts[0].Length != 0 || parts[1] != AgentHooks.Marker) return null;
        byte[] given = Encoding.UTF8.GetBytes(parts[2]), wanted = Encoding.UTF8.GetBytes(token);
        if (!CryptographicOperations.FixedTimeEquals(given, wanted)) return null;
        return parts[3] is HookRoutes.Status or HookRoutes.Ask ? parts[3] : null;
    }

    /// <summary>A complete reply that also closes the connection. An empty body means "no decision".</summary>
    public static byte[] Response(int status, string body = "")
    {
        string reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            413 => "Payload Too Large",
            _ => "Error",
        };
        byte[] payload = Encoding.UTF8.GetBytes(body);
        string head = string.Create(CultureInfo.InvariantCulture,
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        var all = new byte[headBytes.Length + payload.Length];
        headBytes.CopyTo(all, 0);
        payload.CopyTo(all, headBytes.Length);
        return all;
    }

    public static readonly byte[] Continue = "HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray();
}
