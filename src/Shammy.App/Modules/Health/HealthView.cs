using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Quick;
using Shammy.Modules.Quick;
using Shammy.Views.Cards;
using Shammy.Views.Screens;

namespace Shammy.Modules.Health;

/// <summary>
/// The Health card: drives with a bar each on the left, the three cleanups in the middle (size, a word on what
/// goes, and a button that turns red and asks "Sure?" before it does anything; what it did stays in the row), and
/// the daily checks on the right, with a Restart (asked twice, like the Laptop card's) when updates wait or the
/// last restart was a week ago, and a way to hide the daily line for a week.
/// </summary>
internal sealed class HealthView : Grid
{
    const double ViewHeight = 204;
    const string RestartStep = "restart";

    readonly HealthModule _module;
    readonly StackPanel _drives = new(), _cleanups = new(), _checks = new();
    readonly ConfirmStep _step;

    public HealthView(HealthModule module)
    {
        _module = module;
        _step = new ConfirmStep(Dispatcher, Refresh);
        Width = WallDetail.W;
        Height = ViewHeight;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.45, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        Add(Panel("Storage", _drives), 0);
        Add(Panel("Clean up", _cleanups), 2);
        Add(Panel("Checks", _checks), 4);

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

    void Add(UIElement e, int column)
    {
        SetColumn(e, column);
        Children.Add(e);
    }

    static Border Panel(string title, StackPanel body)
    {
        var stack = new StackPanel();
        var label = ScreenKit.Text(title.ToUpperInvariant(), 11, Weight.Bold, ScreenKit.Fg2);
        label.Margin = new Thickness(0, 0, 0, 8);
        stack.Children.Add(label);
        stack.Children.Add(body);
        return new Border
        {
            Background = ScreenKit.Tile,
            BorderBrush = ScreenKit.Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Ui.Tokens.DetailRadius),
            Padding = new Thickness(14, 12, 14, 12),
            Child = stack,
        };
    }

    void Refresh()
    {
        _drives.Children.Clear();
        foreach (var d in _module.Drives.Take(3))
        {
            var name = ScreenKit.Text($"{d.Name}  {StorageText.Free(d.Free, d.Total)}", 12.5, Weight.SemiBold);
            var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Ui.Tokens.Track, Margin = new Thickness(0, 6, 0, 12) };
            var fill = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = d.Low ? Ui.Tokens.Alarm : Ui.Tokens.Text,
            };
            track.Child = fill;
            track.SizeChanged += (_, e) => fill.Width = Math.Max(6, e.NewSize.Width * d.UsedFraction);
            _drives.Children.Add(name);
            _drives.Children.Add(track);
        }
        if (_module.Drives.Count == 0) _drives.Children.Add(ScreenKit.Text("Reading the drives…", 12.5, Weight.Medium, ScreenKit.Fg2));

        _cleanups.Children.Clear();
        foreach (var kind in Enum.GetValues<CleanupKind>()) _cleanups.Children.Add(CleanupRow(kind));

        _checks.Children.Clear();
        if (_module.Report is not { } r)
        {
            _checks.Children.Add(ScreenKit.Text("Checking…", 12.5, Weight.Medium, ScreenKit.Fg2));
            return;
        }
        bool offerRestart = _module.OffersRestart;
        Check(r.Battery is { } b ? $"Battery holds {b.Percent}% of new" : "No battery (or not readable)", r.Battery is { Percent: < HealthText.WornBelow });
        // A long time since the last restart isn't an alarm (Fast Startup keeps it counting); it offers one instead.
        Check(HealthText.Uptime(r.Uptime), false, offerRestart && !r.RestartForUpdates ? RestartButton() : null);
        Check($"{r.StartupApps} apps start with Windows", false);
        if (r.RestartForUpdates) Check("Restart to finish updates", true, RestartButton());
        if (_module.NeedsAttention && !_module.LineHidden)
        {
            var hide = ScreenKit.Secondary("Hide this line for a week", _module.HideForAWeek, height: 22, fontSize: 11);
            hide.HorizontalAlignment = HorizontalAlignment.Left;
            hide.ToolTip = "The daily health line in the notch stays away for 7 days";
            _checks.Children.Add(hide);
        }
    }

    void Check(string text, bool attention, FrameworkElement? action = null)
    {
        var words = ScreenKit.Row(8,
            new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = attention ? Ui.Tokens.Alarm : Ui.Tokens.Ok, VerticalAlignment = VerticalAlignment.Center },
            ScreenKit.Text(text, 12.5, Weight.Medium));
        words.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { Margin = new Thickness(0, 0, 0, action is null ? 9 : 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(words);
        if (action is not null)
        {
            SetColumn(action, 1);
            action.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(action);
        }
        _checks.Children.Add(row);
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

    FrameworkElement CleanupRow(CleanupKind kind)
    {
        var size = _module.Sizes.TryGetValue(kind, out var s) ? s : null;
        bool working = _module.IsWorking(kind);
        var result = _module.Result(kind);
        string sizeText = size is { } v ? (v.Files == 0 ? "Nothing to clean" : $"{StorageText.Size(v.Bytes)} · {Cleanup.Count(kind, v.Files)}") : "Measuring…";
        // What it just did stays in the row (the card redraws often); what's left follows it.
        string line = working ? "Working…"
            : result is { } said ? said.Text + (size is { Files: > 0 } left && !said.Failed ? $" · {StorageText.Size(left.Bytes)} left" : "")
            : sizeText;
        var detail = ScreenKit.Text(line, 11.5, Weight.Medium, result is { Failed: true } && !working ? CardKit.Alarm : ScreenKit.Fg2);
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        var words = ScreenKit.Column(1, ScreenKit.Text(Cleanup.Name(kind), 13, Weight.SemiBold), detail);
        words.ToolTip = Cleanup.Explain(kind);
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(words);
        if (size is { Files: > 0 } some && !working)
        {
            bool armed = _step.Is(kind);
            // First click: say what will happen, in red. Second click (within six seconds): do it.
            var button = ScreenKit.Secondary(armed ? $"Sure? {StorageText.Size(some.Bytes)}" : Cleanup.Verb(kind), () =>
            {
                if (!_step.Is(kind))
                {
                    _step.Arm(kind);
                    return;
                }
                _step.Disarm();
                _ = _module.CleanAsync(kind);
            }, height: 26, fontSize: 11.5);
            ConfirmStep.Look(button, armed);
            SetColumn(button, 1);
            button.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(button);
        }
        return grid;
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
