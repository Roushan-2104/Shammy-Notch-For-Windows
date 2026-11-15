using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Shammy.Core.PhoneLink;

/// <summary>
/// The laptop's end of one phone connection (protocol v1): length-prefixed frames, the hello/auth handshake, then
/// encrypted JSON messages. Pairing and pinning are the caller's decisions; this only proves who is on the other end.
/// </summary>
public sealed class LinkSession(Stream stream, ECDsa identity) : IDisposable
{
    public const int TcpPort = 47900, UdpPort = 47901;

    /// <summary>Frames bigger than this close the connection (messages are small; text is capped at 4 KB).</summary>
    public const int MaxFrame = 64 * 1024;

    public const int MaxText = 4096;

    /// <summary>The hello and reveal are about 400 bytes: nothing larger is read before the other side is known.</summary>
    public const int MaxHandshakeFrame = 4096;

    readonly SemaphoreSlim _sendGate = new(1, 1);
    LinkCrypto.Channel? _send, _recv;

    /// <summary>The phone's identity key (SPKI DER), known after <see cref="HandshakeAsync"/>.</summary>
    public byte[] PhoneIdentity { get; private set; } = [];

    /// <summary>What the phone calls itself in its hello (unverified: for display only).</summary>
    public string PhoneName { get; private set; } = "your phone";

    /// <summary>The 6 digits both screens show at first pairing.</summary>
    public string Sas { get; private set; } = "";

    /// <summary>
    /// The phone commits to its hello (a hash), we answer with ours, the phone reveals its hello, then both prove their
    /// identity keys over the transcript. The commitment stops someone in the middle from trying nonces until both
    /// screens show the same digits: whoever sends its values second must have committed to them first.
    /// </summary>
    public async Task HandshakeAsync(CancellationToken ct)
    {
        var hello = await ReadJsonAsync(ct);
        if (hello["t"]?.GetValue<string>() != "hello" || hello["v"]?.GetValue<int>() != 1)
            throw new InvalidDataException("not a v1 hello");
        var commit = B64(hello, "commit");
        // a name comes only while pairing (afterwards the phone sends none: nothing identifying in the clear)
        var name = Text(hello, "name").Trim();
        PhoneName = name.Length is > 0 and <= 40 ? name : "your phone";

        using var eph = LinkCrypto.NewEphemeral();
        byte[] el = eph.ExportSubjectPublicKeyInfo(), il = identity.ExportSubjectPublicKeyInfo(), nl = RandomNumberGenerator.GetBytes(16);
        await WriteFrameAsync(stream, Json(new JsonObject
        {
            ["t"] = "hello", ["v"] = 1, ["eph"] = Convert.ToBase64String(el), ["id"] = Convert.ToBase64String(il), ["nonce"] = Convert.ToBase64String(nl),
        }), ct);

        var reveal = await ReadJsonAsync(ct);
        if (reveal["t"]?.GetValue<string>() != "reveal") throw new InvalidDataException("not a reveal");
        byte[] ep = B64(reveal, "eph"), ip = B64(reveal, "id"), np = B64(reveal, "nonce");
        if (np.Length != 16 || !CryptographicOperations.FixedTimeEquals(commit, SHA256.HashData([.. ep, .. ip, .. np])))
            throw new CryptographicException("the phone's reveal doesn't match its commitment");
        if (ip.AsSpan().SequenceEqual(il)) throw new CryptographicException("the phone claims this laptop's own key");

        var keys = LinkCrypto.Derive(LinkCrypto.Ecdh(eph, ep), ep, ip, np, el, il, nl);
        _send = new LinkCrypto.Channel(keys.LaptopToPhone);
        _recv = new LinkCrypto.Channel(keys.PhoneToLaptop);
        await SendAsync(new JsonObject { ["t"] = "auth", ["sig"] = Convert.ToBase64String(LinkCrypto.Sign(identity, LinkCrypto.AuthMessage("laptop", keys.Transcript))) }, ct);
        var theirs = await ReceiveAsync(ct);
        if (theirs["t"]?.GetValue<string>() != "auth" || !LinkCrypto.Verify(ip, LinkCrypto.AuthMessage("phone", keys.Transcript), B64(theirs, "sig")))
            throw new CryptographicException("the phone's identity didn't check out");
        PhoneIdentity = ip;
        Sas = keys.Sas;
    }

    public async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct);
        try { await WriteFrameAsync(stream, _send!.Seal(Json(message)), ct); }
        finally { _sendGate.Release(); }
    }

    /// <summary>The next message; throws when the connection ends or a frame fails to decrypt (the caller closes).</summary>
    public async Task<JsonObject> ReceiveAsync(CancellationToken ct) =>
        JsonNode.Parse(_recv!.Open(await ReadFrameAsync(stream, ct))) as JsonObject ?? throw new InvalidDataException("not an object");

    public void Dispose() => stream.Dispose();

    // ---- Wire helpers (also used by the tests' phone) -----------------------------------------------

    public static async Task WriteFrameAsync(Stream s, byte[] data, CancellationToken ct)
    {
        var frame = new byte[4 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, data.Length);
        data.CopyTo(frame, 4);
        await s.WriteAsync(frame, ct);
        await s.FlushAsync(ct);
    }

    public static async Task<byte[]> ReadFrameAsync(Stream s, CancellationToken ct, int max = MaxFrame)
    {
        var head = new byte[4];
        await s.ReadExactlyAsync(head, ct);
        int n = BinaryPrimitives.ReadInt32BigEndian(head);
        if (n < 0 || n > max) throw new InvalidDataException("frame too big");
        var body = new byte[n];
        await s.ReadExactlyAsync(body, ct);
        return body;
    }

    async Task<JsonObject> ReadJsonAsync(CancellationToken ct) =>
        JsonNode.Parse(await ReadFrameAsync(stream, ct, MaxHandshakeFrame)) as JsonObject ?? throw new InvalidDataException("not an object");

    /// <summary>A text field from a message, capped (§5); "" when missing.</summary>
    public static string Text(JsonObject m, string field) => Cap(m[field] is JsonValue v && v.TryGetValue<string>(out var t) ? t : "");

    /// <summary>Text fields are capped at 4 KB (§5).</summary>
    public static string Cap(string s) => s.Length > MaxText ? s[..MaxText] : s;

    /// <summary>The laptop's short id: the first 8 bytes of SHA-256 of its identity key, base64.</summary>
    public static string ShortId(byte[] identitySpki) => Convert.ToBase64String(SHA256.HashData(identitySpki).AsSpan(0, 8));

    /// <summary>
    /// What the laptop answers a phone's broadcast with; null for anything else. While pairing it answers "SHAMMY?1" with
    /// its name; once paired it answers only "SHAMMY?1 &lt;its short id&gt;" (the paired phone asking for it), and without a name,
    /// so a stranger on the network learns nothing about this laptop.
    /// </summary>
    public static byte[]? DiscoveryReply(ReadOnlySpan<byte> query, string name, byte[] identitySpki, bool pairing)
    {
        string id = ShortId(identitySpki);
        bool open = pairing && query.SequenceEqual("SHAMMY?1"u8);
        bool asked = query.SequenceEqual(Encoding.ASCII.GetBytes($"SHAMMY?1 {id}"));
        if (!open && !asked) return null;
        return Json(new JsonObject { ["v"] = 1, ["name"] = open ? name : "", ["port"] = TcpPort, ["id"] = id });
    }

    static byte[] Json(JsonObject o) => Encoding.UTF8.GetBytes(o.ToJsonString());

    static byte[] B64(JsonObject o, string field) =>
        Convert.FromBase64String(o[field]?.GetValue<string>() ?? throw new InvalidDataException($"missing {field}"));
}
