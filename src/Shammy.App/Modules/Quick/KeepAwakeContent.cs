using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Notch;
using Shammy.Core.Quick;

namespace Shammy.Modules.Quick;

/// <summary>
/// Keep-awake in the compact notch: the pet with wide eyes, "Awake" and the time left, and a Stop circle beside it.
/// It keeps going, so a call or a timer takes the pet and this waits as the dot.
/// </summary>
internal sealed class KeepAwakeContent(KeepAwakeClock clock, Action stop) : ICompactContent, IPetActivity
{
    static readonly CircleSpec StopCircle = new(CircleAction.Stop, CircleTone.Neutral, true, "Stop keeping awake");

    TextBlock? _value;

    public PetActivityState Describe(DateTime utcNow) => new()
    {
        Costume = PetCostume.Custom,
        Face = PetFace.Notify,
        Label = "Awake",
        Value = Left(utcNow),
        Ongoing = true,
        Circle = StopCircle,
    };

    public void RunCircle(CircleAction action)
    {
        if (action == CircleAction.Stop) stop();
    }

    /// <summary>The plain pill's time left, for anywhere the pet doesn't draw it.</summary>
    public void Refresh()
    {
        if (_value is null) return;
        var text = Left(DateTime.UtcNow);
        if (_value.Text != text) _value.Text = text;
    }

    // Never "0 min": in its last moments it still reads one minute.
    string Left(DateTime utcNow) => KeepAwakeText.Short(Math.Max(1, clock.MinutesLeft(utcNow)));

    public double Build(ContentControl left, ContentControl right)
    {
        left.Content = Words("Awake", "Fg2");
        _value = Words(Left(DateTime.UtcNow), "Fg");
        right.Content = _value;
        return 64;
    }

    static TextBlock Words(string text, string brush) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = (Brush)Application.Current.Resources[brush],
        VerticalAlignment = VerticalAlignment.Center,
    };

    public UIElement? BuildBubble() => null;
}
