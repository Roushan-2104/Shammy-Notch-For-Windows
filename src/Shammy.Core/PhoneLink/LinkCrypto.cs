using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Shammy.Core.PhoneLink;

/// <summary>
/// The phone link's cryptography (protocol v1, docs/LINK.md on the phone branch), matching Shammy Island on Android byte
/// for byte: P-256 ECDH for fresh session keys, ECDSA identity signatures, HKDF-SHA256, AES-256-GCM frames. Built-in
/// .NET crypto only. A shared test vector in the tests proves both sides compute the same keys and the same 6 digits.
/// </summary>
public static class LinkCrypto
{
    public static ECDiffieHellman NewEphemeral() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>The raw shared secret (the x coordinate, as Java's ECDH returns it).</summary>
    public static byte[] Ecdh(ECDiffieHellman mine, byte[] otherSpki)
    {
        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(otherSpki, out _);
        return mine.DeriveRawSecretAgreement(other.PublicKey);
    }

    /// <summary>The handshake's result: one key per direction, the 6 digits shown at first pairing, and the transcript.</summary>
    public sealed record Keys(byte[] PhoneToLaptop, byte[] LaptopToPhone, string Sas, byte[] Transcript);

    /// <summary>Both ephemeral keys, identities and nonces bound in a fixed order (phone first), then HKDF.</summary>
    public static Keys Derive(byte[] z, byte[] ep, byte[] ip, byte[] np, byte[] el, byte[] il, byte[] nl)
    {
        var t = SHA256.HashData(Concat(ep, ip, np, el, il, nl));
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, z, t);
        uint n = BinaryPrimitives.ReadUInt32BigEndian(Expand(prk, "shammy sas", 4));
        var digits = (n % 1_000_000).ToString("D6");
        return new Keys(Expand(prk, "shammy p->l", 32), Expand(prk, "shammy l->p", 32), $"{digits[..3]} {digits[3..]}", t);
    }

    public static byte[] AuthMessage(byte[] transcript) => Concat(Encoding.ASCII.GetBytes("shammy auth"), transcript);

    public static byte[] Sign(ECDsa identity, byte[] data) =>
        identity.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    public static bool Verify(byte[] identitySpki, byte[] data, byte[] sig)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(identitySpki, out _);
            return key.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException) { return false; }
    }

    static byte[] Expand(byte[] prk, string info, int len) =>
        HKDF.Expand(HashAlgorithmName.SHA256, prk, len, Encoding.ASCII.GetBytes(info));

    static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (var p in parts) { p.CopyTo(all, at); at += p.Length; }
        return all;
    }

    /// <summary>One direction of a connection: AES-256-GCM, nonce = 4 zero bytes ‖ 8-byte big-endian counter.</summary>
    public sealed class Channel(byte[] key)
    {
        readonly AesGcm _aes = new(key, 16);
        long _counter;

        public byte[] Seal(byte[] plain)
        {
            var nonce = Nonce(_counter++);
            var frame = new byte[12 + plain.Length + 16];
            nonce.CopyTo(frame, 0);
            _aes.Encrypt(nonce, plain, frame.AsSpan(12, plain.Length), frame.AsSpan(12 + plain.Length, 16));
            return frame;
        }

        /// <summary>Opens the next frame; throws if it was altered, replayed or out of order.</summary>
        public byte[] Open(byte[] frame)
        {
            if (frame.Length < 28 || !frame.AsSpan(0, 12).SequenceEqual(Nonce(_counter)))
                throw new CryptographicException("frame out of order");
            var plain = new byte[frame.Length - 28];
            _aes.Decrypt(frame.AsSpan(0, 12), frame.AsSpan(12, plain.Length), frame.AsSpan(frame.Length - 16, 16), plain);
            _counter++;
            return plain;
        }

        static byte[] Nonce(long n)
        {
            var b = new byte[12];
            BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(4), n);
            return b;
        }
    }
}
