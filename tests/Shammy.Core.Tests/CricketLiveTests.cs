using Shammy.Core.Ambient;
using Shammy.Core.Notch;

namespace Shammy.Core.Tests;

public class CricketLiveFeedTests
{
    // Shaped like the real feed: a title, the same text as the description, and the match id in the link.
    const string Feed = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <rss version="2.0"><channel><title>Cricinfo Live Scores</title>
        <item><title>Pakistan 434/9  v Sri Lanka 114/3 *</title><guid>http://www.cricinfo.com/ci/engine/match/1550711.html</guid></item>
        <item><title>India Under-19s 309/7  v United Arab Emirates Under-19s 239/10 *</title><guid>http://www.cricinfo.com/ci/engine/match/1558440.html</guid></item>
        <item><title>Chhattisgarh 279/7 * v Hyderabad (India)</title><guid>http://www.cricinfo.com/ci/engine/match/1559000.html</guid></item>
        <item><title>India A 32 &amp;  346/10 * v New Zealand A 424/10</title><guid>http://www.cricinfo.com/ci/engine/match/1559001.html</guid></item>
        <item><title>India Women v England Women</title><guid>http://www.cricinfo.com/ci/engine/match/1559002.html</guid></item>
        <item><title>Australia 239/10 v India 182/4 *</title><guid>http://www.cricinfo.com/ci/engine/match/1559003.html</guid></item>
        </channel></rss>
        """;

    static CricketFeedItem Item(string title, string id = "1") => new(id, CricketFeed.ParseTitle(title)!);

    [Fact]
    public void Reads_each_match_with_the_id_from_its_link()
    {
        var items = CricketLive.ParseItems(Feed);
        Assert.Equal(6, items.Count);
        Assert.Equal("1550711", items[0].Id);
        Assert.Equal("Sri Lanka", items[0].Match.Away.Team);
        Assert.Empty(CricketLive.ParseItems("<html><body>blocked</body>"));
    }

    [Fact]
    public void A_match_without_a_link_is_known_by_its_teams()
    {
        var items = CricketLive.ParseItems("<rss><channel><item><title>India 10/0 * v Australia</title></item></channel></rss>");
        Assert.Equal("India v Australia", Assert.Single(items).Id);
    }

    [Theory]
    [InlineData("India", true)]
    [InlineData("India Women", true)]
    [InlineData("IND", true)]
    [InlineData("IND W", true)]
    [InlineData("India A", false)]
    [InlineData("India Under-19s", false)]
    [InlineData("Hyderabad (India)", false)]
    [InlineData("Indiana", false)]
    [InlineData("Australia", false)]
    public void India_means_the_senior_mens_or_womens_side(string team, bool india) =>
        Assert.Equal(india, CricketLive.IsIndia(team));

    [Fact]
    public void Picks_the_India_match_in_play_and_no_other_teams()
    {
        var live = CricketLive.Pick(CricketLive.ParseItems(Feed))!;
        Assert.Equal("1559003", live.MatchId);
        Assert.Equal("India", live.Batting);
        Assert.Equal("Australia", live.Bowling);
        Assert.Equal(182, live.Runs);
        Assert.Equal(4, live.Wickets);
        Assert.Equal("239/10", live.BowlingScore);
    }

    [Fact]
    public void The_feed_has_no_overs_status_or_format_so_none_are_made_up()
    {
        var live = CricketLive.Pick(CricketLive.ParseItems(Feed))!;
        Assert.Null(live.Balls);
        Assert.Null(live.Status);
        Assert.Null(live.Format);
    }

    [Fact]
    public void India_bowling_is_still_Indias_match()
    {
        var live = CricketLive.Pick(new[] { Item("India 250/8 v Pakistan 31/2 *") })!;
        Assert.Equal("Pakistan", live.Batting);
        Assert.Equal("India", live.Bowling);
        Assert.Equal("250/8", live.BowlingScore);
    }

    [Fact]
    public void A_match_that_has_not_started_is_not_in_play()
    {
        Assert.Null(CricketLive.Pick(new[] { Item("India Women v England Women") }));
        Assert.Null(CricketLive.Pick(new[] { Item("India 287/4 v Australia") })); // nobody marked as batting
        Assert.Null(CricketLive.Pick(Array.Empty<CricketFeedItem>()));
    }

    [Fact]
    public void A_declared_innings_is_not_in_play()
    {
        Assert.Null(CricketLive.InPlay(Item("India 450/5d * v England")));
        // The second innings after a declaration is.
        var second = CricketLive.InPlay(Item("India 250/7d &amp; 12/1 * v England 180/10"))!;
        Assert.Equal((12, 1, 2), (second.Runs, second.Wickets, second.Innings));
    }

    [Fact]
    public void A_score_with_no_wickets_down_reads_as_none_down()
    {
        var live = CricketLive.InPlay(Item("India 32 * v Australia"))!;
        Assert.Equal((32, 0), (live.Runs, live.Wickets));
        Assert.Equal("IND 32/0", CricketText.Label(live));
    }

    [Fact]
    public void Keeps_following_the_same_match_when_two_are_on()
    {
        var items = new[] { Item("India 100/2 * v Australia", "men"), Item("India Women 90/1 * v England Women", "women") };
        Assert.Equal("men", CricketLive.Pick(items)!.MatchId);
        Assert.Equal("women", CricketLive.Pick(items, "women")!.MatchId);
        Assert.Equal("men", CricketLive.Pick(items, "gone")!.MatchId);
    }

    [Fact]
    public void Prefers_an_innings_still_going_over_one_that_is_all_out()
    {
        var items = new[] { Item("India 200/10 * v Australia", "over"), Item("India Women 90/1 * v England Women", "going") };
        Assert.Equal("going", CricketLive.Pick(items)!.MatchId);
    }

    [Theory]
    [InlineData("India", "IND")]
    [InlineData("India Women", "IND W")]
    [InlineData("United Arab Emirates", "UAE")]
    [InlineData("Hong Kong", "HK")]
    [InlineData("Papua New Guinea", "PNG")]
    [InlineData("Kenya", "KEN")]
    [InlineData("AUS", "AUS")]
    [InlineData("Hyderabad (India)", "HYD")]
    public void Every_team_gets_a_short_code(string team, string code) => Assert.Equal(code, CricketLive.Code(team));

    [Theory]
    [InlineData("live cricket on", LiveCricketCommand.On)]
    [InlineData("Live Cricket OFF.", LiveCricketCommand.Off)]
    [InlineData("cricket score", LiveCricketCommand.Score)]
    [InlineData("what's the cricket score?", LiveCricketCommand.Score)]
    [InlineData("live cricket score", LiveCricketCommand.Score)]
    [InlineData("live cricket", LiveCricketCommand.Score)]
    public void Understands_the_live_cricket_commands(string text, LiveCricketCommand expected) =>
        Assert.Equal(expected, CricketLive.ParseCommand(text));

    [Theory]
    [InlineData("cricket")]
    [InlineData("cricket score india vs australia 2011 final")]
    [InlineData("live cricket streaming")]
    [InlineData("ticker cricket")]
    [InlineData("how to score in cricket")]
    public void Leaves_ordinary_cricket_searches_alone(string text) => Assert.Null(CricketLive.ParseCommand(text));

    [Fact]
    public void Turning_the_cricket_ticker_on_turns_live_cricket_on()
    {
        Assert.True(CricketLive.TurnedOnBy(AmbientCommands.ParseTicker("ticker cricket")!));
        Assert.True(CricketLive.TurnedOnBy(AmbientCommands.ParseTicker("ticker cricket india")!));
        Assert.False(CricketLive.TurnedOnBy(AmbientCommands.ParseTicker("ticker cricket off")!));
        Assert.False(CricketLive.TurnedOnBy(AmbientCommands.ParseTicker("ticker stocks")!));
        Assert.False(CricketLive.TurnedOnBy(AmbientCommands.ParseTicker("ticker on")!));
    }
}

public class CricketTextTests
{
    static readonly CricketLiveScore Chase = CricketFake.Parse("IND 182/4 18.2 AUS T20 Need 24 from 10")!;
    static readonly CricketLiveScore FromFeed = new("7", "Australia", "India", 114, 3, BowlingScore: "434/9");

    [Fact]
    public void The_pet_shows_the_batting_sides_score_and_the_overs()
    {
        Assert.Equal("IND 182/4", CricketText.Label(Chase));
        Assert.Equal("18.2 ov", CricketText.Value(Chase));
    }

    [Fact]
    public void Without_overs_the_pet_says_who_they_are_playing()
    {
        Assert.Equal("AUS 114/3", CricketText.Label(FromFeed));
        Assert.Equal("v IND", CricketText.Value(FromFeed));
    }

    [Fact]
    public void The_pets_words_fit_the_notch()
    {
        var long1 = new CricketLiveScore("1", "India Women", "United Arab Emirates", 1234, 10, Balls: 725);
        Assert.True(CricketText.Label(long1).Length <= 13);
        Assert.True(CricketText.Value(long1).Length <= 9);
        Assert.True(CricketText.Value(long1 with { Balls = null }).Length <= 9);
    }

    [Theory]
    [InlineData("18.2", 110)]
    [InlineData("20", 120)]
    [InlineData("0.5", 5)]
    public void Overs_are_counted_in_balls(string overs, int balls)
    {
        Assert.Equal(balls, CricketText.Balls(overs));
        Assert.Equal(overs.Contains('.') ? overs : overs + ".0", CricketText.Overs(balls));
    }

    [Theory]
    [InlineData("18.6")]
    [InlineData("eighteen")]
    [InlineData("18.2.1")]
    [InlineData("")]
    public void Anything_else_is_not_overs(string text) => Assert.Null(CricketText.Balls(text));

    [Fact]
    public void India_is_named_first_whoever_is_batting()
    {
        Assert.Equal("IND v AUS", CricketText.Teams(Chase));
        Assert.Equal("IND v AUS", CricketText.Teams(FromFeed));
        Assert.Equal("India v Australia", CricketText.Teams(FromFeed, codes: false));
    }

    [Fact]
    public void The_format_is_only_named_when_it_is_known()
    {
        Assert.Equal("IND v AUS · T20", CricketText.Kicker(Chase));
        Assert.Equal("IND v AUS", CricketText.Kicker(FromFeed));
    }

    [Fact]
    public void The_line_is_the_feeds_own_words_or_the_other_sides_score()
    {
        Assert.Equal("Need 24 from 10", CricketText.Line(Chase));
        Assert.Equal("India 434/9", CricketText.Line(FromFeed));
        Assert.Equal("India batting", CricketText.Line(new CricketLiveScore("1", "India", "Australia", 4, 0)));
    }
}

public class CricketMomentTests
{
    static readonly CricketLiveScore Before = CricketFake.Parse("IND 182/4 18.2 AUS Need 24 from 10")!;

    [Fact]
    public void Four_more_runs_off_one_ball_is_a_four() =>
        Assert.Equal(CricketMoment.Four, CricketMoments.Between(Before, Before with { Runs = 186, Balls = 111 }));

    [Fact]
    public void Six_more_runs_off_one_ball_is_a_six() =>
        Assert.Equal(CricketMoment.Six, CricketMoments.Between(Before, Before with { Runs = 188, Balls = 111 }));

    [Fact]
    public void Four_more_runs_over_several_balls_could_be_anything_so_nothing_is_said()
    {
        Assert.Equal(CricketMoment.None, CricketMoments.Between(Before, Before with { Runs = 186, Balls = 113 }));
        Assert.Equal(CricketMoment.None, CricketMoments.Between(Before, Before with { Runs = 188, Balls = 112 }));
    }

    [Fact]
    public void Without_a_ball_count_a_jump_in_runs_is_never_called_a_boundary()
    {
        var before = new CricketLiveScore("7", "India", "Australia", 182, 4);
        Assert.Equal(CricketMoment.None, CricketMoments.Between(before, before with { Runs = 186 }));
        Assert.Equal(CricketMoment.None, CricketMoments.Between(before, before with { Runs = 188 }));
    }

    [Fact]
    public void Extras_that_do_not_count_as_a_ball_say_nothing() =>
        Assert.Equal(CricketMoment.None, CricketMoments.Between(Before, Before with { Runs = 187 }));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void Other_scoring_shots_are_not_moments(int runs) =>
        Assert.Equal(CricketMoment.None, CricketMoments.Between(Before, Before with { Runs = 182 + runs, Balls = 111 }));

    [Fact]
    public void A_wicket_shows_in_the_wicket_count_however_many_balls_went_by()
    {
        Assert.Equal(CricketMoment.Wicket, CricketMoments.Between(Before, Before with { Wickets = 5, Balls = 111 }));
        Assert.Equal(CricketMoment.Wicket, CricketMoments.Between(Before, Before with { Wickets = 5, Runs = 190, Balls = 115 }));
        var noBalls = new CricketLiveScore("7", "India", "Australia", 182, 4);
        Assert.Equal(CricketMoment.Wicket, CricketMoments.Between(noBalls, noBalls with { Wickets = 5, Runs = 183 }));
    }

    [Fact]
    public void The_same_score_twice_is_not_a_moment() => Assert.Equal(CricketMoment.None, CricketMoments.Between(Before, Before));

    [Fact]
    public void A_new_innings_is_not_a_moment()
    {
        // The other side comes in to bat.
        var chase = new CricketLiveScore(Before.MatchId, "AUS", "IND", 6, 1, Balls: 1);
        Assert.Equal(CricketMoment.None, CricketMoments.Between(Before with { Runs = 0, Wickets = 0, Balls = 0 }, chase));
        // The same side bats again (a follow-on): its score starts over.
        var first = new CricketLiveScore("9", "India", "England", 180, 9, Innings: 1, BowlingScore: "450/5d");
        Assert.Equal(CricketMoment.None, CricketMoments.Between(first, first with { Runs = 4, Wickets = 0, Innings = 2 }));
        Assert.Equal(CricketMoment.None, CricketMoments.Between(first, first with { Wickets = 10, Innings = 2 }));
        // The other side's score changed underneath: not the same innings any more.
        Assert.Equal(CricketMoment.None, CricketMoments.Between(first, first with { Wickets = 10, BowlingScore = "450/5d & 10/0" }));
    }

    [Fact]
    public void A_new_match_is_not_a_moment()
    {
        var next = Before with { MatchId = "tomorrow", Wickets = 5, Runs = 186, Balls = 111 };
        Assert.Equal(CricketMoment.None, CricketMoments.Between(Before, next));
        Assert.Equal(CricketMoment.None, CricketMoments.Between(null, Before));
        Assert.Equal(CricketMoment.None, CricketMoments.Between(Before, null));
    }

    [Fact]
    public void A_corrected_score_is_not_a_moment()
    {
        Assert.Equal(CricketMoment.None, CricketMoments.Between(Before, Before with { Runs = 178, Balls = 111 }));
        Assert.Equal(CricketMoment.None, CricketMoments.Between(Before, Before with { Wickets = 3 }));
        Assert.Equal(CricketMoment.None, CricketMoments.Between(Before, Before with { Wickets = 5, Balls = 100 }));
    }

    [Fact]
    public void The_pet_is_happy_when_India_scores_and_wide_eyed_at_a_wicket()
    {
        Assert.Equal(PetFace.Happy, CricketMoments.Face(CricketMoment.Six, indiaBatting: true));
        Assert.Equal(PetFace.Happy, CricketMoments.Face(CricketMoment.Four, indiaBatting: true));
        Assert.Equal(PetFace.Focused, CricketMoments.Face(CricketMoment.Six, indiaBatting: false));
        Assert.Equal(PetFace.Notify, CricketMoments.Face(CricketMoment.Wicket, indiaBatting: true));
        Assert.Equal(PetFace.Notify, CricketMoments.Face(CricketMoment.Wicket, indiaBatting: false));
        Assert.Equal(PetFace.Focused, CricketMoments.Face(CricketMoment.None, indiaBatting: true));
    }

    [Fact]
    public void Moments_are_written_loudly()
    {
        Assert.Equal("FOUR!", CricketText.Moment(CricketMoment.Four));
        Assert.Equal("SIX!", CricketText.Moment(CricketMoment.Six));
        Assert.Equal("WICKET", CricketText.Moment(CricketMoment.Wicket));
        Assert.Equal("", CricketText.Moment(CricketMoment.None));
    }
}

public class CricketWatchTests
{
    static readonly DateTime T0 = new(2026, 11, 10, 14, 0, 0, DateTimeKind.Utc);

    static CricketLiveScore India(int runs, int wickets, string id = "7") => new(id, "India", "Australia", runs, wickets, BowlingScore: "239/10");

    [Fact]
    public void Nothing_in_play_is_idle_and_checked_every_ten_minutes()
    {
        var watch = new CricketWatch();
        watch.Observe(null, T0);
        Assert.Equal(CricketPhase.Idle, watch.Phase(T0));
        Assert.Null(watch.Live(T0));
        Assert.Equal(TimeSpan.FromMinutes(10), watch.NextPoll(T0));
    }

    [Fact]
    public void A_match_just_found_is_watched_closely_but_not_called_live_until_its_score_moves()
    {
        // The feed leaves its batting mark on finished matches, so the mark alone proves nothing.
        var watch = new CricketWatch();
        watch.Observe(India(182, 4), T0);
        Assert.Equal(CricketPhase.Watching, watch.Phase(T0));
        Assert.Null(watch.Live(T0));
        Assert.Equal(TimeSpan.FromSeconds(30), watch.NextPoll(T0));

        watch.Observe(India(183, 4), T0.AddSeconds(30));
        Assert.Equal(CricketPhase.Live, watch.Phase(T0.AddSeconds(30)));
        Assert.Equal(183, watch.Live(T0.AddSeconds(30))!.Runs);
        Assert.Equal(TimeSpan.FromSeconds(30), watch.NextPoll(T0.AddSeconds(30)));
    }

    [Fact]
    public void A_match_that_never_moves_drops_back_to_the_slow_check()
    {
        var watch = new CricketWatch();
        watch.Observe(India(182, 4), T0);
        var later = T0 + CricketWatch.Quiet + TimeSpan.FromSeconds(30);
        watch.Observe(India(182, 4), later);
        Assert.Equal(CricketPhase.Watching, watch.Phase(later));
        Assert.Equal(TimeSpan.FromMinutes(10), watch.NextPoll(later));
    }

    [Fact]
    public void A_live_match_goes_quiet_when_the_score_stands_still_and_comes_back_with_the_next_run()
    {
        var watch = new CricketWatch();
        watch.Observe(India(182, 4), T0);
        watch.Observe(India(183, 4), T0.AddSeconds(30));
        var still = T0.AddSeconds(30) + CricketWatch.Quiet;
        Assert.Equal(CricketPhase.Live, watch.Phase(still));
        var quiet = still.AddSeconds(1);
        Assert.Equal(CricketPhase.Watching, watch.Phase(quiet));
        Assert.Null(watch.Live(quiet));
        Assert.Equal(TimeSpan.FromMinutes(10), watch.NextPoll(quiet));

        var back = quiet.AddMinutes(10);
        watch.Observe(India(190, 4), back);
        Assert.Equal(CricketPhase.Live, watch.Phase(back));
    }

    [Fact]
    public void A_wicket_between_two_polls_is_a_moment()
    {
        var watch = new CricketWatch();
        Assert.Equal(CricketMoment.None, watch.Observe(India(182, 4), T0));
        Assert.Equal(CricketMoment.None, watch.Observe(India(183, 4), T0.AddSeconds(30)));
        Assert.Equal(CricketMoment.Wicket, watch.Observe(India(183, 5), T0.AddSeconds(60)));
        Assert.Equal(CricketMoment.None, watch.Observe(India(183, 5), T0.AddSeconds(90)));
    }

    [Fact]
    public void A_wicket_found_after_a_long_gap_is_old_news()
    {
        var watch = new CricketWatch();
        watch.Observe(India(182, 4), T0);
        var later = T0 + CricketWatch.Fresh + TimeSpan.FromSeconds(1);
        Assert.Equal(CricketMoment.None, watch.Observe(India(190, 5), later));
        // It is live again all the same.
        Assert.Equal(CricketPhase.Live, watch.Phase(later));
    }

    [Fact]
    public void A_new_match_starts_over_without_a_moment()
    {
        var watch = new CricketWatch();
        watch.Observe(India(182, 4), T0);
        watch.Observe(India(183, 4), T0.AddSeconds(30));
        Assert.Equal(CricketMoment.None, watch.Observe(India(183, 5, id: "8"), T0.AddSeconds(60)));
        Assert.Equal(CricketPhase.Watching, watch.Phase(T0.AddSeconds(60)));
    }

    [Fact]
    public void The_last_wicket_is_a_moment_and_the_all_out_score_leaves_after_a_minute()
    {
        var watch = new CricketWatch();
        watch.Observe(India(200, 9), T0);
        Assert.Equal(CricketMoment.Wicket, watch.Observe(India(200, 10), T0.AddSeconds(30)));
        Assert.Equal(CricketPhase.Live, watch.Phase(T0.AddSeconds(60)));
        Assert.Equal(CricketPhase.Watching, watch.Phase(T0.AddSeconds(30) + CricketWatch.AllOutShown + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void The_match_ending_in_the_feed_ends_it_here()
    {
        var watch = new CricketWatch();
        watch.Observe(India(182, 4), T0);
        watch.Observe(India(183, 4), T0.AddSeconds(30));
        watch.Observe(null, T0.AddSeconds(60));
        Assert.Equal(CricketPhase.Idle, watch.Phase(T0.AddSeconds(60)));
        Assert.Null(watch.Score);
    }

    [Fact]
    public void A_few_failed_polls_keep_the_score_but_a_long_outage_lets_it_go()
    {
        var watch = new CricketWatch();
        watch.Observe(India(182, 4), T0);
        watch.Observe(India(183, 4), T0.AddSeconds(30));
        watch.Missed(T0.AddSeconds(90));
        Assert.Equal(CricketPhase.Live, watch.Phase(T0.AddSeconds(90)));
        var gone = T0.AddSeconds(30) + CricketWatch.Lost + TimeSpan.FromSeconds(1);
        watch.Missed(gone);
        Assert.Equal(CricketPhase.Idle, watch.Phase(gone));
    }
}

public class CricketFakeTests
{
    [Fact]
    public void Reads_a_made_up_match_from_one_line()
    {
        var m = CricketFake.Parse("IND 182/4 18.2 AUS Need 24 from 10")!;
        Assert.Equal(("IND", "AUS", 182, 4, 110), (m.Batting, m.Bowling, m.Runs, m.Wickets, m.Balls!.Value));
        Assert.Equal("Need 24 from 10", m.Status);
        Assert.Null(m.Format);
        Assert.True(CricketLive.IsIndia(m.Batting));
    }

    [Fact]
    public void The_status_and_format_are_optional()
    {
        var bare = CricketFake.Parse("AUS 12 2.0 IND-W")!;
        Assert.Equal((12, 0, 12, "IND W"), (bare.Runs, bare.Wickets, bare.Balls!.Value, bare.Bowling));
        Assert.Null(bare.Status);
        var odi = CricketFake.Parse("IND 250/3 40.0 PAK ODI India batting first")!;
        Assert.Equal("ODI", odi.Format);
        Assert.Equal("India batting first", odi.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("IND 182/4 18.2")]
    [InlineData("IND lots 18.2 AUS")]
    [InlineData("IND 182/11 18.2 AUS")]
    [InlineData("IND 182/4 18.7 AUS")]
    public void Refuses_a_line_that_is_not_a_match(string line) => Assert.Null(CricketFake.Parse(line));

    [Fact]
    public void A_ball_moves_the_score_the_overs_and_the_chase()
    {
        var m = CricketFake.Parse("IND 182/4 18.2 AUS Need 24 from 10")!;
        var six = CricketFake.Ball(m, "6")!;
        Assert.Equal((188, 4, 111, "Need 18 from 9"), (six.Runs, six.Wickets, six.Balls!.Value, six.Status));
        Assert.Equal(CricketMoment.Six, CricketMoments.Between(m, six));
        var out1 = CricketFake.Ball(six, "w")!;
        Assert.Equal((188, 5, 112, "Need 18 from 8"), (out1.Runs, out1.Wickets, out1.Balls!.Value, out1.Status));
        Assert.Equal(CricketMoment.Wicket, CricketMoments.Between(six, out1));
        Assert.Equal(CricketMoment.None, CricketMoments.Between(out1, CricketFake.Ball(out1, "1")));
        Assert.Equal(CricketMoment.Four, CricketMoments.Between(out1, CricketFake.Ball(out1, "4")));
    }

    [Fact]
    public void The_chase_ends_when_the_runs_or_the_balls_run_out()
    {
        Assert.Equal("IND won", CricketFake.Ball(CricketFake.Parse("IND 200/4 19.4 AUS Need 4 from 2")!, "4")!.Status);
        Assert.Equal("AUS won", CricketFake.Ball(CricketFake.Parse("IND 200/4 19.5 AUS Need 4 from 1")!, "1")!.Status);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("four")]
    [InlineData("")]
    [InlineData("-1")]
    public void Refuses_a_ball_that_cannot_be_bowled(string ball) =>
        Assert.Null(CricketFake.Ball(CricketFake.Parse("IND 182/4 18.2 AUS")!, ball));

    [Fact]
    public void Nobody_bats_on_after_being_all_out() =>
        Assert.Null(CricketFake.Ball(CricketFake.Parse("IND 182/10 18.2 AUS")!, "1"));
}
