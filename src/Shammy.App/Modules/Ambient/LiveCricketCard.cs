using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Ambient;
using Shammy.Views;
using Shammy.Views.Cards;

namespace Shammy.Modules.Ambient;

/// <summary>
/// The cricket card on the Focus Wall, on the rail only while a match is live: a tile with the score as a poster on
/// a dark pitch green; focused, the teams, the line and a control to open the detail. The detail has the two teams
/// and the format, the score big, the overs and the line the feed gives. It shows only what it was given: the
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
    /// The mock's hover card on the kit's detail: the score big on the pitch (the overs under it), the teams and
    /// format as the kicker with "Live" opposite, the two teams as the title, the feed's line, then the balls.
    /// </summary>
    sealed class Detail : WallDetail
    {
        readonly TrackedText _score = CardKit.Poster("", 72), _under = CardKit.Label("");
        readonly TextBlock _liveMark = CardKit.Text("Live", 12.5, FontWeights.SemiBold, secondary: true);
        readonly StackPanel _ballsRow = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };

        public Detail() : base(Pitch, "Cricket", "", "")
        {
            _under.Margin = new Thickness(0, 8, 0, 0);
            ArtBottomLeft(_score, _under);
            Corner = _liveMark;
            Body.Children.Add(_ballsRow);
        }

        public void Show(CricketLiveScore? match, IReadOnlyList<string> balls)
        {
            _ballsRow.Children.Clear();
            if (match is not { } m)
            {
                Kicker.Text = "Cricket";
                TitleText.Text = "No live match";
                TitleText.FontSize = 40;
                Line.Text = "The score shows here while India is playing.";
                _score.Text = "–";
                _under.Text = "";
                _liveMark.Visibility = Visibility.Collapsed;
                CardKit.Name(this, "Cricket: no live match");
                return;
            }
            string title = CricketText.Teams(m, codes: false), score = CricketText.Score(m);
            Kicker.Text = CricketText.Kicker(m);
            TitleText.Text = title;
            TitleText.FontSize = title.Length > 20 ? 28 : 40;
            Line.Text = CricketText.Line(m);
            _score.Text = score;
            _score.FontSize = score.Length > 6 ? 58 : 72;
            _under.Text = Under(m);
            _liveMark.Visibility = Visibility.Visible;
            foreach (var ball in balls) _ballsRow.Children.Add(Ball(ball));
            if (balls.Count > 0) CardKit.Name(_ballsRow, "Last balls: " + string.Join(", ", balls.Select(b => b == "W" ? "wicket" : b)));
            CardKit.Name(this, $"Cricket: {title}, {CricketText.Label(m)}, {Under(m)}, {CricketText.Line(m)}");
        }

        /// <summary>"IND · 18.2 ov" under the score; without overs just who is batting.</summary>
        public static string Under(CricketLiveScore m) => m.Balls is { } b
            ? $"{CricketLive.Code(m.Batting)} · {CricketText.Overs(b)} ov"
            : $"{CricketLive.Code(m.Batting)} batting";

        /// <summary>
        /// One ball: a 26 px chip. A boundary is filled white and a wicket the meaning red; the accent stays the
        /// primary control's alone.
        /// </summary>
        static Border Ball(string ball)
        {
            bool wicket = ball == "W", boundary = ball is "4" or "6";
            var text = CardKit.Text(ball, 12, FontWeights.Bold);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.Foreground = wicket ? Brushes.White : boundary ? CardKit.OnAccent : CardKit.Ink;
            return new Border
            {
                MinWidth = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Padding = new Thickness(7, 0, 7, 0),
                Margin = new Thickness(0, 0, 6, 0),
                Background = wicket ? CardKit.Alarm : boundary ? CardKit.Ink : CardKit.Surface,
                BorderBrush = wicket || boundary ? Brushes.Transparent : CardKit.Hairline,
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
