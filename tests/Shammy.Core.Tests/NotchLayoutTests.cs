using Shammy.Core.Notch;

namespace Shammy.Core.Tests;

public class NotchLayoutTests
{
    [Theory]
    [InlineData(@"C:\a\Report_final.docx", false, "Word")]
    [InlineData(@"C:\a\Screenshot 10.21.PNG", false, "PNG image")]
    [InlineData(@"C:\a\Invoice.pdf", false, "PDF")]
    [InlineData(@"C:\a\thing.xyz", false, "XYZ file")]
    [InlineData(@"C:\a\Makefile", false, "File")]
    [InlineData(@"C:\a\Photos", true, "Folder")]
    public void File_kinds_read_naturally(string path, bool folder, string expected) =>
        Assert.Equal(expected, FileKinds.Describe(path, folder));
}
