using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shammy.Core.Prompter;
using Shammy.Motion;
using Shammy.Views.Screens;

namespace Shammy.Modules.Prompter;

/// <summary>
/// The teleprompter, dropped from the notch (520×150) so it sits right under the camera: the script in big centred
/// lines moving up at a steady reading pace, faded at the top and bottom so the line being read stands out, and a
/// small bar with the controls. It never takes the keyboard (you are in your camera or meeting app), so it works by
/// mouse: click the script to pause, scroll to go back or ahead.
/// The scroll is the one place in the notch that animates all the time, because someone is reading it; it runs at
/// about 30 frames a second and only while the card is on screen and moving.
/// </summary>
internal sealed class PrompterCard : Grid
{
    public const double CardWidth = 520, CardHeight = 150;
    const double ViewHeight = 104, TextWidth = 476, ReadAt = 34, FrameTime = 1.0 / 30;

    readonly TextBlock _text;
    readonly TranslateTransform _move = new();
    readonly TextBlock _hint;
    readonly Segmented _paces;
    readonly int _words;
    IDisposable? _frames;
    double _offset, _pending;

    public PrompterCard(string script, PrompterPace pace, Action<PrompterPace> paceChosen, Action close)
    {
        Width = CardWidth;
        Height = CardHeight;
        Background = Brushes.Transparent;
        Pace = pace;
        _words = PrompterText.Words(script);
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(ViewHeight + 12) });
        RowDefinitions.Add(new RowDefinition());

        _text = ScreenKit.Text(script, 19, Weight.Bold);
        _text.Width = TextWidth;
        _text.TextWrapping = TextWrapping.Wrap;
        _text.TextTrimming = TextTrimming.None;
        _text.TextAlignment = TextAlignment.Center;
        _text.LineHeight = 26;
        _text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _text.RenderTransform = _move;

        var canvas = new Canvas { Width = TextWidth, Height = ViewHeight, ClipToBounds = true };
        canvas.Children.Add(_text);
        var view = new Border
        {
            Width = TextWidth,
            Height = ViewHeight,
            Margin = new Thickness(0, 12, 0, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = canvas,
            // Fades the lines above and below the one being read.
            OpacityMask = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0x28, 0, 0, 0), 0), new(Colors.Black, 0.26), new(Colors.Black, 0.6), new(Color.FromArgb(0x30, 0, 0, 0), 1),
            }, 90),
            ToolTip = "Click to pause or carry on",
        };
        view.MouseLeftButtonUp += (_, e) =>
        {
            Toggle();
            e.Handled = true;
        };
        MouseWheel += (_, e) =>
        {
            Nudge(e.Delta > 0 ? -52 : 52);
            e.Handled = true;
        };
        Children.Add(view);

        var bar = new Grid { Margin = new Thickness(22, 0, 14, 10), VerticalAlignment = VerticalAlignment.Bottom };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _hint = ScreenKit.Text("", 12, Weight.SemiBold, ScreenKit.Fg2);
        _hint.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(_hint);
        var paces = new[] { PrompterPace.Slow, PrompterPace.Normal, PrompterPace.Fast };
        _paces = new Segmented(new[] { "Slow", "Normal", "Fast" }, Array.IndexOf(paces, pace), i =>
        {
            Pace = paces[i];
            paceChosen(Pace);
            ShowHint();
        }, minWidth: 50, height: 24, fontSize: 12);
        SetColumn(_paces, 1);
        bar.Children.Add(_paces);
        var x = new Pressable(ScreenKit.Tile, ScreenKit.TileHover)
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(8, 0, 0, 0),
            Child = ScreenKit.Icon("M 6,6 L 18,18 M 18,6 L 6,18", ScreenKit.Fg, 10, 2.4),
            ToolTip = "Close the teleprompter",
        };
        System.Windows.Automation.AutomationProperties.SetName(x, "Close the teleprompter");
        x.Click += close;
        SetColumn(x, 2);
        bar.Children.Add(x);
        SetRow(bar, 1);
        Children.Add(bar);

        System.Windows.Automation.AutomationProperties.SetName(this, "Teleprompter");
        Loaded += (_, _) =>
        {
            _text.Measure(new Size(TextWidth, double.PositiveInfinity));
            Apply();
            ShowHint();
            Run();
        };
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Run();
            else Stop();
        };
    }

    public PrompterPace Pace { get; private set; }
    public bool Paused { get; private set; }

    double TextHeight => Math.Max(_text.DesiredSize.Height, _text.ActualHeight);

    /// <summary>How far the script can move: until its last line reaches the reading position.</summary>
    double Distance => Math.Max(0, TextHeight - 26);

    public bool Done => Distance > 0 && _offset >= Distance - 0.5;

    public double Percent => Distance <= 0 ? 0 : Math.Clamp(_offset / Distance * 100, 0, 100);

    public string Describe() =>
        $"paused={Paused} pace={Pace} offset={_offset:0} distance={Distance:0} percent={Percent:0} done={Done} words={_words} moving={_frames is not null} hint='{_hint.Text}'";

    public void Toggle()
    {
        if (Done)
        {
            // At the end, a click starts over.
            _offset = 0;
            Paused = false;
        }
        else Paused = !Paused;
        Apply();
        ShowHint();
        Run();
    }

    public void Nudge(double pixels)
    {
        _offset = Math.Clamp(_offset + pixels, 0, Distance);
        Apply();
        ShowHint();
        Run();
    }

    public void Seek(double percent)
    {
        _offset = Distance * Math.Clamp(percent, 0, 100) / 100;
        Apply();
        ShowHint();
        Run();
    }

    public void SetPace(PrompterPace pace)
    {
        Pace = pace;
        _paces.Index = (int)pace;
        ShowHint();
    }

    void Apply() => _move.Y = ReadAt - _offset;

    void ShowHint() =>
        _hint.Text = Done ? "Done · click the script to start over"
            : Paused ? "Paused · click to carry on"
            : $"Click to pause · scroll to nudge · {PrompterText.Length(_words, Pace)}";

    /// <summary>Frames only while there is something to move.</summary>
    void Run()
    {
        if (!IsVisible || Paused || Done || Distance <= 0)
        {
            Stop();
            return;
        }
        _pending = 0;
        _frames ??= Animator.Subscribe(OnFrame);
    }

    void Stop()
    {
        _frames?.Dispose();
        _frames = null;
    }

    void OnFrame(double dt)
    {
        _pending += Math.Min(dt, 0.25);
        if (_pending < FrameTime) return;
        _offset = Math.Min(Distance, _offset + PrompterText.PixelsPerSecond(Distance, _words, Pace) * _pending);
        _pending = 0;
        Apply();
        if (!Done) return;
        ShowHint();
        Stop();
    }
}
