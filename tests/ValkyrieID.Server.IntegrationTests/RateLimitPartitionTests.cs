using System.Net;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.IntegrationTests;

/// <summary>Rate-limit partitions: IPv6 clients are grouped by their /64, so rotating addresses doesn't bypass limits.</summary>
public sealed class RateLimitPartitionTests
{
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:3::1", "2001:db8:1:3::/64")]
    public void Addresses_map_to_their_partition(string address, string partition) =>
        Assert.Equal(partition, AccountExtensions.ClientPartition(IPAddress.Parse(address)));

    [Fact]
    public void Unknown_addresses_share_one_partition() => Assert.Equal("unknown", AccountExtensions.ClientPartition(null));
}
