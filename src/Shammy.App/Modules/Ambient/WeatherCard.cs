using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Shammy.Core.Ambient;
using Shammy.Core.Cards;
using Shammy.Views;
using Shammy.Views.Cards;

namespace Shammy.Modules.Ambient;

/// <summary>
/// The weather card on the Focus Wall (Wall.dc.html): a tile whose field is the sky now (its colour by condition,
/// darker at night), the temperature as a poster and six white bars for the next hours; focused, the city, the
/// condition and feels-like, and a control to open the detail. The detail (the card family's shape) has the
/// temperature and today's range, the next 12 hours as a curve and a 4-day range list, all from the Open-Meteo
/// forecast the weather feature already fetches; anything the forecast lacks is left out.
/// </summary>
internal sealed class WeatherCard
{
    const int MiniHours = 6, ChartHours = 12, DaysShown = 5;

    readonly WallTile _tile = new();
    readonly TrackedText _temp = CardKit.Poster("", 34), _cityRest = CardKit.Label("");
    readonly TextBlock _city, _condition;
    readonly UniformGrid _mini = new() { Rows = 1, Columns = MiniHours, Height = 28 };
    readonly TextBlock _status = CardKit.Text("Finding the weather…", 12, FontWeights.Medium);
    readonly StackPanel _now;

    WeatherReport? _report;
    string _cityName = "";

    public WeatherCard()
    {
        _tile.Field = Fields.Sky(WeatherKind.Cloudy, true);
        _cityRest.Margin = new Thickness(0, 6, 0, 0);
        _now = CardKit.Column(0, _temp);
        _now.Margin = new Thickness(WallTile.Pad);
        _now.VerticalAlignment = VerticalAlignment.Top;
        _now.HorizontalAlignment = HorizontalAlignment.Left;
        _tile.Art.Children.Add(_now);
        _mini.VerticalAlignment = VerticalAlignment.Bottom;
        _mini.Margin = new Thickness(WallTile.Pad - 3, 0, WallTile.Pad - 3, WallTile.Pad);
        _tile.Art.Children.Add(_mini);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(WallTile.Pad);
        _status.VerticalAlignment = VerticalAlignment.Top;
        _tile.Art.Children.Add(_status);

        var rest = new StackPanel { Margin = new Thickness(WallTile.Pad, WallTile.Pad + 34, WallTile.Pad, 0), VerticalAlignment = VerticalAlignment.Top };
        rest.Children.Add(_cityRest);
        _tile.Rest.Children.Add(rest);

        var words = WallTile.Words("", "", out _city, out _condition, Ui.Tokens.InkOnArt);
        words.Margin = new Thickness(WallTile.Pad, WallTile.Pad + 39, WallTile.Pad, 0);
        words.VerticalAlignment = VerticalAlignment.Top;
        _tile.Lit.Children.Add(words);
        _tile.Lit.Children.Add(WallTile.OpenButton("Open weather details", () => Open?.Invoke()));
        Show(null, "");
    }

    /// <summary>Opens the weather detail in the notch (the tile's and the peek's primary control).</summary>
    public Action? Open { get; set; }

    public FrameworkElement Compact => _tile;

    public bool IsRelevant => _report is not null;

    /// <summary>"31° Pune" for the resting notch.</summary>
    public GlanceItem? Glance() =>
        _report is { } r ? new GlanceItem(CardMath.Fit(WeatherText.Glance(r, _cityName)), NotchPalette.Weather, 10) : null;

    /// <summary>The peek: "Weather · 25° Overcast · Pune · feels like 27°", with Hourly.</summary>
    public WallPeek? Peek() => _report is { } r
        ? new WallPeek("Weather", $"{WeatherText.Temp(r.Temperature)} {WeatherCodes.Describe(r.Code)}", $"{_cityName} · feels like {WeatherText.Temp(r.FeelsLike)}",
            Fields.Sky(WeatherCodes.Kind(r.Code), r.IsDay), Open is { } open ? new WallAction("Hourly", open) : null)
        : null;

    /// <summary>Shows "Finding the weather…" style text while there's no forecast yet.</summary>
    public void ShowStatus(string text)
    {
        _status.Text = text;
        if (_report is null) Show(null, _cityName);
    }

    public void Show(WeatherReport? report, string city)
    {
        _report = report;
        _cityName = city;
        bool has = report is not null;
        _now.Visibility = _mini.Visibility = has ? Visibility.Visible : Visibility.Hidden;
        _status.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (report is not { } r)
        {
            CardKit.Name(_tile, "Weather: " + _status.Text);
            return;
        }

        _tile.Field = Fields.Sky(WeatherCodes.Kind(r.Code), r.IsDay);
        _temp.Text = WeatherText.Temp(r.Temperature);
        _cityRest.Text = city;
        _city.Text = city;
        _condition.Text = $"{WeatherCodes.Describe(r.Code)} · feels {WeatherText.Temp(r.FeelsLike)}";

        var hours = r.NextHours(r.LocalNow(DateTime.UtcNow), MiniHours);
        var heights = CardMath.BarHeights(hours.Select(h => h.Temperature).ToList(), floor: 0.5);
        _mini.Children.Clear();
        for (int i = 0; i < hours.Count; i++)
        {
            var bar = new Border
            {
                Height = Math.Round(28 * heights[i]),
                CornerRadius = new CornerRadius(3, 3, 1, 1),
                Background = CardKit.Hex("#BFF5F5F7"),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(3, 0, 3, 0),
                ToolTip = $"{WeatherText.HourLabel(hours[i].Time)} · {WeatherText.Temp(hours[i].Temperature)}",
            };
            _mini.Children.Add(bar);
        }
        CardKit.Name(_tile, $"Weather: {city} {WeatherText.Temp(r.Temperature)}, {WeatherCodes.Describe(r.Code)}");
    }

    static string HourNumber(DateTime t) => (t.Hour % 12 == 0 ? 12 : t.Hour % 12).ToString(CultureInfo.InvariantCulture);

    // ---- Detail ---------------------------------------------------------------------------------

    /// <summary>The card family's weather card, 700 wide; with no forecast yet, a small card with one line.</summary>
    const double SheetWidth = 700, EmptyWidth = 460, CurveWidth = 300, CurveHeight = 110;

    /// <summary>The days the opened card lists (the test pipe still describes <see cref="DaysShown"/>).</summary>
    const int DaysListed = 4;

    /// <summary>
    /// The opened card: the city with "clear · feels like 32°" in the header; the temperature big and light in the
    /// accent with "H · L · wind" under it; the next 12 hours as one smooth curve in the palette's gradient (four
    /// points labelled); and a 4-day list with slim palette ranges. No sky panel: the island's waves are the sky.
    /// </summary>
    public FrameworkElement BuildDetail()
    {
        if (_report is not { } r)
        {
            var empty = new CardSheet("Weather", EmptyWidth);
            empty.Body.Children.Add(Sheet.Meta(_status.Text, 14));
            return empty;
        }

        var sheet = new CardSheet(_cityName.Length > 0 ? _cityName : "Weather", SheetWidth,
            $"{WeatherCodes.Describe(r.Code).ToLowerInvariant()} · feels like {WeatherText.Temp(r.FeelsLike)}");

        var big = Sheet.Num(WeatherText.Temp(r.Temperature), 80, Ink.Soft);
        big.TextTrimming = TextTrimming.None;
        var range = new List<string> { $"H {WeatherText.Temp(r.TodayHigh)}", $"L {WeatherText.Temp(r.TodayLow)}" };
        if (r.WindKmh is { } w) range.Add($"wind {WeatherText.Wind(w)}");
        var under = Sheet.Meta(string.Join(" · ", range));
        under.Margin = new Thickness(0, 8, 0, 0);
        under.ToolTip = string.Join(" · ", Stats(r).Select(s => $"{s.Label} {s.Value}"));
        var now = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, MaxWidth = 180 };
        now.Children.Add(big);
        now.Children.Add(under);

        var curve = new HourCurve(CurvePoints(r)) { Width = CurveWidth, Height = CurveHeight, Margin = new Thickness(18, 0, 18, 0), VerticalAlignment = VerticalAlignment.Bottom };
        var days = DaysBlock(r);
        days.VerticalAlignment = VerticalAlignment.Bottom;

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(now);
        Grid.SetColumn(curve, 1);
        row.Children.Add(curve);
        Grid.SetColumn(days, 2);
        row.Children.Add(days);
        sheet.Body.Children.Add(row);
        CardKit.Name(sheet, $"Weather: {_cityName} {WeatherText.Temp(r.Temperature)}, {WeatherCodes.Describe(r.Code)}");
        return sheet;
    }

    /// <summary>Only what the forecast actually has: feels-like always, humidity and wind when present.</summary>
    static List<(string Label, string Value)> Stats(WeatherReport r)
    {
        var stats = new List<(string, string)> { ("Feels like", WeatherText.Temp(r.FeelsLike)) };
        if (r.Humidity is { } h) stats.Add(("Humidity", $"{h}%"));
        if (r.WindKmh is { } w) stats.Add(("Wind", WeatherText.Wind(w)));
        return stats;
    }

    /// <summary>"Now" plus the next 11 hours.</summary>
    static List<(string Label, double Temp, bool IsNow)> ChartPoints(WeatherReport r)
    {
        var points = new List<(string, double, bool)> { ("Now", r.Temperature, true) };
        foreach (var h in r.NextHours(r.LocalNow(DateTime.UtcNow), ChartHours - 1))
            points.Add((HourNumber(h.Time), h.Temperature, false));
        return points;
    }

    /// <summary>The curve's points: "Now", then the next 11 hours as "4 PM".</summary>
    static List<(string Label, double Temp)> CurvePoints(WeatherReport r)
    {
        var points = new List<(string, double)> { ("Now", r.Temperature) };
        foreach (var h in r.NextHours(r.LocalNow(DateTime.UtcNow), ChartHours - 1))
            points.Add((WeatherText.HourLabel(h.Time), h.Temperature));
        return points;
    }

    /// <summary>The 4-day list: day, low, the day's range on the shared scale in the palette's gradient, high.</summary>
    static FrameworkElement DaysBlock(WeatherReport r)
    {
        var days = r.Days.Take(DaysListed).ToList();
        var list = new StackPanel();
        if (days.Count == 0) return list;
        double min = days.Min(d => d.Low), max = days.Max(d => d.High);
        var today = r.LocalNow(DateTime.UtcNow).Date;
        foreach (var d in days)
        {
            var line = new Grid { Height = 18, Margin = new Thickness(0, list.Children.Count > 0 ? 9 : 0, 0, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            var name = Sheet.Word(WeatherText.DayLabel(d.Date, today), 12);
            var lo = Sheet.Meta(WeatherText.Temp(d.Low), 12);
            var hi = Sheet.Word(WeatherText.Temp(d.High), 12);
            hi.TextAlignment = TextAlignment.Right;
            var span = CardMath.RangeSpan(d.Low, d.High, min, max);
            var bar = RangeBar(span.Left, span.Width);
            bar.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(lo, 1);
            Grid.SetColumn(bar, 2);
            Grid.SetColumn(hi, 3);
            line.Children.Add(name);
            line.Children.Add(lo);
            line.Children.Add(bar);
            line.Children.Add(hi);
            line.ToolTip = $"{WeatherCodes.Describe(d.Code)} · low {WeatherText.Temp(d.Low)}, high {WeatherText.Temp(d.High)}" +
                           (d.RainChance is { } p && p > 0 ? $" · {p}% rain" : "");
            list.Children.Add(line);
        }
        return list;
    }

    /// <summary>A slim (6 px) palette bar from <paramref name="left"/> across <paramref name="width"/> of a quiet track.</summary>
    static Grid RangeBar(double left, double width)
    {
        left = Math.Clamp(left, 0, 1);
        width = Math.Clamp(width, 0.04, 1 - left);
        var g = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(left, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 1 - left - width), GridUnitType.Star) });
        var track = new Border { Background = Sheet.Track, CornerRadius = new CornerRadius(3) };
        Grid.SetColumnSpan(track, 3);
        g.Children.Add(track);
        var fill = new Border { Background = Ink.Gradient, CornerRadius = new CornerRadius(3) };
        Grid.SetColumn(fill, 1);
        g.Children.Add(fill);
        return g;
    }

    /// <summary>
    /// The next 12 hours as one smooth curve: stroked with the palette's gradient over a soft fade of the first
    /// palette colour, a dot on "now", and four points labelled (the temperature above, the hour along the bottom).
    /// </summary>
    sealed class HourCurve : FrameworkElement
    {
        const double PadX = 10, Top = 26, Bottom = 92, LabelGap = 7;

        readonly List<(string Label, double Temp)> _points;

        public HourCurve(List<(string Label, double Temp)> points)
        {
            _points = points;
            ToolTip = string.Join(" · ", points.Select(p => $"{p.Label} {WeatherText.Temp(p.Temp)}"));
            System.Windows.Automation.AutomationProperties.SetName(this, "Next 12 hours: " + ToolTip);
        }

        /// <summary>The four labelled points: now, a third and two thirds of the way, and the last.</summary>
        IEnumerable<int> Labelled()
        {
            int n = _points.Count;
            if (n == 0) yield break;
            var seen = new HashSet<int>();
            foreach (var i in new[] { 0, (int)Math.Round((n - 1) / 3.0), (int)Math.Round((n - 1) * 2 / 3.0), n - 1 })
                if (seen.Add(i)) yield return i;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            int n = _points.Count;
            if (w <= 0 || n == 0) return;
            double lo = _points.Min(p => p.Temp), hi = _points.Max(p => p.Temp);
            double spread = Math.Max(hi - lo, 3);
            double mid = (hi + lo) / 2;
            Point At(int i) => new(
                n == 1 ? PadX : PadX + (w - 2 * PadX) * i / (n - 1),
                Bottom - 12 - (_points[i].Temp - (mid - spread / 2)) / spread * (Bottom - 12 - Top));
            var pts = Enumerable.Range(0, n).Select(At).ToList();

            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (var c = line.Open()) Trace(c, pts, false);
            using (var c = area.Open()) Trace(c, pts, true);
            line.Freeze();
            area.Freeze();

            var a = Ink.A.Color;
            var fade = new LinearGradientBrush(Color.FromArgb(0x47, a.R, a.G, a.B), Color.FromArgb(0, a.R, a.G, a.B), 90);
            fade.Freeze();
            dc.DrawGeometry(fade, null, area);
            dc.DrawGeometry(null, new Pen(Ink.Gradient, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, line);
            dc.DrawEllipse(CardKit.Ink, null, pts[0], 5, 5);

            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            foreach (int i in Labelled())
            {
                bool now = i == 0;
                var temp = Text(WeatherText.Temp(_points[i].Temp), 12, now ? FontWeights.Bold : FontWeights.SemiBold, now ? CardKit.Ink : CardKit.Ink2, dip);
                var hour = Text(_points[i].Label, 11, FontWeights.Medium, CardKit.Ink2, dip);
                dc.DrawText(temp, new Point(Place(pts[i].X, temp.Width, w), pts[i].Y - LabelGap - temp.Height));
                dc.DrawText(hour, new Point(Place(pts[i].X, hour.Width, w), ActualHeight - hour.Height));
            }
        }

        /// <summary>A label centred on its point but kept inside the curve's box.</summary>
        static double Place(double x, double width, double box) => Math.Clamp(x - width / 2, 0, Math.Max(0, box - width));

        /// <summary>A smooth path through the points (Catmull-Rom as Béziers); closed down to the baseline for the fill.</summary>
        static void Trace(StreamGeometryContext c, List<Point> p, bool closed)
        {
            c.BeginFigure(p[0], closed, closed);
            for (int i = 0; i < p.Count - 1; i++)
            {
                Point p0 = p[Math.Max(0, i - 1)], p1 = p[i], p2 = p[i + 1], p3 = p[Math.Min(p.Count - 1, i + 2)];
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                c.BezierTo(c1, c2, p2, true, true);
            }
            if (!closed) return;
            c.LineTo(new Point(p[^1].X, Bottom), false, false);
            c.LineTo(new Point(p[0].X, Bottom), false, false);
        }

        static FormattedText Text(string s, double size, FontWeight weight, Brush brush, double dip) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(CardKit.Face(weight), FontStyles.Normal, weight, FontStretches.Normal), size, brush, dip);
    }

    // ---- Test pipe --------------------------------------------------------------------------------

    public string DescribeCompact()
    {
        if (_report is not { } r) return $"weather: no data ({_status.Text})";
        var hours = r.NextHours(r.LocalNow(DateTime.UtcNow), MiniHours);
        var heights = CardMath.BarHeights(hours.Select(h => h.Temperature).ToList(), floor: 0.5);
        var bars = string.Join(" ", hours.Select((h, i) => $"{HourNumber(h.Time)}:{WeatherText.Temp(h.Temperature)}@{heights[i]:0.00}"));
        return $"weather: city={_cityName} temp={WeatherText.Temp(r.Temperature)} condition={WeatherCodes.Describe(r.Code)} sky={WeatherCodes.Kind(r.Code)}/{(r.IsDay ? "day" : "night")} " +
               $"focused={_tile.IsLit} bars=[{bars}]";
    }

    public string DescribeDetail()
    {
        if (_report is not { } r) return $"weather detail: no data ({_status.Text})";
        var stats = string.Join(", ", Stats(r).Select(s => $"{s.Label} {s.Value}"));
        var chart = string.Join(" ", ChartPoints(r).Select(p => $"{p.Label}:{WeatherText.Temp(p.Temp)}"));
        var today = r.LocalNow(DateTime.UtcNow).Date;
        var days = string.Join(" | ", r.Days.Take(DaysShown).Select(d => $"{WeatherText.DayLabel(d.Date, today)} {WeatherText.Temp(d.Low)}-{WeatherText.Temp(d.High)} {WeatherCodes.Describe(d.Code)}"));
        return $"weather detail: now={WeatherText.Temp(r.Temperature)} {WeatherCodes.Describe(r.Code)} H {WeatherText.Temp(r.TodayHigh)} L {WeatherText.Temp(r.TodayLow)} " +
               $"stats=[{stats}] hours=[{chart}] days=[{days}]";
    }
}
