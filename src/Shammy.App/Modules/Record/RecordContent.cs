using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Shammy.Core.Notch;
using Shammy.Core.Record;

namespace Shammy.Modules.Record;

/// <summary>
/// A recording in the compact notch: the pet in its ninja mask, "Rec" and the running time, and a red Stop circle
/// beside it. It keeps going, so a notification takes the pet for a moment and this comes back.
/// </summary>
internal sealed class RecordContent(Func<TimeSpan> elapsed, Action stop) : ICompactContent, IPetActivity
{
    static readonly CircleSpec StopCircle = new(CircleAction.Stop, CircleTone.Red, true, "Stop recording");

    /// <summary>Stop was asked for and the file is being finished: no circle, and "Saving" for the time.</summary>
    public bool Saving { get; set; }

    public PetActivityState Describe(DateTime utcNow) => new()
    {
        Costume = PetCostume.Custom,
        Face = PetFace.Ninja,
        Label = "Rec",
        Value = Saving ? "Saving" : RecordText.Elapsed(elapsed()),
        Ongoing = true,
        Circle = Saving ? null : StopCircle,
    };

    public void RunCircle(CircleAction action)
    {
        if (action == CircleAction.Stop && !Saving) stop();
    }

    // The plain pill, for anywhere the pet doesn't draw it.
    public double Build(ContentControl left, ContentControl right)
    {
        left.Content = new Ellipse { Width = 8, Height = 8, Fill = (Brush)Application.Current.Resources["Red"], VerticalAlignment = VerticalAlignment.Center };
        right.Content = new TextBlock
        {
            Text = "Recording",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["Fg"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        return 72;
    }

    public UIElement? BuildBubble() => new Ellipse { Width = 9, Height = 9, Fill = (Brush)Application.Current.Resources["Red"] };
}
