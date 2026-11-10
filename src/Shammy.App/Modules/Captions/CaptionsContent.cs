using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Notch;

namespace Shammy.Modules.Captions;

/// <summary>
/// Captions in the compact notch: the pet with its working eyes, "Captions · On", and a Stop circle beside it.
/// The words themselves are in the strip below, never here. It keeps going, so a call or a timer takes the pet
/// and this waits as the dot.
/// </summary>
internal sealed class CaptionsContent(Action stop) : ICompactContent, IPetActivity
{
    static readonly CircleSpec StopCircle = new(CircleAction.Stop, CircleTone.Neutral, true, "Stop captions");

    public PetActivityState Describe(DateTime utcNow) => new()
    {
        Costume = PetCostume.Custom,
        Face = PetFace.Focused,
        Label = "Captions",
        Value = "On",
        Ongoing = true,
        Circle = StopCircle,
    };

    public void RunCircle(CircleAction action)
    {
        if (action == CircleAction.Stop) stop();
    }

    // The plain pill, for anywhere the pet doesn't draw it.
    public double Build(ContentControl left, ContentControl right)
    {
        left.Content = Words("Captions", "Fg2");
        right.Content = Words("On", "Fg");
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
