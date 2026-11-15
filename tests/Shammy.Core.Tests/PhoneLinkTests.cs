using System.Security.Cryptography;
using Shammy.Core.PhoneLink;
using Xunit;

namespace Shammy.Core.Tests;

public class PhoneLinkTests
{
    static byte[] B(byte v, int n) => Enumerable.Repeat(v, n).ToArray();

    /// <summary>The cross-language vector: the phone's tests (Kotlin) check the very same numbers.</summary>
    [Fact]
    public void MatchesTheSharedTestVector()
    {
        var k = LinkCrypto.Derive(B(7, 32), B(1, 65), B(2, 91), B(3, 16), B(4, 65), B(5, 91), B(6, 16));
        Assert.Equal("2c193afb1882d7e090f95e1a22c5def691dabc9b898f3c5ac856f844543197c6", Convert.ToHexString(k.Transcript).ToLowerInvariant());
        Assert.Equal("a06ad3e274f3dcd3d663e417c71eec1ea85adc0dd197c47d578bd2d77abaf041", Convert.ToHexString(k.PhoneToLaptop).ToLowerInvariant());
        Assert.Equal("249c2e7f48a251bf69b5560fdf68a08ccdc91b84f9664da6eb51d51fc4c7929c", Convert.ToHexString(k.LaptopToPhone).ToLowerInvariant());
        Assert.Equal("219 255", k.Sas);
    }

    static (LinkCrypto.Keys phone, LinkCrypto.Keys laptop) Handshake()
    {
        using var ep = LinkCrypto.NewEphemeral();
        using var el = LinkCrypto.NewEphemeral();
        byte[] epk = ep.ExportSubjectPublicKeyInfo(), elk = el.ExportSubjectPublicKeyInfo();
        byte[] ip = B(2, 91), il = B(5, 91), np = B(3, 16), nl = B(6, 16);
        return (LinkCrypto.Derive(LinkCrypto.Ecdh(ep, elk), epk, ip, np, elk, il, nl),
                LinkCrypto.Derive(LinkCrypto.Ecdh(el, epk), epk, ip, np, elk, il, nl));
    }

    [Fact]
    public void BothSidesAgreeOnKeysAndDigits()
    {
        var (p, l) = Handshake();
        Assert.Equal(p.PhoneToLaptop, l.PhoneToLaptop);
        Assert.Equal(p.Sas, l.Sas);
        Assert.Matches(@"^\d{3} \d{3}$", p.Sas);
    }

    [Fact]
    public void FramesRoundTripAndRejectTamperingAndReplays()
    {
        var (p, l) = Handshake();
        var send = new LinkCrypto.Channel(p.PhoneToLaptop);
        var recv = new LinkCrypto.Channel(l.PhoneToLaptop);
        var f = send.Seal("hi"u8.ToArray());
        Assert.Equal("hi"u8.ToArray(), recv.Open(f));
        Assert.ThrowsAny<CryptographicException>(() => recv.Open(f)); // replay
        var g = send.Seal("x"u8.ToArray()); g[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => recv.Open(g)); // tampered
    }

    /// <summary>A phone written straight from docs/LINK.md §4, talking to the real laptop session over loopback TCP.</summary>
    static async Task<(LinkSession laptop, Stream phone, LinkCrypto.Channel toLaptop, LinkCrypto.Channel fromLaptop, string phoneSas)>
        Connect(ECDsa phoneId, ECDsa laptopId, bool forgePhoneSig = false, bool swapAfterCommit = false)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
        var server = await listener.AcceptTcpClientAsync();
        listener.Stop();
        var laptop = new LinkSession(server.GetStream(), laptopId);
        var handshake = laptop.HandshakeAsync(CancellationToken.None);

        Stream s = client.GetStream();
        using var eph = LinkCrypto.NewEphemeral();
        byte[] ep = eph.ExportSubjectPublicKeyInfo(), ip = phoneId.ExportSubjectPublicKeyInfo(), np = RandomNumberGenerator.GetBytes(16);
        await LinkSession.WriteFrameAsync(s, System.Text.Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonObject
        {
            ["t"] = "hello", ["v"] = 1, ["commit"] = Convert.ToBase64String(SHA256.HashData([.. ep, .. ip, .. np])), ["name"] = "Test phone",
        }.ToJsonString()), CancellationToken.None);
        var hello = System.Text.Json.Nodes.JsonNode.Parse(await LinkSession.ReadFrameAsync(s, CancellationToken.None))!;
        byte[] el = Convert.FromBase64String((string)hello["eph"]!), il = Convert.FromBase64String((string)hello["id"]!), nl = Convert.FromBase64String((string)hello["nonce"]!);
        // a man in the middle choosing its nonce after seeing the laptop's: the commitment no longer matches
        if (swapAfterCommit) np = RandomNumberGenerator.GetBytes(16);
        await LinkSession.WriteFrameAsync(s, System.Text.Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonObject
        {
            ["t"] = "reveal", ["eph"] = Convert.ToBase64String(ep), ["id"] = Convert.ToBase64String(ip), ["nonce"] = Convert.ToBase64String(np),
        }.ToJsonString()), CancellationToken.None);
        if (swapAfterCommit) { await handshake; throw new InvalidOperationException("the laptop took a reveal that broke its commitment"); }
        var k = LinkCrypto.Derive(LinkCrypto.Ecdh(eph, el), ep, ip, np, el, il, nl);
        var toLaptop = new LinkCrypto.Channel(k.PhoneToLaptop);
        var fromLaptop = new LinkCrypto.Channel(k.LaptopToPhone);
        var signer = forgePhoneSig ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : phoneId;
        var sig = LinkCrypto.Sign(signer, LinkCrypto.AuthMessage(k.Transcript));
        await LinkSession.WriteFrameAsync(s, toLaptop.Seal(System.Text.Encoding.UTF8.GetBytes(
            $$"""{"t":"auth","sig":"{{Convert.ToBase64String(sig)}}"}""")), CancellationToken.None);
        var auth = System.Text.Json.Nodes.JsonNode.Parse(fromLaptop.Open(await LinkSession.ReadFrameAsync(s, CancellationToken.None)))!;
        Assert.True(LinkCrypto.Verify(il, LinkCrypto.AuthMessage(k.Transcript), Convert.FromBase64String((string)auth["sig"]!)));
        await handshake;
        return (laptop, s, toLaptop, fromLaptop, k.Sas);
    }

    [Fact]
    public async Task HandshakeWithAPhoneAndTalk()
    {
        using var phoneId = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var laptopId = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (laptop, phone, toLaptop, fromLaptop, sas) = await Connect(phoneId, laptopId);
        Assert.Equal(sas, laptop.Sas);
        Assert.Equal(phoneId.ExportSubjectPublicKeyInfo(), laptop.PhoneIdentity);

        await LinkSession.WriteFrameAsync(phone, toLaptop.Seal("""{"t":"notif","key":"k1","title":"Mum"}"""u8.ToArray()), CancellationToken.None);
        var m = await laptop.ReceiveAsync(CancellationToken.None);
        Assert.Equal("Mum", LinkSession.Text(m, "title"));

        await laptop.SendAsync(new System.Text.Json.Nodes.JsonObject { ["t"] = "reply", ["key"] = "k1", ["text"] = "on my way" }, CancellationToken.None);
        var r = System.Text.Json.Nodes.JsonNode.Parse(fromLaptop.Open(await LinkSession.ReadFrameAsync(phone, CancellationToken.None)))!;
        Assert.Equal("on my way", (string)r["text"]!);
        laptop.Dispose();
    }

    [Fact]
    public async Task APhoneThatCantProveItsKeyIsRefused()
    {
        using var phoneId = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var laptopId = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Connect(phoneId, laptopId, forgePhoneSig: true));
    }

    [Fact]
    public async Task ARevealThatBreaksItsCommitmentIsRefused()
    {
        using var phoneId = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var laptopId = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Connect(phoneId, laptopId, swapAfterCommit: true));
    }

    [Fact]
    public void AnswersOnlyTheDiscoveryQuestion()
    {
        Assert.Null(LinkSession.DiscoveryReply("hello"u8, "PC", B(1, 91)));
        var reply = System.Text.Json.Nodes.JsonNode.Parse(LinkSession.DiscoveryReply("SHAMMY?1"u8, "PC", B(1, 91))!)!;
        Assert.Equal(47900, (int)reply["port"]!);
        Assert.Equal(8, Convert.FromBase64String((string)reply["id"]!).Length);
    }

    [Fact]
    public void TextFieldsAreCapped()
    {
        var m = new System.Text.Json.Nodes.JsonObject { ["text"] = new string('a', 5000), ["n"] = 3 };
        Assert.Equal(LinkSession.MaxText, LinkSession.Text(m, "text").Length);
        Assert.Equal("", LinkSession.Text(m, "n"));
        Assert.Equal("", LinkSession.Text(m, "missing"));
    }

    [Fact]
    public void IdentitySignaturesVerifyAndForgeriesDont()
    {
        using var id = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var msg = LinkCrypto.AuthMessage(B(9, 32));
        var sig = LinkCrypto.Sign(id, msg);
        Assert.True(LinkCrypto.Verify(id.ExportSubjectPublicKeyInfo(), msg, sig));
        Assert.False(LinkCrypto.Verify(other.ExportSubjectPublicKeyInfo(), msg, sig));
    }
}
