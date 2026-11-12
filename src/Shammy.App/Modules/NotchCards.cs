using System.Windows;

namespace Shammy.Modules;

/// <summary>
/// A card on the open notch's home row (the v2 design): a compact view that is always 176 logical px tall and
/// <see cref="Width"/> wide, which opens in place into a 924×248 detail view with a Back button the notch adds.
/// Cards replace the old 9 tabs; modules register them with <see cref="ModuleContext.AddCard"/>.
/// </summary>
public sealed class NotchCard
{
    /// <summary>Stable id ("music", "calendar", "timer", "weather", "battery", …), also used by test pipes.</summary>
    public required string Id { get; init; }

    /// <summary>Shown as the chip label and the detail view's title.</summary>
    public required string Title { get; init; }

    /// <summary>One of the design's widths: 340 (wide), 260 (medium) or 176 (square).</summary>
    public required double Width { get; init; }

    /// <summary>The compact card, created once. The notch frames it (background, radius, border) — draw content only.</summary>
    public required FrameworkElement Compact { get; init; }

    /// <summary>Builds the 924×248 detail view; called each time the card opens, so it shows fresh data.</summary>
    public required Func<FrameworkElement> Detail { get; init; }

    /// <summary>Lower comes first on the rail (Wall.dc.html's order: weather, calendar, music, timer, system).</summary>
    public int Order { get; init; } = 100;

    /// <summary>
    /// Whether the card earns a place on the home row right now (music only while something plays, a timer only
    /// while one runs…). A card that isn't relevant (or doesn't fit the row) can only be opened from its chip or
    /// a command, so a card that is ever left off the row should set <see cref="Chip"/>. Call
    /// <see cref="ModuleContext.CardsChanged"/> when the answer changes.
    /// </summary>
    public Func<bool> IsRelevant { get; init; } = () => true;

    /// <summary>
    /// Show a chip for this card in the top row, so it can be opened whether or not it's on the home row (cards
    /// people open on purpose, like Clipboard and Mirror, and any card that comes and goes, like Timers). Chips
    /// that don't fit the row are listed under "More". Without a chip, a card off the row is unreachable by mouse.
    /// </summary>
    public bool Chip { get; init; }

    // ---- Focus Wall (Wall.dc.html) -----------------------------------------------------------------
    // The open notch shows relevant cards as one rail of 168×140 tiles. A Compact that is a
    // Views.Cards.WallTile gets the focused/dimmed states; anything else is scaled into a plain tile. A Detail
    // that is a Views.Cards.WallDetail opens with a morph out of its tile and gets the notch's Back on its art
    // panel; any other detail gets a Back + title header row above it (unless it draws its own, see OwnsHeader).

    /// <summary>
    /// How live the card is right now: 0 idle; higher wins the focus when the notch opens (a ringing alarm 100,
    /// a meeting about to start 80, a song playing 50, a running timer 40).
    /// </summary>
    public Func<int> Live { get; init; } = () => 0;

    /// <summary>What the hover peek shows while this card's tile is the focused one; null shows nothing special.</summary>
    internal Func<Shammy.Views.Cards.WallPeek?>? Peek { get; init; }

    /// <summary>The detail draws its own Back and title row (Alarm.dc.html / Settings.dc.html style), so the notch adds none.</summary>
    public bool OwnsHeader { get; init; }
}

/// <summary>The design's colours, one accent per kind of activity (see the v2 mockup).</summary>
public static class NotchPalette
{
    public const string Music = "#FF8A3D", Calendar = "#4C9AFF", Timer = "#5AC8FA", Battery = Ui.Tokens.OkHex,
        Weather = "#FFC233", Purple = "#B48CFF", Alarm = "#FF453A",
        CardBackground = "#121214", CardStroke = "#1F1F23", Text = Ui.Tokens.TextHex, Text2 = Ui.Tokens.Text2Hex;

    public const double CardHeight = 176, DetailWidth = 924, DetailHeight = 248;
}

/// <summary>
/// One line of glanceable info for the resting notch's rotating glance and the hover peek ("31° Pune",
/// "Standup 10:30"). Modules register a provider with <see cref="ModuleContext.AddGlance"/>; it is asked
/// again each time the glance rotates, and returns null when it has nothing worth showing.
/// </summary>
/// <param name="Text">Short, at most ~18 characters ("Standup 10:30").</param>
/// <param name="AccentHex">The dot colour, one of <see cref="NotchPalette"/>.</param>
/// <param name="Priority">Higher shows first and, in the peek, wins the single slot.</param>
/// <param name="OnClick">What clicking it in the peek does (join a meeting, open the card); null = open the notch.</param>
/// <param name="StartsAt">When the thing it's about starts (a meeting), so the pet can tell "about to start" without
/// reading the text; null for anything that isn't an event.</param>
public sealed record GlanceItem(string Text, string AccentHex, int Priority = 0, Action? OnClick = null, DateTimeOffset? StartsAt = null);
