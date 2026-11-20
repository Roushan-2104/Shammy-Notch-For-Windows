using System.Buffers.Binary;
using System.Text;
using Shammy.Core.DropActions;
using Xunit;

namespace Shammy.Core.Tests;

/// <summary>Browser drags onto the notch (22 Nov): which kind a drag is, its names, and the shell's file descriptor.</summary>
public class DragPlanTests
{
    /// <summary>A FILEGROUPDESCRIPTORW as Chrome writes it: (name, size or null, folder?) per item.</summary>
    static byte[] Descriptor(params (string Name, long? Size, bool Folder)[] items)
    {
        var b = new byte[4 + items.Length * 592];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            var d = b.AsSpan(4 + i * 592, 592);
            uint flags = 0x04 | (items[i].Size is null ? 0u : 0x40u);
            BinaryPrimitives.WriteUInt32LittleEndian(d, flags);
            BinaryPrimitives.WriteUInt32LittleEndian(d[36..], items[i].Folder ? 0x10u : 0x80u);
            if (items[i].Size is { } size)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(d[64..], (uint)(size >> 32));
                BinaryPrimitives.WriteUInt32LittleEndian(d[68..], (uint)size);
            }
            Encoding.Unicode.GetBytes(items[i].Name).CopyTo(d[72..]);
        }
        return b;
    }

    [Fact]
    public void TheDescriptorGivesNamesSizesAndIndexesWithoutFolders()
    {
        var files = DragPlan.ParseDescriptor(Descriptor(("cat.jpg", 12345, false), ("album", null, true), ("big.bin", 5L << 32, false), ("nosize.png", null, false)));
        Assert.Equal(3, files.Count);
        Assert.Equal((0, new VirtualFile("cat.jpg", 12345)), files[0]);
        Assert.Equal((2, new VirtualFile("big.bin", 5L << 32)), files[1]);
        Assert.Equal((3, new VirtualFile("nosize.png", null)), files[2]);
    }

    [Fact]
    public void ShortOrOddDescriptorsGiveNothing()
    {
        Assert.Empty(DragPlan.ParseDescriptor([]));
        Assert.Empty(DragPlan.ParseDescriptor(Descriptor(("a.txt", 1, false))[..300]));
        var huge = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(huge, 5000);
        Assert.Empty(DragPlan.ParseDescriptor(huge));
    }

    [Fact]
    public void LinksComeFromTheUrlFormatOrTextThatIsOneAddress()
    {
        Assert.Equal("https://example.com/a", DragPlan.LinkFrom("https://example.com/a\r\nExample", null));
        Assert.Equal("https://example.com/", DragPlan.LinkFrom(null, " https://example.com "));
        Assert.Null(DragPlan.LinkFrom(null, "see https://example.com today"));
        Assert.Null(DragPlan.LinkFrom("javascript:alert(1)", "javascript:alert(1)"));
        Assert.Null(DragPlan.LinkFrom("file:///C:/x.txt", null));
        Assert.Null(DragPlan.LinkFrom(null, null));
    }

    [Fact]
    public void FilesThenHandedOverFilesThenALinkThenText()
    {
        var image = new List<VirtualFile> { new("cat.jpg", 10) };
        var shortcut = new List<VirtualFile> { new("Example Domain.url", 80) };
        Assert.Equal(DragKind.Files, DragPlan.Choose(true, image, "https://e.com/", "x"));
        // an image from a page also carries its address: it's still the image
        Assert.Equal(DragKind.VirtualFiles, DragPlan.Choose(false, image, "https://e.com/cat.jpg", null));
        // a dragged link: the browser's own .url stands for the link
        Assert.Equal(DragKind.Link, DragPlan.Choose(false, shortcut, "https://example.com/", null));
        Assert.Equal(DragKind.VirtualFiles, DragPlan.Choose(false, shortcut, null, null));
        Assert.Equal(DragKind.Link, DragPlan.Choose(false, [], "https://example.com/", "Example"));
        Assert.Equal(DragKind.Text, DragPlan.Choose(false, [], null, "some words"));
        Assert.Equal(DragKind.None, DragPlan.Choose(false, [], null, "   "));
    }

    [Fact]
    public void LinksAreNamedFromTheTitleElseTheHost()
    {
        Assert.Equal("Example Domain.url", DragPlan.LinkName("https://example.com/", [new("Example Domain.url", 80)]));
        Assert.Equal("github.com.url", DragPlan.LinkName("https://www.github.com/x", []));
        Assert.Equal("a_b.url", DragPlan.LinkName("https://e.com/", [new("a:b.url", 1)]));
        Assert.StartsWith("[InternetShortcut]\r\nURL=https://example.com/\r\n", DragPlan.Shortcut("https://example.com/"));
    }

    [Fact]
    public void NotesAreNamedFromTheirFirstWords()
    {
        Assert.Equal("Meeting moved to Friday at.txt", DragPlan.NoteName("Meeting moved to Friday at 4, room 2"));
        Assert.Equal("a_b c.txt", DragPlan.NoteName("  a:b\n c  "));
        Assert.Equal(44, DragPlan.NoteName(new string('x', 100)).Length);
        Assert.Equal("Note.txt", DragPlan.NoteName("..."));
        Assert.Equal("_CON.txt", DragPlan.NoteName("CON"));
    }
}
