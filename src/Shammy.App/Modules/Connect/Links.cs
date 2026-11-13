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

    void Disconnect();
}

/// <summary>What the phone-send module offers others (screenshot card, downloads card).</summary>
internal interface IPhoneLink
{
    /// <summary>Shows the QR card: one file as is, several or a folder as one zip. Reports problems itself.</summary>
    Task SendAsync(IReadOnlyList<string> paths);
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

    public static ICalendarLink? Calendar { get; set; }

    public static IStageLink? Stage { get; set; }

    /// <summary>Opens a card's detail (or "shelf", "alerts") in the open notch, after any command confirmation (ctx.OpenCard).</summary>
    public static bool OpenCard(ModuleContext ctx, string id) => ctx.OpenCard(id);
}
