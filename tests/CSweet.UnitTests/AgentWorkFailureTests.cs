using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class AgentWorkFailureTests
{
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
}
