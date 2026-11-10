using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Notch;
using Shammy.Core.Nudges;

namespace Shammy.Modules.Nudges;

/// <summary>
/// The eye break: the pet closes its eyes beside "Look away" and a 20-second countdown. The time left is worked
/// out each time the notch asks (once a second while it shows), so nothing here ticks.
/// </summary>
internal sealed class EyeBreakContent(DateTime startedUtc, Action skip) : ICompactContent, IPetActivity
{
    public PetActivityState Describe(DateTime utcNow) => new()
    {
        Costume = PetCostume.Custom,
        Face = PetFace.Resting,
        Label = NudgeText.EyeLabel,
        Value = NudgeText.Countdown(startedUtc, utcNow),
        Ongoing = false,
        Circle = new CircleSpec(CircleAction.Dismiss, CircleTone.Neutral, true, NudgeText.SkipBreak),
    };

    public void RunCircle(CircleAction action)
    {
        if (action == CircleAction.Dismiss) skip();
    }

    public double Build(ContentControl left, ContentControl right) => NudgeEars.Build(right, NudgeText.EyeLabel);
}

/// <summary>
/// The water question: a happy pet beside "Water?" and today's glasses. The check counts one; left alone it
/// goes away uncounted.
/// </summary>
internal sealed class WaterContent(Func<int> glassesToday, Action drank) : ICompactContent, IPetActivity
{
    bool _counted;

    public PetActivityState Describe(DateTime utcNow) => new()
    {
        Costume = PetCostume.Custom,
        Face = PetFace.Happy,
        Label = NudgeText.WaterLabel,
        Value = NudgeText.Glasses(glassesToday()),
        Ongoing = false,
        Circle = new CircleSpec(CircleAction.Dismiss, CircleTone.Accent, true, NudgeText.HadWater),
    };

    public void RunCircle(CircleAction action)
    {
        // One glass per reminder, however the click arrives.
        if (action != CircleAction.Dismiss || _counted) return;
        _counted = true;
        drank();
    }

    public double Build(ContentControl left, ContentControl right) => NudgeEars.Build(right, NudgeText.WaterLabel);
}

/// <summary>The plain pill, for anywhere the pet doesn't draw the nudge.</summary>
file static class NudgeEars
{
    public static double Build(ContentControl right, string text)
    {
        right.Content = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["Fg"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        return 90;
    }
}
