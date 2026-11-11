using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Modules;

namespace Shammy.Views;

/// <summary>
/// The resting notch (the v2 design, "A + C" plus the pet): Shammy the notch pet on the left and a rotating
/// glance on the right that slides to the next useful bit every few seconds (time and date, the next meeting,
/// weather, a low battery...). Some moods take over the right side with their own text ("Listening…",
/// "Hi!", the app that pinged you). Nothing ticks while it's off screen.
/// </summary>
internal sealed class RestView : Grid
{
    public const double RestWidth = 240, RestHeight = 32;
    const double Padding = 14;
    static readonly TimeSpan Rotation = TimeSpan.FromSeconds(4);

    readonly ShammyServices _services;
    readonly GlanceFeed _feed;
    readonly GlanceRotator _rotator = new();
    readonly Grid _glanceHost = new() { ClipToBounds = true };
    readonly DispatcherTimer _rotate;
    FrameworkElement? _slot;
    GlanceEntry? _shown;
    VisualizerBars? _bars;
    bool _petEnabled = true;

    public RestView(ShammyServices services, GlanceFeed feed)
    {
        _services = services;
        _feed = feed;
        Width = RestWidth;
        Height = RestHeight;
        Background = Brushes.Transparent;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());

        Pet = new ShammyPet(MusicLevel) { Margin = new Thickness(Padding, 0, 0, 0) };
        Children.Add(Pet);
        _glanceHost.Margin = new Thickness(8, 0, Padding, 0);
        SetColumn(_glanceHost, 1);
        Children.Add(_glanceHost);

        Mood = new PetController(services, feed, mood =>
        {
            Pet.SetMood(mood);
            if (IsVisible) Rotate(animate: true, advance: false);
        });
        Pet.PointerNear += Mood.PointerNear;

        _rotate = new DispatcherTimer(DispatcherPriority.Background) { Interval = Rotation };
        _rotate.Tick += (_, _) => Rotate(animate: true, advance: true);
        IsVisibleChanged += (_, _) =>
        {
            Mood.SetActive(IsVisible);
            if (IsVisible)
            {
                Rotate(animate: false, advance: false);
                _rotate.Start();
            }
            else
            {
                _rotate.Stop();
            }
        };
    }

    public ShammyPet Pet { get; }
    public PetController Mood { get; }

    public bool PetEnabled => _petEnabled;

    /// <summary>"pet off": no eyes, and none of the moods' own texts; the glance has the resting notch to itself.</summary>
    public void SetPetEnabled(bool on)
    {
        _petEnabled = on;
        Pet.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _glanceHost.Margin = new Thickness(on ? 8 : Padding, 0, Padding, 0);
        _shown = null;
        if (IsVisible) Rotate(animate: false, advance: false);
        else Swap(null, animate: false);
    }

    public string CurrentText => _shown is null ? "-" : _shown.Item.Text + (_shown.Secondary is null || _shown.Kind == GlanceKind.Badge ? "" : " " + _shown.Secondary);

    double MusicLevel()
    {
        var v = _services.Visualizer;
        return (v.Level(0) + v.Level(1) + v.Level(2)) / 3.0;
    }

    /// <summary>What the mood shows on the right instead of the rotation, or null to rotate.</summary>
    GlanceEntry? MoodSide()
    {
        if (!_petEnabled) return null;
        var mood = Mood.Mood;
        return mood switch
        {
            PetMood.Music => _feed.Current().FirstOrDefault(e => e.Kind == GlanceKind.Music),
            PetMood.Notify => new GlanceEntry("pet-notify",
                new GlanceItem(Mood.NotificationTitle.Length > 0 ? $"{Mood.NotificationTitle} · {Mood.NotificationApp}" : Mood.NotificationApp, ""),
                GlanceKind.Badge, Mood.NotificationApp.Length > 0 ? Mood.NotificationApp[..1].ToUpperInvariant() : "•"),
            PetMood.TimerDone => new GlanceEntry("pet-timer", new GlanceItem("Time's up", NotchPalette.Timer)),
            PetMood.Listening => new GlanceEntry("pet-listen", new GlanceItem("Listening…", NotchPalette.Text), GlanceKind.Listen),
            PetMood.Thinking => new GlanceEntry("pet-think", new GlanceItem("On it…", NotchPalette.Text), GlanceKind.Text),
            PetMood.MeetingSoon => Mood.Meeting,
            PetMood.Tired => new GlanceEntry("pet-tired", new GlanceItem($"{SafePercent()}% · plug in", NotchPalette.Alarm)),
            PetMood.Sleepy => _feed.Current().FirstOrDefault(e => e.Kind == GlanceKind.Time),
            PetMood.Happy => new GlanceEntry("pet-happy", new GlanceItem("Hi!", NotchPalette.Text), GlanceKind.Text),
            _ => null,
        };
    }

    int SafePercent()
    {
        try { return _services.Battery.Percent; }
        catch (Exception) { return 0; }
    }

    /// <summary>Shows the mood's own text, or the next glance item (items are asked for fresh every time).</summary>
    public void Rotate(bool animate, bool advance)
    {
        var next = MoodSide();
        if (next is null)
        {
            var entries = _feed.Current();
            var ids = entries.Select(e => e.Id).ToList();
            int index = advance ? _rotator.Advance(ids) : _rotator.Locate(ids) is >= 0 and var at ? at : _rotator.Advance(ids);
            next = index >= 0 ? entries[index] : null;
        }
        if (next is null)
        {
            Swap(null, animate);
            return;
        }
        // Same item again: update in place, no slide.
        if (_shown is not null && _shown.Id == next.Id && _slot is not null)
        {
            if (_shown.Item.Text != next.Item.Text || _shown.Secondary != next.Secondary || _shown.Item.AccentHex != next.Item.AccentHex)
                Swap(next, animate: false);
            else _shown = next;
            return;
        }
        Swap(next, animate);
    }

    public void RefreshMedia()
    {
        if (_bars is not null && _shown?.Kind == GlanceKind.Music) _bars.Active = _services.Media.IsPlaying;
        Mood.Update();
        if (IsVisible && _shown?.Kind == GlanceKind.Music && _shown.Item.Text != _services.Media.Title) Rotate(animate: false, advance: false);
    }

    void Swap(GlanceEntry? entry, bool animate)
    {
        var old = _slot;
        _shown = entry;
        _bars = null;
        _slot = entry is null ? null : Build(entry);
        if (_slot is not null) _glanceHost.Children.Add(_slot);

        if (!animate)
        {
            if (old is not null) _glanceHost.Children.Remove(old);
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(320);
        if (old is not null)
        {
            var fade = new DoubleAnimation(0, duration) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(fade, 20);
            fade.Completed += (_, _) => _glanceHost.Children.Remove(old);
            old.BeginAnimation(OpacityProperty, fade);
            ((TranslateTransform)old.RenderTransform).BeginAnimation(TranslateTransform.YProperty, Lean(new DoubleAnimation(-8, duration) { EasingFunction = ease }));
        }
        if (_slot is not null)
        {
            _slot.BeginAnimation(OpacityProperty, Lean(new DoubleAnimation(0, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop }));
            ((TranslateTransform)_slot.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                Lean(new DoubleAnimation(8, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop }));
        }
    }

    FrameworkElement Build(GlanceEntry e)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            // Beside the pet the glance sits at the right; on its own it's centred in the notch.
            HorizontalAlignment = _petEnabled ? HorizontalAlignment.Right : HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new TranslateTransform(),
        };
        switch (e.Kind)
        {
            case GlanceKind.Time:
                row.Children.Add(Text(e.Item.Text, "CardText", FontWeights.Bold));
                if (e.Secondary is { } date)
                {
                    var d = Text(date, "CardText2", FontWeights.Medium);
                    d.Margin = new Thickness(7, 0, 0, 0);
                    row.Children.Add(d);
                }
                break;
            case GlanceKind.Music:
            case GlanceKind.Listen:
                bool music = e.Kind == GlanceKind.Music;
                _bars = new VisualizerBars
                {
                    BarCount = music ? 3 : 4,
                    Height = music ? 12 : 14,
                    Source = music ? _services.Visualizer.Level : ListenLevel,
                    Feed = music ? _services.Visualizer : null,
                    Color = music ? ParseColor(e.Item.AccentHex) : ParseColor(NotchPalette.Text),
                    Margin = new Thickness(0, 0, 7, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                _bars.Active = !music || _services.Media.IsPlaying;
                row.Children.Add(_bars);
                row.Children.Add(Text(e.Item.Text, "CardText", FontWeights.SemiBold));
                break;
            case GlanceKind.Badge:
                row.Children.Add(new Border
                {
                    Width = 18,
                    Height = 18,
                    CornerRadius = new CornerRadius(9),
                    Background = Ui.Tokens.Res("AccentBrush"),
                    Margin = new Thickness(0, 0, 7, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = e.Secondary ?? "",
                        FontSize = 10,
                        FontWeight = FontWeights.Bold,
                        Foreground = Ui.Tokens.Res("OnAccentBrush"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                });
                row.Children.Add(Text(e.Item.Text, "CardText", FontWeights.SemiBold));
                break;
            case GlanceKind.Text:
                row.Children.Add(Text(e.Item.Text, "CardText", FontWeights.SemiBold));
                break;
            default:
                row.Children.Add(new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = new SolidColorBrush(ParseColor(e.Item.AccentHex)),
                    Margin = new Thickness(0, 0, 7, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                row.Children.Add(Text(e.Item.Text, "CardText", FontWeights.SemiBold));
                break;
        }
        return row;
    }

    /// <summary>The listening bars only show while the notch rests (the voice bar normally covers it), so a
    /// gentle made-up level is enough; no microphone access here.</summary>
    static float ListenLevel(int i) => (float)(0.5 + 0.5 * Math.Sin(Environment.TickCount64 / 160.0 + i * 1.3));

    static TextBlock Text(string text, string brush, FontWeight weight) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = weight,
        Foreground = Ui.Tokens.Res(brush),
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = 140,
    };

    internal static Color ParseColor(string hex, Color? fallback = null)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
        {
            return fallback ?? Colors.White;
        }
    }

    /// <summary>The notch is a layered, software-rendered window: every frame costs CPU, so small motions run at 20 fps.</summary>
    static AnimationTimeline Lean(AnimationTimeline a)
    {
        Timeline.SetDesiredFrameRate(a, 20);
        return a;
    }
}
