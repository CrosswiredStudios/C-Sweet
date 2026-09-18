using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CSweet.TrustedServices;

public sealed record WorkspaceOperationFailure(string Code, string Message, string NextStep, string DiagnosticId);

public sealed class WorkspaceOperationException(WorkspaceOperationFailure failure, int statusCode)
    : InvalidOperationException($"{failure.Code}: {failure.Message} Next step: {failure.NextStep} Diagnostic: {failure.DiagnosticId} (HTTP {statusCode}).")
{
    public WorkspaceOperationFailure Failure { get; } = failure;
    public int StatusCode { get; } = statusCode;
}

/// <summary>Carry actionable, credential-free diagnostics across GitHost, Core and AgentHost.</summary>
public static class WorkspaceOperationErrors
{
    public static bool IsExpected(Exception error) => error is IOException or InvalidOperationException
        or HttpRequestException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException;

    public static (WorkspaceOperationFailure Failure, int Status) Describe(Exception error)
    {
        if (error is WorkspaceOperationException forwarded) return (forwarded.Failure, forwarded.StatusCode);
        var (code, next) = error.Message switch
        {
            "The idempotency key was already used with different content." =>
                ("workspace.publication_content_changed", "The developer must reconcile the saved publication with the retained files, then publish changed content with a new operation key. Retrying the old key will fail."),
            "This publication was superseded by a later work-branch revision." =>
                ("workspace.publication_superseded", "Read the latest task publication and resume from its commit; do not replay the superseded publication."),
            "The work branch changed. Prepare or refresh its exact current revision before publishing." =>
                ("workspace.branch_changed", "Preserve the retained edits, refresh the task branch to its current commit, then validate and publish again."),
            "Workspace assignment is stale." or "Workspace operation does not match its persisted assignment." =>
                ("workspace.assignment_stale", "Reload the current task assignment and prepare its workspace before retrying; preserve the retained source files."),
            _ => error switch
            {
                UnauthorizedAccessException => ("workspace.access_denied", "Check the current task assignment and the agent's repository-team access before retrying."),
                InvalidDataException => ("workspace.snapshot_invalid", "Correct the reported snapshot validation error, upload the retained files again, and retry."),
                ArgumentException => ("workspace.request_invalid", "Correct the reported request field before retrying the operation."),
                KeyNotFoundException => ("workspace.resource_missing", "Restore or select the repository or workspace named by the failed operation."),
                HttpRequestException => ("workspace.service_unavailable", "Restore the source-control service connection, then retry using the retained operation key."),
                IOException => ("workspace.storage_failed", "Check source-control storage availability and the diagnostic record before retrying."),
                _ => ("workspace.operation_failed", "Use the diagnostic identifier to inspect the server error and correct the reported condition before retrying.")
            }
        };
        var status = error switch
        {
            UnauthorizedAccessException => 403, ArgumentException => 400, KeyNotFoundException => 404,
            InvalidDataException => 422, HttpRequestException => 503, _ => 409
        };
        return (new(code, SafeExcerpt(error.Message), next, Guid.NewGuid().ToString("N")), status);
    }

    public static IResult Result(Exception error, ILogger logger)
    {
        var (failure, status) = Describe(error);
        logger.LogWarning(error, "Workspace operation failed: {FailureCode}; diagnostic {DiagnosticId}", failure.Code, failure.DiagnosticId);
        return Results.Json(failure, statusCode: status);
    }

    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string service, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        WorkspaceOperationFailure? failure = null;
        try
        {
            // Never forward arbitrary HTML, proxy errors, or unbounded response bodies to an agent.
            await response.Content.LoadIntoBufferAsync(16 * 1024, ct);
            failure = await response.Content.ReadFromJsonAsync<WorkspaceOperationFailure>(ct);
        }
        catch (Exception error) when (error is JsonException or HttpRequestException or NotSupportedException) { }
        if (failure is null || !Regex.IsMatch(failure.Code ?? "", @"^workspace\.[a-z_]{1,64}$") ||
            !Guid.TryParseExact(failure.DiagnosticId, "N", out _) || string.IsNullOrWhiteSpace(failure.Message))
            failure = new("workspace.http_failure", $"{service} rejected {operation} with HTTP {(int)response.StatusCode} ({response.StatusCode}); no structured diagnostic was returned.",
                "Inspect this operation in the service logs. Upgrade services together so the original failure can be reported.", Guid.NewGuid().ToString("N"));
        else failure = failure with { Message = SafeExcerpt(failure.Message), NextStep = SafeExcerpt(failure.NextStep ?? "", 350) };
        throw new WorkspaceOperationException(failure, (int)response.StatusCode);
    }

    internal static string SafeExcerpt(string text, int maximum = 500)
    {
        text = Regex.Replace(text, @"(?im)^\s*at\s+.*$", "");
        text = Regex.Replace(text, @"(?i)\bBearer\s+\S+", "Bearer [redacted]");
        text = Regex.Replace(text, "(?i)([\"']?(?:password|passwd|token|secret|api[_-]?key|authorization)[\"']?\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s,;]+)", "$1[redacted]");
        text = Regex.Replace(text, @"(?i)https?://\S+", "[endpoint]");
        text = Regex.Replace(text, @"(?<!\w)(?:[A-Za-z]:[\\/]|/(?:var|tmp|home|workspace|usr|etc)/)[^\r\n'""<>]+", "[path]");
        text = Regex.Replace(text, @"[\x00-\x1f]+", " ").Trim();
        return text.Length <= maximum ? text : text[..maximum] + "…";
    }
}
