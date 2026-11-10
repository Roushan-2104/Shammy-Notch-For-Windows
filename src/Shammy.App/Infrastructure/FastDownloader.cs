using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Shammy.Core.Updates;
using Velopack.Sources;

namespace Shammy.Infrastructure;

/// <summary>
/// The updater's web requests, made quicker on awkward connections. Velopack opens a brand-new connection for
/// every request, and an update check asks GitHub about each recent release in turn; where a connection first
/// tries an address that leads nowhere (see <see cref="ConnectOrder"/>), every one of those requests waited
/// about twenty seconds and the check ran out of time. Here all requests share one pool of connections, so a
/// server is connected to once, and each connection tries the working kind of address first.
/// </summary>
internal sealed class FastDownloader : HttpClientFileDownloader
{
    static readonly SocketsHttpHandler Shared = new()
    {
        // As Velopack's own handler is set up.
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectCallback = ConnectAsync,
        // Long enough for one check and its download to share connections; short enough to follow DNS changes.
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    };

    protected override HttpClient CreateHttpClient(IDictionary<string, string>? headers, double timeout)
    {
        // The handler outlives each client: disposing the client must not close the shared connections.
        var client = new HttpClient(Shared, disposeHandler: false);
        client.DefaultRequestHeaders.UserAgent.Add(UserAgent);
        foreach (var header in headers ?? new Dictionary<string, string>()) client.DefaultRequestHeaders.Add(header.Key, header.Value);
        client.Timeout = TimeSpan.FromMinutes(timeout);
        return client;
    }

    /// <summary>Tries the server's addresses in <see cref="ConnectOrder"/>'s order, a few seconds each.</summary>
    internal static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancel)
    {
        var addresses = ConnectOrder.Sort(await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancel).ConfigureAwait(false));
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(ConnectOrder.PerAddress);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), limit.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                last = ex;
                // The whole request was cancelled, not just this address's few seconds.
                cancel.ThrowIfCancellationRequested();
            }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }
}
