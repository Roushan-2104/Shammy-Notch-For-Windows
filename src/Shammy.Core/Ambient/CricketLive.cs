using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Shammy.Core.Notch;

namespace Shammy.Core.Ambient;

/// <summary>
/// A match in play as one poll saw it: who is batting and the innings in progress. The public feed gives only the
/// two score lines, so <see cref="Balls"/>, <see cref="Status"/> and <see cref="Format"/> are null for a real match
/// (a made-up test match fills them); nothing here is guessed.
/// </summary>
/// <param name="MatchId">The feed's own id for the match, so two polls can be told to be the same game.</param>
/// <param name="Innings">How many innings the batting side has on the board (2 = its second innings).</param>
/// <param name="Balls">Balls bowled in this innings (18.2 overs = 110), when known.</param>
/// <param name="BowlingScore">The other side's score as the feed writes it ("239/10", "32 &amp; 346/10").</param>
/// <param name="Status">The line a scorer would add ("Need 24 from 10", "India won by 5 wickets"), when known.</param>
/// <param name="Format">"T20", "ODI", "Test", when known.</param>
public sealed record CricketLiveScore(
    string MatchId, string Batting, string Bowling, int Runs, int Wickets, int Innings = 1,
    int? Balls = null, string? BowlingScore = null, string? Status = null, string? Format = null)
{
    public bool AllOut => Wickets >= 10;
}

/// <summary>One feed item: the match and the id from its link.</summary>
public sealed record CricketFeedItem(string Id, CricketMatch Match);

public enum CricketMoment { None, Four, Six, Wicket }

public enum CricketPhase
{
    /// <summary>No India match in play.</summary>
    Idle,
    /// <summary>The feed marks one as in play, but its score hasn't been seen to move (just found, a break, or it ended).</summary>
    Watching,
    /// <summary>The score is moving: the notch shows it.</summary>
    Live,
}

public enum LiveCricketCommand { On, Off, Score }

/// <summary>Reading the live-scores feed for the notch's live match: which match is India's, and how it is named.</summary>
public static class CricketLive
{
    /// <summary>How often to read the feed while a match is live, and otherwise.</summary>
    public static readonly TimeSpan LiveEvery = TimeSpan.FromSeconds(30), IdleEvery = TimeSpan.FromMinutes(10);

    static readonly Regex MatchIdRx = new(@"/match/(\d+)", RegexOptions.Compiled);
    static readonly Regex InningsRx = new(@"^(?<runs>\d+)(?:/(?<wickets>\d+))?$", RegexOptions.Compiled);
    static readonly Regex Parens = new(@"\s*\([^)]*\)", RegexOptions.Compiled);
    static readonly Regex CommandRx = new(
        @"^(?:live cricket (?<state>on|off)|(?<score>(?:(?:what's|whats|what is|show|show me) )?(?:the )?(?:live )?cricket score|live cricket))$",
        RegexOptions.Compiled);

    /// <summary>The feed's matches with their ids (the number in each item's link; the two names when there is none).</summary>
    public static IReadOnlyList<CricketFeedItem> ParseItems(string xml)
    {
        var items = new List<CricketFeedItem>();
        try
        {
            foreach (var item in XDocument.Parse(xml).Descendants("item"))
            {
                if ((string?)item.Element("title") is not { } title || CricketFeed.ParseTitle(title) is not { } match) continue;
                var link = (string?)item.Element("guid") ?? (string?)item.Element("link") ?? "";
                var id = MatchIdRx.Match(link) is { Success: true } m ? m.Groups[1].Value : $"{match.Home.Team} v {match.Away.Team}";
                items.Add(new CricketFeedItem(id, match));
            }
        }
        catch (XmlException) { }
        return items;
    }

    /// <summary>India's senior men's or women's side ("India", "India Women"); not India A, the Under-19s or a state side.</summary>
    public static bool IsIndia(string team) => CricketFeed.ShortTeam(Name(team)) is "IND" or "IND W";

    public static bool InvolvesIndia(CricketMatch m) => IsIndia(m.Home.Team) || IsIndia(m.Away.Team);

    /// <summary>The team's name without the feed's bracketed notes: "Hyderabad (India)" → "Hyderabad".</summary>
    public static string Name(string team) => Parens.Replace(team, "").Trim();

    /// <summary>A short code for the notch: "India" → "IND", "India Women" → "IND W", "Hong Kong" → "HK", "Kenya" → "KEN".</summary>
    public static string Code(string team)
    {
        var name = Name(team);
        var known = CricketFeed.ShortTeam(name);
        if (!string.Equals(known, name, StringComparison.Ordinal)) return known;
        // Already a code ("IND", "IND W" from the test pipe).
        if (name.Length <= 5 && name.Any(char.IsLetter) && name.All(c => char.IsUpper(c) || c == ' ')) return name;
        var words = name.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return name;
        var code = words.Length > 1 ? string.Concat(words.Take(4).Select(w => w[0])) : words[0][..Math.Min(3, words[0].Length)];
        return code.ToUpperInvariant();
    }

    /// <summary>
    /// The score in play in a feed item: the side the feed marks as batting and the innings it is in. Null when
    /// nobody is batting, the batting side has no score yet, or it has declared.
    /// </summary>
    public static CricketLiveScore? InPlay(CricketFeedItem item)
    {
        var m = item.Match;
        var (batting, bowling) = m.Home.Batting ? (m.Home, m.Away) : (m.Away, m.Home);
        if (!batting.Batting || batting.Score is null) return null;
        var innings = InningsRx.Match(CricketFeed.CurrentInnings(batting));
        if (!innings.Success || !int.TryParse(innings.Groups["runs"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int runs)) return null;
        int wickets = 0;
        if (innings.Groups["wickets"].Success && !int.TryParse(innings.Groups["wickets"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out wickets)) return null;
        int count = batting.Score.Split('&', StringSplitOptions.RemoveEmptyEntries).Length;
        return new CricketLiveScore(item.Id, Name(batting.Team), Name(bowling.Team), runs, wickets, count, BowlingScore: bowling.Score);
    }

    /// <summary>
    /// India's match in play, if any: the one already being followed while it is still there (two can be on at
    /// once, the men's and the women's), else the first whose innings is still going.
    /// </summary>
    public static CricketLiveScore? Pick(IEnumerable<CricketFeedItem> items, string? followingId = null)
    {
        var india = items.Where(i => InvolvesIndia(i.Match)).Select(InPlay).Where(s => s is not null).Select(s => s!).ToList();
        return india.FirstOrDefault(s => s.MatchId == followingId) ?? india.FirstOrDefault(s => !s.AllOut) ?? india.FirstOrDefault();
    }

    /// <summary>India's matches as the feed lists them, in play or not, for the state line.</summary>
    public static IReadOnlyList<string> IndiaListed(IEnumerable<CricketFeedItem> items) =>
        items.Where(i => InvolvesIndia(i.Match)).Select(i => CricketFeed.Format(i.Match) + (i.Match.Live ? " *" : "")).ToList();

    /// <summary>"live cricket on", "live cricket off", "cricket score"; null for anything else.</summary>
    public static LiveCricketCommand? ParseCommand(string text)
    {
        var t = Regex.Replace(text.Trim().TrimEnd('.', '!', '?').Replace('’', '\'').ToLowerInvariant(), @"\s+", " ");
        var m = CommandRx.Match(t);
        if (!m.Success) return null;
        if (m.Groups["score"].Success) return LiveCricketCommand.Score;
        return m.Groups["state"].Value == "on" ? LiveCricketCommand.On : LiveCricketCommand.Off;
    }

    /// <summary>Turning the cricket ticker on ("ticker cricket", "ticker cricket india") turns the live match on with it.</summary>
    public static bool TurnedOnBy(TickerCommand command) => command is { Action: TickerAction.Enable, Kind: TickerKind.Cricket };
}

/// <summary>How a live match is written in the notch and on its card.</summary>
public static class CricketText
{
    /// <summary>The pet's label: "IND 182/4".</summary>
    public static string Label(CricketLiveScore s) => $"{CricketLive.Code(s.Batting)} {Score(s)}";

    /// <summary>The pet's value: the overs when known ("18.2 ov"), else who they are playing ("v AUS").</summary>
    public static string Value(CricketLiveScore s) => s.Balls is { } balls ? $"{Overs(balls)} ov" : $"v {CricketLive.Code(s.Bowling)}";

    public static string Score(CricketLiveScore s) => $"{s.Runs}/{s.Wickets}";

    /// <summary>110 balls → "18.2".</summary>
    public static string Overs(int balls) => string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, balls) / 6}.{Math.Max(0, balls) % 6}");

    /// <summary>"18.2" → 110 balls, "20" → 120; null for anything that isn't overs.</summary>
    public static int? Balls(string overs)
    {
        var m = Regex.Match(overs.Trim(), @"^(\d{1,3})(?:\.([0-5]))?$");
        if (!m.Success) return null;
        return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 6
               + (m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0);
    }

    /// <summary>India first when India is playing, else the batting side: "IND v AUS" (or the full names).</summary>
    public static string Teams(CricketLiveScore s, bool codes = true)
    {
        var (first, second) = CricketLive.IsIndia(s.Bowling) && !CricketLive.IsIndia(s.Batting) ? (s.Bowling, s.Batting) : (s.Batting, s.Bowling);
        return codes ? $"{CricketLive.Code(first)} v {CricketLive.Code(second)}" : $"{first} v {second}";
    }

    /// <summary>"IND v AUS · T20", or just the teams when the format isn't known.</summary>
    public static string Kicker(CricketLiveScore s) => s.Format is { Length: > 0 } f ? $"{Teams(s)} · {f}" : Teams(s);

    /// <summary>
    /// The line under the score: what the feed says about the match ("Need 24 from 10") when it says anything,
    /// else the one other fact it has, the other side's score ("Australia 239/10"), else who is batting.
    /// </summary>
    public static string Line(CricketLiveScore s) =>
        s.Status is { Length: > 0 } status ? status
        : s.BowlingScore is { Length: > 0 } other ? $"{s.Bowling} {other}"
        : $"{s.Batting} batting";

    public static string Moment(CricketMoment moment) => moment switch
    {
        CricketMoment.Four => "FOUR!",
        CricketMoment.Six => "SIX!",
        CricketMoment.Wicket => "WICKET",
        _ => "",
    };
}

/// <summary>Big moments (a four, a six, a wicket), told by comparing one poll with the one before.</summary>
public static class CricketMoments
{
    /// <summary>How long a moment takes the pet's value and face.</summary>
    public static readonly TimeSpan Shown = TimeSpan.FromSeconds(4);

    /// <summary>
    /// What happened between two polls of the same innings. A wicket shows in the wicket count however many balls
    /// went by. A boundary can only be told when exactly one ball was bowled and the score rose by 4 or 6; with
    /// several balls between polls, or no ball count at all, 4 more runs could be anything, so nothing is said.
    /// A new match, a new innings or a corrected score is never a moment.
    /// </summary>
    public static CricketMoment Between(CricketLiveScore? before, CricketLiveScore? after)
    {
        if (before is null || after is null) return CricketMoment.None;
        if (before.MatchId != after.MatchId || before.Batting != after.Batting || before.Innings != after.Innings
            || before.BowlingScore != after.BowlingScore) return CricketMoment.None;
        if (after.Runs < before.Runs || after.Wickets < before.Wickets) return CricketMoment.None;
        if (before.Balls is { } b0 && after.Balls is { } b1 && b1 < b0) return CricketMoment.None;
        if (after.Wickets > before.Wickets) return CricketMoment.Wicket;
        if (before.Balls is not { } from || after.Balls is not { } to || to - from != 1) return CricketMoment.None;
        return (after.Runs - before.Runs) switch
        {
            4 => CricketMoment.Four,
            6 => CricketMoment.Six,
            _ => CricketMoment.None,
        };
    }

    /// <summary>
    /// The pet's face for a moment: happy when India is batting and scored, wide eyes for any wicket; a boundary
    /// hit against India keeps the watching face.
    /// </summary>
    public static PetFace Face(CricketMoment moment, bool indiaBatting) => moment switch
    {
        CricketMoment.Wicket => PetFace.Notify,
        CricketMoment.Four or CricketMoment.Six when indiaBatting => PetFace.Happy,
        _ => PetFace.Focused,
    };
}

/// <summary>
/// Follows India's match from poll to poll and decides when it is live. The feed keeps its "batting" mark on a
/// match for a while after it ends (and through breaks), and has no result line, so the mark alone can't be
/// trusted: a match counts as live only once its score has been seen to move, and stops being live when the score
/// has stood still for <see cref="Quiet"/> (a minute once the side is all out). It comes back with the next run.
/// </summary>
public sealed class CricketWatch
{
    /// <summary>No change for this long (longer than a drinks break or a run of maidens) means play has stopped.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(15);

    /// <summary>An all-out score stays just long enough to be read.</summary>
    public static readonly TimeSpan AllOutShown = TimeSpan.FromMinutes(1);

    /// <summary>Two polls further apart than this (the PC slept, the network dropped) can't tell what just happened.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);

    /// <summary>Without a successful poll for this long the score shown is no longer the score.</summary>
    public static readonly TimeSpan Lost = TimeSpan.FromMinutes(3);

    DateTime _seenUtc, _changedUtc;
    bool _moved;

    /// <summary>The match being followed, live or not; null when India has nothing in play.</summary>
    public CricketLiveScore? Score { get; private set; }

    /// <summary>Takes one poll's answer (null: no India match in play) and says what just happened, if it can tell.</summary>
    public CricketMoment Observe(CricketLiveScore? inPlay, DateTime utcNow)
    {
        if (inPlay is null)
        {
            Reset();
            return CricketMoment.None;
        }
        if (Score is null || Score.MatchId != inPlay.MatchId)
        {
            Score = inPlay;
            _seenUtc = _changedUtc = utcNow;
            _moved = false;
            return CricketMoment.None;
        }
        var moment = CricketMoment.None;
        if (Score != inPlay)
        {
            if (utcNow - _seenUtc <= Fresh) moment = CricketMoments.Between(Score, inPlay);
            _moved = true;
            _changedUtc = utcNow;
        }
        Score = inPlay;
        _seenUtc = utcNow;
        return moment;
    }

    /// <summary>A poll failed (offline, the feed is down): after <see cref="Lost"/> the match is let go.</summary>
    public void Missed(DateTime utcNow)
    {
        if (Score is not null && utcNow - _seenUtc > Lost) Reset();
    }

    public void Reset()
    {
        Score = null;
        _moved = false;
    }

    public CricketPhase Phase(DateTime utcNow) =>
        Score is not { } s ? CricketPhase.Idle
        : _moved && utcNow - _changedUtc <= (s.AllOut ? AllOutShown : Quiet) ? CricketPhase.Live
        : CricketPhase.Watching;

    /// <summary>The live match to show right now, or null.</summary>
    public CricketLiveScore? Live(DateTime utcNow) => Phase(utcNow) == CricketPhase.Live ? Score : null;

    /// <summary>
    /// When to read the feed next: every 30 s while live, and for the first quiet stretch after a match was found
    /// or stopped moving (so its next run is seen at once); every 10 minutes otherwise.
    /// </summary>
    public TimeSpan NextPoll(DateTime utcNow) => Phase(utcNow) switch
    {
        CricketPhase.Live => CricketLive.LiveEvery,
        CricketPhase.Watching when utcNow - _changedUtc <= Quiet => CricketLive.LiveEvery,
        _ => CricketLive.IdleEvery,
    };
}

/// <summary>A made-up live match for the test pipe, so the activity, its moments and the card can be seen without one.</summary>
public static class CricketFake
{
    public const string MatchId = "fake";

    static readonly string[] Formats = { "T20", "T20I", "ODI", "Test" };
    static readonly Regex NeedRx = new(@"^Need (\d+) from (\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// "IND 182/4 18.2 AUS Need 24 from 10": the batting side, its score, the overs, the other side, then the
    /// status line (which may start with a format: "T20 Need 24 from 10"). Null when it doesn't read as that.
    /// </summary>
    public static CricketLiveScore? Parse(string arg)
    {
        var words = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 4) return null;
        var score = Regex.Match(words[1], @"^(\d{1,4})(?:/(\d{1,2}))?$");
        if (!score.Success || CricketText.Balls(words[2]) is not { } balls) return null;
        int wickets = score.Groups[2].Success ? int.Parse(score.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        if (wickets > 10) return null;
        var rest = words.Skip(4).ToList();
        string? format = rest.Count > 0 && Formats.Contains(rest[0]) ? rest[0] : null;
        if (format is not null) rest.RemoveAt(0);
        return new CricketLiveScore(MatchId, Team(words[0]), Team(words[3]), int.Parse(score.Groups[1].Value, CultureInfo.InvariantCulture), wickets,
            Balls: balls, Status: rest.Count > 0 ? string.Join(' ', rest) : null, Format: format);

        // "IND-W" for a two-word side, since the pipe splits on spaces.
        static string Team(string word) => word.Replace('-', ' ').Replace('_', ' ').Trim();
    }

    /// <summary>
    /// The match one ball later: "0".."6" runs off it, or "W" for a wicket. A "Need 24 from 10" status counts
    /// down with it. Null for anything else, or when the side is already all out.
    /// </summary>
    public static CricketLiveScore? Ball(CricketLiveScore match, string ball)
    {
        ball = ball.Trim().ToUpperInvariant();
        if (match.AllOut) return null;
        int runs = 0;
        bool wicket = ball == "W";
        if (!wicket && (ball.Length != 1 || !int.TryParse(ball, NumberStyles.None, CultureInfo.InvariantCulture, out runs) || runs > 6)) return null;
        var status = match.Status;
        if (status is not null && NeedRx.Match(status) is { Success: true } need)
        {
            int left = int.Parse(need.Groups[1].Value, CultureInfo.InvariantCulture) - runs;
            int from = int.Parse(need.Groups[2].Value, CultureInfo.InvariantCulture) - 1;
            status = left <= 0 ? $"{match.Batting} won" : from <= 0 ? $"{match.Bowling} won" : $"Need {left} from {from}";
        }
        return match with
        {
            Runs = match.Runs + runs,
            Wickets = match.Wickets + (wicket ? 1 : 0),
            Balls = (match.Balls ?? 0) + 1,
            Status = status,
        };
    }
}
