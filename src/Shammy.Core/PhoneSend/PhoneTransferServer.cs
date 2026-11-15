using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Shammy.Core.PhoneSend;

public sealed class PhoneTransferOptions
{
    /// <summary>The file offered for download; null for a receive-only page.</summary>
    public string? FilePath { get; init; }

    /// <summary>Where uploads land. Nothing is ever written anywhere else.</summary>
    public required string UploadDirectory { get; init; }

    public IPAddress BindAddress { get; init; } = IPAddress.Any;
    public int PortMin { get; init; } = 49200;
    public int PortMax { get; init; } = 49900;
    public string Token { get; init; } = TransferToken.New();
    public long MaxUploadBytes { get; init; } = 4L << 30;

    /// <summary>Whether the page offers (and the server accepts) uploads to the PC; off when only handing out a file.</summary>
    public bool AllowUploads { get; init; } = true;

    /// <summary>A line under the file's name on the page (e.g. the app's signing fingerprint), or null.</summary>
    public string? Note { get; init; }
    public int MaxConnections { get; init; } = 16;

    /// <summary>A client must finish sending its request head within this.</summary>
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>A transfer that makes no progress for this long is dropped (phone left the Wi-Fi...).</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// A deliberately tiny HTTP/1.1 server for one transfer: a landing page, the file, and an upload form,
/// all behind an unguessable token. Built on TcpListener because HttpListener needs an admin URL ACL to
/// listen on anything but localhost. One request per connection ("Connection: close") keeps it simple.
/// </summary>
public sealed class PhoneTransferServer : IDisposable
{
    const int ChunkSize = 64 * 1024;

    readonly PhoneTransferOptions _o;
    readonly CancellationTokenSource _cts = new();
    readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    readonly object _progressGate = new();
    TcpListener? _listener;
    int _connections, _activeDownloads, _activeUploads, _completedDownloads, _received;
    long _contiguous, _lastActivityTicks;

    public PhoneTransferServer(PhoneTransferOptions options)
    {
        _o = options;
        FileLength = options.FilePath is { } p && File.Exists(p) ? new FileInfo(p).Length : 0;
        _lastActivityTicks = DateTime.UtcNow.Ticks;
    }

    public string Token => _o.Token;
    public int Port { get; private set; }
    public bool IsRunning { get; private set; }
    public long FileLength { get; }

    /// <summary>How far into the file the phone has got, counting resumed ranges.</summary>
    public long BytesSent => Interlocked.Read(ref _contiguous);

    public int ActiveDownloads => Volatile.Read(ref _activeDownloads);

    /// <summary>Uploads being received right now; the transfer must not stop under them.</summary>
    public int ActiveUploads => Volatile.Read(ref _activeUploads);
    public int CompletedDownloads => Volatile.Read(ref _completedDownloads);
    public int FilesReceived => Volatile.Read(ref _received);
    public DateTime LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    /// <summary>A file was saved (full path). Raised on a background thread.</summary>
    public event Action<string>? FileReceived;

    /// <summary>Something unexpected went wrong with one connection (the server keeps running).</summary>
    public event Action<string, Exception>? Warning;

    /// <summary>Binds a random free port in the configured range and starts accepting.</summary>
    public void Start()
    {
        if (IsRunning) return;
        var ports = Enumerable.Range(_o.PortMin, _o.PortMax - _o.PortMin + 1).OrderBy(_ => Random.Shared.Next()).Take(40);
        foreach (var port in ports)
        {
            var listener = new TcpListener(_o.BindAddress, port) { ExclusiveAddressUse = true };
            try
            {
                listener.Start(32);
            }
            catch (SocketException)
            {
                // In use, or reserved by Hyper-V's excluded port ranges; try another.
                continue;
            }
            _listener = listener;
            Port = port;
            IsRunning = true;
            _ = Task.Run(AcceptLoopAsync);
            return;
        }
        throw new IOException($"No free port between {_o.PortMin} and {_o.PortMax}");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _cts.Cancel();
        try { _listener?.Stop(); }
        catch (SocketException) { }
        foreach (var c in _clients.Keys) c.Dispose();
        _clients.Clear();
    }

    // _cts is deliberately not disposed: in-flight handlers may still link to its token after Stop().
    public void Dispose() => Stop();

    void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    async Task AcceptLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                if (ct.IsCancellationRequested) return;
                continue;
            }

            if (Interlocked.Increment(ref _connections) > _o.MaxConnections)
            {
                Interlocked.Decrement(ref _connections);
                client.Dispose();
                continue;
            }
            _clients[client] = 0;
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    async Task HandleClientAsync(TcpClient client)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            cts.CancelAfter(_o.HeaderTimeout);
            await HandleRequestAsync(stream, cts, cts.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
        {
            // Phones vanish mid-transfer all the time; that's not worth a log line.
        }
        catch (Exception ex)
        {
            Warning?.Invoke("connection failed", ex);
        }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Dispose();
            Interlocked.Decrement(ref _connections);
        }
    }

    async Task HandleRequestAsync(NetworkStream stream, CancellationTokenSource cts, CancellationToken ct)
    {
        var buf = new byte[HttpRequestHead.MaxHeadBytes];
        int len = 0, headEnd;
        while ((headEnd = HttpRequestHead.FindHeadEnd(buf.AsSpan(0, len))) < 0)
        {
            if (len == buf.Length)
            {
                await SendTextAsync(stream, 431, "Request Header Fields Too Large", "text/plain", "Too large", false, ct);
                return;
            }
            int n = await stream.ReadAsync(buf.AsMemory(len), ct);
            if (n == 0) return;
            len += n;
        }
        if (!HttpRequestHead.TryParse(buf.AsSpan(0, headEnd), out var head) || head is null)
        {
            await SendTextAsync(stream, 400, "Bad Request", "text/plain", "Bad request", false, ct);
            return;
        }

        var segments = head.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool authorized = segments.Length is 1 or 2 && TransferToken.Matches(_o.Token, segments[0]);
        if (!authorized)
        {
            await NotFoundAsync(stream, head, ct);
            return;
        }
        Touch();
        bool isHead = head.Method == "HEAD";
        bool isGet = head.Method == "GET" || isHead;

        if (segments.Length == 1 && isGet)
        {
            var name = _o.FilePath is { } p ? Path.GetFileName(p) : null;
            await SendTextAsync(stream, 200, "OK", "text/html; charset=utf-8", PhonePage.Transfer(_o.Token, name, FileLength, _o.AllowUploads, _o.Note), isHead, ct, html: true);
        }
        else if (segments.Length == 2 && segments[1] == "file" && isGet && _o.FilePath is not null)
        {
            await SendFileAsync(stream, head, isHead, cts, ct);
        }
        else if (segments.Length == 2 && segments[1] == "upload" && head.Method == "POST" && _o.AllowUploads)
        {
            await ReceiveAsync(stream, head, buf, headEnd, len, cts, ct);
        }
        else
        {
            await NotFoundAsync(stream, head, ct);
        }
    }

    Task NotFoundAsync(Stream stream, HttpRequestHead head, CancellationToken ct) =>
        SendTextAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", "Not found", head.Method == "HEAD", ct);

    // ---- Download ----------------------------------------------------------------------------------

    async Task SendFileAsync(Stream stream, HttpRequestHead head, bool isHead, CancellationTokenSource cts, CancellationToken ct)
    {
        var path = _o.FilePath!;
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ChunkSize, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await SendTextAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", "The file is no longer available", isHead, ct);
            return;
        }

        await using var _ = file;
        long length = file.Length;
        // We send no validators, so an If-Range can never be confirmed; RFC 9110 says send everything.
        var range = head["If-Range"] is null ? head["Range"] : null;
        var kind = ByteRange.Parse(range, length, out long start, out long end);
        if (kind == ByteRange.Kind.Unsatisfiable)
        {
            await WriteHeadAsync(stream, 416, "Range Not Satisfiable", new[]
            {
                ("Content-Range", $"bytes */{length}"),
                ("Content-Length", "0"),
            }, ct);
            return;
        }
        if (kind == ByteRange.Kind.None) (start, end) = (0, length - 1);
        long count = length == 0 ? 0 : end - start + 1;

        var name = Path.GetFileName(path);
        var headers = new List<(string, string)>
        {
            ("Content-Type", MimeTypes.ForFile(name)),
            ("Content-Length", count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Content-Disposition", ContentDisposition.Attachment(name)),
            ("Accept-Ranges", "bytes"),
            ("Cache-Control", "no-store"),
            ("X-Content-Type-Options", "nosniff"),
        };
        if (kind == ByteRange.Kind.Satisfiable) headers.Add(("Content-Range", $"bytes {start}-{end}/{length}"));
        await WriteHeadAsync(stream, kind == ByteRange.Kind.Satisfiable ? 206 : 200,
            kind == ByteRange.Kind.Satisfiable ? "Partial Content" : "OK", headers, ct);
        if (isHead) return;

        Interlocked.Increment(ref _activeDownloads);
        try
        {
            file.Seek(start, SeekOrigin.Begin);
            var chunk = new byte[ChunkSize];
            long sent = 0;
            while (sent < count)
            {
                int n = await file.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, count - sent)), ct);
                if (n == 0) throw new IOException("File shrank while sending");
                await stream.WriteAsync(chunk.AsMemory(0, n), ct);
                sent += n;
                cts.CancelAfter(_o.StallTimeout);
                Touch();
                lock (_progressGate)
                {
                    // Only count bytes that extend what the phone already has from the start, so a video
                    // player probing the last few KB doesn't look like a finished download.
                    if (start <= _contiguous && start + sent > _contiguous) Interlocked.Exchange(ref _contiguous, start + sent);
                }
            }
            if (end == length - 1 && Interlocked.Read(ref _contiguous) >= length) Interlocked.Increment(ref _completedDownloads);
        }
        finally
        {
            Interlocked.Decrement(ref _activeDownloads);
        }
    }

    // ---- Upload ------------------------------------------------------------------------------------

    async Task ReceiveAsync(NetworkStream stream, HttpRequestHead head, byte[] buf, int headEnd, int len,
        CancellationTokenSource cts, CancellationToken ct)
    {
        if (head.IsChunked || head.ContentLength is not { } contentLength)
        {
            await SendTextAsync(stream, 411, "Length Required", "text/plain", "Length required", false, ct);
            await LingerAsync(stream);
            return;
        }
        if (contentLength > _o.MaxUploadBytes)
        {
            await SendTextAsync(stream, 413, "Content Too Large", "text/html; charset=utf-8",
                PhonePage.Uploaded(_o.Token, Array.Empty<string>(), $"That's more than {ByteSize.Format(_o.MaxUploadBytes)}."), false, ct, html: true);
            await LingerAsync(stream);
            return;
        }
        if (MultipartReader.BoundaryFrom(head["Content-Type"]) is not { } boundary)
        {
            await SendTextAsync(stream, 400, "Bad Request", "text/plain", "Expected multipart/form-data", false, ct);
            await LingerAsync(stream);
            return;
        }
        Directory.CreateDirectory(_o.UploadDirectory);
        if (FreeSpace(_o.UploadDirectory) is { } free && free < contentLength)
        {
            await SendTextAsync(stream, 507, "Insufficient Storage", "text/html; charset=utf-8",
                PhonePage.Uploaded(_o.Token, Array.Empty<string>(), "The PC's disk is full."), false, ct, html: true);
            await LingerAsync(stream);
            return;
        }
        if (head.ExpectsContinue) await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct);

        cts.CancelAfter(_o.StallTimeout);
        Interlocked.Increment(ref _activeUploads);
        try
        {
            await ReceiveBodyAsync(stream, buf, headEnd, len, contentLength, boundary, cts, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _activeUploads);
        }
    }

    async Task ReceiveBodyAsync(NetworkStream stream, byte[] buf, int headEnd, int len, long contentLength, string boundary,
        CancellationTokenSource cts, CancellationToken ct)
    {
        var body = new BodyStream(buf, headEnd, len, stream, contentLength);
        var reader = new MultipartReader(body, boundary);
        var saved = new List<string>();
        string? error = null;
        try
        {
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                // An empty file input still submits a part with filename="".
                if (string.IsNullOrEmpty(section.FileName)) continue;
                var path = await SaveSectionAsync(reader, SafeFileName.Sanitize(section.FileName, "upload"), cts, ct);
                saved.Add(Path.GetFileName(path));
                Interlocked.Increment(ref _received);
                FileReceived?.Invoke(path);
            }
        }
        catch (InvalidDataException ex)
        {
            error = saved.Count > 0 ? "Some files didn't arrive." : "The upload didn't complete.";
            Warning?.Invoke("upload rejected: " + ex.Message, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !ct.IsCancellationRequested)
        {
            // Disk filled up mid-upload, or Downloads isn't writable: tell the phone instead of resetting.
            error = "The PC couldn't save the file (disk full?).";
            Warning?.Invoke("upload save failed", ex);
        }
        await SendTextAsync(stream, saved.Count > 0 || error is null ? 200 : 400, saved.Count > 0 || error is null ? "OK" : "Bad Request",
            "text/html; charset=utf-8", PhonePage.Uploaded(_o.Token, saved, error), false, ct, html: true);
        // The closing boundary's trailing CRLF (or an aborted body) may still be unread.
        await LingerAsync(stream);
    }

    /// <summary>
    /// Closing a socket with unread request bytes makes Windows send a reset, and the phone then shows a
    /// connection error instead of our response. Half-close and swallow what's left (briefly) first.
    /// </summary>
    static async Task LingerAsync(NetworkStream stream)
    {
        try
        {
            stream.Socket.Shutdown(SocketShutdown.Send);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var buf = new byte[16 * 1024];
            long drained = 0;
            while (drained < 8 << 20)
            {
                int n = await stream.ReadAsync(buf, cts.Token);
                if (n == 0) break;
                drained += n;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    async Task<string> SaveSectionAsync(MultipartReader reader, string name, CancellationTokenSource cts, CancellationToken ct)
    {
        var dir = _o.UploadDirectory;
        // Not ".part": Shammy's Downloads watcher treats those as browser downloads and would announce it twice.
        var temp = Path.Combine(dir, $".shammy-{Guid.NewGuid():N}.tmp");
        if (!SafeFileName.IsDirectlyInside(dir, temp)) throw new InvalidDataException("Bad upload path");
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, useAsync: true))
            {
                await reader.CopyBodyToAsync(fs, _o.MaxUploadBytes, _ =>
                {
                    cts.CancelAfter(_o.StallTimeout);
                    Touch();
                }, ct);
            }
            // Mark-of-the-Web: whatever arrives over the network gets SmartScreen / Protected View checks
            // when opened, exactly like a browser download. The stream moves with the file on rename.
            try { File.WriteAllText(temp + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
            name = SafeFileName.NeutralizeExecutable(name);
            // Pick the final name only now, and retry if another upload took it in the meantime.
            for (int attempt = 0; ; attempt++)
            {
                var final = SafeFileName.MakeUnique(dir, name, p => File.Exists(p) || Directory.Exists(p));
                if (!SafeFileName.IsDirectlyInside(dir, final)) throw new InvalidDataException("Bad upload path");
                try
                {
                    File.Move(temp, final, overwrite: false);
                    return final;
                }
                catch (IOException) when (attempt < 5 && File.Exists(final))
                {
                }
            }
        }
        catch
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    static long? FreeSpace(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- Responses ---------------------------------------------------------------------------------

    static Task WriteHeadAsync(Stream stream, int status, string reason, IEnumerable<(string Name, string Value)> headers, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        foreach (var (n, v) in headers) sb.Append(n).Append(": ").Append(v).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        return stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).AsTask();
    }

    static async Task SendTextAsync(Stream stream, int status, string reason, string contentType, string body, bool isHead,
        CancellationToken ct, bool html = false)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var headers = new List<(string, string)>
        {
            ("Content-Type", contentType),
            ("Content-Length", bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Cache-Control", "no-store"),
            ("X-Content-Type-Options", "nosniff"),
            ("Referrer-Policy", "no-referrer"),
        };
        if (html)
        {
            headers.Add(("Content-Security-Policy",
                "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'"));
        }
        await WriteHeadAsync(stream, status, reason, headers, ct);
        if (!isHead) await stream.WriteAsync(bytes, ct);
    }

    /// <summary>The request body: bytes that arrived with the head, then the socket, capped at Content-Length.</summary>
    sealed class BodyStream : Stream
    {
        readonly byte[] _prefix;
        readonly Stream _inner;
        int _pos;
        readonly int _prefixEnd;
        long _remaining;

        public BodyStream(byte[] prefix, int start, int end, Stream inner, long length)
        {
            _prefix = prefix;
            _pos = start;
            _prefixEnd = end;
            _inner = inner;
            _remaining = length;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_remaining <= 0 || buffer.Length == 0) return 0;
            int max = (int)Math.Min(buffer.Length, _remaining);
            int n;
            if (_pos < _prefixEnd)
            {
                n = Math.Min(max, _prefixEnd - _pos);
                _prefix.AsSpan(_pos, n).CopyTo(buffer.Span);
                _pos += n;
            }
            else
            {
                n = await _inner.ReadAsync(buffer[..max], ct);
            }
            _remaining -= n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
