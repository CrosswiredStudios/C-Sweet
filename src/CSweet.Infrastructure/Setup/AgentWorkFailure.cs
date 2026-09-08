namespace CSweet.Infrastructure.Setup;

/// <summary>Interprets the SDK's structured failure envelope without exposing exception text.</summary>
public static class AgentWorkFailure
{
    public static bool IsNonRetryable(string? error) =>
        error?.StartsWith("agent-failure:v1;", StringComparison.Ordinal) == true &&
        error.Split(';').Contains("retryable=false", StringComparer.Ordinal);

    public static string DescribeBlocker(string? error)
    {
        var capability = error?.Split(';').FirstOrDefault(x => x.StartsWith("capability=", StringComparison.Ordinal))?[11..];
        return capability switch
        {
            "platform.llm.chat-stream.v1" => "Inference could not run. Check the configured provider's model readiness and inference diagnostics, then retry this task.",
            "platform.decision.decide.v1" => "The project decision could not be approved. Check its authority and current revision before retrying this task.",
            _ => "This task stopped after a non-retryable execution failure. Review the agent's work diagnostics and resolve the cause before retrying."
        };
    }
}
