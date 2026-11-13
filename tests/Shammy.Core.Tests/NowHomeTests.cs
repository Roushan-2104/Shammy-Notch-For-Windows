using Shammy.Core.Notch;

namespace Shammy.Core.Tests;

public class NowHomeTests
{
    static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    // ---- The big moment ---------------------------------------------------------------------------

    [Fact]
    public void Music_playing_wins_over_everything()
    {
        var s = new NowSignals(MusicPlaying: true, HasMusic: true, InCall: true, CountdownLeft: 10 * Min, MeetingStartsIn: 5 * Min,
            MeetingEndsIn: 35 * Min, HasWeather: true, HasBattery: true);
        Assert.Equal(HeroKind.Music, NowHome.Hero(s));
    }

    [Fact]
    public void A_call_comes_next()
    {
        Assert.Equal(HeroKind.Call, NowHome.Hero(new NowSignals(HasMusic: true, InCall: true, CountdownLeft: 10 * Min, HasWeather: true)));
    }

    [Fact]
    public void A_meeting_going_on_counts_as_a_call()
    {
        var s = new NowSignals(MeetingStartsIn: -5 * Min, MeetingEndsIn: 25 * Min, CountdownLeft: 10 * Min, HasWeather: true);
        Assert.Equal(HeroKind.Call, NowHome.Hero(s));
    }

    [Fact]
    public void A_running_focus_beats_the_next_meeting()
    {
        var s = new NowSignals(CountdownLeft: 18 * Min, MeetingStartsIn: 12 * Min, MeetingEndsIn: 42 * Min, HasWeather: true);
        Assert.Equal(HeroKind.Focus, NowHome.Hero(s));
    }

    [Fact]
    public void The_next_meeting_beats_a_paused_timer_and_a_paused_song()
    {
        var s = new NowSignals(HasMusic: true, CountdownLeft: 18 * Min, CountdownPaused: true, MeetingStartsIn: 12 * Min,
            MeetingEndsIn: 42 * Min, HasWeather: true);
        Assert.Equal(HeroKind.Meeting, NowHome.Hero(s));
    }

    [Fact]
    public void A_paused_timer_comes_before_a_paused_song()
    {
        Assert.Equal(HeroKind.Focus, NowHome.Hero(new NowSignals(HasMusic: true, CountdownLeft: 3 * Min, CountdownPaused: true)));
    }

    [Fact]
    public void A_meeting_beyond_the_horizon_is_not_next()
    {
        var far = NowHome.MeetingHorizon + Min;
        Assert.Equal(HeroKind.Weather, NowHome.Hero(new NowSignals(MeetingStartsIn: far, MeetingEndsIn: far + 30 * Min, HasWeather: true)));
    }

    [Fact]
    public void A_paused_song_comes_before_the_weather()
    {
        Assert.Equal(HeroKind.PausedMusic, NowHome.Hero(new NowSignals(HasMusic: true, HasWeather: true)));
    }

    [Fact]
    public void Weather_then_the_clock()
    {
        Assert.Equal(HeroKind.Weather, NowHome.Hero(new NowSignals(HasWeather: true, HasBattery: true)));
        Assert.Equal(HeroKind.Clock, NowHome.Hero(new NowSignals()));
    }

    [Fact]
    public void A_finished_meeting_is_ignored()
    {
        Assert.Equal(HeroKind.Clock, NowHome.Hero(new NowSignals(MeetingStartsIn: -40 * Min, MeetingEndsIn: -10 * Min)));
    }

    // ---- The three numbers ------------------------------------------------------------------------

    [Fact]
    public void In_a_call_the_third_number_is_the_next_thing_not_the_call_again()
    {
        var s = new NowSignals(InCall: true, CountdownLeft: 10 * Min, MeetingStartsIn: 5 * Min, MeetingEndsIn: 35 * Min, HasWeather: true, HasBattery: true);
        Assert.Equal(new[] { NowStat.Weather, NowStat.Battery, NowStat.FocusLeft }, NowHome.Stats(s));
    }

    [Fact]
    public void Focusing_the_third_number_is_the_focus_left()
    {
        var s = new NowSignals(MusicPlaying: true, CountdownLeft: 10 * Min, MeetingStartsIn: 5 * Min, MeetingEndsIn: 35 * Min, HasWeather: true, HasBattery: true);
        Assert.Equal(NowStat.FocusLeft, NowHome.MomentStat(s));
    }

    [Fact]
    public void With_music_playing_and_a_meeting_coming_the_third_number_is_the_meeting()
    {
        var s = new NowSignals(MusicPlaying: true, HasMusic: true, MeetingStartsIn: 12 * Min, MeetingEndsIn: 42 * Min, HasWeather: true, HasBattery: true);
        Assert.Equal(new[] { NowStat.Weather, NowStat.Battery, NowStat.MeetingIn }, NowHome.Stats(s));
    }

    [Fact]
    public void A_paused_timer_is_the_moment_so_its_minutes_are_not_repeated()
    {
        Assert.Null(NowHome.MomentStat(new NowSignals(CountdownLeft: 4 * Min, CountdownPaused: true)));
        Assert.Equal(NowStat.FocusLeft, NowHome.MomentStat(new NowSignals(CountdownLeft: 4 * Min, CountdownPaused: true,
            MeetingStartsIn: 20 * Min, MeetingEndsIn: 50 * Min)));
    }

    [Fact]
    public void Nothing_going_on_shows_the_date()
    {
        Assert.Equal(new[] { NowStat.Weather, NowStat.Battery, NowStat.Date }, NowHome.Stats(new NowSignals(HasWeather: true, HasBattery: true)));
    }

    [Fact]
    public void A_desktop_without_weather_never_repeats_a_number()
    {
        Assert.Equal(new[] { NowStat.Time, NowStat.Date }, NowHome.Stats(new NowSignals()));
        Assert.Equal(new[] { NowStat.Weather, NowStat.Date, NowStat.Time }, NowHome.Stats(new NowSignals(HasWeather: true)));
        Assert.Equal(new[] { NowStat.Time, NowStat.Date }, NowHome.Stats(new NowSignals(CountdownLeft: Min)));
    }

    // ---- Words and numbers ------------------------------------------------------------------------

    [Fact]
    public void The_sentence_joins_up_to_three_parts_once_each()
    {
        Assert.Equal("Right now · Playing Kesariya · Standup in 12 min",
            NowText.Sentence(new[] { "Playing Kesariya", "", null, "Standup in 12 min", "playing kesariya" }));
        Assert.Equal("Right now · a · b · c", NowText.Sentence(new[] { "a", "b", "c", "d" }));
        Assert.Equal("Right now · " + NowText.Quiet, NowText.Sentence(Array.Empty<string>()));
    }

    [Theory]
    [InlineData(12.0, true, "12", "min")]
    [InlineData(0.5, true, "1", "min")]
    [InlineData(0.5, false, "0", "min")]
    [InlineData(-3.0, true, "0", "min")]
    [InlineData(99.0, false, "99", "min")]
    [InlineData(100.0, false, "1:40", "h")]
    [InlineData(125.4, true, "2:06", "h")]
    public void Minutes_read_as_a_big_number_and_unit(double minutes, bool roundUp, string value, string unit)
    {
        Assert.Equal((value, unit), NowText.Minutes(TimeSpan.FromMinutes(minutes), roundUp));
    }

    [Fact]
    public void Temperatures_split_into_number_and_words()
    {
        Assert.Equal(("24", "Partly cloudy"), NowText.SplitTemperature("24° Partly cloudy"));
        Assert.Equal(("-3", "Oslo"), NowText.SplitTemperature("-3° Oslo"));
        Assert.Equal(("−3", ""), NowText.SplitTemperature("−3°"));
        Assert.Null(NowText.SplitTemperature("Finding the weather…"));
        Assert.Null(NowText.SplitTemperature("about 24°"));
        Assert.Null(NowText.SplitTemperature(null));
    }

    [Fact]
    public void Clocks_parse_back_to_spans()
    {
        Assert.Equal(TimeSpan.FromSeconds(754), NowText.ParseClock("12:34"));
        Assert.Equal(new TimeSpan(1, 2, 3), NowText.ParseClock("1:02:03"));
        Assert.Null(NowText.ParseClock("12"));
        Assert.Null(NowText.ParseClock("a:b"));
        Assert.Null(NowText.ParseClock(""));
    }

    [Fact]
    public void Battery_line_says_what_the_battery_is_doing()
    {
        Assert.Equal("Charging", NowText.BatteryLine(true, true, 64, null));
        Assert.Equal("Fully charged", NowText.BatteryLine(true, false, 100, null));
        Assert.Equal("Plugged in", NowText.BatteryLine(true, false, 80, null));
        Assert.Equal("About 3 h left", NowText.BatteryLine(false, false, 70, TimeSpan.FromHours(3)));
        Assert.Equal("On battery", NowText.BatteryLine(false, false, 70, null));
        Assert.Equal("On battery", NowText.BatteryLine(false, false, 70, TimeSpan.MaxValue));
    }
}

public class NowHomeNoRepeatTests
{
    [Fact]
    public void A_call_moment_does_not_repeat_the_call_minutes_as_the_third_number()
    {
        var s = new Shammy.Core.Notch.NowSignals { InCall = true };
        Assert.NotEqual(Shammy.Core.Notch.NowStat.CallMinutes, Shammy.Core.Notch.NowHome.MomentStat(s));
    }

    [Fact]
    public void Hero_line_names_the_call_app()
    {
        Assert.Equal("On a call in Google Meet", Shammy.Core.Notch.NowText.HeroLine(Shammy.Core.Notch.HeroKind.Call, "Google Meet", null, null));
        Assert.Null(Shammy.Core.Notch.NowText.HeroLine(Shammy.Core.Notch.HeroKind.Music, null, null, null));
    }
}
