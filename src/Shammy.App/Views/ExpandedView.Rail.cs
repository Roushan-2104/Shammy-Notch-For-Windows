using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Modules;
using Shammy.Motion;
using Shammy.Views.Cards;

namespace Shammy.Views;

/// <summary>
/// The Focus Wall rail: the relevant cards as 6:5 tiles; the focused one (most live when the notch opens, then
/// wherever the pointer or ←/→ goes) springs to 1.15× with the accent outline and soft glow, shows its words and
/// primary control, while its neighbours step aside and dim. Clicking the focused tile opens its detail out of it.
/// Springs run only while something moves (see <see cref="Animator"/>); reduced motion snaps.
/// </summary>
public partial class ExpandedView
{
    const double Dimmed = 0.58, SlotTop = (176 - WallTile.H) / 2;

    readonly NotchCard _nowPlaying;
    readonly Dictionary<NotchCard, RailSlot> _slots = new();
    List<NotchCard> _rail = new();
    int _focus = -1;
    string? _focusedId;
    readonly SpringValue _railStart = new(0, 0.2);
    readonly TranslateTransform _railShift = new();
    bool _motionHooked, _hoverArmed;
    Interop.Native.POINT _disarmAt;
    RailSlot? _pressedSlot;
    Point _pressedAt;

    static bool ReducedMotion => !Infrastructure.MotionPrefs.Lively;

    /// <summary>Every card that can be on the rail, including the built-in now-playing tile while no module provides music.</summary>
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

    /// <summary>The focused tile's card (null on an empty rail).</summary>
    internal NotchCard? FocusedCard => _focus >= 0 && _focus < _rail.Count ? _rail[_focus] : null;

    /// <summary>
    /// The tile the hover peek shows: the focused one, or — while the notch is closed — the one that will be
    /// focused when it opens (the most live).
    /// </summary>
    internal NotchCard? PeekCard()
    {
        if (_openId is null)
        {
            if (!IsVisible) _focusedId = null;
            RebuildRail(animate: false);
        }
        return FocusedCard;
    }

    /// <summary>"wall-focus &lt;id|next|prev&gt;": moves the focus as hover or the arrow keys would.</summary>
    internal string WallFocus(string arg)
    {
        arg = arg.Trim();
        if (_openId is not null) GoHome(animate: IsVisible);
        RebuildRail(animate: false);
        if (_rail.Count == 0) return "error: the rail is empty";
        switch (arg.ToLowerInvariant())
        {
            case "next": MoveFocus(1); break;
            case "prev" or "previous": MoveFocus(-1); break;
            case "": break;
            default:
                int i = RailIndex(arg);
                if (i < 0) return $"error: '{arg}' isn't on the rail ({string.Join(",", _rail.Select(c => c.Id))})";
                SetFocus(i, animate: IsVisible);
                break;
        }
        return $"focus={FocusedCard?.Id ?? "-"} rail=[{string.Join(",", _rail.Select(c => c.Id))}]";
    }

    void MoveFocus(int delta)
    {
        if (_rail.Count == 0) return;
        SetFocus(WallRail.Step(_focus, delta, _rail.Count), animate: IsVisible);
    }

    /// <summary>Lays out the rail; cheap when nothing changed, so frequent CardsChanged/Refresh calls cost nothing.</summary>
    void RebuildRail(bool animate)
    {
        var ids = ComputeRail();
        var byId = RailCandidates.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var cards = ids.Select(id => byId[id]).ToList();
        bool changed = !cards.SequenceEqual(_rail);
        if (changed)
        {
            RailStrip.Children.Clear();
            for (int i = 0; i < cards.Count; i++)
            {
                var slot = SlotFor(cards[i]);
                Canvas.SetLeft(slot.Root, i * (WallTile.W + WallRail.Gap));
                Canvas.SetTop(slot.Root, SlotTop);
                RailStrip.Children.Add(slot.Root);
            }
            _rail = cards;
        }
        RailStrip.RenderTransform = _railShift;
        EmptyText.Visibility = _openId is null && cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // The notch is as wide as its tiles need; an opened view sets its own size.
        if (_openId is null) SetBodySize(WallRail.OpenWidth(cards.Count), cards.Count == 0 ? EmptyHeight : HomeHeight);
        int focus = WallRail.Keep(ids, _focusedId, cards.Select(LiveOf).ToList());
        if (changed || focus != _focus) SetFocus(focus, animate);
    }

    RailSlot SlotFor(NotchCard card)
    {
        if (_slots.TryGetValue(card, out var s)) return s;
        s = new RailSlot(card, card == _nowPlaying);
        s.Root.MouseMove += (_, _) =>
        {
            if (!HoverArmed()) return;
            int i = _rail.IndexOf(card);
            if (i >= 0 && i != _focus) SetFocus(i, animate: true);
        };
        // Controls on the tile handle their own press, so it never gets here; a drag isn't a click either.
        s.Root.MouseLeftButtonDown += (_, e) =>
        {
            _pressedSlot = s;
            _pressedAt = e.GetPosition(this);
        };
        s.Root.MouseLeftButtonUp += (_, e) =>
        {
            bool click = _pressedSlot == s && !e.Handled && (e.GetPosition(this) - _pressedAt).Length < 6;
            _pressedSlot = null;
            if (!click) return;
            e.Handled = true;
            int i = _rail.IndexOf(card);
            if (i < 0) return;
            if (i != _focus) SetFocus(i, animate: true);
            else if (card != _nowPlaying) OpenByClick(card.Id);
        };
        _slots[card] = s;
        return s;
    }

    void ForgetSlot(NotchCard card)
    {
        if (!_slots.Remove(card, out var slot)) return;
        RailStrip.Children.Remove(slot.Root);
        slot.Release();
        _rail.Remove(card);
    }

    /// <summary>Focuses tile <paramref name="index"/> (−1: none): it grows, its words show, neighbours step aside and dim.</summary>
    void SetFocus(int index, bool animate)
    {
        bool moved = index != _focus;
        _focus = index;
        _focusedId = index >= 0 && index < _rail.Count ? _rail[index].Id : null;
        bool snap = !animate || ReducedMotion || !IsVisible;
        for (int j = 0; j < _rail.Count; j++)
        {
            var slot = _slots[_rail[j]];
            bool on = j == index;
            slot.SetFocused(on);
            Panel.SetZIndex(slot.Root, on ? 2 : 1);
            slot.To(on ? WallRail.FocusScale : 1, WallRail.Offset(j, index), on || index < 0 ? 1 : Dimmed, on ? 1 : 0, snap);
        }
        double start = WallRail.Start(_rail.Count, Math.Max(0, index), Width - WallRail.Padding);
        if (snap) _railStart.Snap(start);
        else _railStart.AnimateTo(start, 0.45, 0.9);
        ApplyMotion();
        if (!snap) EnsureMotion();
        if (moved) FocusChanged?.Invoke();
    }

    /// <summary>Back from a detail: the tile drops into its focused place from slightly larger, like the mockup's settle.</summary>
    void Settle(int index)
    {
        if (ReducedMotion || index < 0 || index >= _rail.Count) return;
        _slots[_rail[index]].Settle();
        EnsureMotion();
    }

    // ---- Hover arming -----------------------------------------------------------------------------

    /// <summary>
    /// Hover moves the focus only after the pointer really moves: the rail appearing under a still pointer (the
    /// notch opening, Back) mustn't steal the focus. WPF also sends synthetic moves after layout, hence the real cursor.
    /// </summary>
    bool HoverArmed()
    {
        if (_hoverArmed) return true;
        if (!Interop.Native.GetCursorPos(out var p)) return false;
        if (Math.Abs(p.X - _disarmAt.X) + Math.Abs(p.Y - _disarmAt.Y) < 3) return false;
        _hoverArmed = true;
        return true;
    }

    void DisarmHover()
    {
        _hoverArmed = false;
        Interop.Native.GetCursorPos(out _disarmAt);
    }

    // ---- Motion -----------------------------------------------------------------------------------

    // The open-in-place morph: the tile's rect grows into the detail; the art panel slides from the tile's
    // place to the left edge; the words rise in a beat later.
    readonly SpringValue _morph = new(1, 0.002), _rise = new(1, 0.005);
    (WallDetail Detail, Rect From, RectangleGeometry Clip, TranslateTransform Art, TranslateTransform Rise)? _morphing;

    readonly SpringValue _inOpacity = new(1, 0.002), _inScale = new(1, 0.0005);
    readonly ScaleTransform _inTransform = new(1, 1);
    FrameworkElement? _animTarget;

    /// <summary>The tile's current on-screen rect (scaled) in the detail frame's coordinates, or null when it isn't showing.</summary>
    Rect? TileRectInDetail(NotchCard card)
    {
        if (!_slots.TryGetValue(card, out var slot) || !_rail.Contains(card) || !slot.Root.IsVisible || ReducedMotion) return null;
        try
        {
            var r = slot.Root.TransformToAncestor(Body).TransformBounds(new Rect(0, 0, WallTile.W, WallTile.H));
            // The detail frame sits centred under the top row: x = (972 − 924) / 2, y = 36 + 12.
            r.Offset(-(Body.ActualWidth - WallDetail.W) / 2, -(36 + 12));
            return r;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    void Morph(WallDetail detail, Rect from)
    {
        var clip = new RectangleGeometry(from, 16, 16);
        DetailFrame.Clip = clip;
        var art = new TranslateTransform(Math.Max(0, from.X), 0);
        detail.Art.RenderTransform = art;
        var rise = new TranslateTransform(0, 6);
        detail.Info.RenderTransform = rise;
        detail.Info.Opacity = 0;
        DetailLine.Opacity = 0;
        _morphing = (detail, from, clip, art, rise);
        _morph.Snap(0);
        _morph.AnimateTo(1, 0.44, 0.95);
        _rise.Snap(0);
        _rise.AnimateTo(1, 0.3, 1, delay: 0.16);
        ApplyMorph();
        EnsureMotion();
    }

    void ApplyMorph()
    {
        if (_morphing is not { } m) return;
        double p = Math.Clamp(_morph.Value, 0, 1.02), q = Math.Clamp(_rise.Value, 0, 1);
        var full = new Rect(0, 0, WallDetail.W, DetailFrame.Height);
        static double L(double a, double b, double t) => a + (b - a) * t;
        m.Clip.Rect = new Rect(L(m.From.X, full.X, p), L(m.From.Y, full.Y, p), Math.Max(0, L(m.From.Width, full.Width, p)), Math.Max(0, L(m.From.Height, full.Height, p)));
        m.Clip.RadiusX = m.Clip.RadiusY = L(16, WallDetail.Radius, Math.Min(1, p));
        m.Art.X = Math.Max(0, m.From.X) * (1 - Math.Min(1, p));
        m.Detail.Info.Opacity = q;
        m.Rise.Y = 6 * (1 - q);
        DetailLine.Opacity = Math.Min(1, p);
        if (_morph.IsSettled && _rise.IsSettled) EndMorph();
    }

    void EndMorph()
    {
        if (_morphing is not { } m) return;
        _morphing = null;
        m.Art.X = 0;
        m.Rise.Y = 0;
        m.Detail.Info.Opacity = 1;
        DetailLine.Opacity = 1;
        DetailFrame.Clip = RoundedFrameClip();
    }

    RectangleGeometry RoundedFrameClip() => new(new Rect(0, 0, WallDetail.W, DetailFrame.Height), WallDetail.Radius, WallDetail.Radius);

    /// <summary>A quick spring grow (scale 0.97 → 1, fading in) for a view that opened without a tile to morph from.</summary>
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
    }

    bool MotionSettled => _railStart.IsSettled && _morphing is null && _inOpacity.IsSettled && _inScale.IsSettled
                          && _rail.All(c => _slots[c].IsSettled);

    void ApplyMotion()
    {
        _railShift.X = Math.Round(_railStart.Value, 2);
        foreach (var c in _rail) _slots[c].Apply();
        ApplyMorph();
        if (_animTarget is not null)
        {
            _animTarget.Opacity = Math.Clamp(_inOpacity.Value, 0, 1);
            _inTransform.ScaleX = _inTransform.ScaleY = _inScale.Value;
        }
    }

    /// <summary>Hidden: everything jumps to where it was going and the frame clock is let go.</summary>
    void StopMotion()
    {
        foreach (var slot in _slots.Values) slot.SnapToTargets();
        _railStart.Snap(_railStart.Target);
        _inOpacity.Snap(1);
        _inScale.Snap(1);
        EndMorph();
        ApplyMotion();
        if (_motionHooked)
        {
            Animator.Frame -= OnMotionFrame;
            _motionHooked = false;
        }
    }

    /// <summary>
    /// One tile on the rail: the card's tile plus what the notch draws around it when focused — a 1.5 px accent
    /// outline, a soft accent glow and a dark drop shadow — and the springs for its scale, step and dim.
    /// </summary>
    sealed class RailSlot
    {
        public NotchCard Card { get; }
        public Grid Root { get; }
        public WallTile? Tile { get; }

        readonly FrameworkElement _shadow, _glow;
        readonly Border _ring;
        readonly ScaleTransform _scale = new(1, 1);
        readonly TranslateTransform _shift = new();
        readonly SpringValue _s = new(1, 0.001), _x = new(0, 0.05), _dim = new(1, 0.004), _lift = new(0, 0.004);

        public RailSlot(NotchCard card, bool builtIn)
        {
            Card = card;
            Root = new Grid
            {
                Width = WallTile.W,
                Height = WallTile.H,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = { _scale, _shift } },
            };
            // The drop shadow and the accent glow are drawn once into bitmaps and shared: as live effects they
            // were re-blurred on every repaint inside the focused tile (a clock, a progress bar), which is costly
            // on the notch's layered, software-rendered window.
            _shadow = Halo(ShadowImage.Value, ShadowPad);
            _glow = Halo(GlowImage.Value, GlowPad);
            _ring = new Border
            {
                Margin = new Thickness(-1.5),
                CornerRadius = new CornerRadius(WallTile.Radius + 1.5),
                BorderBrush = CardKit.Accent,
                BorderThickness = new Thickness(1.5),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            Root.Children.Add(_shadow);
            Root.Children.Add(_glow);
            CardKit.Detach(card.Compact);
            if (card.Compact is WallTile tile)
            {
                Tile = tile;
                Root.Children.Add(tile);
            }
            else
            {
                // A card that hasn't moved to a WallTile yet: its compact scaled into a plain tile.
                Root.Children.Add(new Border
                {
                    Width = WallTile.W,
                    Height = WallTile.H,
                    CornerRadius = new CornerRadius(WallTile.Radius),
                    Background = CardKit.Surface,
                    ClipToBounds = true,
                    Clip = new RectangleGeometry(new Rect(0, 0, WallTile.W, WallTile.H), WallTile.Radius, WallTile.Radius),
                    Child = new Viewbox { Stretch = Stretch.Uniform, Margin = new Thickness(4), Child = card.Compact },
                });
            }
            Root.Children.Add(_ring);
            if (builtIn) Root.Cursor = null;
            Name(false);
        }

        void Name(bool focused) => CardKit.Name(Root, Card.Title + (focused ? ". Open details" : ". Focus"));

        public bool IsSettled => _s.IsSettled && _x.IsSettled && _dim.IsSettled && _lift.IsSettled;

        public void SetFocused(bool on)
        {
            if (Tile is not null) Tile.IsLit = on;
            Name(on);
        }

        public void To(double scale, double shift, double opacity, double lift, bool snap)
        {
            if (snap)
            {
                _s.Snap(scale);
                _x.Snap(shift);
                _dim.Snap(opacity);
                _lift.Snap(lift);
                return;
            }
            // Fast with a slight overshoot for the grow and the step aside; the dim is a short plain fade.
            _s.AnimateTo(scale, 0.42, 0.62);
            _x.AnimateTo(shift, 0.42, 0.7);
            _dim.AnimateTo(opacity, 0.2, 1);
            _lift.AnimateTo(lift, 0.2, 1);
        }

        public void Settle()
        {
            _s.Snap(1.4);
            _dim.Snap(0.6);
            _s.AnimateTo(WallRail.FocusScale, 0.5, 0.62);
            _dim.AnimateTo(1, 0.3, 1);
        }

        public void SnapToTargets()
        {
            _s.Snap(_s.Target);
            _x.Snap(_x.Target);
            _dim.Snap(_dim.Target);
            _lift.Snap(_lift.Target);
            Apply();
        }

        public void Apply()
        {
            _scale.ScaleX = _scale.ScaleY = _s.Value;
            _shift.X = _x.Value;
            Root.Opacity = Math.Clamp(_dim.Value, 0, 1);
            double lift = Math.Clamp(_lift.Value, 0, 1);
            var shown = lift > 0.01 ? Visibility.Visible : Visibility.Collapsed;
            _shadow.Visibility = _glow.Visibility = _ring.Visibility = shown;
            _shadow.Opacity = lift;
            _glow.Opacity = 0.28 * lift;
            _ring.Opacity = lift;
        }

        const double ShadowPad = 64, GlowPad = 32;

        static readonly Lazy<ImageSource> ShadowImage = new(() => Prerender(CardKit.Ground, 0,
            new DropShadowEffect { Color = Colors.Black, BlurRadius = 36, ShadowDepth = 18, Direction = 270, Opacity = 0.55 }, ShadowPad));

        static readonly Lazy<ImageSource> GlowImage = new(() => Prerender(CardKit.Accent, 2, new BlurEffect { Radius = 24 }, GlowPad));

        /// <summary>A tile-sized rounded shape with its effect, rendered once at 2x into a frozen bitmap.</summary>
        static ImageSource Prerender(Brush fill, double grow, Effect effect, double pad)
        {
            double w = WallTile.W + 2 * grow, h = WallTile.H + 2 * grow;
            var shape = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(WallTile.Radius + grow),
                Background = fill,
                Effect = effect,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var host = new Grid { Width = WallTile.W + 2 * pad, Height = WallTile.H + 2 * pad, Children = { shape } };
            host.Measure(new Size(host.Width, host.Height));
            host.Arrange(new Rect(0, 0, host.Width, host.Height));
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(host.Width * 2), (int)(host.Height * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(host);
            bitmap.Freeze();
            return bitmap;
        }

        static FrameworkElement Halo(ImageSource image, double pad) => new System.Windows.Controls.Image
        {
            Source = image,
            Stretch = Stretch.Fill,
            Width = WallTile.W + 2 * pad,
            Height = WallTile.H + 2 * pad,
            Margin = new Thickness(-pad),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        /// <summary>Gives the card's compact back (the card was replaced).</summary>
        public void Release() => CardKit.Detach(Card.Compact);
    }
}
