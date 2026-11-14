using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Calendar;
using Shammy.Modules.Calendar;
using Shammy.Views.Cards;

namespace Shammy.Modules.Mirror;

/// <summary>
/// The mirror's detail on the Focus Wall: the live camera picture IS the art panel (300×248, the notch's Back on
/// it, a "live · not recording" mark at its foot); on the right, the state, a line about the next meeting or the
/// privacy promise, and Flip and brightness. Owns the camera: a 150 ms check while this view is visible starts it
/// when the view is really on screen and stops it as soon as the notch fades it, closes it or the PC locks.
/// </summary>
internal sealed class MirrorView : CardSheet
{
    readonly MirrorModule _module;
    readonly MirrorCamera _camera;
    readonly DispatcherTimer _watch;
    readonly ImageBrush _brush = new() { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
    readonly ScaleTransform _flip = new(1, 1, 0.5, 0.5);
    readonly StackPanel _message;
    readonly TextBlock _messageText;
    readonly Button _settingsButton;
    readonly Border _liveChip;
    readonly TextBlock _flipText, _levelText;
    readonly ContentControl _flipSlot = new() { Focusable = false };
    readonly Grid _levelBar = new() { Width = 110, Height = 16, VerticalAlignment = VerticalAlignment.Center };
    bool _starting, _wantRunning;

    /// <summary>The picture: a rounded glass-edged tile on the left of the card (the camera feed fills it).</summary>
    readonly Grid _art = new() { Width = 300, Height = 196, ClipToBounds = true };
    readonly TextBlock _line = Sheet.Meta("", 13);

    public MirrorView(MirrorModule module, Dispatcher ui) : base("Mirror", 640, "starting")
    {
        _module = module;
        _camera = new MirrorCamera(ui);
        _camera.BitmapChanged += bmp => _brush.ImageSource = bmp;
        _brush.RelativeTransform = _flip;

        _line.Text = MeetingHint() ?? "Camera stays on your PC; nothing is recorded";

        // The art panel: the picture, its live mark, a message when there's no picture.
        _art.Clip = new RectangleGeometry(new Rect(0, 0, 300, 196), 22, 22);
        _art.Children.Add(new Border { Background = Fields.Mirror() });
        _art.Children.Add(new Border { Background = _brush });
        _liveChip = new Border
        {
            Background = CardKit.Hex("#A60B0B0D"),
            Padding = new Thickness(9, 5, 9, 5),
            Visibility = Visibility.Collapsed,
            Child = CardKit.Row(6, CardKit.Dot(6, CardKit.Alarm), CardKit.Label("Live · not recording", null, 10)),
        };
        Ui.Capsule.SetRound(_liveChip, true);
        _art.Children.Add(CardKit.At(_liveChip, HorizontalAlignment.Left, VerticalAlignment.Bottom, 14, 0, 0, 14));
        _messageText = CardKit.Text("", 13, FontWeights.SemiBold);
        _messageText.TextWrapping = TextWrapping.Wrap;
        _messageText.TextTrimming = TextTrimming.None;
        _messageText.TextAlignment = TextAlignment.Center;
        _messageText.MaxWidth = 240;
        _settingsButton = CardKit.Pill("Open camera settings", "Open camera settings", _module.OpenSettings, 30);
        _settingsButton.HorizontalAlignment = HorizontalAlignment.Center;
        _settingsButton.Margin = new Thickness(0, 10, 0, 0);
        var cam = CardKit.Icon(CardKit.Paths.Camera, CardKit.Ink2, 26, 1.8);
        cam.Margin = new Thickness(0, 0, 0, 10);
        _message = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _message.Children.Add(cam);
        _message.Children.Add(_messageText);
        _message.Children.Add(_settingsButton);
        _art.Children.Add(_message);

        // Controls: Flip, then brightness.
        _flipText = CardKit.Text("", 12, FontWeights.Medium, secondary: true);
        var flipLabel = CardKit.Text("Flip", 13.5, FontWeights.SemiBold);
        var flipRow = CardKit.Row(12, _flipSlot, flipLabel, _flipText);

        _levelText = CardKit.Text("", 13, FontWeights.Bold);
        _levelText.Width = 44;
        var minus = CardKit.Quiet("Decrease brightness", CardKit.Icon(CardKit.Paths.Minus, CardKit.Ink, 12, 2.4), () => Step(-10), 28);
        var plus = CardKit.Quiet("Increase brightness", CardKit.Icon(CardKit.Paths.Plus, CardKit.Ink, 12, 2.4), () => Step(10), 28);
        var brightness = CardKit.Label("Brightness", CardKit.Ink2);
        brightness.Width = 84;
        _levelBar.Margin = new Thickness(0, 0, 0, 0);
        brightness.HorizontalAlignment = HorizontalAlignment.Left;
        var levelRow = CardKit.Column(8, brightness, CardKit.Row(10, minus, _levelBar, plus, _levelText));
        levelRow.Margin = new Thickness(0, 14, 0, 0);
        _line.TextWrapping = TextWrapping.Wrap;
        _line.TextTrimming = TextTrimming.None;
        _line.Margin = new Thickness(0, 0, 0, 16);
        flipRow.VerticalAlignment = VerticalAlignment.Center;
        var controls = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0) };
        controls.Children.Add(_line);
        controls.Children.Add(flipRow);
        controls.Children.Add(levelRow);
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.Children.Add(_art);
        Grid.SetColumn(controls, 1);
        layout.Children.Add(controls);
        Body.Children.Add(layout);

        ApplyFlip();
        ApplyLevel();

        _watch = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Normal, (_, _) => Evaluate(), ui) { IsEnabled = false };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _watch.Start();
            else _watch.Stop();
            Evaluate();
        };
        Unloaded += (_, _) => Shutdown();
    }

    public long Frames => _camera.Frames;
    public string Diagnostics => _camera.Diagnostics;
    public string FrameSize => _camera.Size;
    public MirrorProblem Problem { get; private set; }
    public string CameraState => _starting ? "starting" : _camera.Running ? "running" : "stopped";

    /// <summary>
    /// On screen for real: visible, not faded out by the notch (its layers fade before they collapse), and the
    /// PC isn't locked. Starts or stops the camera to match.
    /// </summary>
    public void Evaluate()
    {
        bool shown = IsVisible && !_module.Locked && EffectiveOpacity() >= 0.5;
        if (shown == _wantRunning) return;
        _wantRunning = shown;
        if (shown) _ = StartAsync();
        else _ = StopAsync();
    }

    double EffectiveOpacity()
    {
        double o = 1;
        for (DependencyObject? d = this; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is UIElement u) o *= u.Opacity;
        return o;
    }

    async Task StartAsync()
    {
        if (_starting) return;
        _starting = true;
        Status = "starting";
        ShowMessage(null);
        var problem = await _camera.StartAsync();
        _starting = false;
        Problem = problem;
        if (!_wantRunning)
        {
            // Closed while the camera was still warming up: release it straight away.
            await _camera.StopAsync();
            UpdateStatus();
            return;
        }
        // Reshown while the earlier start was being cancelled: that start gave up, so go again.
        if (problem == MirrorProblem.None && !_camera.Running)
        {
            await StartAsync();
            return;
        }
        if (problem != MirrorProblem.None)
        {
            _module.Info($"Mirror: camera unavailable ({problem})");
            ShowMessage(problem switch
            {
                MirrorProblem.NoCamera => "No camera found",
                MirrorProblem.Denied => "Camera access is off for desktop apps. Turn it on in Settings › Privacy & security › Camera.",
                MirrorProblem.InUse => "The camera is in use by another app",
                _ => "Couldn't start the camera",
            }, problem == MirrorProblem.Denied);
        }
        UpdateStatus();
    }

    async Task StopAsync()
    {
        await _camera.StopAsync();
        _brush.ImageSource = null;
        UpdateStatus();
    }

    /// <summary>Releases the camera now (module shutdown, view thrown away).</summary>
    public void Shutdown()
    {
        _watch.Stop();
        _wantRunning = false;
        _ = StopAsync();
    }

    void UpdateStatus()
    {
        bool live = _camera.Running;
        Status = (live ? "camera on" : Problem != MirrorProblem.None ? "camera off" : _starting ? "starting" : "paused");
        _liveChip.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowMessage(string? text, bool settings = false)
    {
        _message.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        _messageText.Text = text ?? "";
        _settingsButton.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
    }

    void ToggleFlip()
    {
        _module.Store.Mirrored = !_module.Store.Mirrored;
        _module.Save();
        ApplyFlip();
    }

    void ApplyFlip()
    {
        bool mirrored = _module.Store.Mirrored;
        _flip.ScaleX = mirrored ? -1 : 1;
        _flipText.Text = mirrored ? "Mirrored" : "As others see you";
        // On = the white-filled control; off = the quiet one (no accent: Flip isn't the primary action).
        var b = mirrored
            ? CardKit.Pill("On", "Flip off (show what others see)", ToggleFlip, 30)
            : CardKit.Pill("Off", "Flip on (mirrored view)", ToggleFlip, 30);
        b.Width = 52;
        _flipSlot.Content = b;
    }

    void Step(int delta)
    {
        _module.Store.Level = Math.Clamp(_module.Store.Level + delta, 0, 100);
        _module.Save();
        ApplyLevel();
    }

    void ApplyLevel()
    {
        int level = _module.Store.Level;
        // The design's scale: 0 % → 0.6×, 50 % → 1.0×, 100 % → 1.4×.
        _camera.Brightness = 0.6 + level / 100.0 * 0.8;
        _levelText.Text = $"{level}%";
        _levelBar.Children.Clear();
        _levelBar.Children.Add(CardKit.Progress(level / 100.0, Ink.Gradient, 5, Sheet.Track));
    }

    /// <summary>"A quick look before your 10:30 Standup" when a meeting starts within the hour.</summary>
    static string? MeetingHint()
    {
        var now = DateTime.Now;
        if (CalendarAgenda.Next(CalendarModule.Agenda, now) is not { } next || next.Start - now > TimeSpan.FromMinutes(60)) return null;
        return $"A quick look before {next.Title} at {CalendarText.Clock(next.Start, CardKit.H24)} · nothing is recorded";
    }
}

/// <summary>The Mirror tile: warm dark glass with the camera mark; the camera itself only runs in the detail.</summary>
internal static class MirrorTile
{
    public static FrameworkElement Build(Action open)
    {
        var tile = new WallTile { Field = Fields.Mirror() };
        tile.Art.Children.Add(CardKit.At(CardKit.Icon(CardKit.Paths.Camera, CardKit.Ink, 30, 1.8), HorizontalAlignment.Left, VerticalAlignment.Top, 14, 14));
        tile.Rest.Children.Add(WallTile.BottomLeft(CardKit.Poster("Mirror", 26)));
        var words = WallTile.Words("Mirror", "A quick look before a call", out _, out _);
        tile.Lit.Children.Add(CardKit.At(words, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, WallTile.Pad, 0, WallTile.Pad, 10));
        tile.Lit.Children.Add(WallTile.OpenButton("Open the mirror", open));
        CardKit.Name(tile, "Mirror, camera preview. Open mirror");
        return tile;
    }
}
