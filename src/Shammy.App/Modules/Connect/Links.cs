using Shammy.Modules.Productivity;

namespace Shammy.Modules.Connect;

/// <summary>What the Notes/reminders module offers other modules (save a note, set a reminder, read today's list).</summary>
internal interface INotesLink
{
    IReadOnlyList<ReminderItem> Reminders { get; }
    IReadOnlyList<NoteItem> Notes { get; }

    /// <summary>Saves a note and returns the confirmation (the caller shows it, as a command result or a message).</summary>
    CommandResult SaveNote(string text);

    /// <summary>Adds a reminder and returns the confirmation.</summary>
    CommandResult AddReminder(DateTime due, string text);
}

/// <summary>What the clipboard module offers the palette: its history, and copying an entry back.</summary>
internal interface IClipboardLink
{
    IReadOnlyList<Shammy.Core.Clipboard.ClipEntry> Items { get; }
    void CopyBack(Shammy.Core.Clipboard.ClipEntry entry);

    /// <summary>Clears the unpinned history (with the usual Undo pill); false when there was nothing to clear.</summary>
    bool Clear();

    /// <summary>The newest screenshot this session (a snip or a file in Screenshots): when, whether it was a clipboard snip, its size.</summary>
    (DateTime At, bool FromClipboard, int Width, int Height)? LatestScreenshot { get; }

    /// <summary>The newest screenshot as a file (a clipboard-only snip is saved first); null when there is none or it's gone.</summary>
    string? LatestScreenshotFile();
}

/// <summary>What the calendar module offers Settings: whether it's connected, how the sync is, connect and disconnect.</summary>
internal interface ICalendarLink
{
    bool Connected { get; }

    /// <summary>"Couldn't refresh · last updated 10:20 AM" while syncing fails, else null.</summary>
    string? SyncProblem { get; }

    DateTime? FetchedAt { get; }

    /// <summary>Opens the paste-your-address card.</summary>
    void OpenSetup();

    /// <summary>Opens a meeting's call link (Meet, Zoom, Teams only).</summary>
    void Join(Shammy.Core.Calendar.CalendarOccurrence o);

    void Disconnect();
}

/// <summary>What the phone-send module offers others (screenshot card, downloads card).</summary>
internal interface IPhoneLink
{
    /// <summary>Shows the QR card: one file as is, several or a folder as one zip. Reports problems itself.</summary>
    Task SendAsync(IReadOnlyList<string> paths);

    /// <summary>A link or text: over Shammy Island's link while it's online, else as a QR code (long text as a file).</summary>
    void SendText(string text);
}

/// <summary>What the phone link offers Send to phone: Shammy Island over the encrypted link, while it's online.</summary>
internal interface IIslandLink
{
    bool Online { get; }

    /// <summary>Sends a file (up to 50 MB) over the link and says so in the notch; completes when it's sent or failed.</summary>
    Task SendFileAsync(string path);

    /// <summary>A link goes as a link (the phone offers Open), any other text (up to 16,000 characters) to the phone's clipboard.</summary>
    void SendText(string text);
}

/// <summary>What Stage offers others: open with a given text as the script (circle to search's "put it on Stage").</summary>
internal interface IStageLink
{
    /// <summary>Takes the text as the script and opens Stage; returns what to tell the user.</summary>
    CommandResult Present(string text);
}

internal static class LinkExtensions
{
    /// <summary>A command's confirmation as a short notch message (for context-menu actions that aren't commands).</summary>
    public static void ShowResult(this ModuleContext ctx, CommandResult r) =>
        ctx.ShowMessage(r.Glyph, r.Title, r.IsError, seconds: 2.5);
}

/// <summary>
/// The few places modules reach into each other for the features that connect them (reminders on the calendar,
/// "save as note", palette search). Set by the owning module in Start; null while it isn't running.
/// </summary>
internal static class Links
{
    public static INotesLink? Notes { get; set; }

    public static IClipboardLink? Clipboard { get; set; }

    public static IPhoneLink? Phone { get; set; }

    public static IIslandLink? Island { get; set; }

    /// <summary>Text copied in another app, read by the clipboard module after its privacy checks (the phone link shares it).</summary>
    public static event Action<string>? TextCopied;

    internal static void Copied(string text) => TextCopied?.Invoke(text);

    public static ICalendarLink? Calendar { get; set; }

    public static IStageLink? Stage { get; set; }

    /// <summary>Opens a card's detail (or "shelf", "alerts") in the open notch, after any command confirmation (ctx.OpenCard).</summary>
    public static bool OpenCard(ModuleContext ctx, string id) => ctx.OpenCard(id);
}
