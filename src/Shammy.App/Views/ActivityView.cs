using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Modules;

namespace Shammy.Views;

/// <summary>
/// The compact notch while something is live (the Activities board): the pet stays in the middle wearing the
/// activity's costume, a short label on the left and the value on the right ("STANDUP · 12:41"). With the pet
/// turned off it's the same pill with an empty middle, like the real cut-out. The action circle beside the notch
/// is drawn by the window (see Shell.ActivityChrome).
/// </summary>
internal sealed class ActivityView : Grid
{
    public const double ViewWidth = 300, ViewHeight = 38, Radius = 19;

    /// <summary>With a line under the row (a lyric).</summary>
    public const double TallHeight = 62, TallRadius = 22;
    const double Padding = 14;

    readonly ShammyServices _services;
    readonly StackPanel _left = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    readonly StackPanel _right = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    readonly TrackedText _label = new() { FontWeight = FontWeights.SemiBold };
    readonly TextBlock _value = new()
    {
        FontSize = 14,
        FontWeight = FontWeights.Bold,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
    readonly ContentControl _badge = new() { VerticalAlignment = VerticalAlignment.Center, Focusable = false };
    readonly ContentControl _marker = new() { VerticalAlignment = VerticalAlignment.Center, Focusable = false };
    readonly TextBlock _line = new()
    {
        FontSize = 12.5,
        TextTrimming = TextTrimming.CharacterEllipsis,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(18, 0, 18, 0),
        Visibility = Visibility.Collapsed,
    };
    PetCostume? _markerFor;
    bool _markerPlaying;
    VisualizerBars? _bars;
    Color _barsColor;
    Action? _pulse;
    readonly System.Windows.Threading.DispatcherTimer _burstEnd;
    Rectangle? _batteryFill;

    public ActivityView(ShammyServices services)
    {
        _services = services;
        Width = ViewWidth;
        Height = ViewHeight;
        Background = Brushes.Transparent;
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ActivityPet.ColumnWidth) });
        ColumnDefinitions.Add(new ColumnDefinition());
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(ViewHeight) });
        RowDefinitions.Add(new RowDefinition());
        VerticalAlignment = VerticalAlignment.Top;

        _line.SetResourceReference(TextBlock.FontFamilyProperty, "WallFontSemiBold");
        _line.FontWeight = FontWeights.SemiBold;
        _line.Foreground = (Brush)Application.Current.Resources["WallText"];
        SetRow(_line, 1);
        SetColumnSpan(_line, 3);
        Children.Add(_line);

        _value.FontFamily = (FontFamily)Application.Current.Resources["WallFont"];
        _value.Foreground = (Brush)Application.Current.Resources["WallText"];

        // WallFont + SemiBold falls back to Regular; the SemiBold family is the real weight.
        _label.SetResourceReference(TrackedText.FontFamilyProperty, "WallFontSemiBold");
        _left.Margin = new Thickness(Padding, 0, 4, 0);
        _left.Children.Add(_badge);
        _left.Children.Add(_label);
        _right.Margin = new Thickness(4, 0, Padding, 0);
        _right.Children.Add(_marker);
        _right.Children.Add(_value);
        Children.Add(_left);
        // Music's eq bars move with the pet's sway bursts and hold still between them (see ActivityPet.Burst).
        _burstEnd = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background);
        _burstEnd.Tick += (_, _) =>
        {
            _burstEnd.Stop();
            if (_bars is not null) _bars.Frozen = true;
        };
        Pet.Burst += seconds =>
        {
            // The recording dot pulses along with the ninja's burst.
            _pulse?.Invoke();
            if (_bars is null) return;
            _bars.Frozen = false;
            _burstEnd.Stop();
            _burstEnd.Interval = TimeSpan.FromSeconds(Math.Max(1.2, seconds));
            _burstEnd.Start();
        };
        SetColumn(Pet, 1);
        Children.Add(Pet);
        SetColumn(_right, 2);
        Children.Add(_right);
    }

    public ActivityPet Pet { get; } = new();

    public PetLive? Shown { get; private set; }

    public bool PetOn { get; private set; } = true;

    /// <summary>The line under the row, or empty.</summary>
    public string LineText { get; private set; } = "";

    /// <summary>How tall the notch should be for what's shown.</summary>
    public double CurrentHeight => LineText.Length > 0 ? TallHeight : ViewHeight;

    public double CurrentRadius => LineText.Length > 0 ? TallRadius : Radius;

    public string LabelText => _label.Text;
    public string ValueText => _value.Text;

    /// <summary>Show (or update in place) the activity the pet is wearing. True when it is a different one than before.</summary>
    public bool Show(PetLive live, bool petOn)
    {
        // A different activity (or the next notification in the same slot) replays the costume's entrance.
        bool replay = Shown?.Activity.Id != live.Activity.Id
            || (live.State.Costume == PetCostume.Notify && !ReferenceEquals(Shown.Activity.Payload, live.Activity.Payload));
        Shown = live;
        PetOn = petOn;
        var s = live.State;

        // Names (an earbuds model) stay as typed, a touch larger; everything else is the tracked uppercase label.
        _label.Uppercase = !s.PlainLabel;
        _label.Tracking = s.PlainLabel ? 0 : 0.12;
        _label.FontSize = s.PlainLabel ? 11.5 : 10.5;
        _label.Text = s.Label;
        _value.Text = s.Value;
        _value.Visibility = s.Value.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _label.MaxWidth = ViewWidth / 2 - ActivityPet.ColumnWidth / 2 - Padding - 4 - (live.Badge is null && s.Costume != PetCostume.Notify ? 0 : 20);

        if (replay || (_badge.Content is null) != (s.Costume != PetCostume.Notify)) UpdateBadge(live);
        UpdateMarker(live);
        ShowLine(s.Line.Trim());

        Pet.Visibility = petOn || PetActivities.ShowsWithoutPet(live.Face) ? Visibility.Visible : Visibility.Collapsed;
        if (replay) Pet.Reset();
        Pet.Show(live.Face, s.Paused, live.Art, s.Progress, live.Level, live.Cover);
        return replay;
    }

    /// <summary>The line under the row; a new one fades in (the notch itself grows or shrinks around it).</summary>
    void ShowLine(string line)
    {
        if (line == LineText) return;
        LineText = line;
        Height = CurrentHeight;
        _line.Visibility = line.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (line.Length == 0) return;
        _line.Text = line;
        if (!MotionPrefs.Lively) return;
        var fade = new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(260)) { FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(fade, 20);
        _line.BeginAnimation(OpacityProperty, fade);
    }

    void UpdateBadge(PetLive live)
    {
        if (live.State.Costume != PetCostume.Notify)
        {
            _badge.Content = null;
            return;
        }
        FrameworkElement badge = live.Badge is { } icon
            ? new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Background = new ImageBrush(icon) { Stretch = Stretch.UniformToFill } }
            : new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Background = (Brush)Application.Current.Resources["WallHairline"],
                Child = new TextBlock
                {
                    Text = live.State.Label.Length > 0 ? live.State.Label[..1].ToUpperInvariant() : "•",
                    FontSize = 9, FontWeight = FontWeights.Bold, Foreground = (Brush)Application.Current.Resources["WallText"],
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
        badge.Margin = new Thickness(0, 0, 6, 0);
        _badge.Content = badge;
    }

    /// <summary>The small mark beside the value: eq bars, the recording dot, a battery, earbuds.</summary>
    void UpdateMarker(PetLive live)
    {
        var s = live.State;
        bool playing = s.Costume == PetCostume.Music && s.Playing;
        if (_markerFor == s.Costume && _markerPlaying == playing)
        {
            // Called every second: only a new album colour repaints the bars.
            if (_bars is not null && live.Art is { } art && Lighter(art) != _barsColor) _bars.Color = _barsColor = Lighter(art);
            if (_batteryFill is not null) _batteryFill.Width = 12 * Math.Clamp(s.Progress ?? 0, 0, 1);
            return;
        }
        _markerFor = s.Costume;
        _markerPlaying = playing;
        _bars = null;
        _batteryFill = null;
        _pulse = null;
        FrameworkElement? marker = s.Costume switch
        {
            PetCostume.Music when playing => _bars = new VisualizerBars
            {
                BarCount = 3,
                Height = 12,
                Source = _services.Visualizer.Level,
                Feed = _services.Visualizer,
                Color = _barsColor = Lighter(live.Art ?? Colors.White),
                // A few steps a second: the notch's layered window makes every repaint costly.
                MaxFps = 10,
                Frozen = true,
                Active = true,
            },
            PetCostume.Recording => RecDot(),
            PetCostume.Charging => Battery(s.Progress ?? 0),
            PetCostume.Earbuds => Buds(),
            _ => null,
        };
        if (marker is not null) marker.Margin = new Thickness(0, 0, s.Value.Length > 0 ? 6 : 0, 0);
        _marker.Content = marker;
    }

    /// <summary>Album colours can be dark; the bars need to read on black.</summary>
    static Color Lighter(Color c)
    {
        double luma = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        if (luma >= 0.45) return c;
        double t = Math.Min(0.75, 0.45 - luma + 0.25);
        return Color.FromRgb((byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));
    }

    FrameworkElement RecDot()
    {
        var dot = new Ellipse
        {
            Width = 7, Height = 7, Fill = (Brush)Application.Current.Resources["Red"], VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var scale = new ScaleTransform();
        dot.RenderTransform = scale;
        if (MotionPrefs.Lively)
        {
            var pulse = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.0), FillBehavior = FillBehavior.Stop };
            Timeline.SetDesiredFrameRate(pulse, 12);
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0), ease));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.45, KeyTime.FromPercent(0.5), ease));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), ease));
            var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.0), FillBehavior = FillBehavior.Stop };
            Timeline.SetDesiredFrameRate(fade, 12);
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0), ease));
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(0.55, KeyTime.FromPercent(0.5), ease));
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), ease));
            // One pulse per pet burst while on screen (a still dot in between costs nothing).
            _pulse = () =>
            {
                if (!dot.IsVisible) return;
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
                dot.BeginAnimation(OpacityProperty, fade);
            };
        }
        return dot;
    }

    FrameworkElement Battery(double level)
    {
        var c = new Canvas { Width = 20, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        var stroke = (Brush)Application.Current.Resources["WallText2"];
        c.Children.Add(Place(new Rectangle { Width = 16, Height = 10, RadiusX = 2.5, RadiusY = 2.5, Stroke = stroke, StrokeThickness = 1.4 }, 1, 1));
        c.Children.Add(Place(new Rectangle { Width = 1.4, Height = 3, RadiusX = 0.7, RadiusY = 0.7, Fill = stroke }, 18, 4.5));
        _batteryFill = new Rectangle { Width = 12 * Math.Clamp(level, 0, 1), Height = 6, RadiusX = 1, RadiusY = 1, Fill = (Brush)Application.Current.Resources["BatteryAccent"] };
        c.Children.Add(Place(_batteryFill, 3, 3));
        return c;
    }

    static FrameworkElement Buds() => new Path
    {
        Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center,
        Data = Geometry.Parse("M 3.5,2.5 A 2.2,2.2 0 0 1 5.7,4.7 V 5.9 A 2.2,2.2 0 0 1 3.5,8.1 V 12.5 M 10.5,2.5 A 2.2,2.2 0 0 0 8.3,4.7 V 5.9 A 2.2,2.2 0 0 0 10.5,8.1 V 12.5"),
        Stroke = (Brush)Application.Current.Resources["WallText2"], StrokeThickness = 1.4,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
    };

    static T Place<T>(T e, double x, double y) where T : UIElement
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        return e;
    }
}
