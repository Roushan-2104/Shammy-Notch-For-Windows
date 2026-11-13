using Shammy.Core.Captions;
using Shammy.Core.Search;

namespace Shammy.Core.Tests;

public class ModelCleanupTests
{
    [Fact]
    public void Every_model_but_base_goes_including_half_finished_downloads()
    {
        var names = new[]
        {
            "ggml-base.bin", "ggml-small.bin", "ggml-large-v3-turbo-q5_0.bin", "ggml-apex-hinglish-q5_0.bin.part",
            "ggml-apex-hinglish-q5_0.bin", "verified.txt",
        };
        Assert.Equal(new[] { "ggml-small.bin", "ggml-large-v3-turbo-q5_0.bin", "ggml-apex-hinglish-q5_0.bin.part", "ggml-apex-hinglish-q5_0.bin" },
            ModelCleanup.ToRecycle(names));
    }

    [Fact]
    public void The_kept_model_and_its_own_resumable_download_stay_whatever_the_case()
    {
        Assert.Empty(ModelCleanup.ToRecycle(new[] { "GGML-BASE.BIN", "ggml-base.bin.part" }));
    }

    [Fact]
    public void Files_that_are_not_models_and_anything_like_a_path_are_left_alone()
    {
        Assert.Empty(ModelCleanup.ToRecycle(new[] { "verified.txt", "notes.md", "..\\other.bin", "C:\\x.bin", "sub/y.bin", "", " " }));
    }

    [Fact]
    public void The_log_line_is_counts_and_size_only()
    {
        Assert.Equal("nothing", ModelCleanup.Describe(0, 0));
        Assert.Equal("1 file, 149 MB", ModelCleanup.Describe(1, 149L << 20));
        Assert.Equal("3 files, 1.2 GB", ModelCleanup.Describe(3, (long)(1.2 * (1L << 30))));
    }
}

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
