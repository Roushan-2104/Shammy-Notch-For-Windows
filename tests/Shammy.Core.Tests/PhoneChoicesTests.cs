using Shammy.Core.PhoneSend;

namespace Shammy.Core.Tests;

public class PhoneChoicesTests
{
    static readonly DateTime Now = new(2026, 11, 14, 10, 0, 0);

    static string[] Ids(PhoneChoiceInputs input) => PhoneChoices.Build(input).Select(c => c.Id).ToArray();

    [Fact]
    public void NothingAroundLeavesOnlyAFileFromThePc()
    {
        var list = PhoneChoices.Build(new PhoneChoiceInputs { Now = Now });
        var only = Assert.Single(list);
        Assert.Equal(PhoneChoiceKind.PcFile, only.Kind);
        Assert.Equal("pc-file", only.Id);
    }

    [Fact]
    public void ListsEverythingInTheUsualOrder()
    {
        var ids = Ids(new PhoneChoiceInputs
        {
            Now = Now,
            CopiedText = "Meet at 6",
            ScreenshotAt = Now.AddMinutes(-2),
            ShelfNames = new[] { "a.pdf" },
            PageUrl = "https://example.com/page",
        });
        Assert.Equal(new[] { "copied-text", "screenshot", "shelf", "page", "pc-file" }, ids);
    }

    [Fact]
    public void ACopiedLinkIsALinkWithAShortPreview()
    {
        var first = PhoneChoices.Build(new PhoneChoiceInputs { Now = Now, CopiedText = "  https://www.github.com/user/repo/  " })[0];
        Assert.Equal(PhoneChoiceKind.CopiedLink, first.Kind);
        Assert.Equal("What you copied", first.Title);
        Assert.Equal("github.com/user/repo", first.Detail);
    }

    [Fact]
    public void CopiedTextPreviewIsOneQuotedLineCutShort()
    {
        var first = PhoneChoices.Build(new PhoneChoiceInputs
        {
            Now = Now,
            CopiedText = "First line of a long note\n\nthat keeps going and going well past the width of a row",
        })[0];
        Assert.Equal(PhoneChoiceKind.CopiedText, first.Kind);
        Assert.StartsWith("“First line of a long note that keeps", first.Detail);
        Assert.EndsWith("…”", first.Detail);
        Assert.True(first.Detail.Length <= PhoneChoices.DetailMax);
    }

    [Fact]
    public void CopiedFilesBeatTextAndImages()
    {
        var first = PhoneChoices.Build(new PhoneChoiceInputs
        {
            Now = Now,
            CopiedFiles = new[] { @"C:\x\report.pdf", @"C:\x\b.png" },
            CopiedText = "ignored",
            CopiedImage = true,
        })[0];
        Assert.Equal(PhoneChoiceKind.CopiedFiles, first.Kind);
        Assert.Equal("report.pdf + 1 more", first.Detail);
    }

    [Fact]
    public void CopiedImageShowsItsSize()
    {
        var first = PhoneChoices.Build(new PhoneChoiceInputs { Now = Now, CopiedImage = true, CopiedImageWidth = 800, CopiedImageHeight = 600 })[0];
        Assert.Equal(PhoneChoiceKind.CopiedImage, first.Kind);
        Assert.Equal("Image · 800×600", first.Detail);
    }

    [Fact]
    public void BlankCopiedTextIsNothing()
    {
        Assert.Equal(new[] { "pc-file" }, Ids(new PhoneChoiceInputs { Now = Now, CopiedText = "  \r\n " }));
    }

    [Fact]
    public void OldScreenshotsAreLeftOut()
    {
        Assert.Equal(new[] { "pc-file" }, Ids(new PhoneChoiceInputs { Now = Now, ScreenshotAt = Now.AddMinutes(-31) }));
        Assert.Equal(new[] { "screenshot", "pc-file" }, Ids(new PhoneChoiceInputs { Now = Now, ScreenshotAt = Now.AddMinutes(-29) }));
    }

    [Fact]
    public void AScreenshotTakenAfterTheCopyComesFirst()
    {
        var ids = Ids(new PhoneChoiceInputs
        {
            Now = Now,
            CopiedText = "hello",
            CopiedAt = Now.AddMinutes(-5),
            ScreenshotAt = Now.AddMinutes(-1),
        });
        Assert.Equal(new[] { "screenshot", "copied-text", "pc-file" }, ids);
    }

    [Fact]
    public void AStaleCopySinksBelowTheShelfAndThePageInFront()
    {
        var ids = Ids(new PhoneChoiceInputs
        {
            Now = Now,
            CopiedText = "hello",
            CopiedAt = Now.AddHours(-2),
            ShelfNames = new[] { "a.pdf" },
            PageUrl = "https://example.com/",
            BrowserInFront = true,
        });
        Assert.Equal(new[] { "page", "shelf", "copied-text", "pc-file" }, ids);
    }

    [Fact]
    public void ThePageInFrontGoesAboveTheShelf()
    {
        var front = new PhoneChoiceInputs { Now = Now, ShelfNames = new[] { "a.pdf" }, PageUrl = "https://example.com/", BrowserInFront = true };
        var behind = new PhoneChoiceInputs { Now = Now, ShelfNames = new[] { "a.pdf" }, PageUrl = "https://example.com/", BrowserInFront = false };
        Assert.Equal(new[] { "page", "shelf", "pc-file" }, Ids(front));
        Assert.Equal(new[] { "shelf", "page", "pc-file" }, Ids(behind));
    }

    [Fact]
    public void ASnipOnTheClipboardIsOfferedOnceAsTheScreenshot()
    {
        var ids = Ids(new PhoneChoiceInputs
        {
            Now = Now,
            CopiedImage = true,
            CopiedImageWidth = 400,
            CopiedImageHeight = 300,
            CopiedAt = Now.AddSeconds(-20),
            ScreenshotAt = Now.AddSeconds(-21),
            ScreenshotFromClipboard = true,
        });
        Assert.Equal(new[] { "screenshot", "pc-file" }, ids);
    }

    [Fact]
    public void AScreenshotCopiedByShammyItselfIsOfferedOnce()
    {
        // "take a screenshot" saves a file and copies it; the history doesn't know the copy's time.
        var ids = Ids(new PhoneChoiceInputs
        {
            Now = Now,
            CopiedImage = true,
            CopiedImageWidth = 1920,
            CopiedImageHeight = 1080,
            ScreenshotAt = Now.AddSeconds(-5),
            ScreenshotWidth = 1920,
            ScreenshotHeight = 1080,
        });
        Assert.Equal(new[] { "screenshot", "pc-file" }, ids);
    }

    [Fact]
    public void ADifferentCopiedImageStaysNextToTheScreenshot()
    {
        var ids = Ids(new PhoneChoiceInputs
        {
            Now = Now,
            CopiedImage = true,
            CopiedImageWidth = 640,
            CopiedImageHeight = 480,
            ScreenshotAt = Now.AddMinutes(-3),
            ScreenshotWidth = 1920,
            ScreenshotHeight = 1080,
        });
        Assert.Equal(new[] { "copied-image", "screenshot", "pc-file" }, ids);
    }

    [Fact]
    public void TheCopiedLinkAndThePageAreNotOfferedTwice()
    {
        var ids = Ids(new PhoneChoiceInputs
        {
            Now = Now,
            CopiedText = "https://www.Example.com/page/",
            PageUrl = "https://example.com/page",
            BrowserInFront = true,
        });
        Assert.Equal(new[] { "copied-link", "pc-file" }, ids);
    }

    [Fact]
    public void ShelfRowNamesTheNewestFileAndCounts()
    {
        var one = PhoneChoices.Build(new PhoneChoiceInputs { Now = Now, ShelfNames = new[] { "a.pdf" } })[0];
        Assert.Equal("The file on the shelf", one.Title);
        Assert.Equal("a.pdf", one.Detail);
        var three = PhoneChoices.Build(new PhoneChoiceInputs { Now = Now, ShelfNames = new[] { "old.pdf", "mid.pdf", "new.pdf" } })[0];
        Assert.Equal("Files on the shelf · 3", three.Title);
        Assert.Equal("new.pdf + 2 more", three.Detail);
    }

    [Theory]
    [InlineData(10, "just now")]
    [InlineData(4 * 60 + 5, "4 min ago")]
    [InlineData(2 * 3600, "2 h ago")]
    public void SaysHowLongAgo(int seconds, string expected) => Assert.Equal(expected, PhoneChoices.Ago(Now.AddSeconds(-seconds), Now));

    [Fact]
    public void DescribeListsKindsAndTitlesOnly()
    {
        var text = PhoneChoices.Describe(PhoneChoices.Build(new PhoneChoiceInputs { Now = Now, CopiedText = "secret plans" }));
        Assert.Equal("1. copied-text | What you copied\n2. pc-file | A file from your PC", text);
        Assert.DoesNotContain("secret", text);
    }
}

public class PhoneTextTests
{
    [Theory]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData(" http://example.com/a?b=1 ", "http://example.com/a?b=1")]
    [InlineData("www.example.com", "https://www.example.com/")]
    [InlineData("example.com/page", "https://example.com/page")]
    [InlineData("<https://example.com/x>", "https://example.com/x")]
    [InlineData("HTTPS://Example.COM/Path", "https://example.com/Path")]
    public void RecognisesLinks(string text, string expected) => Assert.Equal(expected, PhoneText.AsLink(text));

    [Theory]
    [InlineData("example.com")]
    [InlineData("report.pdf")]
    [InlineData("see https://example.com")]
    [InlineData("hello world")]
    [InlineData("ftp://example.com/file")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://localhost:3000/app")]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("")]
    [InlineData(null)]
    public void PlainTextIsNotALink(string? text) => Assert.Null(PhoneText.AsLink(text));

    [Theory]
    [InlineData("github.com/user/repo", "https://github.com/user/repo")]
    [InlineData("https://www.bing.com/search?q=x", "https://www.bing.com/search?q=x")]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("example.com:8080/x", "https://example.com:8080/x")]
    [InlineData("192.168.1.10:8000/page", "http://192.168.1.10:8000/page")]
    [InlineData("docs.microsoft.com/en-us/", "https://docs.microsoft.com/en-us/")]
    public void ReadsAddressBars(string value, string expected) => Assert.Equal(expected, PhoneText.FromAddressBar(value));

    [Theory]
    [InlineData("edge://settings")]
    [InlineData("chrome://newtab/")]
    [InlineData("about:blank")]
    [InlineData("file:///C:/Users/me/a.html")]
    [InlineData("localhost:3000")]
    [InlineData("http://localhost/")]
    [InlineData("127.0.0.1:5000")]
    [InlineData("weather tomorrow")]
    [InlineData("weather")]
    [InlineData("view-source:example.com")]
    [InlineData("")]
    [InlineData(null)]
    public void InternalPagesAndSearchesAreNoPage(string? value) => Assert.Null(PhoneText.FromAddressBar(value));

    [Fact]
    public void ComparesPagesLoosely()
    {
        Assert.True(PhoneText.SameUrl("https://www.example.com/a/", "http://example.com/a"));
        Assert.False(PhoneText.SameUrl("https://example.com/a", "https://example.com/b"));
        Assert.False(PhoneText.SameUrl("https://example.com/a?x=1", "https://example.com/a?x=2"));
    }

    [Fact]
    public void PreviewsLinksWithoutTheNoise()
    {
        Assert.Equal("example.com/a", PhoneText.LinkPreview("https://www.example.com/a/"));
        var cut = PhoneText.LinkPreview("https://example.com/" + new string('x', 100), 20);
        Assert.Equal(20, cut.Length);
        Assert.EndsWith("…", cut);
    }

    [Fact]
    public void RoutesTextByWhatItIs()
    {
        Assert.Equal(PhoneTextRoute.QrLink, PhoneText.Route("https://example.com/page"));
        Assert.Equal(PhoneTextRoute.QrText, PhoneText.Route("Door code 4521, flat 3B"));
        Assert.Equal(PhoneTextRoute.File, PhoneText.Route(new string('a', PhoneText.QrTextMaxBytes + 1)));
        // Hindi takes three bytes a letter, so fewer letters fit in the code.
        Assert.Equal(PhoneTextRoute.File, PhoneText.Route(new string('न', PhoneText.QrTextMaxBytes / 3 + 1)));
        // A very long link is too dense to scan: it goes as a file instead.
        Assert.Equal(PhoneTextRoute.File, PhoneText.Route("https://example.com/?q=" + new string('x', PhoneText.QrLinkMaxBytes)));
    }

    [Fact]
    public void SaysHowLongTheTextIs()
    {
        Assert.Equal("Text · 1 character", PhoneText.TextSize(" a "));
        Assert.Equal("Text · 5 characters", PhoneText.TextSize("hello"));
    }
}

public class PhoneSendAskTests
{
    [Theory]
    [InlineData("send to phone")]
    [InlineData("send this to my phone")]
    [InlineData("Send it to the phone.")]
    [InlineData("share that to my mobile")]
    [InlineData("send something to my phone")]
    public void NamingNothingAsks(string text) => Assert.True(PhoneSendPhrase.AsksWhat(text));

    [Theory]
    [InlineData("send report.pdf to my phone")]
    [InlineData("send the latest file to my phone")]
    [InlineData("send notes to phone")]
    [InlineData("how to send files to phone")]
    [InlineData("")]
    [InlineData(null)]
    public void NamingSomethingDoesNotAsk(string? text) => Assert.False(PhoneSendPhrase.AsksWhat(text));
}
