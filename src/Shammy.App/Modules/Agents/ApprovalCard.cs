using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Shammy.Core.Agents;
using Shammy.Infrastructure;
using Shammy.Views.Screens;

namespace Shammy.Modules.Agents;

/// <summary>
/// The card that drops from the notch when Claude Code waits for an OK (460 wide): the pet with wide eyes in its
/// tile, which session and for how long, what it wants in one line, the command or file in a one-line box, and
/// Deny / Allow. It never takes focus; the buttons work by mouse. While the screen is shared the box says so
/// instead of showing the command. Nothing here moves by itself: the module calls <see cref="Tick"/> once a second.
/// </summary>
internal sealed class ApprovalCard : Grid
{
    public const double CardWidth = 460;

    const double WithBox = 170, WithoutBox = 128;

    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    readonly SpacedText _label = ScreenKit.Label("Claude Code");
    readonly TextBlock _waiting = ScreenKit.Text("", 12.5, Weight.Regular, ScreenKit.Fg2);
    readonly TextBlock _title = ScreenKit.Text("", 14, Weight.Bold);
    readonly TextBlock _code = new()
    {
        FontFamily = Mono,
        FontSize = 12.5,
        FontWeight = FontWeights.Medium,
        Foreground = ScreenKit.Fg,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };
    readonly Border _box;
    readonly TextBlock _more = ScreenKit.Text("", 12, Weight.Regular, ScreenKit.Fg2);

    string _detail = "";
    DateTime _since;

    public ApprovalCard(Action allow, Action deny)
    {
        Width = CardWidth;
        Background = Brushes.Transparent;

        var rows = new Grid { Margin = new Thickness(16, 14, 16, 16) };
        rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rows.RowDefinitions.Add(new RowDefinition());
        Children.Add(rows);

        // ---- The pet's tile and the words beside it.
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.Children.Add(new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(14),
            Background = ScreenKit.Tile,
            BorderBrush = ScreenKit.Hairline,
            BorderThickness = new Thickness(1),
            Child = WideEyes(),
        });

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.ClipToBounds = true;
        top.Children.Add(_label);
        _waiting.Margin = new Thickness(12, 0, 0, 0);
        SetColumn(_waiting, 1);
        top.Children.Add(_waiting);

        _title.Margin = new Thickness(0, 3, 0, 0);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(top);
        words.Children.Add(_title);
        SetColumn(words, 2);
        head.Children.Add(words);
        rows.Children.Add(head);

        // ---- The command or path, one line.
        _box = new Border
        {
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(9),
            Background = ScreenKit.Tile,
            BorderBrush = ScreenKit.Hairline,
            BorderThickness = new Thickness(1),
            Child = _code,
        };
        SetRow(_box, 1);
        rows.Children.Add(_box);

        // ---- Deny and Allow; Allow is the one accent control.
        var denyButton = ScreenKit.Secondary("Deny", deny, 30, 15, 12.5, ScreenKit.Tile);
        denyButton.Padding = new Thickness(16, 0, 16, 0);
        var allowButton = ScreenKit.Primary("Allow", allow, 30, 12.5, null, 16);
        System.Windows.Automation.AutomationProperties.SetName(denyButton, "Deny");
        System.Windows.Automation.AutomationProperties.SetName(allowButton, "Allow");
        var buttons = ScreenKit.Row(8, denyButton, allowButton);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.VerticalAlignment = VerticalAlignment.Bottom;
        _more.VerticalAlignment = VerticalAlignment.Bottom;
        _more.Margin = new Thickness(0, 0, 0, 7);
        var foot = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        foot.Children.Add(_more);
        foot.Children.Add(buttons);
        SetRow(foot, 2);
        rows.Children.Add(foot);

        Loaded += (_, _) =>
        {
            PrivacyState.Changed -= OnPrivacy;
            PrivacyState.Changed += OnPrivacy;
            ShowBox();
        };
        Unloaded += (_, _) => PrivacyState.Changed -= OnPrivacy;
    }

    /// <summary>The request on show, so a click answers exactly that one.</summary>
    public PendingAsk? Ask { get; private set; }

    public double CardHeight => _detail.Length > 0 ? WithBox : WithoutBox;

    /// <param name="more">How many other requests wait behind this one.</param>
    public void Show(PendingAsk ask, int more)
    {
        Ask = ask;
        _since = ask.ArrivedUtc;
        _detail = AgentText.Detail(ask.Request);
        _label.Text = AgentText.CardLabel(ask.Request.Cwd).ToUpperInvariant();
        _title.Text = AgentText.Wants(ask.Request.ToolName);
        _more.Text = more <= 0 ? "" : more == 1 ? "1 more waiting" : $"{more} more waiting";
        _box.Visibility = _detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Height = CardHeight;
        ShowBox();
        Tick(DateTime.UtcNow);
    }

    public void Tick(DateTime utcNow)
    {
        string text = AgentText.WaitingFor(utcNow - _since);
        if (_waiting.Text != text) _waiting.Text = text;
    }

    void OnPrivacy(bool _) => Dispatcher.BeginInvoke(ShowBox);

    void ShowBox()
    {
        bool hidden = PrivacyState.Presenting;
        _code.Text = AgentText.Box(_detail, hidden);
        _code.Foreground = hidden ? ScreenKit.Fg2 : ScreenKit.Fg;
        System.Windows.Automation.AutomationProperties.SetName(this,
            $"{_label.Text}. {_title.Text}{(_detail.Length > 0 && !hidden ? ": " + _detail : "")}");
    }

    /// <summary>What the card says right now, for the test pipe.</summary>
    public string Describe() =>
        $"label=\"{_label.Text}\" waiting=\"{_waiting.Text}\" title=\"{_title.Text}\" box=\"{(_box.Visibility == Visibility.Visible ? _code.Text : "")}\" more=\"{_more.Text}\"";

    /// <summary>The pet's wide eyes (the mock's 10×14 ovals), in the eye colour the user picked. Still: no animation.</summary>
    static FrameworkElement WideEyes()
    {
        var eyes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var fill = PetLook.Gradient();
        eyes.Children.Add(new Ellipse { Width = 10, Height = 14, Fill = fill });
        eyes.Children.Add(new Ellipse { Width = 10, Height = 14, Fill = fill, Margin = new Thickness(7, 0, 0, 0) });
        return eyes;
    }
}
