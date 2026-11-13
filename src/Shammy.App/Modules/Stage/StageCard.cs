using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Shammy.Core.Prompter;
using Shammy.Core.Stage;
using Shammy.Infrastructure;
using Shammy.Modules.Captions;
using Shammy.Modules.Mirror;
using Shammy.Modules.Prompter;
using Shammy.Modules.Record;
using Shammy.Views.Cards;

namespace Shammy.Modules.Stage;

/// <summary>What Stage needs from the module that opened it.</summary>
internal sealed record StageHost(
    Dispatcher Ui,
    Action Save,
    Action Close,
    Action<double, double> Resize,
    Func<bool, Task<string?>> SetCaptions,
    Func<IntPtr> Monitor,
    Func<IntPtr> Notch);

/// <summary>
/// Stage: the notch as a small studio around the camera. Right under the lens, three lines of the script (the one
/// just read faded, the one to say now in warm off-white, the next one faded), a single hairline of progress, and,
/// when live captions are on, the other person's latest words small and quiet underneath. It moves on a line at a
/// time with one gentle glide while you speak, and waits when you stop (a microphone level, never speech
/// recognition); without a microphone it moves at a steady pace. Around the screen a warm ring light lights your
/// face. The quiet controls (Light, Captions, Mirror, Voice, Record, pace, text size, close) show only while the
/// pointer rests on Stage, and so do its keys, so the meeting app keeps the keyboard.
/// Everything that listens or lights (microphone, ring light, camera, keys) lives only while Stage is open.
/// </summary>
internal sealed class StageCard : Grid
{
    public const double BaseWidth = 640, ColumnWidth = 560, MirrorSide = 108, MirrorSize = 84;
    const double TopPad = 10, QuietHeight = 26, GlideSeconds = 0.9, PreviousOpacity = 0.32, NextOpacity = 0.4;
    static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(40);

    /// <summary>Warm off-white for the script: about 15:1 on the notch's near-black, softer than pure white.</summary>
    static readonly SolidColorBrush Paper = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0xE9, 0xDF)));

    /// <summary>The other person's words: a cool light blue, kept quiet.</summary>
    static readonly SolidColorBrush Theirs = Freeze(new SolidColorBrush(Color.FromRgb(0xCF, 0xE8, 0xFF)));

    static readonly SolidColorBrush Track = Freeze(new SolidColorBrush(Color.FromArgb(0x1C, 0xEF, 0xE9, 0xDF)));
    static readonly SolidColorBrush Fill = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0xEF, 0xE9, 0xDF)));

    readonly string _script;
    readonly PrompterData _prefs;
    readonly StageHost _host;
    readonly StageReader _reader;
    readonly VoiceActivity _vad = new();
    readonly StageMic _mic = new();
    readonly RingLight _ring;
    readonly MirrorCamera _camera;

    readonly Canvas _view = new() { ClipToBounds = true, Background = Brushes.Transparent, Cursor = Cursors.Hand };
    readonly StackPanel _stack = new();
    readonly TranslateTransform _shift = new();
    readonly TextBlock[] _slots = new TextBlock[5];
    readonly Grid _quiet = new() { Height = QuietHeight };
    readonly TextBlock _quietText;
    readonly Border _quietHost = new() { IsHitTestVisible = false };
    readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Grid _bar = new() { Height = 1, SnapsToDevicePixels = true };
    readonly Border _barFill = new() { Background = Fill, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    readonly Ellipse _mirror = new() { Width = MirrorSize, Height = MirrorSize, StrokeThickness = 1, Stroke = Ui.Tokens.HairlineStrong, Fill = Ui.Tokens.Tile };
    readonly ImageBrush _mirrorBrush = new() { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
    readonly ScaleTransform _flip = new(-1, 1, 0.5, 0.5);
    readonly Ellipse _recDot = new() { Width = 6, Height = 6, Fill = Ui.Tokens.Alarm, Opacity = 0.85, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    readonly DispatcherTimer _ticker, _slow;
    readonly Stopwatch _clock = new();

    StageKeys? _keys;
    readonly Func<bool> _holdCards;
    double _size, _charWidth;
    int _chars;
    bool _started, _hovered, _steady, _cameraWanted, _locked;
    string? _notice;
    DateTime _noticeUntil;
    string _quietKind = "none";
    double _lastTick;
    int _lastBarPixels = -1;

    public StageCard(string script, PrompterData prefs, StageHost host)
    {
        _script = script;
        _prefs = prefs;
        _host = host;
        _size = StageText.ClampSize(prefs.TextSize);
        _ring = new RingLight(host.Ui, host.Monitor, host.Notch);
        _camera = new MirrorCamera(host.Ui);
        _camera.BitmapChanged += bmp => _mirrorBrush.ImageSource = bmp;
        _mirrorBrush.RelativeTransform = _flip;
        _reader = new StageReader(Array.Empty<string>(), prefs.Pace);
        _holdCards = () => _started;

        Background = Brushes.Transparent;
        AutomationProperties.SetName(this, "Stage");

        // The script's three lines (and one hidden each side, for the glide).
        for (int i = 0; i < _slots.Length; i++)
        {
            var t = new TextBlock
            {
                Foreground = Paper,
                FontFamily = CardKit.Face(FontWeights.Medium),
                FontWeight = FontWeights.Medium,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Width = ColumnWidth,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            _slots[i] = t;
            _stack.Children.Add(t);
        }
        _stack.RenderTransform = _shift;
        _view.Children.Add(_stack);
        _view.Width = ColumnWidth;
        // No tooltip over the script: nothing should pop up over the words mid-sentence.
        AutomationProperties.SetHelpText(_view, "Click to pause or carry on. With the pointer here, Space pauses, the arrows step a line, plus and minus change the size, and Esc closes.");
        _view.MouseLeftButtonUp += (_, e) =>
        {
            TogglePause();
            e.Handled = true;
        };
        MouseWheel += (_, e) =>
        {
            Step(e.Delta > 0 ? -1 : 1);
            e.Handled = true;
        };

        // Their words or a short notice, swapped for the controls while the pointer is here.
        _quietText = new TextBlock
        {
            FontFamily = CardKit.Face(FontWeights.Normal),
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Width = ColumnWidth - 40,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        _quietHost.Child = _quietText;
        _quiet.Children.Add(_quietHost);
        _chips.Opacity = 0;
        _chips.IsHitTestVisible = false;
        _quiet.Children.Add(_chips);

        _bar.Children.Add(new Border { Background = Track });
        _bar.Children.Add(_barFill);

        _mirror.Visibility = Visibility.Collapsed;
        _mirror.VerticalAlignment = VerticalAlignment.Center;
        _mirror.HorizontalAlignment = HorizontalAlignment.Left;
        _mirror.Margin = new Thickness((MirrorSide - MirrorSize) / 2 + 4, 0, 0, 0);
        AutomationProperties.SetName(_mirror, "Your camera");

        Build();
        Layout();
        Rewrap();

        _ticker = new DispatcherTimer(Tick, DispatcherPriority.Normal, (_, _) => OnTick(), host.Ui) { IsEnabled = false };
        _slow = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => OnSlowTick(), host.Ui) { IsEnabled = false };

        MouseEnter += (_, _) => SetHovered(true);
        MouseLeave += (_, _) => SetHovered(false);
        Loaded += (_, _) => Begin();
        Unloaded += (_, _) => Shutdown();
    }

    // ---- Sizes ---------------------------------------------------------------------------------------

    double LineHeight => Math.Round(_size * 1.5);

    public double CardWidth => BaseWidth + (_prefs.Mirror ? MirrorSide * 2 : 0);

    public double CardHeight => TopPad + LineHeight * 3 + 8 + QuietHeight + 8 + 1 + 12;

    /// <summary>The script's column (lines, the quiet line, the hairline), centred under the camera; the mirror to its left.</summary>
    void Build()
    {
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, TopPad, 0, 0), Width = ColumnWidth };
        column.Children.Add(_view);
        _quiet.Margin = new Thickness(0, 8, 0, 0);
        column.Children.Add(_quiet);
        _bar.Margin = new Thickness(40, 8, 40, 0);
        column.Children.Add(_bar);
        Children.Add(column);
        Children.Add(_mirror);
        Children.Add(CardKit.At(_recDot, HorizontalAlignment.Left, VerticalAlignment.Top, 16, 14));
    }

    /// <summary>Sizes everything for the text size and whether the mirror shows.</summary>
    void Layout()
    {
        Width = CardWidth;
        Height = CardHeight;
        double lh = LineHeight;
        foreach (var t in _slots)
        {
            t.FontSize = _size;
            t.Height = lh;
            t.LineHeight = lh;
            t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }
        _view.Height = lh * 3;
        Canvas.SetTop(_stack, -lh);
        _mirror.Visibility = _prefs.Mirror ? Visibility.Visible : Visibility.Collapsed;
        _mirror.Fill = _mirrorBrush.ImageSource is null ? Ui.Tokens.Tile : _mirrorBrush;
    }

    /// <summary>The average width of a character of the script at this size, from the face itself.</summary>
    double MeasureCharWidth()
    {
        const string Sample = "And here is how we will get there, one quiet step at a time, together.";
        var face = new Typeface(CardKit.Face(FontWeights.Medium), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
        double ppd = 1;
        try { ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch (InvalidOperationException) { }
        var ft = new FormattedText(Sample, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, _size, Paper, ppd);
        // A little room to spare: some lines are wider than the average.
        return ft.WidthIncludingTrailingWhitespace / Sample.Length * 1.08;
    }

    /// <summary>Wraps the script for this size (keeping the place) and shows it.</summary>
    void Rewrap()
    {
        _charWidth = MeasureCharWidth();
        _chars = StageLines.CharsFor(ColumnWidth - 16, _charWidth);
        _reader.SetLines(StageLines.Wrap(_script, _chars));
        Render(0);
        UpdateBar(force: true);
    }

    // ---- Life ------------------------------------------------------------------------------------------

    /// <summary>On screen: open the microphone, light the ring, arm the keys, start the camera if wanted.</summary>
    void Begin()
    {
        if (_started) return;
        _started = true;
        _keys = new StageKeys(_host.Ui, OnKey);
        if (!_keys.IsInstalled) Log.Warn("Stage: the keyboard hook didn’t install; Stage works by mouse");
        if (_prefs.Ring) _ring.Show(_prefs.RingStrength);
        if (_prefs.Voice) OpenMic();
        else _steady = true;
        CaptionsFeed.Changed += OnCaption;
        RecordModule.HoldCards = _holdCards;
        if (MirrorModule.Current is { } m)
        {
            _locked = m.Locked;
            m.LockChanged += OnLock;
        }
        if (_prefs.Mirror) StartCamera();
        BuildChips();
        if (Listening) Notice("Start speaking and Stage follows", 4);
        else ShowQuiet();
        _clock.Restart();
        _lastTick = 0;
        _slow.Start();
        Run();
    }

    /// <summary>Gone from the notch (closed, or another card took its place): everything is released.</summary>
    public void Shutdown()
    {
        if (!_started) return;
        _started = false;
        _ticker.Stop();
        _slow.Stop();
        _keys?.Dispose();
        _keys = null;
        _mic.Close();
        _ring.Hide();
        CaptionsFeed.Changed -= OnCaption;
        if (MirrorModule.Current is { } m) m.LockChanged -= OnLock;
        _cameraWanted = false;
        _ = _camera.StopAsync();
        if (RecordModule.HoldCards == _holdCards) RecordModule.HoldCards = null;
        // A recording that ended while Stage was open shows its card now.
        _host.Ui.BeginInvoke(() => RecordModule.Current?.ShowHeldCard(), DispatcherPriority.Background);
    }

    void OpenMic()
    {
        _vad.Reset();
        if (_mic.Open(out var problem))
        {
            _steady = false;
            return;
        }
        _steady = true;
        Notice($"{problem} · moving at a steady pace", 8);
    }

    // ---- Moving on ---------------------------------------------------------------------------------------

    /// <summary>A level fed through the test pipe instead of the microphone (RMS 0–1); null = the microphone.</summary>
    public double? SimulatedLevel { get; private set; }

    public void Simulate(double? level)
    {
        if (level is not null && SimulatedLevel is null)
        {
            // From a quiet room, so the first level fed is heard as a voice.
            _vad.Reset();
            _vad.Feed(0.0005, 0.5);
        }
        SimulatedLevel = level;
        Run();
    }

    bool Listening => SimulatedLevel is not null || (!_steady && _mic.IsOpen);

    void Run()
    {
        bool moving = _started && !_reader.Paused && !_reader.Done;
        if (moving && !_ticker.IsEnabled)
        {
            _lastTick = _clock.Elapsed.TotalSeconds;
            _ticker.Start();
        }
        else if (!moving) _ticker.Stop();
    }

    void OnTick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastTick, 0, 0.25);
        _lastTick = now;
        bool speaking;
        if (SimulatedLevel is { } simulated) speaking = _vad.Feed(simulated, dt);
        else if (Listening) speaking = _vad.Feed(_mic.TakeLevel(), dt);
        else speaking = true;
        // Hidden (a fullscreen app, the notch tucked away): nobody can read it, so it waits.
        if (!IsVisible) return;
        int moved = _reader.Advance(dt, speaking);
        if (moved != 0) Render(moved);
        UpdateBar();
        if (_reader.Done)
        {
            Notice("The end · Space to start over", 6);
            Run();
        }
    }

    void OnSlowTick()
    {
        // All zeros for a while: Windows is keeping the microphone from desktop apps.
        if (!_steady && SimulatedLevel is null && _mic.IsOpen && _mic.Blocked)
        {
            _mic.Close();
            _steady = true;
            Notice("Microphone access is off · moving at a steady pace", 8);
        }
        UpdateRecord();
        if (_notice is not null && DateTime.UtcNow >= _noticeUntil) ShowQuiet();
    }

    public void TogglePause()
    {
        if (_reader.Done)
        {
            _reader.Restart();
            Render(0);
            UpdateBar(force: true);
            Notice(null);
        }
        else
        {
            _reader.Paused = !_reader.Paused;
            Notice(_reader.Paused ? "Paused · Space to carry on" : null, 4);
        }
        Run();
    }

    public void Step(int lines)
    {
        int moved = _reader.Step(lines);
        if (moved == 0) return;
        Render(Math.Abs(moved) == 1 ? moved : 0);
        UpdateBar(force: true);
        Run();
    }

    public void Seek(double percent)
    {
        _reader.Seek(percent);
        Render(0);
        UpdateBar(force: true);
        Run();
    }

    public void SetPace(PrompterPace pace)
    {
        _reader.Pace = pace;
        _prefs.Pace = pace;
        _host.Save();
        BuildChips();
    }

    /// <summary>
    /// Shows the reader's lines. With <paramref name="glide"/> ±1 the text glides one line up (or down) with the
    /// fades following, in one ease-out of about 0.9 s; with Calm (or a jump) it simply changes.
    /// </summary>
    void Render(int glide)
    {
        var lines = _reader.Lines;
        double[] target = { 0, PreviousOpacity, 1, NextOpacity, 0 };
        for (int k = 0; k < _slots.Length; k++)
        {
            int i = _reader.Index - 2 + k;
            _slots[k].Text = i >= 0 && i < lines.Count ? lines[i] : "";
        }
        bool animate = glide is 1 or -1 && MotionPrefs.Lively && IsLoaded;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromSeconds(GlideSeconds);
        for (int k = 0; k < _slots.Length; k++)
        {
            _slots[k].BeginAnimation(OpacityProperty, null);
            _slots[k].Opacity = target[k];
            if (!animate) continue;
            int from = k + glide;
            double start = from >= 0 && from < target.Length ? target[from] : 0;
            _slots[k].BeginAnimation(OpacityProperty, new DoubleAnimation(start, target[k], duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        }
        _shift.BeginAnimation(TranslateTransform.YProperty, null);
        _shift.Y = 0;
        if (animate)
            _shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(glide * LineHeight, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        AutomationProperties.SetName(_view, _reader.Current);
    }

    void UpdateBar(bool force = false)
    {
        double width = ColumnWidth - 80;
        int pixels = (int)Math.Round(width * _reader.Percent / 100);
        if (!force && pixels == _lastBarPixels) return;
        _lastBarPixels = pixels;
        _barFill.Width = pixels;
    }

    // ---- Keys and the pointer ----------------------------------------------------------------------------

    public bool Hovered => _hovered;

    void SetHovered(bool on)
    {
        _hovered = on;
        if (_keys is not null) _keys.Armed = on;
        if (on) BuildChips();
        _chips.IsHitTestVisible = on;
        Fade(_chips, on ? 1 : 0);
        Fade(_quietHost, on ? 0 : 1);
    }

    static void Fade(UIElement e, double to)
    {
        e.BeginAnimation(OpacityProperty, null);
        if (!MotionPrefs.Lively)
        {
            e.Opacity = to;
            return;
        }
        double from = e.Opacity;
        e.Opacity = to;
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.Stop });
    }

    public void OnKey(StageKey key)
    {
        if (!_started) return;
        switch (key)
        {
            case StageKey.Space: TogglePause(); break;
            case StageKey.Back: Step(-1); break;
            case StageKey.On: Step(1); break;
            case StageKey.Bigger: Resize(1); break;
            case StageKey.Smaller: Resize(-1); break;
            case StageKey.Close: _host.Close(); break;
            case StageKey.Light: ToggleRing(); break;
            case StageKey.Captions: _ = ToggleCaptions(); break;
            case StageKey.Mirror: ToggleMirror(); break;
            case StageKey.Voice: ToggleVoice(); break;
        }
    }

    // ---- The controls ------------------------------------------------------------------------------------

    public void Resize(int direction)
    {
        double size = StageText.StepSize(_size, direction);
        if (size == _size) return;
        _size = size;
        _prefs.TextSize = size;
        _host.Save();
        Layout();
        Rewrap();
        _host.Resize(CardWidth, CardHeight);
    }

    public void SetRing(bool on, double strength)
    {
        _prefs.Ring = on;
        _prefs.RingStrength = StageText.ClampRing(strength);
        _host.Save();
        if (on && _started) _ring.Show(_prefs.RingStrength);
        else _ring.Hide();
        BuildChips();
    }

    void ToggleRing() => SetRing(!_prefs.Ring, _prefs.RingStrength);

    async Task ToggleCaptions()
    {
        bool on = !CaptionsFeed.IsOn;
        var problem = await _host.SetCaptions(on);
        if (problem is not null) Notice(problem, 6);
        else if (on) Notice("Captions on · their words show here", 3);
        BuildChips();
        ShowQuiet();
    }

    void ToggleMirror()
    {
        _prefs.Mirror = !_prefs.Mirror;
        _host.Save();
        if (_prefs.Mirror) StartCamera();
        else StopCamera();
        Layout();
        _host.Resize(CardWidth, CardHeight);
        BuildChips();
    }

    void ToggleVoice()
    {
        _prefs.Voice = !_prefs.Voice;
        _host.Save();
        if (_prefs.Voice)
        {
            OpenMic();
            if (!_steady) Notice("Moves on while you speak", 3);
        }
        else
        {
            _mic.Close();
            _steady = true;
            Notice("Moving at a steady pace", 3);
        }
        BuildChips();
        Run();
    }

    async void ToggleRecord()
    {
        if (RecordModule.Current is not { } record)
        {
            Notice("Recording isn’t available", 4);
            return;
        }
        if (record.IsSaving)
        {
            Notice("Saving the last recording", 3);
            return;
        }
        if (record.IsRecording)
        {
            record.StopRecording();
            Notice("Recording saved to Videos › Shammy", 5);
        }
        else
        {
            try
            {
                var failure = await record.StartScreenWithMic();
                if (failure is not null) Notice("The recording didn’t start", 5);
            }
            catch (Exception ex)
            {
                Log.Warn("Stage: starting a recording failed", ex);
                Notice("The recording didn’t start", 5);
            }
        }
        UpdateRecord();
        BuildChips();
    }

    void UpdateRecord()
    {
        bool recording = RecordModule.Current?.IsRecording == true;
        _recDot.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        if (_hovered && recording) BuildChips();
    }

    void CyclePace() => SetPace(_reader.Pace switch
    {
        PrompterPace.Slow => PrompterPace.Normal,
        PrompterPace.Normal => PrompterPace.Fast,
        _ => PrompterPace.Slow,
    });

    /// <summary>The quiet controls: small, low-contrast words; the on ones carry a warm dot.</summary>
    void BuildChips()
    {
        _chips.Children.Clear();
        var record = RecordModule.Current;
        bool recording = record?.IsRecording == true;
        Add(Chip("Light", _prefs.Ring, _prefs.Ring ? "Turn the ring light off (L)" : "Turn the ring light on (L)", ToggleRing, wheel: d => SetRing(true, StageText.StepRing(_prefs.RingStrength, d))));
        Add(Chip("Captions", CaptionsFeed.IsOn, CaptionsFeed.IsOn ? "Turn live captions off (C)" : "Show their words with live captions (C)", () => _ = ToggleCaptions()));
        Add(Chip("Mirror", _prefs.Mirror, _prefs.Mirror ? "Hide your camera (M)" : "Check your framing in a small mirror (M)", ToggleMirror));
        Add(Chip("Voice", _prefs.Voice && !_steady, _prefs.Voice ? "Move at a steady pace instead (V)" : "Move on only while you speak (V)", ToggleVoice));
        Add(Chip(recording ? "Stop " + Elapsed(record!.Elapsed) : "Record", recording, recording ? "Stop recording" : "Record the screen and your microphone", ToggleRecord, red: recording));
        Add(Chip(_reader.Pace.ToString(), false, $"Reading pace: {_reader.Pace} · click to change", CyclePace));
        Add(Chip("A−", false, "Smaller text (−)", () => Resize(-1)));
        Add(Chip("A+", false, "Bigger text (+)", () => Resize(1)));
        var close = CardKit.CloseButton(_host.Close, "Close Stage (Esc)");
        close.Margin = new Thickness(6, 0, 0, 0);
        _chips.Children.Add(close);

        void Add(FrameworkElement chip) => _chips.Children.Add(chip);
    }

    static string Elapsed(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    static FrameworkElement Chip(string text, bool on, string name, Action click, Action<int>? wheel = null, bool red = false)
    {
        var label = new TextBlock
        {
            Text = text,
            FontFamily = CardKit.Face(FontWeights.SemiBold),
            FontWeight = FontWeights.SemiBold,
            FontSize = 11.5,
            Foreground = on ? Paper : Ui.Tokens.Text2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (on)
        {
            var dot = new Ellipse { Width = 5, Height = 5, Fill = red ? Ui.Tokens.Alarm : Paper, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(dot);
        }
        row.Children.Add(label);
        var face = new Border
        {
            Height = 24,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(12),
            Background = Brushes.Transparent,
            Child = row,
            Cursor = Cursors.Hand,
        };
        var b = new Ui.InvokableBorder
        {
            Child = face,
            Margin = new Thickness(1, 0, 1, 0),
            ToolTip = name,
            Invoked = click,
            Background = Brushes.Transparent,
        };
        AutomationProperties.SetName(b, name);
        Ui.Press.Attach(b);
        b.MouseEnter += (_, _) => face.Background = Ui.Tokens.QuietBg;
        b.MouseLeave += (_, _) => face.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) =>
        {
            click();
            e.Handled = true;
        };
        if (wheel is not null)
        {
            b.MouseWheel += (_, e) =>
            {
                wheel(e.Delta > 0 ? 1 : -1);
                e.Handled = true;
            };
        }
        return b;
    }

    // ---- The quiet line ------------------------------------------------------------------------------------

    void OnCaption(string _) => ShowQuiet();

    /// <summary>A short notice in the quiet line for <paramref name="seconds"/>; null clears it.</summary>
    void Notice(string? text, double seconds = 4)
    {
        _notice = text;
        _noticeUntil = DateTime.UtcNow.AddSeconds(seconds);
        ShowQuiet();
        if (text is not null) Ui.A11y.Announce(text, this, "Shammy.Stage");
    }

    /// <summary>A notice while it lasts, else their latest words while captions are on, else nothing.</summary>
    void ShowQuiet()
    {
        if (_notice is not null && DateTime.UtcNow >= _noticeUntil) _notice = null;
        if (_notice is not null)
        {
            _quietKind = "notice";
            _quietText.Text = _notice;
            _quietText.Foreground = Ui.Tokens.Text2;
            _quietText.FontSize = 12.5;
            _quietText.Opacity = 1;
            return;
        }
        if (CaptionsFeed.IsOn && CaptionsFeed.Latest.Length > 0)
        {
            _quietKind = "caption";
            int chars = (int)Math.Floor((ColumnWidth - 40) / (_charWidth / _size * 13));
            _quietText.Text = StageText.CaptionTail(CaptionsFeed.Latest, Math.Max(24, chars));
            _quietText.Foreground = Theirs;
            _quietText.FontSize = 13;
            _quietText.Opacity = 0.72;
            return;
        }
        _quietKind = "none";
        _quietText.Text = "";
    }

    public string QuietLine => $"{_quietKind} \"{_quietText.Text}\"";

    // ---- The mirror ------------------------------------------------------------------------------------------

    void OnLock(bool locked)
    {
        _locked = locked;
        if (!_prefs.Mirror) return;
        if (locked) StopCamera();
        else StartCamera();
    }

    async void StartCamera()
    {
        if (_locked || !_started) return;
        _cameraWanted = true;
        var store = MirrorModule.Current?.Store;
        _flip.ScaleX = store?.Mirrored == false ? 1 : -1;
        _camera.Brightness = 0.6 + (store?.Level ?? 50) / 100.0 * 0.8;
        var problem = await _camera.StartAsync();
        if (!_cameraWanted)
        {
            await _camera.StopAsync();
            return;
        }
        if (problem == MirrorProblem.None)
        {
            _mirror.Fill = _mirrorBrush;
            return;
        }
        Notice(problem switch
        {
            MirrorProblem.NoCamera => "No camera found",
            MirrorProblem.Denied => "Camera access is off for desktop apps",
            MirrorProblem.InUse => "The camera is in use by another app",
            _ => "Couldn’t start the camera",
        }, 6);
        _prefs.Mirror = false;
        _host.Save();
        _cameraWanted = false;
        Layout();
        _host.Resize(CardWidth, CardHeight);
        BuildChips();
    }

    void StopCamera()
    {
        _cameraWanted = false;
        _ = _camera.StopAsync();
        _mirrorBrush.ImageSource = null;
        _mirror.Fill = Ui.Tokens.Tile;
    }

    // ---- For the test pipe -------------------------------------------------------------------------------------

    public bool Paused => _reader.Paused;
    public bool Done => _reader.Done;
    public double Percent => _reader.Percent;
    public string Ring => _ring.Describe();

    public string Describe()
    {
        string voice = SimulatedLevel is not null ? "simulated" : Listening ? "listening" : "steady";
        return string.Create(CultureInfo.InvariantCulture,
            $"lines={_reader.Lines.Count} index={_reader.Index} percent={_reader.Percent:0} paused={_reader.Paused} done={_reader.Done} " +
            $"pace={_reader.Pace} size={_size:0} chars={_chars} voice={voice} speaking={_vad.Speaking} level={_vad.Level:0}dB floor={_vad.Floor:0}dB " +
            $"moving={_ticker.IsEnabled} ring=({_ring.Describe()}) captions={CaptionsFeed.IsOn} quiet={QuietLine} " +
            $"mirror={(_prefs.Mirror ? (_camera.Running ? "on" : "starting") : "off")} record={(RecordModule.Current?.IsRecording == true ? "on" : "off")} " +
            $"keys={(_keys?.IsInstalled == true ? "ready" : "off")} hover={_hovered} width={CardWidth:0} height={CardHeight:0} " +
            $"prev=\"{_reader.Previous}\" line=\"{_reader.Current}\" next=\"{_reader.Next}\"");
    }

    static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
