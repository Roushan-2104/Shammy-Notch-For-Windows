using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Shammy.Core.Notch;
using Shammy.Interop;
using Shammy.Motion;

namespace Shammy.Views;

/// <summary>
/// Shammy the notch pet: two small eyes on the resting notch whose shape and motion show its mood (see
/// <see cref="PetMoods"/>). At rest it blinks, glances around and follows a nearby pointer; it sways with
/// happy ^ ^ eyes to music, perks up at a notification, bounces when a timer is done, looks up while
/// listening, darts its eyes while thinking, droops on a low battery and dozes late at night.
/// As a cat (the default pet style, see <see cref="CatParts"/>) the same eyes get ears, slit pupils, whiskers and
/// a nose: the ears and whiskers follow the mood and twitch now and then, the pupils slide towards the pointer,
/// and resting the pointer on it earns a purr.
/// Every timer and animation runs only while it is on screen; floating notes, z's and hearts are spawned
/// one at a time by a timer rather than looping forever, so a sleeping pet is nearly free.
/// </summary>
internal sealed class ShammyPet : Grid
{
    const double Gap = 8;

    static readonly TimeSpan FarPoll = TimeSpan.FromMilliseconds(250), NearPoll = TimeSpan.FromMilliseconds(33), StillPoll = TimeSpan.FromSeconds(1);

    // When the pointer hasn't moved for a while the pet looks less often, and once you're away it stops its
    // blinks and fidgets until the pointer moves again: an unattended PC isn't woken four times a second.
    static readonly TimeSpan StillAfter = TimeSpan.FromSeconds(5), AwayAfter = TimeSpan.FromMinutes(2);
    Native.POINT _lastPointer;
    DateTime _stillSince = DateTime.UtcNow;
    bool _away;

    readonly Eye _leftEye = new(), _rightEye = new();
    readonly StackPanel _eyes = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    readonly Canvas _overlay = new() { IsHitTestVisible = false, ClipToBounds = false };
    // The cat's own parts sit behind the eyes and move with the head, but never squash with a blink.
    readonly Canvas _cat = new() { IsHitTestVisible = false, ClipToBounds = false };
    readonly CatParts.Ear _earL = new(false, Brushes.White), _earR = new(true, Brushes.White);
    readonly CatParts.Whiskers _whiskL = new(false, Brushes.White), _whiskR = new(true, Brushes.White);
    readonly System.Windows.Shapes.Path _nose = CatParts.Nose();
    readonly ScaleTransform _lid = new(1, 1), _perk = new(1, 1);
    readonly RotateTransform _sway = new();
    readonly TranslateTransform _glance = new(), _follow = new(), _bounce = new(), _pose = new();
    readonly SpringValue _followX = new(0, 0.01);
    readonly DispatcherTimer _blinkTimer, _glanceTimer, _pointerTimer, _particleTimer, _fidgetTimer;
    readonly Random _random = new();
    readonly List<DispatcherTimer> _waits = new();
    readonly Func<double> _musicLevel;
    IDisposable? _swayFrames;
    double _swayTime;
    bool _following, _subscribed, _alternate;
    double _glanceShift;
    DateTime? _overSince;
    DateTime _purredAt = DateTime.MinValue;

    // Shorter than the quickest hover: the purr comes before the notch opens under the pointer.
    static readonly TimeSpan PetFor = TimeSpan.FromMilliseconds(120), PurrEvery = TimeSpan.FromSeconds(12);

    public ShammyPet(Func<double> musicLevel)
    {
        _musicLevel = musicLevel;
        Width = 32;
        Height = RestView.RestHeight;
        _rightEye.Margin = new Thickness(Gap, 0, 0, 0);
        _eyes.Children.Add(_leftEye);
        _eyes.Children.Add(_rightEye);
        _eyes.RenderTransformOrigin = new Point(0.5, 0.5);
        _eyes.RenderTransform = new TransformGroup { Children = { _lid, _perk, _sway, _glance, _follow, _bounce, _pose } };
        _cat.Width = Width;
        _cat.Height = Height;
        _cat.RenderTransformOrigin = new Point(0.5, 0.5);
        _cat.RenderTransform = new TransformGroup { Children = { _sway, _follow, _bounce, _pose } };
        foreach (var part in new UIElement[] { _earL, _earR, _whiskL, _whiskR, _nose }) _cat.Children.Add(part);
        Children.Add(_cat);
        Children.Add(_eyes);
        Children.Add(_overlay);

        _blinkTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
        _blinkTimer.Tick += (_, _) => Blink();
        _glanceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(7) };
        _glanceTimer.Tick += (_, _) => LookAround();
        _pointerTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = FarPoll };
        _pointerTimer.Tick += (_, _) => TrackPointer();
        _particleTimer = new DispatcherTimer(DispatcherPriority.Background);
        _particleTimer.Tick += (_, _) => SpawnParticle();
        _fidgetTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(6) };
        _fidgetTimer.Tick += (_, _) => Fidget();

        IsVisibleChanged += (_, _) => UpdateRunning();
        Screens.PetLook.Changed += () =>
        {
            if (IsVisible) ApplyMood();
        };
        ApplyMood();
    }

    public PetMood Mood { get; private set; } = PetMood.Idle;
    public bool IsFollowing => _following;
    public double FollowOffset => _followX.Target;

    /// <summary>The pointer came within reach (wakes a sleepy pet). Raised on each nearby poll.</summary>
    public event Action? PointerNear;

    public void SetMood(PetMood mood)
    {
        if (mood == Mood) return;
        var from = Mood;
        Mood = mood;
        ApplyMood();
        if (!IsVisible || !Infrastructure.MotionPrefs.Lively) return;
        if (PetMoments.Yawns(from, mood)) Yawn();
        else if (PetMoments.WakesUp(from, mood)) StartAwake();
    }

    /// <summary>
    /// Nodding off: the eyes droop, then squeeze shut while the ears stretch up (the yawn), then it settles into
    /// sleep. A few repaints, like a blink; any other mood arriving in between simply takes over.
    /// </summary>
    void Yawn()
    {
        var white = Screens.PetLook.Solid();
        SetEyes(EyeShape.Droopy, 11, 8, white);
        _pose.Y = 1.5;
        HoldCat(new CatPose(10, 0.95, 1, 0, 6, 1.3));
        Later(650, () =>
        {
            if (Mood != PetMood.Sleepy) return;
            SetEyes(EyeShape.Lid, 12, 3, white);
            _pose.Y = 0.5;
            HoldCat(new CatPose(0, 1.2, -0.5, 0, -8, 0));
            Later(600, () =>
            {
                if (Mood == PetMood.Sleepy) ApplyMood();
            });
        });
    }

    /// <summary>Woken by the pointer: for a moment the ears are up and the pupils wide, then it relaxes.</summary>
    void StartAwake()
    {
        Perk();
        HoldCat(new CatPose(0, 1.18, 0, 0, -7, CatLooks.Wide));
        Later(750, () =>
        {
            if (Mood == PetMood.Idle) ApplyMood();
        });
    }

    /// <summary>Puts the ears, whiskers and pupils in a pose of their own, between two moods.</summary>
    void HoldCat(CatPose pose)
    {
        if (!CatParts.On) return;
        _leftEye.Pupil(pose.Pupil);
        _rightEye.Pupil(pose.Pupil);
        _earL.Hold(pose.EarTilt, pose.EarHeight, pose.EarDrop);
        _earR.Hold(pose.EarTilt + pose.EarTurn, pose.EarHeight, pose.EarDrop);
        _whiskL.Hold(pose.WhiskerLift);
        _whiskR.Hold(pose.WhiskerLift);
    }

    // ---- Running only while visible ------------------------------------------------------------

    void UpdateRunning()
    {
        if (IsVisible)
        {
            Reschedule(_blinkTimer, EyeLogic.NextBlink(_random, Mood == PetMood.Sleepy));
            Reschedule(_glanceTimer, EyeLogic.NextGlance(_random));
            Reschedule(_fidgetTimer, CatLooks.NextFidget(_random));
            _pointerTimer.Interval = FarPoll;
            _pointerTimer.Start();
            if (!_subscribed)
            {
                Animator.Frame += OnFrame;
                _subscribed = true;
            }
            ApplyMood();
        }
        else
        {
            _blinkTimer.Stop();
            _glanceTimer.Stop();
            _pointerTimer.Stop();
            _particleTimer.Stop();
            _fidgetTimer.Stop();
            // The notch opened under a pointer that had only just reached the cat: it still gets its purr.
            if (_overSince is not null && Mood == PetMood.Idle && DateTime.UtcNow - _purredAt >= PurrEvery) Purr();
            _overSince = null;
            _glanceShift = 0;
            foreach (var w in _waits) w.Stop();
            _waits.Clear();
            _lid.ScaleY = 1;
            _glance.X = 0;
            StopSway();
            _following = false;
            _followX.Snap(0);
            _follow.X = 0;
            _overlay.Children.Clear();
            if (_subscribed)
            {
                Animator.Frame -= OnFrame;
                _subscribed = false;
            }
        }
    }

    static void Reschedule(DispatcherTimer timer, TimeSpan interval)
    {
        timer.Stop();
        timer.Interval = interval;
        timer.Start();
    }

    void OnFrame(double dt)
    {
        _follow.X = _followX.Value;
        // A cat's pupils lead the look: they slide further than the eyes turn.
        double shift = CatLooks.PupilShift(_followX.Value) + _glanceShift;
        _leftEye.PupilShift = shift;
        _rightEye.PupilShift = shift;
    }

    // ---- Moods -----------------------------------------------------------------------------------

    static Brush Res(string key) => Ui.Tokens.Res(key);

    void ApplyMood()
    {
        // Stop whatever the last mood was doing.
        _bounce.BeginAnimation(TranslateTransform.YProperty, null);
        _glance.BeginAnimation(TranslateTransform.XProperty, null);
        _perk.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _perk.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _lid.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _particleTimer.Stop();
        StopSway();
        _sway.Angle = 0;
        _pose.X = _pose.Y = 0;
        _lid.ScaleY = 1;

        // The eye colour and shape chosen in Settings (PetLook); white round pills by default.
        var white = Screens.PetLook.Solid();
        switch (Mood)
        {
            case PetMood.Music:
                SetEyes(EyeShape.Arc, 12, 7, white);
                StartSway();
                StartParticles(TimeSpan.FromMilliseconds(900));
                break;
            case PetMood.Notify:
                SetEyes(EyeShape.Pill, 11, 15, white);
                _pose.X = 2;
                _pose.Y = 1.5;
                Perk();
                break;
            case PetMood.TimerDone:
                SetEyes(EyeShape.Arc, 12, 7, Res("TimerAccent"));
                Repeat(_bounce, TranslateTransform.YProperty, 1000, (0, 0), (200, -4), (400, 0), (600, -2.5), (800, 0));
                break;
            case PetMood.Listening:
                SetEyes(EyeShape.Pill, 11, 14, white);
                _pose.Y = -2;
                break;
            case PetMood.Thinking:
                SetEyes(EyeShape.Pill, 10, 11, white);
                _pose.Y = -1.5;
                Repeat(_glance, TranslateTransform.XProperty, 1200, (0, -2.5), (600, 2.5), (1200, -2.5));
                break;
            case PetMood.MeetingSoon:
                SetEyes(EyeShape.Pill, 11, 13, white);
                _pose.X = 3;
                break;
            case PetMood.Tired:
                SetEyes(EyeShape.Droopy, 11, 8, white);
                _pose.Y = 2;
                break;
            case PetMood.Sleepy:
                SetEyes(EyeShape.Lid, 12, 3, white);
                _pose.Y = 3;
                StartParticles(TimeSpan.FromSeconds(4));
                break;
            case PetMood.Happy:
                SetEyes(EyeShape.Arc, 12, 7, white);
                StartParticles(TimeSpan.FromMilliseconds(700));
                SpawnParticle();
                break;
            default:
                SetEyes(EyeShape.Pill, 11, 13, white);
                break;
        }
        ApplyCat();
    }

    /// <summary>The cat's ears, whiskers, nose and pupils for this mood (nothing when the pet style is Eyes).</summary>
    void ApplyCat()
    {
        bool cat = CatParts.On;
        _cat.Visibility = cat ? Visibility.Visible : Visibility.Collapsed;
        var pose = CatLooks.Pose(Mood);
        _leftEye.Pupil(cat ? pose.Pupil : 0);
        _rightEye.Pupil(cat ? pose.Pupil : 0);
        if (!cat) return;

        var ink = Screens.PetLook.Solid();
        _earL.Fill = ink;
        _earR.Fill = ink;
        _whiskL.Stroke = ink;
        _whiskR.Stroke = ink;
        // Ears and whiskers sit against a resting eye, whatever shape the mood gives the eyes themselves.
        var (w, h, _) = PetLooks.Restyle(Screens.PetLook.Style, 11, 13);
        const double leftEye = 6, rightEye = 26;
        double middle = Height / 2, top = middle - h / 2;
        Place(_earL, leftEye - 5.5, top);
        Place(_earR, rightEye - 5.5, top);
        Place(_whiskL, leftEye - w / 2, middle);
        Place(_whiskR, rightEye + w / 2, middle);
        Place(_nose, (leftEye + rightEye) / 2, middle + CatParts.NoseDrop);
        _earL.Hold(pose.EarTilt, pose.EarHeight, pose.EarDrop);
        _earR.Hold(pose.EarTilt + pose.EarTurn, pose.EarHeight, pose.EarDrop);
        _whiskL.Hold(pose.WhiskerLift);
        _whiskR.Hold(pose.WhiskerLift);
    }

    static void Place(UIElement part, double x, double y)
    {
        Canvas.SetLeft(part, x);
        Canvas.SetTop(part, y);
    }

    /// <summary>
    /// Now and then a resting cat flicks an ear or twitches its whiskers. Like a blink these are a few repaints,
    /// not an animation, and Calm motion turns them off.
    /// </summary>
    void Fidget()
    {
        _fidgetTimer.Interval = CatLooks.NextFidget(_random);
        if (!CatParts.On || !Infrastructure.MotionPrefs.Lively || !CatLooks.Fidgets(Mood)) return;
        if (_random.Next(2) == 0)
        {
            var ear = _random.Next(2) == 0 ? _earL : _earR;
            ear.Flick(16);
            Later(90, () =>
            {
                ear.Flick(0);
                Later(110, () =>
                {
                    ear.Flick(12);
                    Later(90, () => ear.Flick(0));
                });
            });
        }
        else
        {
            Whisk(9);
            Later(110, () =>
            {
                Whisk(-5);
                Later(110, () => Whisk(0));
            });
        }
    }

    void Whisk(double degrees)
    {
        _whiskL.Twitch(degrees);
        _whiskR.Twitch(degrees);
    }

    /// <summary>The pointer resting on the cat for a moment earns a purr, at most every few seconds.</summary>
    void Petting(bool over)
    {
        if (!over || Mood != PetMood.Idle || !CatParts.On)
        {
            _overSince = null;
            return;
        }
        var now = DateTime.UtcNow;
        _overSince ??= now;
        if (now - _overSince < PetFor || now - _purredAt < PurrEvery) return;
        Purr();
    }

    /// <summary>
    /// A purr, with the slow blink cats give the people they trust; the ears and whiskers soften while it lasts.
    /// The sound follows the Purr setting and the notch sounds switch; the blink happens either way.
    /// </summary>
    public void Purr()
    {
        if (!CatParts.On) return;
        _purredAt = DateTime.UtcNow;
        if (Screens.PetLook.Purrs) Infrastructure.NotchSound.Play(Shammy.Core.Audio.PingKind.Purr);
        Purrs++;
        if (!IsVisible) return;
        _lid.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _lid.ScaleY = 0.1;
        _earL.Flick(8);
        _earR.Flick(8);
        Whisk(-8);
        Later(800, () => _lid.ScaleY = 1);
        Later(1700, () =>
        {
            _earL.Flick(0);
            _earR.Flick(0);
            Whisk(0);
        });
    }

    /// <summary>How many times it has purred (for tests).</summary>
    public int Purrs { get; private set; }

    void SetEyes(EyeShape shape, double w, double h, Brush brush)
    {
        if (shape == EyeShape.Pill) (w, h, _) = Shammy.Core.Notch.PetLooks.Restyle(Screens.PetLook.Style, w, h);
        _leftEye.Show(shape, w, h, brush);
        _rightEye.Show(shape, w, h, brush);
    }

    static void Repeat(Animatable target, DependencyProperty property, double periodMs, params (double Ms, double Value)[] frames)
    {
        var a = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromMilliseconds(periodMs) };
        Timeline.SetDesiredFrameRate(a, 20);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        foreach (var (ms, value) in frames) a.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), ease));
        target.BeginAnimation(property, a);
    }

    /// <summary>Eyes go wide for a moment.</summary>
    void Perk()
    {
        var a = new DoubleAnimationUsingKeyFrames();
        Timeline.SetDesiredFrameRate(a, 20);
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.18, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.06, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400))));
        _perk.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        _perk.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// <summary>Sways to the beat: the visualizer's level sets how much (it's already running for the music).</summary>
    void StartSway()
    {
        if (!IsVisible || _swayFrames is not null) return;
        _swayFrames = Animator.Subscribe(dt =>
        {
            _swayTime += dt;
            double phase = _swayTime * 2 * Math.PI / 0.9;
            double energy = 0.35 + 0.65 * Math.Clamp(_musicLevel(), 0, 1);
            _sway.Angle = 7 * Math.Sin(phase) * energy;
            _pose.Y = -1.5 * (1 + Math.Sin(phase * 2)) * energy;
        });
    }

    void StopSway()
    {
        _swayFrames?.Dispose();
        _swayFrames = null;
    }

    void StartParticles(TimeSpan every)
    {
        if (!IsVisible) return;
        _particleTimer.Interval = every;
        _particleTimer.Start();
    }

    /// <summary>One note, z or heart floats up and fades; it removes itself when done.</summary>
    void SpawnParticle()
    {
        if (!IsVisible) return;
        _alternate = !_alternate;
        (string text, string brush, double size, double x, double y, double dx, double dy, double ms) = Mood switch
        {
            PetMood.Music => (_alternate ? "♪" : "♫", "MusicAccent", _alternate ? 11.0 : 10.0, _alternate ? 22.0 : 16.0, 6.0, 12.0, -18.0, 1800.0),
            PetMood.Sleepy => ("z", "CardText2", _alternate ? 10.0 : 8.0, 22.0, 6.0, 9.0, -16.0, 2600.0),
            PetMood.Happy => ("♥", "", 11.0, 6.0, 4.0, 0.0, -14.0, 1400.0),
            _ => ("", "", 0, 0, 0, 0, 0, 0),
        };
        if (text.Length == 0) return;
        var particle = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = FontWeights.Bold,
            Foreground = brush.Length > 0 ? Res(brush) : new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x8A)),
            RenderTransform = new TranslateTransform(),
            Opacity = 0,
        };
        Canvas.SetLeft(particle, x);
        Canvas.SetTop(particle, y);
        _overlay.Children.Add(particle);

        var duration = TimeSpan.FromMilliseconds(ms);
        var fade = new DoubleAnimationUsingKeyFrames { Duration = duration };
        Timeline.SetDesiredFrameRate(fade, 20);
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.25)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        fade.Completed += (_, _) => _overlay.Children.Remove(particle);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var move = (TranslateTransform)particle.RenderTransform;
        move.BeginAnimation(TranslateTransform.XProperty, Lean(new DoubleAnimation(0, dx, duration) { EasingFunction = ease }));
        move.BeginAnimation(TranslateTransform.YProperty, Lean(new DoubleAnimation(4, dy, duration) { EasingFunction = ease }));
        particle.BeginAnimation(OpacityProperty, fade);
    }

    // ---- Idle behaviour: blink, glance, follow ---------------------------------------------------

    bool Blinks => Mood is PetMood.Idle or PetMood.MeetingSoon or PetMood.Listening or PetMood.Notify or PetMood.Tired;

    /// <summary>
    /// A blink is two repaints (shut, open), not an animation: the notch is a layered, software-rendered window
    /// where each animated frame costs real CPU, and at this size a 120 ms close reads as a blink anyway.
    /// </summary>
    void Blink()
    {
        _blinkTimer.Interval = EyeLogic.NextBlink(_random, Mood == PetMood.Sleepy);
        if (!Blinks) return;
        _lid.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _lid.ScaleY = 0.1;
        bool twice = EyeLogic.DoubleBlink(_random);
        Later(120, () =>
        {
            _lid.ScaleY = 1;
            if (!twice) return;
            Later(110, () =>
            {
                _lid.ScaleY = 0.1;
                Later(110, () => _lid.ScaleY = 1);
            });
        });
    }

    /// <summary>One short wait on the UI thread (blinks, glances); dropped when the pet leaves the screen.</summary>
    void Later(int ms, Action run)
    {
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _waits.Remove(t);
            if (IsVisible) run();
        };
        _waits.Add(t);
        t.Start();
    }

    /// <summary>A glance to one side and back, only while idle and not following the pointer.</summary>
    void LookAround()
    {
        _glanceTimer.Interval = EyeLogic.NextGlance(_random);
        if (_following || Mood != PetMood.Idle) return;
        // A glance is a look aside and back: two repaints, not 1.4 s of animation.
        double to = EyeLogic.GlanceDirection(_random);
        _glance.BeginAnimation(TranslateTransform.XProperty, null);
        _glance.X = to;
        _glanceShift = Math.Sign(to) * 1.2;
        Later(1100, () =>
        {
            _glanceShift = 0;
            if (!_following) _glance.X = 0;
        });
    }

    /// <summary>
    /// Polls the cursor slowly while it's far and quickly once it's within reach, so the eyes track smoothly
    /// without a mouse hook. The poll is a single GetCursorPos call.
    /// </summary>
    void TrackPointer()
    {
        if (!IsVisible || PresentationSource.FromVisual(this) is null || !Native.GetCursorPos(out var p)) return;
        var now = DateTime.UtcNow;
        // Still and nowhere near the pet: back off. On or near it, resting still is what makes it purr, so keep going.
        if (p.X == _lastPointer.X && p.Y == _lastPointer.Y && !_following && _overSince is null)
        {
            var still = now - _stillSince;
            if (!_following && still > StillAfter && _pointerTimer.Interval != StillPoll) _pointerTimer.Interval = StillPoll;
            if (!_away && still > AwayAfter)
            {
                _away = true;
                _blinkTimer.Stop();
                _glanceTimer.Stop();
                _fidgetTimer.Stop();
            }
            return;
        }
        _lastPointer = p;
        _stillSince = now;
        if (_away)
        {
            _away = false;
            Reschedule(_blinkTimer, EyeLogic.NextBlink(_random, Mood == PetMood.Sleepy));
            Reschedule(_glanceTimer, EyeLogic.NextGlance(_random));
            Reschedule(_fidgetTimer, CatLooks.NextFidget(_random));
        }
        if (_pointerTimer.Interval == StillPoll) _pointerTimer.Interval = FarPoll;
        var centre = _eyes.PointToScreen(new Point(_eyes.ActualWidth / 2, _eyes.ActualHeight / 2));
        double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double dx = (p.X - centre.X) / scale, dy = (p.Y - centre.Y) / scale;
        var look = EyeLogic.Follow(dx, dy);
        if (look is not null) PointerNear?.Invoke();
        Petting(Math.Abs(dx) < 20 && Math.Abs(dy) < 14);
        bool follows = Mood == PetMood.Idle;
        if (look is { } x && follows)
        {
            if (!_following) _glance.BeginAnimation(TranslateTransform.XProperty, null);
            _following = true;
            if (Math.Abs(_followX.Target - x) > 0.05) _followX.AnimateTo(x, 0.25, 0.9);
            _pointerTimer.Interval = NearPoll;
        }
        else if (_following)
        {
            _following = false;
            _followX.AnimateTo(0, 0.5, 1);
            _pointerTimer.Interval = FarPoll;
        }
        else if (look is not null && Mood == PetMood.Sleepy)
        {
            _pointerTimer.Interval = FarPoll;
        }
    }

    /// <summary>A happy little hop (a song started).</summary>
    public void Bounce()
    {
        if (!IsVisible || Mood == PetMood.TimerDone) return;
        var hop = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(hop, 20);
        var up = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var down = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(-4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), up));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)), down));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(-2.5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(340)), up));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(440)), down));
        _bounce.BeginAnimation(TranslateTransform.YProperty, hop);
    }

    enum EyeShape { Pill, Arc, Lid, Droopy }

    /// <summary>
    /// One eye (the Pet board's round eye): a rounded pill lit from below with a pin-point shine (or a lid / droopy
    /// pill), or a ^ arc for happy moods. The activity costumes (ActivityPet) use the same eye.
    /// </summary>
    sealed class Eye : Grid
    {
        static readonly Color Lit = Color.FromRgb(0xF5, 0xF5, 0xF7), Shade = Color.FromRgb(0xA3, 0xA6, 0xB2);

        readonly Border _pill = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        readonly Canvas _inside = new();
        readonly System.Windows.Shapes.Ellipse _shine = new() { Width = 3, Height = 3, Fill = Brushes.White };
        readonly TranslateTransform _pupilShift = new();
        System.Windows.Shapes.Path? _pupil;
        EyeShape _shape;
        double _w, _h, _open;
        readonly System.Windows.Shapes.Path _arc = new()
        {
            Data = Geometry.Parse("M 1.25,7 V 5.25 A 4.75,4.25 0 0 1 10.75,5.25 V 7"),
            StrokeThickness = 2.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        public Eye()
        {
            Width = 12;
            Height = 16;
            Canvas.SetLeft(_shine, 2);
            Canvas.SetTop(_shine, 2);
            _inside.Children.Add(_shine);
            _pill.Child = _inside;
            Children.Add(_pill);
            Children.Add(_arc);
        }

        public void Show(EyeShape shape, double w, double h, Brush brush)
        {
            bool arc = shape == EyeShape.Arc;
            _arc.Visibility = arc ? Visibility.Visible : Visibility.Collapsed;
            _pill.Visibility = arc ? Visibility.Collapsed : Visibility.Visible;
            _arc.Stroke = brush;
            // White eyes get the board's soft gradient; a coloured mood keeps its flat colour.
            _pill.Background = brush is SolidColorBrush { Color: var c } && c == Lit
                ? new LinearGradientBrush(new GradientStopCollection { new(Shade, 0), new(Lit, 0.62) }, 90)
                : brush;
            _shine.Visibility = shape == EyeShape.Pill && h >= 9 ? Visibility.Visible : Visibility.Collapsed;
            _pill.Width = w;
            _pill.Height = h;
            (_shape, _w, _h) = (shape, w, h);
            Repupil();
            _pill.CornerRadius = shape switch
            {
                EyeShape.Droopy => new CornerRadius(2, 2, w / 2, w / 2),
                EyeShape.Lid => new CornerRadius(h / 2),
                _ => new CornerRadius(Screens.PetLook.Style == Shammy.Core.Notch.PetEyeStyle.Pixel ? 1.5 : w / 2),
            };
        }

        /// <summary>The cat's pupil: half its width (a slit when small), or 0 for none. Lids and arcs never show one.</summary>
        public void Pupil(double open)
        {
            if (open == _open) return;
            _open = open;
            Repupil();
        }

        /// <summary>Slides the pupil sideways inside the eye (the look leading the eye).</summary>
        public double PupilShift
        {
            set => _pupilShift.X = value;
        }

        void Repupil()
        {
            if (_pupil is not null)
            {
                _inside.Children.Remove(_pupil);
                _pupil = null;
            }
            if (_shape is EyeShape.Arc or EyeShape.Lid || CatParts.Pupil(_w, _h, _open) is not { } pupil) return;
            pupil.RenderTransform = _pupilShift;
            Canvas.SetLeft(pupil, _w / 2);
            Canvas.SetTop(pupil, _h / 2 + CatParts.PupilLow);
            // Under the shine, which stays the brightest thing in the eye.
            _inside.Children.Insert(0, pupil);
            _pupil = pupil;
        }
    }

    /// <summary>The notch is a layered, software-rendered window: every frame costs CPU, so small motions run at 20 fps.</summary>
    static AnimationTimeline Lean(AnimationTimeline a)
    {
        Timeline.SetDesiredFrameRate(a, 20);
        return a;
    }
}
