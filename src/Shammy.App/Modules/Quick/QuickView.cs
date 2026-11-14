using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Shammy.Core.Quick;
using Shammy.Views.Cards;
using Shammy.Views.Screens;

namespace Shammy.Modules.Quick;

/// <summary>
/// The Laptop card (the card family's shape, 640 wide): the header holds Lock, Sleep and Power capsules (Power asks
/// first: its first click swaps it for Restart and Shut down in red, one of which has to be clicked within six
/// seconds). Under it, one row of seven round toggles: Wi-Fi, Bluetooth, the power mode, keep-awake, the microphone,
/// Shammy's quiet and night light, each a 54 px circle (the accent when on, glass when off) with its name and a
/// quiet state line. Keep awake's 30m / 1h / 2h / 4h choice shows under the row once it has been tapped. Nothing
/// here animates or ticks: the module says when something changed.
/// </summary>
internal sealed class QuickView : CardSheet
{
    public const double SheetWidth = 640;

    readonly QuickModule _module;
    readonly RoundToggle _awake, _mic, _wifi, _bluetooth, _power, _quiet, _night;
    readonly Segmented _lengths;
    readonly FrameworkElement _lengthRow;
    readonly Button _powerCap;
    readonly FrameworkElement _restart, _shutDown;
    readonly ConfirmStep _offStep;
    string _storage = "…", _offLine = "Asks first";
    bool _lengthsAsked;

    public QuickView(QuickModule module) : base("Laptop", SheetWidth)
    {
        _module = module;

        // The header: Lock, Sleep, Power. Power's first click only offers restart and shut down (in red, like the
        // Health card's "Sure?"); one of them has to be clicked within six seconds to happen.
        Action(Sheet.Cap("Lock", "Lock this PC", () => module.Lock(), Icons.Lock));
        Action(Sheet.Cap("Sleep", "Put the PC to sleep", () => module.Sleep(), Icons.Moon));
        _offStep = new ConfirmStep(Dispatcher, ShowOff);
        _powerCap = Action(Sheet.Cap("Power", "Restart or shut down. Asks first", () => _offStep.Arm("off"), Icons.Power));
        _restart = Action(Armed("Restart", () => { _offStep.Disarm(); module.PowerOff(restart: true); }));
        _shutDown = Action(Armed("Shut down", () => { _offStep.Disarm(); module.PowerOff(restart: false); }));
        _restart.Visibility = _shutDown.Visibility = Visibility.Collapsed;

        _wifi = new RoundToggle(Icons.Wifi, "Wi-Fi", "Checking…", module.ToggleWifi);
        _bluetooth = new RoundToggle(Icons.Bluetooth, "Bluetooth", "Checking…", module.ToggleBluetooth);
        _power = new RoundToggle(Icons.Bolt, "Power mode", "Checking…", module.CyclePowerMode);
        _awake = new RoundToggle(Icons.Cup, "Keep awake", "Off", () =>
        {
            _lengthsAsked = true;
            module.ToggleKeepAwake();
            ShowLengths();
        });
        _mic = new RoundToggle(Icons.Mic, "Mute mic", "Mic is live", module.ToggleMic);
        // Shammy's own quiet: an hour of held pop-ups, a second tap ends it.
        _quiet = new RoundToggle(Icons.BellOff, "Quiet", "Off", module.ToggleQuiet) { ToolTip = "Shammy holds its pop-ups for an hour" };
        // Shammy's own warm tint over every screen.
        _night = new RoundToggle(Icons.Sun, "Night light", "Off", module.ToggleNightLight) { ToolTip = "A warm tint over every screen" };

        var row = new UniformGrid { Rows = 1, Columns = 7, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var t in new[] { _wifi, _bluetooth, _power, _awake, _mic, _quiet, _night }) row.Children.Add(t);

        _lengths = new Segmented(KeepAwakeClock.Choices.Select(m => m < 60 ? $"{m}m" : $"{m / 60}h").ToList(), Choice(module.Pick),
            i => module.PickLength(KeepAwakeClock.Choices[i]), minWidth: 44, height: 28, fontSize: 13)
        {
            ToolTip = "How long to keep awake",
            VerticalAlignment = VerticalAlignment.Center,
        };
        var lengthLabel = Sheet.Meta("Keep awake for", 13);
        lengthLabel.Margin = new Thickness(0, 0, 10, 0);
        _lengthRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { lengthLabel, _lengths },
            Visibility = Visibility.Collapsed,
        };

        Body.Children.Add(new StackPanel { Children = { row, _lengthRow } });

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

    /// <summary>A capsule in the armed red (restart, shut down): red words and a red edge on the glass.</summary>
    static Button Armed(string text, Action onClick)
    {
        var b = Sheet.Cap(text, text, onClick);
        if (b.Content is Border face)
        {
            face.BorderBrush = CardKit.Alarm;
            face.BorderThickness = new Thickness(1.5);
            if (face.Child is Panel p)
                foreach (var t in p.Children.OfType<TextBlock>()) t.Foreground = CardKit.Alarm;
        }
        return b;
    }

    void ShowOff()
    {
        bool armed = _offStep.Is("off");
        _powerCap.Visibility = armed ? Visibility.Collapsed : Visibility.Visible;
        _restart.Visibility = _shutDown.Visibility = armed ? Visibility.Visible : Visibility.Collapsed;
        _offLine = armed ? "Pick one" : "Asks first";
        Status = armed ? "restart or shut down?" : null;
    }

    /// <summary>Test hook: the first click on restart / shut down (it only offers the two).</summary>
    internal void ClickOffForTest() => _offStep.Arm("off");

    internal bool OffChoiceShown => _restart.Visibility == Visibility.Visible;

    /// <summary>Keep awake's lengths show once it has been tapped here (and stay while the card is open).</summary>
    void ShowLengths() => _lengthRow.Visibility = _lengthsAsked ? Visibility.Visible : Visibility.Collapsed;

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
        ShowLengths();

        // Until the first read the radios are unknown, not missing.
        bool read = _module.SwitchesRead;
        _wifi.Line = !read ? "Checking…" : _module.WifiOn switch { true => "On", false => "Off", null => "Not found" };
        _wifi.On = _module.WifiOn == true;
        _bluetooth.Line = !read ? "Checking…" : _module.BluetoothOn switch { true => "On", false => "Off", null => "Not found" };
        _bluetooth.On = _module.BluetoothOn == true;
        _power.Line = _module.PowerModeNow is { } mode ? PowerMode.Name(mode) : read ? "Open settings" : "Checking…";
        // Storage lives on the Health card now; the test pipe still reads it here.
        _storage = _module.StorageLine();

        _quiet.On = _module.QuietOn;
        _quiet.Line = _module.QuietOn ? KeepAwakeText.TileLine(true, _module.QuietMinutesLeft) : "Tap for 1 h";
        _night.On = _module.NightLightOn;
        _night.Line = _module.NightLightOn ? "On" : "Off";

        bool? muted = _module.MicMuted;
        _mic.Line = KeepAwakeText.MicLine(muted);
        // "Mute mic" is on while the microphone is muted.
        _mic.On = muted == true;
    }

    /// <summary>What the tiles say right now, for the test pipe.</summary>
    public string Describe() =>
        $"awake=\"{_awake.Line}\"{(_awake.On ? " (on)" : "")} length={_lengths.Index} mic=\"{_mic.Line}\" " +
        $"wifi=\"{_wifi.Line}\" bluetooth=\"{_bluetooth.Line}\" power=\"{_power.Line}\" quiet=\"{_quiet.Line}\" night=\"{_night.Line}\" " +
        $"storage=\"{_storage}\" off=\"{_offLine}\"";

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

    /// <summary>
    /// One Laptop control: a 54 px circle with the icon (the accent with a dark icon when on, glass when off), the
    /// bold name under it and a quiet state line.
    /// </summary>
    sealed class RoundToggle : Button
    {
        const double Size = 54;
        static readonly Brush GlassFill = Ui.Tokens.Brush("#14FFFFFF"), GlassHover = Ui.Tokens.Brush("#21FFFFFF"), GlassEdge = Ui.Tokens.Brush("#1AFFFFFF");

        readonly Border _circle;
        readonly System.Windows.Shapes.Path _icon;
        readonly TextBlock _line;
        readonly string _name;
        bool _on;

        public RoundToggle(string icon, string name, string line, Action click)
        {
            _name = name;
            var glyph = CardKit.Icon(icon, CardKit.Ink, 22, 2);
            _icon = (System.Windows.Shapes.Path)((Canvas)((Viewbox)glyph).Child).Children[0];
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            _circle = new Border
            {
                Width = Size,
                Height = Size,
                CornerRadius = new CornerRadius(Size / 2),
                BorderThickness = new Thickness(1),
                Child = glyph,
                HorizontalAlignment = HorizontalAlignment.Center,
                SnapsToDevicePixels = true,
            };
            var title = Sheet.Word(name, 12);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.Margin = new Thickness(0, 7, 0, 0);
            _line = Sheet.Meta(line, 11);
            _line.HorizontalAlignment = HorizontalAlignment.Center;
            _line.Margin = new Thickness(0, 1, 0, 0);
            Content = new StackPanel { Children = { _circle, title, _line }, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center };
            Template = Bare;
            Cursor = Cursors.Hand;
            Focusable = true;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            Padding = new Thickness(2, 0, 2, 0);
            Ui.FocusKit.Button(this);
            Ui.Press.Attach(this);
            MouseEnter += (_, _) => Paint();
            MouseLeave += (_, _) => Paint();
            Click += (_, e) =>
            {
                click();
                e.Handled = true;
            };
            Paint();
            Say();
        }

        static readonly ControlTemplate Bare = MakeBare();

        static ControlTemplate MakeBare()
        {
            var t = new ControlTemplate(typeof(ButtonBase)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
            t.Seal();
            return t;
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
                Paint();
                Say();
            }
        }

        void Paint()
        {
            bool hover = IsMouseOver;
            // The accent brushes are live (a new accent repaints a circle already on screen).
            _circle.Background = _on ? (hover ? CardKit.AccentHover : CardKit.Accent) : hover ? GlassHover : GlassFill;
            _circle.BorderBrush = _on ? Brushes.Transparent : GlassEdge;
            _icon.Stroke = _on ? CardKit.OnAccent : CardKit.Ink;
        }

        void Say()
        {
            AutomationProperties.SetName(this, $"{_name}. {_line.Text}");
            AutomationProperties.SetItemStatus(this, _on ? "On" : "Off");
            ToolTip ??= _name;
        }
    }
}
