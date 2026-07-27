using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;

namespace CSweet.UnitTests;

public sealed class McpCapabilityRegistryTests
{
    [Fact]
    public void BaselineToolsStillRequireAnExplicitGrant()
    {
        var registry = new McpToolCatalog([]);

        Assert.Empty(registry.List(new HashSet<string>(StringComparer.Ordinal)));
        var tool = Assert.Single(registry.List(
            new HashSet<string>([PlatformCapabilities.UserInputRequest], StringComparer.Ordinal)));

        Assert.Equal("ask_user", tool.Name);
        Assert.Equal(PlatformCapabilities.UserInputRequest, tool.Capability);
    }
}
