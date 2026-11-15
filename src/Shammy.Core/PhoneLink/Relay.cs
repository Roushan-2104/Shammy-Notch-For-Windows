using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Shammy.Core.PhoneLink;

/// <summary>
/// "Link from anywhere" (docs/LINK.md §9): when the phone and laptop aren't on one network, both dial a relay
/// (tools/relay/relay.py) that only joins the two connections. The same end-to-end encrypted link then runs through it
/// unchanged; the relay sees a room id (a hash of both public keys) and byte counts, never content. No pairing via relay.
/// </summary>
public static class Relay
{
    public const int DefaultPort = 47950;

    /// <summary>The pair's room: the first 16 bytes of SHA-256("shammy relay" ‖ laptop key ‖ phone key), as hex.</summary>
    public static string RoomId(byte[] laptopSpki, byte[] phoneSpki) =>
        Convert.ToHexString(SHA256.HashData([.. "shammy relay"u8, .. laptopSpki, .. phoneSpki]).AsSpan(0, 16)).ToLowerInvariant();

    /// <summary>"host:port" (port optional); null when it isn't one.</summary>
    public static (string Host, int Port)? Parse(string? at)
    {
        if (string.IsNullOrWhiteSpace(at)) return null;
        at = at.Trim();
        int colon = at.LastIndexOf(':');
        if (colon < 0) return (at, DefaultPort);
        return int.TryParse(at[(colon + 1)..], out int port) && port is > 0 and < 65536 && colon > 0 ? (at[..colon], port) : null;
    }

    /// <summary>
    /// Dials the relay as <paramref name="role"/> ("laptop" or "phone") and waits up to <paramref name="wait"/> for the
    /// other side. Returns the connection once the relay says READY; the link's handshake runs on it next.
    /// </summary>
    public static async Task<TcpClient> JoinAsync(string host, int port, string room, string role, TimeSpan wait, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using (var dial = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                dial.CancelAfter(TimeSpan.FromSeconds(10));
                await tcp.ConnectAsync(host, port, dial.Token);
            }
            var s = tcp.GetStream();
            await s.WriteAsync(Encoding.ASCII.GetBytes($"SHAMMY-RELAY 1 {room} {role}\n"), ct);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(wait);
            // byte by byte, so nothing of the link that follows is read here
            var line = new StringBuilder();
            var one = new byte[1];
            while (true)
            {
                if (await s.ReadAsync(one, limit.Token) == 0) throw new IOException("the relay closed");
                if (one[0] == '\n') break;
                if (line.Length > 16) throw new InvalidDataException("not a relay");
                line.Append((char)one[0]);
            }
            if (line.ToString() != "READY") throw new InvalidDataException("not a relay");
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }
}
