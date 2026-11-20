using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Shammy.Core.PhoneLink;
using Xunit;

namespace Shammy.Core.Tests;

/// <summary>The smart round's pure parts (21 Nov): names, links, file checks, battery nudges and the new messages.</summary>
public class PhoneLinkShareTests
{
    [Fact]
    public void ReceivedFilesAreNamedAndKeptLikeBrowserUploads()
    {
        // names go through SafeFileName: leaf only, spoofing characters gone, device names defused
        Assert.Equal("evil.exe", IncomingFile.Start(Start(5, name: "../../evil.exe"))!.Name);
        Assert.Equal("invoicefdp.exe", IncomingFile.Start(Start(5, name: "invoice\u202Efdp.exe"))!.Name);
        Assert.Equal("_nul .txt", IncomingFile.Start(Start(5, name: "nul .txt"))!.Name);
        Assert.Equal("file", IncomingFile.Start(Start(5, name: ".."))!.Name);
        Assert.Equal("a_b.txt", LinkShare.FileStart("x", "a:b.txt", 1)["name"]!.GetValue<string>());

        var dir = Directory.CreateTempSubdirectory("shammy-link-").FullName;
        try
        {
            string Arrive(string name)
            {
                var temp = Path.Combine(dir, $".shammy-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(temp, name);
                return Path.GetFileName(PhoneSend.SafeFileName.MoveIn(temp, dir, name));
            }
            Assert.Equal("a.txt", Arrive("a.txt"));
            Assert.Equal("a (1).txt", Arrive("a.txt"));
            Assert.Equal("a.txt", File.ReadAllText(Path.Combine(dir, "a.txt")));
            Assert.Equal("link.lnk.blocked", Arrive("link.lnk"));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CopiedTextIsCappedWithoutSplittingAnEmoji()
    {
        Assert.Equal("hi", LinkShare.CapClip("hi"));
        Assert.Equal(LinkShare.MaxClip, LinkShare.CapClip(new string('a', 20000)).Length);
        var emoji = new string('a', LinkShare.MaxClip - 1) + "\U0001F600";
        Assert.Equal(LinkShare.MaxClip - 1, LinkShare.CapClip(emoji).Length);
    }

    [Theory]
    [InlineData("https://example.com/a?b=c", true)]
    [InlineData("http://192.168.1.4:8080/x", true)]
    [InlineData("  https://example.com  ", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("example.com/page", false)]
    [InlineData("https://exa mple.com", false)]
    [InlineData("https://example.com/\n", true)]
    [InlineData("", false)]
    public void OnlyWebLinksOpen(string url, bool opens) => Assert.Equal(opens, LinkShare.Url(url) is not null);

    [Fact]
    public void LinksAreCappedAt2048()
    {
        Assert.Equal("https://example.com/", LinkShare.Url("  HTTPS://Example.com  "));
        var url = "https://example.com/" + new string('a', 2048);
        Assert.Null(LinkShare.Url(url));
        Assert.NotNull(LinkShare.Url(url[..LinkShare.MaxUrl]));
    }

    static JsonObject Start(long size, string id = "f1", string name = "a.bin") =>
        new() { ["t"] = "file", ["id"] = id, ["name"] = name, ["size"] = size, ["mime"] = "application/octet-stream" };

    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public void AFileIsKeptOnlyWhenItsSizeAndHashMatch()
    {
        var bytes = RandomNumberGenerator.GetBytes(100_000);
        using (var f = IncomingFile.Start(Start(bytes.Length))!)
        {
            Assert.True(f.Take(0, bytes.AsSpan(0, 60_000)));
            Assert.True(f.Take(1, bytes.AsSpan(60_000)));
            Assert.True(f.Complete(Sha(bytes)));
        }
        using (var f = IncomingFile.Start(Start(bytes.Length))!)
        {
            Assert.True(f.Take(0, bytes));
            Assert.False(f.Complete(Sha([1, 2, 3])));
        }
        using (var f = IncomingFile.Start(Start(bytes.Length))!)
        {
            // short: the hash of what came matches, the size doesn't
            Assert.True(f.Take(0, bytes.AsSpan(0, 10)));
            Assert.False(f.Complete(Sha(bytes[..10])));
        }
        using (var f = IncomingFile.Start(Start(3))!)
            Assert.False(f.Complete("not hex"));
    }

    [Fact]
    public void PiecesMustComeInOrderAndNotPastTheSize()
    {
        using var f = IncomingFile.Start(Start(10))!;
        Assert.False(f.Take(1, new byte[2]));
        Assert.True(f.Take(0, new byte[4]));
        Assert.False(f.Take(0, new byte[4]));
        Assert.False(f.Take(1, new byte[7]));
        Assert.True(f.Take(1, new byte[6]));
        Assert.Equal(10, f.Received);
    }

    [Fact]
    public void FileStartsAreCheckedAndNamesCleaned()
    {
        Assert.Null(IncomingFile.Start(Start(LinkShare.MaxFile + 1)));
        Assert.Null(IncomingFile.Start(Start(-1)));
        Assert.Null(IncomingFile.Start(Start(5, id: "")));
        Assert.Null(IncomingFile.Start(new JsonObject { ["t"] = "file", ["id"] = "x", ["name"] = "a", ["size"] = "big" }));
        Assert.NotNull(IncomingFile.Start(Start(LinkShare.MaxFile)));

        var start = LinkShare.FileStart("id1", "Holiday.JPG", 1234);
        Assert.Equal("file", start["t"]!.GetValue<string>());
        Assert.Equal("image/jpeg", start["mime"]!.GetValue<string>());
        Assert.Equal(1234, start["size"]!.GetValue<long>());
        Assert.Equal("application/octet-stream", LinkShare.Mime("setup"));
    }

    [Fact]
    public void ThePhoneIsNudgedAt20And10OnceEachPerDischarge()
    {
        int last = 101;
        int? Step(int percent, bool charging)
        {
            if (charging) last = 101;
            var level = LinkStatus.Nudge(percent, charging, last);
            if (level is { } l) last = l;
            return level;
        }
        Assert.Null(Step(40, false));
        Assert.Equal(20, Step(20, false));
        Assert.Null(Step(18, false));
        Assert.Null(Step(15, false));
        Assert.Equal(10, Step(10, false));
        Assert.Null(Step(5, false));
        Assert.Null(Step(5, true)); // charging: never, and the next discharge starts over
        Assert.Equal(20, Step(19, false));
        // straight from 25 % to 8 %: one nudge, for 10 %
        last = 101;
        Assert.Equal(10, Step(8, false));
        Assert.Null(Step(7, false));
    }

    [Fact]
    public void TheLaptopTellsThePhoneOnWholeStepsOrAChargingChange()
    {
        Assert.True(LinkStatus.Moved(null, 80, false));
        Assert.False(LinkStatus.Moved((80, false), 77, false));
        Assert.True(LinkStatus.Moved((80, false), 75, false));
        Assert.True(LinkStatus.Moved((80, false), 80, true));
        Assert.False(LinkStatus.Moved((-1, false), -1, false));

        var m = LinkStatus.Laptop(true, 64, true);
        Assert.Equal("status", m["t"]!.GetValue<string>());
        Assert.Equal(64, m["battery"]!.GetValue<int>());
        Assert.True(m["charging"]!.GetValue<bool>());
        var none = LinkStatus.Laptop(false, 100, true);
        Assert.Equal(-1, none["battery"]!.GetValue<int>());
        Assert.False(none["charging"]!.GetValue<bool>());
    }

    [Fact]
    public void ThePhonesStatusAndMusicAreReadSafely()
    {
        var s = PhoneStatus.From(new JsonObject { ["t"] = "status", ["battery"] = 42, ["charging"] = true, ["ringer"] = "vibrate", ["dnd"] = false, ["canRinger"] = true })!;
        Assert.Equal(new PhoneStatus(42, true, "vibrate", false, true), s);
        Assert.Equal("", PhoneStatus.From(new JsonObject { ["battery"] = 5, ["ringer"] = "loud" })!.Ringer);
        Assert.False(PhoneStatus.From(new JsonObject { ["battery"] = 5, ["charging"] = "yes" })!.Charging);
        Assert.Null(PhoneStatus.From(new JsonObject { ["battery"] = 140 }));
        Assert.Null(PhoneStatus.From(new JsonObject { ["ringer"] = "sound" }));

        var a = PhoneMedia.From(new JsonObject { ["t"] = "phoneMedia", ["has"] = true, ["title"] = "Song", ["artist"] = "Band", ["playing"] = true, ["volume"] = 0.4, ["art"] = "QUJD" });
        Assert.True(a.Has && a.Playing);
        Assert.Equal(0.4, a.Volume);
        Assert.Equal("QUJD", a.Art);
        var b = PhoneMedia.From(new JsonObject { ["has"] = true, ["title"] = "Song", ["artist"] = "Band", ["volume"] = 7 });
        Assert.True(b.SameSong(a));
        Assert.Equal(-1, b.Volume);
        Assert.Equal("", b.Art);
        Assert.False(PhoneMedia.From(new JsonObject { ["title"] = "Other" }).SameSong(a));
        Assert.False(a.SameSong(null));
    }
}
