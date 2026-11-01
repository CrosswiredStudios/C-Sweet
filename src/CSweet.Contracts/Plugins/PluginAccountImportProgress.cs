namespace CSweet.Contracts.Plugins;

/// <summary>Content-free host diagnostics. Correlation IDs are generated locally, never copied from provider responses.</summary>
public sealed record PluginAccountImportProgress(Guid CorrelationId, string State, string Stage, int Attempt,
    DateTimeOffset? QueuedAt = null, DateTimeOffset? StartedAt = null, DateTimeOffset? FinishedAt = null,
    string? FailureKind = null, string? ExceptionType = null, int? HttpStatus = null, string? HttpStatusSource = null)
{
    // Validate even stored/received metadata before displaying it. Never render arbitrary error text or type names.
    public bool IsSafe() => CorrelationId != Guid.Empty && Attempt is >= 0 and <= 1_000_000 &&
        State is "Queued" or "Running" or "Completed" or "Failed" or "Interrupted" &&
        Stage is "Queued" or "Authority" or "ManagedDiscovery" or "IdentityRead" or "ResourceRead" or "ResponseValidation" or "Persistence" or "Complete" &&
        FailureKind is null or "http_error" or "invalid_response" or "authority_changed" or "interrupted" or "unexpected" &&
        ExceptionType is null or "HttpRequestException" or "InvalidOperationException" or "JsonException" or "UnauthorizedAccessException" or "OperationCanceledException" or "Other" &&
        (HttpStatus is null or >= 100 and <= 599) && HttpStatusSource is null or "ManagedService" or "Provider" or "Upstream";
}
