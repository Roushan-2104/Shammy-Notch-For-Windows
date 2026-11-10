using Shammy.Core.Captions;
using Xunit;

namespace Shammy.Core.Tests;

public class CaptionMergerTests
{
    static CaptionMerger Heard(params string[] hearings)
    {
        var merger = new CaptionMerger();
        foreach (var h in hearings) merger.Merge(h);
        return merger;
    }

    static string Bright(CaptionMerger m) => string.Join(' ', m.Words.Where(w => w.Batch == m.Batch).Select(w => w.Text));

    [Fact]
    public void The_first_hearing_is_shown_as_it_is()
    {
        var m = new CaptionMerger();
        var r = m.Merge("and that's why the second approach is faster");
        Assert.True(r.Changed);
        Assert.Equal(8, r.Added);
        Assert.Equal("and that's why the second approach is faster", m.Text);
    }

    [Fact]
    public void A_window_that_overlaps_only_adds_the_words_past_the_overlap()
    {
        var m = Heard("and that's why the second approach is faster");
        var r = m.Merge("the second approach is faster because it never copies the whole list");
        Assert.Equal(7, r.Added);
        Assert.Equal("and that's why the second approach is faster because it never copies the whole list", m.Text);
    }

    [Fact]
    public void Only_the_newest_words_are_bright()
    {
        var m = Heard("and that's why the second approach is faster",
            "the second approach is faster because it never copies the whole list");
        Assert.Equal("because it never copies the whole list", Bright(m));
    }

    [Fact]
    public void Three_rolling_windows_make_one_sentence_with_nothing_repeated()
    {
        var m = Heard(
            "So today we are going to look at",
            "we are going to look at two ways of sorting",
            "look at two ways of sorting a list of numbers.");
        Assert.Equal("So today we are going to look at two ways of sorting a list of numbers.", m.Text);
    }

    [Fact]
    public void The_same_hearing_again_changes_nothing()
    {
        var m = Heard("because it never copies the whole list");
        int batch = m.Batch;
        var r = m.Merge("because it never copies the whole list");
        Assert.False(r.Changed);
        Assert.Equal(0, r.Added);
        Assert.Equal(batch, m.Batch);
        Assert.Equal("because it never copies the whole list", m.Text);
    }

    [Fact]
    public void The_same_hearing_five_times_is_still_one_sentence()
    {
        var m = new CaptionMerger();
        for (int i = 0; i < 5; i++) m.Merge("It never copies the whole list.");
        Assert.Equal("It never copies the whole list.", m.Text);
    }

    [Fact]
    public void The_window_sliding_past_the_start_of_a_finished_sentence_changes_nothing()
    {
        var m = Heard("because it never copies the whole list");
        Assert.False(m.Merge("never copies the whole list").Changed);
        Assert.False(m.Merge("the whole list").Changed);
        Assert.False(m.Merge("list").Changed);
        Assert.Equal("because it never copies the whole list", m.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("...")]
    public void An_empty_hearing_changes_nothing(string? heard)
    {
        var m = Heard("it never copies the whole list");
        var r = m.Merge(heard);
        Assert.False(r.Changed);
        Assert.Equal("it never copies the whole list", m.Text);
    }

    [Fact]
    public void An_empty_hearing_first_shows_nothing()
    {
        var m = new CaptionMerger();
        Assert.False(m.Merge("").Changed);
        Assert.Empty(m.Words);
    }

    [Theory]
    [InlineData("Thank you.")]
    [InlineData("Thanks for watching!")]
    [InlineData("you")]
    public void What_the_model_invents_for_music_is_not_shown(string heard)
    {
        var m = Heard("it never copies the whole list");
        Assert.False(m.Merge(heard).Changed);
        Assert.Equal("it never copies the whole list", m.Text);
    }

    [Fact]
    public void A_reworded_last_word_is_replaced_not_repeated()
    {
        var m = Heard("because it never copies the hole");
        m.Merge("it never copies the whole list");
        Assert.Equal("because it never copies the whole list", m.Text);
        Assert.Equal("whole list", Bright(m));
    }

    [Fact]
    public void Two_reworded_last_words_are_replaced()
    {
        var m = Heard("and that is faster because it never copy the hall");
        m.Merge("faster because it never copies the whole list every time");
        Assert.Equal("and that is faster because it never copies the whole list every time", m.Text);
    }

    [Fact]
    public void A_last_word_cut_off_by_the_window_is_completed()
    {
        var m = Heard("the second approach is fast");
        m.Merge("second approach is faster because it never copies");
        Assert.Equal("the second approach is faster because it never copies", m.Text);
    }

    [Fact]
    public void A_misheard_word_in_the_middle_does_not_break_the_overlap()
    {
        var m = Heard("we sort the numbers in ascending order before the search");
        m.Merge("sort the numbers in a sending order before the search starts again");
        // Settled words stay as they were first shown; only the end grows.
        Assert.Equal("we sort the numbers in ascending order before the search starts again", m.Text);
    }

    [Fact]
    public void Words_older_than_the_last_few_never_change()
    {
        var m = Heard("one two three four five six seven eight nine ten");
        m.Merge("ONE TOO THREE FOR five six seven eight nine ten eleven");
        Assert.StartsWith("one two three four five six", m.Text);
        Assert.EndsWith("ten eleven", m.Text);
        Assert.Equal(11, m.Words.Count);
    }

    [Fact]
    public void Punctuation_and_case_differences_still_overlap()
    {
        var m = Heard("And that's why the second approach is faster.");
        m.Merge("THE SECOND APPROACH IS FASTER, BECAUSE IT NEVER COPIES");
        // Nothing is repeated; the last few words take the newer hearing's punctuation with them.
        Assert.Equal("And that's why the SECOND APPROACH IS FASTER, BECAUSE IT NEVER COPIES", m.Text);
        Assert.Equal(4, m.Words.Count(w => w.Batch == m.Batch));
    }

    [Fact]
    public void A_full_stop_the_model_put_mid_sentence_goes_when_the_sentence_continues()
    {
        var m = Heard("that's why the second approach is faster.");
        m.Merge("the second approach is faster because it never copies");
        Assert.Equal("that's why the second approach is faster because it never copies", m.Text);
    }

    [Fact]
    public void A_comma_that_arrives_later_is_added_without_counting_as_new_words()
    {
        var m = Heard("the second approach is faster");
        var r = m.Merge("the second approach is faster,");
        Assert.True(r.Changed);
        Assert.Equal(0, r.Added);
        Assert.Equal("the second approach is faster,", m.Text);
    }

    [Fact]
    public void The_capital_the_model_gives_its_first_word_is_not_copied_into_the_middle_of_a_sentence()
    {
        var m = Heard("that's why the second approach is faster");
        m.Merge("Approach is faster because it never copies");
        Assert.Equal("that's why the second approach is faster because it never copies", m.Text);
    }

    [Fact]
    public void A_single_word_carried_over_is_not_said_twice()
    {
        var m = Heard("Okay.");
        m.Merge("Okay. So today we will look at sorting.");
        Assert.Equal("Okay. So today we will look at sorting.", m.Text);
    }

    [Fact]
    public void A_phrase_really_said_twice_is_shown_twice()
    {
        var m = Heard("and we say thank you");
        m.Merge("thank you thank you");
        Assert.Equal("and we say thank you thank you", m.Text);
    }

    [Fact]
    public void A_repeated_sentence_lines_up_with_its_latest_telling()
    {
        var m = Heard("add one to it add one to it");
        m.Merge("add one to it again");
        Assert.Equal("add one to it add one to it again", m.Text);
    }

    [Fact]
    public void A_hearing_that_stops_short_never_takes_words_away()
    {
        var m = Heard("it never copies the whole list every time");
        var r = m.Merge("it never copies the");
        Assert.False(r.Changed);
        Assert.Equal("it never copies the whole list every time", m.Text);
    }

    [Fact]
    public void New_speech_with_nothing_in_common_is_added_after_what_was_shown()
    {
        var m = Heard("it never copies the whole list.");
        m.Merge("Now open your editor and type along.");
        Assert.Equal("it never copies the whole list. Now open your editor and type along.", m.Text);
    }

    [Fact]
    public void A_chance_shared_word_is_not_mistaken_for_an_overlap()
    {
        var m = Heard("it never copies the whole list every single time");
        m.Merge("Next we measure the time it takes");
        Assert.Equal("it never copies the whole list every single time Next we measure the time it takes", m.Text);
    }

    [Fact]
    public void Speech_after_a_pause_is_added_even_when_it_starts_with_the_same_words()
    {
        var m = Heard("so that is the first way.");
        m.Merge("That is the first way I would try.", overlaps: false);
        Assert.Equal("so that is the first way. That is the first way I would try.", m.Text);
    }

    [Fact]
    public void A_stray_dash_joins_the_word_before_it()
    {
        var m = Heard("the second approach - is faster");
        Assert.Equal(5, m.Words.Count);
        Assert.Equal("approach -", m.Words[2].Text);
    }

    [Fact]
    public void An_hour_of_captions_keeps_only_the_recent_words()
    {
        var m = new CaptionMerger();
        for (int i = 0; i < 400; i++) m.Merge($"word{i} next{i} more{i}", overlaps: false);
        Assert.InRange(m.Words.Count, 100, 220);
        Assert.Equal(1200, m.Words.Count + m.Dropped);
        Assert.Equal("more399", m.Words[^1].Text);
    }

    [Fact]
    public void Hindi_words_overlap_like_any_others()
    {
        var m = Heard("kal subah saat baje ka alarm");
        m.Merge("saat baje ka alarm laga do");
        Assert.Equal("kal subah saat baje ka alarm laga do", m.Text);
    }
}

public class CaptionTextTests
{
    [Theory]
    [InlineData("List,", "list")]
    [InlineData("that's", "thats")]
    [InlineData("(Faster!)", "faster")]
    [InlineData("--", "")]
    public void A_words_key_ignores_case_and_punctuation(string word, string key) => Assert.Equal(key, CaptionText.Key(word));

    [Theory]
    [InlineData("whole", "hole", true)]
    [InlineData("list", "lists", true)]
    [InlineData("the", "then", false)]
    [InlineData("cat", "bat", false)]
    [InlineData("approach", "approaches", true)]
    [InlineData("faster", "slower", false)]
    public void Words_one_letter_apart_are_the_same_word_only_when_long_enough(string a, string b, bool same) =>
        Assert.Equal(same, CaptionText.Similar(a, b));

    [Theory]
    [InlineData(true, "en", "Hinglish")]
    [InlineData(false, "en", "English")]
    [InlineData(false, "auto", "Any language")]
    [InlineData(false, null, "English")]
    public void The_strip_names_the_voice_mode_it_listens_in(bool hinglish, string? language, string name) =>
        Assert.Equal(name, CaptionText.LanguageName(hinglish, language));
}

public class CaptionLinesTests
{
    static IReadOnlyList<CaptionWord> Words(string text, int batch = 1) =>
        CaptionText.Words(text).Select(w => new CaptionWord(w.Text, w.Key, batch)).ToList();

    static string Line(IReadOnlyList<CaptionWord> line) => string.Join(' ', line.Select(w => w.Text));

    // One unit per character, twenty to a line.
    static CaptionLines Lines() => new(20, s => s.Length);

    [Fact]
    public void A_short_caption_sits_on_the_upper_line()
    {
        var v = Lines().Layout(Words("it never copies"));
        Assert.Equal("it never copies", Line(v.Upper));
        Assert.Empty(v.Lower);
    }

    [Fact]
    public void Nothing_heard_yet_is_an_empty_view()
    {
        var v = Lines().Layout(Words(""));
        Assert.True(v.IsEmpty);
    }

    [Fact]
    public void Words_that_do_not_fit_go_to_the_lower_line()
    {
        var v = Lines().Layout(Words("it never copies the whole list"));
        Assert.Equal("it never copies the", Line(v.Upper));
        Assert.Equal("whole list", Line(v.Lower));
    }

    [Fact]
    public void A_full_lower_line_moves_up_and_the_oldest_line_leaves()
    {
        var lines = Lines();
        lines.Layout(Words("it never copies the whole list every"));
        var v = lines.Layout(Words("it never copies the whole list every single time"));
        Assert.Equal("whole list every", Line(v.Upper));
        Assert.Equal("single time", Line(v.Lower));
    }

    [Fact]
    public void Words_already_shown_stay_where_they_are_as_more_arrive()
    {
        var lines = Lines();
        var before = lines.Layout(Words("it never copies the whole"));
        var after = lines.Layout(Words("it never copies the whole list"));
        Assert.Equal(Line(before.Upper), Line(after.Upper));
        Assert.Equal("whole", Line(before.Lower));
        Assert.Equal("whole list", Line(after.Lower));
    }

    [Fact]
    public void There_are_never_more_than_two_lines_however_much_arrives_at_once()
    {
        var v = Lines().Layout(Words("one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen"));
        Assert.True(Line(v.Upper).Length <= 20);
        Assert.True(Line(v.Lower).Length <= 20);
        Assert.Equal("sixteen", v.Lower[^1].Text);
    }

    [Fact]
    public void A_word_longer_than_the_line_still_gets_a_line()
    {
        var v = Lines().Layout(Words("see pneumonoultramicroscopicsilicovolcanoconiosis here"));
        Assert.Equal("pneumonoultramicroscopicsilicovolcanoconiosis", Line(v.Upper));
        Assert.Equal("here", Line(v.Lower));
    }

    [Fact]
    public void The_lines_stay_put_when_old_words_are_forgotten()
    {
        var lines = Lines();
        var all = Words("it never copies the whole list every single time");
        var before = lines.Layout(all);
        // The merger dropped the first four words; the same two lines must still be showing.
        var after = lines.Layout(all.Skip(4).ToList(), dropped: 4);
        Assert.Equal(Line(before.Upper), Line(after.Upper));
        Assert.Equal(Line(before.Lower), Line(after.Lower));
    }

    [Fact]
    public void A_merged_caption_rolls_through_two_lines_in_order()
    {
        var merger = new CaptionMerger();
        var lines = new CaptionLines(30, s => s.Length);
        CaptionView v = CaptionView.Empty;
        foreach (var heard in new[]
                 {
                     "and that's why the second approach",
                     "the second approach is faster because it never",
                     "is faster because it never copies the whole list",
                 })
        {
            merger.Merge(heard);
            v = lines.Layout(merger.Words, merger.Dropped);
        }
        Assert.Equal("approach is faster because it", Line(v.Upper));
        Assert.Equal("never copies the whole list", Line(v.Lower));
        Assert.Equal("copies the whole list", string.Join(' ', v.Lower.Where(w => w.Batch == merger.Batch).Select(w => w.Text)));
        Assert.DoesNotContain(v.Upper, w => w.Batch == merger.Batch);
    }
}

public class CaptionAudioTests
{
    const int Rate = CaptionAudio.SampleRate;

    static float[] Speech(double seconds)
    {
        var s = new float[(int)(seconds * Rate)];
        for (int i = 0; i < s.Length; i++) s[i] = 0.2f * MathF.Sin(i * 0.2f);
        return s;
    }

    static float[] Quiet(double seconds) => new float[(int)(seconds * Rate)];

    static double Seconds(CaptionChunk c) => c.Samples.Length / (double)Rate;

    [Fact]
    public void Nothing_is_transcribed_before_enough_sound_has_arrived()
    {
        var a = new CaptionAudio();
        a.Add(Speech(1.5));
        Assert.False(a.TryTake(out _));
    }

    [Fact]
    public void Two_seconds_of_speech_are_handed_out()
    {
        var a = new CaptionAudio();
        a.Add(Speech(2));
        Assert.True(a.TryTake(out var chunk));
        Assert.Equal(2, Seconds(chunk), 1);
        Assert.False(chunk.Overlaps);
    }

    [Fact]
    public void The_same_sound_is_not_handed_out_twice()
    {
        var a = new CaptionAudio();
        a.Add(Speech(2));
        Assert.True(a.TryTake(out _));
        Assert.False(a.TryTake(out _));
    }

    [Fact]
    public void Silence_is_never_transcribed()
    {
        var a = new CaptionAudio();
        for (int i = 0; i < 40; i++)
        {
            a.Add(Quiet(0.5));
            Assert.False(a.TryTake(out _));
        }
    }

    [Fact]
    public void Sound_below_the_noise_floor_counts_as_silence()
    {
        var a = new CaptionAudio();
        var hiss = Speech(4).Select(v => v * 0.01f).ToArray();
        a.Add(hiss);
        Assert.False(a.TryTake(out _));
    }

    [Fact]
    public void A_click_is_not_speech()
    {
        var a = new CaptionAudio();
        a.Add(Quiet(1));
        a.Add(Speech(0.06));
        a.Add(Quiet(1.5));
        Assert.False(a.TryTake(out _));
    }

    [Fact]
    public void Continuous_speech_gives_overlapping_windows_every_two_seconds()
    {
        var a = new CaptionAudio();
        a.Add(Speech(2));
        Assert.True(a.TryTake(out var first));
        a.Add(Speech(1));
        Assert.False(a.TryTake(out _));
        a.Add(Speech(1));
        Assert.True(a.TryTake(out var second));
        Assert.False(first.Overlaps);
        Assert.True(second.Overlaps);
        Assert.Equal(4, Seconds(second), 1);
    }

    [Fact]
    public void A_window_is_never_longer_than_eight_seconds()
    {
        var a = new CaptionAudio();
        CaptionChunk? last = null;
        for (int i = 0; i < 30; i++)
        {
            a.Add(Speech(1));
            if (a.TryTake(out var chunk)) last = chunk;
        }
        Assert.NotNull(last);
        Assert.Equal(8, Seconds(last!), 1);
        Assert.True(last!.Overlaps);
    }

    [Fact]
    public void Quiet_before_the_first_word_is_cut_off()
    {
        var a = new CaptionAudio();
        a.Add(Quiet(5));
        Assert.False(a.TryTake(out _));
        a.Add(Speech(2));
        Assert.True(a.TryTake(out var chunk));
        Assert.InRange(Seconds(chunk), 2.0, 2.5);
        Assert.False(chunk.Overlaps);
    }

    [Fact]
    public void A_sentence_that_ends_is_transcribed_without_waiting_out_the_step()
    {
        var a = new CaptionAudio();
        a.Add(Speech(1));
        Assert.False(a.TryTake(out _));
        a.Add(Quiet(0.7));
        Assert.True(a.TryTake(out var chunk));
        Assert.InRange(Seconds(chunk), 1.5, 1.8);
    }

    [Fact]
    public void Speech_that_has_only_just_begun_waits_for_a_little_more()
    {
        var a = new CaptionAudio();
        a.Add(Quiet(1.6));
        a.Add(Speech(0.5));
        Assert.False(a.TryTake(out _));
        a.Add(Speech(0.9));
        Assert.True(a.TryTake(out var chunk));
        Assert.InRange(Seconds(chunk), 1.4, 1.8);
    }

    [Fact]
    public void After_a_pause_the_window_starts_at_the_new_sentence()
    {
        var a = new CaptionAudio();
        a.Add(Speech(2));
        Assert.True(a.TryTake(out _));
        a.Add(Speech(1));
        a.Add(Quiet(0.7));
        Assert.True(a.TryTake(out var end));
        Assert.True(end.Overlaps);
        a.Add(Quiet(1.3));
        Assert.False(a.TryTake(out _));
        a.Add(Speech(2));
        Assert.True(a.TryTake(out var next));
        Assert.InRange(Seconds(next), 2.0, 2.5);
        Assert.False(next.Overlaps);
    }

    [Fact]
    public void A_short_breath_between_sentences_keeps_the_overlap()
    {
        var a = new CaptionAudio();
        a.Add(Speech(2));
        Assert.True(a.TryTake(out _));
        a.Add(Quiet(0.4));
        a.Add(Speech(1.6));
        Assert.True(a.TryTake(out var next));
        Assert.True(next.Overlaps);
        Assert.Equal(4, Seconds(next), 1);
    }

    [Fact]
    public void A_long_silence_throws_nothing_away_and_speech_after_it_is_heard()
    {
        var a = new CaptionAudio();
        a.Add(Speech(2));
        Assert.True(a.TryTake(out _));
        for (int i = 0; i < 120; i++)
        {
            a.AddSilence(Rate / 2);
            a.TryTake(out _);
        }
        a.Add(Speech(2));
        Assert.True(a.TryTake(out var chunk));
        Assert.False(chunk.Overlaps);
        Assert.InRange(Seconds(chunk), 2.0, 2.5);
    }

    [Fact]
    public void A_word_cut_in_half_by_a_chunk_is_heard_whole_once_it_ends()
    {
        var a = new CaptionAudio();
        a.Add(Speech(2));
        Assert.True(a.TryTake(out _));
        // The word goes on for a moment longer, then the speaker stops.
        a.Add(Speech(0.1));
        a.Add(Quiet(0.7));
        Assert.True(a.TryTake(out var chunk));
        Assert.True(chunk.Overlaps);
        Assert.Equal(2.8, Seconds(chunk), 1);
        a.Add(Quiet(3));
        Assert.False(a.TryTake(out _));
    }

    [Fact]
    public void A_sentence_that_ended_just_before_a_chunk_gets_one_more_look_and_no_more()
    {
        var a = new CaptionAudio();
        a.Add(Speech(1.95));
        a.Add(Quiet(0.05));
        Assert.True(a.TryTake(out _));
        a.Add(Quiet(0.6));
        Assert.True(a.TryTake(out var again));
        Assert.True(again.Overlaps);
        for (int i = 0; i < 20; i++)
        {
            a.Add(Quiet(0.5));
            Assert.False(a.TryTake(out _));
        }
    }

    [Fact]
    public void A_chunk_the_model_was_too_busy_for_is_offered_again()
    {
        var a = new CaptionAudio();
        a.Add(Speech(2));
        Assert.True(a.TryTake(out var first));
        a.Undo();
        Assert.True(a.TryTake(out var again));
        Assert.Equal(first.Samples.Length, again.Samples.Length);
        Assert.False(again.Overlaps);
    }

    [Fact]
    public void Sound_that_arrived_while_the_model_was_busy_is_in_the_next_window()
    {
        var a = new CaptionAudio();
        a.Add(Speech(2));
        Assert.True(a.TryTake(out _));
        a.Undo();
        a.Add(Speech(3));
        Assert.True(a.TryTake(out var chunk));
        Assert.Equal(5, Seconds(chunk), 1);
    }

    [Fact]
    public void More_than_a_window_fed_at_once_keeps_only_its_end()
    {
        var a = new CaptionAudio();
        a.Add(Speech(20));
        Assert.Equal(20L * Rate, a.Total);
        Assert.True(a.TryTake(out var chunk));
        Assert.Equal(8, Seconds(chunk), 1);
    }

    [Fact]
    public void The_window_holds_the_newest_sound_in_order()
    {
        var a = new CaptionAudio(windowSeconds: 1, stepSeconds: 0.5);
        var ramp = new float[Rate * 3];
        for (int i = 0; i < ramp.Length; i++) ramp[i] = 0.1f + i / (float)ramp.Length * 0.5f;
        foreach (var part in ramp.Chunk(700)) a.Add(part);
        Assert.True(a.TryTake(out var chunk));
        Assert.Equal(Rate, chunk.Samples.Length);
        Assert.Equal(ramp[^1], chunk.Samples[^1]);
        Assert.Equal(ramp[^Rate], chunk.Samples[0]);
    }
}

public class CaptionPhrasesTests
{
    [Theory]
    [InlineData("captions")]
    [InlineData("live captions")]
    [InlineData("captions on")]
    [InlineData("turn on captions")]
    [InlineData("Turn on live captions.")]
    [InlineData("turn the captions on")]
    [InlineData("start captions")]
    [InlineData("Captions on, please")]
    [InlineData("hey shammy, turn on captions")]
    [InlineData("subtitles on")]
    public void These_turn_captions_on(string text) => Assert.True(CaptionPhrases.Parse(text));

    [Theory]
    [InlineData("captions off")]
    [InlineData("stop captions")]
    [InlineData("Stop captions.")]
    [InlineData("turn off captions")]
    [InlineData("turn off live captions")]
    [InlineData("turn captions off")]
    [InlineData("hide the captions")]
    [InlineData("live captions off")]
    public void These_turn_captions_off(string text) => Assert.False(CaptionPhrases.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("captions for instagram")]
    [InlineData("how to turn on captions on youtube")]
    [InlineData("funny captions")]
    [InlineData("caption")]
    [InlineData("subtitles")]
    [InlineData("stop")]
    [InlineData("turn on bluetooth")]
    [InlineData("captions off meaning")]
    public void Anything_else_is_left_for_search(string text) => Assert.Null(CaptionPhrases.Parse(text));
}

public class CaptionResamplerTests
{
    static float[] Tone(int rate, double hz, double seconds, float amplitude = 0.5f)
    {
        var s = new float[(int)(rate * seconds)];
        for (int i = 0; i < s.Length; i++) s[i] = amplitude * (float)Math.Sin(2 * Math.PI * hz * i / rate);
        return s;
    }

    static float[] Resample(int rate, float[] sound, int piece)
    {
        var r = new CaptionResampler(rate);
        var all = new List<float>();
        var buffer = new float[16];
        for (int at = 0; at < sound.Length; at += piece)
        {
            int n = r.Process(sound.AsSpan(at, Math.Min(piece, sound.Length - at)), ref buffer);
            all.AddRange(buffer.Take(n));
        }
        return all.ToArray();
    }

    // Loudness away from the ends, where the filter is still filling up.
    static double Rms(float[] s) => Math.Sqrt(s.Skip(200).Take(s.Length - 400).Sum(v => (double)v * v) / (s.Length - 400));

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(96000)]
    [InlineData(8000)]
    public void A_second_of_sound_becomes_sixteen_thousand_samples(int rate)
    {
        var result = Resample(rate, Tone(rate, 440, 1), rate / 100);
        Assert.InRange(result.Length, 15900, 16001);
    }

    [Theory]
    [InlineData(48000, 300)]
    [InlineData(48000, 1000)]
    [InlineData(48000, 3000)]
    [InlineData(44100, 1000)]
    [InlineData(44100, 3000)]
    [InlineData(96000, 1000)]
    public void A_voice_band_tone_comes_through_as_loud_as_it_went_in(int rate, int hz)
    {
        var result = Resample(rate, Tone(rate, hz, 1), rate / 100);
        Assert.InRange(Rms(result), 0.34, 0.365);
    }

    [Theory]
    [InlineData(48000, 12000)]
    [InlineData(48000, 15000)]
    [InlineData(44100, 13000)]
    [InlineData(96000, 20000)]
    public void Sound_too_high_for_the_model_is_removed_not_folded_into_the_speech(int rate, int hz)
    {
        var result = Resample(rate, Tone(rate, hz, 1), rate / 100);
        Assert.True(Rms(result) < 0.01, $"left {Rms(result):0.0000}");
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void Small_pieces_give_the_same_sound_as_one_long_piece(int rate)
    {
        var sound = Tone(rate, 700, 0.5);
        var whole = Resample(rate, sound, sound.Length);
        var pieces = Resample(rate, sound, 441);
        var odd = Resample(rate, sound, 37);
        Assert.Equal(whole.Length, pieces.Length);
        Assert.Equal(whole.Length, odd.Length);
        for (int i = 0; i < whole.Length; i++)
        {
            Assert.Equal(whole[i], pieces[i], 5);
            Assert.Equal(whole[i], odd[i], 5);
        }
    }

    [Fact]
    public void A_tone_keeps_its_pitch()
    {
        // 1 kHz at 16 kHz crosses zero upwards a thousand times a second.
        var result = Resample(44100, Tone(44100, 1000, 1), 441);
        int crossings = 0;
        for (int i = 1; i < result.Length; i++)
            if (result[i - 1] < 0 && result[i] >= 0) crossings++;
        Assert.InRange(crossings, 990, 1001);
    }

    [Fact]
    public void Sound_already_at_sixteen_kilohertz_is_passed_through_untouched()
    {
        var sound = Tone(16000, 500, 0.1);
        var result = Resample(16000, sound, 160);
        Assert.Equal(sound, result);
    }

    [Fact]
    public void Silence_stays_silent()
    {
        var result = Resample(48000, new float[48000], 480);
        Assert.All(result, v => Assert.Equal(0f, v));
    }
}

/// <summary>The window and the merger together, with a stand-in for the model that "hears" exactly the words
/// spoken inside each chunk it is given.</summary>
public class CaptionPipelineTests
{
    const int Rate = CaptionAudio.SampleRate;
    const double WordSeconds = 0.26, GapSeconds = 0.06, Poll = 0.15;

    sealed record Spoken(string Word, double From, double To);

    /// <summary>Speaks the text (a "/" is a pause of that many tenths of a second, e.g. "/16") through the
    /// window as the worker would, and returns what ends up as the caption.</summary>
    static (string Caption, int Chunks, int Fresh) Run(string script, bool cutWordsCount)
    {
        var words = new List<Spoken>();
        double t = 0.4;
        foreach (var token in script.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith('/'))
            {
                t += int.Parse(token[1..]) / 10.0;
                continue;
            }
            words.Add(new Spoken(token, t, t + WordSeconds));
            t += WordSeconds + GapSeconds;
        }
        // Sound while a word is being said, quiet between words.
        var sound = new float[(int)((t + 3) * Rate)];
        foreach (var w in words)
            for (int i = (int)(w.From * Rate); i < (int)(w.To * Rate); i++) sound[i] = 0.2f * MathF.Sin(i * 0.3f);

        var audio = new CaptionAudio();
        var merger = new CaptionMerger();
        int chunks = 0, fresh = 0, piece = (int)(Poll * Rate);
        for (int fed = 0; fed + piece <= sound.Length;)
        {
            audio.Add(sound.AsSpan(fed, piece));
            fed += piece;
            if (!audio.TryTake(out var chunk)) continue;
            chunks++;
            if (!chunk.Overlaps) fresh++;
            double to = fed / (double)Rate, from = to - chunk.Samples.Length / (double)Rate;
            // A word cut by either end of the chunk is heard or not, depending on the run.
            var heard = words.Where(w => cutWordsCount ? w.To > from + 0.05 && w.From < to - 0.05 : w.From >= from && w.To <= to);
            merger.Merge(string.Join(' ', heard.Select(w => w.Word)), chunk.Overlaps);
        }
        return (merger.Text, chunks, fresh);
    }

    const string Lecture =
        "so today we are going to look at two ways of sorting a list of numbers /6 " +
        "the first way copies the whole list every time it runs and that is slow /18 " +
        "the second way never copies the list and that is why the second way is faster than the first /5 " +
        "now open your editor and type along with me";

    static string Said(string script) => string.Join(' ', script.Split(' ').Where(w => !w.StartsWith('/')));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_lecture_comes_out_word_for_word_with_nothing_repeated_or_lost(bool cutWordsCount)
    {
        var (caption, chunks, _) = Run(Lecture, cutWordsCount);
        Assert.Equal(Said(Lecture), caption);
        Assert.InRange(chunks, 8, 20);
    }

    [Fact]
    public void A_sentence_after_a_long_pause_is_transcribed_on_its_own()
    {
        var (caption, _, fresh) = Run("one two three four five six /30 seven eight nine ten eleven twelve", cutWordsCount: false);
        Assert.Equal("one two three four five six seven eight nine ten eleven twelve", caption);
        Assert.Equal(2, fresh);
    }

    [Fact]
    public void The_same_words_said_again_after_a_pause_are_shown_again()
    {
        var (caption, _, _) = Run("thank you very much /25 thank you very much", cutWordsCount: false);
        Assert.Equal("thank you very much thank you very much", caption);
    }

    [Fact]
    public void A_single_word_is_captioned()
    {
        var (caption, chunks, _) = Run("hello", cutWordsCount: false);
        Assert.Equal("hello", caption);
        Assert.Equal(1, chunks);
    }

    [Fact]
    public void Ten_minutes_of_talking_never_repeats_a_word()
    {
        var script = string.Join(' ', Enumerable.Range(0, 1800).Select(i => i % 40 == 39 ? $"w{i} /12" : $"w{i}"));
        var (caption, _, _) = Run(script, cutWordsCount: true);
        var words = caption.Split(' ');
        Assert.Equal(words.Length, words.Distinct().Count());
        Assert.Equal("w1799", words[^1]);
    }
}
