namespace Shammy.Modules.CircleMemory;

/// <summary>
/// One circle for the circle memory, handed over by the circle flow after its answer showed
/// (<see cref="HostLinks.RememberCircle"/>). The memory reads the app in front by itself.
/// </summary>
/// <param name="Words">The text read from the circled region; empty for a plain picture.</param>
/// <param name="Title">The island card's big line.</param>
/// <param name="Line">The island card's answer or body line; null when it had none.</param>
/// <param name="Kind">A <c>CircleKind</c> name ("When", "Flight"…) or "Picture", "Translation", "Product", "Question".</param>
/// <param name="Png">The circled region as a PNG (a small copy is kept); null when there is none.</param>
/// <param name="At">When it was circled (local time).</param>
internal sealed record CircleRecord(string Words, string Title, string? Line, string Kind, byte[]? Png, DateTime At);
