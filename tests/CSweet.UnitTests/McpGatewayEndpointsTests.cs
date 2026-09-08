using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;

namespace CSweet.UnitTests;

public sealed class McpGatewayEndpointsTests
{
    [Fact]
    public void ArrayToolErrorRemainsReadableWhileSuccessfulPayloadIsValidated()
    {
        using var schema = System.Text.Json.JsonDocument.Parse("{\"type\":\"array\"}");
        using var denial = System.Text.Json.JsonDocument.Parse("{\"code\":\"Denied\",\"message\":\"No board read scope\"}");
        McpGatewayEndpoints.ValidateSuccessfulToolOutput(false, denial.RootElement, schema.RootElement);
        Assert.ThrowsAny<Exception>(() => McpGatewayEndpoints.ValidateSuccessfulToolOutput(true, denial.RootElement, schema.RootElement));
    }
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
