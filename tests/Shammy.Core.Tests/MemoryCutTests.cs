using Shammy.Core.Captions;

namespace Shammy.Core.Tests;

public class LiveCaptionsTextTests
{
    [Fact]
    public void Live_captions_need_windows_11_22h2()
    {
        Assert.False(LiveCaptionsText.Supported(19045));
        Assert.False(LiveCaptionsText.Supported(22000));
        Assert.True(LiveCaptionsText.Supported(22621));
        Assert.True(LiveCaptionsText.Supported(26100));
    }

    [Fact]
    public void The_tail_is_the_last_words_on_one_line()
    {
        Assert.Equal("four five six", LiveCaptionsText.Tail("one two three\nfour  five six ", 3));
        Assert.Equal("one two three", LiveCaptionsText.Tail("  one\r\ntwo three", 10));
        Assert.Equal("", LiveCaptionsText.Tail(null));
        Assert.Equal("", LiveCaptionsText.Tail("   \n "));
    }

    [Fact]
    public void The_latest_is_the_last_two_lines()
    {
        Assert.Equal("Second line. Third line is here.", LiveCaptionsText.LastLines("First line.\nSecond line.\r\nThird line is here."));
        Assert.Equal("Only one.", LiveCaptionsText.LastLines("Only one."));
        Assert.Equal("", LiveCaptionsText.LastLines(""));
    }

    [Fact]
    public void A_growing_transcript_merges_without_repeating_words()
    {
        var merger = new CaptionMerger();
        var transcript = "so the plan for today is to look at the second approach";
        merger.Merge(LiveCaptionsText.Tail(transcript, 40));
        transcript += " and then we compare it with the first one";
        merger.Merge(LiveCaptionsText.Tail(transcript, 40));
        Assert.Equal(transcript, merger.Text);
    }
}
