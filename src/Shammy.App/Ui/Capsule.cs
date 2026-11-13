using System.Windows;
using System.Windows.Controls;

namespace Shammy.Ui;

/// <summary>
/// A true capsule: <c>ui:Capsule.Round="True"</c> keeps a Border's corners at exactly half its height (a circle when it's
/// square). WPF doesn't clamp a huge radius like 999 the way CSS does; it shrinks it proportionally and the pill comes out
/// as a squashed oval. Every pill, chip and round button in the notch uses this, so they all have the same shape.
/// </summary>
public static class Capsule
{
    public static readonly DependencyProperty RoundProperty = DependencyProperty.RegisterAttached(
        "Round", typeof(bool), typeof(Capsule), new PropertyMetadata(false, OnRoundChanged));

    public static bool GetRound(DependencyObject d) => (bool)d.GetValue(RoundProperty);

    public static void SetRound(DependencyObject d, bool value) => d.SetValue(RoundProperty, value);

    static void OnRoundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;
        border.SizeChanged -= OnSizeChanged;
        if (e.NewValue is not true) return;
        border.SizeChanged += OnSizeChanged;
        Apply(border);
    }

    static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((Border)sender);

    static void Apply(Border border)
    {
        double side = Math.Min(border.ActualWidth, border.ActualHeight);
        if (side <= 0) return;
        border.CornerRadius = new CornerRadius(side / 2);
    }

    /// <summary>For borders made in code: a capsule from now on.</summary>
    public static T Make<T>(T border) where T : Border
    {
        SetRound(border, true);
        return border;
    }
}
