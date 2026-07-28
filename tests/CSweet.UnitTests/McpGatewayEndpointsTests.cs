using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;

namespace CSweet.UnitTests;

public sealed class McpGatewayEndpointsTests
{
    [Fact]
    public void GetToolResponseText_UsesCapabilityErrorWhenFailurePayloadIsEmpty()
    {
        var result = new CapabilityResult
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Succeeded = false,
            Error = "The selected model is not approved for this provider profile."
        };

        var text = McpGatewayEndpoints.GetToolResponseText(result);

        Assert.Equal(result.Error, text);
    }

    [Fact]
    public void GetToolResponseText_NeverReturnsBlankForUnspecifiedFailure()
    {
        var result = new CapabilityResult
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Succeeded = false
        };

        var text = McpGatewayEndpoints.GetToolResponseText(result);

        Assert.Equal("The platform capability failed without an error message.", text);
    }
}
