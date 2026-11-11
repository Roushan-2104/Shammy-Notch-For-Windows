using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Notch;
using Shammy.Core.Productivity;
using Shammy.Views;

namespace Shammy.Modules.Productivity;

internal enum ClockKind { Timer, Stopwatch, Pomodoro, Focus }

/// <summary>
/// One running timer, stopwatch, pomodoro or focus session. Time is kept as wall-clock spans
/// (not tick counts) so a late or skipped tick never makes it drift.
/// </summary>
internal sealed class RunningClock
{
    TimeSpan _banked;
    DateTime? _since;

    public RunningClock(int id, ClockKind kind, string label, string glyph, TimeSpan total)
    {
        Id = id;
        Kind = kind;
        Label = label;
        Glyph = glyph;
        Total = total;
        _since = DateTime.UtcNow;
        Content = new ClockContent(this);
    }

    public int Id { get; }
    public ClockKind Kind { get; }
    public string Label { get; private set; }
    public string Glyph { get; private set; }
    public TimeSpan Total { get; private set; }
    public int PomodoroStep { get; set; }
    public ClockContent Content { get; set; }

    public string ActivityId => $"productivity.clock.{Id}";
    public bool IsCountdown => Kind != ClockKind.Stopwatch;
    public bool Paused => _since is null;

    public TimeSpan Elapsed(DateTime utcNow) => _banked + (_since is { } s ? utcNow - s : TimeSpan.Zero);
    public TimeSpan Remaining(DateTime utcNow) => Total - Elapsed(utcNow);

    /// <summary>"4:59" counting down, or "1:02" up for the stopwatch.</summary>
    public string Display(DateTime utcNow) =>
        IsCountdown ? TimeFormat.Countdown(Remaining(utcNow)) : TimeFormat.Elapsed(Elapsed(utcNow));

    public void Pause(DateTime utcNow)
    {
        _banked = Elapsed(utcNow);
        _since = null;
    }

    public void Resume(DateTime utcNow) => _since ??= utcNow;

    /// <summary>Next pomodoro phase: same clock, new length and label.</summary>
    public void Restart(TimeSpan total, string label, string glyph)
    {
        Total = total;
        Label = label;
        Glyph = glyph;
        _banked = TimeSpan.Zero;
        _since = DateTime.UtcNow;
    }

    /// <summary>"+1 min" on the timer card: a longer countdown, same progress so far.</summary>
    public void Extend(TimeSpan more) => Total += more;

    /// <summary>When a running countdown ends, in local time (null for a stopwatch).</summary>
    public DateTime? EndsLocal(DateTime utcNow) => IsCountdown ? DateTime.Now + Remaining(utcNow) : null;

    /// <summary>How long until the shown text changes, so ticks land right on the second.</summary>
    public TimeSpan UntilNextSecond(DateTime utcNow)
    {
        double s = IsCountdown ? Remaining(utcNow).TotalSeconds : Elapsed(utcNow).TotalSeconds;
        double frac = IsCountdown ? s - Math.Floor(s) : 1 - (s - Math.Floor(s));
        return TimeSpan.FromSeconds(frac <= 0.001 ? 1 : frac);
    }
}

/// <summary>
/// The compact ears for a clock: a draining ring and glyph (plus label) on the left, the time on the
/// right. The same instance stays posted and only its own text and ring change each second. The pet wears it
/// as the timer costume (eyes circling a ticking clock), and the action circle pauses and resumes.
/// </summary>
internal sealed class ClockContent : ICompactContent, IPetActivity
{
    const string PauseGlyph = "\uE769";

    readonly RunningClock _clock;
    ProgressRing? _ring, _bubbleRing;
    TextBlock? _glyph, _time, _bubbleGlyph;
    bool _hours;

    public ClockContent(RunningClock clock) => _clock = clock;

    /// <summary>Pauses or resumes the clock (set by the module, which owns the tick).</summary>
    public Action? TogglePause { get; set; }

    public PetActivityState Describe(DateTime utcNow) => new()
    {
        Costume = PetCostume.Timer,
        Label = _clock.Label.Length > 0 ? _clock.Label : _clock.Kind.ToString(),
        Value = _clock.Display(utcNow),
        Paused = _clock.Paused,
        Progress = _clock.IsCountdown ? Math.Clamp(_clock.Remaining(utcNow).TotalSeconds / Math.Max(1, _clock.Total.TotalSeconds), 0, 1) : null,
    };

    public void RunCircle(CircleAction action)
    {
        if (action is CircleAction.Pause or CircleAction.Resume) TogglePause?.Invoke();
    }

    public double Build(ContentControl left, ContentControl right)
    {
        var now = DateTime.UtcNow;
        _ring = new ProgressRing();
        _glyph = CompactView.Glyph(_clock.Glyph, 13, Ui.Tokens.Res("Fg"));
        _glyph.Margin = new Thickness(7, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_ring);
        row.Children.Add(_glyph);
        if (_clock.Label.Length > 0)
        {
            var label = Text(_clock.Label, Ui.Tokens.Res("Fg2"));
            label.MaxWidth = 86;
            label.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(label);
        }
        left.Content = row;

        _time = Text("", Ui.Tokens.Res("Fg"));
        right.Content = _time;

        // Size the ear for the widest reading this clock will show, so the pill doesn't jiggle.
        _hours = _clock.IsCountdown ? _clock.Total.TotalHours >= 1 : _clock.Elapsed(now).TotalHours >= 1;
        var widest = Text(_hours ? "00:00:00" : "00:00", Ui.Tokens.Res("Fg"));
        Refresh(now);
        return Math.Ceiling(Math.Max(Measure(row), Measure(widest))) + 2;
    }

    public UIElement? BuildBubble()
    {
        _bubbleRing = new ProgressRing { Width = 22, Height = 22 };
        _bubbleGlyph = CompactView.Glyph(_clock.Glyph, 9, Ui.Tokens.Res("Fg"));
        var grid = new Grid { Width = 22, Height = 22 };
        grid.Children.Add(_bubbleRing);
        grid.Children.Add(_bubbleGlyph);
        Refresh(DateTime.UtcNow);
        return grid;
    }

    /// <summary>A stopwatch passing the hour needs a wider ear, which means a rebuild.</summary>
    public bool NeedsRebuild(DateTime utcNow) => !_clock.IsCountdown && !_hours && _clock.Elapsed(utcNow).TotalHours >= 1;

    public void Refresh(DateTime utcNow)
    {
        double progress = _clock.IsCountdown
            ? Math.Clamp(_clock.Remaining(utcNow).TotalSeconds / Math.Max(1, _clock.Total.TotalSeconds), 0, 1)
            : _clock.Elapsed(utcNow).TotalSeconds % 60 / 60;
        var glyph = _clock.Paused ? PauseGlyph : _clock.Glyph;
        if (_ring is not null) _ring.Value = progress;
        if (_bubbleRing is not null) _bubbleRing.Value = progress;
        if (_glyph is not null) _glyph.Text = glyph;
        if (_bubbleGlyph is not null) _bubbleGlyph.Text = glyph;
        if (_time is not null)
        {
            _time.Text = _clock.Display(utcNow);
            _time.Foreground = Ui.Tokens.Res(_clock.Paused ? "Fg2" : "Fg");
        }
    }

    static TextBlock Text(string text, Brush brush)
    {
        var t = new TextBlock
        {
            Text = text,
            FontFamily = Ui.Tokens.FontRes("ShammyFont"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // Equal-width digits keep the countdown from wobbling every second.
        System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    static double Measure(FrameworkElement e)
    {
        e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return e.DesiredSize.Width;
    }

}
