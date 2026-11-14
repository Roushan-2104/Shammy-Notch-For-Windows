using Shammy.Core.Clipboard;
using Shammy.Core.DropActions;

namespace Shammy.Core.Tests;

public class ClipCardTextTests
{
    static ClipEntry T(string text) => ClipEntry.FromText(text, new DateTime(2026, 11, 3, 10, 0, 0));

    [Theory]
    [InlineData("https://github.com/Roushan/shammy-notch", ClipShape.Link)]
    [InlineData("www.example.com/page", ClipShape.Link)]
    [InlineData("see https://github.com for more", ClipShape.Text)]
    [InlineData("#FF8A3D", ClipShape.Colour)]
    [InlineData("#f80", ClipShape.Colour)]
    [InlineData("rgb(255, 138, 61)", ClipShape.Colour)]
    [InlineData("def wake(word):\n    return word == \"shammy\"", ClipShape.Code)]
    [InlineData("Meeting notes for Thursday\nShip the mockup; review latency", ClipShape.Text)]
    [InlineData("#hashtag", ClipShape.Text)]
    [InlineData("github.com/x", ClipShape.Link)]
    [InlineData("decade", ClipShape.Text)]
    public void Classifies_text(string text, ClipShape shape) => Assert.Equal(shape, ClipCardText.Shape(T(text)));

    [Fact]
    public void Files_and_images_keep_their_kind()
    {
        Assert.Equal(ClipShape.Files, ClipCardText.Shape(ClipEntry.FromFiles(new[] { @"C:\a.txt" }, DateTime.Now)));
        Assert.Equal(ClipShape.Image, ClipCardText.Shape(ClipEntry.FromImage("h", 2, 2, DateTime.Now)));
    }

    [Fact]
    public void Link_label_drops_scheme_and_www()
    {
        Assert.Equal("github.com/Roushan/shammy-notch", ClipCardText.LinkLabel("https://www.github.com/Roushan/shammy-notch/"));
        Assert.Equal("example.com", ClipCardText.LinkLabel("https://example.com/"));
        Assert.Equal("github.com/x", ClipCardText.LinkLabel("github.com/x"));
        Assert.Equal("https://github.com/x", ClipCardText.LinkUrl("github.com/x"));
    }

    [Fact]
    public void Colour_values()
    {
        Assert.Equal(((byte)255, (byte)138, (byte)61), ClipCardText.ColourOf("#FF8A3D"));
        Assert.Equal(((byte)255, (byte)136, (byte)0), ClipCardText.ColourOf("#f80"));
        Assert.Null(ClipCardText.ColourOf("rgb(300, 1, 1)"));
    }

    [Theory]
    [InlineData("#80FF0000")]
    [InlineData("#f80")]
    [InlineData("rgb(25, 195, 178)")]
    public void Colours_read_as_circle_to_search_reads_them(string text)
    {
        // One convention: eight digits are #RRGGBBAA, as CSS writes them.
        var circle = Shammy.Core.Search.CircleSense.Read(text, DateTime.Now)!.Colour!.Value;
        Assert.Equal((circle.R, circle.G, circle.B), ClipCardText.ColourOf(text));
        Assert.Equal(((byte)0x80, (byte)0xFF, (byte)0x00), ClipCardText.ColourOf("#80FF0000"));
    }

    [Fact]
    public void Source_names_and_footer()
    {
        Assert.Equal("Edge", ClipCardText.SourceName("msedge"));
        Assert.Equal("VS Code", ClipCardText.SourceName("Code.exe"));
        Assert.Equal("Myapp", ClipCardText.SourceName("myapp"));
        Assert.Equal("", ClipCardText.SourceName(null));
        var e = T("x");
        e.Source = "Chrome";
        Assert.Equal("Chrome · 2m ago", ClipCardText.Footer(e, e.Time.AddMinutes(2)));
        e.Source = "";
        Assert.Equal("now", ClipCardText.Footer(e, e.Time));
    }

    [Fact]
    public void Re_copy_updates_the_source()
    {
        var h = new ClipHistory();
        var a = T("same");
        a.Source = "Chrome";
        h.Add(a);
        var b = T("same");
        b.Source = "Notion";
        Assert.Equal("Notion", h.Add(b).Source);
        Assert.Single(h.Items);
    }
}

public class DropPlanTests
{
    [Fact]
    public void Zip_names_next_to_the_first_item()
    {
        Assert.Equal(@"C:\docs\Report_final.zip", DropPlan.ZipPath(new[] { @"C:\docs\Report_final.docx" }, _ => false));
        Assert.Equal(@"C:\docs\Archive.zip", DropPlan.ZipPath(new[] { @"C:\docs\a.txt", @"D:\b.txt" }, _ => false));
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\docs\Report_final.zip", @"C:\docs\Report_final (2).zip" };
        Assert.Equal(@"C:\docs\Report_final (3).zip", DropPlan.ZipPath(new[] { @"C:\docs\Report_final.docx" }, taken.Contains));
    }

    [Fact]
    public void Convert_targets()
    {
        Assert.Equal(".jpg", DropPlan.ConvertExtension(@"C:\a\shot.PNG"));
        Assert.Equal(".png", DropPlan.ConvertExtension(@"C:\a\photo.jpeg"));
        Assert.Equal(".jpg", DropPlan.ConvertExtension(@"C:\a\photo.webp"));
        Assert.Null(DropPlan.ConvertExtension(@"C:\a\report.docx"));
        Assert.Null(DropPlan.ConvertExtension(@"C:\a\noext"));
        Assert.Equal(@"C:\a\shot (2).jpg", DropPlan.ConvertPath(@"C:\a\shot.png", ".jpg", p => p == @"C:\a\shot.jpg"));
    }

    [Fact]
    public void Entry_names_are_relative_and_unique()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("a.txt", DropPlan.EntryName(@"C:\x\a.txt", @"C:\x\a.txt", taken));
        Assert.Equal("a (2).txt", DropPlan.EntryName(@"D:\y\a.txt", @"D:\y\a.txt", taken));
        Assert.Equal("folder/sub/b.txt", DropPlan.EntryName(@"C:\x\folder", @"C:\x\folder\sub\b.txt", taken));
    }

    [Fact]
    public void Describe_and_parse()
    {
        Assert.Equal("Report_final.docx · 2.4 MB", DropPlan.Describe(new[] { @"C:\Report_final.docx" }, 2_516_582));
        Assert.Equal("3 items · 12 KB", DropPlan.Describe(new[] { "a", "b", "c" }, 12 * 1024));
        Assert.Equal(DropAction.Compress, DropPlan.ParseAction("zip"));
        Assert.Equal(DropAction.Phone, DropPlan.ParseAction("Phone"));
        Assert.Null(DropPlan.ParseAction("delete"));
    }
}
