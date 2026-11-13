using Shammy.Core.Prompter;
using Shammy.Core.Stage;

namespace Shammy.Core.Tests;

public class StageLinesTests
{
    const string Script =
        "Good morning everyone, and thank you for joining this call today. I want to walk you through three things " +
        "we learnt this quarter and what we plan to do next.\n\nFirst, the numbers.";

    [Fact]
    public void Every_line_fits_and_no_word_is_lost()
    {
        var lines = StageLines.Wrap(Script, 46);
        Assert.All(lines, l => Assert.InRange(l.Length, 1, 46));
        Assert.Equal(PrompterText.Words(Script), lines.Sum(StageLines.Words));
        Assert.Equal(string.Join(' ', Script.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries)), string.Join(' ', lines));
    }

    [Fact]
    public void Lines_are_filled_close_to_the_width()
    {
        var lines = StageLines.Wrap(Script, 46);
        // All but each paragraph's last line are well filled.
        Assert.True(lines[0].Length >= 36, lines[0]);
        Assert.True(lines[1].Length >= 36, lines[1]);
    }

    [Fact]
    public void Paragraphs_start_a_new_line_and_blank_lines_go()
    {
        var lines = StageLines.Wrap("One two three.\n\n\nFour five.", 46);
        Assert.Equal(new[] { "One two three.", "Four five." }, lines);
    }

    [Fact]
    public void A_last_word_on_its_own_is_given_company()
    {
        // Greedy wrapping would leave "end." alone on the last line.
        var text = "aaaa bbbb cccc dddd eeee ffff gggg hhhh iiii end.";
        var lines = StageLines.Wrap(text, 45);
        Assert.Equal(2, lines.Count);
        Assert.True(lines[1].Length > 10, lines[1]);
        Assert.Equal(text, string.Join(' ', lines));
    }

    [Fact]
    public void A_word_longer_than_a_line_is_split()
    {
        var url = new string('x', 50);
        var lines = StageLines.Wrap("see " + url, 20);
        Assert.All(lines, l => Assert.True(l.Length <= 20));
        Assert.Equal(url, string.Concat(lines.Skip(1)).Replace(" ", ""));
    }

    [Fact]
    public void Nothing_is_no_lines()
    {
        Assert.Empty(StageLines.Wrap(null));
        Assert.Empty(StageLines.Wrap("  \n\n \t "));
    }

    [Theory]
    [InlineData(560, 11.5, 48)]
    [InlineData(400, 11.5, 34)]
    [InlineData(560, 9, 48)]
    [InlineData(100, 11.5, StageLines.MinChars)]
    [InlineData(0, 11.5, StageLines.DefaultChars)]
    public void The_line_length_follows_the_width_but_stays_comfortable(double width, double charWidth, int expected) =>
        Assert.Equal(expected, StageLines.CharsFor(width, charWidth));
}

public class VoiceActivityTests
{
    const double Block = 0.04;

    /// <summary>A room at about −60 dBFS, a voice at about −25 dBFS.</summary>
    const double Room = 0.001, Voice = 0.056;

    static bool Run(VoiceActivity vad, double rms, double seconds)
    {
        bool speaking = vad.Speaking;
        for (double t = 0; t < seconds - 1e-9; t += Block) speaking = vad.Feed(rms, Block);
        return speaking;
    }

    [Fact]
    public void Quiet_is_not_speech_and_a_voice_is()
    {
        var vad = new VoiceActivity();
        Assert.False(Run(vad, Room, 2));
        Assert.True(Run(vad, Voice, 0.3));
    }

    [Fact]
    public void A_short_knock_does_not_start_it()
    {
        var vad = new VoiceActivity();
        Run(vad, Room, 1);
        Assert.False(Run(vad, Voice, 0.04));
        Assert.False(Run(vad, Room, 0.2));
    }

    [Fact]
    public void Gaps_between_words_do_not_stop_it_but_a_pause_does()
    {
        var vad = new VoiceActivity();
        Run(vad, Room, 1);
        Assert.True(Run(vad, Voice, 0.5));
        // A 300 ms gap between words: still speaking.
        Assert.True(Run(vad, Room, 0.3));
        Assert.True(Run(vad, Voice, 0.4));
        // A real pause: stops after the hangover.
        Assert.False(Run(vad, Room, 1.2));
    }

    [Fact]
    public void The_floor_follows_a_noisy_room()
    {
        var vad = new VoiceActivity();
        Run(vad, Room, 1);
        // A fan comes on at about −40 dBFS (20 dB over the room): at first it sounds like a voice...
        const double Fan = 0.01;
        Run(vad, Fan, 1);
        // ...but it is steady, and the floor learns it.
        Assert.False(Run(vad, Fan, 60));
        Assert.InRange(vad.Floor, -44, -38);
        // A voice over the fan still counts.
        Assert.True(Run(vad, Voice, 0.3));
    }

    [Fact]
    public void A_mixed_synthetic_stream_is_tracked()
    {
        var vad = new VoiceActivity();
        var rng = new Random(7);
        var timeline = new List<(double Seconds, bool Talking)> { (1.5, false), (3, true), (1.5, false), (2, true), (2, false) };
        var results = new List<bool>();
        foreach (var (seconds, talking) in timeline)
        {
            int speakingBlocks = 0, blocks = 0;
            for (double t = 0; t < seconds - 1e-9; t += Block)
            {
                // Speech is bursty: syllables at -20..-30 dB with short dips; the room jitters around -60 dB.
                double rms = talking
                    ? (rng.NextDouble() < 0.2 ? Room * 2 : Voice * (0.5 + rng.NextDouble()))
                    : Room * (0.6 + 0.8 * rng.NextDouble());
                if (vad.Feed(rms, Block)) speakingBlocks++;
                blocks++;
            }
            results.Add(speakingBlocks > blocks * 0.6);
        }
        Assert.Equal(new[] { false, true, false, true, false }, results);
    }

    [Fact]
    public void Silence_from_a_blocked_microphone_never_counts()
    {
        var vad = new VoiceActivity();
        Assert.False(Run(vad, 0, 3));
        Assert.Equal(-90, vad.Level);
    }

    [Fact]
    public void Decibels_are_from_full_scale() => Assert.Equal(-20, VoiceActivity.Decibels(0.1), 6);
}

public class StageReaderTests
{
    static StageReader Reader(params string[] lines) => new(lines, PrompterPace.Normal);

    [Fact]
    public void The_reading_rate_is_about_150_words_a_minute_and_the_pace_scales_it()
    {
        Assert.Equal(150, StageReader.WordsPerMinute(PrompterPace.Normal), 6);
        Assert.True(StageReader.WordsPerMinute(PrompterPace.Slow) < 120);
        Assert.True(StageReader.WordsPerMinute(PrompterPace.Fast) > 180);
        // Ten words at 150 a minute: four seconds.
        Assert.Equal(4, StageReader.Seconds(10, PrompterPace.Normal), 6);
        // A one-word line still gets its moment.
        Assert.Equal(StageReader.ShortestLine, StageReader.Seconds(1, PrompterPace.Normal));
    }

    [Fact]
    public void It_moves_on_only_while_you_speak()
    {
        var r = Reader("one two three four five six seven eight nine ten", "next line here", "last");
        Assert.Equal(0, r.Advance(10, speaking: false));
        Assert.Equal(0, r.Index);
        Assert.Equal(0, r.Advance(3.9, speaking: true));
        Assert.Equal(1, r.Advance(0.2, speaking: true));
        Assert.Equal(1, r.Index);
        Assert.Equal("one two three four five six seven eight nine ten", r.Previous);
        Assert.Equal("next line here", r.Current);
        Assert.Equal("last", r.Next);
    }

    [Fact]
    public void Pausing_holds_it()
    {
        var r = Reader("a b c", "d e f");
        r.Paused = true;
        Assert.Equal(0, r.Advance(30, true));
        r.Paused = false;
        Assert.Equal(1, r.Advance(1.3, true));
    }

    [Fact]
    public void A_slower_pace_takes_longer()
    {
        var normal = Reader("one two three four five six seven eight nine ten", "b");
        var slow = new StageReader(normal.Lines, PrompterPace.Slow);
        Assert.Equal(1, normal.Advance(4.1, true));
        Assert.Equal(0, slow.Advance(4.1, true));
    }

    [Fact]
    public void It_stops_at_the_end_and_says_so()
    {
        var r = Reader("a b c", "d e f");
        r.Advance(1, true);
        r.Advance(1, true);
        r.Advance(1, true);
        Assert.Equal(1, r.Index);
        Assert.True(r.Done);
        Assert.Equal(100, r.Percent, 6);
        Assert.Equal(0, r.Advance(5, true));
        r.Restart();
        Assert.Equal(0, r.Index);
        Assert.False(r.Done);
    }

    [Fact]
    public void Arrows_step_a_line_and_stay_in_the_script()
    {
        var r = Reader("a", "b", "c");
        Assert.Equal(1, r.Step(1));
        Assert.Equal(1, r.Step(5));
        Assert.Equal(2, r.Index);
        Assert.Equal(-2, r.Step(-9));
        Assert.Equal(0, r.Index);
    }

    [Fact]
    public void Progress_is_counted_in_words()
    {
        var r = Reader("one two three four five six seven eight nine ten", "a b c d e f g h i j");
        Assert.Equal(0, r.Percent);
        r.Advance(2, true);
        Assert.Equal(25, r.Percent, 6);
        r.Seek(50);
        Assert.Equal(1, r.Index);
        Assert.Equal(50, r.Percent, 6);
    }

    [Fact]
    public void Wrapping_again_keeps_the_place()
    {
        var script = string.Join(' ', Enumerable.Range(1, 60).Select(i => "word" + i));
        var r = new StageReader(StageLines.Wrap(script, 46));
        r.Step(3);
        int word = r.WordIndex;
        r.SetLines(StageLines.Wrap(script, 24));
        Assert.InRange(r.WordIndex, word - 5, word);
        Assert.Contains("word" + (word + 1), r.Current.Split(' ').Concat(new[] { r.Next.Split(' ').FirstOrDefault() ?? "" }));
    }

    [Fact]
    public void An_empty_script_is_done_at_once()
    {
        var r = Reader();
        Assert.True(r.Done);
        Assert.Equal(0, r.Advance(1, true));
        Assert.Equal("", r.Current);
    }
}

public class StageTextTests
{
    [Theory]
    [InlineData(22, 1, 24)]
    [InlineData(22, -1, 20)]
    [InlineData(32, 1, 32)]
    [InlineData(16, -1, 16)]
    [InlineData(double.NaN, 1, 24)]
    public void Text_sizes_step_within_bounds(double size, int direction, double expected) =>
        Assert.Equal(expected, StageText.StepSize(size, direction));

    [Fact]
    public void The_caption_line_keeps_the_newest_words()
    {
        Assert.Equal("and that is why", StageText.CaptionTail("first line\nand  that is why"));
        var tail = StageText.CaptionTail("so the plan for the next quarter is to grow the team and ship the new app", 30);
        Assert.StartsWith("…", tail);
        Assert.EndsWith("ship the new app", tail);
        Assert.True(tail.Length <= 30);
        Assert.Equal("", StageText.CaptionTail(null));
    }

    [Theory]
    [InlineData("0.2", true, 0.2)]
    [InlineData("3", true, 1.0)]
    [InlineData("off", true, null)]
    [InlineData("loud", false, null)]
    [InlineData("-1", false, null)]
    public void Simulated_voice_levels_are_read(string arg, bool ok, double? level)
    {
        Assert.Equal(ok, StageText.TryVoiceLevel(arg, out var got));
        Assert.Equal(level, got);
    }

    [Fact]
    public void The_ring_light_is_switched_and_dimmed()
    {
        Assert.True(StageText.TryRing("off", true, 0.6, out var on, out var s));
        Assert.False(on);
        Assert.Equal(0.6, s);
        Assert.True(StageText.TryRing("80%", false, 0.6, out on, out s));
        Assert.True(on);
        Assert.Equal(0.8, s, 6);
        Assert.True(StageText.TryRing("5", true, 0.6, out _, out s));
        Assert.Equal(StageText.FaintestRing, s);
        Assert.True(StageText.TryRing("brighter", true, 0.9, out _, out s));
        Assert.Equal(1, s);
        Assert.False(StageText.TryRing("purple", true, 0.6, out _, out _));
    }

    [Theory]
    [InlineData("stage", PrompterCommandKind.Open)]
    [InlineData("start stage", PrompterCommandKind.Open)]
    [InlineData("Open Stage.", PrompterCommandKind.Open)]
    [InlineData("stage on", PrompterCommandKind.Open)]
    [InlineData("close stage", PrompterCommandKind.Close)]
    [InlineData("end stage", PrompterCommandKind.Close)]
    [InlineData("stage off", PrompterCommandKind.Close)]
    [InlineData("stage from clipboard", PrompterCommandKind.FromClipboard)]
    public void Stage_has_its_own_phrases(string text, PrompterCommandKind kind) =>
        Assert.Equal(kind, PrompterText.Parse(text)?.Kind);

    [Theory]
    [InlineData("stage 4")]
    [InlineData("stage fright tips")]
    [InlineData("backstage")]
    public void Other_stage_searches_stay_searches(string text) => Assert.Null(PrompterText.Parse(text));
}
