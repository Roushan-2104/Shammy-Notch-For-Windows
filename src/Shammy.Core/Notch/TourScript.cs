namespace Shammy.Core.Notch;

/// <summary>One page of the welcome tour: the small tracked label, the big line and the friendly sentence under it.</summary>
public sealed record TourPage(string Kicker, string Title, string Body);

/// <summary>
/// The welcome tour's words (the pet talks, so it's "I" and "me"), with the pet's current name filled in.
/// Five steps, then a goodbye for "finished" or "skipped".
/// </summary>
public static class TourScript
{
    public const int Steps = 5;

    public static TourPage Step(int step, string name) => Math.Clamp(step, 1, Steps) switch
    {
        1 => new("Welcome · 1 of 5", $"Hi, I’m {name}!", "I live up here in your notch and keep the little things close. Quick tour? Thirty seconds, promise."),
        2 => new("Talk · 2 of 5", "Hold Right Alt to talk", "Hold, say it, let go. Try “set a timer for 25 minutes”. English or Hinglish, I get both."),
        3 => new("Type · 3 of 5", "Tap Right Alt to type", "A quick tap opens the palette. Nothing typed yet? I suggest things. Esc brings you back here."),
        4 => new("Circle · 4 of 5", "Circle anything", "Press the Copilot key and draw around a flight, a number, a photo. I’ll peek and offer the next step."),
        _ => new("Everything else · 5 of 5", "Hover me for the rest",
            "Tap ? for all I can do, or hold the middle mouse button for the wheel. Pick a name for me first?"),
    };

    // Not "help": the guide answers that.
    static readonly string[] Phrases = { "tour", "show the tour", "show tour", "show me the tour", "welcome tour", "take the tour", "replay the tour", "how does this work" };

    /// <summary>"tour", "show the tour" (any case, a trailing ?/! ignored): typed or said, it replays the tour.</summary>
    public static bool IsTourCommand(string? text)
    {
        var t = (text ?? "").Trim().TrimEnd('?', '!', '.').Trim().ToLowerInvariant();
        return Phrases.Contains(t);
    }

    public static TourPage Finished(string name) =>
        new("All set", "See you up there!", $"I’m {name}. Hover the notch any time, or hold Right Alt and just say hi.");

    public static TourPage Skipped(string name) =>
        new("Tour skipped", "No worries!", $"Hover the notch whenever you’re curious. {name} is right here. The tray menu can replay this.");
}
