using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Notch;
using Shammy.Views;

namespace Shammy.Modules.Productivity;

/// <summary>
/// A timer, focus session or pomodoro phase that just ran out. The pet jumps for joy with a little confetti, and
/// the action circle (in the accent: it's the one thing to do) dismisses it. Without the pet it's the old
/// glyph-and-text pill.
/// </summary>
internal sealed class TimerDoneContent : ICompactContent, IPetActivity
{
    readonly string _glyph, _text, _label, _value;
    readonly Action _dismiss;

    public TimerDoneContent(string glyph, string text, string label, string value, Action dismiss)
    {
        _glyph = glyph;
        _text = text;
        _label = label;
        _value = value;
        _dismiss = dismiss;
    }

    public PetActivityState Describe(DateTime utcNow) => new()
    {
        Costume = PetCostume.Timer,
        Done = true,
        Label = _label,
        Value = _value,
        Progress = 0,
    };

    public void RunCircle(CircleAction action)
    {
        if (action == CircleAction.Dismiss) _dismiss();
    }

    public double Build(ContentControl left, ContentControl right)
    {
        left.Content = CompactView.Glyph(_glyph, 15, Ui.Tokens.Res("AccentBrush"));
        right.Content = new TextBlock
        {
            Text = _text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Tokens.Res("Fg"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 170,
        };
        return 170;
    }
}
