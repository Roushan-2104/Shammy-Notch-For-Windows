using Shammy.Core.Agents;

namespace Shammy.Core.Tests;

public class ClaudeAuthTests
{
    [Theory]
    [InlineData("{\"loggedIn\": false, \"authMethod\": \"none\"}", false)]
    [InlineData("{\"loggedIn\": true, \"authMethod\": \"claude.ai\"}", true)]
    [InlineData("Warning: something\n{\"loggedIn\": true}", true)]
    [InlineData("{\"authMethod\": \"none\"}", null)]
    [InlineData("not json", null)]
    [InlineData("", null)]
    public void Status_output_reads_as_signed_in_out_or_unknown(string output, bool? expected) =>
        Assert.Equal(expected, ClaudeAuth.SignedIn(output));

    [Fact]
    public void The_native_install_comes_first_then_npm_then_the_PATH()
    {
        var list = ClaudeAuth.Candidates(@"C:\Users\me", @"C:\Users\me\AppData\Roaming", @"C:\tools;""C:\other bin"";relative;;C:\tools");
        Assert.Equal(@"C:\Users\me\.local\bin\claude.exe", list[0]);
        Assert.Equal(@"C:\Users\me\AppData\Roaming\npm\claude.cmd", list[1]);
        Assert.Contains(@"C:\tools\claude.exe", list);
        Assert.Contains(@"C:\other bin\claude.cmd", list);
        // Relative folders and repeats are skipped.
        Assert.DoesNotContain(list, p => p.StartsWith("relative", StringComparison.Ordinal));
        Assert.Equal(list.Count, list.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
