using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Shammy.Core.Clipboard;
using Shammy.Core.PhoneSend;
using Shammy.Modules.ClipboardHistory;
using Shammy.Shelf;
using WinClipboard = System.Windows.Clipboard;

namespace Shammy.Modules.PhoneSend;

/// <summary>
/// Everything "send to phone" could send right now, read once when the sheet opens: what's copied, the newest
/// screenshot, the shelf, the page in the browser. Picking a row sends what the row showed, even if the clipboard
/// changed since.
/// </summary>
internal sealed class PhoneSnapshot
{
    public required IReadOnlyList<PhoneChoice> Choices { get; init; }
    public string? CopiedText { get; init; }
    public BitmapSource? CopiedImage { get; init; }
    public IReadOnlyList<string> CopiedFiles { get; init; } = Array.Empty<string>();

    /// <summary>The screenshot is the clipboard module's latest capture (ask it for the file when picked).</summary>
    public bool ScreenshotFromLink { get; init; }

    /// <summary>A screenshot file found in the Screenshots folder (when the clipboard module had nothing newer).</summary>
    public string? ScreenshotPath { get; init; }

    public IReadOnlyList<ShelfItem> Shelf { get; init; } = Array.Empty<ShelfItem>();
    public string? PageUrl { get; init; }

    /// <summary>For logs: kinds only, never content ("copied-link,shelf,pc-file").</summary>
    public string Kinds => string.Join(",", Choices.Select(c => c.Id));
}

internal static class PhoneSources
{
    static readonly TimeSpan BrowserLimit = TimeSpan.FromMilliseconds(1500);

    /// <summary>Gathers on the UI thread (the clipboard needs it); the browser and the Screenshots folder are read meanwhile in the background.</summary>
    public static async Task<PhoneSnapshot> GatherAsync(ModuleContext ctx)
    {
        if (!ctx.Ui.CheckAccess()) return await ctx.Ui.InvokeAsync(() => GatherAsync(ctx)).Task.Unwrap();
        var background = Task.Run(NewestScreenshotFile);
        var page = BrowserAddress.ReadAsync(BrowserLimit);
        var copy = ReadClipboard();
        var file = await background;
        var p = await page;
        return Compose(ctx, copy, file, p);
    }

    /// <summary>The same, blocking, for the test pipe (nothing in the background waits on the UI thread).</summary>
    public static PhoneSnapshot Gather(ModuleContext ctx)
    {
        var background = Task.Run(NewestScreenshotFile);
        var copy = ReadClipboard();
        var page = BrowserAddress.Read(BrowserLimit);
        (string, DateTime)? file = null;
        try { if (background.Wait(TimeSpan.FromSeconds(2))) file = background.Result; }
        catch (AggregateException) { }
        return Compose(ctx, copy, file, page);
    }

    static PhoneSnapshot Compose(ModuleContext ctx, Copied copy, (string Path, DateTime At)? folderShot, BrowserAddress.Page? page)
    {
        var now = DateTime.Now;
        var link = Connect.Links.Clipboard;

        // The clipboard module's capture knows clipboard-only snips too; a newer file in Screenshots wins.
        var captured = link?.LatestScreenshot;
        bool fromLink = captured is not null;
        DateTime? shotAt = captured?.At;
        bool shotFromClipboard = captured?.FromClipboard ?? false;
        int shotW = captured?.Width ?? 0, shotH = captured?.Height ?? 0;
        string? shotPath = null;
        if (folderShot is { } f && (shotAt is null || f.At > shotAt.Value.AddSeconds(3)))
        {
            fromLink = false;
            shotAt = f.At;
            shotFromClipboard = false;
            shotW = shotH = 0;
            shotPath = f.Path;
        }

        var shelf = ctx.Services.Shelf.Items.Where(i => File.Exists(i.Path)).ToList();
        var inputs = new PhoneChoiceInputs
        {
            Now = now,
            CopiedText = copy.Text,
            CopiedImage = copy.Image is not null,
            CopiedImageWidth = copy.Image?.PixelWidth ?? 0,
            CopiedImageHeight = copy.Image?.PixelHeight ?? 0,
            CopiedFiles = copy.Files,
            CopiedAt = CopiedAt(link, copy),
            ScreenshotAt = shotAt,
            ScreenshotFromClipboard = shotFromClipboard,
            ScreenshotWidth = shotW,
            ScreenshotHeight = shotH,
            ShelfNames = shelf.Select(s => s.Name).ToList(),
            PageUrl = page?.Url,
            BrowserInFront = page?.InFront ?? false,
        };
        return new PhoneSnapshot
        {
            Choices = PhoneChoices.Build(inputs),
            CopiedText = copy.Text,
            CopiedImage = copy.Image,
            CopiedFiles = copy.Files,
            ScreenshotFromLink = fromLink,
            ScreenshotPath = shotPath,
            Shelf = shelf,
            PageUrl = page?.Url,
        };
    }

    /// <summary>When the current copy was made, from the clipboard history's newest entry when it is this copy.</summary>
    static DateTime? CopiedAt(Connect.IClipboardLink? link, Copied copy)
    {
        if (link is null || link.Items.Count == 0) return null;
        var newest = link.Items.MaxBy(e => e.Time);
        if (newest is null) return null;
        bool same = newest.Kind switch
        {
            ClipKind.Text => copy.Text is { } t && string.Equals(newest.Text, t, StringComparison.Ordinal),
            ClipKind.Files => copy.Files.Count > 0 && newest.Files.Length > 0 &&
                              string.Equals(newest.Files[0], copy.Files[0], StringComparison.OrdinalIgnoreCase),
            ClipKind.Image => copy.Image is not null && copy.Text is null && copy.Files.Count == 0,
            _ => false,
        };
        return same ? newest.Time : null;
    }

    // ---- The clipboard -------------------------------------------------------------------------

    internal readonly record struct Copied(string? Text, BitmapSource? Image, IReadOnlyList<string> Files)
    {
        public static readonly Copied None = new(null, null, Array.Empty<string>());
    }

    /// <summary>Copies longer than this are too big to be meant for a phone (and too big to hold on to).</summary>
    const int MaxText = 2_000_000;

    /// <summary>
    /// What's on the clipboard. Copies that password managers mark private are never offered (nor previewed), the
    /// same rule the clipboard history follows. Must run on the UI thread.
    /// </summary>
    static Copied ReadClipboard()
    {
        if (ClipNative.HasFormat(ClipPrivacy.ExcludeFormat)) return Copied.None;
        IDataObject? data = null;
        for (int attempt = 0; data is null && attempt < 3; attempt++)
        {
            try { data = WinClipboard.GetDataObject(); }
            catch (ExternalException) { Thread.Sleep(30); }
        }
        if (data is null) return Copied.None;
        try
        {
            if (ClipPrivacy.ShouldSkip(false, ClipboardModule.Flag(data, ClipPrivacy.HistoryFormat), ClipboardModule.Flag(data, ClipPrivacy.CloudFormat)))
                return Copied.None;
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                var existing = files.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
                if (existing.Count > 0) return new Copied(null, null, existing);
            }
            if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text
                && !string.IsNullOrWhiteSpace(text) && text.Length <= MaxText)
                return new Copied(text, null, Array.Empty<string>());
            if (ClipImages.FromDataObject(data) is { } image)
            {
                if (!image.IsFrozen && image.CanFreeze) image.Freeze();
                return new Copied(null, image, Array.Empty<string>());
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            Infrastructure.Log.Warn("[phonesend] Reading the clipboard failed", ex);
        }
        return Copied.None;
    }

    // ---- Screenshots folder --------------------------------------------------------------------

    /// <summary>The newest .png/.jpg in Pictures › Screenshots written in the last half hour, or null.</summary>
    static (string Path, DateTime At)? NewestScreenshotFile()
    {
        try
        {
            var dir = new DirectoryInfo(ScreenshotWatcher.ResolveFolder());
            if (!dir.Exists) return null;
            var since = DateTime.Now - PhoneChoices.ScreenshotRecent;
            FileInfo? best = null;
            foreach (var f in dir.EnumerateFiles())
            {
                if (!ScreenshotWatcher.IsImage(f.FullName) || f.LastWriteTime < since) continue;
                if (best is null || f.LastWriteTime > best.LastWriteTime) best = f;
            }
            return best is null ? null : (best.FullName, best.LastWriteTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Infrastructure.Log.Warn("[phonesend] Looking in the Screenshots folder failed", ex);
            return null;
        }
    }
}
