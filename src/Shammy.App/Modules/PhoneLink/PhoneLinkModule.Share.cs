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
/// in Downloads\Shammy (name cleaned, never overwriting, kept only if its size and SHA-256 match) with Open / Show in
/// folder. To the phone: Send to phone goes over the link while the phone is online (PhoneSend asks
/// <see cref="Connect.IIslandLink"/>), and "Send clipboard" on the Your phone card. One file at a time each way.
/// Nothing sent or received is logged.
/// </summary>
internal sealed partial class PhoneLinkModule : Connect.IIslandLink
{
    const string LinkGlyph = "", CopyGlyph = "";

    static string IncomingDir => Path.Combine(Path.GetTempPath(), "Shammy", "phone-in");

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
        Send(new JsonObject { ["t"] = "clip", ["text"] = text[..Math.Min(text.Length, LinkShare.MaxClip)] });
        _ctx.ShowMessage(CopyGlyph, "On your phone's clipboard");
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

    void TakeClip(string text)
    {
        if (text.Length == 0) return;
        if (!SelectionTools.ClipboardText.TrySet(text, secret: true))
        {
            _ctx.ShowMessage(CopyGlyph, "The clipboard is busy", error: true);
            return;
        }
        _ctx.ShowMessage(CopyGlyph, "Copied from your phone");
        _ctx.Info("Copied text from the phone");
    }

    void OpenLink(string raw)
    {
        if (LinkShare.Url(raw) is not { } url) return;
        if (!_ctx.Open(url))
        {
            _ctx.ShowMessage(LinkGlyph, "Couldn't open the link from your phone", error: true);
            return;
        }
        _ctx.ShowMessage(LinkGlyph, "Opened a link from your phone");
        _ctx.Info("Opened a link from the phone");
    }

    readonly object _fileLock = new();

    // under _fileLock: the file coming in (one at a time), written as its pieces arrive
    (IncomingFile File, FileStream To, string Temp)? _incoming;

    /// <summary>`file`, `fileChunk`, `fileEnd` (on the read loop, in order, so pieces are written as they come).</summary>
    void TakeFile(JsonObject m)
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
                    DropIncoming();
                    Directory.CreateDirectory(IncomingDir);
                    var temp = Path.Combine(IncomingDir, Path.GetRandomFileName());
                    _incoming = (f, new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None), temp);
                    return;
                }
                if (_incoming is not { } inc || LinkSession.Text(m, "id") != inc.File.Id) return;
                if (t == "fileChunk")
                {
                    int i = m["i"] is JsonValue iv && iv.TryGetValue<int>(out int n) ? n : -1;
                    var data = Convert.FromBase64String(m["data"] is JsonValue dv && dv.TryGetValue<string>(out var d) ? d : "");
                    if (!inc.File.Take(i, data)) throw new InvalidDataException("a piece out of order or past the size");
                    inc.To.Write(data);
                }
                else if (t == "fileEnd")
                {
                    bool whole = inc.File.Complete(LinkSession.Text(m, "sha256"));
                    inc.To.Dispose();
                    _incoming = null;
                    inc.File.Dispose();
                    if (!whole)
                    {
                        File.Delete(inc.Temp);
                        _ctx.Info("A file from the phone didn't match its size or SHA-256; dropped");
                        _ = _ctx.Ui.BeginInvoke(() => _ctx.ShowMessage(PhoneGlyph, "A file from your phone didn't arrive whole", error: true));
                        return;
                    }
                    var kept = Keep(inc.Temp, inc.File.Name);
                    _ctx.Info($"Received a file from the phone ({inc.File.Size / 1024} KB)");
                    _ = _ctx.Ui.BeginInvoke(() => ShowReceived(kept));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
            {
                DropIncoming();
                _ctx.Info($"Receiving a file from the phone stopped ({ex.GetType().Name})");
                _ = _ctx.Ui.BeginInvoke(() => _ctx.ShowMessage(PhoneGlyph, "A file from your phone didn't arrive", error: true));
            }
        }
    }

    /// <summary>Moves a checked file into Downloads\Shammy under its cleaned name, never over another, marked as from elsewhere.</summary>
    static string Keep(string temp, string name)
    {
        var dir = Path.Combine(Native.KnownFolder(Native.DownloadsFolder)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "Shammy");
        Directory.CreateDirectory(dir);
        string kept;
        while (true)
        {
            kept = DropPlan.Unique(dir, Path.GetFileNameWithoutExtension(name), Path.GetExtension(name), File.Exists);
            try
            {
                File.Move(temp, kept, overwrite: false);
                break;
            }
            catch (IOException) when (File.Exists(kept)) { /* taken a moment ago: the next free name */ }
        }
        // like a browser download, so Windows asks before running anything that came from the phone
        try { File.WriteAllText(kept + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
        return kept;
    }

    /// <summary>The connection ended mid-file (or a piece was wrong): what came so far goes.</summary>
    void DropIncoming()
    {
        lock (_fileLock)
        {
            if (_incoming is not { } inc) return;
            _incoming = null;
            inc.To.Dispose();
            inc.File.Dispose();
            try { File.Delete(inc.Temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    void ShowReceived(string path)
    {
        PhonePeek? card = null;
        void Close()
        {
            if (card is not null && ReferenceEquals(_ctx.Notch.OpenCardContent, card)) _ctx.CloseCard();
        }
        var size = File.Exists(path) ? DropPlan.Size(new FileInfo(path).Length) : "";
        card = new PhonePeek(PhoneChoiceSheet.Icons.File, "From your phone", PrivacyState.Presenting ? "A file" : Path.GetFileName(path),
            $"{size} · Downloads\\Shammy",
            ("Show in folder", () => { Close(); _ctx.Open("explorer.exe", $"/select,\"{path}\""); }),
            ("Open", () => { Close(); _ctx.Open(path); }));
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
        foreach (var big in new[] { "data", "art" })
            if (copy[big] is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 80) copy[big] = $"({text.Length} chars)";
        _lastSent[copy["t"]?.GetValue<string>() ?? "?"] = copy.ToJsonString();
    }

    void StartShare()
    {
        Connect.Links.Island = this;
        // leftovers of a transfer cut short when Shammy last closed
        try { if (Directory.Exists(IncomingDir)) Directory.Delete(IncomingDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    void StopShare()
    {
        if (Connect.Links.Island == this) Connect.Links.Island = null;
        DropIncoming();
    }

    void StartShareTests()
    {
        _ctx.AddPipeCommand("phonelink-sent", arg => arg.Trim() is { Length: > 0 } t
            ? _lastSent.TryGetValue(t, out var json) ? json : "none"
            : string.Join(" ", _lastSent.Keys.Order()));
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
            TakeFile(LinkShare.FileStart(id, Path.GetFileName(path), bytes.Length));
            int pieces = 0;
            for (int at = 0; at < bytes.Length; at += LinkShare.Chunk, pieces++)
                TakeFile(new JsonObject { ["t"] = "fileChunk", ["id"] = id, ["i"] = pieces, ["data"] = Convert.ToBase64String(bytes[at..Math.Min(at + LinkShare.Chunk, bytes.Length)]) });
            var sha = Convert.ToHexString(SHA256.HashData(p.Length > 1 ? new byte[] { 1 } : bytes));
            TakeFile(new JsonObject { ["t"] = "fileEnd", ["id"] = id, ["sha256"] = sha });
            return $"fed {pieces} pieces";
        });
    }
}
