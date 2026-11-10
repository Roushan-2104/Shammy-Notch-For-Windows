using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Notch;
using Shammy.Core.OnlineVoice;
using Shammy.Infrastructure;

namespace Shammy.Modules.OnlineVoice;

/// <summary>
/// The pet while it is saying something aloud: its talking face, its name on the left and, when they fit, the
/// words on the right. A moment, not a state: it has no circle, never waits as the dot, and is only posted when
/// the notch has nothing else to show. The words stay off a shared screen.
/// </summary>
internal sealed class SpeakingContent(string name, string words) : ICompactContent, IPetActivity
{
    public PetActivityState Describe(DateTime utcNow) => new()
    {
        Costume = PetCostume.Custom,
        Face = PetFace.Talking,
        Label = name,
        Value = PrivacyState.Presenting ? "" : SpokenPhrases.PetValue(words),
        Ongoing = false,
    };

    public void RunCircle(CircleAction action)
    {
    }

    // The plain pill, for anywhere the pet doesn't draw it.
    public double Build(ContentControl left, ContentControl right)
    {
        right.Content = new TextBlock
        {
            Text = name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["Fg"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        return 80;
    }

    public UIElement? BuildBubble() => null;
}
