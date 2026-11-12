using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Quick;
using Shammy.Views.Cards;
using Shammy.Views.Screens;
using Path = System.Windows.Shapes.Path;

namespace Shammy.Modules.Quick;

/// <summary>
/// The Laptop view in the open notch: two rows of tiles under the notch's own Back and title row, the laptop's
/// controls in one place. Wi-Fi, Bluetooth, the power mode, keep-awake, the microphone and quiet on top; night
/// light, storage, lock, sleep and a wide restart / shut down below (that last one always asks which, and needs a
/// second click). Each tile is an icon, a name and a small line with its state; a switched-on tile wears the
/// accent colour. Nothing here animates or ticks: the module says when something changed.
/// </summary>
internal sealed class QuickView : Grid
{
    const double Gap = 8, ViewHeight = 204;
    const int Columns = 6;

    readonly QuickModule _module;
    readonly QuickTile _awake, _mic, _wifi, _bluetooth, _power, _quiet, _night, _storage, _off;
    readonly Segmented _lengths;
    readonly FrameworkElement _offChoice;
    readonly Pressable _restart, _shutDown;
    readonly ConfirmStep _offStep;

    public QuickView(QuickModule module)
    {
        _module = module;
        Width = WallDetail.W;
        Height = ViewHeight;
        for (int c = 0; c < Columns * 2 - 1; c++)
            ColumnDefinitions.Add(new ColumnDefinition { Width = c % 2 == 1 ? new GridLength(Gap) : new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < 3; r++)
            RowDefinitions.Add(new RowDefinition { Height = r == 1 ? new GridLength(Gap) : new GridLength(1, GridUnitType.Star) });

        _awake = new QuickTile(Icons.Cup, "Keep awake", "Off", module.ToggleKeepAwake);
        _lengths = new Segmented(KeepAwakeClock.Choices.Select(m => m < 60 ? $"{m}m" : $"{m / 60}h").ToList(), Choice(module.Pick),
            i => module.PickLength(KeepAwakeClock.Choices[i]), minWidth: 22, height: 22, fontSize: 10.5)
        {
            ToolTip = "How long to keep awake",
        };
        // A click on the control's own edge is not a click on the tile.
        _lengths.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _awake.Corner = _lengths;
        _wifi = new QuickTile(Icons.Wifi, "Wi-Fi", "Checking…", module.ToggleWifi);
        _bluetooth = new QuickTile(Icons.Bluetooth, "Bluetooth", "Checking…", module.ToggleBluetooth);
        _power = new QuickTile(Icons.Bolt, "Power mode", "Checking…", module.CyclePowerMode);
        _mic = new QuickTile(Icons.Mic, "Mute mic", "Mic is live", module.ToggleMic);
        // Shammy's own quiet: an hour of held pop-ups, a second tap ends it.
        _quiet = new QuickTile(Icons.BellOff, "Quiet", "Off", module.ToggleQuiet) { ToolTip = "Shammy holds its pop-ups for an hour" };
        Place(_wifi, 0, 0);
        Place(_bluetooth, 0, 1);
        Place(_power, 0, 2);
        Place(_awake, 0, 3);
        Place(_mic, 0, 4);
        Place(_quiet, 0, 5);

        // Shammy's own warm tint over every screen.
        _night = new QuickTile(Icons.Sun, "Night light", "Off", module.ToggleNightLight) { ToolTip = "A warm tint over every screen" };
        Place(_night, 1, 0);
        _storage = new QuickTile(Icons.Drive, "Storage", "…", module.OpenStorage);
        Place(_storage, 1, 1);
        Place(new QuickTile(Icons.Lock, "Lock", "Lock this PC", () => module.Lock()), 1, 2);
        Place(new QuickTile(Icons.Moon, "Sleep", "Put the PC to sleep", () => module.Sleep()), 1, 3);
        // Restart and shut down: the first click only offers the two (in red, like the Health card's "Sure?");
        // one of them has to be clicked within six seconds to happen.
        _offStep = new ConfirmStep(Dispatcher, ShowOff);
        _restart = ScreenKit.Secondary("Restart", () => { _offStep.Disarm(); module.PowerOff(restart: true); }, height: 24, fontSize: 11.5);
        _shutDown = ScreenKit.Secondary("Shut down", () => { _offStep.Disarm(); module.PowerOff(restart: false); }, height: 24, fontSize: 11.5);
        _offChoice = ScreenKit.Row(6, _restart, _shutDown);
        _offChoice.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _offChoice.Visibility = Visibility.Collapsed;
        _off = new QuickTile(Icons.Power, "Restart or shut down", "Asks first", () => _offStep.Arm("off"));
        _off.Corner = _offChoice;
        Place(_off, 1, 4, span: 2);

        Loaded += (_, _) =>
        {
            _module.Changed -= Refresh;
            _module.Changed += Refresh;
            Refresh();
            _ = _module.RefreshSwitchesAsync();
        };
        Unloaded += (_, _) =>
        {
            _module.Changed -= Refresh;
            _offStep.Disarm();
        };
        Refresh();
    }

    void ShowOff()
    {
        bool armed = _offStep.Is("off");
        _offChoice.Visibility = armed ? Visibility.Visible : Visibility.Collapsed;
        _off.Line = armed ? "Pick one" : "Asks first";
        ConfirmStep.Look(_restart, armed);
        ConfirmStep.Look(_shutDown, armed);
    }

    /// <summary>Test hook: the first click on restart / shut down (it only offers the two).</summary>
    internal void ClickOffForTest() => _offStep.Arm("off");

    internal bool OffChoiceShown => _offChoice.Visibility == Visibility.Visible;

    void Place(QuickTile tile, int row, int column, int span = 1)
    {
        SetRow(tile, row * 2);
        SetColumn(tile, column * 2);
        if (span > 1) SetColumnSpan(tile, span * 2 - 1);
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

        // Until the first read the radios are unknown, not missing.
        bool read = _module.SwitchesRead;
        _wifi.Line = !read ? "Checking…" : _module.WifiOn switch { true => "On", false => "Off", null => "Not found" };
        _wifi.On = _module.WifiOn == true;
        _bluetooth.Line = !read ? "Checking…" : _module.BluetoothOn switch { true => "On", false => "Off", null => "Not found" };
        _bluetooth.On = _module.BluetoothOn == true;
        _power.Line = _module.PowerModeNow is { } mode ? PowerMode.Name(mode) : read ? "Open settings" : "Checking…";
        _storage.Line = _module.StorageLine();

        _quiet.On = _module.QuietOn;
        _quiet.Line = _module.QuietOn ? KeepAwakeText.TileLine(true, _module.QuietMinutesLeft) : "Tap for 1 h";
        _night.On = _module.NightLightOn;
        _night.Line = _module.NightLightOn ? "On" : "Off";

        bool? muted = _module.MicMuted;
        _mic.Line = KeepAwakeText.MicLine(muted);
        // Red is for meaning only: a muted microphone.
        _mic.IconBrush = muted == true ? CardKit.Alarm : ScreenKit.Fg;

        // The accent may have changed since the tiles were drawn.
        foreach (var tile in Children.OfType<QuickTile>()) tile.Retint();
    }

    /// <summary>What the tiles say right now, for the test pipe.</summary>
    public string Describe() =>
        $"awake=\"{_awake.Line}\"{(_awake.On ? " (on)" : "")} length={_lengths.Index} mic=\"{_mic.Line}\" " +
        $"wifi=\"{_wifi.Line}\" bluetooth=\"{_bluetooth.Line}\" power=\"{_power.Line}\" quiet=\"{_quiet.Line}\" night=\"{_night.Line}\" " +
        $"storage=\"{_storage.Line}\" off=\"{_off.Line}\"";

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
        CardKit.Name(tile, "Laptop. Wi-Fi, Bluetooth, power mode, quiet, night light, lock, sleep and more");
        return tile;
    }
}

/// <summary>
/// One Laptop control: the icon top-left, the name and its state line at the foot, on the wall's tile surface with
/// its hover and press. <see cref="On"/> is the switched-on state, tinted with the accent the user picked (about
/// 14 % over the tile, a 40 % ring).
/// </summary>
internal sealed class QuickTile : Pressable
{
    const double FillShare = 0.14, FillHoverShare = 0.20, RingShare = 0.40, RingHoverShare = 0.55;

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
        CornerRadius = new CornerRadius(Ui.Tokens.DetailRadius);
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
        _line.TextTrimming = TextTrimming.CharacterEllipsis;
        var title = ScreenKit.Text(name, 13, Weight.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var words = ScreenKit.Column(0, title, _line);
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

    /// <summary>Paints again with the accent as it is now (the user may have picked another).</summary>
    public void Retint()
    {
        if (_on) Tint();
    }

    void Tint()
    {
        Paint();
        if (_on)
        {
            bool hover = IsMouseOver;
            Background = Mix(hover ? FillHoverShare : FillShare);
            BorderBrush = Mix(hover ? RingHoverShare : RingShare);
        }
        _icon.Stroke = _on ? ScreenKit.Accent : _iconBrush;
    }

    /// <summary>The accent laid over the tile's own colour at that share, as one solid colour.</summary>
    static SolidColorBrush Mix(double share)
    {
        var a = ScreenKit.Accent.Color;
        var t = ScreenKit.Tile.Color;
        byte Blend(byte under, byte over) => (byte)Math.Round(under + (over - under) * share);
        var brush = new SolidColorBrush(Color.FromRgb(Blend(t.R, a.R), Blend(t.G, a.G), Blend(t.B, a.B)));
        brush.Freeze();
        return brush;
    }

    void Say() => CardKit.Name(this, $"{_name}. {_line.Text}");
}
