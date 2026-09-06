using CSweet.Infrastructure.Setup;
using System.Net;
using CSweet.AgentHost.Broker;

namespace CSweet.UnitTests;

public sealed class OutboundNetworkPolicyTests
{
    [Theory]
    [InlineData("/api", "/api", true)]
    [InlineData("/api/users", "/api", true)]
    [InlineData("/api/", "/api/", true)]
    [InlineData("/apievil", "/api", false)]
    [InlineData("/api-v2", "/api", false)]
    public void Path_prefixes_require_a_segment_boundary(string path, string prefix, bool expected)
    {
        Assert.Equal(expected, OutboundNetworkPolicy.IsPathWithinPrefix(path, prefix));
    }

    [Fact]
    public void Origins_are_normalized_before_credential_binding()
    {
        var destination = new Uri("https://EXAMPLE.com./resource");

        Assert.True(OutboundNetworkPolicy.IsAllowedOrigin(destination, ["https://example.com"]));
        Assert.False(OutboundNetworkPolicy.IsAllowedOrigin(destination, ["https://example.com:8443"]));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("fc00::1")]
    public void Built_in_private_and_reserved_ranges_are_blocked(string address)
    {
        Assert.True(OutboundNetworkPolicy.IsForbiddenAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public void Configured_cidrs_extend_the_block_list()
    {
        var blocked = OutboundNetworkPolicy.ParseCidrs("203.0.113.0/24; 2001:db8::/32");

        Assert.True(OutboundNetworkPolicy.IsForbiddenAddress(IPAddress.Parse("203.0.113.17"), blocked));
        Assert.True(OutboundNetworkPolicy.IsForbiddenAddress(IPAddress.Parse("2001:db8::17"), blocked));
        Assert.False(OutboundNetworkPolicy.IsForbiddenAddress(IPAddress.Parse("8.8.8.8"), blocked));
    }
}
