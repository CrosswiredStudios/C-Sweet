namespace CSweet.Contracts.Core;

public static class ApprovalDashboardKinds
{
    public const string ResourceChange = "ResourceChange";
    public const string AgentAction = "AgentAction";
    public const string HiringWorkflow = "HiringWorkflow";
    public const string Artifact = "Artifact";
}

public sealed record ApprovalDashboardItemResponse(
    Guid Id,
    string Kind,
    string Title,
    string Summary,
    string Status,
    string RequestedBy,
    string AssignedTo,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    string ActionUri,
    bool CanDecide,
    ResourceChangeRequestResponse? ResourceChange = null);

public sealed record ApprovalDashboardResponse(
    Guid CurrentOrganizationUserId,
    int PendingCount,
    IReadOnlyList<ApprovalDashboardItemResponse> Items);
