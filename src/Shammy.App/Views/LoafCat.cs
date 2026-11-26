using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Shammy.Core.Notch;
using Path = System.Windows.Shapes.Path;

namespace Shammy.Views;

/// <summary>
/// The Loaf pet style: a whole cat lying on the resting notch's floor, its tail curled up behind it. It lies with
/// its head up, puts its head down to sleep and sits up when something needs you (<see cref="LoafLooks"/>).
/// Drawn in the pet's 32 × 32 box; lying, its tail hangs about 8 px below it, over the island's edge. The eyes take the pet's
/// blink transform, so they blink and slow-blink (the purr) with the rest of the pet.
/// </summary>
internal sealed class LoafCat : Canvas
{
    static readonly Brush Ink = Ui.Tokens.Frozen(Color.FromRgb(0x0B, 0x0C, 0x10));
    static readonly Brush Pink = Ui.Tokens.Frozen(Color.FromRgb(0xFF, 0x9D, 0xB0));

    static readonly Geometry LyingBody = Parsed("M8,27 C8,21.5 13.5,19.5 21,19.5 C27,19.5 31,21.5 31,27 Z");
    static readonly Geometry HeadUp = Parsed("M1.5,27 C1,22.5 2.5,19 5.5,18 L5,12.8 L8.6,15.6 Q11,15.2 13.4,15.6 L17,12.8 L16.5,18 C19.5,19 21,22.5 20.5,27 Z");
    static readonly Geometry HeadDown = Parsed("M1.5,27 C1,24 2.5,21.5 5.5,20.8 L5.2,16.5 L8.6,18.8 Q11,18.5 13.4,18.8 L16.8,16.5 L16.5,20.8 C19.5,21.5 21,24 20.5,27 Z");
    static readonly Geometry Sitting = Parsed("M10,27.5 C8.5,22 10,17 13.5,15.5 L13,9.5 L16.8,12.3 Q19.5,11.9 22.2,12.3 L26,9.5 L25.5,15.5 C29,17 30.5,22 29,27.5 Z");
    // Lying, the tail hangs over the island's bottom edge (the island lets it through, see TailRoom).
    static readonly Geometry TailHanging = Parsed("M29.5,26 C33,27.5 34,34.5 31.5,37.8 C29.6,40.2 26.4,39.4 27.4,36.8");
    static readonly Geometry TailUp = Parsed("M29,26 C33.5,24.5 34,17.5 31.5,13.5");
    static readonly Geometry ClosedEye = Parsed("M-1.6,-0.4 Q0,1.1 1.6,-0.4");
    static readonly Geometry HappyEye = Parsed("M-1.6,0.6 Q0,-1.1 1.6,0.6");
    static readonly Geometry NoseShape = Parsed("M-0.9,0 H0.9 L0,1.1 Z");

    readonly Path _body = new(), _head = new(), _tail = new() { StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    readonly Path _nose = new() { Data = NoseShape, Fill = Pink };
    readonly Canvas _eyes = new() { Width = 8, Height = 4, RenderTransformOrigin = new Point(0.5, 0.5) };
    readonly RotateTransform _tailTurn = new();

    static Geometry Parsed(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    public LoafCat(Transform blink)
    {
        IsHitTestVisible = false;
        Width = Height = 32;
        _tail.RenderTransform = _tailTurn;
        _eyes.RenderTransform = blink;
        foreach (var part in new UIElement[] { _tail, _body, _head, _eyes, _nose }) Children.Add(part);
    }

    /// <summary>Draws the pose for a mood in the pet's colour; <paramref name="moving"/> is false while off screen (no swish).</summary>
    public void Show(LoafPose pose, Brush fill, bool moving)
    {
        bool sitting = pose.Posture == LoafPosture.Sitting, asleep = pose.Posture == LoafPosture.Asleep;
        _body.Fill = _head.Fill = fill;
        _tail.Stroke = fill;
        _body.Data = sitting ? Sitting : LyingBody;
        _head.Visibility = sitting ? Visibility.Collapsed : Visibility.Visible;
        _head.Data = asleep ? HeadDown : HeadUp;
        _tail.Data = sitting ? TailUp : TailHanging;
        (_tailTurn.CenterX, _tailTurn.CenterY) = sitting ? (29, 26) : (29.5, 26);

        // The eye row: its centre between the two eyes, 5.6 apart.
        var (x, y) = sitting ? (19.5, 18.6) : asleep ? (11, 23.8) : (11, 21.6);
        _eyes.Children.Clear();
        foreach (double dx in new[] { -2.8, 2.8 }) _eyes.Children.Add(Eye(pose.Eyes, 4 + dx));
        SetLeft(_eyes, x - 4);
        SetTop(_eyes, y - 2);
        _nose.Visibility = pose.Eyes == LoafEyes.Closed ? Visibility.Collapsed : Visibility.Visible;
        SetLeft(_nose, x);
        SetTop(_nose, y + 1.9);
        Swish(pose.Swish && moving && Infrastructure.MotionPrefs.Lively);
    }

    static UIElement Eye(LoafEyes eyes, double cx)
    {
        if (eyes == LoafEyes.Open)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 2.4, Height = 2.6, Fill = Ink };
            SetLeft(dot, cx - 1.2);
            SetTop(dot, 0.7);
            return dot;
        }
        var arc = new Path { Data = eyes == LoafEyes.Closed ? ClosedEye : HappyEye, Stroke = Ink, StrokeThickness = 1, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        SetLeft(arc, cx);
        SetTop(arc, 2);
        return arc;
    }

    /// <summary>Turns the tail by a few degrees (a flick is a few of these in a row); 0 puts it back.</summary>
    public void TailAngle(double degrees)
    {
        _tailTurn.BeginAnimation(RotateTransform.AngleProperty, null);
        _tailTurn.Angle = degrees;
    }

    /// <summary>The tail sways to the beat while music plays; off stops it at rest.</summary>
    public void Swish(bool on)
    {
        if (!on)
        {
            TailAngle(0);
            return;
        }
        var a = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromMilliseconds(900) };
        Timeline.SetDesiredFrameRate(a, 20);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(-12, KeyTime.FromTimeSpan(TimeSpan.Zero), ease));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)), ease));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(-12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)), ease));
        _tailTurn.BeginAnimation(RotateTransform.AngleProperty, a);
    }
}
