namespace CSweet.Infrastructure.Setup;

/// <summary>Interprets the SDK's structured failure envelope without exposing exception text.</summary>
public static class AgentWorkFailure
{
    public static bool IsNonRetryable(string? error) =>
        error?.StartsWith("agent-failure:v1;", StringComparison.Ordinal) == true &&
        error.Split(';').Contains("retryable=false", StringComparer.Ordinal);

    public static bool IsRetryableTransport(string? error) =>
        error?.StartsWith("agent-failure:v1;", StringComparison.Ordinal) == true &&
        error.Split(';').Contains("code=runtime.transport", StringComparer.Ordinal) &&
        error.Split(';').Contains("retryable=true", StringComparer.Ordinal);

    public static bool IsRecoverableAtAttentionReview(string? error)
    {
        if (error?.StartsWith("agent-failure:v1;", StringComparison.Ordinal) != true ||
            IsNonRetryable(error)) return false;
        var code = error.Split(';')
            .FirstOrDefault(x => x.StartsWith("code=", StringComparison.Ordinal))?[5..];
        return code is "runtime.transport" or "agent.invalid_operation" or
            "agent.payload_invalid" or "agent.unhandled";
    }

    public static bool IsLlmProviderBlocker(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return false;
        return reason.Contains("platform.llm.chat-stream.v1", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("LLM provider", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("Inference could not run", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("output-token budget", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDevelopmentBlocker(string? reason) =>
        reason?.StartsWith("Development is blocked:", StringComparison.OrdinalIgnoreCase) == true;

    public static string DescribeCollaborationFailure(string? summary)
    {
        const string prefix = "Collaboration failed because an agent turn could not continue: ";
        var error = summary?.StartsWith(prefix, StringComparison.Ordinal) == true
            ? summary[prefix.Length..] : null;
        // Older sessions contain the same durable envelope. Do not display raw exception text.
        return error?.StartsWith("agent-failure:v1;", StringComparison.Ordinal) == true
            ? DescribeBlocker(error)
            : "This collaboration stopped after an execution failure. Review the agent's diagnostics and resolve the cause before retrying.";
    }
    public static string DescribeBlocker(string? error)
    {
        var capability = error?.Split(';').FirstOrDefault(x => x.StartsWith("capability=", StringComparison.Ordinal))?[11..];
        var code = error?.Split(';').FirstOrDefault(x => x.StartsWith("code=", StringComparison.Ordinal))?[5..];
        if (code is "agent.invalid_operation")
            return "The agent stopped before reporting a structured result. C-Sweet will retry this task automatically at the next attention review within its automatic recovery limit.";
        if (code is "agent.payload_invalid")
            return "The agent returned an invalid structured result. C-Sweet will retry this task automatically at the next attention review within its automatic recovery limit.";
        if (code is "agent.unhandled" or "runtime.transport")
            return "The agent run ended unexpectedly. C-Sweet will retry this task automatically within its automatic recovery limit; repeated failures use a longer backoff.";
        return capability switch
        {
            "platform.llm.chat-stream.v1" => "Inference could not run. Check the configured provider's model readiness and inference diagnostics, then retry this task.",
            "platform.decision.decide.v1" => "The project decision could not be approved. Check its authority and current revision before retrying this task.",
            _ => "This task stopped after an execution failure exhausted automatic recovery or could not be retried. Review the agent's work diagnostics and resolve the cause before retrying."
        };
    }
}
