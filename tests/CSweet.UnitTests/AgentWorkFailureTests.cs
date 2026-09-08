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
}
