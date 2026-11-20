using Shammy.Core.DropActions;

namespace Shammy.Modules.DropActions;

/// <summary>
/// Compress and Convert from the drop zone. Compress zips what was dropped next to the first item (never
/// overwriting) and puts the zip on the shelf; Convert turns PNG into JPG and JPG into PNG next to the
/// original, and shelves the result. Send to phone is handled by the PhoneSend module; Shelf by the shelf.
/// </summary>
internal sealed class DropActionsModule : IShammyModule
{
    const string ZipGlyph = "\uF012", ConvertGlyph = "\uE8AB";

    ModuleContext _ctx = null!;
    string _last = "none";

    public string Name => "dropactions";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        ctx.Notch.DropZone.ActionDropped += (action, files) =>
        {
            if (action == DropAction.Compress) _ = CompressAsync(files);
            else if (action == DropAction.Convert) _ = ConvertAsync(files);
        };
        // The same two actions on a shelf item's right-click menu.
        ctx.AddShelfAction(new ShelfAction("Compress", item => _ = CompressAsync(new[] { item.Path })));
        ctx.AddShelfAction(new ShelfAction("Convert", item => _ = ConvertAsync(new[] { item.Path }))
        {
            AppliesTo = item => DropPlan.ConvertExtension(item.Path) is not null,
        });
        ctx.AddPipeCommand("shelf-action", ShelfActionTest);
        ctx.AddPipeCommand("drop-test", DropTest);
        ctx.AddPipeCommand("drop-browser", DropBrowserTest);
        ctx.AddPipeCommand("drop-state", _ => $"last={_last} hovered={ctx.Notch.DropZone.Hovered?.ToString() ?? "-"}");
    }

    async Task CompressAsync(string[] files)
    {
        var items = files.Where(f => File.Exists(f) || Directory.Exists(f)).ToList();
        if (items.Count == 0)
        {
            Report("compress: nothing to zip", ZipGlyph, "Nothing to zip", error: true);
            return;
        }
        _ctx.ShowMessage(ZipGlyph, "Zipping…", seconds: 30);
        try
        {
            var zip = await Task.Run(() =>
            {
                var path = DropPlan.ZipPath(items, p => File.Exists(p) || Directory.Exists(p));
                DropFiles.Zip(items, path);
                return path;
            });
            _ctx.Services.Shelf.Add(new[] { zip });
            Report($"compress: {zip}", ZipGlyph, $"Zipped · {Path.GetFileName(zip)}");
            _ctx.Notch.ExpandTo(shelf: true, hold: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            _ctx.Warn("Zipping dropped files failed", ex);
            Report($"compress failed: {ex.GetType().Name}", ZipGlyph,
                ex is UnauthorizedAccessException ? "Can't write a zip in that folder" : "Couldn't zip that", error: true);
        }
    }

    async Task ConvertAsync(string[] files)
    {
        var images = files.Where(f => File.Exists(f) && DropPlan.ConvertExtension(f) is not null).ToList();
        if (images.Count == 0)
        {
            Report("convert: no images", ConvertGlyph, "Convert works on images for now", error: true);
            return;
        }
        var made = new List<string>();
        int failed = 0;
        foreach (var image in images)
        {
            try
            {
                var ext = DropPlan.ConvertExtension(image)!;
                made.Add(await Task.Run(() => DropFiles.ConvertImage(image, ext)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                           or FileFormatException or ArgumentException or InvalidOperationException
                                           or System.Runtime.InteropServices.COMException)
            {
                _ctx.Warn("Converting a dropped image failed", ex);
                failed++;
            }
        }
        if (made.Count == 0)
        {
            Report($"convert failed ({failed})", ConvertGlyph, "Couldn't convert that image", error: true);
            return;
        }
        _ctx.Services.Shelf.Add(made);
        int skipped = files.Length - images.Count;
        string text = made.Count == 1 ? $"Saved {Path.GetFileName(made[0])}" : $"Converted {made.Count} images";
        if (skipped > 0 || failed > 0) text += $" · {skipped + failed} skipped";
        Report($"convert: {string.Join(" | ", made)}", ConvertGlyph, text);
        _ctx.Notch.ExpandTo(shelf: true, hold: true);
    }

    void Report(string state, string glyph, string message, bool error = false)
    {
        _last = state;
        _ctx.Info($"Drop: {state}");
        _ctx.ShowMessage(glyph, message, error, seconds: 3);
    }

    /// <summary>
    /// "drop-test compress C:\some\file.png": copies the file (or folder) into a fresh temp folder and runs the
    /// action on the copy as if it was dropped there, so nothing next to the original is touched.
    /// </summary>
    string DropTest(string arg)
    {
        var parts = arg.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || DropPlan.ParseAction(parts[0]) is not { } action)
            return "error: usage drop-test <shelf|phone|compress|convert> <path>";
        var source = parts[1].Trim().Trim('"');
        if (!File.Exists(source) && !Directory.Exists(source)) return "error: path not found";
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "shammy-drop-test", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(dir);
            var copy = Path.Combine(dir, Path.GetFileName(source.TrimEnd('\\', '/')));
            if (File.Exists(source)) File.Copy(source, copy);
            else CopyFolder(source, copy);
            var files = new[] { copy };
            if (action == DropAction.Shelf) _ctx.Services.Shelf.Add(files);
            else _ctx.Notch.DropZone.RaiseDropped(action, files);
            _last = $"{action.ToString().ToLowerInvariant()}: running";
            return $"ok {action.ToString().ToLowerInvariant()} on {copy} (see drop-state)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "error: " + ex.Message;
        }
    }

    /// <summary>
    /// "drop-browser phone link https://example.com", "… shelf text some words", "… shelf image C:\a.png": a browser's
    /// drag (a link, selected words, or an image handed over as a virtual file) dropped on a target, through the real drop path.
    /// </summary>
    string DropBrowserTest(string arg)
    {
        var p = arg.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3 || DropPlan.ParseAction(p[0]) is not { } action) return "error: usage drop-browser <shelf|phone|compress|convert> <link|text|image> <value>";
        var data = new System.Windows.DataObject();
        switch (p[1])
        {
            case "link":
                data.SetData("UniformResourceLocatorW", new MemoryStream(System.Text.Encoding.Unicode.GetBytes(p[2] + "\0")));
                data.SetText(p[2]);
                break;
            case "text":
                data.SetText(p[2]);
                break;
            case "image":
                var path = p[2].Trim('"');
                if (!File.Exists(path)) return "error: image not found";
                var d = new byte[4 + 592];
                d[0] = 1;
                System.Text.Encoding.Unicode.GetBytes(Path.GetFileName(path)).CopyTo(d, 4 + 72);
                data.SetData("FileGroupDescriptorW", new MemoryStream(d));
                data.SetData("FileContents", new MemoryStream(File.ReadAllBytes(path)));
                break;
            default:
                return "error: usage drop-browser <shelf|phone|compress|convert> <link|text|image> <value>";
        }
        return $"accepted={Shammy.Shell.BrowserDrop.Accepts(data)} {_ctx.Notch.DropData(data, action)}";
    }

    /// <summary>
    /// "shelf-action Compress 2": runs a shelf item's right-click action by label on the n-th shelf item (1-based),
    /// and lists which actions that item offers.
    /// </summary>
    string ShelfActionTest(string arg)
    {
        var parts = arg.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var items = _ctx.Services.Shelf.Items;
        if (parts.Length < 2 || !int.TryParse(parts[^1], out var n) || n < 1 || n > items.Count)
            return $"error: usage shelf-action <label> <1..{items.Count}>";
        var label = string.Join(' ', parts[..^1]);
        var item = items[n - 1];
        var offered = _ctx.Notch.ExpandedView.ShelfActions.Where(a => a.AppliesTo?.Invoke(item) ?? true).ToList();
        var action = offered.FirstOrDefault(a => string.Equals(a.Label, label, StringComparison.OrdinalIgnoreCase));
        if (action is null) return $"not offered for {item.Name}; offered: {string.Join(", ", offered.Select(a => a.Label))}";
        action.Run(item);
        return $"ran {action.Label} on {item.Name} (offered: {string.Join(", ", offered.Select(a => a.Label))})";
    }

    static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    public void Dispose()
    {
    }
}
