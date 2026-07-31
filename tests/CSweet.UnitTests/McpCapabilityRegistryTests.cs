using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.Communications;
using System.Text.Json;

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

    [Fact]
    public void TeamRosterTool_IsReadOnlyAndGrantGated()
    {
        var registry = new McpToolCatalog([]);

        Assert.DoesNotContain(
            registry.List(new HashSet<string>(StringComparer.Ordinal)),
            x => x.Capability == PlatformCapabilities.TeamRosterRead);
        var tool = Assert.Single(registry.List(
            new HashSet<string>([PlatformCapabilities.TeamRosterRead], StringComparer.Ordinal)));
        Assert.Equal("read_team_roster", tool.Name);
        Assert.Equal(McpToolExecutionPolicy.ReadOnly, tool.ExecutionPolicy);
    }

    [Fact]
    public void ResourceChangeSchema_AcceptsTheTypedSdkRequestIncludingRoleTeamId()
    {
        var registry = new McpToolCatalog([]);
        var tool = Assert.Single(registry.List(
            new HashSet<string>([PlatformCapabilities.ResourceChangePropose], StringComparer.Ordinal)));
        var request = new ResourceChangeProposalRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Validate a browser game prototype.",
            "A compact delivery team is required for the approved proof of concept.",
            1,
            [
                new ResourceChangeRole(
                    "web-game-developer",
                    "Product",
                    "Web Game Developer",
                    "Build the playable prototype.",
                    1,
                    1,
                    "Now",
                    ["web-game-development"],
                    false,
                    null,
                    null)
                {
                    TeamId = null
                }
            ],
            [],
            [],
            null,
            "resource-change-schema-test");

        JsonSchemaValidator.Validate(
            JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            tool.InputSchema);
    }
}
