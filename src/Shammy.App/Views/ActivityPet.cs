using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using Shammy.Core.Notch;

namespace Shammy.Views;

/// <summary>
/// The pet in costume, centred in the compact notch (the Activities board): a headset and moving mouth on a
/// call, happy ^ ^ eyes and notes to music, eyes circling a tiny clock for a timer, a ninja mask while the screen
/// is shared, a progress bar it holds for a download, and so on. Drawn in a 72 × 38 column; accessories may
/// spill out of it. Every animation is a WPF clock that is stopped as soon as the pet leaves the screen or
/// changes face, and with Windows animations turned off it holds a still pose.
/// The notch is a layered, software-rendered window where each repaint costs real CPU, and an isolated repaint
/// costs several times one inside a running animation. So nothing loops: everything hangs off the once-a-second
/// refresh that already repaints the value (<see cref="Second"/>). A blink closes the eyes on that frame, the
/// call's lean and the timer's hand move on it, and each costume's ambient motion (a sway, a nod) plays as a short
/// low-frame-rate burst starting on it every few seconds, then rests with no clock running. The call mouth
/// redraws only when the mic level changes noticeably.
/// </summary>
internal sealed class ActivityPet : Canvas
{
    public const double ColumnWidth = 72, ColumnHeight = 38;
    const double Cx = ColumnWidth / 2;

    static readonly Color Ink = Color.FromRgb(0xF5, 0xF5, 0xF7), Ink2 = Color.FromRgb(0xA3, 0xA3, 0xAB),
        Pink = Color.FromRgb(0xFF, 0x6B, 0x8A), MouthInside = Color.FromRgb(0x3A, 0x1F, 0x28), Bolt = Color.FromRgb(0xFF, 0xC2, 0x33),
        Mask = Color.FromRgb(0x4A, 0x4A, 0x57), MaskDark = Color.FromRgb(0x2C, 0x2C, 0x35);

    /// <summary>Ambient motion's frame rate: smooth enough for a 1-3 px sway, and cheap on a layered window.</summary>
    const int AmbientFps = 10;

    /// <summary>Talk sampling: quick while speaking, slow while quiet (a silent mouth never redraws).</summary>
    static readonly TimeSpan TalkFast = TimeSpan.FromMilliseconds(110), TalkSlow = TimeSpan.FromMilliseconds(300);

    readonly List<(IAnimatable Target, DependencyProperty Property)> _running = new();
    readonly List<(IAnimatable Target, DependencyProperty Property, AnimationTimeline Animation)> _cycle = new();
    readonly List<ScaleTransform> _blinks = new();
    readonly List<DispatcherTimer> _timers = new();
    readonly Random _random = new();
    readonly DispatcherTimer _levelTimer;
    double _rest = 6;
    int _ticks, _nextBlink = 3, _cycleEvery, _cycleAt;
    long _lastSecond;
    TranslateTransform? _lean;
    RotateTransform? _leanTilt, _hand;
    int _mouthStep = -1;
    bool _mouthTalking;
    readonly TalkMeter _talk = new();
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    PetFace? _face;
    ImageSource? _cover;
    bool _paused;
    Color _art = Ink;
    double _progress;
    Func<double>? _level;

    // Parts that change in place.
    Border? _barFill;
    FrameworkElement? _talkMouth, _smileMouth;
    ScaleTransform? _talkScale;

    public ActivityPet()
    {
        Width = ColumnWidth;
        Height = ColumnHeight;
        ClipToBounds = false;
        IsHitTestVisible = false;
        _levelTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TalkSlow };
        _levelTimer.Tick += (_, _) => SampleLevel();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Rebuild();
            else Stop();
        };
    }

    public PetFace? Face => _face;

    /// <summary>
    /// The costume's ambient burst started (its length in seconds): the eq bars beside it move only meanwhile,
    /// so music repaints in short bursts rather than all the time.
    /// </summary>
    public event Action<double>? Burst;

    /// <summary>How open the mouth is on a call (0..1), for the test pipe.</summary>
    public double MouthOpen => _talk.Open;

    static bool Reduced => !Infrastructure.MotionPrefs.Lively;

    /// <summary>Wear this face; rebuilt (and its little entrance replayed) only when the face or pause changes.</summary>
    public void Show(PetFace face, bool paused, Color? art, double? progress, Func<double>? level, ImageSource? cover = null)
    {
        _level = level;
        if (!ReferenceEquals(cover, _cover))
        {
            // A new song's cover: the record is redrawn (same face, so nothing else notices).
            _cover = cover;
            if (face is PetFace.Disc or PetFace.DiscPaused && face == _face) _face = null;
        }
        _progress = Math.Clamp(progress ?? 0, 0, 1);
        if (_barFill is not null) _barFill.Width = 24 * _progress;
        var newArt = art ?? Ink;
        if (face == _face && paused == _paused && newArt == _art)
        {
            Second();
            return;
        }
        _face = face;
        _paused = paused;
        _art = newArt;
        if (IsVisible) Rebuild();
    }

    /// <summary>Clear the face (the next Show replays its entrance).</summary>
    public void Reset()
    {
        _face = null;
        Stop();
    }

    void Stop()
    {
        _levelTimer.Stop();
        foreach (var t in _timers) t.Stop();
        _timers.Clear();
        foreach (var (target, property) in _running) target.BeginAnimation(property, null);
        _running.Clear();
        _cycle.Clear();
        _blinks.Clear();
        _rest = 6;
        _cycleEvery = 0;
        _lean = null;
        _leanTilt = _hand = null;
        _mouthStep = -1;
        _mouthTalking = false;
        Children.Clear();
        _barFill = null;
        _talkMouth = _smileMouth = null;
        _talkScale = null;
        _talk.Reset();
    }

    void Rebuild()
    {
        Stop();
        if (_face is not { } face) return;
        switch (face)
        {
            case PetFace.Call:
            case PetFace.CallMuted:
                BuildCall(face == PetFace.CallMuted);
                break;
            case PetFace.MusicVibe: BuildMusic(); break;
            case PetFace.MusicPaused: BuildMusicPaused(); break;
            case PetFace.TimerWatch: BuildTimer(); break;
            case PetFace.TimerDone: BuildTimerDone(); break;
            case PetFace.Ninja: BuildNinja(); break;
            case PetFace.DownloadHold: BuildDownload(); break;
            case PetFace.DownloadNom: BuildNom(); break;
            case PetFace.ChargeSip: BuildCharge(); break;
            case PetFace.Earbuds: BuildEarbuds(); break;
            case PetFace.Disc: BuildDisc(spinning: true); break;
            case PetFace.DiscPaused: BuildDisc(spinning: false); break;
            case PetFace.Focused: BuildFocused(); break;
            case PetFace.Happy: BuildHappy(); break;
            case PetFace.Resting: BuildResting(); break;
            case PetFace.Talking: BuildTalking(); break;
            default: BuildNotify(); break;
        }
        StartCycle();
    }

    /// <summary>
    /// Plays the costume's ambient motion as a burst after its entrance, then again every few seconds from
    /// <see cref="Second"/> (<see cref="_rest"/> seconds of stillness between). Between bursts no clock runs.
    /// </summary>
    void StartCycle()
    {
        if (_cycle.Count == 0 || Reduced) return;
        double length = CycleLength;
        _cycleEvery = (int)Math.Ceiling(length + _rest);
        _cycleAt = _ticks + _cycleEvery;
        After(0.6, PlayCycle);
    }

    double CycleLength => _cycle.Count == 0 ? 0 : _cycle.Max(c => (c.Animation.BeginTime ?? TimeSpan.Zero).TotalSeconds + c.Animation.Duration.TimeSpan.TotalSeconds
        * (c.Animation.RepeatBehavior.HasCount ? c.Animation.RepeatBehavior.Count : 1));

    void PlayCycle()
    {
        if (!IsVisible) return;
        foreach (var (target, property, animation) in _cycle) target.BeginAnimation(property, animation);
        Burst?.Invoke(CycleLength);
    }

    /// <summary>
    /// Once a second, from the window's refresh (which repaints the value anyway): blink now and then, step the
    /// lean or the clock hand, and start the next ambient burst, all on that same repaint.
    /// </summary>
    void Second()
    {
        long now = _clock.ElapsedMilliseconds;
        if (now - _lastSecond < 900 || !IsVisible || Reduced) return;
        _lastSecond = now;
        _ticks++;
        if (_blinks.Count > 0 && _ticks >= _nextBlink)
        {
            _nextBlink = _ticks + 4 + _random.Next(3);
            foreach (var b in _blinks)
            {
                b.ScaleY = 0.1;
                b.ScaleX = 1.2;
            }
            After(0.12, () =>
            {
                foreach (var b in _blinks)
                {
                    b.ScaleY = 1;
                    b.ScaleX = 1;
                }
            });
        }
        if (_lean is not null && _leanTilt is not null && _ticks % 4 == 0)
        {
            bool leanIn = _lean.X == 0;
            _lean.X = leanIn ? 1 : 0;
            _leanTilt.Angle = leanIn ? 2 : 0;
        }
        if (_hand is not null && !_paused) _hand.Angle = (_hand.Angle + 30) % 360;
        if (_cycleEvery > 0 && _ticks >= _cycleAt)
        {
            _cycleAt = _ticks + _cycleEvery;
            PlayCycle();
        }
    }

    DispatcherTimer Every(double seconds, Action run)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => run();
        timer.Start();
        _timers.Add(timer);
        return timer;
    }

    void After(double seconds, Action run)
    {
        DispatcherTimer? timer = null;
        timer = Every(seconds, () =>
        {
            timer!.Stop();
            _timers.Remove(timer);
            run();
        });
    }

    // ---- Faces ------------------------------------------------------------------------------------

    /// <summary>
    /// Music: no eyes, just the song's cover as a 30 px record. It turns while the song plays (a slow, low
    /// frame-rate spin: this is the one continuous motion in the notch, and Calm motion stops it) and sits still
    /// and dimmed when paused. A song with no cover gets a plain dark record.
    /// </summary>
    void BuildDisc(bool spinning)
    {
        const double size = 30;
        var layer = Layer(out _);
        var spin = new RotateTransform();
        var disc = new Grid { Width = size, Height = size, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = spin };
        Brush face = _cover is { } cover
            ? new ImageBrush(cover) { Stretch = Stretch.UniformToFill }
            : new RadialGradientBrush(new GradientStopCollection { new(Color.FromRgb(0x3A, 0x3A, 0x44), 0), new(Color.FromRgb(0x3A, 0x3A, 0x44), 0.3), new(Color.FromRgb(0x17, 0x17, 0x1B), 0.32) });
        disc.Children.Add(new Ellipse { Fill = face });
        // The rim and a groove, then the spindle hole.
        disc.Children.Add(new Ellipse { Stroke = Solid(Color.FromRgb(0, 0, 0)), StrokeThickness = 1.5 });
        disc.Children.Add(new Ellipse { Margin = new Thickness(1.5), Stroke = Solid(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1 });
        disc.Children.Add(new Ellipse { Margin = new Thickness(4.5), Stroke = Solid(Color.FromArgb(0x30, 0, 0, 0)), StrokeThickness = 1.5 });
        disc.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Solid(Color.FromRgb(0, 0, 0)), Stroke = Solid(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1.2 });
        if (!spinning) disc.Opacity = 0.55;
        layer.Children.Add(At(disc, Cx - size / 2, (ColumnHeight - size) / 2));
        Pop(disc, 0.4, 0);
        // Pop set a scale on the record; keep it and add the spin.
        if (disc.RenderTransform is ScaleTransform pop) disc.RenderTransform = new TransformGroup { Children = { pop, spin } };
        if (!spinning || Reduced) return;
        var turn = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(6)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(turn, DiscFps);
        Run(spin, RotateTransform.AngleProperty, turn);
    }

    /// <summary>The record's spin: enough steps to read as turning, few enough to stay cheap on a layered window.</summary>
    const int DiscFps = 12;

    /// <summary>Working on something: narrowed eyes that look left and right, like reading.</summary>
    void BuildFocused()
    {
        var face = Layer(out _);
        var eyes = AddEyes(face, 13, 9);
        _rest = 2;
        Loop(eyes.Translate, TranslateTransform.XProperty, 2.4, 0, (0, 0), (0.25, -3.5), (0.75, 3.5), (1, 0));
    }

    /// <summary>Pleased: ^ ^ eyes, a blush and a smile, with one small hop.</summary>
    void BuildHappy()
    {
        var face = Layer(out var faceT);
        double row = 11.5;
        AddArcs(face, row);
        AddBlush(face, row + 7, 32);
        face.Children.Add(At(Smile(), Cx - 4.5, row + 10));
        Once(faceT.Translate, TranslateTransform.YProperty, 0.5, 0.1, (0, 0), (0.45, -3), (1, 0));
    }

    /// <summary>Eyes closed, breathing slowly (an eye break, taken together).</summary>
    void BuildResting()
    {
        var face = Layer(out var faceT);
        foreach (double x in new[] { Cx - 15, Cx + 4 })
            face.Children.Add(At(new Rectangle { Width = 11, Height = 2.5, RadiusX = 1.25, RadiusY = 1.25, Fill = Solid(Ink) }, x, 17));
        _rest = 1;
        Loop(faceT.Scale, ScaleTransform.ScaleYProperty, 3.2, 0, (0, 1), (0.5, 1.06), (1, 1));
        Loop(faceT.Translate, TranslateTransform.YProperty, 3.2, 0, (0, 0), (0.5, -1), (1, 0));
    }

    /// <summary>Saying something aloud: eyes, and a mouth that opens and closes on its own.</summary>
    void BuildTalking()
    {
        var face = Layer(out _);
        AddEyes(face, 8, 13);
        var o = OMouth();
        var talk = new ScaleTransform(1, 0.5);
        o.RenderTransformOrigin = new Point(0.5, 0.5);
        o.RenderTransform = talk;
        face.Children.Add(At(o, Cx - 3, 24));
        _rest = 1;
        var open = Keys(0.36, 0, (0, 0.4), (0.5, 1.1), (1, 0.4));
        open.RepeatBehavior = new RepeatBehavior(6);
        LoopKeys(talk, ScaleTransform.ScaleYProperty, open);
    }

    void BuildCall(bool muted)
    {
        // Eyes (13) + gap 3 + mouth (6), centred.
        var face = Layer(out var faceT);
        double row = 8, mouth = 24;
        AddEyes(face, row, 13);
        if (muted)
        {
            var zip = PathOf("M 1,3 H 11.5 M 3.5,1.2 V 4.8 M 6,1.2 V 4.8 M 8.5,1.2 V 4.8", Ink, 1.2);
            var zipCanvas = new Canvas { Width = 15, Height = 6 };
            zipCanvas.Children.Add(zip);
            zipCanvas.Children.Add(At(new Ellipse { Width = 3.2, Height = 3.2, Fill = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0D)), Stroke = Solid(Ink), StrokeThickness = 1.2 }, 11.2, 1.4));
            Pop(zipCanvas, 0.4, 0);
            face.Children.Add(At(zipCanvas, Cx - 7.5, mouth));
        }
        else
        {
            _smileMouth = At(Smile(), Cx - 4.5, mouth);
            var o = OMouth();
            _talkScale = new ScaleTransform(1.15, 0.35);
            o.RenderTransformOrigin = new Point(0.5, 0.5);
            o.RenderTransform = _talkScale;
            o.Opacity = 0;
            _talkMouth = At(o, Cx - 3, mouth);
            face.Children.Add(_smileMouth);
            face.Children.Add(_talkMouth);
            if (_level is not null && !Reduced) _levelTimer.Start();
        }
        // The headset drops on.
        var headset = new Canvas { Width = 46, Height = 28 };
        headset.Children.Add(PathOf("M 5,14 V 12 A 18,10 0 0 1 41,12 V 14", Ink, 2));
        headset.Children.Add(At(new Rectangle { Width = 6, Height = 10, RadiusX = 2.5, RadiusY = 2.5, Fill = Solid(Ink) }, 1, 10));
        headset.Children.Add(At(new Rectangle { Width = 6, Height = 10, RadiusX = 2.5, RadiusY = 2.5, Fill = Solid(Ink) }, 39, 10));
        headset.Children.Add(PathOf("M 4,20 Q 5,26 14,26", Ink2, 1.4));
        headset.Children.Add(At(new Ellipse { Width = 3.4, Height = 3.4, Fill = Solid(Ink) }, 13.3, 24.3));
        DropOn(headset, 0.5, 10);
        face.Children.Add(At(headset, Cx - 23, row - 9));
        // Leans in a little now and then, like listening.
        _lean = faceT.Translate;
        _leanTilt = faceT.Rotate;
    }

    void BuildMusic()
    {
        var face = Layer(out var faceT);
        double row = 11.5, mouth = 21.5;
        AddArcs(face, row);
        AddBlush(face, row + 7, 32);
        face.Children.Add(At(Smile(), Cx - 4.5, mouth));
        // Notes in the album's colours float off.
        var c1 = Mix(_art, Colors.White, 0.4);
        var c2 = Mix(_art, Color.FromRgb(0xD9, 0x9A, 0xD0), 0.55);
        face.Children.Add(At(Note(c1, 1), Cx - 16 + 34, row - 2));
        face.Children.Add(At(Note(c2, 0.78), Cx - 16 - 12, row + 4));
        NoteUp((FrameworkElement)face.Children[^2], 0);
        NoteUp((FrameworkElement)face.Children[^1], 0.9);
        // Sways to the beat, then a breather while the eq bars keep the rhythm.
        _rest = 5;
        Loop(faceT.Translate, TranslateTransform.YProperty, 0.9, 0, (0, 0), (0.5, -3), (1, 0));
        Loop(faceT.Rotate, RotateTransform.AngleProperty, 0.9, 0, (0, -7), (0.5, 7), (1, -7));
        Loop(faceT.Scale, ScaleTransform.ScaleXProperty, 0.9, 0, (0, 1.04), (0.5, 0.97), (1, 1.04));
        Loop(faceT.Scale, ScaleTransform.ScaleYProperty, 0.9, 0, (0, 0.96), (0.5, 1.04), (1, 0.96));
    }

    void BuildMusicPaused()
    {
        var face = Layer(out _);
        var eyes = AddEyes(face, 10, 13);
        eyes.Translate.X = 3; // peeking at the play button
        face.Children.Add(At(Flat(), Cx - 3, 26));
    }

    void BuildTimer()
    {
        var face = Layer(out _);
        const double shift = -4; // the face moves aside for the clock
        var eyes = AddEyes(face, 10, 13, shift);
        face.Children.Add(At(Flat(), Cx - 3 + shift, 26));
        var clock = new Canvas { Width = 13, Height = 14 };
        clock.Children.Add(At(new Ellipse { Width = 10, Height = 10, Stroke = Solid(Ink), StrokeThickness = 1.4 }, 1.5, 3));
        clock.Children.Add(PathOf("M 5,1.4 H 8 M 10.4,3.4 L 11.3,2.5", Ink, 1.4));
        var hand = PathOf("M 6.5,8 V 4.8", Ink, 1.4);
        var spin = new RotateTransform(0, 6.5, 8);
        hand.RenderTransform = spin;
        clock.Children.Add(hand);
        face.Children.Add(At(clock, Cx - 15 + shift + 37, 11));
        if (_paused) return;
        // Eyes follow the hand round now and then; the hand ticks once a second (with the countdown's own redraw).
        eyes.Translate.X = 2;
        eyes.Translate.Y = -1.5;
        Loop(eyes.Translate, TranslateTransform.XProperty, 3, 0, (0, 2), (0.25, 3), (0.5, 2), (0.75, 1), (1, 2));
        Loop(eyes.Translate, TranslateTransform.YProperty, 3, 0, (0, -1.5), (0.25, 0), (0.5, 1.5), (0.75, 0), (1, -1.5));
        if (Reduced) return;
        _hand = spin;
    }

    void BuildTimerDone()
    {
        var face = Layer(out var faceT, new Point(0.5, 0.75));
        double row = 11, mouth = 21;
        AddArcs(face, row);
        AddBlush(face, row + 7, 32);
        var grin = new Border
        {
            Width = 11, Height = 6, BorderThickness = new Thickness(2), BorderBrush = Solid(Ink), Background = Solid(Pink),
            CornerRadius = new CornerRadius(2, 2, 5, 5),
        };
        face.Children.Add(At(grin, Cx - 5.5, mouth));
        // Jumps for joy.
        // An alert you asked for, on screen for seconds: it keeps jumping with only a short pause.
        _rest = 0.4;
        (double, double)[] y = { (0, 0), (0.18, 2), (0.40, -8), (0.60, 0), (0.75, -1), (0.88, 0), (1, 0) };
        Loop(faceT.Translate, TranslateTransform.YProperty, 1.1, 0, y);
        Loop(faceT.Scale, ScaleTransform.ScaleXProperty, 1.1, 0, (0, 1), (0.18, 1.22), (0.40, 0.86), (0.60, 1.18), (0.75, 0.95), (0.88, 1), (1, 1));
        Loop(faceT.Scale, ScaleTransform.ScaleYProperty, 1.1, 0, (0, 1), (0.18, 0.76), (0.40, 1.2), (0.60, 0.84), (0.75, 1.06), (0.88, 1), (1, 1));
        // A small burst of confetti beside it.
        if (Reduced) return;
        Color[] colours = { Ink, Ink2, Color.FromRgb(0xFF, 0xB5, 0x85), Ink, Color.FromRgb(0xD4, 0xD4, 0xDA), Color.FromRgb(0xFF, 0xB5, 0x85), Ink, Ink2 };
        int[] angles = { 70, 100, 125, 150, 210, 235, 260, 290 };
        for (int i = 0; i < angles.Length; i++)
        {
            var arm = new Canvas { RenderTransform = new RotateTransform(angles[i]) };
            var bit = new Rectangle
            {
                Width = i % 3 == 0 ? 5 : 4, Height = 5, Fill = Solid(colours[i]),
                RadiusX = i % 3 == 0 ? 2.5 : 1, RadiusY = i % 3 == 0 ? 2.5 : 1,
                RenderTransformOrigin = new Point(0.5, 0.5), Opacity = 0,
            };
            var t = new TransformParts();
            bit.RenderTransform = t.Group;
            arm.Children.Add(At(bit, -2, -3));
            Children.Add(At(arm, Cx - 16 + 36, 19));
            double delay = i * 0.05;
            Loop(bit, OpacityProperty, 1.1, delay, (0, 0), (0.12, 1), (0.65, 1), (1, 0));
            Loop(t.Translate, TranslateTransform.YProperty, 1.1, delay, (0, 0), (0.65, -20), (1, -24));
            Loop(t.Scale, ScaleTransform.ScaleXProperty, 1.1, delay, (0, 0.3), (0.65, 1), (1, 0.8));
            Loop(t.Scale, ScaleTransform.ScaleYProperty, 1.1, delay, (0, 0.3), (0.65, 1), (1, 0.8));
            Loop(t.Rotate, RotateTransform.AngleProperty, 1.1, delay, (0, 0), (0.65, 220), (1, 320));
        }
    }

    void BuildNinja()
    {
        var face = Layer(out _);
        const double top = 11; // a 56 × 16 face
        double left = Cx - 28;
        // Mask ties flap behind.
        var tie1 = new Rectangle { Width = 9, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = Solid(Mask), RenderTransformOrigin = new Point(1, 0.5) };
        var tie2 = new Rectangle { Width = 8, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = Solid(Mask), RenderTransformOrigin = new Point(1, 0.5) };
        var r1 = new RotateTransform();
        var r2 = new RotateTransform();
        tie1.RenderTransform = r1;
        tie2.RenderTransform = r2;
        face.Children.Add(At(tie1, left - 3, top + 3));
        face.Children.Add(At(tie2, left - 1, top + 8));
        Loop(r1, RotateTransform.AngleProperty, 0.5, 0, (0, -12), (0.5, 16), (1, -12));
        Loop(r2, RotateTransform.AngleProperty, 0.5, 0.15, (0, -12), (0.5, 16), (1, -12));
        var band = new Border
        {
            Width = 48, Height = 12, CornerRadius = new CornerRadius(6),
            Background = new LinearGradientBrush(Mask, MaskDark, 90),
        };
        DropOn(band, 0.45, 10);
        face.Children.Add(At(band, left + 4, top + 2));
        // Narrowed eyes dart side to side.
        var eyes = AddEyes(face, top + 4.5, 7);
        _rest = 8;
        Loop(eyes.Translate, TranslateTransform.XProperty, 2, 0, (0, 0), (0.2, -3), (0.45, -3), (0.6, 3), (0.85, 3), (1, 0));
    }

    void BuildDownload()
    {
        var face = Layer(out var faceT);
        double row = 8;
        var eyes = AddEyes(face, row, 13, shineBottom: true);
        eyes.Translate.Y = 1;
        // Two little hands hold the bar.
        double barLeft = Cx - 15, barTop = 25;
        face.Children.Add(At(new Ellipse { Width = 5, Height = 5, Fill = Solid(Ink) }, barLeft - 3, barTop));
        face.Children.Add(At(new Ellipse { Width = 5, Height = 5, Fill = Solid(Ink) }, barLeft + 28, barTop));
        var track = new Border { Width = 24, Height = 3, CornerRadius = new CornerRadius(1.5), Background = new SolidColorBrush(Color.FromArgb(0x38, 0xF5, 0xF5, 0xF7)) };
        _barFill = new Border { Width = 24 * _progress, Height = 3, CornerRadius = new CornerRadius(1.5), Background = Solid(Ink), HorizontalAlignment = HorizontalAlignment.Left };
        track.Child = _barFill;
        face.Children.Add(At(track, barLeft + 3, barTop + 1));
        Loop(faceT.Translate, TranslateTransform.YProperty, 1.6, 0, (0, 0), (0.5, 1), (1, 0));
        Loop(faceT.Rotate, RotateTransform.AngleProperty, 1.6, 0, (0, 0), (0.5, -3), (1, 0));
    }

    void BuildNom()
    {
        var face = Layer(out var faceT);
        double row = 7.5, mouth = 23.5;
        const double T = 3.4;
        // Eyes close happily as it swallows.
        var eyes = AddEyes(face, row, 13, blink: false);
        Once(eyes.Host, OpacityProperty, T, 0, (0, 1), (0.82, 1), (0.86, 0));
        var arcs = AddArcs(face, row + 3);
        arcs.Host.Opacity = 0;
        Once(arcs.Host, OpacityProperty, T, 0, (0, 0), (0.82, 0), (0.88, 1));
        var blush = AddBlush(face, row + 13, 30);
        blush.Opacity = 0;
        Once(blush, OpacityProperty, 0.3, 2.9, (0, 0), (1, 1));
        // The mouth opens, gulps the file and chews.
        var chomp = new Ellipse { Width = 10, Height = 7, Fill = Solid(Pink), Stroke = Solid(Ink), StrokeThickness = 1.5, RenderTransformOrigin = new Point(0.5, 0.5) };
        var chompScale = new ScaleTransform(1, 0.15);
        chomp.RenderTransform = chompScale;
        face.Children.Add(At(chomp, Cx - 5, mouth));
        Once(chompScale, ScaleTransform.ScaleYProperty, T, 0, (0, 0.15), (0.12, 1), (0.36, 1), (0.42, 0.2), (0.50, 0.85), (0.58, 0.2), (0.66, 0.85), (0.74, 0.2), (0.82, 0.15));
        Once(chomp, OpacityProperty, T, 0, (0, 1), (0.82, 1), (0.86, 0));
        var smile = Smile();
        smile.Opacity = 0;
        face.Children.Add(At(smile, Cx - 4.5, mouth));
        Once(smile, OpacityProperty, T, 0, (0, 0), (0.82, 0), (0.88, 1));
        var file = new Canvas { Width = 11, Height = 12, Opacity = 0 };
        file.Children.Add(Fill("M 1.5,1.5 H 6.5 L 9.5,4.5 V 10.5 H 1.5 Z", Ink));
        file.Children.Add(Fill("M 6.5,1.5 V 4.5 H 9.5", Ink2));
        file.Children.Add(PathOf("M 3.5,7 H 7.5 M 3.5,9 H 6.5", Color.FromRgb(0x0B, 0x0B, 0x0D), 1));
        var fileT = new TransformParts();
        file.RenderTransformOrigin = new Point(0.5, 0.5);
        file.RenderTransform = fileT.Group;
        face.Children.Add(At(file, Cx - 15 + 40, row + 13));
        Once(file, OpacityProperty, T, 0, (0, 0), (0.10, 1), (0.38, 1), (0.40, 0));
        Once(fileT.Translate, TranslateTransform.XProperty, T, 0, (0, 26), (0.30, 0), (0.38, -30));
        Once(fileT.Translate, TranslateTransform.YProperty, T, 0, (0, 0), (0.30, 0), (0.38, 1));
        Once(fileT.Rotate, RotateTransform.AngleProperty, T, 0, (0, 10), (0.30, -8), (0.38, 0));
        Once(fileT.Scale, ScaleTransform.ScaleXProperty, T, 0, (0, 1), (0.30, 1), (0.38, 0.25));
        Once(fileT.Scale, ScaleTransform.ScaleYProperty, T, 0, (0, 1), (0.30, 1), (0.38, 0.25));
        // Three chews.
        if (Reduced) return;
        var chew = Keys(0.45, 1.4, (0, 0), (0.5, 1), (1, 0));
        chew.RepeatBehavior = new RepeatBehavior(3);
        Run(faceT.Translate, TranslateTransform.YProperty, chew);
        var chewX = Keys(0.45, 1.4, (0, 1), (0.5, 1.06), (1, 1));
        chewX.RepeatBehavior = new RepeatBehavior(3);
        Run(faceT.Scale, ScaleTransform.ScaleXProperty, chewX);
        var chewY = Keys(0.45, 1.4, (0, 1), (0.5, 0.93), (1, 1));
        chewY.RepeatBehavior = new RepeatBehavior(3);
        Run(faceT.Scale, ScaleTransform.ScaleYProperty, chewY);
    }

    void BuildCharge()
    {
        var face = Layer(out var faceT, new Point(0.5, 0.8));
        double row = 8, mouth = 24;
        // Eyes glow while it sips, then close into a contented ^ ^.
        var glow = new Ellipse
        {
            Width = 40, Height = 20, Fill = new RadialGradientBrush(Color.FromArgb(0x55, 0xF5, 0xF5, 0xF7), Color.FromArgb(0, 0xF5, 0xF5, 0xF7)),
            Opacity = 0.4,
        };
        face.Children.Add(At(glow, Cx - 20, row - 3.5));
        Loop(glow, OpacityProperty, 1.2, 0, (0, 0.35), (0.5, 1), (1, 0.35));
        var eyes = AddEyes(face, row, 13, blink: false);
        Once(eyes.Host, OpacityProperty, 0.2, 2.7, (0, 1), (1, 0));
        Once(glow, OpacityProperty, 0.2, 2.7, (0, 1), (1, 0));
        var arcs = AddArcs(face, row + 3);
        arcs.Host.Opacity = 0;
        Once(arcs.Host, OpacityProperty, 0.25, 2.75, (0, 0), (1, 1));
        var o = OMouth();
        o.RenderTransformOrigin = new Point(0.5, 0.5);
        var sip = new ScaleTransform();
        o.RenderTransform = sip;
        face.Children.Add(At(o, Cx - 3, mouth));
        var sipAnim = Keys(0.65, 0, (0, 1), (0.5, 1.35), (1, 1));
        sipAnim.RepeatBehavior = new RepeatBehavior(4);
        if (!Reduced)
        {
            Run(sip, ScaleTransform.ScaleXProperty, sipAnim);
            Run(sip, ScaleTransform.ScaleYProperty, sipAnim.Clone());
        }
        Once(o, OpacityProperty, 0.2, 2.6, (0, 1), (1, 0));
        var smile = Smile();
        smile.Opacity = 0;
        face.Children.Add(At(smile, Cx - 4.5, mouth));
        Once(smile, OpacityProperty, 0.3, 2.75, (0, 0), (1, 1));
        // The bolt comes in twice.
        var bolt = Fill("M 5.5,0.5 L 1,7 H 4 L 3,11.5 L 8,5 H 5 Z", Bolt);
        var boltBox = new Canvas { Width = 9, Height = 12, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5) };
        boltBox.Children.Add(bolt);
        var boltT = new TransformParts();
        boltBox.RenderTransform = boltT.Group;
        face.Children.Add(At(boltBox, Cx - 15 + 40, row + 4));
        var boltOp = Keys(1.3, 0, (0, 0), (0.15, 1), (0.85, 1), (1, 0));
        boltOp.RepeatBehavior = new RepeatBehavior(2);
        var boltX = Keys(1.3, 0, (0, 10), (0.55, 0), (0.85, -25), (1, -25));
        boltX.RepeatBehavior = new RepeatBehavior(2);
        var boltS = Keys(1.3, 0, (0, 1), (0.55, 1), (0.85, 0.3), (1, 0.2));
        boltS.RepeatBehavior = new RepeatBehavior(2);
        if (!Reduced)
        {
            Run(boltBox, OpacityProperty, boltOp);
            Run(boltT.Translate, TranslateTransform.XProperty, boltX);
            Run(boltT.Scale, ScaleTransform.ScaleXProperty, boltS);
            Run(boltT.Scale, ScaleTransform.ScaleYProperty, boltS.Clone());
        }
        // A happy sigh and a little puff.
        Once(faceT.Scale, ScaleTransform.ScaleXProperty, 0.9, 2.7, (0, 1), (0.4, 1.08), (1, 1));
        Once(faceT.Scale, ScaleTransform.ScaleYProperty, 0.9, 2.7, (0, 1), (0.4, 0.86), (1, 1));
        var puff = new Canvas { Width = 10, Height = 6, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5) };
        var puffColor = Solid(Color.FromRgb(0xC9, 0xCC, 0xD3));
        puff.Children.Add(At(new Ellipse { Width = 3.2, Height = 3.2, Fill = puffColor }, 0.4, 1.9));
        puff.Children.Add(At(new Ellipse { Width = 4, Height = 4, Fill = puffColor }, 3.5, 0.5));
        puff.Children.Add(At(new Ellipse { Width = 2.6, Height = 2.6, Fill = puffColor }, 7.2, 2.5));
        var puffT = new TransformParts();
        puff.RenderTransform = puffT.Group;
        face.Children.Add(At(puff, Cx - 15 + 30, row + 15));
        if (Reduced) return;
        Once(puff, OpacityProperty, 1.2, 2.8, (0, 0), (0.25, 1), (1, 0));
        Once(puffT.Translate, TranslateTransform.XProperty, 1.2, 2.8, (0, 0), (1, 12));
        Once(puffT.Translate, TranslateTransform.YProperty, 1.2, 2.8, (0, 0), (1, -4));
        Once(puffT.Scale, ScaleTransform.ScaleXProperty, 1.2, 2.8, (0, 0.6), (1, 1.15));
        Once(puffT.Scale, ScaleTransform.ScaleYProperty, 1.2, 2.8, (0, 0.6), (1, 1.15));
    }

    void BuildEarbuds()
    {
        var face = Layer(out var faceT);
        double row = 11.5, mouth = 21.5;
        AddArcs(face, row);
        face.Children.Add(At(Smile(), Cx - 4.5, mouth));
        var phones = new Canvas { Width = 46, Height = 24 };
        phones.Children.Add(PathOf("M 5,15 V 12 A 18,10 0 0 1 41,12 V 15", Ink, 2.2));
        phones.Children.Add(At(new Rectangle { Width = 7, Height = 11, RadiusX = 3, RadiusY = 3, Fill = Solid(Ink) }, 1, 11));
        phones.Children.Add(At(new Rectangle { Width = 7, Height = 11, RadiusX = 3, RadiusY = 3, Fill = Solid(Ink) }, 38, 11));
        DropOn(phones, 0.55, 10);
        face.Children.Add(At(phones, Cx - 23, row - 9));
        Loop(faceT.Translate, TranslateTransform.YProperty, 1.4, 0.6, (0, 0), (0.5, 1), (1, 0));
        Loop(faceT.Rotate, RotateTransform.AngleProperty, 1.4, 0.6, (0, 0), (0.5, -3), (1, 0));
    }

    void BuildNotify()
    {
        var face = Layer(out _);
        double row = 7, mouth = 25;
        var eyes = AddEyes(face, row, 15, origin: new Point(0.5, 1));
        // Eyes go wide, then glance at the app badge on the left.
        Once(eyes.Translate, TranslateTransform.YProperty, 0.7, 0, (0, 0), (0.25, 2), (0.55, -2), (0.75, 1), (1, 0));
        Once(eyes.Scale, ScaleTransform.ScaleXProperty, 0.7, 0, (0, 1), (0.25, 1.18), (0.55, 0.9), (0.75, 1.12), (1, 1.08));
        Once(eyes.Scale, ScaleTransform.ScaleYProperty, 0.7, 0, (0, 1), (0.25, 0.78), (0.55, 1.22), (0.75, 1.06), (1, 1.08));
        Once(eyes.Translate, TranslateTransform.XProperty, 0.35, 1, (0, 0), (1, -3));
        var o = OMouth();
        Pop(o, 0.5, 0.2);
        face.Children.Add(At(o, Cx - 3, mouth));
    }

    // ---- The mouth on a call follows the microphone -------------------------------------------------

    void SampleLevel()
    {
        if (!IsVisible || _talkScale is null || _talkMouth is null || _smileMouth is null)
        {
            _levelTimer.Stop();
            return;
        }
        double peak;
        try { peak = _level?.Invoke() ?? 0; }
        catch (Exception) { peak = 0; }
        double open = _talk.Sample(peak, _clock.Elapsed);
        bool talking = _talk.Speaking;
        // Sample quickly only while there's speech; a quiet call polls the meter a few times a second.
        var interval = talking ? TalkFast : TalkSlow;
        if (_levelTimer.Interval != interval) _levelTimer.Interval = interval;
        // Four mouth sizes: only a real change redraws (the window is expensive to repaint).
        int step = talking ? (int)Math.Round(open * 4) : -1;
        if (step == _mouthStep && talking == _mouthTalking) return;
        _mouthStep = step;
        _mouthTalking = talking;
        _talkMouth.Opacity = talking ? 1 : 0;
        _smileMouth.Opacity = talking ? 0 : 1;
        // Closed it's a flat oval (1.15 × 0.35), fully open a tall "o".
        double o = Math.Max(0, step) / 4.0;
        _talkScale.ScaleX = 1.15 - 0.15 * o;
        _talkScale.ScaleY = 0.35 + 0.8 * o;
    }

    // ---- Parts ------------------------------------------------------------------------------------

    sealed class TransformParts
    {
        public readonly ScaleTransform Scale = new();
        public readonly RotateTransform Rotate = new();
        public readonly TranslateTransform Translate = new();
        public TransformGroup Group => new() { Children = { Scale, Rotate, Translate } };
    }

    sealed record EyeRow(Canvas Host, ScaleTransform Scale, TranslateTransform Translate);

    /// <summary>A full-column canvas for the face with its own scale/rotate/translate about the given origin.</summary>
    Canvas Layer(out TransformParts t, Point? origin = null)
    {
        t = new TransformParts();
        var c = new Canvas { Width = ColumnWidth, Height = ColumnHeight, RenderTransformOrigin = origin ?? new Point(0.5, 0.5) };
        c.RenderTransform = t.Group;
        Children.Add(c);
        return c;
    }

    EyeRow AddEyes(Canvas face, double top, double height, double shift = 0, bool shineBottom = false, bool blink = true, Point? origin = null)
    {
        var host = new Canvas { Width = ColumnWidth, Height = ColumnHeight, RenderTransformOrigin = origin ?? new Point(0.5, 0.5) };
        var scale = new ScaleTransform();
        var move = new TranslateTransform();
        host.RenderTransform = new TransformGroup { Children = { scale, move } };
        host.RenderTransformOrigin = new Point(0.5, (top + height) / ColumnHeight);
        foreach (double x in new[] { Cx - 15 + shift, Cx + 4 + shift })
        {
            var eye = Eye(height, shineBottom);
            if (blink) Blink(eye);
            host.Children.Add(At(eye, x, top));
        }
        face.Children.Add(host);
        return new EyeRow(host, scale, move);
    }

    EyeRow AddArcs(Canvas face, double top)
    {
        var host = new Canvas { Width = ColumnWidth, Height = ColumnHeight };
        host.Children.Add(At(Arc(), Cx - 16, top));
        host.Children.Add(At(Arc(), Cx + 4, top));
        face.Children.Add(host);
        return new EyeRow(host, new ScaleTransform(), new TranslateTransform());
    }

    Canvas AddBlush(Canvas face, double top, double rowWidth)
    {
        var host = new Canvas { Width = ColumnWidth, Height = ColumnHeight, IsHitTestVisible = false };
        double left = Cx - rowWidth / 2;
        host.Children.Add(At(new Ellipse { Width = 8, Height = 4, Fill = Solid(Pink), Opacity = 0.4 }, left - 6, top + 1));
        host.Children.Add(At(new Ellipse { Width = 8, Height = 4, Fill = Solid(Pink), Opacity = 0.4 }, left + rowWidth - 2, top + 1));
        face.Children.Add(host);
        return host;
    }

    /// <summary>A rounded eye lit from below, with a pin-point shine (the board's eye).</summary>
    static Border Eye(double height, bool shineBottom)
    {
        var grid = new Canvas { Width = 11, Height = height, ClipToBounds = true };
        grid.Children.Add(At(new Ellipse { Width = 3, Height = 3, Fill = Brushes.White }, 2, shineBottom ? height - 5 : height < 10 ? 1 : 2));
        return new Border
        {
            Width = 11,
            Height = height,
            CornerRadius = new CornerRadius(Math.Min(5.5, height / 2)),
            Background = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(0xA3, 0xA6, 0xB2), 0),
                new(Ink, 0.62),
            }, 90),
            Child = grid,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
    }

    /// <summary>A happy ^ eye.</summary>
    static Path Arc() => PathOf("M 1.25,7 V 5.25 A 4.75,4.25 0 0 1 10.75,5.25 V 7", Ink, 2.5, PenLineCap.Flat);

    static Path Smile() => PathOf("M 1,0 V 0.5 A 3.5,3.5 0 0 0 8,0.5 V 0", Ink, 2, PenLineCap.Flat);

    static Ellipse OMouth() => new() { Width = 6, Height = 6, Stroke = Solid(Ink), StrokeThickness = 2, Fill = Solid(MouthInside) };

    static Rectangle Flat() => new() { Width = 6, Height = 2, RadiusX = 1, RadiusY = 1, Fill = Solid(Ink) };

    static Canvas Note(Color color, double scale)
    {
        var c = new Canvas { Width = 9, Height = 11, LayoutTransform = new ScaleTransform(scale, scale), Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5) };
        c.Children.Add(PathOf("M 3,9 V 2.2 L 8,1 V 7.4", color, 1.5, PenLineCap.Flat));
        c.Children.Add(At(new Ellipse { Width = 3.6, Height = 3.6, Fill = Solid(color) }, 0.1, 7.2));
        c.Children.Add(At(new Ellipse { Width = 3.6, Height = 3.6, Fill = Solid(color) }, 5.1, 5.8));
        return c;
    }

    static Path PathOf(string data, Color stroke, double thickness, PenLineCap cap = PenLineCap.Round) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = Solid(stroke),
        StrokeThickness = thickness,
        StrokeStartLineCap = cap,
        StrokeEndLineCap = cap,
        StrokeLineJoin = PenLineJoin.Round,
    };

    static Path Fill(string data, Color fill) => new() { Data = Geometry.Parse(data), Fill = Solid(fill) };

    static SolidColorBrush Solid(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static T At<T>(T element, double x, double y) where T : UIElement
    {
        SetLeft(element, x);
        SetTop(element, y);
        return element;
    }

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    // ---- Motion vocabulary --------------------------------------------------------------------------

    /// <summary>Gives the eye a scale for blinking; <see cref="Second"/> closes and reopens it now and then.</summary>
    void Blink(Border eye)
    {
        var s = new ScaleTransform();
        eye.RenderTransform = s;
        _blinks.Add(s);
    }

    /// <summary>Drops onto the head from above with a little bounce.</summary>
    void DropOn(UIElement element, double seconds, double from)
    {
        var t = new TranslateTransform();
        element.RenderTransform = t;
        if (Reduced) return;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
        Run(t, TranslateTransform.YProperty, new DoubleAnimation(-from, 0, TimeSpan.FromSeconds(seconds)) { EasingFunction = ease });
        Run(element, OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(seconds * 0.6)));
    }

    /// <summary>Pops in: grows past full size and settles.</summary>
    void Pop(FrameworkElement element, double seconds, double delay)
    {
        var s = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = s;
        if (Reduced) return;
        element.Opacity = 0;
        Once(element, OpacityProperty, seconds, delay, (0, 0), (0.6, 1), (1, 1));
        Once(s, ScaleTransform.ScaleXProperty, seconds, delay, (0, 0.3), (0.6, 1.18), (0.8, 0.94), (1, 1));
        Once(s, ScaleTransform.ScaleYProperty, seconds, delay, (0, 0.3), (0.6, 1.18), (0.8, 0.94), (1, 1));
    }

    void NoteUp(FrameworkElement note, double delay)
    {
        var t = new TransformParts();
        note.RenderTransform = t.Group;
        if (Reduced)
        {
            note.Opacity = 0.8;
            return;
        }
        Loop(note, OpacityProperty, 1.8, delay, (0, 0), (0.2, 1), (1, 0));
        Loop(t.Translate, TranslateTransform.XProperty, 1.8, delay, (0, 0), (1, 14));
        Loop(t.Translate, TranslateTransform.YProperty, 1.8, delay, (0, 4), (1, -8));
        Loop(t.Scale, ScaleTransform.ScaleXProperty, 1.8, delay, (0, 0.6), (1, 1));
        Loop(t.Scale, ScaleTransform.ScaleYProperty, 1.8, delay, (0, 0.6), (1, 1));
        Loop(t.Rotate, RotateTransform.AngleProperty, 1.8, delay, (0, -10), (1, 12));
    }

    static DoubleAnimationUsingKeyFrames Keys(double seconds, double delay, params (double At, double Value)[] frames) =>
        Keys(seconds, delay, false, frames);

    static DoubleAnimationUsingKeyFrames Keys(double seconds, double delay, bool linear, (double At, double Value)[] frames)
    {
        var a = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(seconds),
            BeginTime = TimeSpan.FromSeconds(delay),
        };
        Timeline.SetDesiredFrameRate(a, 20);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        foreach (var (at, value) in frames)
        {
            var time = KeyTime.FromPercent(Math.Clamp(at, 0, 1));
            a.KeyFrames.Add(linear ? new LinearDoubleKeyFrame(value, time) : new EasingDoubleKeyFrame(value, time, ease));
        }
        return a;
    }

    /// <summary>A looping animation; nothing at all with Windows animations off (the pose stays at rest).</summary>
    void Loop(IAnimatable target, DependencyProperty property, double seconds, double delay, params (double At, double Value)[] frames) =>
        LoopKeys(target, property, Keys(seconds, delay, false, frames));

    void LoopKeys(IAnimatable target, DependencyProperty property, DoubleAnimationUsingKeyFrames a)
    {
        if (Reduced) return;
        a.FillBehavior = FillBehavior.Stop;
        Timeline.SetDesiredFrameRate(a, AmbientFps);
        _cycle.Add((target, property, a));
        _running.Add((target, property));
    }

    /// <summary>A one-shot that holds its last value; with Windows animations off it jumps straight there.</summary>
    void Once(IAnimatable target, DependencyProperty property, double seconds, double delay, params (double At, double Value)[] frames)
    {
        if (frames.Length == 0) return;
        if (Reduced)
        {
            ((DependencyObject)target).SetValue(property, frames[^1].Value);
            return;
        }
        Run(target, property, Keys(seconds, delay, false, frames));
    }

    void Run(IAnimatable target, DependencyProperty property, AnimationTimeline animation)
    {
        target.BeginAnimation(property, animation);
        _running.Add((target, property));
    }
}
