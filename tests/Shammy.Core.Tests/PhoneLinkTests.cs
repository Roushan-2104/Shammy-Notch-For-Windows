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
