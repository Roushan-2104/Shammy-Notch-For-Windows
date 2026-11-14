using Shammy.Core.Guide;

namespace Shammy.Core.Tests;

public class FeatureGuideTests
{
    [Fact]
    public void Every_feature_has_a_name_a_line_and_a_group_and_ids_are_unique()
    {
        Assert.Equal(FeatureGuide.All.Count, FeatureGuide.All.Select(f => f.Id).Distinct().Count());
        Assert.Equal(FeatureGuide.All.Count, FeatureGuide.All.Select(f => f.Name).Distinct().Count());
        foreach (var f in FeatureGuide.All)
        {
            Assert.InRange(f.Group, 0, FeatureGuide.Groups.Count - 1);
            Assert.True(f.Name.Length is > 2 and <= 24, f.Name);
            Assert.True(f.Line.Length is > 10 and <= 80, f.Line);
            Assert.True(f.Phrase.Length > 0, f.Id);
            Assert.EndsWith(".", f.Line);
            Assert.Equal(f.Switch == GuideSwitch.None, f.SwitchName.Length == 0);
        }
    }

    [Fact]
    public void Every_group_has_something_in_it() =>
        Assert.All(Enumerable.Range(0, FeatureGuide.Groups.Count), g => Assert.Contains(FeatureGuide.All, f => f.Group == g));

    [Theory]
    [InlineData("0.6.0", "0.6")]
    [InlineData("0.6.0+6f0cafa", "0.6")]
    [InlineData("1.12.3", "1.12")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("dev", "")]
    public void The_release_line_is_the_first_two_numbers(string? version, string line) => Assert.Equal(line, FeatureGuide.Line(version));

    [Fact]
    public void New_means_new_in_the_version_that_is_running()
    {
        var record = FeatureGuide.ById("record")!;
        Assert.True(FeatureGuide.IsNew(record, "0.6.0"));
        Assert.True(FeatureGuide.IsNew(record, "0.6.4"));
        Assert.False(FeatureGuide.IsNew(record, "0.7.0"));
        Assert.False(FeatureGuide.IsNew(FeatureGuide.ById("alarms")!, "0.6.0"));
    }

    [Theory]
    [InlineData("nowline", "NowLineEnabled")]
    [InlineData("peek", "PeekEnabled")]
    [InlineData("gestures", "GesturesEnabled")]
    [InlineData("copypreview", "CopyPreviewEnabled")]
    public void Notch_features_have_their_own_switch(string id, string setting)
    {
        var f = FeatureGuide.ById(id)!;
        Assert.Equal(GuideSwitch.Setting, f.Switch);
        Assert.Equal(setting, f.SwitchName);
        Assert.Equal("", f.NewIn);
    }

    [Fact]
    public void Chained_requests_and_hide_while_using_are_explained()
    {
        Assert.Equal("timer 5m and pause music", FeatureGuide.ById("chain")!.Phrase);
        Assert.NotNull(FeatureGuide.ById("hideapps"));
    }

    [Theory]
    [InlineData("weather")]
    [InlineData("answers")]
    [InlineData("shelf")]
    public void A_switch_that_turns_off_more_says_so(string id) => Assert.StartsWith("Also turns off ", FeatureGuide.ById(id)!.Also);

    [Fact]
    public void One_name_per_feature()
    {
        Assert.Equal("Laptop", FeatureGuide.ById("quick")!.Name);
        Assert.Equal("Settings › Connections", FeatureGuide.ById("claude")!.Phrase);
        Assert.DoesNotContain(FeatureGuide.All, f => f.Phrase.Contains("General ›") || f.Phrase.Contains("Notch & pet"));
    }

    [Fact]
    public void Nothing_mentions_the_dock() => Assert.Empty(FeatureFinder.Find("dock"));

    [Fact]
    public void What_Try_it_types_is_something_its_own_feature_answers_to()
    {
        foreach (var f in FeatureGuide.All.Where(f => f.Try.Length > 0 && !f.Try.StartsWith("card:")))
            Assert.Equal(f.Id, FeatureFinder.UsedBy(f.Try)?.Id);
    }
}

public class FeatureFinderTests
{
    [Theory]
    [InlineData("save what is on my screen", "record")]
    [InlineData("stop my pc sleeping", "quick")]
    [InlineData("i can't hear what they are saying", "captions")]
    [InlineData("one app is too loud", "mixer")]
    [InlineData("my eyes are tired", "eyebreaks")]
    [InlineData("read my speech on camera", "teleprompter")]
    [InlineData("subtitles", "captions")]
    [InlineData("wake me up in the morning", "alarms")]
    [InlineData("recording", "record")]
    [InlineData("what did i copy earlier", "clipboard")]
    [InlineData("drink water", "water")]
    public void Everyday_words_find_the_feature(string said, string id) => Assert.Equal(id, FeatureFinder.Find(said)[0].Feature.Id);

    [Fact]
    public void An_empty_search_lists_everything_in_guide_order()
    {
        var all = FeatureFinder.Find("   ");
        Assert.Equal(FeatureGuide.All.Select(f => f.Id), all.Select(m => m.Feature.Id));
    }

    [Theory]
    [InlineData("xyzzy plugh")]
    [InlineData("the a of to")]
    [InlineData("?!...")]
    public void Nonsense_finds_nothing_or_everything_but_never_throws(string said) =>
        Assert.True(FeatureFinder.Find(said).Count is 0 || FeatureFinder.Words(said).Count == 0);

    [Fact]
    public void A_word_in_the_name_beats_a_word_in_the_description()
    {
        var found = FeatureFinder.Find("music");
        Assert.Equal("music", found[0].Feature.Id);
        Assert.True(found[0].Score > found.Last().Score || found.Count == 1);
    }

    [Fact]
    public void Short_words_inside_longer_ones_do_not_match() =>
        Assert.DoesNotContain(FeatureFinder.Find("eat"), m => m.Feature.Id == "eyebreaks" && m.Score > 0 && !m.Feature.Words.Contains("eat"));

    [Fact]
    public void Searching_within_a_group_only_looks_there()
    {
        var extras = FeatureGuide.All.Where(f => f.Group == 7);
        Assert.All(FeatureFinder.Find("score", extras), m => Assert.Equal(7, m.Feature.Group));
    }

    [Theory]
    [InlineData("timer 5m tea", "timers")]
    [InlineData("Timer 25m", "timers")]
    [InlineData("record my screen", "record")]
    [InlineData("stop recording", "record")]
    [InlineData("live captions", "captions")]
    [InlineData("play kesariya", "music")]
    [InlineData("remind me at 6pm to call mom", "reminders")]
    [InlineData("note: buy milk", "reminders")]
    [InlineData("wake me up at 6:30", "alarms")]
    [InlineData("volume mixer", "mixer")]
    [InlineData("volume 50", "levels")]
    [InlineData("keep awake for 2 hours", "quick")]
    [InlineData("open spotify", "launcher")]
    [InlineData("pet off", "pet")]
    public void A_command_counts_as_using_its_feature(string typed, string id) => Assert.Equal(id, FeatureFinder.UsedBy(typed)?.Id);

    [Theory]
    [InlineData("")]
    [InlineData("what is the capital of france")]
    [InlineData("playlist ideas")]
    [InlineData("recorder")]
    [InlineData("timers are nice")]
    public void Ordinary_searches_use_nothing(string typed) => Assert.Null(FeatureFinder.UsedBy(typed));
}

public class GuidePhrasesTests
{
    [Theory]
    [InlineData("help")]
    [InlineData("Help?")]
    [InlineData("features")]
    [InlineData("commands")]
    [InlineData("what can you do")]
    [InlineData("What can Shammy do?")]
    public void Asking_what_it_does_opens_the_whole_guide(string said)
    {
        var r = GuidePhrases.Parse(said);
        Assert.NotNull(r);
        Assert.Equal("", r!.Query);
        Assert.Null(r.Feature);
    }

    [Theory]
    [InlineData("mixer", "mixer")]
    [InlineData("captions", "captions")]
    [InlineData("Subtitles", "captions")]
    [InlineData("record", "record")]
    [InlineData("cat", "pet")]
    [InlineData("water", "water")]
    [InlineData("claude code", "claude")]
    public void A_features_own_short_name_opens_the_guide_on_it(string said, string id) => Assert.Equal(id, GuidePhrases.Parse(said)?.Feature?.Id);

    [Theory]
    [InlineData("how do i record my screen", "record")]
    [InlineData("How do I record my screen?", "record")]
    [InlineData("can you show subtitles for this video", "captions")]
    [InlineData("how to stop my pc sleeping", "quick")]
    [InlineData("is there a way to read my speech on camera", "teleprompter")]
    [InlineData("can shammy remind me to drink water", "water")]
    [InlineData("how do I use the dial", "wheel")]
    [InlineData("how do i turn the dial", "wheel")]
    public void A_question_about_something_it_does_opens_that_feature(string said, string id) => Assert.Equal(id, GuidePhrases.Parse(said)?.Feature?.Id);

    [Theory]
    [InlineData("how do i make tea")]
    [InlineData("how to fix a flat tyre")]
    [InlineData("can you tell me a joke")]
    [InlineData("where is paris")]
    [InlineData("recording studio near me")]
    [InlineData("claude")]
    [InlineData("settings")]
    [InlineData("weather in mumbai")]
    [InlineData("")]
    public void Everything_else_is_left_alone(string said) => Assert.Null(GuidePhrases.Parse(said));

    [Fact]
    public void A_very_long_question_is_not_ours() => Assert.Null(GuidePhrases.Parse("how do i " + new string('x', 200)));

    [Fact]
    public void No_alias_is_shared_by_two_features()
    {
        var all = FeatureGuide.All.SelectMany(f => f.Aliases).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}

public class GuideSuggestionsTests
{
    static readonly GuideMoment Quiet = new(false, false, false, false);

    [Fact]
    public void Something_playing_suggests_captions_and_the_mixer()
    {
        var (moment, s) = GuideSuggestions.For(Quiet with { SoundPlaying = true }, _ => true, _ => true);
        Assert.Equal("Something is playing", moment);
        Assert.Equal(new[] { "captions", "mixer", "lyrics" }, s.Select(x => x.Feature.Id));
    }

    [Fact]
    public void A_call_comes_before_everything_else()
    {
        var (moment, s) = GuideSuggestions.For(new GuideMoment(true, true, true, true), _ => true, _ => true);
        Assert.Equal("You are on a call", moment);
        Assert.Equal("teleprompter", s[0].Feature.Id);
    }

    [Fact]
    public void A_quiet_moment_offers_what_you_have_not_tried()
    {
        var (moment, s) = GuideSuggestions.For(Quiet, id => id != "captions" && id != "mixer", _ => true);
        Assert.Equal("Not tried yet", moment);
        Assert.Equal(new[] { "mixer", "captions" }, s.Select(x => x.Feature.Id));
        Assert.All(s, x => Assert.Contains("not tried", x.Why));
    }

    [Fact]
    public void Nothing_is_suggested_when_everything_has_been_tried_and_nothing_is_happening() =>
        Assert.Empty(GuideSuggestions.For(Quiet, _ => true, _ => true).Suggestions);

    [Fact]
    public void Switched_off_features_are_never_suggested()
    {
        var (_, s) = GuideSuggestions.For(Quiet with { SoundPlaying = true }, _ => true, f => f.Id != "captions");
        Assert.DoesNotContain(s, x => x.Feature.Id == "captions");
    }

    [Fact]
    public void Never_more_than_three_and_never_the_same_twice()
    {
        var (_, s) = GuideSuggestions.For(Quiet with { SoundPlaying = true }, _ => false, _ => true);
        Assert.Equal(GuideSuggestions.Max, s.Count);
        Assert.Equal(s.Count, s.Select(x => x.Feature.Id).Distinct().Count());
    }
}
