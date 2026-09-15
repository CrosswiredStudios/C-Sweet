using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class AgentWorkFailureTests
{
    [Theory]
    [InlineData("runtime.transport")]
    [InlineData("agent.unhandled")]
    [InlineData("agent.invalid_operation")]
    [InlineData("agent.payload_invalid")]
    public void NonRetryableFailureDoesNotPromiseRecovery(string code)
    {
        var message = AgentWorkFailure.DescribeBlocker($"agent-failure:v1;code={code};retryable=false");
        Assert.Contains("not eligible for automatic retry", message);
        Assert.DoesNotContain("will retry", message);
    }

    [Theory]
    [InlineData("91bcb8ee-7176-4f82-a196-40e4c0463fbd", true)]
    [InlineData("11111111-2222-3333-4444-555555555555", false)]
    public void StreamFailureRequiresMatchingDiagnosticEvidence(string logId, bool matched)
    {
        var message = AgentWorkFailure.DescribeBlocker(
            "agent-failure:v1;code=runtime.transport;retryable=true;diagnosticId=91bcb8ee-7176-4f82-a196-40e4c0463fbd",
            $"Work failure summary {logId}: HttpRequestException: Error while copying content to a stream.");
        Assert.Equal(matched, message.Contains("Error while copying content to a stream."));
        Assert.Contains("cause was not captured", message);
    }

    [Fact]
    public void PersistedInferenceFailureExplainsReadinessWithoutExposingDiagnosticBody()
    {
        var message = AgentWorkFailure.DescribeCollaborationFailure(
            "Collaboration failed because an agent turn could not continue: agent-failure:v1;code=platform.capability.unavailable;retryable=false;capability=platform.llm.chat-stream.v1;private=secret");
        Assert.Contains("model readiness", message);
        Assert.DoesNotContain("secret", message);
        Assert.DoesNotContain("agent-failure", message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("private exception body")]
    [InlineData("Collaboration failed because an agent turn could not continue: private exception body")]
    public void UnstructuredFailuresUseSafeFallback(string? summary)
    {
        var message = AgentWorkFailure.DescribeCollaborationFailure(summary);
        Assert.Contains("Review the agent's diagnostics", message);
        Assert.DoesNotContain("private", message);
    }

    [Theory]
    [InlineData("agent-failure:v1;code=runtime.transport;retryable=true", true)]
    [InlineData("agent-failure:v1;code=agent.invalid_operation", true)]
    [InlineData("agent-failure:v1;code=agent.payload_invalid", true)]
    [InlineData("agent-failure:v1;code=agent.unhandled", true)]
    [InlineData("agent-failure:v1;code=platform.capability.unavailable;retryable=false", false)]
    [InlineData("private exception body", false)]
    public void AttentionReviewRecoveryIsBoundedToRecoverableAgentFailures(string error, bool expected) =>
        Assert.Equal(expected, AgentWorkFailure.IsRecoverableAtAttentionReview(error));

    [Theory]
    [InlineData("agent-failure:v1;code=agent.invalid_operation;diagnosticId=test", "structured result")]
    [InlineData("agent-failure:v1;code=agent.payload_invalid;diagnosticId=test", "invalid structured result")]
    [InlineData("agent-failure:v1;code=runtime.transport;retryable=true;diagnosticId=test", "retry this task automatically")]
    public void RecoverableFailuresExplainAutomaticAttentionRecovery(string error, string expected) =>
        Assert.Contains(expected, AgentWorkFailure.DescribeBlocker(error));

    [Fact]
    public void RecoverableRuntimeFailureNamesTheSafeFailureTypeAndDiagnostic()
    {
        var message = AgentWorkFailure.DescribeBlocker(
            "agent-failure:v1;code=runtime.transport;retryable=true;exceptionType=HttpRequestException;httpStatus=502;diagnosticId=11111111-2222-3333-4444-555555555555");

        Assert.Contains("**Failure type:** `HttpRequestException`", message);
        Assert.Contains("**HTTP status:** `502`", message);
        Assert.Contains("**Diagnostic ID:** `11111111-2222-3333-4444-555555555555`", message);
        Assert.Contains("retry this task automatically", message);
    }

    [Fact]
    public void RecoverableFailureDoesNotRenderUntrustedFailureFields()
    {
        var message = AgentWorkFailure.DescribeBlocker(
            "agent-failure:v1;code=runtime.transport;retryable=true;exceptionType=HttpRequestException**spoof;httpStatus=999;diagnosticId=not-a-guid");

        Assert.DoesNotContain("spoof", message);
        Assert.DoesNotContain("999", message);
        Assert.DoesNotContain("not-a-guid", message);
    }
}
