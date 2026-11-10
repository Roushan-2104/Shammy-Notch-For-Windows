using System.Net;
using System.Net.Sockets;

namespace Shammy.Core.Updates;

/// <summary>
/// The order in which the updater tries a server's addresses. Some connections hand out IPv6 addresses that
/// lead nowhere: each attempt then hangs for about twenty seconds before the working IPv4 one is tried, and an
/// update check makes many requests. So IPv4 goes first, every address gets only a few seconds, and IPv6 stays
/// as the fallback for networks that have nothing else.
/// </summary>
public static class ConnectOrder
{
    /// <summary>How long one address gets before the next is tried.</summary>
    public static readonly TimeSpan PerAddress = TimeSpan.FromSeconds(6);

    /// <summary>IPv4 addresses first, then the rest, each group in the order the lookup gave them.</summary>
    public static IReadOnlyList<IPAddress> Sort(IEnumerable<IPAddress> addresses)
    {
        var all = addresses.ToList();
        var first = all.Where(a => a.AddressFamily == AddressFamily.InterNetwork);
        var rest = all.Where(a => a.AddressFamily != AddressFamily.InterNetwork);
        return first.Concat(rest).ToList();
    }
}
