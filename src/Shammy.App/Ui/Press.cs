using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Shammy.Ui;

/// <summary>
/// The one press feedback for every kit control: it eases down to <see cref="Scale"/> in 90 ms when pressed and
/// back in 160 ms when let go. With Calm (or Windows' animations off) the scale snaps instead of easing.
/// </summary>
internal static class Press
{
    public const double Scale = 0.96;

    /// <summary>Gives a styled control the press from XAML (WallControls.xaml's buttons and chips).</summary>
    public static readonly DependencyProperty FeedbackProperty = DependencyProperty.RegisterAttached("Feedback", typeof(bool), typeof(Press),
        new PropertyMetadata(false, (d, e) =>
        {
            if (e.NewValue is true && e.OldValue is not true && d is UIElement element) Attach(element);
        }));

    public static bool GetFeedback(DependencyObject d) => (bool)d.GetValue(FeedbackProperty);

    public static void SetFeedback(DependencyObject d, bool value) => d.SetValue(FeedbackProperty, value);

    static readonly IEasingFunction Ease = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseOut });

    /// <summary>
    /// Gives <paramref name="element"/> the press: down on a left press (or Space on a focused button), up on
    /// release, on leaving and when the mouse capture goes. Returns the scale it drives.
    /// </summary>
    public static ScaleTransform Attach(UIElement element, double scale = Scale)
    {
        var t = Transform(element);
        element.PreviewMouseLeftButtonDown += (_, _) => To(t, true, scale);
        element.PreviewMouseLeftButtonUp += (_, _) => To(t, false, scale);
        element.MouseLeave += (_, _) => To(t, false, scale);
        element.LostMouseCapture += (_, _) =>
        {
            if (!element.IsMouseCaptured) To(t, false, scale);
        };
        if (element is ButtonBase)
        {
            element.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Space) To(t, true, scale);
            };
            element.KeyUp += (_, _) => To(t, false, scale);
            element.LostKeyboardFocus += (_, _) => To(t, false, scale);
        }
        return t;
    }

    /// <summary>The element's own scale transform (made, centred, if it has none).</summary>
    public static ScaleTransform Transform(UIElement element)
    {
        if (element.RenderTransform is ScaleTransform { IsFrozen: false } own) return own;
        var t = new ScaleTransform(1, 1);
        if (element.RenderTransform is { } existing && existing != System.Windows.Media.Transform.Identity)
            element.RenderTransform = new TransformGroup { Children = { existing, t } };
        else element.RenderTransform = t;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        return t;
    }

    /// <summary>Moves a press scale down (<paramref name="down"/>) or back up.</summary>
    public static void To(ScaleTransform t, bool down, double scale = Scale)
    {
        double s = down ? scale : 1;
        if (!Infrastructure.MotionPrefs.Lively || scale >= 1)
        {
            t.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            t.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            t.ScaleX = t.ScaleY = s;
            return;
        }
        var a = new DoubleAnimation(s, TimeSpan.FromMilliseconds(down ? 90 : 160)) { EasingFunction = Ease };
        a.Freeze();
        t.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        t.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    static IEasingFunction Freeze(QuadraticEase e)
    {
        e.Freeze();
        return e;
    }
}
