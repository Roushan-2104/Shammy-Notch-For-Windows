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
/// condition and feels-like, and a control to open the detail. The detail grows the sky into the art panel with
/// the temperature and today's range, the next 12 hours as bars and a 5-day range list, all from the Open-Meteo
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

    public FrameworkElement BuildDetail()
    {
        if (_report is not { } r)
        {
            var empty = new WallDetail(Fields.Sky(WeatherKind.Cloudy, true), "Weather", "Weather", _status.Text);
            empty.ArtBottomLeft(CardKit.Poster("–", 96));
            return empty;
        }
        var kind = WeatherCodes.Kind(r.Code);
        var now = r.LocalNow(DateTime.UtcNow);
        var d = new WallDetail(Fields.Sky(kind, r.IsDay), $"Now · {now.ToString(CardKit.H24 ? "HH:mm" : "h:mm tt", CultureInfo.InvariantCulture)}", _cityName,
            string.Join(" · ", new[] { WeatherCodes.Describe(r.Code) }.Concat(Stats(r).Select(s => $"{s.Label.ToLowerInvariant()} {s.Value}"))));
        var range = CardKit.Label($"{WeatherCodes.Describe(r.Code)} · H {WeatherText.Temp(r.TodayHigh)} L {WeatherText.Temp(r.TodayLow)}");
        range.Margin = new Thickness(0, 8, 0, 0);
        d.ArtBottomLeft(CardKit.Poster(WeatherText.Temp(r.Temperature), 96), range);
        d.Body.Children.Add(HoursBlock(r));
        d.Aside = DaysBlock(r);
        return d;
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

    /// <summary>The next 12 hours: the temperature over a white bar (now solid, later ones softer), the hour under it.</summary>
    static FrameworkElement HoursBlock(WeatherReport r)
    {
        var points = ChartPoints(r);
        var heights = CardMath.BarHeights(points.Select(p => p.Temp).ToList(), floor: 0.25);
        var chart = new UniformGrid { Rows = 1, Columns = Math.Max(1, points.Count), VerticalAlignment = VerticalAlignment.Bottom };
        const double maxBar = 56;
        for (int i = 0; i < points.Count; i++)
        {
            var temp = CardKit.Text(WeatherText.Temp(points[i].Temp), 11, FontWeights.Bold);
            temp.HorizontalAlignment = HorizontalAlignment.Center;
            var bar = new Border
            {
                Height = Math.Round(maxBar * heights[i]),
                CornerRadius = new CornerRadius(4, 4, 2, 2),
                Background = points[i].IsNow ? CardKit.Ink : CardKit.Hex("#73F5F5F7"),
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            var slot = new Grid { Height = maxBar, Margin = new Thickness(0, 4, 0, 4) };
            slot.Children.Add(bar);
            var label = CardKit.Text(points[i].Label, 11, points[i].IsNow ? FontWeights.Bold : FontWeights.Medium, secondary: !points[i].IsNow);
            label.TextTrimming = TextTrimming.None;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var column = CardKit.Column(0, temp, slot, label);
            column.Margin = new Thickness(3.5, 0, 3.5, 0);
            column.VerticalAlignment = VerticalAlignment.Bottom;
            chart.Children.Add(column);
        }
        return chart;
    }

    /// <summary>The 5-day list: day, low, the day's range on the shared scale (a dot for now, today), high.</summary>
    static FrameworkElement DaysBlock(WeatherReport r)
    {
        var days = r.Days.Take(DaysShown).ToList();
        var list = new StackPanel { Width = 206 };
        if (days.Count == 0) return list;
        double min = days.Min(d => d.Low), max = days.Max(d => d.High);
        var today = r.LocalNow(DateTime.UtcNow).Date;
        foreach (var d in days)
        {
            var line = new DockPanel { Height = 24 };
            var name = CardKit.Text(WeatherText.DayLabel(d.Date, today), 12.5, FontWeights.Bold);
            name.Width = 38;
            var lo = CardKit.Text(WeatherText.Temp(d.Low), 12.5, FontWeights.Medium, secondary: true);
            lo.Width = 28;
            lo.TextAlignment = TextAlignment.Right;
            var hi = CardKit.Text(WeatherText.Temp(d.High), 12.5, FontWeights.Bold);
            hi.Width = 30;
            hi.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(name, Dock.Left);
            DockPanel.SetDock(lo, Dock.Left);
            DockPanel.SetDock(hi, Dock.Right);
            line.Children.Add(name);
            line.Children.Add(lo);
            line.Children.Add(hi);
            line.Children.Add(new RangeBar
            {
                Margin = new Thickness(8, 0, 0, 0),
                Span = CardMath.RangeSpan(d.Low, d.High, min, max),
                Dot = d.Date == today ? CardMath.Fraction(r.Temperature - min, max - min) : null,
            });
            line.ToolTip = $"{WeatherCodes.Describe(d.Code)} · low {WeatherText.Temp(d.Low)}, high {WeatherText.Temp(d.High)}" +
                           (d.RainChance is { } p && p > 0 ? $" · {p}% rain" : "");
            list.Children.Add(line);
        }
        return list;
    }

    /// <summary>A day's low–high range on the shared scale: white on a quiet track, a dot for the temperature now (today only).</summary>
    sealed class RangeBar : FrameworkElement
    {
        public (double Left, double Width) Span { get; init; }
        public double? Dot { get; init; }

        protected override Size MeasureOverride(Size available) => new(0, 10);

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, y = (ActualHeight - 4) / 2;
            if (w <= 0) return;
            dc.DrawRoundedRectangle(CardKit.TrackBg, null, new Rect(0, y, w, 4), 2, 2);
            dc.DrawRoundedRectangle(CardKit.Ink, null, new Rect(w * Span.Left, y, Math.Max(4, w * Span.Width), 4), 2, 2);
            if (Dot is { } d)
            {
                var c = new Point(Math.Clamp(w * d, 4, w - 4), y + 2);
                dc.DrawEllipse(CardKit.Ink, new Pen(CardKit.Surface, 2), c, 4, 4);
            }
        }
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
