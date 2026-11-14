using System.Net;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Core.PhoneSend;
using Shammy.Interop;
using Shammy.Shell;

namespace Shammy.Modules.PhoneSend;

/// <summary>
/// One "send to phone": the server, its card, and the rules for when both go away. The server only
/// exists while the card does, so nothing listens on the network when you're not looking at a QR code.
/// </summary>
internal sealed class PhoneTransfer : IDisposable
{
    static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(10);
    static readonly TimeSpan LingerAfterSent = TimeSpan.FromSeconds(3);
    static readonly TimeSpan UploadGrace = TimeSpan.FromSeconds(20);

    readonly ModuleContext _ctx;
    readonly PhoneTransferServer _server;
    readonly PhoneSendCard _card;
    readonly DispatcherTimer _timer;
    readonly List<string> _received = new();
    DateTime? _sentAt;
    bool _stopped;

    PhoneTransfer(ModuleContext ctx, string path, IPAddress address)
    {
        _ctx = ctx;
        FilePath = path;
        _server = new PhoneTransferServer(new PhoneTransferOptions
        {
            FilePath = path,
            UploadDirectory = DownloadsFolder(),
            // Only the Wi-Fi/LAN adapter the QR advertises, not VPNs, Hyper-V or any public interface.
            BindAddress = address,
            // Unauthenticated clients that never finish a request line shouldn't hold slots for long.
            HeaderTimeout = TimeSpan.FromSeconds(5),
        });
        _server.FileReceived += p => ctx.Ui.BeginInvoke(() => OnReceived(p));
        _server.Warning += (message, ex) => ctx.Warn(message, ex);
        _server.Start();
        // Test pipe only: an example address on the card, so screenshots don't show where this PC really is.
        var shown = Infrastructure.CommandServer.IsEnabled && Environment.GetEnvironmentVariable("SHAMMY_TEST_LAN_ADDRESS") is { Length: > 0 } test ? test : address.ToString();
        Url = $"http://{shown}:{_server.Port}/{_server.Token}";
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Tick(), ctx.Ui);
        try
        {
            _card = new PhoneSendCard(Url, Path.GetFileName(path), _server.FileLength);
            _card.CloseRequested += () => Stop();
            ctx.ShowCard(_card, PhoneSendCard.CardWidth, PhoneSendCard.CardHeight, interactive: true);
        }
        catch
        {
            _server.Dispose();
            throw;
        }
        _timer.Start();
        ctx.Info($"Sharing {Path.GetFileName(path)} on port {_server.Port} via {address}");
    }

    public string Url { get; }
    public string FilePath { get; }
    public bool IsRunning => !_stopped;
    public long BytesSent => _server.BytesSent;
    public long FileLength => _server.FileLength;
    public int CompletedDownloads => _server.CompletedDownloads;
    public int ActiveDownloads => _server.ActiveDownloads;
    public int FilesReceived => _server.FilesReceived;

    /// <summary>The card closed and the server stopped, for whatever reason.</summary>
    public event Action? Ended;

    /// <summary>Starts sharing <paramref name="path"/>. Throws <see cref="PhoneSendException"/> with a user-facing reason.</summary>
    public static PhoneTransfer Start(ModuleContext ctx, string path)
    {
        if (Directory.Exists(path)) throw new PhoneSendException("Folders can't be sent yet");
        if (!File.Exists(path)) throw new PhoneSendException("That file no longer exists");
        var address = LanAddressPicker.Pick(LanAddressPicker.FromSystem())
            ?? throw new PhoneSendException("Connect to Wi-Fi first");
        try
        {
            return new PhoneTransfer(ctx, path, address);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException)
        {
            ctx.Warn("Starting the transfer server failed", ex);
            throw new PhoneSendException("Couldn't start sharing");
        }
    }

    void Tick()
    {
        if (_stopped) return;
        if (!CardIsOpen())
        {
            // Closed from outside: clicked elsewhere, or another module took the card.
            Stop(closeCard: false);
            return;
        }

        var now = DateTime.UtcNow;
        if (_server.CompletedDownloads > 0)
        {
            if (_sentAt is null)
            {
                _sentAt = now;
                _card.ShowSent();
            }
            // Wait out uploads too, and give a phone that has just finished downloading a moment to send
            // something back before the page goes dead.
            else if (now - _sentAt >= LingerAfterSent && _server.ActiveDownloads == 0 && _server.ActiveUploads == 0 &&
                     now - _server.LastActivityUtc >= UploadGrace)
            {
                Stop();
                return;
            }
        }
        else if (_server.ActiveDownloads > 0 || _server.BytesSent > 0)
        {
            _card.SetProgress(_server.BytesSent, _server.FileLength);
        }

        if (now - _server.LastActivityUtc > IdleLimit && _server.ActiveDownloads == 0 && _server.ActiveUploads == 0)
        {
            _ctx.Info("Transfer idle for 10 minutes; stopping");
            Stop();
        }
    }

    /// <summary>
    /// The host has no "card closed" event, so infer it: the card is gone if our content was replaced,
    /// or the notch is in a mode it can only reach with no card open. Search, drag and fullscreen modes
    /// temporarily cover a card without closing it, so they don't count.
    /// </summary>
    bool CardIsOpen() =>
        _card.Parent is not null
        && _ctx.Notch.Mode is not (NotchMode.Idle or NotchMode.Compact or NotchMode.Notification or NotchMode.Expanded);

    void OnReceived(string path)
    {
        var name = Path.GetFileName(path);
        _received.Add(path);
        // What the phone sent lands on the shelf too, ready to drag into whatever needs it.
        _ctx.Services.Shelf.Add(new[] { path });
        var text = _received.Count == 1 ? $"Received {name}" : $"Received {_received.Count} files";
        if (!_stopped) _card.ShowReceived(text);
        // Priority 45: above downloads (40), below messages (50).
        _ctx.Post(new Activity($"{_ctx.Name}.received", ActivityKind.Message, 45,
            new MessagePayload("", text), DateTime.UtcNow.AddSeconds(6))
        {
            Click = () => Shelf.ShelfStore.ShowInFolder(path),
        });
        _ctx.Info("Received a file from the phone");
    }

    public void Stop(bool closeCard = true)
    {
        if (_stopped) return;
        _stopped = true;
        _timer.Stop();
        _server.Dispose();
        // Only close the card if it's still ours; another module may have replaced it.
        if (closeCard && _card.Parent is not null) _ctx.CloseCard();
        _ctx.Info("Transfer stopped");
        Ended?.Invoke();
    }

    // On app exit the notch is going away too; just make sure the port is released.
    public void Dispose() => Stop(closeCard: false);

    static string DownloadsFolder() =>
        Native.KnownFolder(Native.DownloadsFolder)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}

/// <summary>A reason to show the user, e.g. "Connect to Wi-Fi first".</summary>
internal sealed class PhoneSendException(string message) : Exception(message);
