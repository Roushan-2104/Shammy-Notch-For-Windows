using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Shammy.Views.Cards;

/// <summary>
/// A card opened in place, in the card family's shape: one header (Back, the pet's eyes, the card's name, one short
/// status, the card's own buttons on the right) and the card's body under it. The island takes the sheet's size:
/// <see cref="Width"/> as given, the height of whatever the body holds (see <see cref="FitHeight"/>), so a short list
/// makes a short island and an empty card a small one. The notch hides its own top bar while a sheet is open.
/// </summary>
internal class CardSheet : Grid
{
    public const double PadX = 18, PadTop = 16, PadBottom = 18, MaxFit = 560;

    readonly Grid _header = new() { MinHeight = 34 };
    readonly TextBlock _status;

    public CardSheet(string title, double width, string? status = null)
    {
        Width = width;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        var frame = new Grid { Margin = new Thickness(PadX, PadTop, PadX, PadBottom) };
        frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(frame);

        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            _header.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        var back = Sheet.Back(() => BackRequested?.Invoke());
        _header.Children.Add(back);

        var eyes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        eyes.Children.Add(new Border { Width = 6, Height = 10, CornerRadius = new CornerRadius(3), Background = CardKit.Ink });
        eyes.Children.Add(new Border { Width = 6, Height = 10, CornerRadius = new CornerRadius(3), Background = CardKit.Ink, Margin = new Thickness(4, 0, 0, 0) });
        Grid.SetColumn(eyes, 1);
        _header.Children.Add(eyes);

        TitleText = Sheet.Word(title, 16);
        Grid.SetColumn(TitleText, 2);
        _header.Children.Add(TitleText);

        _status = Sheet.Meta("");
        _status.Margin = new Thickness(6, 0, 12, 0);
        Grid.SetColumn(_status, 3);
        _header.Children.Add(_status);
        Status = status;

        Actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(Actions, 4);
        _header.Children.Add(Actions);
        frame.Children.Add(_header);

        Body.Margin = new Thickness(0, 14, 0, 0);
        Grid.SetRow(Body, 1);
        frame.Children.Add(Body);
        System.Windows.Automation.AutomationProperties.SetName(this, title);
    }

    /// <summary>Back was clicked (the notch goes home).</summary>
    public Action? BackRequested { get; set; }

    public TextBlock TitleText { get; }

    /// <summary>The header's short status after the name ("· next in 7 h 12 m"); null or empty hides it.</summary>
    public string? Status
    {
        get => _status.Text.Length == 0 ? null : _status.Text[2..];
        set => _status.Text = string.IsNullOrWhiteSpace(value) ? "" : "· " + value;
    }

    /// <summary>The header's right side: the card's own buttons, 8 px apart.</summary>
    public StackPanel Actions { get; }

    /// <summary>Adds a button (or anything) to the header's right side.</summary>
    public T Action<T>(T element) where T : FrameworkElement
    {
        if (Actions.Children.Count > 0) element.Margin = new Thickness(8, 0, 0, 0);
        element.VerticalAlignment = VerticalAlignment.Center;
        Actions.Children.Add(element);
        return element;
    }

    /// <summary>The card's content under the header.</summary>
    public Grid Body { get; } = new();

    /// <summary>The space between the header and the body (14 by default).</summary>
    public double BodyGap
    {
        get => Body.Margin.Top;
        set => Body.Margin = new Thickness(0, value, 0, 0);
    }

    /// <summary>The height the sheet wants (header, body, padding), capped at <see cref="MaxFit"/>.</summary>
    public double FitHeight { get; private set; }

    /// <summary>The sheet's wanted height changed (a row added, a list filtered): the island follows.</summary>
    public event Action? FitChanged;

    /// <summary>Works out <see cref="FitHeight"/> now (before the sheet is on screen, so the island opens at the right size).</summary>
    public double Fit()
    {
        Measure(new Size(Width, double.PositiveInfinity));
        return FitHeight;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var wanted = base.MeasureOverride(new Size(Width, double.PositiveInfinity));
        double h = Math.Min(MaxFit, Math.Ceiling(wanted.Height));
        if (Math.Abs(h - FitHeight) > 0.5)
        {
            bool first = FitHeight == 0;
            FitHeight = h;
            if (!first) Dispatcher.BeginInvoke(() => FitChanged?.Invoke());
        }
        return new Size(Width, h);
    }
}
