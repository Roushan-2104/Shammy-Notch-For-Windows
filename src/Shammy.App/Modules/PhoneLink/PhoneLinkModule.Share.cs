using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Shammy.Core.DropActions;
using Shammy.Core.PhoneLink;
using Shammy.Core.PhoneSend;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Modules.PhoneSend;

namespace Shammy.Modules.PhoneLink;

/// <summary>
/// Copied text, links and files both ways over the link (your answers, 21 Nov). From the phone: copied text lands on
/// this clipboard (private: no clipboard history), a link opens in the default browser (http/https only), a file lands
/// in Downloads\Shammy (kept only if its size and SHA-256 match, then named, marked and neutralized like an upload from
/// the phone's browser: SafeFileName.MoveIn) with Open / Show in folder. To the phone: Send to phone goes over the link
/// while the phone is online (PhoneSend asks <see cref="Connect.IIslandLink"/>), "Send clipboard" on the Your phone
/// card, and (unless switched off in Settings) whatever text you copy, silently. One file at a time each way.
/// Nothing sent or received is logged.
/// </summary>
internal sealed partial class PhoneLinkModule : Connect.IIslandLink
{
    const string LinkGlyph = "\uE774", CopyGlyph = "\uE8C8";

    bool Connect.IIslandLink.Online => Online;

    // ---- To the phone --------------------------------------------------------------------------------

    /// <summary>A link goes as a link (the phone offers Open), any other text to the phone's clipboard.</summary>
    public void SendText(string text)
    {
        if (LinkShare.Url(PhoneText.AsLink(text)) is { } url)
        {
            Send(new JsonObject { ["t"] = "link", ["url"] = url });
            _ctx.ShowMessage(LinkGlyph, "Link sent to your phone");
            return;
        }
        Send(new JsonObject { ["t"] = "clip", ["text"] = text });
        _ctx.ShowMessage(CopyGlyph, "On your phone's clipboard");
    }

    // ---- Shared clipboard (Settings › Connections › Share clipboard with your phone) ----------------------

    static readonly TimeSpan ClipSettle = TimeSpan.FromMilliseconds(300);
    System.Windows.Threading.DispatcherTimer? _autoClip;
    string? _pendingClip;

    /// <summary>The hash of the text the phone last sent, and when (UI thread): for a few seconds it never goes straight back.</summary>
    (int Hash, DateTime At)? _fromPhone;

    static readonly TimeSpan EchoFor = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Text copied in another app (the clipboard module has already left out password managers' copies and Shammy's
    /// own, like codes): after 300 ms of quiet it's on the phone's clipboard too, silently (`clip` with `auto`).
    /// </summary>
    void OnLaptopCopy(string text)
    {
        if (!Online || !_ctx.Settings.ShareClipboardWithPhone || text.Length > LinkShare.MaxClip || text.Trim().Length == 0
            || PrivacyState.Presenting || _fromPhone is { } f && f.Hash == text.GetHashCode() && DateTime.UtcNow - f.At < EchoFor) return;
        _pendingClip = text;
        _autoClip?.Stop();
        _autoClip = UiDelay.After(ClipSettle, SendAutoClip, _ctx.Ui);
    }

    void SendAutoClip()
    {
        var text = _pendingClip;
        _pendingClip = null;
        _autoClip = null;
        if (text is null || !Online || !_ctx.Settings.ShareClipboardWithPhone || PrivacyState.Presenting) return;
        Send(new JsonObject { ["t"] = "clip", ["text"] = text, ["auto"] = true });
    }

    /// <summary>"Send clipboard" on the Your phone card: what you copied, to the phone's clipboard.</summary>
    string SendClipboard()
    {
        var text = SelectionTools.ClipboardText.TryGet();
        string? problem = text is null ? "The clipboard is busy" : text.Trim().Length == 0 ? "Nothing copied to send"
            : text.Length > LinkShare.MaxClip ? "Too long for the phone's clipboard" : null;
        if (problem is not null)
        {
            _ctx.ShowMessage(CopyGlyph, problem, error: true);
            return "error: " + problem;
        }
        Send(new JsonObject { ["t"] = "clip", ["text"] = text });
        _ctx.ShowMessage(CopyGlyph, "On your phone's clipboard");
        return "sent clipboard";
    }

    int _sending;

    /// <summary>A file in pieces, then its SHA-256; the phone keeps it only if both match.</summary>
    public Task SendFileAsync(string path)
    {
        if (!Online) return Task.CompletedTask;
        if (Interlocked.Exchange(ref _sending, 1) == 1)
        {
            _ctx.ShowMessage(PhoneGlyph, "Still sending a file to your phone", error: true);
            return Task.CompletedTask;
        }
        var s = _live;
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        _ctx.ShowMessage(PhoneGlyph, "Sending to your phone…", seconds: 60);
        return Task.Run(async () =>
        {
            bool sent = false;
            try
            {
                await using var file = File.OpenRead(path);
                if (file.Length > LinkShare.MaxFile) throw new IOException("over 50 MB");
                await SendNowAsync(s, LinkShare.FileStart(id, Path.GetFileName(path), file.Length));
                var sha = await SendPiecesAsync(s, file, (i, data) => new JsonObject { ["t"] = "fileChunk", ["id"] = id, ["i"] = i, ["data"] = data });
                await SendNowAsync(s, new JsonObject { ["t"] = "fileEnd", ["id"] = id, ["sha256"] = sha });
                sent = true;
                _ctx.Info($"Sent a file to the phone ({file.Length / 1024} KB)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or SocketException)
            {
                _ctx.Info($"Sending a file to the phone stopped ({ex.GetType().Name})");
            }
            finally
            {
                Interlocked.Exchange(ref _sending, 0);
                _ = _ctx.Ui.BeginInvoke(() => _ctx.ShowMessage(PhoneGlyph, sent ? "Sent to your phone" : "Couldn't send that to your phone", error: !sent));
            }
        });
    }

    /// <summary>One message, awaited (pieces must go in order); with no phone (the test switch) it's only noted.</summary>
    async Task SendNowAsync(LinkSession? s, JsonObject m)
    {
        Note(m);
        if (s is not null) await s.SendAsync(m, CancellationToken.None);
    }

    /// <summary>A stream in 36 KB pieces, in order from 0 (the app update and files); returns its SHA-256 (hex).</summary>
    async Task<string> SendPiecesAsync(LinkSession? s, Stream from, Func<int, string, JsonObject> piece)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[LinkShare.Chunk];
        for (int i = 0; ; i++)
        {
            int n = await from.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);
            if (n == 0) break;
            sha.AppendData(buffer, 0, n);
            await SendNowAsync(s, piece(i, Convert.ToBase64String(buffer, 0, n)));
            if (n < buffer.Length) break;
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    // ---- From the phone ------------------------------------------------------------------------------

    static readonly TimeSpan LinkGap = TimeSpan.FromSeconds(5);
    static readonly long Headroom = 1L << 30;
    DateTime _linkOpenedAt;

    void TakeClip(string text)
    {
        if (text.Length == 0) return;
        _fromPhone = (text.GetHashCode(), DateTime.UtcNow);
        if (!SelectionTools.ClipboardText.TrySet(text, secret: true))
        {
            _ctx.ShowMessage(CopyGlyph, "The clipboard is busy", error: true);
            return;
        }
        _ctx.ShowMessage(CopyGlyph, "Copied from your phone");
        _ctx.Info("Copied text from the phone");
    }

    /// <summary>
    /// A shared link opens in the default browser: at most one every 5 s (more just say so), and while the screen is
    /// shared or recorded only when you press Open. Never over a ringing call's card.
    /// </summary>
    void OpenLink(string raw)
    {
        if (LinkShare.Url(raw) is not { } url) return;
        var now = DateTime.UtcNow;
        if (now - _linkOpenedAt < LinkGap)
        {
            _ctx.ShowMessage(LinkGlyph, "Another link from your phone, not opened");
            return;
        }
        _linkOpenedAt = now;
        if (!PrivacyState.Presenting) Browse(url);
        else if (_callKey is not null) _ctx.ShowMessage(LinkGlyph, "A link from your phone, not opened while sharing");
        else ShowPeek(PhoneChoiceSheet.Icons.Globe, "A link", "Opens in your browser", ("Not now", () => { }), ("Open", () => Browse(url)));
    }

    void Browse(string url)
    {
        if (!_ctx.Open(url))
        {
            _ctx.ShowMessage(LinkGlyph, "Couldn't open the link from your phone", error: true);
            return;
        }
        _ctx.ShowMessage(LinkGlyph, "Opened a link from your phone");
        _ctx.Info("Opened a link from the phone");
    }

    static string ReceivedDir => Path.Combine(Native.KnownFolder(Native.DownloadsFolder)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "Shammy");

    readonly object _fileLock = new();

    // under _fileLock: the file coming in (one at a time) and the session it came on (null: the test pipe), written as
    // its pieces arrive into a hidden temp file next to where it will be kept
    (IncomingFile File, FileStream To, string Temp, LinkSession? From)? _incoming;

    /// <summary>`file`, `fileChunk`, `fileEnd` (on the read loop, in order, so pieces are written as they come).</summary>
    void TakeFile(JsonObject m, LinkSession? from)
    {
        lock (_fileLock)
        {
            try
            {
                var t = m["t"]?.GetValue<string>();
                if (t == "file")
                {
                    if (IncomingFile.Start(m) is not { } f) return;
                    // still one at a time: a new start means the sender gave up on the last one
                    DropIncoming(null, any: true);
                    var dir = ReceivedDir;
                    Directory.CreateDirectory(dir);
                    if (PhoneTransferServer.FreeSpace(dir) is { } free && free < f.Size + Headroom)
                    {
                        f.Dispose();
                        _ctx.Info("Refused a file from the phone: the disk is nearly full");
                        _ = _ctx.Ui.BeginInvoke(() => _ctx.ShowMessage(PhoneGlyph, "No room for a file from your phone", error: true));
                        return;
                    }
                    // not ".part": the Downloads watcher would take it for a browser download
                    var temp = Path.Combine(dir, $".shammy-{Guid.NewGuid():N}.tmp");
                    _incoming = (f, new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None), temp, from);
                    return;
                }
                if (_incoming is not { } inc || !ReferenceEquals(inc.From, from) || LinkSession.Text(m, "id") != inc.File.Id) return;
                if (t == "fileChunk")
                {
                    int i = m["i"] is JsonValue iv && iv.TryGetValue<int>(out int n) ? n : -1;
                    var data = Convert.FromBase64String(m["data"] is JsonValue dv && dv.TryGetValue<string>(out var d) ? d : "");
                    if (!inc.File.Take(i, data)) throw new InvalidDataException("a piece out of order or past the size");
                    inc.To.Write(data);
                }
                else if (t == "fileEnd")
                {
                    inc.To.Dispose();
                    if (!inc.File.Complete(LinkSession.Text(m, "sha256")))
                    {
                        DropIncoming(from);
                        _ctx.Info("A file from the phone didn't match its size or SHA-256; dropped");
                        _ = _ctx.Ui.BeginInvoke(() => _ctx.ShowMessage(PhoneGlyph, "A file from your phone didn't arrive whole", error: true));
                        return;
                    }
                    var kept = SafeFileName.MoveIn(inc.Temp, ReceivedDir, inc.File.Name);
                    _incoming = null;
                    inc.File.Dispose();
                    _ctx.Info($"Received a file from the phone ({inc.File.Size / 1024} KB)");
                    _ = _ctx.Ui.BeginInvoke(() => ShowReceived(kept));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or InvalidDataException)
            {
                DropIncoming(from);
                _ctx.Info($"Receiving a file from the phone stopped ({ex.GetType().Name})");
                _ = _ctx.Ui.BeginInvoke(() => _ctx.ShowMessage(PhoneGlyph, "A file from your phone didn't arrive", error: true));
            }
        }
    }

    /// <summary>The connection ended mid-file (or a piece was wrong): what came so far goes, but only that session's.</summary>
    void DropIncoming(LinkSession? from, bool any = false)
    {
        lock (_fileLock)
        {
            if (_incoming is not { } inc || !any && !ReferenceEquals(inc.From, from)) return;
            _incoming = null;
            inc.To.Dispose();
            inc.File.Dispose();
            try { File.Delete(inc.Temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    void ShowReceived(string path)
    {
        var name = PrivacyState.Presenting ? "A file" : Path.GetFileName(path);
        // a ringing call keeps its card
        if (_callKey is not null)
        {
            _ctx.ShowMessage(PhoneGlyph, $"{name} from your phone is in Downloads\\Shammy", seconds: 5);
            return;
        }
        var size = File.Exists(path) ? DropPlan.Size(new FileInfo(path).Length) : "";
        ShowPeek(PhoneChoiceSheet.Icons.File, name, $"{size} · Downloads\\Shammy",
            ("Show in folder", () => _ctx.Open("explorer.exe", $"/select,\"{path}\"")), ("Open", () => _ctx.Open(path)));
    }

    /// <summary>A card from the phone for 12 s; each button closes it first.</summary>
    void ShowPeek(string icon, string title, string line, (string Text, Action Click) quiet, (string Text, Action Click) primary)
    {
        PhonePeek? card = null;
        void Close()
        {
            if (card is not null && ReferenceEquals(_ctx.Notch.OpenCardContent, card)) _ctx.CloseCard();
        }
        card = new PhonePeek(icon, "From your phone", title, line,
            (quiet.Text, () => { Close(); quiet.Click(); }), (primary.Text, () => { Close(); primary.Click(); }));
        _ctx.ShowCard(card, PhonePeek.CardWidth, PhonePeek.CardHeight, interactive: false);
        UiDelay.After(PeekFor, Close, _ctx.Ui);
    }

    // ---- Test hooks --------------------------------------------------------------------------------

    /// <summary>What went to the phone last, by type (only with the test pipe on; pieces without their data).</summary>
    readonly ConcurrentDictionary<string, string> _lastSent = new();

    void Note(JsonObject m)
    {
        if (!CommandServer.IsEnabled) return;
        var copy = (JsonObject)m.DeepClone();
        foreach (var big in new[] { "data", "art", "jpg", "png" })
            if (copy[big] is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 80) copy[big] = $"({text.Length} chars)";
        _lastSent[copy["t"]?.GetValue<string>() ?? "?"] = copy.ToJsonString();
    }

    void StartShare()
    {
        Connect.Links.Island = this;
        Connect.Links.TextCopied += OnLaptopCopy;
        // leftovers of a transfer cut short when Shammy last closed
        try
        {
            if (Directory.Exists(ReceivedDir))
                foreach (var left in Directory.EnumerateFiles(ReceivedDir, ".shammy-*.tmp")) File.Delete(left);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    void StopShare()
    {
        if (Connect.Links.Island == this) Connect.Links.Island = null;
        Connect.Links.TextCopied -= OnLaptopCopy;
        _autoClip?.Stop();
        DropIncoming(null, any: true);
    }

    void StartShareTests()
    {
        _ctx.AddPipeCommand("phonelink-sent", arg => arg.Trim() is { Length: > 0 } t
            ? _lastSent.TryGetValue(t, out var json) ? json : "none"
            : string.Join(" ", _lastSent.Keys.Order()));
        // a copy in another app, as the clipboard module passes it on (or use clipboard-copy for the whole path)
        _ctx.AddPipeCommand("phonelink-fake-copy", text => { OnLaptopCopy(text); return _pendingClip is null ? "skipped" : "sending in 300 ms"; });
        _ctx.AddPipeCommand("phonelink-fake-clip", text => { OnPhoneSays(new JsonObject { ["t"] = "clip", ["text"] = text }); return "clip taken"; });
        _ctx.AddPipeCommand("phonelink-fake-link", url => { OnPhoneSays(new JsonObject { ["t"] = "link", ["url"] = url.Trim() }); return "link taken"; });
        // "path" or "path|bad" (a wrong SHA-256): the file as the phone would send it
        _ctx.AddPipeCommand("phonelink-fake-file", arg =>
        {
            var p = arg.Split('|');
            var path = p[0].Trim().Trim('"');
            if (!File.Exists(path)) return "error: usage phonelink-fake-file <path>[|bad]";
            var bytes = File.ReadAllBytes(path);
            var id = "fake" + Environment.TickCount64;
            TakeFile(LinkShare.FileStart(id, Path.GetFileName(path), bytes.Length), null);
            int pieces = 0;
            for (int at = 0; at < bytes.Length; at += LinkShare.Chunk, pieces++)
                TakeFile(new JsonObject { ["t"] = "fileChunk", ["id"] = id, ["i"] = pieces, ["data"] = Convert.ToBase64String(bytes[at..Math.Min(at + LinkShare.Chunk, bytes.Length)]) }, null);
            var sha = Convert.ToHexString(SHA256.HashData(p.Length > 1 ? new byte[] { 1 } : bytes));
            TakeFile(new JsonObject { ["t"] = "fileEnd", ["id"] = id, ["sha256"] = sha }, null);
            return $"fed {pieces} pieces";
        });
    }
}
