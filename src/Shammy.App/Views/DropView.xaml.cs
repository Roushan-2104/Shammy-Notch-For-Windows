using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Shammy.Core.DropActions;
using Shammy.Modules.DropActions;
using Shammy.Motion;
using Shammy.Shell;

namespace Shammy.Views;

/// <summary>
/// What the notch shows while files are dragged over it: a chip for what's being dragged, and four Focus Wall tiles:
/// Shelf, Send to phone, Compress (.zip) and Convert (PNG ↔ JPG). The tile under the pointer is the focused one
/// (accent outline, soft glow, a springy lift) while the others dim; <see cref="TakeTarget"/> tells the window
/// where the files landed.
/// </summary>
public partial class DropView : UserControl
{
    /// <summary>How far the focused tile grows, and how far the others fade (the Wall's ~55%).</summary>
    const double FocusScale = 1.04, DimOpacity = 0.58;

    sealed record Target(DropAction Action, string Title, Grid Root, Border Tile, TrackedText Subtitle, string Normal,
        ScaleTransform Scale, SpringValue Grow, SpringValue Fade);

    readonly List<Target> _targets = new();
    readonly TextBlock _chipText = WallParts.Text("", 13, FontWeights.SemiBold);
    readonly TextBlock _chipSize = WallParts.Text("", 12, FontWeights.Normal, "WallText2");
    readonly TextBlock _chipIcon = WallParts.Glyph("", 15);
    /// <summary>The Wall's focus glow (accent, soft, no offset); shared and frozen since one tile wears it at a time.</summary>
    static readonly DropShadowEffect Glow = MakeGlow();
    IDataObject? _data;
    string[] _files = Array.Empty<string>();
    DropAction? _hovered;
    IDisposable? _frames;

    public DropView()
    {
        InitializeComponent();
        // Fits the window: the design's 924 where the notch window is wide enough, else its width less a margin.
        Width = Math.Min(924, NotchWindow.WindowWidth - 40);
        Build();
        PreviewDragEnter += OnDragOver;
        PreviewDragOver += OnDragOver;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) Settle();
        };
    }

    /// <summary>Files dropped on Send to phone, Compress or Convert (Shelf drops keep the window's own path).</summary>
    internal event Action<DropAction, string[]>? ActionDropped;

    /// <summary>The target under the pointer, if any.</summary>
    internal DropAction? Hovered => _hovered;

    /// <summary>Where the files landed (the hovered target), clearing the highlight for the next drag.</summary>
    internal DropAction? TakeTarget()
    {
        var t = _hovered;
        Reset();
        return t;
    }

    internal void Reset()
    {
        _data = null;
        SetHovered(null);
    }

    internal void RaiseDropped(DropAction action, string[] files) => ActionDropped?.Invoke(action, files);

    void Build()
    {
        var header = new DockPanel { Height = 36, Margin = new Thickness(0, 0, 0, 12), LastChildFill = false };
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _chipIcon.Margin = new Thickness(0, 0, 9, 0);
        chipRow.Children.Add(_chipIcon);
        chipRow.Children.Add(_chipText);
        _chipSize.Margin = new Thickness(6, 0, 0, 0);
        chipRow.Children.Add(_chipSize);
        // The thing in your hand: a tile-coloured chip, tilted a touch as if held.
        var chip = new Border
        {
            Height = 36,
            CornerRadius = new CornerRadius(WallParts.SmallRadius),
            Background = WallParts.Res("WallTile"),
            BorderBrush = WallParts.Res("WallHairline"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 0, 14, 0),
            MaxWidth = 380,
            Child = chipRow,
            RenderTransform = new RotateTransform(-2),
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        header.Children.Add(chip);
        var dropTo = WallParts.Label("Drop to");
        dropTo.Margin = new Thickness(16, 0, 0, 0);
        header.Children.Add(dropTo);
        var cancel = WallParts.Text("Release outside the notch to cancel", 11.5, FontWeights.Normal, "WallText2");
        DockPanel.SetDock(cancel, Dock.Right);
        header.Children.Add(cancel);
        DockPanel.SetDock(header, Dock.Top);
        Root.Children.Add(header);

        var grid = new Grid();
        for (int i = 0; i < 4; i++)
        {
            if (i > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        AddTarget(grid, 0, DropAction.Shelf, "", "Shelf", "Keep for later");
        AddTarget(grid, 2, DropAction.Phone, "", "Send to phone", "QR · same Wi-Fi");
        AddTarget(grid, 4, DropAction.Compress, "", "Compress", ".zip");
        AddTarget(grid, 6, DropAction.Convert, "", "Convert", "PNG ↔ JPG");
        Root.Children.Add(grid);
    }

    void AddTarget(Grid grid, int column, DropAction action, string glyph, string title, string subtitle)
    {
        var root = new Grid { Background = Brushes.Transparent };
        var icon = WallParts.Glyph(glyph, 22);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.VerticalAlignment = VerticalAlignment.Top;
        var name = WallParts.Text(title, 17, FontWeights.Bold);
        var sub = WallParts.Label(subtitle);
        sub.HorizontalAlignment = HorizontalAlignment.Left;
        sub.Margin = new Thickness(0, 5, 0, 0);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        words.Children.Add(name);
        words.Children.Add(sub);
        var content = new Grid { Margin = new Thickness(14, 13, 14, 13) };
        content.Children.Add(icon);
        content.Children.Add(words);

        var scale = new ScaleTransform(1, 1);
        var tile = new Border
        {
            CornerRadius = new CornerRadius(WallParts.Radius),
            Background = WallParts.Res("WallTile"),
            BorderThickness = new Thickness(1.5),
            Child = content,
            RenderTransform = scale,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        root.Children.Add(tile);
        Grid.SetColumn(root, column);
        grid.Children.Add(root);
        var t = new Target(action, title, root, tile, sub, subtitle, scale, new SpringValue(1, 0.001), new SpringValue(1, 0.002));
        _targets.Add(t);
        Paint(t, false);
        AutomationProperties.SetName(root, $"Drop on {title}, {subtitle}");
    }

    /// <summary>The focused tile wears the accent outline and glow; the others keep a hairline.</summary>
    void Paint(Target t, bool hot)
    {
        t.Tile.BorderBrush = WallParts.Res(hot ? "WallAccent" : "WallHairline");
        t.Tile.Effect = hot ? Glow : null;
        Panel.SetZIndex(t.Root, hot ? 1 : 0);
        AutomationProperties.SetItemStatus(t.Root, hot ? "Selected" : "");
    }

    static DropShadowEffect MakeGlow()
    {
        var glow = new DropShadowEffect { Color = Color.FromRgb(0xFF, 0x8A, 0x3D), BlurRadius = 24, ShadowDepth = 0, Opacity = 0.32 };
        glow.Freeze();
        return glow;
    }

    void SetHovered(DropAction? action)
    {
        if (action == _hovered) return;
        _hovered = action;
        bool motion = Infrastructure.MotionPrefs.Lively && IsVisible;
        foreach (var t in _targets)
        {
            bool hot = t.Action == action;
            Paint(t, hot);
            double grow = hot ? FocusScale : 1;
            double fade = action is null || hot ? 1 : DimOpacity;
            if (motion)
            {
                // A quick spring with a little overshoot on the focused tile; the dim is a plain fast fade.
                t.Grow.AnimateTo(grow, 0.32, 0.62);
                t.Fade.AnimateTo(fade, 0.16, 1);
            }
            else
            {
                t.Grow.Snap(grow);
                t.Fade.Snap(fade);
            }
        }
        if (motion) _frames ??= Animator.Subscribe(_ => Apply());
        else Apply();
    }

    void Apply()
    {
        bool settled = true;
        foreach (var t in _targets)
        {
            t.Scale.ScaleX = t.Scale.ScaleY = t.Grow.Value;
            t.Root.Opacity = t.Fade.Value;
            settled &= t.Grow.IsSettled && t.Fade.IsSettled;
        }
        if (settled) StopFrames();
    }

    /// <summary>Hidden mid-spring: jump to the end so nothing keeps the frame clock running.</summary>
    void Settle()
    {
        foreach (var t in _targets)
        {
            t.Grow.Snap(t.Grow.Target);
            t.Fade.Snap(t.Fade.Target);
        }
        Apply();
        StopFrames();
    }

    void StopFrames()
    {
        _frames?.Dispose();
        _frames = null;
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        if (!ReferenceEquals(e.Data, _data))
        {
            _data = e.Data;
            Describe(e.Data);
        }
        DropAction? over = null;
        foreach (var t in _targets)
        {
            var p = e.GetPosition(t.Root);
            if (p.X >= 0 && p.Y >= 0 && p.X <= t.Root.ActualWidth && p.Y <= t.Root.ActualHeight) over = t.Action;
        }
        SetHovered(over);
        // Not handled: the window's own drag handler still decides the cursor and the drop.
    }

    void Describe(IDataObject data)
    {
        _files = data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] f ? f : Array.Empty<string>();
        _chipText.Text = _files.Length switch
        {
            0 => "File",
            1 => Path.GetFileName(_files[0].TrimEnd('\\', '/')),
            _ => $"{_files.Length} items",
        };
        long size = DropFiles.Size(_files);
        _chipSize.Text = size > 0 ? "· " + DropPlan.Size(size) : "";
        _chipIcon.Text = ((char)(_files.Length == 1 && Directory.Exists(_files[0]) ? 0xE8B7 : _files.Length > 1 ? 0xE8F1 : 0xE8A5)).ToString();
        bool images = _files.Length > 0 && _files.All(DropPlan.IsImage);
        bool anyImage = _files.Any(DropPlan.IsImage);
        foreach (var t in _targets)
        {
            string text = t.Normal;
            if (t.Action == DropAction.Convert)
                text = images ? (_files.All(p => DropPlan.ConvertExtension(p) == ".png") ? "to PNG" : _files.All(p => DropPlan.ConvertExtension(p) == ".jpg") ? "to JPG" : "PNG ↔ JPG")
                    : anyImage ? "Images only" : "Images only for now";
            if (t.Action == DropAction.Phone && (_files.Length > 1 || _files.Any(Directory.Exists))) text = "As a .zip · QR";
            t.Subtitle.Text = text;
            AutomationProperties.SetName(t.Root, $"Drop on {t.Title}, {text}");
        }
    }
}
