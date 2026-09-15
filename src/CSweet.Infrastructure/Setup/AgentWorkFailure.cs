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
    public static string DescribeBlocker(string? error, string? runtimeLog = null)
    {
        var capability = FailureField(error, "capability");
        var code = FailureField(error, "code");
        var exceptionType = SafeExceptionType(FailureField(error, "exceptionType"));
        var httpStatus = SafeHttpStatus(FailureField(error, "httpStatus"));
        var diagnosticId = SafeDiagnosticId(FailureField(error, "diagnosticId"));
        if (code == "runtime.transport")
        {
            var matchedStreamFailure = diagnosticId is not null && runtimeLog?.Contains(
                $"Work failure summary {diagnosticId}: HttpRequestException: Error while copying content to a stream.",
                StringComparison.Ordinal) == true;
            return RecoverableFailureMessage(matchedStreamFailure
                ? "HTTP content transfer failed: Error while copying content to a stream. The underlying connection cause was not captured."
                : "An HTTP request failed. The exact connection cause was not captured in the failure report.",
                exceptionType, httpStatus, diagnosticId, IsRecoverableAtAttentionReview(error));
        }
        if (code is "agent.invalid_operation")
            return RecoverableFailureMessage("The agent stopped before reporting a structured result.", exceptionType, httpStatus, diagnosticId, IsRecoverableAtAttentionReview(error));
        if (code is "agent.payload_invalid")
            return RecoverableFailureMessage("The agent returned an invalid structured result.", exceptionType, httpStatus, diagnosticId, IsRecoverableAtAttentionReview(error));
        if (code is "agent.unhandled")
            return RecoverableFailureMessage("The agent run ended unexpectedly.", exceptionType, httpStatus, diagnosticId, IsRecoverableAtAttentionReview(error));
        return capability switch
        {
            "platform.llm.chat-stream.v1" => "Inference could not run. Check the configured provider's model readiness and inference diagnostics, then retry this task.",
            "platform.decision.decide.v1" => "The project decision could not be approved. Check its authority and current revision before retrying this task.",
            _ => "This task stopped after an execution failure exhausted automatic recovery or could not be retried. Review the agent's work diagnostics and resolve the cause before retrying."
        };
    }

    private static string? FailureField(string? error, string name) => error?.Split(';')
        .FirstOrDefault(x => x.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];

    private static string? SafeExceptionType(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.All(char.IsLetterOrDigit) ? value : null;

    private static string? SafeHttpStatus(string? value) => int.TryParse(value, out var status) &&
        Enum.IsDefined(typeof(System.Net.HttpStatusCode), status) ? status.ToString() : null;

    private static string? SafeDiagnosticId(string? value) => Guid.TryParse(value, out var identifier)
        ? identifier.ToString("D") : null;

    private static string RecoverableFailureMessage(string headline, string? exceptionType,
        string? httpStatus, string? diagnosticId, bool retryable)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(exceptionType)) details.Add($"**Failure type:** `{exceptionType}`");
        if (!string.IsNullOrWhiteSpace(httpStatus)) details.Add($"**HTTP status:** `{httpStatus}`");
        if (!string.IsNullOrWhiteSpace(diagnosticId)) details.Add($"**Diagnostic ID:** `{diagnosticId}`");
        var evidence = details.Count == 0 ? string.Empty : "\n\n" + string.Join("  \n", details);
        var recovery = retryable
            ? "C-Sweet will retry this task automatically within its automatic recovery limit; repeated failures use a longer backoff."
            : "This failure is not eligible for automatic retry. Resolve the reported issue before requeuing the task.";
        return $"{headline}{evidence}\n\n{recovery}";
    }
}
