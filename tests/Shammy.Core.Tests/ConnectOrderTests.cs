using System.Net;
using Shammy.Core.Updates;

namespace Shammy.Core.Tests;

public class ConnectOrderTests
{
    static readonly IPAddress V4a = IPAddress.Parse("20.207.73.85"), V4b = IPAddress.Parse("140.82.112.3");
    static readonly IPAddress V6a = IPAddress.Parse("64:ff9b::14cf:4955"), V6b = IPAddress.Parse("2606:50c0:8000::154");

    [Fact]
    public void Ipv4_goes_first_however_the_lookup_ordered_them() =>
        Assert.Equal(new[] { V4a, V4b, V6a, V6b }, ConnectOrder.Sort(new[] { V6a, V4a, V6b, V4b }));

    [Fact]
    public void A_network_with_only_ipv6_still_gets_its_addresses() =>
        Assert.Equal(new[] { V6b, V6a }, ConnectOrder.Sort(new[] { V6b, V6a }));

    [Fact]
    public void Nothing_in_nothing_out() => Assert.Empty(ConnectOrder.Sort(Array.Empty<IPAddress>()));

    [Fact]
    public void A_dead_address_costs_seconds_not_the_whole_check()
    {
        // The updater gives a whole check two minutes; ten dead addresses in a row must still leave room.
        Assert.InRange(ConnectOrder.PerAddress.TotalSeconds, 3, 8);
        Assert.True(ConnectOrder.PerAddress.TotalSeconds * 10 < 120);
    }
}
