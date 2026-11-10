using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Agents;
using Shammy.Core.Notch;

namespace Shammy.Modules.Agents;

/// <summary>
/// A Claude Code session in the compact notch, worn by the pet. Working: narrowed scanning eyes, "Claude" and the
/// time working; it keeps going, so anything more urgent takes the pet and this waits as the dot. Waiting with no
/// card to answer on: wide eyes and "Needs you". Finished: happy eyes and "Done" for a few seconds, with a circle
/// that brings the session's window forward (or, when that window isn't known, just dismisses).
/// </summary>
/// <param name="finished">The "Done" moment rather than the live session.</param>
internal sealed class AgentPetContent(AgentsModule module, string sessionId, bool finished) : ICompactContent, IPetActivity
{
    static readonly CircleSpec OpenCircle = new(CircleAction.Open, CircleTone.Accent, true, "Open Claude Code");
    static readonly CircleSpec DismissCircle = new(CircleAction.Dismiss, CircleTone.Accent, true, "Dismiss");

    TextBlock? _value;

    public string SessionId => sessionId;

    public PetActivityState Describe(DateTime utcNow)
    {
        var s = module.Session(sessionId);
        bool canOpen = module.CanOpen(sessionId);
        if (finished || s is null || s.State == AgentState.Done)
        {
            bool failed = s?.Failed == true;
            return new PetActivityState
            {
                Costume = PetCostume.Custom,
                Face = failed ? PetFace.Notify : PetFace.Happy,
                Label = AgentText.PetLabel,
                Value = failed ? "Stopped" : "Done",
                Ongoing = false,
                CanOpen = true,
                Circle = canOpen ? OpenCircle : DismissCircle,
            };
        }
        if (s.State == AgentState.Waiting)
        {
            return new PetActivityState
            {
                Costume = PetCostume.Custom,
                Face = PetFace.Notify,
                Label = AgentText.PetLabel,
                Value = AgentText.PetValue(s, module.Now),
                Ongoing = true,
                Circle = canOpen ? OpenCircle : null,
            };
        }
        return new PetActivityState
        {
            Costume = PetCostume.Custom,
            Face = PetFace.Focused,
            Label = AgentText.PetLabel,
            Value = AgentText.PetValue(s, module.Now),
            Ongoing = true,
        };
    }

    public void RunCircle(CircleAction action) => module.CircleClicked(sessionId, finished, action);

    /// <summary>The plain pill, for anywhere the pet doesn't draw it. No running clock there, so nothing has to tick.</summary>
    public double Build(ContentControl left, ContentControl right)
    {
        left.Content = Words(AgentText.PetLabel, "Fg2");
        _value = Words(Value(), "Fg");
        right.Content = _value;
        return 64;
    }

    public void Refresh()
    {
        if (_value is null) return;
        string text = Value();
        if (_value.Text != text) _value.Text = text;
    }

    string Value() => module.Session(sessionId) is { } s && !finished
        ? s.State == AgentState.Working ? "Working" : AgentText.PetValue(s, module.Now)
        : "Done";

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
