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
    [Theory]
    [InlineData("{\"code\":\"Denied\",\"message\":\"The project delivery grant is required\"}", "platform.capability.denied")]
    [InlineData("{\"code\":\"NotFound\",\"message\":\"Board was not found.\"}", "platform.capability.not_found")]
    [InlineData("{\"code\":\"ValidationFailed\",\"message\":\"bad\"}", "platform.capability.validation_failed")]
    [InlineData("{\"message\":\"no code\"}", null)]
    [InlineData("[1]", null)]
    [InlineData("{\"code\":\"not;a code\"}", null)]
    public void A_capability_error_payload_carries_its_code_to_the_runtime(string payload, string? expected) =>
        // Without it a refusal reaches the agent as a generic capability.failed and the ticket says
        // "execution stopped unexpectedly" (Prism Break, 2026-10-08).
        Assert.Equal(expected, McpGatewayEndpoints.FailureCodeFromPayload(System.Text.Json.Nodes.JsonNode.Parse(payload)));

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

    [Fact]
    public void GetToolResponseText_SummarizesPayloadAboveConfiguredInlineLimit()
    {
        var result = new CapabilityResult
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Succeeded = true,
            Payload = JsonPayload.From(System.Text.Encoding.UTF8.GetBytes("{\"value\":\"large\"}"))
        };

        var text = McpGatewayEndpoints.GetToolResponseText(result, 4);

        Assert.Equal($"The capability returned {result.Payload.Length} bytes in structuredContent.", text);
    }
}
