using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Shammy.Core.Cards;
using Shammy.Core.MeetingSystem;
using Shammy.SystemInfo;
using Shammy.Views;
using Shammy.Views.Cards;

namespace Shammy.Modules.MeetingSystem;

/// <summary>What the System card shows, gathered by <see cref="SystemModule"/>.</summary>
internal sealed record SystemCardData(
    SystemSample Sample,
    TimeSpan? TimeLeft,
    DateTime? FullAt,
    IReadOnlyList<(DateTime At, int Percent)> BatteryHistory,
    IReadOnlyList<BluetoothAudioDevice> Headsets);

/// <summary>
/// The system card on the Focus Wall (Wall.dc.html): a graphite tile whose artwork is two tall meters — the
/// laptop's battery and a connected headset (or CPU and memory on a desktop) — with the battery as a poster;
/// focused, a line about the headset and a control to open the detail. The detail (the card family's shape) says
/// how things are in its header and shows battery, memory and CPU as three palette rings, headsets as chips. Windows reports one
/// level per headset, so earbuds get one "Earbuds" level rather than made-up left/right numbers. On the tile a low
/// battery is the one place colour appears: the meaning red.
/// </summary>
internal sealed class SystemCard
{
    readonly WallTile _tile = new();
    readonly Grid _meters = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(14, 0, 0, WallTile.Pad) };
    readonly TrackedText _big = CardKit.Poster("", 28), _restLabel = CardKit.Label("Battery");
    readonly TextBlock _litTitle, _litLine;

    SystemCardData? _data;
    Detail? _detail;
    bool _detailOpen;

    public SystemCard()
    {
        _tile.Field = Fields.System();
        _tile.Art.Children.Add(_meters);
        _big.HorizontalAlignment = HorizontalAlignment.Right;
        _tile.Art.Children.Add(CardKit.At(_big, HorizontalAlignment.Right, VerticalAlignment.Bottom, 0, 0, WallTile.Pad, WallTile.Pad + 20));
        // Focused, the poster steps up to make room for the two lines under it.
        _tile.FocusChanged += on => _big.Margin = new Thickness(0, 0, WallTile.Pad, WallTile.Pad + (on ? 36 : 20));
        _restLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _tile.Rest.Children.Add(CardKit.At(_restLabel, HorizontalAlignment.Right, VerticalAlignment.Bottom, 0, 0, WallTile.Pad, WallTile.Pad));
        var words = WallTile.Words("", "", out _litTitle, out _litLine);
        _litTitle.FontSize = 13;
        _litTitle.TextAlignment = _litLine.TextAlignment = TextAlignment.Right;
        _litTitle.HorizontalAlignment = _litLine.HorizontalAlignment = HorizontalAlignment.Right;
        words.HorizontalAlignment = HorizontalAlignment.Right;
        words.VerticalAlignment = VerticalAlignment.Bottom;
        words.Margin = new Thickness(0, 0, WallTile.Pad, WallTile.Pad - 2);
        words.MaxWidth = 92;
        _tile.Lit.Children.Add(words);
        _tile.Lit.Children.Add(WallTile.OpenButton("Open system details", () => Open?.Invoke()));
        _tile.IsVisibleChanged += (_, _) => VisibilityChanged?.Invoke();
    }

    public FrameworkElement Compact => _tile;

    /// <summary>Opens the system detail in the notch (the tile's and the peek's primary control).</summary>
    public Action? Open { get; set; }

    /// <summary>The tile or the detail went on or off screen (the module samples faster while one shows).</summary>
    public event Action? VisibilityChanged;

    public bool IsShowing => _tile.IsVisible || _detailOpen;

    public bool IsRelevant(bool hasBattery, int headsets) => hasBattery || headsets > 0;

    /// <summary>A low battery on battery power wants attention; otherwise the system is quiet.</summary>
    public int Live => _data is { } d && PowerText.IsLow(d.Sample.BatteryPercent, d.Sample.PluggedIn) && d.Sample.HasBattery ? 60 : 0;

    /// <summary>The peek: "System · Battery 81% · Earbuds 60% · Airdopes", with Details.</summary>
    public WallPeek? Peek()
    {
        if (_data is not { } d) return null;
        var s = d.Sample;
        string title = s.HasBattery ? $"Battery {s.BatteryPercent}%" : $"CPU {SystemFormat.Percent(s.Cpu)}";
        return new WallPeek("System", title, Line(d), Fields.System(), Open is { } open ? new WallAction("Details", open) : null);
    }

    /// <summary>"Earbuds 60% · Airdopes 311 Pro", else how the power is.</summary>
    static string Line(SystemCardData d)
    {
        if (d.Headsets.FirstOrDefault() is { } h)
            return $"{ShortName(h.Name)} {(h.Battery is { } b ? b + "%" : "connected")}" + (ShortName(h.Name) != h.Name ? $" · {h.Name}" : "");
        return FootText(d);
    }

    public void Show(SystemCardData data)
    {
        _data = data;
        var s = data.Sample;
        bool low = s.HasBattery && PowerText.IsLow(s.BatteryPercent, s.PluggedIn);
        _meters.Children.Clear();
        var levels = Levels(data).Take(2).ToList();
        for (int i = 0; i < levels.Count; i++)
        {
            var meter = CardKit.Meter(levels[i].Fraction, 28, 84, 7);
            if (i == 0 && low) ((Border)meter.Children[1]).Background = CardKit.Alarm;
            meter.HorizontalAlignment = HorizontalAlignment.Left;
            meter.Margin = new Thickness(i * 36, 0, 0, 0);
            meter.ToolTip = $"{levels[i].Name} {levels[i].Value}";
            _meters.Children.Add(meter);
        }
        _big.Text = s.HasBattery ? $"{s.BatteryPercent}%" : SystemFormat.Percent(s.Cpu);
        _restLabel.Text = s.HasBattery ? "Battery" : "CPU";
        _litTitle.Text = s.HasBattery ? "Battery" : "CPU";
        _litLine.Text = data.Headsets.FirstOrDefault() is { } h ? $"{ShortName(h.Name)} {(h.Battery is { } b ? b + "%" : "on")}" : FootText(data);
        CardKit.Name(_tile, $"System: {(s.HasBattery ? $"battery {s.BatteryPercent} percent" : $"CPU {SystemFormat.Percent(s.Cpu)}")}, {Line(data)}");
        _detail?.Show(data);
    }

    /// <summary>The meters, in order: the laptop battery (or the processor), headsets, then memory.</summary>
    static IEnumerable<(string Name, double Fraction, string Value, string Meta)> Levels(SystemCardData d)
    {
        var s = d.Sample;
        if (s.HasBattery) yield return ("Battery", CardMath.Percent(s.BatteryPercent), $"{s.BatteryPercent}%", FootText(d));
        else yield return ("CPU", CardMath.Percent(s.Cpu), SystemFormat.Percent(s.Cpu), s.Top is { } t ? $"{AppNames.Friendly(t.Name)} uses most" : "Processor");
        foreach (var h in d.Headsets.Take(2))
            yield return (ShortName(h.Name), CardMath.Percent(h.Battery ?? 0), h.Battery is { } b ? $"{b}%" : "–", h.Battery is null ? "Level not reported" : h.Name);
        if (s.HasBattery) yield return ("CPU", CardMath.Percent(s.Cpu), SystemFormat.Percent(s.Cpu), s.Top is { } t ? $"{AppNames.Friendly(t.Name)} uses most" : "Processor");
        yield return ("Memory", CardMath.Percent(s.MemoryLoad), $"{s.MemoryLoad}%", $"{SystemFormat.Gigabytes(s.UsedMemory)} of {SystemFormat.Gigabytes(s.TotalMemory)} GB");
    }

    static string FootText(SystemCardData d)
    {
        var s = d.Sample;
        if (!s.HasBattery) return $"Memory {s.MemoryLoad}%";
        if (s.Charging) return PowerText.ChargingLine(d.FullAt);
        if (s.PluggedIn) return PowerText.Status(true, true, false, s.BatteryPercent);
        return d.TimeLeft is { } left ? PowerText.TimeLeft(left) : "On battery";
    }

    /// <summary>Earbuds and headphones read better as what they are than by model name in a tiny label.</summary>
    static string ShortName(string name) =>
        name.Contains("bud", StringComparison.OrdinalIgnoreCase) || name.Contains("dopes", StringComparison.OrdinalIgnoreCase)
        || name.Contains("pods", StringComparison.OrdinalIgnoreCase) ? "Earbuds"
        : name.Length > 12 ? name[..11].TrimEnd() + "…" : name;

    // ---- Detail ---------------------------------------------------------------------------------

    public FrameworkElement BuildDetail()
    {
        var detail = new Detail();
        _detail = detail;
        detail.IsVisibleChanged += (_, _) =>
        {
            _detailOpen = detail.IsVisible;
            if (_detailOpen) _detail = detail;
            else if (_detail == detail) _detail = null;
            VisibilityChanged?.Invoke();
        };
        if (_data is { } d) detail.Show(d);
        return detail;
    }

    /// <summary>
    /// The opened card (the card family's shape, 620 wide): "System · all good · plugged in" in the header, then
    /// three equal rings, one palette colour each: the battery (its number in the accent), memory and the processor
    /// (on a desktop, just memory and the processor), each with a bold name and a quiet line. Connected headsets
    /// follow as quiet chips.
    /// </summary>
    sealed class Detail : CardSheet
    {
        const double SheetWidth = 620, RingSize = 76;

        public Detail() : base("System", SheetWidth)
        {
            Body.Children.Add(Sheet.Meta("Reading the system…", 14));
        }

        public void Show(SystemCardData d)
        {
            var s = d.Sample;
            bool low = s.HasBattery && PowerText.IsLow(s.BatteryPercent, s.PluggedIn);
            Status = string.Join(" · ", new[]
            {
                low ? "battery low" : s.Charging ? "charging" : "all good",
                !s.HasBattery ? "desktop power" : s.PluggedIn ? "plugged in" : "on battery",
            });

            var rings = new UniformGrid { Rows = 1 };
            if (s.HasBattery)
                rings.Children.Add(Gauge("Battery", CardMath.Percent(s.BatteryPercent), s.BatteryPercent.ToString(CultureInfo.InvariantCulture), BatteryLine(d), Ink.A, Ink.Soft));
            rings.Children.Add(Gauge("Memory", CardMath.Percent(s.MemoryLoad), s.MemoryLoad.ToString(CultureInfo.InvariantCulture),
                $"{SystemFormat.Gigabytes(s.UsedMemory)} of {SystemFormat.Gigabytes(s.TotalMemory)} GB", Ink.B, CardKit.Ink));
            rings.Children.Add(Gauge("CPU", CardMath.Percent(s.Cpu), SystemFormat.Percent(s.Cpu).TrimEnd('%'),
                s.Top is { } t ? $"{AppNames.Friendly(t.Name)} uses most" : "Processor", Ink.C, CardKit.Ink));
            rings.Columns = rings.Children.Count;

            var col = new StackPanel();
            col.Children.Add(rings);
            if (d.Headsets.Count > 0)
            {
                var chips = new WrapPanel { Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
                foreach (var h in d.Headsets.Take(3))
                {
                    var chip = Sheet.Chip($"{h.Name} · {(h.Battery is { } b ? b + "%" : "connected")}", quiet: true, height: 28);
                    chip.Margin = new Thickness(4, 0, 4, 0);
                    chip.ToolTip = h.Battery is null ? $"{h.Name}: level not reported" : $"{h.Name}: {h.Battery}%";
                    chips.Children.Add(chip);
                }
                col.Children.Add(chips);
            }
            Body.Children.Clear();
            Body.Children.Add(col);
            CardKit.Name(this, $"System: {Status}");
        }

        /// <summary>"Full", "Charging · full by 4:10", "About 2 h left", "Plugged in", "On battery".</summary>
        static string BatteryLine(SystemCardData d)
        {
            var s = d.Sample;
            if (s.Charging) return PowerText.ChargingLine(d.FullAt);
            if (s.PluggedIn) return s.BatteryPercent >= 99 ? "Full" : "Plugged in";
            return d.TimeLeft is { } left ? PowerText.TimeLeft(left) : "On battery";
        }

        /// <summary>A ring in one palette colour with its number inside, then a bold name and a quiet line beside it.</summary>
        static FrameworkElement Gauge(string name, double fraction, string value, string meta, Brush ring, Brush number)
        {
            var dial = new Grid { Width = RingSize, Height = RingSize };
            dial.Children.Add(new ArcRing(RingSize, fraction) { Thickness = 6, Stroke = ring });
            var num = Sheet.Num(value, 24, number);
            num.HorizontalAlignment = HorizontalAlignment.Center;
            num.TextTrimming = TextTrimming.None;
            dial.Children.Add(num);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            words.Children.Add(Sheet.Word(name, 14));
            var line = Sheet.Meta(meta, 12);
            line.Margin = new Thickness(0, 2, 0, 0);
            words.Children.Add(line);
            var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center, ToolTip = $"{name} {value} · {meta}" };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(dial);
            Grid.SetColumn(words, 1);
            grid.Children.Add(words);
            CardKit.Name(grid, $"{name} {value} percent, {meta}");
            return grid;
        }
    }

    // ---- Test pipe --------------------------------------------------------------------------------

    public string DescribeCompact()
    {
        if (_data is not { } d) return "system: no sample yet";
        var s = d.Sample;
        var laptop = s.HasBattery ? $"laptop={s.BatteryPercent}%{(s.Charging ? " charging" : "")} meter={CardMath.Percent(s.BatteryPercent):0.00}" : $"cpu={SystemFormat.Percent(s.Cpu)} (no battery)";
        var buds = d.Headsets.Count == 0 ? "-" : string.Join(", ", d.Headsets.Take(2).Select(h => $"{ShortName(h.Name)} {(h.Battery is { } b ? b + "%" : "?")}"));
        return $"system: {laptop} headsets=[{buds}] foot=\"{FootText(d)}\" focused={_tile.IsLit} visible={_tile.IsVisible}";
    }

    public string DescribeDetail()
    {
        if (_data is not { } d) return "system detail: no sample yet";
        var s = d.Sample;
        var time = s.Charging ? (d.FullAt is { } at ? PowerText.FullBy(at) : "charging, no estimate")
            : s.PluggedIn ? "plugged in" : d.TimeLeft is { } left ? PowerText.TimeLeft(left) : "estimating";
        var buds = d.Headsets.Count == 0 ? "none" : string.Join(", ", d.Headsets.Select(h => $"{h.Name} {(h.Battery is { } b ? b + "%" : "not reported")}"));
        var top = s.Top is { } t ? $"{AppNames.Friendly(t.Name)} {SystemFormat.Percent(t.Percent)}" : "-";
        return $"system detail: battery={(s.HasBattery ? s.BatteryPercent + "%" : "none")} time=\"{time}\" history={d.BatteryHistory.Count} " +
               $"headsets=[{buds}] cpu={SystemFormat.Percent(s.Cpu)} memory={s.MemoryLoad}% ({SystemFormat.Gigabytes(s.UsedMemory)}/{SystemFormat.Gigabytes(s.TotalMemory)} GB) top={top} open={_detailOpen}";
    }
}
