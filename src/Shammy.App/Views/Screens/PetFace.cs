using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Shammy.Core.Notch;

namespace Shammy.Views.Screens;

/// <summary>What the drawn pet is doing on the designed screens.</summary>
internal enum FaceMood
{
    /// <summary>Blinks and glances side to side.</summary>
    Idle,
    /// <summary>The tour's hello: sleepy lids, eyes pop open, a stretch, then blush, smile and a wave.</summary>
    Wake,
    /// <summary>Happy eyes, smile, waving.</summary>
    Wave,
    /// <summary>Bobbing with a little open mouth and sound bars by its ear.</summary>
    Listen,
    /// <summary>Darting eyes, flat mouth, thinking dots.</summary>
    Think,
    /// <summary>Tall shifty eyes and an "o" mouth: peeking at what you circled.</summary>
    Peek,
    /// <summary>^ ^ eyes, blush, smile, a wiggle and floating hearts.</summary>
    Happy,
    /// <summary>A big jump with a grin and a burst of confetti (twice), then still.</summary>
    Celebrate,
    /// <summary>The alarm: hopping under a rattling bell, mouth going.</summary>
    Ring,
    /// <summary>Closed lids, slow breathing, drifting z's (snoozed).</summary>
    Sleep,
    /// <summary>A big morning stretch, then ^ ^ eyes, blush, smile and a wave (alarm stopped).</summary>
    Stretch,
    /// <summary>Open eyes that blink and look where <see cref="PetFace.Look"/> says (the wheel's centre).</summary>
    Watch,
}

/// <summary>
/// The pet's face as the mockups draw it (Alarm, Tour, Settings): two gradient eyes with a shine, arcs, lids,
/// mouths, blush and a few props (bell, waving hand, z's, hearts, confetti), built at the design's 1× size and
/// scaled. Moods are WPF animations on transforms; they run only while the face is visible, stop when it's
/// hidden, and become still poses when Windows' animation effects are off. Follows <see cref="PetLook"/>.
/// </summary>
internal sealed class PetFace : Grid
{
    const double W = 48, H = 40, Mid = 24, EyeY = 22;

    readonly Canvas _face = new() { Width = W, Height = H, ClipToBounds = false };
    readonly ScaleTransform _bodyScale = new(1, 1);
    readonly RotateTransform _bodyTurn = new();
    readonly TranslateTransform _bodyMove = new();
    readonly Canvas _eyes = new() { Width = W, Height = H };
    readonly TranslateTransform _eyesShift = new();
    readonly ScaleTransform _eyesPop = new(1, 1);
    readonly EyePart _left = new(), _right = new();
    // The cat's parts sit behind the eyes (see CatParts); hidden when the pet style is Eyes.
    readonly Canvas _cat = new() { Width = W, Height = H, IsHitTestVisible = false };
    readonly CatParts.Ear _earL = new(false, Brushes.White), _earR = new(true, Brushes.White);
    readonly CatParts.Whiskers _whiskL = new(false, Brushes.White), _whiskR = new(true, Brushes.White);
    readonly Path _nose = CatParts.Nose();
    readonly Ellipse _blushL = Blush(), _blushR = Blush();
    readonly Path _smile = Stroke("M1,0 Q1,4 4.5,4 Q8,4 8,0", 2), _flat = Stroke("M0,1 L6,1", 2);
    readonly Ellipse _oMouth = new() { Width = 6, Height = 6, StrokeThickness = 2, Fill = ScreenKit.B("#3A1F28") };
    readonly Border _grin = new() { Width = 11, Height = 6, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(2, 2, 9, 9), Background = ScreenKit.Blush };
    readonly ScaleTransform _mouthScale = new(1, 1);
    readonly Canvas _bell = new() { Width = 30, Height = 20 };
    readonly RotateTransform _bellSwing = new();
    readonly TranslateTransform _clapper = new();
    readonly Path _ringLines = Stroke("M3 9Q1 5 4 1.5M27 9Q29 5 26 1.5", 1.1);
    readonly ScaleTransform _ringLinesScale = new(1, 1);
    readonly Canvas _hand = new() { Width = 8, Height = 10 };
    readonly RotateTransform _handWave = new();
    readonly List<FrameworkElement> _props = new();
    readonly List<(IAnimatable Target, DependencyProperty Property)> _running = new();
    FaceMood _mood = FaceMood.Idle;
    bool _hooked;

    public PetFace(double scale = 1, FaceMood mood = FaceMood.Idle)
    {
        IsHitTestVisible = false;
        _mood = mood;
        var holder = new Grid { Width = W, Height = H, LayoutTransform = new ScaleTransform(scale, scale) };
        holder.Children.Add(_face);
        Children.Add(holder);

        _face.RenderTransformOrigin = new Point(0.5, 0.82);
        _face.RenderTransform = new TransformGroup { Children = { _bodyScale, _bodyTurn, _bodyMove } };
        _eyes.RenderTransformOrigin = new Point(0.5, (EyeY + 7) / H);
        _eyes.RenderTransform = new TransformGroup { Children = { _eyesPop, _eyesShift } };
        foreach (var part in new UIElement[] { _earL, _earR, _whiskL, _whiskR, _nose }) _cat.Children.Add(part);
        _face.Children.Add(_cat);
        _face.Children.Add(_eyes);
        _eyes.Children.Add(_left);
        _eyes.Children.Add(_right);
        _face.Children.Add(_blushL);
        _face.Children.Add(_blushR);
        foreach (var m in new FrameworkElement[] { _smile, _flat, _oMouth, _grin })
        {
            m.RenderTransformOrigin = new Point(0.5, 0.5);
            m.RenderTransform = _mouthScale;
            _face.Children.Add(m);
        }
        Canvas.SetLeft(_blushL, 3);
        Canvas.SetLeft(_blushR, 37);
        Canvas.SetTop(_blushL, 29.5);
        Canvas.SetTop(_blushR, 29.5);

        // The bell rides above the face, swinging from its top; its clapper knocks side to side.
        var bellBody = new Path
        {
            Data = Geometry.Parse("M7 1.2a1 1 0 0 1 1 1v.7a4.4 4.4 0 0 1 3.3 4.3v2.9l1.3 1.7H1.4l1.3-1.7V7.2A4.4 4.4 0 0 1 6 2.9v-.7a1 1 0 0 1 1-1Z"),
        };
        var clapper = new Ellipse { Width = 3, Height = 3, RenderTransform = _clapper };
        Canvas.SetLeft(clapper, 5.5);
        Canvas.SetTop(clapper, 12.1);
        var swing = new Canvas { Width = 14, Height = 16, RenderTransformOrigin = new Point(0.5, 0), RenderTransform = _bellSwing };
        swing.Children.Add(bellBody);
        swing.Children.Add(clapper);
        Canvas.SetLeft(swing, 8);
        Canvas.SetTop(swing, 1);
        _ringLines.RenderTransformOrigin = new Point(0.5, 0.5);
        _ringLines.RenderTransform = _ringLinesScale;
        _bell.Children.Add(_ringLines);
        _bell.Children.Add(swing);
        Canvas.SetLeft(_bell, Mid - 15);
        Canvas.SetTop(_bell, -4.5);
        _face.Children.Add(_bell);

        foreach (var (x, y, r) in new[] { (4.0, 6.6, 3.0), (1.5, 3.0, 1.1), (4.0, 2.0, 1.1), (6.5, 3.0, 1.1) })
        {
            var dot = new Ellipse { Width = r * 2, Height = r * 2 };
            Canvas.SetLeft(dot, x - r);
            Canvas.SetTop(dot, y - r);
            _hand.Children.Add(dot);
        }
        _hand.RenderTransformOrigin = new Point(0.5, 1);
        _hand.RenderTransform = _handWave;
        Canvas.SetLeft(_hand, 46);
        Canvas.SetTop(_hand, 21.5);
        _face.Children.Add(_hand);

        IsVisibleChanged += (_, _) => Restart();
        Unloaded += (_, _) => Unhook();
        Restyle();
        Pose();
    }

    public FaceMood Mood
    {
        get => _mood;
        set
        {
            if (_mood == value) return;
            _mood = value;
            Restart();
        }
    }

    /// <summary>A preview in a colour or style that isn't saved yet (Settings shows the choice before you leave).</summary>
    public PetEyeStyle? StyleOverride { get; set; }
    public PetEyeColor? ColorOverride { get; set; }

    /// <summary>Cat parts on or off for this face alone (the wheel without the Cat theme shows the plain eyes).</summary>
    public bool? CatOverride { get; set; }

    /// <summary>In the Watch mood: where the eyes look, in face pixels (about ±3 across, ±2 up and down).</summary>
    public void Look(double dx, double dy)
    {
        if (_mood != FaceMood.Watch) return;
        _eyesShift.X = Math.Clamp(dx, -3.5, 3.5);
        _eyesShift.Y = Math.Clamp(dy, -2.5, 2.5);
    }

    public void Restyle()
    {
        var solid = PetLook.Solid(ColorOverride);
        _left.Paint(StyleOverride ?? PetLook.Style, PetLook.Gradient(ColorOverride), solid);
        _right.Paint(StyleOverride ?? PetLook.Style, PetLook.Gradient(ColorOverride), solid);
        _smile.Stroke = _flat.Stroke = _oMouth.Stroke = solid;
        _grin.BorderBrush = solid;
        _ringLines.Stroke = ScreenKit.Fg;
        foreach (var child in _bell.Children.OfType<Canvas>().SelectMany(c => c.Children.OfType<Shape>())) child.Fill = ScreenKit.Fg;
        foreach (Ellipse dot in _hand.Children) dot.Fill = ScreenKit.Fg;
        Pose();
    }

    void Restart()
    {
        StopAll();
        if (IsVisible) Hook();
        Pose();
        if (IsVisible && !ScreenKit.ReducedMotion) Animate();
    }

    void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        PetLook.Changed += OnLookChanged;
    }

    void Unhook()
    {
        StopAll();
        if (!_hooked) return;
        _hooked = false;
        PetLook.Changed -= OnLookChanged;
    }

    void OnLookChanged()
    {
        Restyle();
        Restart();
    }

    // ---- Poses (the still frame of each mood, and what reduced motion shows) -----------------------

    void Pose()
    {
        foreach (var p in _props) _face.Children.Remove(p);
        _props.Clear();
        bool tall = _mood is FaceMood.Ring or FaceMood.Peek;
        var eye = EyeKind.Open;
        bool blush = false, bell = false, hand = false;
        FrameworkElement? mouth = null;
        switch (_mood)
        {
            case FaceMood.Wake:
            case FaceMood.Stretch:
                // The still frame is the end of the sequence; the animation starts from lids/open eyes.
                eye = _mood == FaceMood.Stretch ? EyeKind.Arc : EyeKind.Open;
                blush = hand = true;
                mouth = _smile;
                break;
            case FaceMood.Wave:
            case FaceMood.Happy:
                eye = EyeKind.Arc;
                blush = true;
                mouth = _smile;
                hand = _mood == FaceMood.Wave;
                break;
            case FaceMood.Celebrate:
                eye = EyeKind.Arc;
                blush = true;
                mouth = _grin;
                break;
            case FaceMood.Listen:
                mouth = _oMouth;
                _mouthScale.ScaleX = _mouthScale.ScaleY = 0.8;
                AddBars();
                break;
            case FaceMood.Think:
                mouth = _flat;
                AddDots();
                break;
            case FaceMood.Peek:
                mouth = _oMouth;
                break;
            case FaceMood.Ring:
                mouth = _oMouth;
                bell = true;
                break;
            case FaceMood.Sleep:
                eye = EyeKind.Lid;
                break;
        }
        if (_mood is not FaceMood.Listen) _mouthScale.ScaleX = _mouthScale.ScaleY = 1;
        _left.Open = _right.Open = tall ? CatLooks.Wide : _mood is FaceMood.Listen ? CatLooks.Alert : CatLooks.Slit;
        _left.Show(eye, tall);
        _right.Show(eye, tall);
        // Whiskers give way to whatever sits beside the face; the nose to a mouth.
        PoseCat(nose: mouth is null, whiskers: !hand && _mood is not (FaceMood.Listen or FaceMood.Think));
        var (w, h) = _left.Size;
        Canvas.SetLeft(_left, Mid - 4 - w);
        Canvas.SetLeft(_right, Mid + 4);
        Canvas.SetTop(_left, EyeY - h / 2);
        Canvas.SetTop(_right, EyeY - h / 2);
        _blushL.Visibility = _blushR.Visibility = blush ? Visibility.Visible : Visibility.Collapsed;
        _bell.Visibility = bell ? Visibility.Visible : Visibility.Collapsed;
        _hand.Visibility = hand ? Visibility.Visible : Visibility.Collapsed;
        foreach (var m in new FrameworkElement[] { _smile, _flat, _oMouth, _grin })
        {
            m.Visibility = m == mouth ? Visibility.Visible : Visibility.Collapsed;
            m.Opacity = 1;
        }
        if (mouth is not null)
        {
            double mw = mouth == _smile ? 9 : mouth == _flat ? 6 : mouth == _grin ? 11 : 6;
            Canvas.SetLeft(mouth, Mid - mw / 2);
            Canvas.SetTop(mouth, EyeY + h / 2 + 3);
        }
        _blushL.Opacity = _blushR.Opacity = 0.4;
        _hand.Opacity = 1;
    }

    /// <summary>The cat's ears, whiskers and nose for the mood, against a resting eye's place.</summary>
    void PoseCat(bool nose, bool whiskers)
    {
        bool cat = CatOverride ?? CatParts.On;
        _cat.Visibility = cat ? Visibility.Visible : Visibility.Collapsed;
        if (!cat) return;
        var solid = PetLook.Solid(ColorOverride);
        _earL.Fill = solid;
        _earR.Fill = solid;
        _whiskL.Stroke = solid;
        _whiskR.Stroke = solid;
        var (w, h, _) = PetLooks.Eye(StyleOverride ?? PetLook.Style);
        double top = EyeY - h / 2, leftEye = Mid - 4 - w, rightEye = Mid + 4;
        Canvas.SetLeft(_earL, leftEye + w / 2 - 5.5);
        Canvas.SetLeft(_earR, rightEye + w / 2 - 5.5);
        Canvas.SetTop(_earL, top);
        Canvas.SetTop(_earR, top);
        Canvas.SetLeft(_whiskL, leftEye);
        Canvas.SetLeft(_whiskR, rightEye + w);
        Canvas.SetTop(_whiskL, EyeY);
        Canvas.SetTop(_whiskR, EyeY);
        Canvas.SetLeft(_nose, Mid);
        Canvas.SetTop(_nose, EyeY + CatParts.NoseDrop);
        var pose = _mood switch
        {
            FaceMood.Sleep => CatLooks.Pose(PetMood.Sleepy),
            FaceMood.Happy or FaceMood.Wave or FaceMood.Celebrate or FaceMood.Stretch or FaceMood.Wake => CatLooks.Pose(PetMood.Happy),
            FaceMood.Ring or FaceMood.Peek => CatLooks.Pose(PetMood.Notify),
            FaceMood.Listen => CatLooks.Pose(PetMood.Listening),
            _ => CatLooks.Pose(PetMood.Idle),
        };
        _earL.Hold(pose.EarTilt, pose.EarHeight, pose.EarDrop);
        _earR.Hold(pose.EarTilt + pose.EarTurn, pose.EarHeight, pose.EarDrop);
        _whiskL.Hold(pose.WhiskerLift);
        _whiskR.Hold(pose.WhiskerLift);
        _whiskL.Visibility = _whiskR.Visibility = whiskers ? Visibility.Visible : Visibility.Collapsed;
        _nose.Visibility = nose ? Visibility.Visible : Visibility.Collapsed;
    }

    void AddBars()
    {
        for (int i = 0; i < 3; i++)
        {
            var bar = new Border { Width = 1.5, Height = 10, CornerRadius = new CornerRadius(1), Background = ScreenKit.Fg, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 0.6) };
            Canvas.SetLeft(bar, 45 + i * 3);
            Canvas.SetTop(bar, 17.5);
            AddProp(bar);
        }
    }

    void AddDots()
    {
        for (int i = 0; i < 3; i++)
        {
            var dot = new Ellipse { Width = 2.5, Height = 2.5, Fill = ScreenKit.Fg, Opacity = 0.6, RenderTransform = new TranslateTransform() };
            Canvas.SetLeft(dot, 45 + i * 4);
            Canvas.SetTop(dot, 11.5);
            AddProp(dot);
        }
    }

    void AddProp(FrameworkElement e)
    {
        _props.Add(e);
        _face.Children.Add(e);
    }

    // ---- Motion ----------------------------------------------------------------------------------

    void Animate()
    {
        switch (_mood)
        {
            case FaceMood.Watch:
                Blink();
                break;
            case FaceMood.Idle:
                Blink();
                Loop(_eyesShift, TranslateTransform.XProperty, 7000, (0, 0), (.30, 0), (.38, 3), (.58, 3), (.66, -3), (.80, -3), (.88, 0), (1, 0));
                break;
            case FaceMood.Wake:
                WakeSequence();
                break;
            case FaceMood.Wave:
                Wave(0);
                break;
            case FaceMood.Listen:
                Loop(_bodyMove, TranslateTransform.YProperty, 1600, (0, -2), (.5, -3), (1, -2));
                Loop(_bodyScale, ScaleTransform.ScaleXProperty, 1600, (0, 1), (.5, 1.06), (1, 1));
                Loop(_bodyScale, ScaleTransform.ScaleYProperty, 1600, (0, 1), (.5, 1.06), (1, 1));
                Blink();
                int b = 0;
                foreach (var bar in _props.OfType<Border>())
                    LoopFrom((ScaleTransform)bar.RenderTransform, ScaleTransform.ScaleYProperty, 500, 150 * b++, new[] { (0.0, .35), (.5, 1), (1, .35) });
                break;
            case FaceMood.Think:
                Blink();
                Loop(_eyesShift, TranslateTransform.XProperty, 2400, (0, -3), (.2, 3), (.38, 3), (.58, -1), (.78, -3), (1, -3));
                Loop(_eyesShift, TranslateTransform.YProperty, 2400, (0, -2), (.2, -3), (.38, -3), (.58, -1), (.78, -3), (1, -2));
                int d = 0;
                foreach (var dot in _props.OfType<Ellipse>())
                {
                    LoopFrom(dot, OpacityProperty, 1000, 200 * d, new[] { (0.0, .35), (.5, 1), (1, .35) });
                    LoopFrom((TranslateTransform)dot.RenderTransform, TranslateTransform.YProperty, 1000, 200 * d++, new[] { (0.0, 0.0), (.5, -2), (1, 0) });
                }
                break;
            case FaceMood.Peek:
                Blink();
                Loop(_eyesShift, TranslateTransform.XProperty, 2600, (0, -2), (.4, -2), (.55, 2), (.8, 2), (1, -2));
                _eyesShift.Y = 2;
                break;
            case FaceMood.Happy:
                Wiggle();
                Hearts();
                break;
            case FaceMood.Celebrate:
                Celebrate();
                break;
            case FaceMood.Ring:
                Ring();
                break;
            case FaceMood.Sleep:
                Loop(_bodyMove, TranslateTransform.YProperty, 3200, (0, 1), (.5, 1), (1, 1));
                Loop(_bodyScale, ScaleTransform.ScaleXProperty, 3200, (0, 1), (.5, 1.08), (1, 1));
                Loop(_bodyScale, ScaleTransform.ScaleYProperty, 3200, (0, 1), (.5, .9), (1, 1));
                Zees();
                break;
            case FaceMood.Stretch:
                StretchSequence();
                break;
        }
    }

    void Blink()
    {
        foreach (var eye in new[] { _left, _right })
        {
            Loop(eye.Lid, ScaleTransform.ScaleYProperty, 5000, (0, 1), (.44, 1), (.47, .1), (.50, 1), (.84, 1), (.87, .1), (.90, 1), (1, 1));
            Loop(eye.Lid, ScaleTransform.ScaleXProperty, 5000, (0, 1), (.44, 1), (.47, 1.2), (.50, 1), (.84, 1), (.87, 1.2), (.90, 1), (1, 1));
        }
    }

    void Wiggle()
    {
        Loop(_bodyTurn, RotateTransform.AngleProperty, 700, (0, -6), (.5, 6), (1, -6));
        Loop(_bodyScale, ScaleTransform.ScaleXProperty, 700, (0, 1.05), (.5, .96), (1, 1.05));
        Loop(_bodyScale, ScaleTransform.ScaleYProperty, 700, (0, .95), (.5, 1.05), (1, .95));
    }

    void Wave(double delayMs) =>
        LoopFrom(_handWave, RotateTransform.AngleProperty, 450, delayMs, new[] { (0.0, -18.0), (.5, 22), (1, -18) });

    void Ring()
    {
        // hop .7 s on the body, rattle .14 s, the bell swinging .28 s with its clapper, the ring lines pulsing.
        Loop(_bodyMove, TranslateTransform.YProperty, 700, (0, 0), (.15, 1), (.40, -5), (.62, 0), (.80, 0), (1, 0));
        Loop(_bodyScale, ScaleTransform.ScaleXProperty, 700, (0, 1), (.15, 1.16), (.40, .9), (.62, 1.1), (.80, .98), (1, 1));
        Loop(_bodyScale, ScaleTransform.ScaleYProperty, 700, (0, 1), (.15, .82), (.40, 1.12), (.62, .9), (.80, 1.03), (1, 1));
        Loop(_bodyTurn, RotateTransform.AngleProperty, 140, (0, -5), (.5, 5), (1, -5));
        Loop(_bellSwing, RotateTransform.AngleProperty, 280, (0, -22), (.5, 22), (1, -22));
        Loop(_clapper, TranslateTransform.XProperty, 280, (0, -1.5), (.5, 1.5), (1, -1.5));
        Loop(_ringLines, OpacityProperty, 560, (0, .2), (.5, 1), (1, .2));
        Loop(_ringLinesScale, ScaleTransform.ScaleXProperty, 560, (0, .85), (.5, 1.1), (1, .85));
        Loop(_ringLinesScale, ScaleTransform.ScaleYProperty, 560, (0, .85), (.5, 1.1), (1, .85));
        Loop(_mouthScale, ScaleTransform.ScaleXProperty, 560, (0, 1.1), (.5, 1.25), (1, 1.1));
        Loop(_mouthScale, ScaleTransform.ScaleYProperty, 560, (0, .6), (.5, 1.25), (1, .6));
    }

    void WakeSequence()
    {
        // 0–0.8 s asleep (lids, a fading z), the eyes pop open, a tall stretch at 1.4 s, then blush, smile, wave.
        _left.Show(EyeKind.Lid, false);
        _right.Show(EyeKind.Lid, false);
        var z = Zee(36 + 9, 5 + 15.5, 9);
        AddProp(z);
        Once(z, OpacityProperty, 900, (0, 1), (.78, 1), (1, 0));
        After(800, () =>
        {
            if (_mood != FaceMood.Wake) return;
            _left.Show(EyeKind.Open, false);
            _right.Show(EyeKind.Open, false);
            Once(_eyesPop, ScaleTransform.ScaleXProperty, 550, (0, .5), (.6, .92), (.8, 1.06), (1, 1));
            Once(_eyesPop, ScaleTransform.ScaleYProperty, 550, (0, .3), (.6, 1.15), (.8, .95), (1, 1));
        });
        After(1400, () =>
        {
            if (_mood != FaceMood.Wake) return;
            Once(_eyesPop, ScaleTransform.ScaleXProperty, 1000, (0, 1), (.3, .8), (.5, .8), (.68, 1.14), (.84, .97), (1, 1));
            Once(_eyesPop, ScaleTransform.ScaleYProperty, 1000, (0, 1), (.3, 1.6), (.5, 1.6), (.68, .86), (.84, 1.04), (1, 1));
        });
        FadeIn(_blushL, 2400, 0.4);
        FadeIn(_blushR, 2400, 0.4);
        FadeIn(_smile, 2400);
        FadeIn(_hand, 2400);
        Wave(2400);
        Blink();
    }

    void StretchSequence()
    {
        // Open eyes through a big stretch (2.4 s), then ^ ^ eyes with blush and smile, and a wave from 2.2 s.
        _left.Show(EyeKind.Open, false);
        _right.Show(EyeKind.Open, false);
        Once(_bodyScale, ScaleTransform.ScaleXProperty, 2400, (0, 1), (.14, 1.18), (.40, .78), (.58, .78), (.72, 1.16), (.84, .96), (.92, 1.02), (1, 1));
        Once(_bodyScale, ScaleTransform.ScaleYProperty, 2400, (0, 1), (.14, .8), (.40, 1.55), (.58, 1.55), (.72, .86), (.84, 1.05), (.92, .98), (1, 1));
        Once(_bodyMove, TranslateTransform.YProperty, 2400, (0, 0), (.14, 1), (.40, -3), (.58, -3), (.72, 0), (1, 0));
        After(2000, () =>
        {
            if (_mood != FaceMood.Stretch) return;
            _left.Show(EyeKind.Arc, false);
            _right.Show(EyeKind.Arc, false);
        });
        FadeIn(_blushL, 2000, 0.4);
        FadeIn(_blushR, 2000, 0.4);
        FadeIn(_smile, 1900);
        FadeIn(_hand, 2200);
        Wave(2200);
    }

    void Celebrate()
    {
        var jumpY = new[] { (0.0, 0.0), (.18, 2), (.40, -8), (.60, 0), (.75, -1), (.88, 0), (1, 0) };
        OnceFrom(_bodyMove, TranslateTransform.YProperty, 1500, jumpY, 0, 2);
        OnceFrom(_bodyScale, ScaleTransform.ScaleXProperty, 1500, new[] { (0.0, 1.0), (.18, 1.22), (.40, .86), (.60, 1.18), (.75, .95), (.88, 1), (1, 1) }, 0, 2);
        OnceFrom(_bodyScale, ScaleTransform.ScaleYProperty, 1500, new[] { (0.0, 1.0), (.18, .76), (.40, 1.2), (.60, .84), (.75, 1.06), (.88, 1), (1, 1) }, 0, 2);
        string[] colours = { "#F5F5F7", "#A3A3AB", "#FFB585", "#F5F5F7", "#D4D4DA", "#FFB585", "#F5F5F7", "#A3A3AB" };
        double[] angles = { 70, 100, 125, 150, 210, 235, 260, 290 };
        for (int i = 0; i < 8; i++)
        {
            bool round = i % 3 == 0;
            var bit = new Border
            {
                Width = round ? 5 : 4,
                Height = 5,
                CornerRadius = new CornerRadius(round ? 2.5 : 1),
                Background = ScreenKit.B(colours[i]),
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            var fly = new TranslateTransform();
            var spin = new RotateTransform();
            var size = new ScaleTransform(.3, .3);
            // Each piece flies "up" in its own frame, turned to its angle around the face's middle.
            var holder = new Canvas { Width = 0, Height = 0, RenderTransform = new RotateTransform(angles[i]) };
            bit.RenderTransform = new TransformGroup { Children = { size, spin, fly } };
            Canvas.SetLeft(bit, -2);
            Canvas.SetTop(bit, -3);
            holder.Children.Add(bit);
            Canvas.SetLeft(holder, Mid);
            Canvas.SetTop(holder, 21.5);
            AddProp(holder);
            double delay = 250 + i * 30;
            OnceFrom(bit, OpacityProperty, 1500, new[] { (0.0, 0.0), (.12, 1), (.65, 1), (1, 0) }, delay, 2);
            OnceFrom(fly, TranslateTransform.YProperty, 1500, new[] { (0.0, 0.0), (.65, -20), (1, -24) }, delay, 2);
            OnceFrom(spin, RotateTransform.AngleProperty, 1500, new[] { (0.0, 0.0), (.65, 220), (1, 320) }, delay, 2);
            OnceFrom(size, ScaleTransform.ScaleXProperty, 1500, new[] { (0.0, .3), (.65, 1), (1, .8) }, delay, 2);
            OnceFrom(size, ScaleTransform.ScaleYProperty, 1500, new[] { (0.0, .3), (.65, 1), (1, .8) }, delay, 2);
        }
    }

    void Hearts()
    {
        foreach (var (x, y, w, delay) in new[] { (-4.0, 14.5, 10.0, 0.0), (43.0, 16.5, 8.0, 700.0) })
        {
            var heart = new Path
            {
                Data = Geometry.Parse("M5 8.4 1.3 4.9A2.3 2.3 0 0 1 5 2a2.3 2.3 0 0 1 3.7 2.9Z"),
                Fill = ScreenKit.Blush,
                Width = w,
                Height = w * 0.9,
                Stretch = Stretch.Uniform,
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            var move = new TranslateTransform();
            var grow = new ScaleTransform(.5, .5);
            heart.RenderTransform = new TransformGroup { Children = { grow, move } };
            Canvas.SetLeft(heart, x);
            Canvas.SetTop(heart, y);
            AddProp(heart);
            LoopFrom(heart, OpacityProperty, 1400, delay, new[] { (0.0, 0.0), (.3, 1), (1, 0) });
            LoopFrom(move, TranslateTransform.XProperty, 1400, delay, new[] { (0.0, 0.0), (.3, 0), (1, 4) });
            LoopFrom(move, TranslateTransform.YProperty, 1400, delay, new[] { (0.0, 4.0), (.3, -1), (1, -9) });
            LoopFrom(grow, ScaleTransform.ScaleXProperty, 1400, delay, new[] { (0.0, .5), (.3, 1.15), (1, .9) });
            LoopFrom(grow, ScaleTransform.ScaleYProperty, 1400, delay, new[] { (0.0, .5), (.3, 1.15), (1, .9) });
        }
    }

    void Zees()
    {
        foreach (var (x, y, size, delay) in new[] { (43.0, 9.5, 10.0, 0.0), (49.0, 6.5, 8.0, 1300.0) })
        {
            var z = Zee(x, y, size);
            z.Opacity = 0;
            var move = new TranslateTransform();
            var grow = new ScaleTransform(.6, .6);
            z.RenderTransform = new TransformGroup { Children = { grow, move } };
            AddProp(z);
            LoopFrom(z, OpacityProperty, 2600, delay, new[] { (0.0, 0.0), (.3, 1), (1, 0) });
            LoopFrom(move, TranslateTransform.XProperty, 2600, delay, new[] { (0.0, 0.0), (1, 10) });
            LoopFrom(move, TranslateTransform.YProperty, 2600, delay, new[] { (0.0, 0.0), (1, -10) });
            LoopFrom(grow, ScaleTransform.ScaleXProperty, 2600, delay, new[] { (0.0, .6), (1, 1.1) });
            LoopFrom(grow, ScaleTransform.ScaleYProperty, 2600, delay, new[] { (0.0, .6), (1, 1.1) });
        }
    }

    static TextBlock Zee(double x, double y, double size)
    {
        var z = ScreenKit.Text("z", size, Weight.Bold, ScreenKit.B("#D4D4DA"));
        z.RenderTransformOrigin = new Point(0.5, 0.5);
        Canvas.SetLeft(z, x);
        Canvas.SetTop(z, y - size);
        return z;
    }

    void FadeIn(UIElement e, double delayMs, double to = 1) =>
        Once(e, OpacityProperty, delayMs + 300, (0, 0), (delayMs / (delayMs + 300), 0), (1, to));

    // ---- Animation plumbing ----------------------------------------------------------------------

    void Loop(IAnimatable target, DependencyProperty property, double periodMs, params (double At, double Value)[] frames) =>
        LoopFrom(target, property, periodMs, 0, frames);

    void LoopFrom(IAnimatable target, DependencyProperty property, double periodMs, double delayMs, (double At, double Value)[] frames)
    {
        var a = Frames(periodMs, frames);
        a.RepeatBehavior = RepeatBehavior.Forever;
        a.BeginTime = TimeSpan.FromMilliseconds(delayMs);
        Run(target, property, a);
    }

    void Once(IAnimatable target, DependencyProperty property, double ms, params (double At, double Value)[] frames) =>
        OnceFrom(target, property, ms, frames, 0, 1);

    /// <summary>Plays <paramref name="repeat"/> times and holds the last frame.</summary>
    void OnceFrom(IAnimatable target, DependencyProperty property, double ms, (double At, double Value)[] frames, double delayMs, int repeat)
    {
        var a = Frames(ms, frames);
        a.RepeatBehavior = new RepeatBehavior(repeat);
        a.BeginTime = TimeSpan.FromMilliseconds(delayMs);
        a.FillBehavior = FillBehavior.HoldEnd;
        Run(target, property, a);
    }

    static DoubleAnimationUsingKeyFrames Frames(double ms, (double At, double Value)[] frames)
    {
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(ms) };
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        foreach (var (at, value) in frames) a.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromPercent(Math.Clamp(at, 0, 1)), ease));
        return a;
    }

    void Run(IAnimatable target, DependencyProperty property, AnimationTimeline a)
    {
        target.BeginAnimation(property, a);
        _running.Add((target, property));
    }

    readonly List<System.Windows.Threading.DispatcherTimer> _timers = new();

    /// <summary>A step of a one-off sequence; skipped if the face was hidden (or changed mood) meanwhile.</summary>
    void After(double ms, Action action)
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _timers.Remove(t);
            if (IsVisible) action();
        };
        _timers.Add(t);
        t.Start();
    }

    void StopAll()
    {
        foreach (var t in _timers) t.Stop();
        _timers.Clear();
        foreach (var (target, property) in _running) target.BeginAnimation(property, null);
        _running.Clear();
        _eyesShift.X = _eyesShift.Y = 0;
        _bodyMove.Y = 0;
        _bodyTurn.Angle = 0;
        _bodyScale.ScaleX = _bodyScale.ScaleY = 1;
        _eyesPop.ScaleX = _eyesPop.ScaleY = 1;
        _handWave.Angle = 0;
    }

    static Ellipse Blush() => new() { Width = 8, Height = 4, Fill = ScreenKit.Blush, Opacity = 0.4 };

    static Path Stroke(string data, double thickness) => new()
    {
        Data = Geometry.Parse(data),
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
    };

    enum EyeKind { Open, Arc, Lid }

    /// <summary>One eye: the gradient pill with its shine, or a ^ arc, or a closed lid; blinks around its centre.</summary>
    sealed class EyePart : Grid
    {
        readonly Rectangle _pill = new();
        readonly Rectangle _shine = new() { Width = 3, Height = 3, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 2, 0, 0) };
        readonly Path _arc = Stroke("", 2.5), _lid = Stroke("", 2.5);
        // A zero-size canvas in the middle of the eye: the pupil is drawn around its origin.
        readonly Canvas _pupilAt = new() { Width = 0, Height = 0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        PetEyeStyle _style;
        bool _tall;

        public EyePart()
        {
            Lid = new ScaleTransform(1, 1);
            RenderTransformOrigin = new Point(0.5, 0.5);
            RenderTransform = Lid;
            Children.Add(_pill);
            Children.Add(_pupilAt);
            Children.Add(_shine);
            Children.Add(_arc);
            Children.Add(_lid);
            _arc.StrokeStartLineCap = _arc.StrokeEndLineCap = PenLineCap.Flat;
            _lid.StrokeStartLineCap = _lid.StrokeEndLineCap = PenLineCap.Flat;
        }

        public ScaleTransform Lid { get; }

        /// <summary>The cat's pupil: half its width (a slit by default). Shown only in an open eye.</summary>
        public double Open { get; set; } = CatLooks.Slit;

        public (double W, double H) Size => (Width, Height);

        public void Paint(PetEyeStyle style, Brush fill, Brush solid)
        {
            _style = style;
            _pill.Fill = fill;
            _arc.Stroke = _lid.Stroke = solid;
            bool pixel = style == PetEyeStyle.Pixel;
            _shine.RadiusX = _shine.RadiusY = pixel ? 0 : 1.5;
            // Pixel eyes get square arcs and lids too.
            _arc.Data = Geometry.Parse(pixel ? "M1.25,7 L1.25,1.25 L10.75,1.25 L10.75,7" : "M1.25,7 L1.25,5.5 Q1.25,1.25 6,1.25 Q10.75,1.25 10.75,5.5 L10.75,7");
            _lid.Data = Geometry.Parse(pixel ? "M1.25,0 L1.25,4.75 L10.75,4.75 L10.75,0" : "M1.25,0 L1.25,1 Q1.25,4.75 6,4.75 Q10.75,4.75 10.75,1 L10.75,0");
            Show(EyeKind.Open, _tall);
        }

        public void Show(EyeKind kind, bool tall)
        {
            _tall = tall;
            var (w, h, r) = PetLooks.Eye(_style);
            if (tall) h += 2;
            bool open = kind == EyeKind.Open;
            _pill.Visibility = _shine.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            _arc.Visibility = kind == EyeKind.Arc ? Visibility.Visible : Visibility.Collapsed;
            _lid.Visibility = kind == EyeKind.Lid ? Visibility.Visible : Visibility.Collapsed;
            _pupilAt.Children.Clear();
            if (open && CatParts.Pupil(w, h, Open) is { } pupil)
            {
                Canvas.SetTop(pupil, CatParts.PupilLow);
                _pupilAt.Children.Add(pupil);
            }
            if (open)
            {
                Width = w;
                Height = h;
                _pill.RadiusX = Math.Min(r, w / 2);
                _pill.RadiusY = _style == PetEyeStyle.Oval ? h / 2 : Math.Min(r, h / 2);
            }
            else
            {
                Width = 12;
                Height = kind == EyeKind.Arc ? 7 : 6;
            }
        }
    }
}
