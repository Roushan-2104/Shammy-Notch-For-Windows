using System.Windows.Threading;
using Shammy.Core.CircleMemory;
using Shammy.Core.Search;
using Shammy.Infrastructure;

namespace Shammy.Search;

/// <summary>
/// Circle memory's side of the circle flow: every circle is handed to the memory once its card shows, and again when a
/// better answer arrives for the same circle (a translation, an AI answer, the prices), so it stays one row with the
/// latest answer. A remembered row clicked in the Circles card comes back here as an island answer.
/// </summary>
internal partial class SearchCoordinator
{
    const string MemoryGlyph = "";

    // The circle being answered: its session and when it was drawn (the memory's key for "the same circle").
    int _circleSession = -1;
    DateTime _circleAt;
    bool _circleRemembered;

    void BeginCircleMemory(int session)
    {
        _circleSession = session;
        _circleAt = DateTime.Now;
        _circleRemembered = false;
    }

    /// <summary>
    /// Hands the current circle to the memory with what the island shows for it. Only for the circle's own session
    /// (a typed question or a test lookup isn't a circle); placeholders ("Thinking…") aren't answers.
    /// </summary>
    void RememberCircle(int session, string kind, string? title, string? line)
    {
        if (session != _session || session != _circleSession || _circle is not { } now) return;
        if (Modules.HostLinks.RememberCircle is not { } remember) return;
        if (line is { } l && (l.Trim().Length == 0 || (l.EndsWith('…') && l.Length < 40))) line = null;
        try
        {
            remember(new Modules.CircleMemory.CircleRecord(now.Selection.Text, title ?? "", line, kind, now.Info.Png, _circleAt));
            _circleRemembered = true;
        }
        catch (Exception ex)
        {
            Log.Warn("Handing a circle to memory failed", ex);
        }
    }

    /// <summary>A circle that showed no answer of its own (copied, sent to the full results) is still remembered.</summary>
    void RememberCircleAnyway(int session)
    {
        if (_circleRemembered || _circle is not { } now) return;
        var text = now.Selection.Text;
        RememberCircle(session, CircleKindName(now), text.Length > 0 ? Clip(text, 60) : "Selected area", null);
    }

    /// <summary>The memory's kind for a circle: what it was understood as, or a picture, or plain text.</summary>
    static string CircleKindName(CircleNow now) => now.Meaning is { } m ? m.Kind.ToString()
        : now.Selection.Kind == SelectionKind.Image ? CircleKinds.Picture : CircleKinds.Text;

    /// <summary>A remembered circle shown again as the island's answer (a row clicked in the Circles card).</summary>
    void ShowRemembered(string title, string label, string? answer)
    {
        Reset(keepNotch: true);
        int session = _session;
        var ctx = new Context(title, title, _settings.DefaultSearchMode, SearchFilter.None, null, null);
        string copy = answer is { Length: > 0 } ? answer : title;
        var actions = new List<Views.MomentAction>
        {
            new("Copy", () => Modules.SelectionTools.ClipboardText.TrySet(copy) ? "Copied" : "Clipboard is busy"),
            Run("Search", () => ShowResultAsync(ctx)),
        };
        if (Modules.HostLinks.OpenCircles is { } open)
        {
            // The moment closes first; the card opens once it has.
            actions.Add(new("All circles", () =>
            {
                _notch.Dispatcher.BeginInvoke(() => open(), DispatcherPriority.Background);
                return null;
            }, Closes: true));
        }
        var moment = new Views.IslandMoment(MemoryGlyph, label, Clip(title, 60), null, actions, Array.Empty<Views.MomentSend>())
        {
            Answer = answer,
            Width = 620,
            DragText = copy,
        };
        ShowMoment(session, moment, ctx);
    }
}
