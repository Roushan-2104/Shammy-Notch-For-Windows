using System.Globalization;
using System.Text;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shammy.Core.Clipboard;
using Shammy.Core.PhoneSend;
using Shammy.Shelf;
using Shammy.Views.Cards;

namespace Shammy.Modules.PhoneSend;

/// <summary>
/// AirDrop-style transfers over the local Wi-Fi: a QR code on the notch opens a page on the phone to
/// download the file, or upload files back into Downloads. No app, no cloud, no account.
/// "Send this to my phone" with nothing named asks what to send: what you copied, your last screenshot,
/// the shelf, the page in your browser, or a file from the PC. Links and short text skip the server and
/// go in the QR code itself.
/// </summary>
internal sealed class PhoneSendModule : IShammyModule, Connect.IPhoneLink
{
    const string PhoneGlyph = "";

    /// <summary>The shelf step lists this many files (newest first); "All as one zip" covers the rest.</summary>
    const int ShelfRows = 6;

    static readonly TimeSpan TempKeep = TimeSpan.FromDays(1);

    ModuleContext? _ctx;
    PhoneTransfer? _transfer;
    PhoneSendCard? _qrCard;
    string _qrKind = "";
    PhoneChoiceSheet? _sheet;
    PhoneSnapshot? _snapshot;

    public string Name => "phonesend";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _ = Task.Run(SweepTemp);
        // A folder on the shelf goes as a zip, like a folder dropped on "Send to phone".
        ctx.AddShelfAction(new ShelfAction("Send to phone", item => _ = SendDroppedAsync(new[] { item.Path })));
        Connect.Links.Phone = this;
        ctx.AddCommand(new SendCommand(this));
        // "Send to phone" on the drop zone: one file goes as is; several (or a folder) go as one zip.
        ctx.Notch.DropZone.ActionDropped += (action, files) =>
        {
            if (action == Shammy.Core.DropActions.DropAction.Phone) _ = SendDroppedAsync(files);
        };

        ctx.AddPipeCommand("phone-send", arg =>
        {
            var path = arg.Trim().Trim('"');
            if (path.Length == 0) return "error: usage phone-send <path>";
            return Send(path) is { } error ? "error: " + error : _transfer!.Url;
        });
        ctx.AddPipeCommand("phone-stop", _ =>
        {
            if (_qrCard is not null)
            {
                CloseQr();
                return "stopped";
            }
            if (_transfer is not { IsRunning: true }) return "not running";
            _transfer.Stop();
            return "stopped";
        });
        ctx.AddPipeCommand("phone-state", _ => _transfer is { IsRunning: true } t
            ? string.Create(CultureInfo.InvariantCulture,
                $"running url={t.Url} file=\"{Path.GetFileName(t.FilePath)}\" sent={t.BytesSent}/{t.FileLength} active={t.ActiveDownloads} completed={t.CompletedDownloads} received={t.FilesReceived}")
            : _qrCard is { Parent: not null } ? $"qr kind={_qrKind}"
            : SheetOpen ? "choosing"
            : "stopped");
        // The choice sheet, for tests: what it would show (or shows now), open it, pick a row, send some text.
        ctx.AddPipeCommand("phone-choices", _ => SheetOpen ? DescribeRows(_sheet!.Rows) : DescribeRows(RowsFor(PhoneSources.Gather(ctx))));
        ctx.AddPipeCommand("phone-ask", _ =>
        {
            OpenSheet(PhoneSources.Gather(ctx));
            return DescribeRows(_sheet!.Rows);
        });
        ctx.AddPipeCommand("phone-pick", arg =>
        {
            if (!int.TryParse(arg.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
                return "error: usage phone-pick <n> (1 is the top row)";
            if (SheetOpen) return _sheet!.Pick(n - 1);
            var rows = RowsFor(PhoneSources.Gather(ctx));
            return n <= rows.Count ? rows[n - 1].Pick() : $"error: no choice {n} (there are {rows.Count})";
        });
        ctx.AddPipeCommand("phone-send-text", arg => arg.Trim().Length == 0 ? "error: usage phone-send-text <text>" : SendText(arg));
    }

    bool SheetOpen => _sheet is { Parent: not null };

    // ---- Sending -------------------------------------------------------------------------------

    /// <summary>Starts sharing a file (replacing any current transfer). Returns a user-facing error, or null.</summary>
    string? Send(string path)
    {
        if (_ctx is null) return "Not ready";
        _transfer?.Stop();
        _transfer = null;
        try
        {
            var transfer = PhoneTransfer.Start(_ctx, path);
            transfer.Ended += () =>
            {
                if (ReferenceEquals(_transfer, transfer)) _transfer = null;
                DeleteIfTemp(path);
            };
            _transfer = transfer;
            // Its card took the notch from the sheet or a QR code.
            _sheet = null;
            _qrCard = null;
            return null;
        }
        catch (PhoneSendException ex)
        {
            DeleteIfTemp(path);
            return ex.Message;
        }
    }

    /// <summary><see cref="Send"/> for a row: shows the problem in the notch, and says what happened for the pipe.</summary>
    string SendFile(string path, string what)
    {
        if (Send(path) is { } error)
        {
            CloseSheet();
            _ctx?.ShowMessage(PhoneGlyph, error, error: true);
            return "error: " + error;
        }
        _ctx?.Info($"Sending {what} to the phone");
        return "sharing " + what;
    }

    async Task SendDroppedAsync(IReadOnlyList<string> files)
    {
        if (_ctx is null) return;
        var items = files.Where(f => File.Exists(f) || Directory.Exists(f)).ToList();
        if (items.Count == 0) return;
        string path = items[0];
        if (items.Count > 1 || Directory.Exists(path))
        {
            try
            {
                // A zip in Shammy's own temp folder: nothing appears next to the user's files.
                var dir = TempFolder();
                _ctx.ShowMessage(PhoneGlyph, "Zipping for your phone…", seconds: 30);
                path = await Task.Run(() =>
                {
                    var zip = Shammy.Core.DropActions.DropPlan.ZipPath(items, _ => false);
                    zip = Shammy.Core.DropActions.DropPlan.Unique(dir, Path.GetFileNameWithoutExtension(zip), ".zip", File.Exists);
                    DropActions.DropFiles.Zip(items, zip);
                    return zip;
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _ctx.Warn("Zipping for the phone failed", ex);
                _ctx.ShowMessage(PhoneGlyph, "Couldn't zip that", error: true);
                return;
            }
        }
        if (Send(path) is { } error) _ctx.ShowMessage(PhoneGlyph, error, error: true);
    }

    /// <summary>
    /// Text to the phone: a link goes in the QR code so the phone opens it, short text goes in the code to read
    /// there, anything longer is served as a .txt file like any other transfer. Says what happened.
    /// </summary>
    string SendText(string text)
    {
        if (_ctx is null) return "error: not ready";
        text = text.Trim();
        switch (PhoneText.Route(text))
        {
            case PhoneTextRoute.QrLink:
                var url = PhoneText.AsLink(text)!;
                return ShowQr(url, PhoneText.LinkPreview(url, 90), "Link", "Scan with your phone's camera to open it",
                    "QR code: scan it with your phone's camera to open the link", "link");
            case PhoneTextRoute.QrText:
                return ShowQr(text, ClipText.FirstLine(text, 90), PhoneText.TextSize(text), "Scan with your phone's camera to read it",
                    "QR code: scan it with your phone's camera to read the text", "text");
            default:
                // A neutral name: the file name shows in logs, the text itself never does.
                if (WriteTemp("Copied text", ".txt", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray()) is not { } path)
                {
                    CloseSheet();
                    _ctx.ShowMessage(PhoneGlyph, "Couldn't prepare the text", error: true);
                    return "error: couldn't write the text file";
                }
                return SendFile(path, "text as a file");
        }
    }

    /// <summary>A card whose QR code carries the thing itself; no server runs.</summary>
    string ShowQr(string payload, string title, string subtitle, string hint, string qrName, string kind)
    {
        if (_ctx is null) return "error: not ready";
        _transfer?.Stop();
        _transfer = null;
        var card = PhoneSendCard.ForQr(payload, title, subtitle, hint, "", qrName);
        card.CloseRequested += () =>
        {
            if (!ReferenceEquals(_qrCard, card)) return;
            CloseQr();
        };
        _qrCard = card;
        _qrKind = kind;
        _sheet = null;
        _ctx.ShowCard(card, PhoneSendCard.CardWidth, PhoneSendCard.CardHeight, interactive: true);
        _ctx.Info($"Showing a {kind} QR code for the phone");
        return "qr " + kind;
    }

    void CloseQr()
    {
        if (_qrCard is { Parent: not null }) _ctx?.CloseCard();
        _qrCard = null;
    }

    // ---- The choice sheet ----------------------------------------------------------------------

    void OpenSheet(PhoneSnapshot snapshot)
    {
        if (_ctx is not { } ctx) return;
        _snapshot = snapshot;
        var sheet = new PhoneChoiceSheet(CloseSheet);
        // A step with more or fewer rows: the island follows the sheet's height.
        sheet.FitChanged += () =>
        {
            if (ReferenceEquals(_sheet, sheet) && sheet.Parent is not null)
                ctx.ShowCard(sheet, PhoneChoiceSheet.SheetWidth, sheet.FitHeight, interactive: true);
        };
        _sheet = sheet;
        ShowMain();
        ctx.ShowCard(sheet, PhoneChoiceSheet.SheetWidth, sheet.Fit(), interactive: true);
        ctx.Info($"Asked what to send: {snapshot.Kinds}");
    }

    void ShowMain()
    {
        if (_sheet is null || _snapshot is null) return;
        _sheet.Show("choose what to send", RowsFor(_snapshot));
    }

    void CloseSheet()
    {
        if (_sheet is { Parent: not null }) _ctx?.CloseCard();
        _sheet = null;
    }

    /// <summary>The sheet's rows for a snapshot, in the order <see cref="PhoneChoices.Build"/> chose.</summary>
    List<PhoneChoiceSheet.Row> RowsFor(PhoneSnapshot s) =>
        s.Choices.Select(c => new PhoneChoiceSheet.Row(c.Id, IconOf(c.Kind), c.Title, c.Detail, () => Pick(c.Kind, s))).ToList();

    static string IconOf(PhoneChoiceKind kind) => kind switch
    {
        PhoneChoiceKind.CopiedLink => CardKit.Paths.Link,
        PhoneChoiceKind.CopiedText => CardKit.Paths.Note,
        PhoneChoiceKind.CopiedImage => PhoneChoiceSheet.Icons.Image,
        PhoneChoiceKind.CopiedFiles => CardKit.Paths.Copy,
        PhoneChoiceKind.Screenshot => PhoneChoiceSheet.Icons.Screenshot,
        PhoneChoiceKind.Shelf => PhoneChoiceSheet.Icons.File,
        PhoneChoiceKind.BrowserPage => PhoneChoiceSheet.Icons.Globe,
        _ => CardKit.Paths.Laptop,
    };

    static string DescribeRows(IReadOnlyList<PhoneChoiceSheet.Row> rows) =>
        // One line: the test pipe answers a single line.
        string.Join(" ; ", rows.Select((r, i) => $"{i + 1}. {r.Id} | {r.Title}"));

    /// <summary>Sends what a row offered. Kinds only reach the log, never what was copied or which page.</summary>
    string Pick(PhoneChoiceKind kind, PhoneSnapshot s)
    {
        if (_ctx is null) return "error: not ready";
        _ctx.Info($"Picked {PhoneChoices.IdOf(kind)}");
        switch (kind)
        {
            case PhoneChoiceKind.CopiedLink or PhoneChoiceKind.CopiedText when s.CopiedText is { } text:
                return SendText(text);
            case PhoneChoiceKind.CopiedImage when s.CopiedImage is { } image:
                _ = SendImageAsync(image);
                return "preparing copied image";
            case PhoneChoiceKind.CopiedFiles when s.CopiedFiles.Count > 0:
                if (s.CopiedFiles.Count == 1 && File.Exists(s.CopiedFiles[0])) return SendFile(s.CopiedFiles[0], "a copied file");
                CloseSheet();
                _ = SendDroppedAsync(s.CopiedFiles);
                return "zipping copied files";
            case PhoneChoiceKind.Screenshot:
            {
                var path = s.ScreenshotFromLink ? Connect.Links.Clipboard?.LatestScreenshotFile() : s.ScreenshotPath;
                if (path is null || !File.Exists(path))
                {
                    CloseSheet();
                    _ctx.ShowMessage(PhoneGlyph, "That screenshot's file is gone", error: true);
                    return "error: screenshot file is gone";
                }
                return SendFile(path, "the last screenshot");
            }
            case PhoneChoiceKind.Shelf:
                return PickShelf(s);
            case PhoneChoiceKind.BrowserPage when s.PageUrl is { } url:
                return ShowQr(url, PhoneText.LinkPreview(url, 90), "Web page", "Scan with your phone's camera to open this page",
                    "QR code: scan it with your phone's camera to open the page", "page");
            case PhoneChoiceKind.PcFile:
                CloseSheet();
                // After this click (or pipe call) has returned: the picker is modal.
                _ctx.Ui.BeginInvoke(DispatcherPriority.Background, new Action(PickFromPc));
                return "picker opened";
            default:
                return "error: that's no longer available";
        }
    }

    /// <summary>One shelf file goes as is; several open a second step: each file (newest first), or all as one zip.</summary>
    string PickShelf(PhoneSnapshot s)
    {
        var files = s.Shelf.Where(i => File.Exists(i.Path)).ToList();
        if (files.Count == 0)
        {
            CloseSheet();
            _ctx?.ShowMessage(PhoneGlyph, "The shelf is empty now", error: true);
            return "error: shelf is empty";
        }
        if (files.Count == 1) return SendFile(files[0].Path, "a shelf file");

        var rows = new List<PhoneChoiceSheet.Row>();
        foreach (var item in Enumerable.Reverse(files).Take(ShelfRows))
            rows.Add(new PhoneChoiceSheet.Row("shelf-file", PhoneChoiceSheet.Icons.File, item.Name, ByteSize.Format(Size(item.Path)),
                () => SendFile(item.Path, "a shelf file")));
        var paths = files.Select(f => f.Path).ToList();
        rows.Add(new PhoneChoiceSheet.Row("shelf-zip", PhoneChoiceSheet.Icons.Zip, $"All {files.Count} as one zip",
            files.Count > ShelfRows ? $"Including {files.Count - ShelfRows} more not listed" : "One download on the phone",
            () =>
            {
                CloseSheet();
                _ = SendDroppedAsync(paths);
                return "zipping shelf files";
            }));
        if (_sheet is { Parent: not null } sheet)
        {
            sheet.Show("on the shelf", rows, back: ShowMain);
            return $"shelf step ({rows.Count} rows)";
        }
        // From the pipe with no sheet open: open it on this step.
        OpenSheet(s);
        _sheet!.Show("on the shelf", rows, back: ShowMain);
        return $"shelf step ({rows.Count} rows)";
    }

    static long Size(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    async Task SendImageAsync(BitmapSource image)
    {
        if (_ctx is null) return;
        CloseSheet();
        byte[] png;
        try
        {
            var frozen = image;
            if (!frozen.IsFrozen)
            {
                frozen = new WriteableBitmap(frozen);
                frozen.Freeze();
            }
            png = await Task.Run(() => ClipboardHistory.ClipImages.EncodePng(frozen));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            _ctx.Warn("Encoding the copied image failed", ex);
            _ctx.ShowMessage(PhoneGlyph, "Couldn't prepare the image", error: true);
            return;
        }
        if (WriteTemp("Copied image", ".png", png) is not { } path)
        {
            _ctx.ShowMessage(PhoneGlyph, "Couldn't prepare the image", error: true);
            return;
        }
        SendFile(path, "a copied image");
    }

    /// <summary>The usual Windows open dialog over the notch; several files go as one zip.</summary>
    void PickFromPc()
    {
        if (_ctx is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Send to your phone",
            Multiselect = true,
            CheckFileExists = true,
        };
        bool? ok;
        try
        {
            ok = dialog.ShowDialog(_ctx.Notch);
        }
        catch (InvalidOperationException ex)
        {
            _ctx.Warn("The file picker couldn't open", ex);
            ok = dialog.ShowDialog();
        }
        if (ok != true || dialog.FileNames.Length == 0) return;
        _ctx.Info($"Picked {dialog.FileNames.Length} file(s) from the PC");
        _ = SendDroppedAsync(dialog.FileNames);
    }

    // ---- Temp files ------------------------------------------------------------------------------

    static string TempDir => Path.Combine(Path.GetTempPath(), "Shammy", "phone-send");

    static string TempFolder()
    {
        Directory.CreateDirectory(TempDir);
        return TempDir;
    }

    /// <summary>A zip or copied text/image made for one transfer goes when it ends; the user's own files never do.</summary>
    void DeleteIfTemp(string path)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), TempDir, StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ctx?.Warn("Deleting a file made for the phone failed", ex); }
    }

    /// <summary>At start: files a crash or a locked delete left behind, once older than a day.</summary>
    static void SweepTemp()
    {
        try
        {
            if (!Directory.Exists(TempDir)) return;
            var cutoff = DateTime.Now - TempKeep;
            foreach (var old in new DirectoryInfo(TempDir).EnumerateFiles().Where(f => f.LastWriteTime < cutoff))
            {
                try { old.Delete(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* still in use; next time */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Infrastructure.Log.Warn("Tidying files made for the phone failed", ex);
        }
    }

    /// <summary>Writes a file for the phone in Shammy's temp folder; null on failure.</summary>
    string? WriteTemp(string stem, string extension, byte[] bytes)
    {
        try
        {
            var dir = TempFolder();
            var path = Shammy.Core.DropActions.DropPlan.Unique(dir, stem, extension, File.Exists);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ctx?.Warn("Writing a file for the phone failed", ex);
            return null;
        }
    }

    // ---- The command -------------------------------------------------------------------------------

    /// <summary>Shelf entries that are files (folders can't be sent), oldest first like the shelf.</summary>
    List<ShelfItem> ShelfFiles() =>
        _ctx?.Services.Shelf.Items.Where(i => File.Exists(i.Path)).ToList() ?? new List<ShelfItem>();

    bool CanHandle(string text)
    {
        if (!PhoneSendPhrase.TryParse(text, out var query)) return false;
        if (query is null) return true;
        // "send money to my phone" is a web search unless the shelf has something like it, or it
        // clearly names a file (then a miss is worth reporting).
        var files = ShelfFiles();
        return PhoneSendPhrase.FindItem(files.Select(f => f.Name).ToList(), query) >= 0
            || PhoneSendPhrase.LooksLikeFileName(query);
    }

    async Task<CommandResult> RunAsync(string text)
    {
        PhoneSendPhrase.TryParse(text, out var query);
        var files = ShelfFiles();
        // Nothing named ("send this to my phone"), or "the latest file" with an empty shelf: ask what to send.
        if (_ctx is not null && (PhoneSendPhrase.AsksWhat(text) || (query is null && files.Count == 0)))
        {
            var ctx = _ctx;
            var snapshot = await PhoneSources.GatherAsync(ctx);
            // The sheet takes the notch once this confirmation has had its moment.
            _ = ctx.Ui.BeginInvoke(DispatcherPriority.Background, new Action(() => ctx.Notch.AfterSearch(() => OpenSheet(snapshot))));
            return new CommandResult(PhoneGlyph, "Send to phone", "Choose what to send");
        }
        if (files.Count == 0)
            return new CommandResult(PhoneGlyph, "Nothing on the shelf", "Drop a file on the notch first", IsError: true);
        int index = PhoneSendPhrase.FindItem(files.Select(f => f.Name).ToList(), query);
        if (index < 0)
            return new CommandResult(PhoneGlyph, "Not on the shelf", $"No shelf file matches “{query}”", IsError: true);

        var item = files[index];
        // The card waits behind this confirmation and appears when it clears.
        return Send(item.Path) is { } error
            ? new CommandResult(PhoneGlyph, error, item.Name, IsError: true)
            : new CommandResult(PhoneGlyph, "Scan to send to your phone", item.Name);
    }

    Task Connect.IPhoneLink.SendAsync(IReadOnlyList<string> paths) => SendDroppedAsync(paths);

    public void Dispose()
    {
        if (Connect.Links.Phone == this) Connect.Links.Phone = null;
        _transfer?.Dispose();
        _transfer = null;
    }

    sealed class SendCommand(PhoneSendModule module) : ITextCommand
    {
        public bool CanHandle(string text) => module.CanHandle(text);
        public Task<CommandResult> RunAsync(string text) => module.RunAsync(text);
    }
}
