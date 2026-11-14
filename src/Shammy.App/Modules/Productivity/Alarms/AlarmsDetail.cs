using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shammy.Core.Productivity;
using Shammy.Views.Cards;
using Shammy.Views.Screens;

namespace Shammy.Modules.Productivity.Alarms;

/// <summary>
/// The Alarms card (the card family's shape, 560 wide, as tall as its list): the header says when the next alarm
/// rings and holds "Add" (the one accent) and ⋯ (snooze length, breaking through fullscreen, gentle wake). Each
/// alarm is a glass row: its time in light numbers (the next one in the accent), its name and M–S days, an on/off
/// switch; click the time or name to edit it. "Add" and editing swap the list for an inline editor.
/// </summary>
internal sealed class AlarmsDetail : CardSheet
{
    public const double SheetWidth = 560;

    readonly IAlarmHost _host;
    readonly ContentControl _content = new() { Focusable = false };
    readonly Button _add, _more;
    Editor? _editor;
    bool _options;

    public AlarmsDetail(IAlarmHost host) : base("Alarms", SheetWidth)
    {
        _host = host;
        _more = Action(Sheet.Round("Alarm options", Dots, ToggleOptions, 34, iconSize: 18));
        _add = Action(Sheet.Cap("Add", "Add alarm", () => OpenEditor(null), CardKit.Paths.Plus, accent: true));
        Body.Children.Add(_content);
        // "in 8 h 12 m" only has to be right while it's looked at.
        CardKit.TickWhileVisible(this, TimeSpan.FromSeconds(30), RefreshNext);
        Refresh();
    }

    /// <summary>Three dots (⋯) as strokes.</summary>
    const string Dots = "M5 12h.01 M12 12h.01 M19 12h.01";

    /// <summary>Rebuilds after any change (from here, a command, the pipe or a ringing alarm). An open editor stays.</summary>
    public void Refresh()
    {
        RefreshNext();
        if (_editor is not null) return;
        _content.Content = _options ? Options() : List();
    }

    void RefreshNext()
    {
        var now = DateTime.Now;
        var next = _host.Alarms.Select(_host.UpcomingLocal).Where(t => t is not null).Min();
        TitleText.Text = _editor is not null ? (_editor.IsNew ? "New alarm" : "Edit alarm") : _options ? "Alarm options" : "Alarms";
        Status = _editor is not null || _options ? null
            : next is { } n ? "next " + AlarmText.Short(n - now)
            : _host.Alarms.Count == 0 ? null : "none on";
        _add.Visibility = _editor is null && !_options ? Visibility.Visible : Visibility.Collapsed;
        _more.Visibility = _editor is null ? Visibility.Visible : Visibility.Collapsed;
        _more.ToolTip = _options ? "Back to alarms" : "Alarm options";
    }

    void ToggleOptions()
    {
        _options = !_options;
        Refresh();
    }

    // ---- The list ----------------------------------------------------------------------------------

    FrameworkElement List()
    {
        var alarms = _host.Alarms.OrderBy(a => a.Hour * 60 + a.Minute).ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase).ToList();
        if (alarms.Count == 0)
        {
            var bell = new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = new CornerRadius(19),
                Background = Ink.Tint,
                Child = CardKit.Icon(CardKit.Paths.Bell, Ink.Soft, 18, 2),
            };
            var line = Sheet.Meta("", 14);
            line.Inlines.Add(CardKit.Run("No alarms yet. ", FontWeights.Bold, CardKit.Ink));
            line.Inlines.Add(new System.Windows.Documents.Run("Say “wake me up at 6:30 every weekday”."));
            line.Margin = new Thickness(12, 0, 0, 0);
            return Sheet.Row(0, bell, line);
        }
        var now = DateTime.Now;
        var next = alarms.Where(a => _host.UpcomingLocal(a) is not null).MinBy(a => _host.UpcomingLocal(a));
        var rows = new StackPanel();
        foreach (var a in alarms)
        {
            var row = Row(a, ReferenceEquals(a, next));
            if (rows.Children.Count > 0) row.Margin = new Thickness(0, 8, 0, 0);
            rows.Children.Add(row);
        }
        // Four rows show; more scroll.
        return new ScrollViewer
        {
            Content = rows,
            MaxHeight = 4 * 66 + 3 * 8,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
    }

    FrameworkElement Row(Alarm a, bool next)
    {
        var (time, suffix) = ClockPreference.Parts(a.Hour, a.Minute);
        var big = Sheet.Num(time, 36, next && a.Enabled ? Ink.Soft : CardKit.Ink);
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        timeRow.Children.Add(big);
        if (suffix.Length > 0)
        {
            var am = Sheet.Num(suffix, 15, big.Foreground);
            am.VerticalAlignment = VerticalAlignment.Bottom;
            am.Margin = new Thickness(4, 0, 0, 5);
            timeRow.Children.Add(am);
        }

        var name = Sheet.Word(a.Label.Length > 0 ? a.Label : "Alarm", 14);
        var under = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        if (Note(a) is { } note)
        {
            var t = Sheet.Meta(note, 12);
            if (a.MissedUtc is not null) t.Foreground = Ink.Soft;
            under.Children.Add(t);
        }
        else
        {
            foreach (var (day, letter, dayName) in AlarmText.Week)
            {
                var toggle = new DayToggle(letter, (a.Days & day) != 0, on => _host.SetDays(a, on ? a.Days | day : a.Days & ~day),
                    $"Repeat on {dayName}", 20) { Margin = new Thickness(under.Children.Count > 0 ? 3 : 0, 0, 0, 0) };
                under.Children.Add(toggle);
            }
        }
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        words.Children.Add(name);
        words.Children.Add(under);

        // The time opens the editor (and so does the name).
        var open = new Pressable(Brushes.Transparent, Brushes.Transparent) { PressScale = 0.98, ToolTip = "Edit", Child = timeRow, Width = 132 };
        System.Windows.Automation.AutomationProperties.SetName(open, $"Edit the {AlarmText.Time(a)} alarm");
        open.Click += () => OpenEditor(a);
        name.Cursor = Cursors.Hand;
        name.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenEditor(a);
        };

        var toggleOn = new ToggleSwitch(a.Enabled, on => _host.SetEnabled(a, on), a.Enabled ? "Turn off" : "Turn on")
        {
            VerticalAlignment = VerticalAlignment.Center,
        };
        var grid = new Grid { MinHeight = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(open);
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);
        Grid.SetColumn(toggleOn, 2);
        grid.Children.Add(toggleOn);
        var tile = Sheet.Tile(grid, new Thickness(16, 10, 14, 10));
        if (!a.Enabled) tile.Opacity = 0.6;
        return tile;
    }

    /// <summary>A one-off, snoozed or missed alarm says so instead of showing the days.</summary>
    string? Note(Alarm a)
    {
        var now = DateTime.Now;
        if (a.MissedUtc is not null) return "Missed · " + AlarmText.Repeat(a, now, _host.UpcomingLocal(a));
        if (a.IsSnoozed && a.SnoozedUntilUtc is { } s) return $"Snoozed till {ClockPreference.Format(s.ToLocalTime())}";
        if (a.Days == AlarmDays.None) return AlarmText.Repeat(a, now, _host.UpcomingLocal(a));
        return null;
    }

    // ---- Options (behind ⋯) --------------------------------------------------------------------------

    FrameworkElement Options()
    {
        int[] lengths = { 5, 10, 15 };
        int at = Array.IndexOf(lengths, _host.DefaultSnoozeMinutes);
        var snooze = new Segmented(lengths.Select(m => $"{m} min").ToList(), at, i => _host.SetSnoozeMinutes(lengths[i]), minWidth: 60, height: 28, fontSize: 13);
        var breaks = new ToggleSwitch(_host.BreaksFullscreen, on => _host.BreaksFullscreen = on, "Ring over games, videos and slides");
        var gentle = new ToggleSwitch(_host.GentleWake, on => _host.GentleWake = on, "Gentle wake: the sound swells over 30 seconds");
        var col = new StackPanel();
        col.Children.Add(OptionRow("Snooze", "", snooze));
        col.Children.Add(OptionRow("Break through full screen", "", breaks));
        var last = OptionRow("Gentle wake", "fades in over 30 s", gentle);
        last.Margin = new Thickness(2, 0, 0, 0);
        col.Children.Add(last);
        return col;
    }

    static FrameworkElement OptionRow(string title, string aside, FrameworkElement control)
    {
        var words = Sheet.Row(6, Sheet.Word(title, 14));
        if (aside.Length > 0) words.Children.Add(Sheet.Meta("· " + aside));
        control.VerticalAlignment = VerticalAlignment.Center;
        var row = Sheet.Spread(words, control);
        row.Margin = new Thickness(2, 0, 0, 12);
        return row;
    }

    // ---- The editor ----------------------------------------------------------------------------------

    void OpenEditor(Alarm? a)
    {
        _editor = new Editor(a, Close, Save, a is null ? null : () =>
        {
            _host.Delete(a);
            Close();
        });
        _content.Content = _editor;
        ScreenKit.Rise(_editor, 0, 6);
        RefreshNext();
    }

    void Close()
    {
        _editor = null;
        Refresh();
    }

    void Save(Alarm? a, int hour, int minute, AlarmDays days, string label)
    {
        _editor = null;
        if (a is null) _host.Add(hour, minute, days, label);
        else _host.Edit(a, hour, minute, days, label);
        Refresh();
    }

    /// <summary>
    /// The inline editor: the time as two big steppers (wheel or ±; AM/PM when the clock is 12-hour), a label,
    /// the days, and Save (the accent while editing) / Cancel / Delete.
    /// </summary>
    sealed class Editor : Grid
    {
        readonly Alarm? _alarm;
        AlarmDays _days;
        readonly Stepper _hour, _minute;
        readonly Segmented? _half;
        readonly TextBox _label;
        readonly bool _h12 = !ClockPreference.Is24Hour;

        public Editor(Alarm? alarm, Action cancel, Action<Alarm?, int, int, AlarmDays, string> save, Action? delete)
        {
            _alarm = alarm;
            var now = DateTime.Now;
            int hour = alarm?.Hour ?? 7, minute = alarm?.Minute ?? 0;
            if (alarm is null && now.Hour is >= 4 and < 10) (hour, minute) = (now.AddHours(1).Hour, 0);
            _days = alarm?.Days ?? AlarmDays.None;
            Margin = new Thickness(0);
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            int shown = _h12 ? (hour % 12 == 0 ? 12 : hour % 12) : hour;
            _hour = new Stepper(shown, _h12 ? 1 : 0, _h12 ? 12 : 23, 1, null, v => _h12 ? v.ToString() : v.ToString("00"), 40, 24, 44, wrap: true, "Earlier hour", "Later hour");
            _minute = new Stepper(minute, 0, 59, 5, null, v => v.ToString("00"), 40, 24, 44, wrap: true, "5 minutes earlier", "5 minutes later");
            _minute.WheelStep = 1;
            var colon = ScreenKit.Text(":", 24, Weight.ExtraBold);
            colon.Margin = new Thickness(6, 0, 6, 2);
            var time = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            time.Children.Add(_hour);
            time.Children.Add(colon);
            time.Children.Add(_minute);
            if (_h12)
            {
                _half = new Segmented(new[] { "AM", "PM" }, hour < 12 ? 0 : 1, _ => { }, minWidth: 44, height: 34, fontSize: 13);
                _half.Margin = new Thickness(10, 0, 0, 0);
                time.Children.Add(_half);
            }

            _label = new TextBox
            {
                Style = Application.Current?.TryFindResource("BareTextBox") as Style,
                Text = alarm?.Label ?? "",
                FontSize = 13,
                FontFamily = ScreenKit.Font(Weight.SemiBold).Family,
                FontWeight = FontWeights.SemiBold,
                Foreground = ScreenKit.Fg,
                CaretBrush = ScreenKit.Fg,
                MaxLength = 40,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var placeholder = ScreenKit.Text("Label, like Gym", 13, Weight.Medium, ScreenKit.Fg2);
            placeholder.IsHitTestVisible = false;
            placeholder.Margin = new Thickness(2, 0, 0, 0);
            void SyncPlaceholder() => placeholder.Visibility = _label.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _label.TextChanged += (_, _) => SyncPlaceholder();
            SyncPlaceholder();
            var labelBox = new Border
            {
                Width = 190,
                Height = 36,
                CornerRadius = new CornerRadius(18),
                Background = Sheet.CapFill,
                BorderBrush = Sheet.GlassEdge,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 0, 14, 0),
                Margin = new Thickness(18, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Grid { Children = { placeholder, _label } },
            };
            _label.GotKeyboardFocus += (_, _) => labelBox.BorderBrush = ScreenKit.Fg;
            _label.LostKeyboardFocus += (_, _) => labelBox.BorderBrush = Sheet.GlassEdge;
            _label.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                Commit(save);
            };

            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(time);
            top.Children.Add(labelBox);
            Children.Add(top);

            var repeatText = ScreenKit.Text(AlarmText.Repeat(_days), 12, Weight.Regular, ScreenKit.Fg2);
            repeatText.Margin = new Thickness(12, 0, 0, 0);
            var days = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 14, 0, 14) };
            days.Children.Add(new ScreenKitLabelHolder(ScreenKit.Label("Repeat")) { Margin = new Thickness(0, 0, 12, 0) });
            foreach (var (day, letter, dayName) in AlarmText.Week)
            {
                days.Children.Add(new DayToggle(letter, (_days & day) != 0, on =>
                {
                    _days = on ? _days | day : _days & ~day;
                    repeatText.Text = AlarmText.Repeat(_days);
                }, $"Repeat on {dayName}", 28) { Margin = new Thickness(days.Children.Count > 1 ? 4 : 0, 0, 0, 0) });
            }
            days.Children.Add(repeatText);
            SetRow(days, 1);
            Children.Add(days);

            var buttons = new Grid();
            if (delete is not null)
            {
                var del = ScreenKit.Secondary("Delete", delete);
                del.HorizontalAlignment = HorizontalAlignment.Left;
                buttons.Children.Add(del);
            }
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(ScreenKit.Secondary("Cancel", cancel, 34, -1, 13));
            var saveButton = ScreenKit.Primary(alarm is null ? "Add" : "Save", () => Commit(save), 34, 13, null, 20);
            saveButton.Margin = new Thickness(8, 0, 0, 0);
            right.Children.Add(saveButton);
            buttons.Children.Add(right);
            SetRow(buttons, 2);
            Children.Add(buttons);
        }

        public bool IsNew => _alarm is null;

        void Commit(Action<Alarm?, int, int, AlarmDays, string> save)
        {
            int hour = _hour.Value;
            if (_h12) hour = hour % 12 + (_half?.Index == 1 ? 12 : 0);
            save(_alarm, hour, _minute.Value, _days, _label.Text.Trim());
        }
    }

    /// <summary>Lets a tracked label sit in a panel with a margin and vertical centring.</summary>
    sealed class ScreenKitLabelHolder : Border
    {
        public ScreenKitLabelHolder(FrameworkElement label)
        {
            VerticalAlignment = VerticalAlignment.Center;
            Child = label;
        }
    }
}
