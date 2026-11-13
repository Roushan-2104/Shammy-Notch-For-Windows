using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Calendar;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Modules;
using Shammy.Playback;
using Shammy.Views.Cards;

namespace Shammy.Views;

/// <summary>
/// The open island's home (layout "Now + moment row"): the right-now sentence in the top bar, one big now moment
/// on faint glass (music, a call or meeting going on, a focus or timer, the next meeting, the weather or the clock;
/// <see cref="NowHome"/> decides), and three light numbers beside it — the weather, the battery and one that fits
/// the moment. It reads what the cards already know (media, battery, the calendar, the running clocks, the call)
/// and draws nothing of their tiles. It refreshes once a second while it shows, and not at all while the island
/// is closed or a card is open.
/// </summary>
public partial class ExpandedView
{
    /// <summary>The sentence shows only items at least this important (the time, the weather and the song don't qualify).</summary>
    const int NowMinPriority = 20;

    static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    DispatcherTimer? _homeTimer;
    GlanceFeed? _feed;
    List<GlanceEntry> _nowItems = new();
    string _sentence = "";
    HeroKind _heroKind = HeroKind.Clock;
    string _heroKey = "";
    Action? _heroTick, _heroOpen;
    NowData _now = new();
    readonly NumberRow[] _numbers = new NumberRow[3];
    IReadOnlyList<NowStat> _stats = Array.Empty<NowStat>();
    DateTime? _callSeenUtc;
    (WallPeek? Peek, DateTime At) _weatherPeek;

    void BuildHome()
    {
        for (int i = 0; i < _numbers.Length; i++)
        {
            _numbers[i] = new NumberRow();
            NumbersPanel.Children.Add(_numbers[i].Root);
        }
        NowSentence.MouseLeftButtonUp += (_, e) =>
        {
            if (RunNow()) e.Handled = true;
        };
        AutomationProperties.SetLiveSetting(NowSentence, AutomationLiveSetting.Polite);
        HeroHost.SizeChanged += (_, _) =>
            HeroHost.Clip = new RectangleGeometry(new Rect(0, 0, HeroHost.ActualWidth, HeroHost.ActualHeight), 22, 22);
        HeroHost.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled || _heroOpen is null) return;
            e.Handled = true;
            OpenHero();
        };
    }

    /// <summary>The moment's own card (the music card, Timers, Calendar, Weather), as a click on the moment opens it.</summary>
    void OpenHero() => _heroOpen?.Invoke();

    /// <summary>Works the home out again and redraws what changed; starts the once-a-second refresh while it shows.</summary>
    void RefreshHome(bool rebuildHero = false)
    {
        if (_s is null || _openId is not null) return;
        _now = Gather();
        _heroKind = NowHome.Hero(_now.Signals);
        RefreshSentence();
        RefreshHero(rebuildHero);
        RefreshNumbers();
        RefreshTools();
        SetBodySize(HomeWidth, HomeHeight);
        if (!IsVisible) return;
        _homeTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (IsVisible && _openId is null) RefreshHome();
            else StopHome();
        }, Dispatcher);
        _homeTimer.Start();
    }

    void StopHome() => _homeTimer?.Stop();

    // ---- What is going on -------------------------------------------------------------------------

    sealed class NowData
    {
        public NowSignals Signals = new();
        public CalendarOccurrence? Meeting;
        public Modules.Productivity.RunningClock? Clock;
        public Modules.Productivity.ITimerCardHost? Timers;
        public string CallApp = "";
        public TimeSpan CallElapsed;
        public bool CallMuted;
        public WallPeek? Weather;
        public (string Number, string Words)? Temperature;
        public string City = "";
        public bool HasBattery;
    }

    NowData Gather()
    {
        var d = new NowData();
        var now = DateTime.Now;
        var utc = DateTime.UtcNow;
        var media = _s!.Media;

        // The call: the mic activity when the host shares its activities, else quiet mode's "call".
        bool inCall = QuietState.Describe().Split('+').Contains("call");
        bool timed = false;
        try
        {
            foreach (var a in HostLinks.Activities?.Invoke() ?? Array.Empty<Core.Activities.Activity>())
            {
                if (a.Payload is not IPetActivity pet) continue;
                var state = pet.Describe(utc);
                if (state.Costume != PetCostume.Call) continue;
                inCall = true;
                d.CallApp = state.Label;
                d.CallMuted = state.Muted;
                if (NowText.ParseClock(state.Value) is { } elapsed)
                {
                    d.CallElapsed = elapsed;
                    timed = true;
                }
                break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Reading the call for the island's home failed", ex);
        }
        if (inCall)
        {
            _callSeenUtc ??= utc;
            if (!timed) d.CallElapsed = utc - _callSeenUtc.Value;
        }
        else _callSeenUtc = null;

        // The running clock: the newest countdown going, else the newest paused one (the timer card's choice).
        if (Modules.Connect.Links.Notes is Modules.Productivity.ITimerCardHost timers)
        {
            d.Timers = timers;
            d.Clock = timers.Clocks.LastOrDefault(c => c.IsCountdown && !c.Paused) ?? timers.Clocks.LastOrDefault(c => c.IsCountdown);
        }

        d.Meeting = CalendarAgenda.Next(Modules.Calendar.CalendarModule.Agenda, now);

        // The weather card's own words ("24° Partly cloudy", "Pune · feels like 26°"), asked at most twice a minute.
        if (utc - _weatherPeek.At > TimeSpan.FromSeconds(30)) _weatherPeek = (PeekOf("weather"), utc);
        d.Weather = _weatherPeek.Peek;
        d.Temperature = NowText.SplitTemperature(d.Weather?.Title);
        d.City = d.Weather?.Line.Split(" · ")[0] ?? "";

        try { d.HasBattery = _s.Battery.HasBattery; }
        catch (Exception) { d.HasBattery = false; }

        d.Signals = new NowSignals(
            MusicPlaying: media.HasSession && media.IsPlaying,
            HasMusic: media.HasSession,
            InCall: inCall,
            CountdownLeft: d.Clock?.Remaining(utc),
            CountdownPaused: d.Clock?.Paused ?? false,
            MeetingStartsIn: d.Meeting is { } m ? m.Start - now : null,
            MeetingEndsIn: d.Meeting is { } e ? e.End - now : null,
            HasWeather: d.Temperature is not null,
            HasBattery: d.HasBattery);
        return d;
    }

    WallPeek? PeekOf(string cardId)
    {
        try { return CardById(cardId)?.Peek?.Invoke(); }
        catch (Exception ex)
        {
            Log.Warn($"The {cardId} card's peek failed", ex);
            return null;
        }
    }

    static bool H24 => CardKit.H24;

    /// <summary>A meeting's title, or just "Meeting" while the screen is shared.</summary>
    static string MeetingTitle(CalendarOccurrence m) => PrivacyState.Presenting || m.Title.Trim().Length == 0 ? "Meeting" : m.Title.Trim();

    static string ClockName(Modules.Productivity.RunningClock c) => c.Kind switch
    {
        Modules.Productivity.ClockKind.Focus => "Focus",
        Modules.Productivity.ClockKind.Pomodoro => "Pomodoro",
        Modules.Productivity.ClockKind.Stopwatch => "Stopwatch",
        _ => c.Label.Length > 0 ? c.Label : "Timer",
    };

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpper(s[0], Culture) + s[1..];

    // ---- The right-now sentence -------------------------------------------------------------------

    void RefreshSentence()
    {
        if (_s is null) return;
        if (!_s.Settings.NowLineEnabled)
        {
            NowSentence.Visibility = Visibility.Collapsed;
            _nowItems.Clear();
            _sentence = "";
            return;
        }
        NowSentence.Visibility = Visibility.Visible;
        _feed ??= new GlanceFeed(_s);
        _feed.Providers ??= HostLinks.Glances;
        var items = _feed.Current().Where(e => e.Item.Priority >= NowMinPriority && e.Kind is not (GlanceKind.Music or GlanceKind.Time)).ToList();
        // New messages since Alerts was last opened (never their words: just how many and from where).
        if (_s.Notifications.UnreadCount is var unread and > 0)
        {
            var apps = string.Join(", ", _s.Notifications.History.Take(unread).Select(n => n.AppName).Distinct().Take(2));
            items.Add(new GlanceEntry("alerts", new GlanceItem(unread == 1 ? $"1 new message · {apps}" : $"{unread} new messages · {apps}",
                NotchPalette.Calendar, 35, ShowAlerts)));
        }
        _nowItems = items.OrderByDescending(e => e.Item.Priority).ToList();

        var parts = new List<string>();
        if (_s.Media is { HasSession: true, IsPlaying: true } m) parts.Add("Playing " + m.Title);
        parts.AddRange(_nowItems.Select(e => e.Item.Text));
        // Never "Nothing needs you" while the moment shows a call, a countdown or a meeting: say that instead.
        if (parts.Count == 0 && NowText.HeroLine(_heroKind, _now.CallApp, _now.Meeting is null ? null : MeetingTitle(_now.Meeting), _now.Clock is null ? null : ClockName(_now.Clock)) is { } line)
            parts.Add(line);
        var text = NowText.Sentence(parts);
        if (text == _sentence) return;
        _sentence = text;
        int split = text.IndexOf(" · ", StringComparison.Ordinal);
        NowSentenceText.Inlines.Clear();
        NowSentenceText.Inlines.Add(new Run(text[..split]) { Foreground = CardKit.Ink2 });
        NowSentenceText.Inlines.Add(new Run(text[split..]));
        NowSentence.ToolTip = text;
        NowSentence.Cursor = _nowItems.Any(e => e.Item.OnClick is not null) ? Cursors.Hand : null;
        AutomationProperties.SetName(NowSentence, text);
    }

    /// <summary>Clicking the sentence does what its most important clickable part does (Join, open Alerts…).</summary>
    bool RunNow()
    {
        if (_nowItems.FirstOrDefault(e => e.Item.OnClick is not null)?.Item.OnClick is not { } click) return false;
        try { click(); }
        catch (Exception ex) { Log.Warn("A right-now action failed", ex); }
        return true;
    }

    // ---- The big moment ---------------------------------------------------------------------------

    void RefreshHero(bool rebuild)
    {
        var key = HeroKey();
        if (rebuild || key != _heroKey || HeroHost.Child is null)
        {
            _heroKey = key;
            _heroTick = _heroOpen = null;
            FrameworkElement hero;
            try { hero = BuildHero(); }
            catch (Exception ex)
            {
                Log.Warn($"Building the island's {_heroKind} moment failed", ex);
                hero = ClockHero();
            }
            HeroHost.Child = hero;
            HeroHost.Cursor = _heroOpen is null ? null : Cursors.Hand;
        }
        try { _heroTick?.Invoke(); }
        catch (Exception ex) { Log.Warn("Updating the island's moment failed", ex); }
    }

    /// <summary>What the moment is about; a new key builds it again, anything else only updates its live parts.</summary>
    string HeroKey()
    {
        var m = _s!.Media;
        return _heroKind switch
        {
            HeroKind.Music or HeroKind.PausedMusic =>
                $"music:{m.Title}|{m.Artist}|{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(m.Art)}|{string.Join(",", Theme.PaletteTheme.Colours.Select(c => c.Hex))}",
            HeroKind.Call => $"call:{_now.CallApp}|{(_now.Signals.MeetingNow ? _now.Meeting?.Uid : "")}|{PrivacyState.Presenting}",
            HeroKind.Focus => $"focus:{_now.Clock?.Id}|{_now.Clock?.Paused}",
            HeroKind.Meeting => $"meeting:{_now.Meeting?.Uid}|{_now.Meeting?.Start:O}|{PrivacyState.Presenting}",
            HeroKind.Weather => $"weather:{_now.Weather?.Title}|{_now.Weather?.Line}",
            _ => "clock",
        };
    }

    FrameworkElement BuildHero() => _heroKind switch
    {
        HeroKind.Music or HeroKind.PausedMusic => MusicHero(),
        HeroKind.Call => CallHero(),
        HeroKind.Focus when _now.Clock is not null => FocusHero(_now.Clock),
        HeroKind.Meeting when _now.Meeting is not null => MeetingHero(_now.Meeting),
        HeroKind.Weather => WeatherHero(),
        _ => ClockHero(),
    };

    static TrackedText Kicker(string text) => CardKit.Label(text, CardKit.Ink2, 10.5);

    static TextBlock HeroTitle(string text)
    {
        var t = CardKit.Text(text, 18, FontWeights.Bold);
        t.HorizontalAlignment = HorizontalAlignment.Left;
        return t;
    }

    static TextBlock HeroLine(string text)
    {
        var t = CardKit.Text(text, 12.5, FontWeights.Medium, secondary: true);
        t.HorizontalAlignment = HorizontalAlignment.Left;
        return t;
    }

    /// <summary>Words on the left, an optional control in the bottom-right corner, an optional big number on the right.</summary>
    static Grid Layout(FrameworkElement words, FrameworkElement? corner = null, FrameworkElement? big = null)
    {
        var g = new Grid();
        words.Margin = new Thickness(20, 16, big is null ? 20 : 150, 16);
        words.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(words);
        if (big is not null) g.Children.Add(CardKit.At(big, HorizontalAlignment.Right, VerticalAlignment.Center, 0, 0, 20, 0));
        if (corner is not null) g.Children.Add(CardKit.At(corner, HorizontalAlignment.Right, VerticalAlignment.Bottom, 0, 0, 14, 14));
        return g;
    }

    /// <summary>A song: the cover, title, artist, time and the controls, on a soft glow in the album's (or palette's) colours.</summary>
    FrameworkElement MusicHero()
    {
        var m = _s!.Media;
        var root = new Grid();
        var colours = Theme.PaletteTheme.Colours;
        var a = Theme.PaletteTheme.ToColor(colours[0]);
        var b = Theme.PaletteTheme.ToColor(colours[Math.Min(1, colours.Count - 1)]);
        root.Children.Add(CardKit.At(CardKit.Glow(a, 320, 0.40), HorizontalAlignment.Left, VerticalAlignment.Top, -130, -150));
        root.Children.Add(CardKit.At(CardKit.Glow(b, 280, 0.26), HorizontalAlignment.Right, VerticalAlignment.Bottom, 0, 0, -110, -150));

        var art = new Border
        {
            Width = 88,
            Height = 88,
            CornerRadius = new CornerRadius(14),
            Background = m.Art is { } img ? new ImageBrush(img) { Stretch = Stretch.UniformToFill } : Fields.Music(ArtPalette.Field(m.Art)),
            BorderBrush = CardKit.Hex("#1FFFFFFF"),
            BorderThickness = new Thickness(1),
        };
        root.Children.Add(CardKit.At(art, HorizontalAlignment.Left, VerticalAlignment.Center, 16));

        var kicker = Kicker("");
        kicker.HorizontalAlignment = HorizontalAlignment.Left;
        var words = CardKit.Column(2, kicker, HeroTitle(m.Title), HeroLine(m.Artist));

        var pos = CardKit.Text("", 11, FontWeights.Medium, secondary: true);
        var dur = CardKit.Text("", 11, FontWeights.Medium, secondary: true);
        var fill = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        var rest = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        var bar = new Grid { Height = 3, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        bar.ColumnDefinitions.Add(fill);
        bar.ColumnDefinitions.Add(rest);
        var track = new Border { CornerRadius = new CornerRadius(1.5), Background = CardKit.Hex("#33FFFFFF") };
        Grid.SetColumnSpan(track, 2);
        bar.Children.Add(track);
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = CardKit.Ink });
        var time = new Grid();
        time.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        time.ColumnDefinitions.Add(new ColumnDefinition());
        time.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(bar, 1);
        Grid.SetColumn(dur, 2);
        time.Children.Add(pos);
        time.Children.Add(bar);
        time.Children.Add(dur);

        var play = CardKit.Primary("Play", CardKit.PlayPause(false, 13), () => RunMedia(x => x.TogglePlayPause()), 38);
        var controls = CardKit.Row(6,
            CardKit.Quiet("Previous", CardKit.Glyph(CardKit.Paths.Previous, CardKit.Ink, 12), () => RunMedia(x => x.Previous()), 30),
            play,
            CardKit.Quiet("Next", CardKit.Glyph(CardKit.Paths.Next, CardKit.Ink, 12), () => RunMedia(x => x.Next()), 30));
        controls.VerticalAlignment = VerticalAlignment.Center;
        controls.Margin = new Thickness(12, 0, 0, 0);
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(controls, Dock.Right);
        bottom.Children.Add(controls);
        bottom.Children.Add(time);

        var right = new DockPanel { Margin = new Thickness(120, 12, 16, 12) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        right.Children.Add(bottom);
        words.VerticalAlignment = VerticalAlignment.Center;
        right.Children.Add(words);
        root.Children.Add(right);

        if (CardById("music") is not null) _heroOpen = () => OpenByClick("music");
        bool? shown = null;
        _heroTick = () =>
        {
            var media = _s!.Media;
            bool playing = media.IsPlaying;
            if (shown != playing)
            {
                shown = playing;
                kicker.Text = playing ? "Now playing" : "Paused";
                ((Border)play.Content).Child = CardKit.PlayPause(playing, 13);
                string name = playing ? $"Pause {media.Title}" : $"Play {media.Title}";
                AutomationProperties.SetName(play, name);
                play.ToolTip = name;
                AutomationProperties.SetName(root, $"{(playing ? "Now playing" : "Paused")}: {media.Title}{(media.Artist.Length > 0 ? " by " + media.Artist : "")}");
            }
            var length = media.Duration;
            var at = media.Position;
            bool timed = length > TimeSpan.Zero;
            time.Visibility = timed ? Visibility.Visible : Visibility.Hidden;
            if (!timed) return;
            pos.Text = Core.Cards.MediaTime.Clock(at);
            dur.Text = Core.Cards.MediaTime.Clock(length);
            double f = Math.Clamp(at.TotalSeconds / length.TotalSeconds, 0, 1);
            fill.Width = new GridLength(f, GridUnitType.Star);
            rest.Width = new GridLength(1 - f, GridUnitType.Star);
        };
        return root;
    }

    void RunMedia(Action<MediaService> act)
    {
        if (_s is null) return;
        act(_s.Media);
        Dispatcher.BeginInvoke(() => RefreshHome(), DispatcherPriority.Background);
    }

    /// <summary>A call (or a meeting going on): its name, the app and how long it has run.</summary>
    FrameworkElement CallHero()
    {
        var d = _now;
        bool meeting = d.Signals.MeetingNow && d.Meeting is not null;
        string name = meeting ? MeetingTitle(d.Meeting!) : d.CallApp.Length > 0 ? d.CallApp : "Call";
        string app = d.CallApp.Length > 0 ? d.CallApp : d.Meeting?.Provider ?? "";
        var dot = CardKit.Dot(7, Ui.Tokens.Ok);
        var kicker = Kicker(d.Signals.InCall ? "On a call" : "In a meeting");
        kicker.Margin = new Thickness(7, 0, 0, 0);
        var head = CardKit.Row(0, dot, kicker);
        var line = HeroLine("");
        var words = CardKit.Column(4, head, HeroTitle(name), line);
        var clock = CardKit.Big("", 34);
        var root = Layout(words, big: clock);
        if (meeting) _heroOpen = () => OpenByClick("calendar");
        _heroTick = () =>
        {
            var n = _now;
            var elapsed = n.Signals.InCall ? n.CallElapsed : n.Meeting is { } m ? DateTime.Now - m.Start : TimeSpan.Zero;
            clock.Text = Core.Productivity.TimeFormat.Elapsed(elapsed);
            bool muted = n.Signals.InCall && n.CallMuted;
            dot.Fill = muted ? CardKit.Alarm : Ui.Tokens.Ok;
            line.Text = string.Join(" · ", new[] { app == name ? "" : app, n.Signals.InCall ? (muted ? "Mic muted" : "Mic on") : "" }.Where(s => s.Length > 0));
            AutomationProperties.SetName(root, $"{kicker.Text}: {name}{(app.Length > 0 && app != name ? ", " + app : "")}, {clock.Text}");
        };
        return root;
    }

    /// <summary>A focus session, pomodoro or timer: the time left, when it ends, and Pause or Resume.</summary>
    FrameworkElement FocusHero(Modules.Productivity.RunningClock c)
    {
        var host = _now.Timers;
        var kicker = Kicker(c.Paused ? $"{ClockName(c)} · paused" : ClockName(c));
        kicker.HorizontalAlignment = HorizontalAlignment.Left;
        var digits = CardKit.Poster("", 46);
        digits.HorizontalAlignment = HorizontalAlignment.Left;
        digits.Margin = new Thickness(0, 4, 0, 2);
        var line = HeroLine("");
        var words = CardKit.Column(0, kicker, digits, line);
        string label = c.Paused ? "Resume" : "Pause";
        var corner = host is null ? null : CardKit.PrimaryPill(label, $"{label} {ClockName(c)}", () =>
        {
            host.TogglePause(c);
            Dispatcher.BeginInvoke(() => RefreshHome(), DispatcherPriority.Background);
        }, 30, 12.5);
        var root = Layout(words, corner);
        _heroOpen = () => OpenByClick("timer");
        _heroTick = () =>
        {
            var utc = DateTime.UtcNow;
            digits.Text = c.Display(utc);
            line.Text = c.Paused ? $"{TimeFormatOf(c.Total)} · paused"
                : c.EndsLocal(utc) is { } ends ? $"{TimeFormatOf(c.Total)} · ends at {CalendarText.ClockAmPm(ends, H24)}" : "";
            AutomationProperties.SetName(root, $"{kicker.Text}: {digits.Text} left. {line.Text}");
        };
        return root;
    }

    static string TimeFormatOf(TimeSpan total) => Core.Productivity.TimeFormat.Describe(total);

    /// <summary>The next meeting: when, what, where, and Join (or the agenda).</summary>
    FrameworkElement MeetingHero(CalendarOccurrence m)
    {
        var kicker = Kicker("");
        kicker.HorizontalAlignment = HorizontalAlignment.Left;
        var title = HeroTitle($"{CalendarText.ClockAmPm(m.Start, H24)} {MeetingTitle(m)}");
        var line = HeroLine(string.Join(" · ", new[] { m.Provider ?? "", CalendarText.Length(m) }.Where(s => s.Length > 0)));
        var words = CardKit.Column(4, kicker, title, line);
        // Join goes through the calendar card's own action, which only ever opens a recognised meeting link.
        var join = m.HasLink && PeekOf("calendar")?.Primary is { Label: "Join" } act ? act : null;
        FrameworkElement corner = join is not null
            ? CardKit.PrimaryPill("Join", $"Join {MeetingTitle(m)}", join.Run, 30, 12.5)
            : CardKit.Pill("Agenda", "Open the calendar", () => OpenByClick("calendar"), 30, 12);
        var root = Layout(words, corner);
        _heroOpen = () => OpenByClick("calendar");
        _heroTick = () =>
        {
            kicker.Text = "Next · " + CalendarText.Until(m.Start, m.End, DateTime.Now);
            AutomationProperties.SetName(root, $"{kicker.Text}: {title.Text}, {line.Text}");
        };
        return root;
    }

    /// <summary>The weather: the temperature big, the sky in words, where, and Hourly.</summary>
    FrameworkElement WeatherHero()
    {
        var peek = _now.Weather;
        var (number, condition) = _now.Temperature ?? ("", "");
        var root = new Grid();
        if (peek?.Swatch is { } sky) root.Children.Add(new Border { Background = sky, Opacity = 0.22, IsHitTestVisible = false });
        var temp = CardKit.Poster(number + "°", 52);
        temp.VerticalAlignment = VerticalAlignment.Center;
        var words = CardKit.Column(3, Kicker("Weather"), HeroTitle(condition), HeroLine(peek?.Line ?? ""));
        ((FrameworkElement)words.Children[0]).HorizontalAlignment = HorizontalAlignment.Left;
        words.Margin = new Thickness(18, 0, 0, 0);
        words.VerticalAlignment = VerticalAlignment.Center;
        var row = CardKit.Row(0, temp, words);
        var layout = Layout(row, CardById("weather") is not null ? CardKit.Pill("Hourly", "Hourly weather", () => OpenByClick("weather"), 30, 12) : null);
        root.Children.Add(layout);
        if (CardById("weather") is not null) _heroOpen = () => OpenByClick("weather");
        AutomationProperties.SetName(root, $"Weather: {number} degrees, {condition}. {peek?.Line}");
        return root;
    }

    /// <summary>Nothing else going on: the time and the date.</summary>
    FrameworkElement ClockHero()
    {
        var time = CardKit.Poster("", 52);
        time.HorizontalAlignment = HorizontalAlignment.Left;
        var date = HeroLine("");
        var words = CardKit.Column(2, Kicker("Right now"), time, date);
        ((FrameworkElement)words.Children[0]).HorizontalAlignment = HorizontalAlignment.Left;
        var root = Layout(words);
        _heroTick = () =>
        {
            var now = DateTime.Now;
            time.Text = BuiltInGlances.Time(now, H24).Time + (H24 ? "" : now.ToString(" tt", CultureInfo.InvariantCulture).ToLowerInvariant());
            date.Text = now.ToString("dddd d MMMM", Culture);
            AutomationProperties.SetName(root, $"{time.Text}, {date.Text}");
        };
        return root;
    }

    // ---- The three numbers ------------------------------------------------------------------------

    void RefreshNumbers()
    {
        _stats = NowHome.Stats(_now.Signals);
        for (int i = 0; i < _numbers.Length; i++)
        {
            var row = _numbers[i];
            if (i >= _stats.Count)
            {
                row.Root.Visibility = Visibility.Collapsed;
                continue;
            }
            row.Root.Visibility = Visibility.Visible;
            try { Fill(row, _stats[i]); }
            catch (Exception ex)
            {
                Log.Warn($"The island's {_stats[i]} number failed", ex);
                row.Set(_stats[i], "–", "", "", "");
            }
        }
    }

    void Fill(NumberRow row, NowStat stat)
    {
        var d = _now;
        var now = DateTime.Now;
        switch (stat)
        {
            case NowStat.Weather:
                var (number, condition) = d.Temperature!.Value;
                row.Set(stat, number, "°", condition, d.City);
                break;
            case NowStat.Battery:
                var battery = _s!.Battery;
                int percent = battery.Percent;
                row.Set(stat, percent.ToString(CultureInfo.InvariantCulture), "%", "Battery",
                    NowText.BatteryLine(battery.IsPluggedIn, battery.IsCharging, percent, BatteryLeft()));
                break;
            case NowStat.CallMinutes:
            {
                var elapsed = d.Signals.InCall ? d.CallElapsed : d.Meeting is { } m ? now - m.Start : TimeSpan.Zero;
                var (value, unit) = NowText.Minutes(elapsed, roundUp: false);
                row.Set(stat, value, unit, d.Signals.InCall ? "On the call" : "In the meeting",
                    d.CallApp.Length > 0 ? d.CallApp : d.Meeting is { } mm ? MeetingTitle(mm) : "Call");
                break;
            }
            case NowStat.FocusLeft when d.Clock is { } c:
            {
                var (value, unit) = NowText.Minutes(d.Signals.CountdownLeft ?? TimeSpan.Zero, roundUp: true);
                var utc = DateTime.UtcNow;
                row.Set(stat, value, unit, $"{ClockName(c)} left",
                    c.Paused ? "Paused" : c.EndsLocal(utc) is { } ends ? "Ends at " + CalendarText.ClockAmPm(ends, H24) : "");
                break;
            }
            case NowStat.MeetingIn when d.Meeting is { } m:
            {
                var (value, unit) = NowText.Minutes(m.Start - now, roundUp: true);
                row.Set(stat, value, unit, "Until " + MeetingTitle(m), CalendarText.ClockAmPm(m.Start, H24));
                break;
            }
            case NowStat.Time:
                row.Set(stat, now.ToString(H24 ? "HH:mm" : "h:mm", CultureInfo.InvariantCulture), H24 ? "" : now.ToString("tt", CultureInfo.InvariantCulture).ToLowerInvariant(),
                    "Time", now.ToString("dddd", Culture));
                break;
            default:
                row.Set(NowStat.Date, now.Day.ToString(CultureInfo.InvariantCulture), "", now.ToString("dddd", Culture), now.ToString("MMMM", Culture));
                break;
        }
    }

    /// <summary>Windows' own estimate of the time left on battery, or null while it is still working it out.</summary>
    static TimeSpan? BatteryLeft()
    {
        try
        {
            var left = Windows.System.Power.PowerManager.RemainingDischargeTime;
            return left > TimeSpan.Zero && left < TimeSpan.FromDays(2) ? left : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>One number: light and large with a small unit, and two grey lines that say what it is.</summary>
    sealed class NumberRow
    {
        public readonly Grid Root = new() { Height = 40 };
        readonly Run _value = new(), _unit = new();
        readonly TextBlock _line1, _line2;

        public NowStat Stat { get; private set; }

        public NumberRow()
        {
            // The number at the left, its two lines at the right edge: the row fills the column (no empty band).
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var number = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Light, Foreground = CardKit.Ink };
            number.FontFamily = Ui.Tokens.FontRes("ShammyDisplayFont");
            _value.FontSize = 34;
            _unit.FontSize = 14;
            _unit.Foreground = CardKit.Ink2;
            number.Inlines.Add(_value);
            number.Inlines.Add(new Run(" ") { FontSize = 6 });
            number.Inlines.Add(_unit);
            Root.Children.Add(number);
            _line1 = CardKit.Text("", 11.5, FontWeights.SemiBold, secondary: true);
            _line2 = CardKit.Text("", 11.5, FontWeights.Medium, secondary: true);
            _line1.HorizontalAlignment = _line2.HorizontalAlignment = HorizontalAlignment.Right;
            _line1.TextAlignment = _line2.TextAlignment = TextAlignment.Right;
            var lines = CardKit.Column(1, _line1, _line2);
            lines.VerticalAlignment = VerticalAlignment.Center;
            lines.HorizontalAlignment = HorizontalAlignment.Right;
            lines.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(lines, 1);
            Root.Children.Add(lines);
        }

        public void Set(NowStat stat, string value, string unit, string line1, string line2)
        {
            Stat = stat;
            if (_value.Text != value) _value.Text = value;
            if (_unit.Text != unit) _unit.Text = unit;
            if (_line1.Text != line1) _line1.Text = line1;
            if (_line2.Text != line2) _line2.Text = line2;
            AutomationProperties.SetName(Root, $"{value}{unit} {line1}, {line2}".Trim());
        }

        public string Describe() => $"{Stat}:{_value.Text}{_unit.Text} {_line1.Text}/{_line2.Text}";
    }

    // ---- The state pipe ---------------------------------------------------------------------------

    /// <summary>"home-state": the sentence, the moment, the numbers and the six tools.</summary>
    internal string DescribeHomeTop()
    {
        if (_s is not null && _openId is null) RefreshHome();
        var numbers = _numbers.Take(_stats.Count).Select(n => n.Describe());
        return $"sentence='{(_sentence.Length == 0 ? "off" : _sentence)}' hero={_heroKind} numbers=[{string.Join(" | ", numbers)}] " +
               $"tools=[{string.Join(",", _toolIds)}] moment={Core.Wheel.WheelMoments.Name(_toolMoment)} ({_toolWhy}) " +
               $"view={_openId ?? "home"} size={Width}x{Height}";
    }
}
