using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Notch;

namespace Shammy.Modules;

/// <summary>
/// A downloaded update: the pet shows the version, and the button beside the notch restarts into it. It stays until
/// it's pressed, because nobody should have to go looking for a tray icon to update. Calls, timers and downloads
/// keep the pet meanwhile (this waits as the small dot), and an update nobody presses still installs on quit.
/// </summary>
internal sealed class UpdateReadyContent(string version, Action restart) : ICompactContent, IPetActivity
{
    public PetActivityState Describe(DateTime utcNow) => new()
    {
        Costume = PetCostume.Update,
        Label = "Update",
        Value = version,
    };

    public void RunCircle(CircleAction action)
    {
        if (action == CircleAction.Restart) restart();
    }

    // The plain pill, for anywhere the pet doesn't draw it.
    public double Build(ContentControl left, ContentControl right)
    {
        right.Content = new TextBlock
        {
            Text = $"Update {version}",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Tokens.Res("Fg"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        return 100;
    }

    public UIElement? BuildBubble() => null;
}
