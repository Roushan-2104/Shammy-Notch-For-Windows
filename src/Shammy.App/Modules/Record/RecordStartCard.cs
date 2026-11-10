using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Record;
using Shammy.Views;
using Shammy.Views.Screens;

namespace Shammy.Modules.Record;

/// <summary>What to record: the whole screen the notch is on, or a window chosen in Windows' own picker.</summary>
internal enum RecordChoice { WholeScreen, Window }

/// <summary>
/// The card dropped from the notch to start a recording (380 wide): "Record the screen", the choice of what, the
/// sound line whose two halves switch PC sound and the microphone when clicked, and Start.
/// </summary>
internal sealed class RecordStartCard : Grid
{
    public const double CardWidth = 380, CardHeight = 134;

    readonly TextBlock _sound, _mic;

    public RecordStartCard(RecordChoice choice, bool sound, bool mic, Action<RecordChoice> chosen, Action<bool> soundChanged, Action<bool> micChanged,
        Action start, Action close)
    {
        Width = CardWidth;
        Height = CardHeight;
        Background = Brushes.Transparent;
        Sound = sound;
        Mic = mic;

        var rows = new StackPanel { Margin = new Thickness(16, 14, 16, 16) };
        Children.Add(rows);

        var head = new Grid { Height = 22 };
        var title = ScreenKit.Display(RecordText.Title, 17, Weight.ExtraBold, -0.015);
        title.HorizontalAlignment = HorizontalAlignment.Left;
        title.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(title);
        var shut = WallParts.Icon("", "Close", close, 24, 10);
        shut.HorizontalAlignment = HorizontalAlignment.Right;
        shut.VerticalAlignment = VerticalAlignment.Center;
        shut.Margin = new Thickness(0, 0, -4, 0);
        head.Children.Add(shut);
        rows.Children.Add(head);

        var what = new Segmented(new[] { RecordText.WholeScreen, RecordText.Window }, (int)choice, i => chosen((RecordChoice)i))
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0),
        };
        AutomationProperties.SetName(what, "What to record");
        rows.Children.Add(what);

        var bottom = new Grid { Height = 30, Margin = new Thickness(0, 10, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var soundHalf = Half(out _sound, () =>
        {
            Sound = !Sound;
            soundChanged(Sound);
            Paint();
        });
        var micHalf = Half(out _mic, () =>
        {
            Mic = !Mic;
            micChanged(Mic);
            Paint();
        });
        line.Children.Add(soundHalf);
        var dot = ScreenKit.Text(" · ", 12.5, Weight.Regular, ScreenKit.Fg2);
        dot.IsHitTestVisible = false;
        line.Children.Add(dot);
        line.Children.Add(micHalf);
        bottom.Children.Add(line);

        var go = ScreenKit.Primary("Start", start, 30, 12.5, null, 16);
        go.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(go, "Start recording");
        SetColumn(go, 1);
        bottom.Children.Add(go);
        rows.Children.Add(bottom);

        Paint();
    }

    public bool Sound { get; private set; }
    public bool Mic { get; private set; }

    /// <summary>One clickable half of the sound line: grey like the rest of the line, white under the pointer.</summary>
    static Pressable Half(out TextBlock text, Action click)
    {
        var words = ScreenKit.Text("", 12.5, Weight.Regular, ScreenKit.Fg2);
        text = words;
        var half = new Pressable(Brushes.Transparent, Brushes.Transparent)
        {
            Child = words,
            Padding = new Thickness(0, 4, 0, 4),
            PressScale = 0.97,
        };
        half.MouseEnter += (_, _) => words.Foreground = ScreenKit.Fg;
        half.MouseLeave += (_, _) => words.Foreground = ScreenKit.Fg2;
        half.Click += click;
        return half;
    }

    void Paint()
    {
        _sound.Text = RecordText.PcSound(Sound);
        _mic.Text = RecordText.Mic(Mic);
        Describe((FrameworkElement)_sound.Parent, _sound.Text, Sound ? "Click to leave the PC’s sound out" : "Click to record the PC’s sound");
        Describe((FrameworkElement)_mic.Parent, _mic.Text, Mic ? "Click to leave your microphone out" : "Click to record your microphone too");
    }

    static void Describe(FrameworkElement half, string state, string tip)
    {
        half.ToolTip = tip;
        AutomationProperties.SetName(half, $"{state}. {tip}");
    }

    /// <summary>For the test pipe.</summary>
    public string DescribeState() => RecordText.Sound(Sound, Mic);
}
