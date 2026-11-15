using System.Windows.Threading;
using Shammy.Core.Calendar;
using Shammy.Interop;
using Shammy.Modules.Calendar;

namespace Shammy.Shell;

public partial class NotchWindow
{
    // ---- The calendar wing (tap the date in the open island's header) -------------------------------------

    CalendarWing? _wing;

    /// <summary>How far left of centre the island sits while the wing is out (island units: DIPs before the size scale).</summary>
    double _wingShift;

    double _slideFrom, _slideTo;
    readonly System.Diagnostics.Stopwatch _slideClock = new();
    DispatcherTimer? _slide;
    bool _overWing;

    const double SlideMs = 320;

    internal bool CalendarOpen => _wing is { IsOpen: true };

    /// <summary>For the test pipe.</summary>
    internal string CalendarState() => (_wing?.Describe() ?? "closed") + $" shift={_wingShift:0} header='{Expanded.HeaderClock.Text}'";

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
        return CalendarState();
    }

    /// <summary>"clock-state": the time on each face of the island (it's always there).</summary>
    internal string ClockState() =>
        $"rest='{_restView.Clock.Text}' activity='{_activityView.Clock.Text}' header='{Expanded.HeaderClock.Text}' mode={_mode}";

    void InitCalendarWing()
    {
        Expanded.DateClicked += ToggleCalendar;
        // Opening a view changes the island's size under the neck: the wing folds back first.
        Expanded.ViewChanged += () => FoldCalendar(animate: false);
    }

    internal void ToggleCalendar()
    {
        if (CalendarOpen) FoldCalendar(animate: true);
        else PourCalendar();
    }

    internal void PourCalendar()
    {
        if (_mode != NotchMode.Expanded || _hwnd == IntPtr.Zero || CalendarOpen) return;
        if (Native.GetDpiForMonitor(Monitor, 0, out uint dpi, out _) != 0 || dpi == 0) return;
        var work = Native.MonitorInfo(Monitor).rcWork;
        double px = dpi / 96.0 * _scale;
        var (wingWidth, shift) = CalendarWingFit.Fit(Expanded.Width, (work.Right - work.Left) / px);
        if (_wing is null)
        {
            _wing = new CalendarWing();
            _wing.PointerOver += OnWingPointer;
            _wing.FoldWanted += () => FoldCalendar(animate: true);
        }
        _wing.Pour(wingWidth, _scale, Expanded.Height, Shape.Fill, ExcludedFromCapture);
        Expanded.SetCalendarOpen(true);
        SlideTo(shift);
    }

    internal void FoldCalendar(bool animate)
    {
        if (!CalendarOpen) return;
        _wing!.Fold(animate);
        if (_overWing)
        {
            _overWing = false;
            _holdOpen--;
        }
        Expanded.SetCalendarOpen(false);
        SlideTo(0, animate);
    }

    void OnWingPointer(bool over)
    {
        if (over == _overWing) return;
        _overWing = over;
        // While the pointer is on the wing the island stays open, as while typing or dragging.
        _holdOpen += over ? 1 : -1;
        if (!over && !Root.IsMouseOver) _hoverOut.Start();
    }

    /// <summary>Slides the island (and the wing with it) so the two sit centred together; back to centre with 0.</summary>
    void SlideTo(double shift, bool animate = true)
    {
        _slideFrom = _wingShift;
        _slideTo = shift;
        if (!animate || !Infrastructure.MotionPrefs.Lively)
        {
            _slide?.Stop();
            _wingShift = shift;
            Reposition();
            PlaceWing();
            return;
        }
        _slide ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => SlideStep(), Dispatcher);
        _slideClock.Restart();
        _slide.Start();
    }

    void SlideStep()
    {
        double t = Math.Min(1, _slideClock.Elapsed.TotalMilliseconds / SlideMs);
        double eased = 1 - Math.Pow(1 - t, 3);
        double before = _wingShift;
        _wingShift = _slideFrom + (_slideTo - _slideFrom) * eased;
        if (t >= 1)
        {
            _slide!.Stop();
            _wingShift = _slideTo;
            // Settled: place it properly once (the glass is remade for where it is now).
            Reposition();
            PlaceWing();
            return;
        }
        // Mid-slide only the window moves: remaking the glass every frame would re-read the wallpaper.
        if (Native.GetDpiForMonitor(Monitor, 0, out uint dpi, out _) != 0 || dpi == 0) return;
        int dx = (int)Math.Round((_wingShift - before) * _scale * dpi / 96.0);
        if (dx == 0) return;
        _windowPx = (_windowPx.X - dx, _windowPx.Y);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _windowPx.X, _windowPx.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        PlaceWing();
    }

    /// <summary>The island's shift in device pixels (the window's placement subtracts it).</summary>
    int WingShiftPx(uint dpi) => (int)Math.Round(_wingShift * _scale * dpi / 96.0);

    /// <summary>Puts the wing's neck against the island's right edge, level with its top.</summary>
    void PlaceWing()
    {
        if (_wing is null || !_wing.IsVisible) return;
        if (Native.GetDpiForMonitor(Monitor, 0, out uint dpi, out _) != 0 || dpi == 0) return;
        double px = dpi / 96.0 * _scale;
        int left = _windowPx.X + (int)Math.Round((Cx + Expanded.Width / 2) * px) - 1;
        int top = _windowPx.Y + (int)Math.Round(_gap * px);
        _wing.PlaceAt(left, top);
    }
}
