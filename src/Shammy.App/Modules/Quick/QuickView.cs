using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Quick;
using Shammy.Views.Cards;
using Shammy.Views.Screens;
using Path = System.Windows.Shapes.Path;

namespace Shammy.Modules.Quick;

/// <summary>
/// The Quick actions view in the open notch: three by two tiles under the notch's own Back and title row. Each
/// tile is an icon, a name and a small line with its state; Keep awake also carries its length. Nothing here
/// animates or ticks: the module says when something changed.
/// </summary>
internal sealed class QuickView : Grid
{
    const double Gap = 8, ViewHeight = 204;

    readonly QuickModule _module;
    readonly QuickTile _awake, _mic;
    readonly Segmented _lengths;

    public QuickView(QuickModule module)
    {
        _module = module;
        Width = WallDetail.W;
        Height = ViewHeight;
        for (int c = 0; c < 5; c++)
            ColumnDefinitions.Add(new ColumnDefinition { Width = c % 2 == 1 ? new GridLength(Gap) : new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < 3; r++)
            RowDefinitions.Add(new RowDefinition { Height = r == 1 ? new GridLength(Gap) : new GridLength(1, GridUnitType.Star) });

        _awake = new QuickTile(Icons.Cup, "Keep awake", "Off", module.ToggleKeepAwake);
        _lengths = new Segmented(KeepAwakeClock.Choices.Select(KeepAwakeText.Long).ToList(), Choice(module.Pick),
            i => module.PickLength(KeepAwakeClock.Choices[i]), minWidth: 38, height: 24, fontSize: 11.5)
        {
            ToolTip = "How long to keep awake",
        };
        // A click on the control's own edge is not a click on the tile.
        _lengths.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _awake.Corner = _lengths;
        Place(_awake, 0, 0);
        Place(new QuickTile(Icons.Lock, "Lock", "Lock this PC", () => module.Lock()), 0, 1);
        Place(new QuickTile(Icons.Moon, "Sleep", "Put the PC to sleep", () => module.Sleep()), 0, 2);
        // Windows has no supported switch for these two, so the tiles open their Settings pages.
        Place(new QuickTile(Icons.Sun, "Night light", "Open settings", () => module.OpenSettings(QuickModule.NightLightPage)), 1, 0);
        Place(new QuickTile(Icons.BellOff, "Do not disturb", "Open settings", () => module.OpenSettings(QuickModule.DoNotDisturbPage)), 1, 1);
        _mic = new QuickTile(Icons.Mic, "Mute mic", "Mic is live", module.ToggleMic);
        Place(_mic, 1, 2);

        Loaded += (_, _) =>
        {
            _module.Changed -= Refresh;
            _module.Changed += Refresh;
            Refresh();
        };
        Unloaded += (_, _) => _module.Changed -= Refresh;
        Refresh();
    }

    void Place(QuickTile tile, int row, int column)
    {
        SetRow(tile, row * 2);
        SetColumn(tile, column * 2);
        Children.Add(tile);
    }

    static int Choice(int minutes)
    {
        for (int i = 0; i < KeepAwakeClock.Choices.Count; i++)
            if (KeepAwakeClock.Choices[i] == minutes) return i;
        return -1;
    }

    void Refresh()
    {
        bool on = _module.KeepAwakeOn;
        _awake.On = on;
        _awake.Line = KeepAwakeText.TileLine(on, _module.MinutesLeft);
        // While it runs the chosen segment is the running length (none for one asked for by voice, like 45 min).
        int index = Choice(on ? _module.StartedMinutes : _module.Pick);
        if (_lengths.Index != index) _lengths.Index = index;

        bool? muted = _module.MicMuted;
        _mic.Line = KeepAwakeText.MicLine(muted);
        // Red is for meaning only: a muted microphone.
        _mic.IconBrush = muted == true ? CardKit.Alarm : ScreenKit.Fg;
    }

    /// <summary>What the tiles say right now, for the test pipe.</summary>
    public string Describe() => $"awake=\"{_awake.Line}\"{(_awake.On ? " (on)" : "")} length={_lengths.Index} mic=\"{_mic.Line}\"";

    /// <summary>The mockup's icons (24×24, stroked).</summary>
    static class Icons
    {
        public const string Cup = "M5 9h11v6a4 4 0 0 1-4 4H9a4 4 0 0 1-4-4z M16 10h1.5a2.5 2.5 0 0 1 0 5H16 M8 3v2 M12 3v2";
        public const string Lock = "M7.5 11h9a2.5 2.5 0 0 1 2.5 2.5v4a2.5 2.5 0 0 1-2.5 2.5h-9a2.5 2.5 0 0 1-2.5-2.5v-4a2.5 2.5 0 0 1 2.5-2.5z M8 11V8a4 4 0 0 1 8 0v3";
        public const string Moon = "M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z";
        public const string Sun = "M12 8a4 4 0 1 1 0 8a4 4 0 1 1 0-8z M12 3v2 M12 19v2 M3 12h2 M19 12h2 M5.6 5.6l1.4 1.4 M17 17l1.4 1.4 M5.6 18.4L7 17 M17 7l1.4-1.4";
        public const string BellOff = "M6 16V11a6 6 0 0 1 12 0v5l1.5 2h-15z M10 21h4 M4 4l16 16";
        public const string Mic = "M9 6a3 3 0 0 1 6 0v5a3 3 0 0 1-6 0z M5 11a7 7 0 0 0 14 0 M12 18v3";
    }

    /// <summary>The card's tile for the rail (it's opened from its chip, so this only shows if the rail ever asks).</summary>
    public static FrameworkElement BuildCompact(Action open)
    {
        var tile = new WallTile { Field = Fields.System() };
        tile.Art.Children.Add(CardKit.At(CardKit.Icon(Icons.Cup, CardKit.Ink, 30, 1.8), HorizontalAlignment.Left, VerticalAlignment.Top, 14, 14));
        tile.Rest.Children.Add(WallTile.BottomLeft(CardKit.Poster("Quick", 26)));
        var words = WallTile.Words("Quick actions", "Keep awake, lock, sleep", out _, out _);
        tile.Lit.Children.Add(CardKit.At(words, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, WallTile.Pad, 0, WallTile.Pad, 10));
        tile.Lit.Children.Add(WallTile.OpenButton("Open quick actions", open));
        CardKit.Name(tile, "Quick actions. Keep awake, lock, sleep and more");
        return tile;
    }
}

/// <summary>
/// One quick action: the icon top-left, the name and its state line at the foot, on the wall's tile surface with
/// its hover and press. <see cref="On"/> is the accent-tinted state (keep-awake running): the one thing switched on.
/// </summary>
internal sealed class QuickTile : Pressable
{
    static readonly Brush OnFill = ScreenKit.B("#2A1A0E"), OnFillHover = ScreenKit.B("#35210F"),
        OnRing = ScreenKit.B("#6B3D1C"), OnRingHover = ScreenKit.B("#8A4F24");

    readonly Path _icon;
    readonly TextBlock _line;
    readonly Grid _grid = new();
    readonly string _name;
    Brush _iconBrush = ScreenKit.Fg;
    bool _on;

    public QuickTile(string icon, string name, string line, Action click)
        : base(ScreenKit.Tile, ScreenKit.TileHover, ScreenKit.Hairline, ScreenKit.HairlineHover)
    {
        _name = name;
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(12, 11, 12, 11);
        PressScale = 0.98;

        _icon = new Path
        {
            Data = Geometry.Parse(icon),
            Stroke = _iconBrush,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(_icon);
        _grid.Children.Add(new Viewbox
        {
            Width = 18,
            Height = 18,
            Child = canvas,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        });

        _line = ScreenKit.Text(line, 11.5, Weight.Medium, ScreenKit.Fg2);
        _line.Margin = new Thickness(0, 1, 0, 0);
        var words = ScreenKit.Column(0, ScreenKit.Text(name, 13, Weight.SemiBold), _line);
        words.VerticalAlignment = VerticalAlignment.Bottom;
        words.IsHitTestVisible = false;
        _grid.Children.Add(words);
        Child = _grid;

        Click += click;
        // After the kit's own hover paint, so the switched-on tint wins.
        MouseEnter += (_, _) => Tint();
        MouseLeave += (_, _) => Tint();
        Say();
    }

    /// <summary>A control in the tile's top-right corner (keep-awake's lengths); its clicks stay its own.</summary>
    public FrameworkElement Corner
    {
        set
        {
            value.HorizontalAlignment = HorizontalAlignment.Right;
            value.VerticalAlignment = VerticalAlignment.Top;
            _grid.Children.Add(value);
        }
    }

    public string Line
    {
        get => _line.Text;
        set
        {
            if (_line.Text == value) return;
            _line.Text = value;
            Say();
        }
    }

    public bool On
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            Tint();
        }
    }

    public Brush IconBrush
    {
        set
        {
            if (ReferenceEquals(_iconBrush, value)) return;
            _iconBrush = value;
            Tint();
        }
    }

    void Tint()
    {
        Paint();
        if (_on)
        {
            Background = IsMouseOver ? OnFillHover : OnFill;
            BorderBrush = IsMouseOver ? OnRingHover : OnRing;
        }
        _icon.Stroke = _on ? ScreenKit.Accent : _iconBrush;
    }

    void Say() => CardKit.Name(this, $"{_name}. {_line.Text}");
}
