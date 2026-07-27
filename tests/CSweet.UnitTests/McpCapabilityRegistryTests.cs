using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.Communications;

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

    [Fact]
    public void SharedCommunicationCapabilities_AreNotClaimedByTheWorkforceHandler()
    {
        var workforce = new WorkforcePlatformCapabilityHandler(null!, null!, [], []);
        var communications = new CommunicationHubCapabilityHandler(null!, null!);
        IPlatformCapabilityHandler[] handlers = [workforce, communications];

        Assert.False(workforce.CanHandle(PlatformCapabilities.UserInputRequest));
        Assert.False(workforce.CanHandle(PlatformCapabilities.UserActionSuggest));
        Assert.Same(
            communications,
            Assert.Single(handlers, x => x.CanHandle(SuggestedUserActionCapabilities.Suggest)));
    }
}
