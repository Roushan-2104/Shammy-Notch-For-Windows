using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Quick;
using Shammy.Modules.Quick;
using Shammy.Views.Cards;
using Shammy.Views.Screens;

namespace Shammy.Modules.Health;

/// <summary>
/// The Health card (the card family's shape, 640 wide): "Health · 2 things to tidy" in the header; the system
/// drive's free space as the hero (light numbers in the accent over a palette bar of what's used, other drives in
/// a quiet line); the cleanups that have something to clean as glass rows with a capsule each (the biggest saving
/// is the accent; every one asks "Sure?" in red before it does anything, and what it did stays in the row); and
/// the daily checks as quiet chips with palette dots, with a Restart (asked twice, like the Laptop card's) when
/// updates wait or the last restart was a week ago, and a way to hide the daily line for a week.
/// </summary>
internal sealed class HealthView : CardSheet
{
    const double SheetWidth = 640;
    const string RestartStep = "restart";

    readonly HealthModule _module;
    readonly ConfirmStep _step;

    public HealthView(HealthModule module) : base("Health", SheetWidth)
    {
        _module = module;
        _step = new ConfirmStep(Dispatcher, Refresh);

        Loaded += (_, _) =>
        {
            _module.Changed -= Refresh;
            _module.Changed += Refresh;
            _module.Opened();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            _module.Changed -= Refresh;
            _step.Disarm();
        };
        Refresh();
    }

    /// <summary>The cleanups to show: measuring, something to clean, working, or just done (its result stays).</summary>
    IEnumerable<CleanupKind> Shown() => Enum.GetValues<CleanupKind>().Where(k =>
        !_module.Sizes.TryGetValue(k, out var s) || s is null || s.Value.Files > 0 || _module.IsWorking(k) || _module.Result(k) is not null);

    static int ToTidy(HealthModule m) =>
        Enum.GetValues<CleanupKind>().Count(k => m.Sizes.TryGetValue(k, out var s) && s is { Files: > 0 }) + (m.Report is { RestartForUpdates: true } ? 1 : 0);

    void Refresh()
    {
        int tidy = ToTidy(_module);
        bool measuring = Enum.GetValues<CleanupKind>().Any(k => !_module.Sizes.TryGetValue(k, out var s) || s is null);
        Status = tidy > 0 ? (tidy == 1 ? "1 thing to tidy" : $"{tidy} things to tidy") : measuring ? "measuring…" : "all tidy";

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.Children.Add(Storage());
        var cleanups = Cleanups();
        Grid.SetColumn(cleanups, 2);
        top.Children.Add(cleanups);

        var col = new StackPanel();
        col.Children.Add(top);
        var checks = Checks();
        checks.Margin = new Thickness(0, 14, 0, 0);
        col.Children.Add(checks);
        Body.Children.Clear();
        Body.Children.Add(col);
    }

    // ---- Storage -------------------------------------------------------------------------------------

    FrameworkElement Storage()
    {
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var root = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
        var main = _module.Report?.System is { } sys && _module.Drives.FirstOrDefault(d => d.Name == sys.Name) is { } fresh ? fresh
            : _module.Drives.FirstOrDefault(d => string.Equals(d.Name, root, StringComparison.OrdinalIgnoreCase)) ?? _module.Drives.FirstOrDefault();
        if (main is null)
        {
            col.Children.Add(Sheet.Meta("Reading the drives…", 13));
            return col;
        }

        var (amount, unit) = Split(StorageText.Size(main.Free));
        var big = Sheet.Num(amount, 52, Ink.Soft);
        big.TextTrimming = TextTrimming.None;
        var free = Sheet.Word($"{unit} free", 15);
        free.VerticalAlignment = VerticalAlignment.Bottom;
        free.Margin = new Thickness(6, 0, 0, 8);
        col.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { big, free } });

        var bar = Sheet.Bar(main.UsedFraction);
        bar.Margin = new Thickness(0, 8, 0, 8);
        bar.ToolTip = StorageText.Free(main.Free, main.Total);
        col.Children.Add(bar);
        col.Children.Add(Sheet.Meta($"{main.Name} · {StorageText.Size(main.Total - main.Free)} of {StorageText.Size(main.Total)} used{(main.Low ? " · low" : "")}", 12));
        foreach (var d in _module.Drives.Where(d => d != main).Take(2))
        {
            var other = Sheet.Meta($"{d.Name} · {StorageText.Free(d.Free, d.Total)}{(d.Low ? " · low" : "")}", 12);
            other.Margin = new Thickness(0, 4, 0, 0);
            col.Children.Add(other);
        }
        CardKit.Name(col, $"Storage: {main.Name} {StorageText.Free(main.Free, main.Total)}");
        return col;
    }

    /// <summary>"19 GB" → ("19", "GB").</summary>
    static (string Amount, string Unit) Split(string size)
    {
        int space = size.LastIndexOf(' ');
        return space < 0 ? (size, "") : (size[..space], size[(space + 1)..]);
    }

    // ---- Clean-up ------------------------------------------------------------------------------------

    FrameworkElement Cleanups()
    {
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var shown = Shown().ToList();
        // The biggest saving gets the accent.
        CleanupKind? biggest = shown
            .Where(k => !_module.IsWorking(k) && _module.Sizes.TryGetValue(k, out var s) && s is { Files: > 0 })
            .OrderByDescending(k => _module.Sizes[k]!.Value.Bytes)
            .Select(k => (CleanupKind?)k)
            .FirstOrDefault();
        foreach (var kind in shown)
        {
            var row = CleanupRow(kind, kind == biggest);
            if (col.Children.Count > 0) row.Margin = new Thickness(0, 8, 0, 0);
            col.Children.Add(row);
        }
        if (col.Children.Count == 0)
        {
            var line = Sheet.Meta("", 13);
            line.Inlines.Add(CardKit.Run("Nothing to clean up. ", FontWeights.Bold, CardKit.Ink));
            line.Inlines.Add(new System.Windows.Documents.Run("Temporary files, the Recycle Bin and old downloads are clear."));
            line.TextWrapping = TextWrapping.Wrap;
            line.TextTrimming = TextTrimming.None;
            col.Children.Add(line);
        }
        return col;
    }

    FrameworkElement CleanupRow(CleanupKind kind, bool accent)
    {
        var size = _module.Sizes.TryGetValue(kind, out var s) ? s : null;
        bool working = _module.IsWorking(kind);
        var result = _module.Result(kind);
        string sizeText = size is { } v ? (v.Files == 0 ? "Nothing to clean" : $"{StorageText.Size(v.Bytes)} · {Cleanup.Count(kind, v.Files)}") : "Measuring…";
        // What it just did stays in the row (the card redraws often); what's left follows it.
        string line = working ? "Working…"
            : result is { } said ? said.Text + (size is { Files: > 0 } left && !said.Failed ? $" · {StorageText.Size(left.Bytes)} left" : "")
            : sizeText;
        var detail = Sheet.Meta(line, 12);
        if (result is { Failed: true } && !working) detail.Foreground = Ink.Soft;
        detail.ToolTip = line;
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, ToolTip = Cleanup.Explain(kind) };
        words.Children.Add(Sheet.Word(Cleanup.Name(kind), 13));
        words.Children.Add(detail);
        var grid = new Grid { MinHeight = 28 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(words);
        if (size is { Files: > 0 } some && !working)
        {
            bool armed = _step.Is(kind);
            // First click: say what will happen, in red. Second click (within six seconds): do it.
            void Click()
            {
                if (!_step.Is(kind))
                {
                    _step.Arm(kind);
                    return;
                }
                _step.Disarm();
                _ = _module.CleanAsync(kind);
            }
            Pressable button;
            if (accent && !armed) button = ScreenKit.Primary(Cleanup.Verb(kind), Click, 28, 12, padding: 12);
            else
            {
                button = ScreenKit.Secondary(armed ? $"Sure? {StorageText.Size(some.Bytes)}" : Cleanup.Verb(kind), Click, height: 28, fontSize: 12);
                ConfirmStep.Look(button, armed);
            }
            button.Margin = new Thickness(10, 0, 0, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
        }
        return Sheet.Tile(grid, new Thickness(14, 8, 8, 8), 18);
    }

    // ---- Checks --------------------------------------------------------------------------------------

    FrameworkElement Checks()
    {
        var wrap = new WrapPanel();
        if (_module.Report is not { } r)
        {
            wrap.Children.Add(Chip("Checking…", false));
            return wrap;
        }
        bool offerRestart = _module.OffersRestart;
        wrap.Children.Add(Chip(r.Battery is { } b ? $"Battery holds {b.Percent}% of new" : "No battery (or not readable)", r.Battery is { Percent: < HealthText.WornBelow }));
        // A long time since the last restart isn't an alarm (Fast Startup keeps it counting); it offers one instead.
        wrap.Children.Add(Chip(HealthText.Uptime(r.Uptime), false, offerRestart && !r.RestartForUpdates ? RestartButton() : null));
        wrap.Children.Add(Chip($"{r.StartupApps} apps start with Windows", false));
        if (r.RestartForUpdates) wrap.Children.Add(Chip("Restart to finish updates", true, RestartButton()));
        if (_module.NeedsAttention && !_module.LineHidden)
        {
            var hide = ScreenKit.Secondary("Hide this line for a week", _module.HideForAWeek, height: 28, fontSize: 12);
            hide.ToolTip = "The daily health line in the notch stays away for 7 days";
            hide.Margin = new Thickness(0, 0, 8, 8);
            wrap.Children.Add(hide);
        }
        return wrap;
    }

    /// <summary>
    /// A quiet check chip with a palette dot; one that wants attention takes the accent's tint and soft words. An
    /// action (Restart) sits at the chip's end.
    /// </summary>
    static FrameworkElement Chip(string text, bool attention, FrameworkElement? action = null)
    {
        var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = attention ? Ink.Soft : Ink.C, VerticalAlignment = VerticalAlignment.Center };
        var words = CardKit.Text(text, 12, FontWeights.SemiBold);
        words.Foreground = attention ? Ink.Soft : Ui.Tokens.Brush("#D9EBEBF5");
        words.VerticalAlignment = VerticalAlignment.Center;
        words.Margin = new Thickness(8, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { dot, words } };
        if (action is not null)
        {
            action.Margin = new Thickness(10, 0, 0, 0);
            action.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(action);
        }
        return new Border
        {
            MinHeight = 28,
            CornerRadius = new CornerRadius(14),
            Background = attention ? Ink.Tint : Ui.Tokens.Brush("#0FFFFFFF"),
            Padding = new Thickness(12, 0, action is null ? 12 : 3, 0),
            Margin = new Thickness(0, 0, 8, 8),
            Child = row,
        };
    }

    /// <summary>Restart, asked twice: the first click turns it red ("Sure?"), the second restarts; six seconds and it's plain again.</summary>
    Pressable RestartButton()
    {
        bool armed = _step.Is(RestartStep);
        var button = ScreenKit.Secondary(armed ? "Sure?" : "Restart", () =>
        {
            if (!_step.Is(RestartStep))
            {
                _step.Arm(RestartStep);
                return;
            }
            _step.Disarm();
            _module.Restart();
        }, height: 22, fontSize: 11);
        ConfirmStep.Look(button, armed);
        return button;
    }

    /// <summary>The card's tile for the rail.</summary>
    public static FrameworkElement BuildCompact(Action open)
    {
        var tile = new WallTile { Field = Fields.System() };
        tile.Rest.Children.Add(WallTile.BottomLeft(CardKit.Poster("Health", 26)));
        var words = WallTile.Words("Health", "Storage, cleanup and checks", out _, out _);
        tile.Lit.Children.Add(CardKit.At(words, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, WallTile.Pad, 0, WallTile.Pad, 10));
        tile.Lit.Children.Add(WallTile.OpenButton("Open health", open));
        CardKit.Name(tile, "Health. Storage, cleanup and daily checks");
        return tile;
    }
}
