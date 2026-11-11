using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Quick;
using Shammy.Views.Cards;
using Shammy.Views.Screens;
using Path = System.Windows.Shapes.Path;

namespace Shammy.Modules.Quick;

/// <summary>
/// The Laptop view in the open notch: five by two tiles under the notch's own Back and title row, the laptop's
/// controls in one place. Wi-Fi, Bluetooth, the power mode, keep-awake and the microphone on top; night light,
/// storage, lock, sleep and restart / shut down below (that last one always asks which, and needs a second
/// click). Each tile is an icon, a name and a small line with its state. Nothing here animates or ticks: the
/// module says when something changed.
/// </summary>
internal sealed class QuickView : Grid
{
    const double Gap = 8, ViewHeight = 204;

    readonly QuickModule _module;
    readonly QuickTile _awake, _mic, _wifi, _bluetooth, _power, _storage, _off;
    readonly Segmented _lengths;
    readonly FrameworkElement _offChoice;
    System.Windows.Threading.DispatcherTimer? _offRevert;

    public QuickView(QuickModule module)
    {
        _module = module;
        Width = WallDetail.W;
        Height = ViewHeight;
        for (int c = 0; c < 9; c++)
            ColumnDefinitions.Add(new ColumnDefinition { Width = c % 2 == 1 ? new GridLength(Gap) : new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < 3; r++)
            RowDefinitions.Add(new RowDefinition { Height = r == 1 ? new GridLength(Gap) : new GridLength(1, GridUnitType.Star) });

        _awake = new QuickTile(Icons.Cup, "Keep awake", "Off", module.ToggleKeepAwake);
        _lengths = new Segmented(KeepAwakeClock.Choices.Select(m => m < 60 ? $"{m}m" : $"{m / 60}h").ToList(), Choice(module.Pick),
            i => module.PickLength(KeepAwakeClock.Choices[i]), minWidth: 24, height: 22, fontSize: 10.5)
        {
            ToolTip = "How long to keep awake",
        };
        // A click on the control's own edge is not a click on the tile.
        _lengths.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _awake.Corner = _lengths;
        _wifi = new QuickTile(Icons.Wifi, "Wi-Fi", "…", module.ToggleWifi);
        _bluetooth = new QuickTile(Icons.Bluetooth, "Bluetooth", "…", module.ToggleBluetooth);
        _power = new QuickTile(Icons.Bolt, "Power mode", "…", module.CyclePowerMode);
        _mic = new QuickTile(Icons.Mic, "Mute mic", "Mic is live", module.ToggleMic);
        Place(_wifi, 0, 0);
        Place(_bluetooth, 0, 1);
        Place(_power, 0, 2);
        Place(_awake, 0, 3);
        Place(_mic, 0, 4);

        // Windows has no supported switch for night light, so the tile opens its Settings page.
        Place(new QuickTile(Icons.Sun, "Night light", "Open settings", () => module.OpenSettings(QuickModule.NightLightPage)), 1, 0);
        _storage = new QuickTile(Icons.Drive, "Storage", "…", module.OpenStorage);
        Place(_storage, 1, 1);
        Place(new QuickTile(Icons.Lock, "Lock", "Lock this PC", () => module.Lock()), 1, 2);
        Place(new QuickTile(Icons.Moon, "Sleep", "Put the PC to sleep", () => module.Sleep()), 1, 3);
        // Restart and shut down: the first click only offers the two; one of them has to be clicked to happen.
        var restart = ScreenKit.Secondary("Restart", () => { HideOffChoice(); module.PowerOff(restart: true); }, height: 24, fontSize: 11.5);
        var shutDown = ScreenKit.Secondary("Shut down", () => { HideOffChoice(); module.PowerOff(restart: false); }, height: 24, fontSize: 11.5);
        _offChoice = ScreenKit.Row(6, restart, shutDown);
        _offChoice.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _offChoice.Visibility = Visibility.Collapsed;
        _off = new QuickTile(Icons.Power, "Restart or shut down", "Asks first", ShowOffChoice);
        _off.Corner = _offChoice;
        Place(_off, 1, 4);

        Loaded += (_, _) =>
        {
            _module.Changed -= Refresh;
            _module.Changed += Refresh;
            Refresh();
            _ = _module.RefreshSwitchesAsync();
        };
        Unloaded += (_, _) => _module.Changed -= Refresh;
        Refresh();
    }

    void ShowOffChoice()
    {
        _offChoice.Visibility = Visibility.Visible;
        _off.Line = "Pick one";
        _offRevert ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(6), System.Windows.Threading.DispatcherPriority.Background, (_, _) => HideOffChoice(), Dispatcher);
        _offRevert.Stop();
        _offRevert.Start();
    }

    void HideOffChoice()
    {
        _offRevert?.Stop();
        _offChoice.Visibility = Visibility.Collapsed;
        _off.Line = "Asks first";
    }

    /// <summary>Test hook: the first click on restart / shut down (it only offers the two).</summary>
    internal void ClickOffForTest() => ShowOffChoice();

    internal bool OffChoiceShown => _offChoice.Visibility == Visibility.Visible;

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

        _wifi.Line = _module.WifiOn switch { true => "On", false => "Off", null => "Not found" };
        _wifi.On = _module.WifiOn == true;
        _bluetooth.Line = _module.BluetoothOn switch { true => "On", false => "Off", null => "Not found" };
        _bluetooth.On = _module.BluetoothOn == true;
        _power.Line = _module.PowerModeNow is { } mode ? Core.Quick.PowerMode.Name(mode) : "Open settings";
        _storage.Line = _module.StorageLine();

        bool? muted = _module.MicMuted;
        _mic.Line = KeepAwakeText.MicLine(muted);
        // Red is for meaning only: a muted microphone.
        _mic.IconBrush = muted == true ? CardKit.Alarm : ScreenKit.Fg;
    }

    /// <summary>What the tiles say right now, for the test pipe.</summary>
    public string Describe() =>
        $"awake=\"{_awake.Line}\"{(_awake.On ? " (on)" : "")} length={_lengths.Index} mic=\"{_mic.Line}\" " +
        $"wifi=\"{_wifi.Line}\" bluetooth=\"{_bluetooth.Line}\" power=\"{_power.Line}\" storage=\"{_storage.Line}\" off=\"{_off.Line}\"";

    /// <summary>The mockup's icons (24×24, stroked).</summary>
    static class Icons
    {
        public const string Cup = "M5 9h11v6a4 4 0 0 1-4 4H9a4 4 0 0 1-4-4z M16 10h1.5a2.5 2.5 0 0 1 0 5H16 M8 3v2 M12 3v2";
        public const string Lock = "M7.5 11h9a2.5 2.5 0 0 1 2.5 2.5v4a2.5 2.5 0 0 1-2.5 2.5h-9a2.5 2.5 0 0 1-2.5-2.5v-4a2.5 2.5 0 0 1 2.5-2.5z M8 11V8a4 4 0 0 1 8 0v3";
        public const string Moon = "M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z";
        public const string Sun = "M12 8a4 4 0 1 1 0 8a4 4 0 1 1 0-8z M12 3v2 M12 19v2 M3 12h2 M19 12h2 M5.6 5.6l1.4 1.4 M17 17l1.4 1.4 M5.6 18.4L7 17 M17 7l1.4-1.4";
        public const string BellOff = "M6 16V11a6 6 0 0 1 12 0v5l1.5 2h-15z M10 21h4 M4 4l16 16";
        public const string Mic = "M9 6a3 3 0 0 1 6 0v5a3 3 0 0 1-6 0z M5 11a7 7 0 0 0 14 0 M12 18v3";
        public const string Wifi = "M2.5 9a14 14 0 0 1 19 0 M5.5 12.5a9.5 9.5 0 0 1 13 0 M8.8 16a5 5 0 0 1 6.4 0 M12 19.5h.01";
        public const string Bluetooth = "M7 7l10 10-5 4V3l5 4L7 17";
        public const string Bolt = "M13 3L5 13.5h6L10 21l8-10.5h-6z";
        public const string Drive = "M4 7a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2z M8 15h.01 M12 15h4";
        public const string Power = "M12 3v8 M6.5 6.5a7.8 7.8 0 1 0 11 0";
    }

    /// <summary>The card's tile for the rail (it's opened from its chip, so this only shows if the rail ever asks).</summary>
    public static FrameworkElement BuildCompact(Action open)
    {
        var tile = new WallTile { Field = Fields.System() };
        tile.Art.Children.Add(CardKit.At(CardKit.Icon(Icons.Power, CardKit.Ink, 30, 1.8), HorizontalAlignment.Left, VerticalAlignment.Top, 14, 14));
        tile.Rest.Children.Add(WallTile.BottomLeft(CardKit.Poster("Laptop", 26)));
        var words = WallTile.Words("Laptop", "Wi-Fi, power, lock and more", out _, out _);
        tile.Lit.Children.Add(CardKit.At(words, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, WallTile.Pad, 0, WallTile.Pad, 10));
        tile.Lit.Children.Add(WallTile.OpenButton("Open the laptop controls", open));
        CardKit.Name(tile, "Laptop. Wi-Fi, Bluetooth, power mode, lock, sleep and more");
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
