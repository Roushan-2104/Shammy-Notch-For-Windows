using Shammy.Core.Calendar;
using Shammy.Interop;
using Shammy.Modules.Calendar;

namespace Shammy.Shell;

public partial class NotchWindow
{
    // ---- The calendar wing (tap the date in the open island's header) -------------------------------------

    CalendarWing? _wing;
    bool _overWing;

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

    /// <summary>Opens the wing on the island's right; the island itself never moves.</summary>
    internal void PourCalendar()
    {
        if (_mode != NotchMode.Expanded || _hwnd == IntPtr.Zero || CalendarOpen) return;
        if (Native.GetDpiForMonitor(Monitor, 0, out uint dpi, out _) != 0 || dpi == 0) return;
        double px = dpi / 96.0;
        var work = Native.MonitorInfo(Monitor).rcWork;
        double islandRight = _windowPx.X + (Cx + Expanded.Width / 2) * _scale * px;
        double scale = CalendarWingFit.Scale((work.Right - islandRight) / px, _scale);
        if (_wing is null)
        {
            _wing = new CalendarWing();
            _wing.PointerOver += OnWingPointer;
            _wing.FoldWanted += () => FoldCalendar(animate: true);
        }
        // Its own height in its units, so on screen it's exactly as tall (and as round) as the island.
        _wing.Pour(scale, Expanded.Height * _scale / scale, _spec.R * _scale / scale, Shape.Fill, ExcludedFromCapture);
        _wing.PlaceAt((int)Math.Round(islandRight) - 1, _windowPx.Y + (int)Math.Round(_gap * _scale * px));
        Expanded.SetCalendarOpen(true);
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
    }

    void OnWingPointer(bool over)
    {
        if (over == _overWing) return;
        _overWing = over;
        // While the pointer is on the wing the island stays open, as while typing or dragging.
        _holdOpen += over ? 1 : -1;
        if (!over && !Root.IsMouseOver) _hoverOut.Start();
    }
}
