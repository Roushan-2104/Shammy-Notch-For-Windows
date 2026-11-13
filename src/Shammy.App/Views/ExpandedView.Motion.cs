using System.Windows;
using System.Windows.Media;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Modules;
using Shammy.Motion;
using Shammy.Views.Cards;

namespace Shammy.Views;

/// <summary>
/// Which card the hover peek shows (the most live relevant card, as the old Focus Wall rail focused it, now without
/// tiles on screen), and the quick spring a view grows in with when it opens. Springs run only while something
/// moves (see <see cref="Animator"/>); Calm motion snaps.
/// </summary>
public partial class ExpandedView
{
    readonly NotchCard _nowPlaying;
    List<NotchCard> _rail = new();
    int _focus = -1;
    string? _focusedId;
    bool _motionHooked;

    static bool ReducedMotion => !MotionPrefs.Lively;

    /// <summary>Every card the peek can show, including the built-in now-playing one while no module provides music.</summary>
    IEnumerable<NotchCard> RailCandidates =>
        _cards.Any(c => string.Equals(c.Id, "music", StringComparison.OrdinalIgnoreCase)) ? _cards : _cards.Prepend(_nowPlaying);

    IReadOnlyList<string> ComputeRail() =>
        WallRail.Tiles(RailCandidates.Select(c => new WallCard(c.Id, c.Order, IsRelevant(c))));

    static bool IsRelevant(NotchCard c)
    {
        try { return c.IsRelevant(); }
        catch (Exception ex)
        {
            Log.Warn($"The {c.Id} card's relevance check failed", ex);
            return false;
        }
    }

    static int LiveOf(NotchCard c)
    {
        try { return c.Live(); }
        catch (Exception ex)
        {
            Log.Warn($"The {c.Id} card's live check failed", ex);
            return 0;
        }
    }

    int RailIndex(string id) => _rail.FindIndex(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The card the peek is about (null when no card is relevant).</summary>
    internal NotchCard? FocusedCard => _focus >= 0 && _focus < _rail.Count ? _rail[_focus] : null;

    /// <summary>The card the hover peek shows: the one chosen, or — while the notch is closed — the most live one now.</summary>
    internal NotchCard? PeekCard()
    {
        if (!IsVisible) _focusedId = null;
        ChoosePeek();
        return FocusedCard;
    }

    /// <summary>
    /// "wall-focus &lt;id|next|prev&gt;" moves the peek's card; "wall-focus tool &lt;n|moment …&gt;" presses the
    /// home's tool n (as <see cref="HomeTool"/> does).
    /// </summary>
    internal string WallFocus(string arg)
    {
        arg = arg.Trim();
        if (arg.StartsWith("tool", StringComparison.OrdinalIgnoreCase)) return HomeTool(arg[4..]);
        ChoosePeek();
        if (_rail.Count == 0) return "error: no card is relevant";
        int i = arg.ToLowerInvariant() switch
        {
            "next" => WallRail.Step(_focus, 1, _rail.Count),
            "prev" or "previous" => WallRail.Step(_focus, -1, _rail.Count),
            "" => _focus,
            _ => RailIndex(arg),
        };
        if (i < 0) return $"error: '{arg}' isn't relevant now ({string.Join(",", _rail.Select(c => c.Id))})";
        SetPeek(i);
        return $"focus={FocusedCard?.Id ?? "-"} rail=[{string.Join(",", _rail.Select(c => c.Id))}]";
    }

    /// <summary>Works out the relevant cards and keeps (or picks) the peek's card; cheap, nothing is drawn.</summary>
    void ChoosePeek()
    {
        var ids = ComputeRail();
        var byId = RailCandidates.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        _rail = ids.Select(id => byId[id]).ToList();
        SetPeek(WallRail.Keep(ids, _focusedId, _rail.Select(LiveOf).ToList()));
    }

    void SetPeek(int index)
    {
        bool moved = index != _focus;
        _focus = index;
        _focusedId = index >= 0 && index < _rail.Count ? _rail[index].Id : null;
        if (moved) FocusChanged?.Invoke();
    }

    // ---- Motion -----------------------------------------------------------------------------------

    readonly SpringValue _inOpacity = new(1, 0.002), _inScale = new(1, 0.0005);
    readonly ScaleTransform _inTransform = new(1, 1);
    FrameworkElement? _animTarget;

    RectangleGeometry RoundedFrameClip() => new(new Rect(0, 0, WallDetail.W, DetailFrame.Height), WallDetail.Radius, WallDetail.Radius);

    /// <summary>A quick spring grow (scale 0.97 → 1, fading in) as a view opens or the home comes back.</summary>
    void AnimateIn(FrameworkElement el)
    {
        if (DetailContent.Content is WallDetail) DetailFrame.Clip = RoundedFrameClip();
        if (ReducedMotion) return;
        if (_animTarget is not null && _animTarget != el)
        {
            _animTarget.Opacity = 1;
            _animTarget.RenderTransform = Transform.Identity;
        }
        _animTarget = el;
        el.RenderTransformOrigin = new Point(0.5, 0);
        el.RenderTransform = _inTransform;
        _inOpacity.Snap(0);
        _inScale.Snap(0.97);
        _inOpacity.AnimateTo(1, 0.22, 1);
        _inScale.AnimateTo(1, 0.3, 0.72);
        ApplyMotion();
        EnsureMotion();
    }

    void EnsureMotion()
    {
        if (_motionHooked) return;
        _motionHooked = true;
        Animator.Frame += OnMotionFrame;
    }

    void OnMotionFrame(double dt)
    {
        ApplyMotion();
        if (!MotionSettled) return;
        Animator.Frame -= OnMotionFrame;
        _motionHooked = false;
        // Rest exactly on the targets (a settled spring can stop a hair short of 1, leaving text at a fractional
        // scale) and repaint once: the layered window only refreshes what changed, so a pixel left over from a
        // moving frame of the spring-in could otherwise stay on screen.
        if (_animTarget is not null) _animTarget.Opacity = Math.Clamp(_inOpacity.Target, 0, 1);
        _inTransform.ScaleX = _inTransform.ScaleY = _inScale.Target;
        InvalidateVisual();
        if (_animTarget is UIElement target) target.InvalidateVisual();
        // A view swapped in place (home → a card) can leave pieces of the old one behind; repaint the whole island.
        (Window.GetWindow(this) as Shell.NotchWindow)?.RepaintAll();
    }

    bool MotionSettled => _inOpacity.IsSettled && _inScale.IsSettled;

    void ApplyMotion()
    {
        if (_animTarget is null) return;
        _animTarget.Opacity = Math.Clamp(_inOpacity.Value, 0, 1);
        _inTransform.ScaleX = _inTransform.ScaleY = _inScale.Value;
    }

    /// <summary>Hidden: everything jumps to where it was going and the frame clock is let go.</summary>
    void StopMotion()
    {
        _inOpacity.Snap(1);
        _inScale.Snap(1);
        ApplyMotion();
        if (_motionHooked)
        {
            Animator.Frame -= OnMotionFrame;
            _motionHooked = false;
        }
    }
}
