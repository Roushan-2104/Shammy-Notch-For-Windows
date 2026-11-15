using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Shammy.Core.Notch;
using Shammy.Views.Screens;
using PetFace = Shammy.Views.Screens.PetFace;

namespace Shammy.Modules.Tour;

internal enum TourMode { Tour, Finished, Skipped }

/// <summary>
/// The welcome tour, led by the pet, as a card dropped out of the notch (560×292): the pet in its tile acting out
/// the step (waking and waving, listening, thinking, peeking, happy), a tracked label, a big line, a friendly
/// sentence, the step's extra (a key cap, "Open the palette", the name picker), progress dots and Skip / Back /
/// Next. Next is the one accent. A goodbye page (confetti or a wave) closes it.
/// </summary>
internal sealed class TourCard : Grid
{
    public const double CardWidth = 560, CardHeight = 292;

    public TourCard(int step, TourMode mode, string name, Actions actions)
    {
        Width = CardWidth;
        Height = CardHeight;
        Background = Brushes.Transparent;
        Step = step;
        Mode = mode;

        var page = mode switch
        {
            TourMode.Finished => TourScript.Finished(name),
            TourMode.Skipped => TourScript.Skipped(name),
            _ => TourScript.Step(step, name),
        };
        var mood = mode switch
        {
            TourMode.Finished => FaceMood.Celebrate,
            TourMode.Skipped => FaceMood.Wave,
            _ => step switch { 1 => FaceMood.Wake, 2 => FaceMood.Listen, 3 => FaceMood.Think, 4 => FaceMood.Peek, _ => FaceMood.Happy },
        };

        var grid = new Grid { Margin = new Thickness(22) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Children.Add(grid);

        var tile = new Border
        {
            Width = 120,
            Height = 120,
            CornerRadius = new CornerRadius(18),
            Background = ScreenKit.Tile,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true,
            // Sized so the face's extras (hearts, blush, sparkles) stay inside the rounded tile instead of being cut at its edge.
            Child = new PetFace(2.15, mood) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) },
        };
        grid.Children.Add(tile);

        var words = new Grid();
        words.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        words.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        words.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SetColumn(words, 2);
        grid.Children.Add(words);

        var title = ScreenKit.Display(page.Title, 30, Weight.ExtraBold, -0.03);
        var body = ScreenKit.Para(page.Body, 15, ScreenKit.Fg2, 22.5);
        var text = ScreenKit.Column(8, ScreenKit.Label(page.Kicker), title, body);
        if (mode == TourMode.Tour && Extra(step, name, actions) is { } extra)
        {
            extra.Margin = new Thickness(0, 12, 0, 0);
            text.Children.Add(extra);
        }
        words.Children.Add(text);

        var footer = Footer(step, mode, actions);
        SetRow(footer, 2);
        words.Children.Add(footer);

        ScreenKit.Rise(text, 0, 8);
    }

    public int Step { get; }
    public TourMode Mode { get; }

    /// <summary>What the card's buttons do (the module decides).</summary>
    internal sealed record Actions(Action Next, Action Back, Action Skip, Action Restart, Action OpenPalette, Action<string> PickName, Action Close);

    static FrameworkElement? Extra(int step, string name, Actions actions)
    {
        switch (step)
        {
            case 2:
            {
                // The key cap with a bar filling as if held, on a loop (still with reduced motion).
                var cap = ScreenKit.KeyCap("Right Alt", 34, 13);
                var fill = new Border { Height = 3, Background = ScreenKit.Fg, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(-12, 0, -12, 0) };
                var scale = new ScaleTransform(ScreenKit.ReducedMotion ? 1 : 0, 1);
                fill.RenderTransform = scale;
                if (cap.Child is Grid g) g.Children.Add(fill);
                if (!ScreenKit.ReducedMotion)
                {
                    var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1600), RepeatBehavior = RepeatBehavior.Forever };
                    a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                    a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.7), new SineEase()));
                    a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(1)));
                    // Only while the card shows.
                    cap.IsVisibleChanged += (_, _) => scale.BeginAnimation(ScaleTransform.ScaleXProperty, cap.IsVisible ? a : null);
                }
                return ScreenKit.Row(10, cap, Hint("Try it now: hold, speak, let go."));
            }
            case 3:
            {
                var open = ScreenKit.Secondary("Open the palette", actions.OpenPalette, 34, 17, 13, ScreenKit.Tile);
                return ScreenKit.Row(10, ScreenKit.KeyCap("Right Alt", 34, 13), open);
            }
            case 4:
                return ScreenKit.Row(10, ScreenKit.KeyCap("Copilot", 34, 13, ScreenKit.Icon(ScreenKit.Paths.Copilot, ScreenKit.Fg, 14, 2)),
                    Hint("Press, then draw around anything."));
            case 5:
            {
                var names = PetLooks.NamePresets.ToList();
                var seg = new Segmented(names, names.IndexOf(name), i => actions.PickName(names[i]), minWidth: 64, height: 30, fontSize: 13);
                seg.HorizontalAlignment = HorizontalAlignment.Left;
                return ScreenKit.Column(6, ScreenKit.Label("Pick a name for me"), seg);
            }
        }
        return null;
    }

    static TextBlock Hint(string text) => ScreenKit.Text(text, 12, Weight.Regular, ScreenKit.Fg2);

    static FrameworkElement Footer(int step, TourMode mode, Actions actions)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        if (mode != TourMode.Tour)
        {
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(ScreenKit.Secondary("Restart tour", actions.Restart, 36, 18, 13, ScreenKit.Tile));
            var close = ScreenKit.Primary(mode == TourMode.Finished ? "Let’s go" : "Close", actions.Close, 36, 14, null, 20);
            close.Margin = new Thickness(8, 0, 0, 0);
            right.Children.Add(close);
            grid.Children.Add(right);
            return grid;
        }

        var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, ToolTip = $"Step {step} of {TourScript.Steps}" };
        for (int i = 1; i <= TourScript.Steps; i++)
        {
            dots.Children.Add(new Border
            {
                Height = 6,
                Width = i == step ? 20 : 6,
                CornerRadius = new CornerRadius(3),
                Background = i == step ? ScreenKit.Fg : i < step ? ScreenKit.Fg2 : ScreenKit.HairlineHover,
                Margin = new Thickness(i > 1 ? 5 : 0, 0, 0, 0),
            });
        }
        grid.Children.Add(dots);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (step < TourScript.Steps)
        {
            var skip = new Pressable(Brushes.Transparent, Brushes.Transparent) { Height = 36, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 8, 0) };
            var skipText = ScreenKit.Text("Skip", 13, Weight.SemiBold, ScreenKit.Fg2);
            skip.Child = skipText;
            skip.MouseEnter += (_, _) =>
            {
                skipText.Foreground = ScreenKit.Fg;
                skipText.TextDecorations = TextDecorations.Underline;
            };
            skip.MouseLeave += (_, _) =>
            {
                skipText.Foreground = ScreenKit.Fg2;
                skipText.TextDecorations = null;
            };
            skip.Click += actions.Skip;
            buttons.Children.Add(skip);
        }
        var back = ScreenKit.Secondary("Back", actions.Back, 36, 18, 13, ScreenKit.Tile);
        // Nothing to go back to on the first step: no greyed-out button.
        if (step == 1) back.Visibility = Visibility.Collapsed;
        buttons.Children.Add(back);
        var next = ScreenKit.Primary(step < TourScript.Steps ? "Next" : "Let’s go", actions.Next, 36, 14, null, 20);
        // The arrow sits after the word.
        if (next.Child is StackPanel row)
        {
            var arrow = ScreenKit.Icon(ScreenKit.Paths.Arrow, ScreenKit.OnAccent, 14, 2.6);
            arrow.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(arrow);
        }
        next.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(next);
        grid.Children.Add(buttons);
        return grid;
    }
}
