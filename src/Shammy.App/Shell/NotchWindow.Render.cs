using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Core.Music;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Modules;
using Shammy.Motion;
using Shammy.Notifications;
using Shammy.Views;

namespace Shammy.Shell;

public partial class NotchWindow
{
    // ---- Per-frame rendering ------------------------------------------------------------------

    void OnFrame(double dt) => ApplyVisuals(force: false);

    void ApplyVisuals(bool force)
    {
        double w = _w.Value, h = _h.Value, r = _r.Value;
        if (force || w != _lastW || h != _lastH || r != _lastR)
        {
            _lastW = w;
            _lastH = h;
            _lastR = r;
            Shape.Update(w, h, r);
            ShadowShape.Update(w, h, r);
            Edge.UpdateShape(w, h, r);
            ContentHost.Clip = Shape.Geometry;

            bool visible = h > 1;
            Canvas.SetLeft(HitArea, Cx - w / 2 - 12);
            Canvas.SetTop(HitArea, 0);
            HitArea.Width = visible ? w + 24 : 0;
            HitArea.Height = visible ? h + 8 : 0;
        }
        ShadowShape.Opacity = _shadow.Value;

        bool lively = MotionPrefs.Lively;
        foreach (var layer in _layers)
        {
            double o = layer.Opacity.Value, swap = layer.Swap.Value;
            layer.El.Opacity = o * (0.2 + 0.8 * swap);
            layer.Scale.ScaleX = layer.Scale.ScaleY = layer.Size.Value;
            if (!layer.Active && o <= 0.003 && layer.El.Visibility == Visibility.Visible) layer.El.Visibility = Visibility.Collapsed;

            // Content blurs through a change: soft while it is part-way in or out, sharp (and free) once settled.
            double clear = Math.Min(o, swap);
            if (lively && clear > 0.02 && clear < 0.97 && layer.Blurs)
            {
                layer.Blur.Radius = (1 - clear) * 6;
                if (layer.El.Effect is null) layer.El.Effect = layer.Blur;
            }
            else if (layer.El.Effect is not null)
            {
                layer.El.Effect = null;
            }
        }

        double tab = _tabShown.Value;
        TuckTab.Opacity = tab;
        TuckTab.IsHitTestVisible = _tabShown.Target > 0 && !_tucked;
        TuckTab.Visibility = tab > 0.003 ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(TuckTab, Cx - TuckTab.Width / 2);
        Canvas.SetTop(TuckTab, h - 1);
        TuckHit.IsHitTestVisible = TuckTab.IsHitTestVisible;
        TuckHit.Visibility = TuckTab.Visibility;
        Canvas.SetLeft(TuckHit, Cx - TuckHit.Width / 2);
        Canvas.SetTop(TuckHit, h - 1);
        double away = _tuck.Value;
        _rootShift.Y = -away * (h + 24);
        _lineShift.Y = -_rootShift.Y;
        TuckLine.Opacity = away;
        Canvas.SetLeft(TuckLine, Cx - TuckLine.Width / 2);
        PlaceStack(StackA, _stackA.Value, w, h, 36, 7);
        PlaceStack(StackB, _stackB.Value, w, h, 72, 14);
        _chrome.Apply(Cx, w, h, r);
        Bubble.Opacity = _bubbleOpacity.Value;
        BubbleScale.ScaleX = BubbleScale.ScaleY = _bubbleScale.Value;
        Canvas.SetLeft(Bubble, Cx + w / 2 + 10);
        Canvas.SetTop(Bubble, 0);
        Bubble.IsHitTestVisible = _bubbleOpacity.Target > 0;

        if (_w.IsSettled && _h.IsSettled && _shadow.IsSettled)
        {
            double want = (_spec.H + ShadowPad) * _scale;
            if (Math.Abs(Height - want) > 0.5) Height = want;
        }
    }

    /// <summary>One or two cards show under a message while more wait beneath it.</summary>
    void UpdateStack()
    {
        bool on = _mode == NotchMode.Notification;
        _stackA.AnimateTo(on && _stackDepth >= 1 ? 1 : 0, 0.3, 1);
        _stackB.AnimateTo(on && _stackDepth >= 2 ? 1 : 0, 0.3, 1);
    }

    /// <summary>A waiting card: narrower than the message above it and peeking out just below, sliding out as it appears.</summary>
    void PlaceStack(Border card, double shown, double w, double h, double narrower, double below)
    {
        if (shown <= 0.003)
        {
            if (card.Visibility == Visibility.Visible) card.Visibility = Visibility.Collapsed;
            return;
        }
        card.Visibility = Visibility.Visible;
        card.Opacity = shown;
        card.Width = Math.Max(0, w - narrower);
        Canvas.SetLeft(card, Cx - card.Width / 2);
        Canvas.SetTop(card, h - card.Height + below * shown);
    }

    internal string DescribeStack() =>
        $"depth={_stackDepth} a={_stackA.Target:0}/{StackA.Visibility} b={_stackB.Target:0}/{StackB.Visibility} " +
        $"aTop={Canvas.GetTop(StackA):0.0} aWidth={StackA.Width:0} notchH={_h.Value:0.0} blur=[{string.Join(",", _layers.Where(l => l.El.Effect is not null).Select(l => l.El.GetType().Name))}]";

    void EnsureWindowHeight(double contentHeight)
    {
        double want = (contentHeight + ShadowPad) * _scale;
        if (want > Height) Height = want;
    }
}
