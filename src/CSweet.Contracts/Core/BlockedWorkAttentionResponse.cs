namespace CSweet.Contracts.Core;

/// <summary>An authorized recovery action on existing work, not a new approval or execution grant.</summary>
public sealed record BlockedWorkAttentionResponse(
    Guid ItemId, Guid OwnerOrganizationUserId, string OwnerName, string? ManagerName,
    string Title, string Reason, long Revision, bool CanRetry, string ActionUri, Guid? DecisionId = null);
