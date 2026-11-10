using Shammy.Core.Prompter;

namespace Shammy.Core.Tests;

public class PrompterTests
{
    [Theory]
    [InlineData("teleprompter")]
    [InlineData("Prompter")]
    [InlineData("open the teleprompter")]
    [InlineData("show my prompter.")]
    public void Plain_phrases_open_it(string text) =>
        Assert.Equal(PrompterCommandKind.Open, PrompterText.Parse(text)?.Kind);

    [Theory]
    [InlineData("teleprompter from clipboard")]
    [InlineData("prompter from my clipboard")]
    [InlineData("prompter paste")]
    public void The_clipboard_can_be_the_script(string text) =>
        Assert.Equal(PrompterCommandKind.FromClipboard, PrompterText.Parse(text)?.Kind);

    [Theory]
    [InlineData("close teleprompter")]
    [InlineData("stop the prompter")]
    [InlineData("teleprompter off")]
    public void It_can_be_closed_by_phrase(string text) =>
        Assert.Equal(PrompterCommandKind.Close, PrompterText.Parse(text)?.Kind);

    [Theory]
    [InlineData(@"teleprompter C:\Users\me\script.txt", @"C:\Users\me\script.txt")]
    [InlineData("prompter \"C:\\My Videos\\intro script.md\"", @"C:\My Videos\intro script.md")]
    [InlineData(@"teleprompter \\nas\share\talk.txt", @"\\nas\share\talk.txt")]
    public void A_path_to_a_text_file_loads_it(string text, string path)
    {
        var command = PrompterText.Parse(text);
        Assert.Equal(PrompterCommandKind.FromFile, command?.Kind);
        Assert.Equal(path, command?.Path);
    }

    [Theory]
    [InlineData("teleprompter app for mac")]
    [InlineData("best teleprompter")]
    [InlineData("teleprompter script.txt")]
    [InlineData(@"teleprompter C:\Users\me\movie.mp4")]
    [InlineData("how does a teleprompter work")]
    [InlineData("prompter engineering jobs")]
    [InlineData("")]
    public void Anything_else_stays_a_search(string text) => Assert.Null(PrompterText.Parse(text));

    [Fact]
    public void A_script_is_tidied()
    {
        var (text, cut) = PrompterText.Clean("  Hello there.  \r\n\r\n\r\n\r\nSecond\tpart.\r\n");
        Assert.Equal("Hello there.\n\nSecond part.", text);
        Assert.False(cut);
    }

    [Fact]
    public void A_very_long_script_is_cut_at_a_word()
    {
        var raw = string.Join(' ', Enumerable.Repeat("teleprompter", 2500));
        var (text, cut) = PrompterText.Clean(raw);
        Assert.True(cut);
        Assert.InRange(text.Length, PrompterText.MaxLength - 20, PrompterText.MaxLength);
        Assert.EndsWith("teleprompter", text);
    }

    [Fact]
    public void Nothing_is_an_empty_script() => Assert.Equal(("", false), PrompterText.Clean(null));

    [Theory]
    [InlineData("one two  three\nfour", 4)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void Words_are_counted(string? text, int words) => Assert.Equal(words, PrompterText.Words(text));

    [Fact]
    public void Faster_paces_finish_sooner()
    {
        Assert.Equal(84, PrompterText.Seconds(140, PrompterPace.Slow));
        Assert.Equal(60, PrompterText.Seconds(140, PrompterPace.Normal));
        Assert.True(PrompterText.Seconds(140, PrompterPace.Fast) < 60);
        Assert.Equal(2, PrompterText.Seconds(1, PrompterPace.Fast));
    }

    [Fact]
    public void The_scroll_speed_covers_the_distance_in_the_reading_time()
    {
        Assert.Equal(10, PrompterText.PixelsPerSecond(600, 140, PrompterPace.Normal), 3);
        Assert.Equal(0, PrompterText.PixelsPerSecond(0, 140, PrompterPace.Normal));
    }

    [Theory]
    [InlineData(70, PrompterPace.Normal, "30 s")]
    [InlineData(1400, PrompterPace.Normal, "10 min")]
    public void The_length_reads_naturally(int words, PrompterPace pace, string expected) =>
        Assert.Equal(expected, PrompterText.Length(words, pace));

    [Fact]
    public void The_example_script_is_itself_a_valid_script()
    {
        var (text, cut) = PrompterText.Clean(PrompterText.Example);
        Assert.False(cut);
        Assert.True(PrompterText.Words(text) > 20);
    }
}
