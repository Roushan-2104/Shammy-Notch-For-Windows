using Shammy.Core.Record;

namespace Shammy.Core.Tests;

public class RecordNamingTests
{
    static readonly DateTime At = new(2026, 11, 10, 22, 41, 7);

    [Fact]
    public void A_recording_is_named_after_the_moment_it_started() =>
        Assert.Equal("Shammy 2026-11-10 22.41.07.mp4", RecordNaming.FileName(At));

    [Fact]
    public void Single_digit_times_keep_their_zeros() =>
        Assert.Equal("Shammy 2026-01-02 03.04.05.mp4", RecordNaming.FileName(new DateTime(2026, 1, 2, 3, 4, 5)));

    [Fact]
    public void Recordings_go_in_a_Shammy_folder_inside_Videos() =>
        Assert.Equal(Path.Combine("C:\\Users\\me\\Videos", "Shammy"), RecordNaming.Folder("C:\\Users\\me\\Videos"));

    [Fact]
    public void A_free_name_is_used_as_it_is() =>
        Assert.Equal(Path.Combine("V", "Shammy 2026-11-10 22.41.07.mp4"), RecordNaming.FreePath("V", At, _ => false));

    [Fact]
    public void A_taken_name_gets_a_number_instead_of_being_overwritten()
    {
        var taken = new HashSet<string>
        {
            Path.Combine("V", "Shammy 2026-11-10 22.41.07.mp4"),
            Path.Combine("V", "Shammy 2026-11-10 22.41.07 (2).mp4"),
        };
        Assert.Equal(Path.Combine("V", "Shammy 2026-11-10 22.41.07 (3).mp4"), RecordNaming.FreePath("V", At, taken.Contains));
    }
}

public class RecordMathsTests
{
    [Theory]
    [InlineData(1280, 720, 3_500_000)]
    [InlineData(1920, 1080, 8_000_000)]
    [InlineData(2560, 1440, 14_500_000)]
    [InlineData(3840, 2160, 32_500_000)]
    public void Bitrate_grows_with_the_picture(int w, int h, int bits) => Assert.Equal(bits, RecordMaths.Bitrate(w, h));

    [Fact]
    public void A_tiny_window_still_gets_two_megabits() => Assert.Equal(RecordMaths.MinBitrate, RecordMaths.Bitrate(320, 240));

    [Fact]
    public void A_huge_screen_never_goes_over_forty_megabits() => Assert.Equal(RecordMaths.MaxBitrate, RecordMaths.Bitrate(7680, 4320));

    [Fact]
    public void Bitrate_is_always_a_whole_half_megabit()
    {
        foreach (var (w, h) in new[] { (1366, 768), (1600, 900), (1920, 1200), (3440, 1440), (2880, 1800) })
            Assert.Equal(0, RecordMaths.Bitrate(w, h) % 500_000);
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1367, 769, 1366, 768)]
    [InlineData(801, 600, 800, 600)]
    [InlineData(1, 1, 2, 2)]
    public void The_video_keeps_the_screen_size_with_even_sides(int w, int h, int outW, int outH) =>
        Assert.Equal((outW, outH), RecordMaths.OutputSize(w, h));

    [Fact]
    public void A_screen_too_big_for_the_encoder_is_scaled_to_fit_in_the_same_shape()
    {
        var (w, h) = RecordMaths.OutputSize(5120, 2880);
        Assert.Equal((4096, 2304), (w, h));
        var wide = RecordMaths.OutputSize(5120, 1440);
        Assert.Equal((4096, 1152), wide);
        var tall = RecordMaths.OutputSize(2160, 3840);
        Assert.Equal((1296, 2304), tall);
    }

    [Fact]
    public void Frames_are_a_thirtieth_of_a_second_apart_without_drifting()
    {
        Assert.Equal(TimeSpan.Zero, RecordMaths.FrameTime(0));
        Assert.Equal(TimeSpan.FromSeconds(1), RecordMaths.FrameTime(30));
        Assert.Equal(TimeSpan.FromMinutes(60), RecordMaths.FrameTime(30 * 3600));
        Assert.Equal(333_333, RecordMaths.FrameTime(1).Ticks);
    }

    [Fact]
    public void An_hour_at_full_hd_with_sound_is_about_three_and_a_half_gigabytes()
    {
        long hour = RecordMaths.BytesPerMinute(1920, 1080, sound: true) * 60;
        Assert.InRange(hour, 3_600_000_000L, 3_700_000_000L);
    }

    [Fact]
    public void A_dragged_area_is_kept_on_the_screen_with_even_sides()
    {
        Assert.Equal((100, 50, 300, 200), RecordMaths.Area(100, 50, 301, 201, 1920, 1080));
        // Dragged up and to the left, and off the edge.
        Assert.Equal((0, 0, 400, 300), RecordMaths.Area(400, 300, -500, -400, 1920, 1080));
        Assert.Equal((1800, 1000, 120, 80), RecordMaths.Area(1800, 1000, 500, 500, 1920, 1080));
    }

    [Fact]
    public void A_drag_too_small_to_mean_anything_is_no_area()
    {
        Assert.Null(RecordMaths.Area(10, 10, 20, 300, 1920, 1080));
        Assert.Null(RecordMaths.Area(10, 10, 0, 0, 1920, 1080));
    }
}

public class RecordLimitsTests
{
    const long Plenty = 50L * 1024 * 1024 * 1024;

    [Fact]
    public void A_recording_with_room_and_time_keeps_going() =>
        Assert.Null(RecordLimits.Check(TimeSpan.FromMinutes(59.9), Plenty));

    [Fact]
    public void It_stops_by_itself_at_sixty_minutes() =>
        Assert.Equal(RecordStop.TimeLimit, RecordLimits.Check(TimeSpan.FromMinutes(60), Plenty));

    [Fact]
    public void It_stops_when_less_than_500_megabytes_are_free()
    {
        Assert.Equal(RecordStop.DiskLow, RecordLimits.Check(TimeSpan.FromMinutes(1), RecordLimits.MinFreeBytes - 1));
        Assert.Null(RecordLimits.Check(TimeSpan.FromMinutes(1), RecordLimits.MinFreeBytes));
    }

    [Fact]
    public void Free_space_that_cannot_be_read_never_stops_a_recording() =>
        Assert.Null(RecordLimits.Check(TimeSpan.FromMinutes(1), null));

    [Fact]
    public void A_recording_asked_to_run_for_a_while_stops_then()
    {
        Assert.Null(RecordLimits.Check(TimeSpan.FromSeconds(3.9), Plenty, TimeSpan.FromSeconds(4)));
        Assert.Equal(RecordStop.Timed, RecordLimits.Check(TimeSpan.FromSeconds(4), Plenty, TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void A_full_disk_matters_more_than_the_clock() =>
        Assert.Equal(RecordStop.DiskLow, RecordLimits.Check(TimeSpan.FromMinutes(61), 1, TimeSpan.FromSeconds(4)));

    [Fact]
    public void Nothing_starts_on_a_nearly_full_disk()
    {
        Assert.False(RecordLimits.CanStart(100L * 1024 * 1024));
        Assert.True(RecordLimits.CanStart(RecordLimits.MinFreeBytes));
        Assert.True(RecordLimits.CanStart(null));
    }
}

public class RecordPhraseTests
{
    [Theory]
    [InlineData("record screen")]
    [InlineData("record my screen")]
    [InlineData("Record my screen.")]
    [InlineData("record the screen")]
    [InlineData("screen recording")]
    [InlineData("screen record")]
    [InlineData("start recording")]
    [InlineData("Start recording!")]
    [InlineData("start a screen recording")]
    [InlineData("start screen recording")]
    [InlineData("start recording my screen")]
    [InlineData("record a video of my screen")]
    [InlineData("please record my screen")]
    [InlineData("hey shammy, record my screen")]
    [InlineData("record   my   screen   now")]
    public void Asking_to_record_opens_the_start_card(string text) => Assert.Equal(RecordIntent.Open, RecordPhrases.Parse(text));

    [Theory]
    [InlineData("stop recording")]
    [InlineData("Stop recording.")]
    [InlineData("stop the recording")]
    [InlineData("stop screen recording")]
    [InlineData("stop recording my screen")]
    [InlineData("end recording")]
    [InlineData("finish the recording")]
    [InlineData("stop recording please")]
    public void Asking_to_stop_stops_it(string text) => Assert.Equal(RecordIntent.Stop, RecordPhrases.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("record")]
    [InlineData("recording")]
    [InlineData("take a screenshot")]
    [InlineData("screenshot")]
    [InlineData("screen recording software")]
    [InlineData("best screen recorder for windows")]
    [InlineData("how to record my screen")]
    [InlineData("record player")]
    [InlineData("world record 100m")]
    [InlineData("start recording studio near me")]
    [InlineData("stop recording my calls")]
    [InlineData("stop")]
    [InlineData("stop music")]
    [InlineData("record a voice note")]
    public void Anything_else_is_left_to_search_and_other_commands(string text) => Assert.Null(RecordPhrases.Parse(text));
}

public class RecordTextTests
{
    [Fact]
    public void The_sound_line_reads_as_in_the_design() => Assert.Equal("PC sound on · Mic off", RecordText.Sound(true, false));

    [Theory]
    [InlineData(true, true, "PC sound on · Mic on")]
    [InlineData(false, false, "PC sound off · Mic off")]
    [InlineData(false, true, "PC sound off · Mic on")]
    public void Each_half_of_the_sound_line_says_its_own_state(bool pc, bool mic, string line)
    {
        Assert.Equal(line, RecordText.Sound(pc, mic));
        Assert.Equal(line, RecordText.PcSound(pc) + " · " + RecordText.Mic(mic));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(42.9, "0:42")]
    [InlineData(725, "12:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    public void Elapsed_time_counts_up_like_a_stopwatch(double seconds, string text) =>
        Assert.Equal(text, RecordText.Elapsed(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Elapsed_time_always_fits_beside_the_pet() =>
        Assert.True(RecordText.Elapsed(RecordLimits.MaxLength).Length <= 9);

    [Theory]
    [InlineData(4.2, "4 sec")]
    [InlineData(59.4, "59 sec")]
    [InlineData(185, "3 min 05 sec")]
    [InlineData(3720, "1 h 02 min")]
    public void A_saved_video_says_how_long_it_is(double seconds, string text) =>
        Assert.Equal(text, RecordText.Length(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(900L, "900 B")]
    [InlineData(655_360L, "640 KB")]
    [InlineData(8_808_038L, "8.4 MB")]
    [InlineData(1_288_490_189L, "1.2 GB")]
    public void A_saved_video_says_how_big_it_is(long bytes, string text) => Assert.Equal(text, RecordText.Size(bytes));

    [Fact]
    public void The_card_shows_length_then_size() =>
        Assert.Equal("42 sec · 8.4 MB", RecordText.Details(TimeSpan.FromSeconds(42), 8_808_038));

    [Fact]
    public void A_recording_that_stopped_by_itself_says_why()
    {
        Assert.Contains("60 minutes", RecordText.StoppedBecause(RecordStop.TimeLimit));
        Assert.Contains("disk", RecordText.StoppedBecause(RecordStop.DiskLow));
        Assert.Contains("went away", RecordText.StoppedBecause(RecordStop.SourceClosed));
        Assert.Null(RecordText.StoppedBecause(RecordStop.User));
        Assert.Null(RecordText.StoppedBecause(RecordStop.Timed));
    }

    [Fact]
    public void Every_failure_has_plain_words()
    {
        foreach (var code in new[]
                 {
                     RecordFailure.NotSupported, RecordFailure.DiskLow, RecordFailure.NoFolder, RecordFailure.NoFile, RecordFailure.NoGraphics,
                     RecordFailure.NoSource, RecordFailure.Encoder, RecordFailure.Busy, RecordFailure.Unknown, null,
                 })
        {
            foreach (var words in new[] { RecordText.Failure(code), RecordText.FailureTitle(code) })
            {
                Assert.False(string.IsNullOrWhiteSpace(words));
                Assert.DoesNotContain("Exception", words);
                Assert.DoesNotContain("0x", words);
            }
        }
    }

    [Fact]
    public void A_failure_before_it_began_and_one_while_saving_have_different_titles()
    {
        Assert.Equal("Couldn’t start recording", RecordText.FailureTitle(RecordFailure.DiskLow));
        Assert.Equal("The recording wasn’t saved", RecordText.FailureTitle(RecordFailure.Encoder));
        Assert.Contains("removed", RecordText.Failure(RecordFailure.Encoder));
    }
}

public class RecordPipeTests
{
    [Fact]
    public void A_bare_start_uses_the_defaults_sound_on_and_mic_off() =>
        Assert.Equal(new RecordPipeStart(true, false, null), RecordPipe.ParseStart("screen", out _));

    [Fact]
    public void Every_option_can_be_set() =>
        Assert.Equal(new RecordPipeStart(false, true, 4), RecordPipe.ParseStart("screen sound=off mic=on seconds=4", out _));

    [Fact]
    public void An_area_is_left_top_width_height() =>
        Assert.Equal(new RecordPipeStart(true, false, 5, (200, 100, 800, 600)), RecordPipe.ParseStart("screen seconds=5 area=200,100,800,600", out _));

    [Theory]
    [InlineData("")]
    [InlineData("window")]
    [InlineData("screen area=1,2,3")]
    [InlineData("screen area=0,0,0,600")]
    [InlineData("screen sound=loud")]
    [InlineData("screen seconds=0")]
    [InlineData("screen seconds=abc")]
    [InlineData("screen extra")]
    public void A_line_it_cannot_read_is_refused_with_the_usage(string arg)
    {
        Assert.Null(RecordPipe.ParseStart(arg, out var error));
        Assert.Contains("record-start screen", error);
    }
}

public class AudioTimelineTests
{
    const int Rate = 48000;

    static float[] Burst(int frames, float value) => Enumerable.Repeat(value, frames * 2).ToArray();

    static float[] Read(AudioTimeline t, long position, int frames, out int had)
    {
        var mix = new float[frames * 2];
        had = t.Read(position, mix);
        return mix;
    }

    [Fact]
    public void Sound_lands_where_the_clock_says_it_was_heard()
    {
        var t = new AudioTimeline(Rate, 2);
        // 480 frames that finished arriving at one second in: they cover 47520..48000.
        t.Write(Burst(480, 0.5f), 48000);
        var before = Read(t, 47040, 480, out _);
        Assert.All(before, v => Assert.Equal(0f, v));
        var at = Read(t, 47520, 480, out int had);
        Assert.Equal(480, had);
        Assert.All(at, v => Assert.Equal(0.5f, v));
    }

    [Fact]
    public void Bursts_that_follow_each_other_join_without_a_gap()
    {
        var t = new AudioTimeline(Rate, 2);
        t.Write(Burst(480, 0.1f), 480);
        // The second arrives a little late (jitter under the gap): it continues where the first ended.
        t.Write(Burst(480, 0.2f), 1000);
        Assert.Equal(960, t.End);
        var mix = Read(t, 0, 960, out int had);
        Assert.Equal(960, had);
        Assert.Equal(0.1f, mix[0]);
        Assert.Equal(0.2f, mix[480 * 2]);
    }

    [Fact]
    public void Quiet_between_two_sounds_stays_quiet_and_the_second_stays_on_time()
    {
        var t = new AudioTimeline(Rate, 2);
        t.Write(Burst(480, 0.3f), 480);
        // Nothing played for a second; then sound again.
        t.Write(Burst(480, 0.6f), 48480);
        var gap = Read(t, 480, 47520, out int hadGap);
        Assert.Equal(47520, hadGap);
        Assert.All(gap, v => Assert.Equal(0f, v));
        var second = Read(t, 48000, 480, out _);
        Assert.All(second, v => Assert.Equal(0.6f, v));
    }

    [Fact]
    public void A_source_that_ran_ahead_of_the_clock_is_pulled_back()
    {
        var t = new AudioTimeline(Rate, 2, gapSeconds: 0.05);
        t.Write(Burst(4800, 0.1f), 4800);
        // The clock says this burst ended at 6000, but the source would put it at 4800..9600: too far ahead.
        t.Write(Burst(4800, 0.2f), 6000);
        Assert.Equal(6000, t.End);
    }

    [Fact]
    public void Sound_from_before_the_recording_started_is_dropped()
    {
        var t = new AudioTimeline(Rate, 2);
        // 480 frames ending 100 frames in: 380 of them are from before the start.
        var burst = new float[480 * 2];
        for (int f = 0; f < 480; f++) burst[f * 2] = burst[f * 2 + 1] = f;
        t.Write(burst, 100);
        var mix = Read(t, 0, 100, out int had);
        Assert.Equal(100, had);
        Assert.Equal(380f, mix[0]);
        Assert.Equal(479f, mix[99 * 2]);
    }

    [Fact]
    public void Two_sources_add_up_in_the_mix()
    {
        var pc = new AudioTimeline(Rate, 2);
        var mic = new AudioTimeline(Rate, 2);
        pc.Write(Burst(480, 0.25f), 480);
        mic.Write(Burst(480, 0.5f), 480);
        var mix = new float[480 * 2];
        pc.Read(0, mix);
        mic.Read(0, mix);
        Assert.All(mix, v => Assert.Equal(0.75f, v));
    }

    [Fact]
    public void What_has_been_read_is_forgotten()
    {
        var t = new AudioTimeline(Rate, 2);
        t.Write(Burst(960, 0.4f), 960);
        Read(t, 0, 480, out _);
        Assert.Equal(480, t.Buffered);
        // Reading the same stretch again finds nothing.
        Read(t, 0, 480, out int again);
        Assert.Equal(0, again);
    }

    [Fact]
    public void A_reader_that_fell_behind_loses_the_oldest_sound_not_the_newest()
    {
        var t = new AudioTimeline(Rate, 2, bufferSeconds: 1);
        for (int i = 1; i <= 300; i++) t.Write(Burst(480, i), i * 480L);
        Assert.Equal(Rate, t.Buffered);
        var newest = Read(t, 300 * 480 - 480, 480, out int had);
        Assert.Equal(480, had);
        Assert.All(newest, v => Assert.Equal(300f, v));
    }

    [Fact]
    public void Mixed_sound_is_clipped_not_wrapped_when_it_is_too_loud()
    {
        var pcm = new byte[8];
        PcmMix.ToInt16(new[] { 0f, 1.7f, -1.7f, 0.5f }, pcm);
        Assert.Equal(0, BitConverter.ToInt16(pcm, 0));
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(pcm, 2));
        Assert.Equal(-short.MaxValue, BitConverter.ToInt16(pcm, 4));
        Assert.InRange(BitConverter.ToInt16(pcm, 6), (short)16383, (short)16384);
    }

    [Fact]
    public void A_mono_microphone_is_heard_on_both_sides()
    {
        var stereo = new float[6];
        Assert.Equal(3, PcmMix.ToStereo(new[] { 0.1f, 0.2f, 0.3f }, 1, stereo));
        Assert.Equal(new[] { 0.1f, 0.1f, 0.2f, 0.2f, 0.3f, 0.3f }, stereo);
    }

    [Fact]
    public void Surround_sound_keeps_its_front_left_and_right()
    {
        var stereo = new float[4];
        Assert.Equal(2, PcmMix.ToStereo(new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 11f, 12f }, 6, stereo));
        Assert.Equal(new[] { 1f, 2f, 7f, 8f }, stereo);
    }
}
