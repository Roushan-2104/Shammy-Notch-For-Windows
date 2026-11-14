using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Ambient;
using Shammy.Views;
using Shammy.Views.Cards;

namespace Shammy.Modules.Ambient;

/// <summary>
/// The cricket card on the Focus Wall, on the rail only while a match is live: a tile with the score as a poster on
/// a dark pitch green; focused, the teams, the line and a control to open the detail. The detail (the card family's
/// shape) has the teams and the format, the batting side's score as the hero, the overs and the line the feed gives. It shows only what it was given: the
/// public feed has no overs, no result line, no format and no ball-by-ball, so for a real match those places stay
/// empty and the line is the other side's score. The last-six-balls chips appear only with ball-by-ball data.
/// </summary>
internal sealed class LiveCricketCard
{
    /// <summary>Above a song (50) and a running timer (40), below a meeting about to start (80).</summary>
    const int LiveNow = 55;

    const int BallsShown = 6;

    static readonly Brush Pitch = MakeField();

    readonly WallTile _tile = new();
    readonly TrackedText _score = CardKit.Poster("", 34), _teamsRest = CardKit.Label("");
    readonly TextBlock _teams, _line;

    CricketLiveScore? _match;
    IReadOnlyList<string> _balls = Array.Empty<string>();
    Detail? _detail;

    public LiveCricketCard()
    {
        _tile.Field = Pitch;
        _tile.Art.Children.Add(CardKit.At(_score, HorizontalAlignment.Left, VerticalAlignment.Top, WallTile.Pad, WallTile.Pad));

        _teamsRest.Margin = new Thickness(0, 6, 0, 0);
        var rest = new StackPanel { Margin = new Thickness(WallTile.Pad, WallTile.Pad + 34, WallTile.Pad, 0), VerticalAlignment = VerticalAlignment.Top };
        rest.Children.Add(_teamsRest);
        _tile.Rest.Children.Add(rest);

        var words = WallTile.Words("", "", out _teams, out _line, Ui.Tokens.InkOnArt);
        words.Margin = new Thickness(WallTile.Pad, WallTile.Pad + 39, WallTile.Pad, 0);
        words.VerticalAlignment = VerticalAlignment.Top;
        _tile.Lit.Children.Add(words);
        _tile.Lit.Children.Add(WallTile.OpenButton("Open the cricket score", () => Open?.Invoke()));
        Show(null, _balls);
    }

    /// <summary>Opens the cricket detail in the notch (the tile's and the peek's primary control).</summary>
    public Action? Open { get; set; }

    public FrameworkElement Compact => _tile;

    public bool IsLive => _match is not null;

    public int Live => _match is null ? 0 : LiveNow;

    public CricketLiveScore? Match => _match;

    /// <summary>The peek: "IND v AUS · T20 · Live", "IND 182/4 (18.2)", "Need 24 from 10".</summary>
    public WallPeek? Peek() => _match is { } m
        ? new WallPeek($"{CricketText.Kicker(m)} · Live", ScoreLine(m), CricketText.Line(m), Pitch, Open is { } open ? new WallAction("Score", open) : null)
        : null;

    /// <param name="balls">The last balls, oldest first ("1", "4", "W"); empty when there is no ball-by-ball data.</param>
    public void Show(CricketLiveScore? match, IReadOnlyList<string> balls)
    {
        _match = match;
        _balls = balls.Count > BallsShown ? balls.Skip(balls.Count - BallsShown).ToList() : balls.ToList();
        if (_detail is { IsLoaded: true } detail) detail.Show(match, _balls);
        if (match is not { } m)
        {
            _score.Text = "";
            _teamsRest.Text = _teams.Text = "Cricket";
            _line.Text = "No live match";
            CardKit.Name(_tile, "Cricket: no live match");
            return;
        }
        string score = CricketText.Score(m);
        _score.Text = score;
        _score.FontSize = score.Length > 6 ? 28 : 34;
        _teamsRest.Text = _teams.Text = CricketText.Teams(m);
        _line.Text = TileLine(m);
        CardKit.Name(_tile, $"Cricket: {CricketText.Label(m)}, {TileLine(m)}");
    }

    /// <summary>"IND 182/4 (18.2)": who is batting, the score, and the overs when known.</summary>
    static string ScoreLine(CricketLiveScore m) =>
        m.Balls is { } balls ? $"{CricketText.Label(m)} ({CricketText.Overs(balls)})" : CricketText.Label(m);

    static string TileLine(CricketLiveScore m) =>
        m.Balls is { } balls ? $"{CricketText.Overs(balls)} ov · {CricketText.Line(m)}" : CricketText.Line(m);

    static Brush MakeField()
    {
        var b = new LinearGradientBrush(CardKit.Color("#1F4A35"), CardKit.Color("#0D1F17"), new Point(0.25, 0), new Point(0.75, 1));
        b.Freeze();
        return b;
    }

    // ---- Detail ---------------------------------------------------------------------------------

    public FrameworkElement BuildDetail()
    {
        var d = new Detail();
        d.Show(_match, _balls);
        d.Unloaded += (_, _) =>
        {
            if (_detail == d) _detail = null;
        };
        _detail = d;
        return d;
    }

    /// <summary>
    /// The opened card (the card family's shape, 580 wide): "IND v AUS · T20 · live" with a small red Live chip;
    /// the other side's score quiet, the batting side's the hero in light numbers in the accent, the overs, and the
    /// feed's line ("Need 24 from 10") bold with the rate it asks for; the last balls under it when known. A
    /// finished match drops the Live chip and says so; no match is one short line.
    /// </summary>
    sealed class Detail : CardSheet
    {
        const double SheetWidth = 580;

        /// <summary>The one colour outside the palette in the card family: the small Live dot.</summary>
        static readonly Brush LiveRed = Ui.Tokens.Brush("#FF6B6B");

        static readonly Regex NeedRx = new(@"^Need (\d+) from (\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex EndedRx = new(@"\b(won|tied|drawn|draw|no result|abandoned)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        readonly Border _liveChip;

        public Detail() : base("Cricket", SheetWidth)
        {
            var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = LiveRed, VerticalAlignment = VerticalAlignment.Center };
            var word = CardKit.Text("Live", 12, FontWeights.SemiBold, secondary: true);
            word.VerticalAlignment = VerticalAlignment.Center;
            word.Margin = new Thickness(8, 0, 0, 0);
            _liveChip = Action(new Border
            {
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = Ui.Tokens.Brush("#0FFFFFFF"),
                Padding = new Thickness(12, 0, 12, 0),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, word } },
            });
        }

        /// <summary>Whether the feed's line says the match is over ("India won by 5 wickets").</summary>
        static bool Ended(CricketLiveScore m) => m.Status is { Length: > 0 } s && EndedRx.IsMatch(s);

        public void Show(CricketLiveScore? match, IReadOnlyList<string> balls)
        {
            Body.Children.Clear();
            if (match is not { } m)
            {
                TitleText.Text = "Cricket";
                Status = null;
                _liveChip.Visibility = Visibility.Collapsed;
                var line = Sheet.Meta("", 14);
                line.Inlines.Add(CardKit.Run("No live match. ", FontWeights.Bold, CardKit.Ink));
                line.Inlines.Add(new System.Windows.Documents.Run("The score shows here while India is playing."));
                Body.Children.Add(line);
                CardKit.Name(this, "Cricket: no live match");
                return;
            }

            bool ended = Ended(m);
            TitleText.Text = CricketText.Teams(m);
            Status = string.Join(" · ", new[] { m.Format, ended ? "result" : "live" }.Where(s => !string.IsNullOrEmpty(s)));
            _liveChip.Visibility = ended ? Visibility.Collapsed : Visibility.Visible;

            var row = new Grid();
            foreach (var w in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = w });

            // The other side: quiet (only when the feed gives its score).
            if (m.BowlingScore is { Length: > 0 } other)
            {
                var side = Side(CricketLive.Code(m.Bowling), Sheet.Num(other, 30, CardKit.Ink2), CardKit.Ink2);
                side.Margin = new Thickness(0, 0, 18, 0);
                side.MaxWidth = 150;
                row.Children.Add(side);
            }

            // The batting side: the hero.
            var hero = Side($"{CricketLive.Code(m.Batting)} · batting", Sheet.Num(CricketText.Score(m), 44, Ink.Soft), Ink.Soft);
            Grid.SetColumn(hero, 1);
            row.Children.Add(hero);

            if (m.Balls is { } b)
            {
                var overs = Sheet.Meta($"{CricketText.Overs(b)} ov");
                overs.Margin = new Thickness(18, 0, 0, 0);
                Grid.SetColumn(overs, 2);
                row.Children.Add(overs);
            }

            // The line the feed gives, bold, with the rate it asks for (or the rate so far).
            if (m.Status is { Length: > 0 } status)
            {
                var words = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
                var said = Sheet.Word(status, 18);
                said.HorizontalAlignment = HorizontalAlignment.Right;
                words.Children.Add(said);
                if (Rate(m) is { } rate)
                {
                    var r = Sheet.Meta(rate, 12);
                    r.HorizontalAlignment = HorizontalAlignment.Right;
                    r.Margin = new Thickness(0, 6, 0, 0);
                    words.Children.Add(r);
                }
                Grid.SetColumn(words, 4);
                row.Children.Add(words);
            }
            else if (Rate(m) is { } rate)
            {
                var r = Sheet.Meta(rate, 12);
                r.Margin = new Thickness(16, 0, 0, 0);
                Grid.SetColumn(r, 4);
                row.Children.Add(r);
            }

            var col = new StackPanel();
            col.Children.Add(row);
            if (balls.Count > 0)
            {
                var ballsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
                foreach (var ball in balls) ballsRow.Children.Add(Ball(ball));
                CardKit.Name(ballsRow, "Last balls: " + string.Join(", ", balls.Select(x => x == "W" ? "wicket" : x)));
                col.Children.Add(ballsRow);
            }
            Body.Children.Add(col);
            CardKit.Name(this, $"Cricket: {CricketText.Teams(m, codes: false)}, {CricketText.Label(m)}, {Under(m)}, {CricketText.Line(m)}");
        }

        /// <summary>"IND · 18.2 ov" under the score; without overs just who is batting.</summary>
        public static string Under(CricketLiveScore m) => m.Balls is { } b
            ? $"{CricketLive.Code(m.Batting)} · {CricketText.Overs(b)} ov"
            : $"{CricketLive.Code(m.Batting)} batting";

        /// <summary>"Run rate needed 14.4" for a "Need 24 from 10" line; else the rate so far when the overs are known.</summary>
        static string? Rate(CricketLiveScore m)
        {
            if (m.Status is { Length: > 0 } s && NeedRx.Match(s) is { Success: true } need
                && int.TryParse(need.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int runs)
                && int.TryParse(need.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int left) && left > 0)
                return "Run rate needed " + (runs * 6.0 / left).ToString("0.0", CultureInfo.InvariantCulture);
            if (Ended(m) || m.Balls is not { } b || b <= 0) return null;
            return "Run rate " + (m.Runs * 6.0 / b).ToString("0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>A side: its small tracked label over its score.</summary>
        static StackPanel Side(string label, TextBlock score, Brush labelBrush)
        {
            var lab = CardKit.Text(label.ToUpperInvariant(), 10, FontWeights.Bold);
            lab.Foreground = labelBrush;
            lab.TextTrimming = TextTrimming.CharacterEllipsis;
            score.Margin = new Thickness(0, 4, 0, 0);
            return new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { lab, score } };
        }

        /// <summary>
        /// One ball: a 26 px glass chip. A boundary takes the palette's tint and a wicket the third palette colour;
        /// the accent stays the hero's alone.
        /// </summary>
        static Border Ball(string ball)
        {
            bool wicket = ball == "W", boundary = ball is "4" or "6";
            var text = CardKit.Text(ball, 12, FontWeights.Bold);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Foreground = wicket ? CardKit.OnAccent : boundary ? Ink.Soft : CardKit.Ink;
            return new Border
            {
                MinWidth = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Padding = new Thickness(7, 0, 7, 0),
                Margin = new Thickness(0, 0, 6, 0),
                Background = wicket ? Ink.C : boundary ? Ink.Tint : Sheet.GlassFill,
                BorderBrush = wicket ? Brushes.Transparent : boundary ? Ink.TintEdge : Sheet.GlassEdge,
                BorderThickness = new Thickness(1),
                Child = text,
            };
        }
    }

    // ---- Test pipe --------------------------------------------------------------------------------

    public string DescribeCompact() => _match is { } m
        ? $"cricket: score={_score.Text} teams='{_teams.Text}' line='{_line.Text}' focused={_tile.IsLit} peek=[{CricketText.Kicker(m)} · Live | {ScoreLine(m)} | {CricketText.Line(m)}]"
        : "cricket: no live match";

    public string DescribeDetail() => _match is { } m
        ? $"cricket detail: kicker='{CricketText.Kicker(m)}' live=yes title='{CricketText.Teams(m, codes: false)}' score={CricketText.Score(m)} under='{Detail.Under(m)}' " +
          $"line='{CricketText.Line(m)}' balls=[{string.Join(" ", _balls)}]{(_balls.Count == 0 ? " (no ball-by-ball data)" : "")} open={(_detail is { IsLoaded: true } ? "yes" : "no")}"
        : "cricket detail: no live match";
}
