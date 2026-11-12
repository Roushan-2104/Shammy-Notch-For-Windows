using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Mixer;
using Shammy.Infrastructure;
using Shammy.Modules.Palette;
using Shammy.Views.Cards;
using Shammy.Views.Screens;

namespace Shammy.Modules.Mixer;

/// <summary>
/// The Volume view: under the notch's own Back and "Volume" title, a list with "Everything" (the output's
/// volume) first and then a row for each app that is playing, each with its icon, name, slider and level. The
/// output switcher sits on the title's line, at the right. Clicking an icon mutes just that row; dragging a
/// slider sets its level (and unmutes it, as turning something up should).
///
/// It looks at the sound once a second, and only while it is on screen: when the notch hides it the timer
/// stops, the audio worker ends and every row the user had touched is forgotten.
/// </summary>
internal sealed class MixerView : Grid
{
    /// <summary>The notch's Back and title row above this view; the output switcher is lifted into it.</summary>
    const double HeaderBand = 44;

    readonly MixerModule _module;
    readonly MixerBoard _board = new();
    readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    readonly StackPanel _list = new();
    readonly Dictionary<string, MixerRowView> _views = new(StringComparer.Ordinal);
    readonly ContentControl _outputSlot = new()
    {
        Focusable = false,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, -HeaderBand, 0, 0),
    };
    readonly TextBlock _note = CardKit.Text("", 12.5, FontWeights.Medium, secondary: true);
    IMixerSource? _source;
    AudioOutputs? _outputs;
    IReadOnlyList<MixerOutput> _shown = Array.Empty<MixerOutput>();
    IReadOnlyList<MixerRow> _rows = Array.Empty<MixerRow>();
    bool _on, _reading;
    int _run, _changes;

    public MixerView(MixerModule module)
    {
        _module = module;
        _note.Margin = new Thickness(14, 13, 14, 13);
        _note.Visibility = Visibility.Collapsed;
        var panel = new Border
        {
            Background = CardKit.Surface,
            BorderBrush = CardKit.Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Ui.Tokens.DetailRadius),
            Child = WallDetail.Scroll(CardKit.Column(0, _list, _note)),
        };
        Children.Add(panel);
        AutomationProperties.SetName(_outputSlot, "Sound output");
        SetZIndex(_outputSlot, 1);
        Children.Add(_outputSlot);

        _timer.Tick += (_, _) => Look();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Begin();
            else End();
        };
        Unloaded += (_, _) => End();
    }

    /// <summary>On screen and reading.</summary>
    public bool IsOn => _on;

    public IReadOnlyList<MixerRow> Rows => _rows;

    /// <summary>The output sound is playing through, as the switcher names it.</summary>
    public string? OutputLabel => _shown.FirstOrDefault(o => o.Current)?.Label;

    // ---- Coming and going ------------------------------------------------------------------------

    void Begin()
    {
        if (_on) return;
        _on = true;
        _run++;
        _reading = false;
        _list.Children.Clear();
        _views.Clear();
        _source = _module.Source;
        _source.Start();
        _outputs = new AudioOutputs();
        _outputs.ListChanged += ShowOutputs;
        _outputs.RefreshIfStale(force: true);
        ShowOutputs();
        if (_module.Volume is { } volume)
        {
            volume.Changed += OnMasterChanged;
            volume.DefaultDeviceChanged += OnOutputChanged;
        }
        _module.Icons.Loaded += OnIconLoaded;
        Look();
        _timer.Start();
    }

    void End()
    {
        if (!_on) return;
        _on = false;
        _run++;
        _timer.Stop();
        if (_module.Volume is { } volume)
        {
            volume.Changed -= OnMasterChanged;
            volume.DefaultDeviceChanged -= OnOutputChanged;
        }
        _module.Icons.Loaded -= OnIconLoaded;
        if (_outputs is { } outputs)
        {
            outputs.ListChanged -= ShowOutputs;
            outputs.Dispose();
        }
        _outputs = null;
        _source?.Stop();
        _source = null;
        _board.Clear();
    }

    /// <summary>The module is closing, or switched between real apps and stand-ins.</summary>
    public void Shutdown() => End();

    public void Restart()
    {
        if (!_on) return;
        End();
        Begin();
    }

    // ---- Rows ------------------------------------------------------------------------------------

    void Look()
    {
        if (!_on || _reading || _source is null) return;
        _reading = true;
        int run = _run, changes = _changes;
        _source.Read(reading =>
        {
            // Asked for before the view was hidden, or shown again.
            if (run != _run) return;
            _reading = false;
            // The user changed something after this was asked for: it's already out of date, and the next
            // look is at most a second away.
            if (changes != _changes) return;
            Apply(reading);
        });
    }

    /// <summary>Looks now and waits for the answer (the test pipe).</summary>
    public IReadOnlyList<MixerRow> RefreshNow()
    {
        if (!_on || _source is null) return _rows;
        _changes++;
        Apply(_source.ReadNow());
        return _rows;
    }

    void Apply(MixerReading? reading)
    {
        _rows = reading is null
            ? Array.Empty<MixerRow>()
            : _board.Update(reading.MasterLevel, reading.MasterMuted, reading.Sources, useTitles: !PrivacyState.Presenting);

        foreach (var gone in _views.Keys.Where(key => _rows.All(r => r.Key != key)).ToList())
        {
            _list.Children.Remove(_views[gone]);
            _views.Remove(gone);
        }
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (!_views.TryGetValue(row.Key, out var view)) _views[row.Key] = view = new MixerRowView(this, _module);
            if (i >= _list.Children.Count || !ReferenceEquals(_list.Children[i], view))
            {
                _list.Children.Remove(view);
                _list.Children.Insert(i, view);
            }
            view.Show(row, first: i == 0);
        }

        string? note = reading is null ? "No speakers or headphones found"
            : _rows.All(r => r.Kind == MixerRowKind.Everything) ? "No apps are making sound right now"
            : null;
        _note.Text = note ?? "";
        _note.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        _outputSlot.Visibility = reading is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A slider moved (or the pipe set a level).</summary>
    public void SetLevel(string key, float level, bool unmute = false)
    {
        if (_source is null) return;
        Touched(key);
        _source.SetLevel(key, level);
        if (unmute) _source.SetMuted(key, false);
    }

    /// <summary>An icon was clicked (or the pipe muted a row).</summary>
    public void SetMuted(string key, bool muted)
    {
        if (_source is null) return;
        Touched(key);
        _source.SetMuted(key, muted);
    }

    void Touched(string key)
    {
        _changes++;
        // A row the user changed stays until the view closes, even if its app goes quiet.
        if (key != MixerBoard.EverythingKey) _board.Keep(key);
    }

    /// <summary>The user let go: show what Windows says now.</summary>
    public void Settled() => Look();

    /// <summary>The volume keys, the taskbar or another app changed the output's volume.</summary>
    void OnMasterChanged(float level, bool muted)
    {
        if (!_on || !_views.TryGetValue(MixerBoard.EverythingKey, out var view)) return;
        var row = new MixerRow(MixerBoard.EverythingKey, MixerNames.Everything, MixerBoard.Percent(level), muted, MixerRowKind.Everything);
        _rows = _rows.Select(r => r.Key == row.Key ? row : r).ToList();
        view.Show(row, first: true);
    }

    void OnIconLoaded(string path, ImageSource icon)
    {
        foreach (var view in _views.Values) view.IconArrived(path, icon);
    }

    // ---- Outputs ---------------------------------------------------------------------------------

    void OnOutputChanged()
    {
        if (!_on) return;
        _changes++;
        ShowOutputs();
        Look();
    }

    void ShowOutputs()
    {
        if (!_on || _outputs is null) return;
        var listed = _outputs.List();
        // A fresh list takes a moment to arrive; until then, what the switcher showed last time.
        var shown = listed.Count > 0 ? MixerOutputs.Choose(listed.Select(d => (d.Id, d.Name, d.IsDefault))) : _module.LastOutputs;
        if (listed.Count > 0) _module.LastOutputs = shown;
        if (_outputSlot.Content is not null && shown.SequenceEqual(_shown)) return;
        _shown = shown;
        if (shown.Count == 0)
        {
            _outputSlot.Content = null;
            return;
        }
        int current = shown.ToList().FindIndex(o => o.Current);
        _outputSlot.Content = new Segmented(shown.Select(o => o.Label).ToList(), current, i => Switch(shown[i]), minWidth: 56, height: 26, fontSize: 12);
    }

    void Switch(MixerOutput output)
    {
        if (_outputs is not { } outputs) return;
        // Clicked before the fresh list arrived: get it, so the id can be checked against what Windows has now.
        if (outputs.List().Count == 0) outputs.ListNow();
        bool switched = outputs.SetDefault(output.Id);
        if (!switched) _module.Say($"Couldn't switch to {output.Label}");
        // Redraw once this click is over: the mark moves to the new output, or back if it didn't switch.
        Dispatcher.BeginInvoke(() =>
        {
            _shown = Array.Empty<MixerOutput>();
            _outputSlot.Content = null;
            OnOutputChanged();
        }, DispatcherPriority.Background);
    }
}

/// <summary>One row: icon (click to mute), name, slider, level.</summary>
internal sealed class MixerRowView : Grid
{
    public const double RowHeight = 44, IconSize = 26;

    static readonly Brush MutedFill = CardKit.Hex("#55565F");

    readonly MixerView _owner;
    readonly MixerModule _module;
    readonly Border _rule = CardKit.Rule();
    readonly Pressable _icon;
    readonly TextBlock _name = CardKit.Text("", 13, FontWeights.SemiBold);
    readonly TextBlock _level = CardKit.Text("", 12, FontWeights.Medium, secondary: true);
    readonly CardTrack _track = new() { TrackHeight = 5, KnobSize = 0 };
    MixerRow _row = new("", "", 0, false, MixerRowKind.App);
    bool _shownOnce;

    public MixerRowView(MixerView owner, MixerModule module)
    {
        _owner = owner;
        _module = module;
        Height = RowHeight;
        _rule.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(_rule);

        var cells = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        foreach (double width in new[] { IconSize, 10, 150, 10, 0, 10, 44 })
            cells.ColumnDefinitions.Add(new ColumnDefinition { Width = width > 0 ? new GridLength(width) : new GridLength(1, GridUnitType.Star) });
        Children.Add(cells);

        _icon = new Pressable(Brushes.Transparent, Brushes.Transparent)
        {
            Width = IconSize,
            Height = IconSize,
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            PressScale = 0.9,
        };
        _icon.Click += () =>
        {
            bool muted = !_row.Muted;
            _owner.SetMuted(_row.Key, muted);
            _row = _row with { Muted = muted };
            Paint();
            _owner.Settled();
        };
        cells.Children.Add(_icon);

        SetColumn(_name, 2);
        cells.Children.Add(_name);

        _track.VerticalAlignment = VerticalAlignment.Center;
        _track.Changed += value =>
        {
            int percent = MixerBoard.Percent((float)value);
            // Turning a muted app up means "I want to hear it", like the volume keys.
            bool unmute = _row.Muted && percent > 0;
            _owner.SetLevel(_row.Key, (float)value, unmute);
            _row = _row with { Percent = percent, Muted = _row.Muted && !unmute };
            Paint();
        };
        _track.Committed += _ => _owner.Settled();
        SetColumn(_track, 4);
        cells.Children.Add(_track);

        _level.HorizontalAlignment = HorizontalAlignment.Right;
        _level.TextTrimming = TextTrimming.None;
        SetColumn(_level, 6);
        cells.Children.Add(_level);
    }

    public void Show(MixerRow row, bool first)
    {
        bool newIcon = !_shownOnce || row.Kind != _row.Kind || row.IconPath != _row.IconPath || (row.IconPath is null && row.Name != _row.Name);
        // While the pointer holds the slider, the level is the pointer's.
        _row = _track.IsDragging ? row with { Percent = _row.Percent, Muted = _row.Muted } : row;
        _shownOnce = true;
        _rule.Visibility = first ? Visibility.Collapsed : Visibility.Visible;
        _name.Text = row.Name;
        if (newIcon) _icon.Child = Icon(row);
        Paint();
    }

    public void IconArrived(string path, ImageSource icon)
    {
        if (_row.Kind == MixerRowKind.App && string.Equals(_row.IconPath, path, StringComparison.OrdinalIgnoreCase)) _icon.Child = Picture(icon);
    }

    void Paint()
    {
        string what = _row.Kind == MixerRowKind.Everything ? "everything" : _row.Name;
        string action = (_row.Muted ? "Unmute " : "Mute ") + what;
        _icon.ToolTip = action;
        AutomationProperties.SetName(_icon, action);
        _icon.Opacity = _row.Muted ? 0.45 : 1;
        AutomationProperties.SetName(_track, _row.Name + " volume");

        var fill = _row.Muted ? MutedFill : CardKit.Ink;
        if (!ReferenceEquals(_track.Fill, fill))
        {
            _track.Fill = fill;
            _track.InvalidateVisual();
        }
        // Ignored while dragging: the track shows where the pointer is.
        _track.Value = _row.Percent / 100.0;
        _level.Text = _row.Muted ? "Muted" : _row.Percent.ToString(CultureInfo.InvariantCulture);
    }

    UIElement Icon(MixerRow row)
    {
        switch (row.Kind)
        {
            case MixerRowKind.Everything:
                return Badge(CardKit.Ink, CardKit.Icon(CardKit.Paths.Volume, CardKit.OnAccent, 15, 2.2));
            case MixerRowKind.SystemSounds:
                return Badge(CardKit.PillBg, CardKit.Icon(CardKit.Paths.Bell, CardKit.Ink2, 14, 2));
        }
        if (row.IconPath is { } path && _module.Icons.Get(path, iconOnly: true) is { } icon) return Picture(icon);
        // Until the icon arrives, and for apps whose exe can't be read: the name's first letter.
        var letter = CardKit.Text(row.Name.Length > 0 ? row.Name[..1].ToUpperInvariant() : "?", 12, FontWeights.Bold, secondary: true);
        letter.HorizontalAlignment = HorizontalAlignment.Center;
        return Badge(CardKit.PillBg, letter);
    }

    static Border Badge(Brush background, FrameworkElement content)
    {
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Width = IconSize, Height = IconSize, CornerRadius = new CornerRadius(8), Background = background, Child = content };
    }

    static Image Picture(ImageSource icon)
    {
        var image = new Image { Source = icon, Width = IconSize, Height = IconSize, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }
}

/// <summary>
/// The Volume tile. The card is opened on purpose from its chip, so the tile is never on the rail by itself;
/// it exists so the card reads like every other one.
/// </summary>
internal static class MixerTile
{
    public static FrameworkElement Build(Action open)
    {
        var tile = new WallTile { Field = Fields.System() };
        tile.Art.Children.Add(CardKit.At(CardKit.Icon(CardKit.Paths.Volume, CardKit.Ink, 30, 1.8), HorizontalAlignment.Left, VerticalAlignment.Top, 14, 14));
        tile.Rest.Children.Add(WallTile.BottomLeft(CardKit.Poster("Volume", 26)));
        var words = WallTile.Words("Volume mixer", "A slider for each app", out _, out _);
        tile.Lit.Children.Add(CardKit.At(words, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, WallTile.Pad, 0, WallTile.Pad, 10));
        tile.Lit.Children.Add(WallTile.OpenButton("Open volume", open));
        CardKit.Name(tile, "Volume, a slider for each app. Open volume");
        return tile;
    }
}
