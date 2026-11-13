using System.Text;
using Shammy.Infrastructure;

namespace Shammy.Modules.SelectionTools;

/// <summary>
/// "Send it anywhere" for a circle: keep the words as a note, put what was circled on the Shelf, send it to the phone,
/// or read it on Stage. Text goes as text (a .txt for the Shelf and the phone); a picture goes as a PNG. Files are kept
/// in Shammy's own "circles" folder and cleared after a week. Every target is optional: one whose module is off is
/// simply not offered.
/// </summary>
internal static class CircleSend
{
    static ModuleContext? _ctx;

    public static string Folder => Path.Combine(Paths.Root, "circles");

    /// <summary>Set by the selection module when it starts; null while it's off (then nothing is offered).</summary>
    internal static void Use(ModuleContext? ctx)
    {
        _ctx = ctx;
        if (ctx is null) return;
        // Anything still on the Shelf stays, however old.
        var onShelf = ctx.Services.Shelf.Items.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ = Task.Run(() => CleanOld(onShelf));
    }

    public static bool Ready => _ctx is not null;

    public static bool CanNote(SelectionInfo s) => Connect.Links.Notes is not null && HasText(s);
    public static bool CanShelf(SelectionInfo s) => _ctx is not null && (HasText(s) || s.Png is { Length: > 0 });
    public static bool CanPhone(SelectionInfo s) => Connect.Links.Phone is not null && CanShelf(s);
    public static bool CanStage(SelectionInfo s) => Connect.Links.Stage is not null && HasText(s) && Words(s.Text) >= 12;

    static bool HasText(SelectionInfo s) => !string.IsNullOrWhiteSpace(s.Text) && !s.IsImage;

    static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    public static CommandResult ToNotes(SelectionInfo s)
    {
        if (Connect.Links.Notes is not { } notes || !HasText(s)) return new CommandResult("", "Notes are off", IsError: true);
        return notes.SaveNote(s.Text.Trim());
    }

    public static CommandResult ToShelf(SelectionInfo s)
    {
        if (_ctx is null) return new CommandResult("", "Not ready", IsError: true);
        var path = Save(s);
        if (path is null) return new CommandResult("", "Couldn't save that", IsError: true);
        _ctx.Services.Shelf.Add(new[] { path });
        return new CommandResult("", "On the Shelf", Path.GetFileName(path));
    }

    public static async Task<CommandResult> ToPhoneAsync(SelectionInfo s)
    {
        if (Connect.Links.Phone is not { } phone) return new CommandResult("", "Send to phone is off", IsError: true);
        var path = Save(s);
        if (path is null) return new CommandResult("", "Couldn't save that", IsError: true);
        await phone.SendAsync(new[] { path });
        return new CommandResult("", "Scan to send to your phone", Path.GetFileName(path));
    }

    public static CommandResult ToStage(SelectionInfo s)
    {
        if (Connect.Links.Stage is not { } stage || !HasText(s)) return new CommandResult("", "Stage is off", IsError: true);
        return stage.Present(s.Text);
    }

    /// <summary>The circle as a file: its words in a .txt, or the picture as a PNG. Null if it couldn't be written.</summary>
    internal static string? Save(SelectionInfo s)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            if (HasText(s))
            {
                var path = Unique($"Circled {Name(s.Text)} {stamp}", ".txt");
                File.WriteAllText(path, s.Text.Trim() + Environment.NewLine, new UTF8Encoding(false));
                return path;
            }
            if (s.Png is { Length: > 0 } png)
            {
                var path = Unique($"Circled {stamp}", ".png");
                File.WriteAllBytes(path, png);
                return path;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Saving a circle failed", ex);
        }
        return null;
    }

    /// <summary>The first few words, safe as a file name ("Circled Dentist Fri 21 Nov").</summary>
    internal static string Name(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(5);
        var name = new string(string.Join(' ', words).Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        if (name.Length > 40) name = name[..40].TrimEnd();
        return name.Length == 0 ? "text" : name;
    }

    static string Unique(string name, string ext)
    {
        var path = Path.Combine(Folder, name + ext);
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(Folder, $"{name} ({n}){ext}");
        return path;
    }

    /// <summary>Circles kept for the Shelf or the phone go after a week, like Shammy's other temporary files.</summary>
    static void CleanOld(HashSet<string> keep)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var file in Directory.EnumerateFiles(Folder))
            {
                if (keep.Contains(file) || File.GetLastWriteTime(file) >= cutoff) continue;
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use (on the Shelf being dragged, say): next time.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Clearing old circles failed", ex);
        }
    }
}
