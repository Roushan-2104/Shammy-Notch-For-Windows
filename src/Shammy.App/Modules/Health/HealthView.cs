using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Quick;
using Shammy.Views.Cards;
using Shammy.Views.Screens;

namespace Shammy.Modules.Health;

/// <summary>
/// The Health card: drives with a bar each on the left, the three cleanups in the middle (size, a word on what
/// goes, and a button that asks "Sure?" before it does anything), and the daily checks on the right.
/// </summary>
internal sealed class HealthView : Grid
{
    const double ViewHeight = 204;

    readonly HealthModule _module;
    readonly StackPanel _drives = new(), _cleanups = new(), _checks = new();

    public HealthView(HealthModule module)
    {
        _module = module;
        Width = WallDetail.W;
        Height = ViewHeight;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.45, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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
        Unloaded += (_, _) => _module.Changed -= Refresh;
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
            CornerRadius = new CornerRadius(14),
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
        Check(r.Battery is { } b ? $"Battery holds {b.Percent}% of new" : "No battery (or not readable)", r.Battery is { Percent: < 70 });
        Check(HealthText.Uptime(r.Uptime), r.Uptime.TotalDays >= 14);
        Check($"{r.StartupApps} apps start with Windows", false);
        if (r.RestartForUpdates) Check("Restart to finish updates", true);
    }

    void Check(string text, bool attention)
    {
        var row = ScreenKit.Row(8,
            new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = attention ? Ui.Tokens.Alarm : Ui.Tokens.Brush("#34C759"), VerticalAlignment = VerticalAlignment.Center },
            ScreenKit.Text(text, 12.5, Weight.Medium));
        row.Margin = new Thickness(0, 0, 0, 9);
        _checks.Children.Add(row);
    }

    FrameworkElement CleanupRow(CleanupKind kind)
    {
        var size = _module.Sizes.TryGetValue(kind, out var s) ? s : null;
        string sizeText = size is { } v ? (v.Files == 0 ? "Nothing to clean" : $"{StorageText.Size(v.Bytes)} · {v.Files} file{(v.Files == 1 ? "" : "s")}") : "Measuring…";
        var words = ScreenKit.Column(1,
            ScreenKit.Text(Cleanup.Name(kind), 13, Weight.SemiBold),
            ScreenKit.Text(sizeText, 11.5, Weight.Medium, ScreenKit.Fg2));
        words.ToolTip = Cleanup.Explain(kind);
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(words);
        if (size is { Files: > 0 } some)
        {
            Pressable button = null!;
            bool armed = false;
            button = ScreenKit.Secondary(Cleanup.Verb(kind), async () =>
            {
                // First click: say what will happen. Second click: do it.
                if (!armed)
                {
                    armed = true;
                    SetLabel(button, $"Sure? {StorageText.Size(some.Bytes)}");
                    return;
                }
                button.Enabled = false;
                SetLabel(button, "Working…");
                var said = await _module.CleanAsync(kind);
                SetLabel(button, said);
            }, height: 26, fontSize: 11.5);
            SetColumn(button, 1);
            button.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(button);
        }
        return grid;
    }

    static void SetLabel(Pressable button, string text)
    {
        if (button.Child is TextBlock t) t.Text = text;
        else if (button.Child is Panel p && p.Children.OfType<TextBlock>().LastOrDefault() is { } last) last.Text = text;
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
