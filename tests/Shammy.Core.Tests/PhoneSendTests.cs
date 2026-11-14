using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Shammy.Core.PhoneSend;

namespace Shammy.Core.Tests;

public class HttpRequestHeadTests
{
    static HttpRequestHead Parse(string s)
    {
        Assert.True(HttpRequestHead.TryParse(Encoding.Latin1.GetBytes(s), out var head));
        return head!;
    }

    static bool Fails(string s) => !HttpRequestHead.TryParse(Encoding.Latin1.GetBytes(s), out _);

    [Fact]
    public void ParsesRequestLineAndHeaders()
    {
        var h = Parse("GET /abc/file?x=1 HTTP/1.1\r\nHost: 192.168.1.5:49300\r\nrange: bytes=0-\r\nAccept: a\r\nAccept: b\r\n\r\n");
        Assert.Equal("GET", h.Method);
        Assert.Equal("/abc/file?x=1", h.Target);
        Assert.Equal("/abc/file", h.Path);
        Assert.Equal("HTTP/1.1", h.Version);
        Assert.Equal("bytes=0-", h["Range"]);
        Assert.Equal("a, b", h["accept"]);
        Assert.Null(h["Missing"]);
    }

    [Fact]
    public void ReadsBodyRelatedHeaders()
    {
        var h = Parse("POST /t/upload HTTP/1.1\r\nContent-Length: 1234\r\nExpect: 100-continue\r\n\r\n");
        Assert.Equal(1234, h.ContentLength);
        Assert.True(h.ExpectsContinue);
        Assert.False(h.IsChunked);
        Assert.Null(Parse("POST / HTTP/1.1\r\nContent-Length: -5\r\n\r\n").ContentLength);
        Assert.True(Parse("POST / HTTP/1.1\r\nTransfer-Encoding: gzip, chunked\r\n\r\n").IsChunked);
    }

    [Fact]
    public void ToleratesBareLineFeedsAndLeadingBlankLine()
    {
        var h = Parse("\r\nGET / HTTP/1.0\nHost: x\n\n");
        Assert.Equal("/", h.Path);
        Assert.Equal("x", h["Host"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("GET /\r\n\r\n")]
    [InlineData("GET / HTTP/2.0\r\n\r\n")]
    [InlineData("GET  / HTTP/1.1\r\n\r\n")]
    [InlineData("GET abc HTTP/1.1\r\n\r\n")]
    [InlineData("GET /a\u00FF HTTP/1.1\r\n\r\n")]
    [InlineData("G(T / HTTP/1.1\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost : x\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nNoColon\r\n\r\n")]
    public void RejectsMalformedHeads(string head) => Assert.True(Fails(head));

    [Fact]
    public void FindsEndOfHead()
    {
        Assert.Equal(-1, HttpRequestHead.FindHeadEnd("GET / HTTP/1.1\r\nHost: x\r\n"u8));
        Assert.Equal(27, HttpRequestHead.FindHeadEnd("GET / HTTP/1.1\r\nHost: x\r\n\r\nBODY"u8));
        Assert.Equal(12, HttpRequestHead.FindHeadEnd("GET / HTTP\n\nBODY"u8));
    }

    [Theory]
    [InlineData(null, 100, ByteRange.Kind.None, 0, 99)]
    [InlineData("bytes=0-", 100, ByteRange.Kind.Satisfiable, 0, 99)]
    [InlineData("bytes=10-19", 100, ByteRange.Kind.Satisfiable, 10, 19)]
    [InlineData("bytes=90-500", 100, ByteRange.Kind.Satisfiable, 90, 99)]
    [InlineData("bytes=-10", 100, ByteRange.Kind.Satisfiable, 90, 99)]
    [InlineData("bytes=-500", 100, ByteRange.Kind.Satisfiable, 0, 99)]
    [InlineData("bytes=100-", 100, ByteRange.Kind.Unsatisfiable, 0, 99)]
    [InlineData("bytes=-0", 100, ByteRange.Kind.Unsatisfiable, 0, 99)]
    [InlineData("bytes=0-1,5-6", 100, ByteRange.Kind.None, 0, 99)]
    [InlineData("items=0-5", 100, ByteRange.Kind.None, 0, 99)]
    [InlineData("bytes=5-2", 100, ByteRange.Kind.None, 0, 99)]
    [InlineData("bytes=abc", 100, ByteRange.Kind.None, 0, 99)]
    [InlineData("bytes=0-", 0, ByteRange.Kind.Unsatisfiable, 0, -1)]
    public void ParsesRanges(string? header, long length, ByteRange.Kind kind, long start, long end)
    {
        var k = ByteRange.Parse(header, length, out var s, out var e);
        Assert.Equal(kind, k);
        if (k == ByteRange.Kind.Satisfiable)
        {
            Assert.Equal(start, s);
            Assert.Equal(end, e);
        }
    }
}

public class MultipartReaderTests
{
    const string Boundary = "----WebKitFormBoundaryX7a";

    static byte[] Body(params (string Headers, byte[] Content)[] parts)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.UTF8.GetBytes(s));
        foreach (var (headers, content) in parts)
        {
            W("--" + Boundary + "\r\n" + headers + "\r\n\r\n");
            ms.Write(content);
            W("\r\n");
        }
        W("--" + Boundary + "--\r\n");
        return ms.ToArray();
    }

    static async Task<List<(MultipartSection Section, byte[] Content)>> ReadAll(Stream input, long max = long.MaxValue)
    {
        var reader = new MultipartReader(input, Boundary, 1024);
        var list = new List<(MultipartSection, byte[])>();
        while (await reader.ReadNextSectionAsync() is { } s)
        {
            var dest = new MemoryStream();
            await reader.CopyBodyToAsync(dest, max);
            list.Add((s, dest.ToArray()));
        }
        return list;
    }

    [Fact]
    public async Task ReadsFieldsAndFiles()
    {
        var data = Enumerable.Range(0, 70_000).Select(i => (byte)(i * 31)).ToArray();
        var body = Body(
            ("Content-Disposition: form-data; name=\"note\"", "hello"u8.ToArray()),
            ("Content-Disposition: form-data; name=\"file\"; filename=\"caf\u00E9 \u2615.bin\"\r\nContent-Type: application/octet-stream", data));
        var parts = await ReadAll(new MemoryStream(body));
        Assert.Equal(2, parts.Count);
        Assert.Equal("note", parts[0].Section.Name);
        Assert.False(parts[0].Section.IsFile);
        Assert.Equal("hello", Encoding.UTF8.GetString(parts[1 - 1].Content));
        Assert.Equal("caf\u00E9 \u2615.bin", parts[1].Section.FileName);
        Assert.Equal("application/octet-stream", parts[1].Section.Headers["content-type"]);
        Assert.Equal(data, parts[1].Content);
    }

    [Fact]
    public async Task WorksWhenBytesTrickleInAcrossBoundaries()
    {
        // Content that almost looks like the delimiter must survive being split at every possible point.
        var tricky = Encoding.ASCII.GetBytes("a\r\n--" + Boundary[..^1] + "Z\r\n-\r\n--x\r");
        var body = Body(
            ("Content-Disposition: form-data; name=\"f\"; filename=\"a.txt\"", tricky),
            ("Content-Disposition: form-data; name=\"g\"; filename=\"b.txt\"", Array.Empty<byte>()));
        var parts = await ReadAll(new TrickleStream(body, 3));
        Assert.Equal(2, parts.Count);
        Assert.Equal(tricky, parts[0].Content);
        Assert.Empty(parts[1].Content);
    }

    [Fact]
    public async Task SkipsPreambleAndUnreadBodies()
    {
        var body = Encoding.ASCII.GetBytes("This is a preamble\r\n").Concat(Body(
            ("Content-Disposition: form-data; name=\"a\"", "skip me"u8.ToArray()),
            ("Content-Disposition: form-data; name=\"b\"", "keep"u8.ToArray()))).ToArray();
        var reader = new MultipartReader(new MemoryStream(body), Boundary);
        Assert.Equal("a", (await reader.ReadNextSectionAsync())!.Name);
        var b = await reader.ReadNextSectionAsync();
        Assert.Equal("b", b!.Name);
        var dest = new MemoryStream();
        await reader.CopyBodyToAsync(dest);
        Assert.Equal("keep", Encoding.ASCII.GetString(dest.ToArray()));
        Assert.Null(await reader.ReadNextSectionAsync());
        Assert.Null(await reader.ReadNextSectionAsync());
    }

    [Fact]
    public async Task HandlesPartWithoutHeaders()
    {
        var body = Encoding.ASCII.GetBytes($"--{Boundary}\r\n\r\nraw\r\n--{Boundary}--");
        var parts = await ReadAll(new MemoryStream(body));
        Assert.Single(parts);
        Assert.Empty(parts[0].Section.Headers);
        Assert.Equal("raw", Encoding.ASCII.GetString(parts[0].Content));
    }

    [Fact]
    public async Task TruncatedBodyThrows()
    {
        var body = Body(("Content-Disposition: form-data; name=\"f\"; filename=\"a\"", new byte[5000]));
        var cut = body[..^(Boundary.Length + 8)];
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAll(new MemoryStream(cut)));
    }

    [Fact]
    public async Task EnforcesSizeLimit()
    {
        var body = Body(("Content-Disposition: form-data; name=\"f\"; filename=\"a\"", new byte[5000]));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAll(new MemoryStream(body), max: 4999));
        Assert.Single(await ReadAll(new MemoryStream(body), max: 5000));
    }

    [Theory]
    [InlineData("multipart/form-data; boundary=abc", "abc")]
    [InlineData("multipart/form-data; charset=utf-8; boundary=\"a b:c\"", "a b:c")]
    [InlineData("Multipart/Form-Data;boundary=----WebKitFormBoundaryX", "----WebKitFormBoundaryX")]
    [InlineData("multipart/form-data", null)]
    [InlineData("application/json; boundary=abc", null)]
    [InlineData("multipart/form-data; boundary=\"\"", null)]
    [InlineData(null, null)]
    public void ExtractsBoundary(string? contentType, string? expected) =>
        Assert.Equal(expected, MultipartReader.BoundaryFrom(contentType));

    [Fact]
    public void RejectsOverlongBoundary() =>
        Assert.Null(MultipartReader.BoundaryFrom("multipart/form-data; boundary=" + new string('a', 71)));

    /// <summary>Returns at most a few bytes per read, like a slow phone on bad Wi-Fi.</summary>
    sealed class TrickleStream : Stream
    {
        readonly byte[] _data;
        readonly int _max;
        int _pos, _turn;

        public TrickleStream(byte[] data, int max) => (_data, _max) = (data, max);

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(count, 1 + _turn++ % _max), _data.Length - _pos);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public class SafeFileNameTests
{
    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("../../Windows/System32/evil.dll", "evil.dll")]
    [InlineData("..\\..\\evil.exe", "evil.exe")]
    [InlineData("C:\\Users\\x\\notes.txt", "notes.txt")]
    [InlineData("a:b*c?d\"e<f>g|h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("report.txt:hidden", "report.txt_hidden")]
    [InlineData("name.txt. . .", "name.txt")]
    [InlineData("  spaced  .md", "spaced  .md")]
    [InlineData("tab\there\r\n.txt", "tab here  .txt")]
    [InlineData("bell\u0007\u007F.txt", "bell.txt")]
    [InlineData(".bashrc", ".bashrc")]
    [InlineData("..", "file")]
    [InlineData("...", "file")]
    [InlineData("", "file")]
    [InlineData(null, "file")]
    [InlineData("   ", "file")]
    [InlineData("/", "file")]
    [InlineData("dir/", "file")]
    public void Sanitizes(string? input, string expected) => Assert.Equal(expected, SafeFileName.Sanitize(input));

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("NUL.tar.gz", "_NUL.tar.gz")]
    [InlineData("com1.log", "_com1.log")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData("COM\u00B9.txt", "_COM\u00B9.txt")]
    [InlineData("CON .txt", "_CON .txt")]
    [InlineData("console.txt", "console.txt")]
    [InlineData("com10.txt", "com10.txt")]
    public void EscapesReservedDeviceNames(string input, string expected) => Assert.Equal(expected, SafeFileName.Sanitize(input));

    [Fact]
    public void StripsSpoofingCharactersButKeepsRealUnicode()
    {
        // U+202E would render "invoice\u202Egpj.exe" as "invoiceexe.jpg".
        Assert.Equal("invoicegpj.exe", SafeFileName.Sanitize("invoice\u202Egpj.exe"));
        Assert.Equal("ab.txt", SafeFileName.Sanitize("a\u200Bb.txt"));
        Assert.Equal("\u65E5\u672C\u8A9E \uD83D\uDCF8.png", SafeFileName.Sanitize("\u65E5\u672C\u8A9E \uD83D\uDCF8.png"));
        Assert.Equal("x.txt", SafeFileName.Sanitize("x\uD800.txt")); // unpaired surrogate
        // Decomposed e + combining accent becomes the composed form.
        Assert.Equal("caf\u00E9.txt", SafeFileName.Sanitize("cafe\u0301.txt"));
    }

    [Fact]
    public void TruncatesLongNamesKeepingExtension()
    {
        var s = SafeFileName.Sanitize(new string('a', 300) + ".jpeg");
        Assert.Equal(SafeFileName.MaxLength, s.Length);
        Assert.EndsWith(".jpeg", s);

        // Never split an emoji's surrogate pair.
        var emoji = SafeFileName.Sanitize(string.Concat(Enumerable.Repeat("\uD83D\uDE00", 150)) + ".txt");
        Assert.True(emoji.Length <= SafeFileName.MaxLength);
        Assert.EndsWith(".txt", emoji);
        Assert.False(char.IsHighSurrogate(emoji[^5]));
    }

    [Fact]
    public void FallbackIsConfigurable() => Assert.Equal("upload", SafeFileName.Sanitize("..", "upload"));

    [Fact]
    public void MakesNamesUniqueLikeExplorer()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine("D", "a.txt"), Path.Combine("D", "a (1).txt"), Path.Combine("D", "noext"),
        };
        Assert.Equal(Path.Combine("D", "a (2).txt"), SafeFileName.MakeUnique("D", "a.txt", taken.Contains));
        Assert.Equal(Path.Combine("D", "b.txt"), SafeFileName.MakeUnique("D", "b.txt", taken.Contains));
        Assert.Equal(Path.Combine("D", "noext (1)"), SafeFileName.MakeUnique("D", "noext", taken.Contains));
    }

    [Fact]
    public void ChecksContainment()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shammy-dl");
        Assert.True(SafeFileName.IsDirectlyInside(dir, Path.Combine(dir, "a.txt")));
        Assert.True(SafeFileName.IsDirectlyInside(dir + Path.DirectorySeparatorChar, Path.Combine(dir, "a.txt")));
        Assert.False(SafeFileName.IsDirectlyInside(dir, Path.Combine(dir, "..", "a.txt")));
        Assert.False(SafeFileName.IsDirectlyInside(dir, Path.Combine(dir, "sub", "a.txt")));
        Assert.False(SafeFileName.IsDirectlyInside(dir, dir + "-evil" + Path.DirectorySeparatorChar + "a.txt"));
    }
}

public class ContentDispositionTests
{
    [Fact]
    public void BuildsAsciiAttachment() =>
        Assert.Equal("attachment; filename=\"report.pdf\"; filename*=UTF-8''report.pdf", ContentDisposition.Attachment("report.pdf"));

    [Fact]
    public void EncodesUnicodeAndSpecials()
    {
        var h = ContentDisposition.Attachment("caf\u00E9 \"q\" 100%.txt");
        Assert.Equal("attachment; filename=\"caf_ _q_ 100%.txt\"; filename*=UTF-8''caf%C3%A9%20%22q%22%20100%25.txt", h);
        Assert.True(h.All(c => c < 0x80));
    }

    [Fact]
    public void RoundTripsThroughParser()
    {
        foreach (var name in new[] { "a b.txt", "\u65E5\u672C.png", "x'y;z=\"w\".bin", "\uD83D\uDE00.jpg" })
            Assert.Equal(name, ContentDisposition.FileName(ContentDisposition.Attachment(name)));
    }

    [Fact]
    public void ParsesFormDataParameters()
    {
        var p = ContentDisposition.Parameters("form-data; name=\"file\"; filename=\"a \\\"b\\\".txt\"");
        Assert.Equal("file", p["name"]);
        Assert.Equal("a \"b\".txt", p["filename"]);

        Assert.Equal("plain", ContentDisposition.FileName("attachment; filename=plain"));
        Assert.Equal("\u00E9.txt", ContentDisposition.FileName("attachment; filename*=UTF-8''%C3%A9.txt; filename=\"e.txt\""));
        Assert.Null(ContentDisposition.FileName("form-data; name=\"x\""));
        Assert.Null(ContentDisposition.FileName(null));
        Assert.Equal("b", ContentDisposition.Parameters("form-data; junk; name=b")["name"]);
    }
}

public class TransferTokenTests
{
    [Fact]
    public void TokensAreUrlSafe128BitAndUnique()
    {
        var tokens = Enumerable.Range(0, 2000).Select(_ => TransferToken.New()).ToList();
        Assert.All(tokens, t =>
        {
            Assert.Equal(22, t.Length);
            Assert.Matches("^[A-Za-z0-9_-]{22}$", t);
        });
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Fact]
    public void MatchesExactlyOnly()
    {
        var t = TransferToken.New();
        Assert.True(TransferToken.Matches(t, t));
        Assert.False(TransferToken.Matches(t, t[..^1]));
        Assert.False(TransferToken.Matches(t, t + "x"));
        Assert.False(TransferToken.Matches(t, null));
    }

    [Theory]
    [InlineData("a.JPG", "image/jpeg")]
    [InlineData("b.pdf", "application/pdf")]
    [InlineData("c.mp4", "video/mp4")]
    [InlineData("d.unknownext", "application/octet-stream")]
    [InlineData("noext", "application/octet-stream")]
    public void MapsMimeTypes(string name, string type) => Assert.Equal(type, MimeTypes.ForFile(name));

    [Fact]
    public void FormatsSizes()
    {
        Assert.Equal("512 B", ByteSize.Format(512));
        Assert.Equal("2 KB", ByteSize.Format(2048));
        Assert.Equal("1.5 MB", ByteSize.Format(1536 * 1024));
        Assert.Equal("4.0 GB", ByteSize.Format(4L << 30));
    }

    [Fact]
    public void PageEscapesFileName()
    {
        var html = PhonePage.Transfer("TOKEN", "<script>alert(1)</script>.txt", 10);
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("/TOKEN/file", html);
        Assert.Contains("/TOKEN/upload", html);
        Assert.Contains("multipart/form-data", html);
    }
}

public class LanAddressPickerTests
{
    static NetworkCandidate Nic(string name, string ip, NetworkInterfaceType type = NetworkInterfaceType.Wireless80211,
        bool gateway = true, OperationalStatus status = OperationalStatus.Up, string? description = null) =>
        new(name, description ?? name, type, status, gateway, new[] { IPAddress.Parse(ip) });

    [Fact]
    public void PrefersRealWifiOverVirtualAdapters()
    {
        var nics = new[]
        {
            Nic("vEthernet (Default Switch)", "172.24.80.1", NetworkInterfaceType.Ethernet, gateway: false, description: "Hyper-V Virtual Ethernet Adapter"),
            Nic("vEthernet (WSL)", "172.30.0.1", NetworkInterfaceType.Ethernet, gateway: false, description: "Hyper-V Virtual Ethernet Adapter #2"),
            Nic("Tailscale", "100.101.102.103", NetworkInterfaceType.Unknown, gateway: false, description: "Tailscale Tunnel"),
            Nic("Wi-Fi", "192.168.1.42", description: "Intel(R) Wi-Fi 6 AX201 160MHz"),
            Nic("Loopback Pseudo-Interface 1", "127.0.0.1", NetworkInterfaceType.Loopback),
        };
        Assert.Equal(IPAddress.Parse("192.168.1.42"), LanAddressPicker.Pick(nics));
    }

    [Fact]
    public void PrefersAdapterWithGatewayEvenIfVpnHasOne()
    {
        var nics = new[]
        {
            Nic("NordLynx", "10.5.0.2", NetworkInterfaceType.Unknown, gateway: true, description: "WireGuard Tunnel"),
            Nic("Ethernet", "10.0.0.15", NetworkInterfaceType.Ethernet, description: "Realtek PCIe GbE Family Controller"),
        };
        Assert.Equal(IPAddress.Parse("10.0.0.15"), LanAddressPicker.Pick(nics));
    }

    [Fact]
    public void SkipsUnusableAddresses()
    {
        var nics = new[]
        {
            Nic("Wi-Fi", "169.254.10.20"),
            Nic("Ethernet", "192.168.0.9", NetworkInterfaceType.Ethernet, status: OperationalStatus.Down),
            new NetworkCandidate("Wi-Fi 2", "x", NetworkInterfaceType.Wireless80211, OperationalStatus.Up, true, new[] { IPAddress.IPv6Loopback }),
        };
        Assert.Null(LanAddressPicker.Pick(nics));
        Assert.Null(LanAddressPicker.Pick(Array.Empty<NetworkCandidate>()));
    }

    [Fact]
    public void FallsBackToVirtualWhenNothingElse()
    {
        var nics = new[] { Nic("vEthernet (Default Switch)", "172.24.80.1", NetworkInterfaceType.Ethernet, gateway: false) };
        Assert.Equal(IPAddress.Parse("172.24.80.1"), LanAddressPicker.Pick(nics));
    }

    [Fact]
    public void SystemEnumerationNeverThrows() => Assert.NotNull(LanAddressPicker.FromSystem());
}

public class PhoneSendPhraseTests
{
    [Theory]
    [InlineData("send to phone", null)]
    [InlineData("Send to my phone.", null)]
    [InlineData("send this to my phone", null)]
    [InlineData("send it to the phone", null)]
    [InlineData("send the latest file to phone", null)]
    [InlineData("send report.pdf to phone", "report.pdf")]
    [InlineData("please send \"holiday photo\" to my iphone", "holiday photo")]
    [InlineData("share notes to phone", "notes")]
    [InlineData("airdrop slides to my mobile", "slides")]
    public void ParsesCommands(string text, string? query)
    {
        Assert.True(PhoneSendPhrase.TryParse(text, out var q));
        Assert.Equal(query, q);
    }

    [Theory]
    [InlineData("how to send files to phone")]
    [InlineData("send money to mom")]
    [InlineData("phone")]
    [InlineData("send to phones")]
    [InlineData("best phone to buy")]
    [InlineData("")]
    public void IgnoresOtherText(string text) => Assert.False(PhoneSendPhrase.TryParse(text, out _));

    [Fact]
    public void FindsShelfItems()
    {
        var names = new[] { "Quarterly_Report-2024.pdf", "photo.jpg", "notes.txt", "photo.png" };
        Assert.Equal(3, PhoneSendPhrase.FindItem(names, null));
        Assert.Equal(1, PhoneSendPhrase.FindItem(names, "PHOTO.JPG"));
        Assert.Equal(3, PhoneSendPhrase.FindItem(names, "photo")); // most recent of equals
        Assert.Equal(2, PhoneSendPhrase.FindItem(names, "notes"));
        Assert.Equal(0, PhoneSendPhrase.FindItem(names, "quarterly report"));
        Assert.Equal(0, PhoneSendPhrase.FindItem(names, "report dot pdf"));
        Assert.Equal(0, PhoneSendPhrase.FindItem(names, "2024"));
        Assert.Equal(-1, PhoneSendPhrase.FindItem(names, "invoice"));
        Assert.Equal(-1, PhoneSendPhrase.FindItem(Array.Empty<string>(), null));
    }

    [Fact]
    public void RecognisesFileNameShapedQueries()
    {
        Assert.True(PhoneSendPhrase.LooksLikeFileName("report.pdf"));
        Assert.False(PhoneSendPhrase.LooksLikeFileName("money"));
    }
}

/// <summary>The real server on 127.0.0.1 with short timeouts; each test takes milliseconds.</summary>
public sealed class PhoneTransferServerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "shammy-phonesend-" + Guid.NewGuid().ToString("N"));
    readonly string _uploads;
    readonly string _file;
    readonly byte[] _data;
    readonly PhoneTransferServer _server;
    readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };

    public PhoneTransferServerTests()
    {
        Directory.CreateDirectory(_dir);
        _uploads = Path.Combine(_dir, "Downloads");
        _file = Path.Combine(_dir, "r\u00E9sum\u00E9 final.pdf");
        _data = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(_file, _data);
        _server = new PhoneTransferServer(new PhoneTransferOptions
        {
            FilePath = _file,
            UploadDirectory = _uploads,
            BindAddress = IPAddress.Loopback,
            MaxUploadBytes = 1_000_000,
            HeaderTimeout = TimeSpan.FromMilliseconds(400),
            StallTimeout = TimeSpan.FromSeconds(2),
        });
        _server.Start();
    }

    string Url(string path = "") => $"http://127.0.0.1:{_server.Port}/{_server.Token}{path}";

    public void Dispose()
    {
        _server.Dispose();
        _http.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void BindsInsideTheConfiguredRange()
    {
        Assert.True(_server.IsRunning);
        Assert.InRange(_server.Port, 49200, 49900);
        Assert.Matches("^[A-Za-z0-9_-]{22}$", _server.Token);
    }

    [Fact]
    public async Task ServesLandingPage()
    {
        var res = await _http.GetAsync(Url());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType!.MediaType);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("r\u00E9sum\u00E9 final.pdf", html);
        Assert.Contains("293 KB", html);
        Assert.True(res.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/favicon.ico")]
    [InlineData("/AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("/AAAAAAAAAAAAAAAAAAAAAA/file")]
    public async Task UnknownPathsAre404(string path)
    {
        var res = await _http.GetAsync($"http://127.0.0.1:{_server.Port}{path}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task OtherRoutesUnderTheTokenAre404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(Url("/nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(Url("/file/x"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(Url("/upload"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync(Url("/file"), new StringContent("x"))).StatusCode);
    }

    [Fact]
    public async Task DownloadsTheFileAndCountsCompletion()
    {
        var res = await _http.GetAsync(Url("/file"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/pdf", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal(_data.Length, res.Content.Headers.ContentLength);
        Assert.Equal("attachment", res.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("r\u00E9sum\u00E9 final.pdf", res.Content.Headers.ContentDisposition.FileNameStar);
        Assert.Equal(_data, await res.Content.ReadAsByteArrayAsync());
        await WaitFor(() => _server.CompletedDownloads == 1);
        Assert.Equal(_data.Length, _server.BytesSent);
        Assert.Equal(0, _server.ActiveDownloads);
    }

    [Fact]
    public async Task ServesRanges()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Url("/file"));
        req.Headers.Range = new RangeHeaderValue(100, 199);
        var res = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, res.StatusCode);
        Assert.Equal($"bytes 100-199/{_data.Length}", res.Content.Headers.ContentRange!.ToString());
        Assert.Equal(_data[100..200], await res.Content.ReadAsByteArrayAsync());

        // A tail probe (what video players do) must not count as a finished download.
        req = new HttpRequestMessage(HttpMethod.Get, Url("/file"));
        req.Headers.Range = new RangeHeaderValue(null, 1000);
        res = await _http.SendAsync(req);
        Assert.Equal(_data[^1000..], await res.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, _server.CompletedDownloads);

        req = new HttpRequestMessage(HttpMethod.Get, Url("/file"));
        req.Headers.Range = new RangeHeaderValue(_data.Length, null);
        res = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, res.StatusCode);
    }

    [Fact]
    public async Task ResumedDownloadCountsAsComplete()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Url("/file"));
        req.Headers.Range = new RangeHeaderValue(0, 99_999);
        await (await _http.SendAsync(req)).Content.ReadAsByteArrayAsync();
        Assert.Equal(0, _server.CompletedDownloads);
        req = new HttpRequestMessage(HttpMethod.Get, Url("/file"));
        req.Headers.Range = new RangeHeaderValue(100_000, null);
        await (await _http.SendAsync(req)).Content.ReadAsByteArrayAsync();
        await WaitFor(() => _server.CompletedDownloads == 1);
    }

    [Fact]
    public async Task HandlesConcurrentDownloads()
    {
        var all = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _http.GetByteArrayAsync(Url("/file"))));
        Assert.All(all, b => Assert.Equal(_data, b));
        await WaitFor(() => _server.CompletedDownloads == 8);
    }

    [Fact]
    public async Task HeadReturnsHeadersOnly()
    {
        var res = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Head, Url("/file")));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(_data.Length, res.Content.Headers.ContentLength);
        Assert.Equal(0, _server.CompletedDownloads);
    }

    [Fact]
    public async Task ReceivesUploadsSafelyAndWithoutClobbering()
    {
        Directory.CreateDirectory(_uploads);
        File.WriteAllText(Path.Combine(_uploads, "evil.txt"), "existing");
        var received = new List<string>();
        _server.FileReceived += p => { lock (received) received.Add(p); };

        var res = await Upload(("../../evil.txt", "first"u8.ToArray()), ("\u65E5\u672C.txt", "second"u8.ToArray()), ("", Array.Empty<byte>()));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Sent to PC", await res.Content.ReadAsStringAsync());

        Assert.Equal("existing", File.ReadAllText(Path.Combine(_uploads, "evil.txt")));
        Assert.Equal("first", File.ReadAllText(Path.Combine(_uploads, "evil (1).txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(_uploads, "\u65E5\u672C.txt")));
        Assert.Equal(2, received.Count);
        Assert.All(received, p => Assert.True(SafeFileName.IsDirectlyInside(_uploads, p)));
        Assert.Equal(2, _server.FilesReceived);
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
        Assert.Empty(Directory.GetFiles(_uploads, "*.tmp"));
    }

    [Fact]
    public async Task RejectsOversizeUploads()
    {
        var res = await Upload(("big.bin", new byte[1_100_000]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
        Assert.False(Directory.Exists(_uploads) && Directory.GetFiles(_uploads).Length > 0);
    }

    [Fact]
    public async Task RejectsNonMultipartUploads()
    {
        var res = await _http.PostAsync(Url("/upload"), new StringContent("hello"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task TruncatedUploadLeavesNoFiles()
    {
        // Claim more bytes than we send, then hang up mid-body.
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _server.Port);
        var s = tcp.GetStream();
        var body = $"--b\r\nContent-Disposition: form-data; name=\"f\"; filename=\"half.bin\"\r\n\r\n" + new string('x', 5000);
        var head = $"POST /{_server.Token}/upload HTTP/1.1\r\nHost: x\r\nContent-Type: multipart/form-data; boundary=b\r\nContent-Length: 90000\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head + body));
        await Task.Delay(100);
        tcp.Close();
        await Task.Delay(200);
        Assert.False(Directory.Exists(_uploads) && Directory.GetFiles(_uploads).Length > 0);
        Assert.Equal(0, _server.FilesReceived);
    }

    [Fact]
    public async Task DropsClientsThatNeverSendAHead()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _server.Port);
        var s = tcp.GetStream();
        await s.WriteAsync("GET / HT"u8.ToArray());
        var buf = new byte[64];
        var read = s.ReadAsync(buf).AsTask();
        Assert.Same(read, await Task.WhenAny(read, Task.Delay(3000)));
        Assert.Equal(0, await read); // closed without a response
        // And the server still answers others.
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync(Url())).StatusCode);
    }

    [Fact]
    public async Task GarbageGets400AndServerSurvives()
    {
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, _server.Port);
            var s = tcp.GetStream();
            await s.WriteAsync("\x16\x03\x01 not http\r\n\r\n"u8.ToArray());
            var reply = await new StreamReader(s).ReadToEndAsync();
            Assert.StartsWith("HTTP/1.1 400", reply);
        }
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync(Url())).StatusCode);
    }

    [Fact]
    public async Task StopEndsInFlightTransfersAndRefusesNewOnes()
    {
        // Big enough that it can't all sit in loopback socket buffers, so the send really is in flight.
        var big = Path.Combine(_dir, "big.bin");
        using (var f = File.Create(big)) f.SetLength(48L << 20);
        using var server = new PhoneTransferServer(new PhoneTransferOptions
        {
            FilePath = big,
            UploadDirectory = _uploads,
            BindAddress = IPAddress.Loopback,
        });
        server.Start();

        using var tcp = new TcpClient { ReceiveBufferSize = 4096 };
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        var s = tcp.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes($"GET /{server.Token}/file HTTP/1.1\r\nHost: x\r\n\r\n"));
        await s.ReadAsync(new byte[1024]); // started; now stop reading so the server blocks on send
        await WaitFor(() => server.ActiveDownloads == 1);

        server.Stop();
        Assert.False(server.IsRunning);
        await WaitFor(() => server.ActiveDownloads == 0);
        Assert.Equal(0, server.CompletedDownloads);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _http.GetAsync($"http://127.0.0.1:{server.Port}/{server.Token}"));
    }

    async Task<HttpResponseMessage> Upload(params (string Name, byte[] Data)[] files)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.UTF8.GetBytes(s));
        const string b = "----ShammyTestBoundary";
        foreach (var (name, data) in files)
        {
            W($"--{b}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{name}\"\r\nContent-Type: application/octet-stream\r\n\r\n");
            ms.Write(data);
            W("\r\n");
        }
        W($"--{b}--\r\n");
        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={b}");
        return await _http.PostAsync(Url("/upload"), content);
    }

    static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
