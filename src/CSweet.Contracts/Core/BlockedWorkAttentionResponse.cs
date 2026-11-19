namespace CSweet.Contracts.Core;

/// <summary>An authorized recovery action on existing work, not a new approval or execution grant.</summary>
public sealed record BlockedWorkAttentionResponse(
    Guid ItemId, Guid OwnerOrganizationUserId, string OwnerName, string? ManagerName,
    string Title, string Reason, long Revision, bool CanRetry, string ActionUri, Guid? DecisionId = null)
{
    public IReadOnlyList<OverviewBlockerSource> Blockers { get; init; } = [];
    public string? RetryKey { get; init; }
    public string? RetryReason { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool OffersRetry => CanRetry && !string.IsNullOrEmpty(RetryKey);
}

public sealed record OverviewBlockerSource(string Key, Guid ItemId, string Title, string? ActionUri = null);
