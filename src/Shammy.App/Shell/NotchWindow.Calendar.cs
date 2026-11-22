using Shammy.Core.Calendar;
using Shammy.Interop;
using Shammy.Modules.Calendar;

namespace Shammy.Shell;

public partial class NotchWindow
{
    // ---- The calendar wing (tap the date in the open island's header) -------------------------------------

    CalendarWing? _wing;
    bool _overWing;

    /// <summary>How far left of centre the island sits while the wing is out (DIPs), so the two sit centred together.</summary>
    double _wingShift;

    double _slideFrom, _slideTo;
    readonly System.Diagnostics.Stopwatch _slideClock = new();
    /// <summary>The glide's frames, on the app's frame clock so it moves in step with the screen (null at rest).</summary>
    IDisposable? _slide;
    /// <summary>The window's left edge (device pixels) with no glide, worked out as the glide starts.</summary>
    int _slideBaseX;
    const double SlideMs = 320;

    internal bool CalendarOpen => _wing is { IsOpen: true };

    /// <summary>"calendar open|close|toggle|state|pick 2026-11-18|flip 1": the wing, driven without a pointer.</summary>
    internal string CalendarCommand(string arg)
    {
        var parts = arg.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        switch (parts.Length == 0 ? "state" : parts[0])
        {
            case "open": PourCalendar(); break;
            case "close": FoldCalendar(animate: true); break;
            case "toggle": ToggleCalendar(); break;
            case "pick" when parts.Length > 1 && DateTime.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day):
                return CalendarOpen ? _wing!.Pick(day) : "error: the calendar isn't open";
            case "flip" when parts.Length > 1 && int.TryParse(parts[1], out var months):
                return CalendarOpen ? _wing!.FlipBy(months) : "error: the calendar isn't open";
            case "state": break;
            default: return "error: calendar open|close|toggle|state|pick <yyyy-mm-dd>|flip <n>";
        }
        return (_wing?.Describe() ?? "closed") + $" island={_windowPx.X}";
    }

    /// <summary>"clock-state": the time on each face of the island (it's always there).</summary>
    internal string ClockState() =>
        $"rest='{_restView.Clock.Text}' activity='{(_petShowing ? _activityView.Clock : ((Views.CompactView)_compactActive.El).Clock).Text}' header='{Expanded.HeaderClock.Text}' mode={_mode}";

    void InitCalendarWing()
    {
        Expanded.DateClicked += ToggleCalendar;
        // Opening a view changes the island's size beside the wing: the wing folds back first.
        Expanded.ViewChanged += () => FoldCalendar(animate: false);
    }

    internal void ToggleCalendar()
    {
        if (CalendarOpen) FoldCalendar(animate: true);
        else PourCalendar();
    }

    /// <summary>
    /// Opens the wing on the island's right while the island glides left by half the wing's width, so the two end up
    /// centred together and the calendar lands near the middle of the screen, close to the pointer.
    /// </summary>
    internal void PourCalendar()
    {
        if (_mode != NotchMode.Expanded || _hwnd == IntPtr.Zero || CalendarOpen) return;
        if (Native.GetDpiForMonitor(Monitor, 0, out uint dpi, out _) != 0 || dpi == 0) return;
        double px = dpi / 96.0;
        var work = Native.MonitorInfo(Monitor).rcWork;
        // The room both sides of the centred pair share: the screen less the island (one margin is the Fit's own).
        double room = (work.Right - work.Left) / px - CalendarWingFit.Margin - Expanded.Width * _scale;
        double scale = CalendarWingFit.Scale(room, _scale);
        if (_wing is null)
        {
            _wing = new CalendarWing();
            _wing.PointerOver += OnWingPointer;
            _wing.FoldWanted += () => FoldCalendar(animate: true);
        }
        // Placed before it shows, so it never flashes at the window's default spot.
        _wing.PlaceAt(_windowPx.X + (int)Math.Round((Cx + Expanded.Width / 2) * _scale * px) - 1, _windowPx.Y + (int)Math.Round(_gap * _scale * px));
        // Its own height in its units, so on screen it's exactly as tall (and as round) as the island.
        _wing.Pour(scale, Expanded.Height * _scale / scale, _spec.R * _scale / scale, Shape.Fill, ExcludedFromCapture);
        PlaceWing();
        Expanded.SetCalendarOpen(true);
        SlideTo((CalendarWingFit.Neck + CalendarWingFit.Width) * scale / 2);
    }

    /// <param name="withIsland">The island itself is changing shape (closing, a card): the wing goes at once instead of
    /// fading beside an island that's already shrinking away from it; the glide back still animates.</param>
    internal void FoldCalendar(bool animate, bool withIsland = false)
    {
        if (!CalendarOpen) return;
        _wing!.Fold(animate && !withIsland);
        if (_overWing)
        {
            _overWing = false;
            _holdOpen--;
        }
        Expanded.SetCalendarOpen(false);
        SlideTo(0, animate);
    }

    /// <summary>Glides the island (the wing riding along) to <paramref name="shift"/> DIPs left of centre.</summary>
    void SlideTo(double shift, bool animate = true)
    {
        _slideFrom = _wingShift;
        _slideTo = shift;
        if (!animate || !Infrastructure.MotionPrefs.Lively || Native.GetDpiForMonitor(Monitor, 0, out uint dpi, out _) != 0 || dpi == 0)
        {
            StopSlide();
            _wingShift = shift;
            Reposition();
            return;
        }
        _slideBaseX = _windowPx.X + WingShiftPx(dpi);
        _slideClock.Restart();
        _slide ??= Motion.Animator.Subscribe(_ => SlideStep());
    }

    void StopSlide()
    {
        _slide?.Dispose();
        _slide = null;
    }

    /// <summary>
    /// One screen frame of the glide: the island's window and the calendar's move together (one call, same frame),
    /// and the glass is held still against the wallpaper while they move, so it doesn't jump when it's remade.
    /// </summary>
    void SlideStep()
    {
        double t = Math.Min(1, _slideClock.Elapsed.TotalMilliseconds / SlideMs);
        _wingShift = _slideFrom + (_slideTo - _slideFrom) * (1 - Math.Pow(1 - t, 3));
        if (t >= 1)
        {
            StopSlide();
            _wingShift = _slideTo;
            // Settled: placed properly once (the wing follows), so the glass is remade for where it is now.
            Reposition();
            return;
        }
        if (Native.GetDpiForMonitor(Monitor, 0, out uint dpi, out _) != 0 || dpi == 0) return;
        int x = _slideBaseX - WingShiftPx(dpi);
        if (x != _windowPx.X)
        {
            _windowPx = (x, _windowPx.Y);
            double px = dpi / 96.0;
            if (_wing is { IsVisible: true } w)
                Native.MoveTogether((_hwnd, _windowPx.X, _windowPx.Y),
                    (w.Handle, _windowPx.X + (int)Math.Round((Cx + Expanded.Width / 2) * _scale * px) - 1, _windowPx.Y + (int)Math.Round(_gap * _scale * px)));
            else Native.MoveTogether((_hwnd, _windowPx.X, _windowPx.Y));
        }
        ApplyLook();
    }

    /// <summary>The island's glide in device pixels (the window's placement subtracts it).</summary>
    int WingShiftPx(uint dpi) => (int)Math.Round(_wingShift * dpi / 96.0);

    /// <summary>Puts the wing's neck against the island's right edge, level with its top.</summary>
    void PlaceWing()
    {
        if (_wing is not { IsOpen: true } && _wing?.IsVisible != true) return;
        if (Native.GetDpiForMonitor(Monitor, 0, out uint dpi, out _) != 0 || dpi == 0) return;
        double px = dpi / 96.0;
        _wing!.PlaceAt(_windowPx.X + (int)Math.Round((Cx + Expanded.Width / 2) * _scale * px) - 1, _windowPx.Y + (int)Math.Round(_gap * _scale * px));
    }

    void OnWingPointer(bool over)
    {
        // Back onto a wing that's already folding: no hold, since nothing would be left to release it.
        if (over && !CalendarOpen) return;
        if (over == _overWing) return;
        _overWing = over;
        // While the pointer is on the wing the island stays open, as while typing or dragging.
        _holdOpen += over ? 1 : -1;
        if (!over && !Root.IsMouseOver) _hoverOut.Start();
    }
}
