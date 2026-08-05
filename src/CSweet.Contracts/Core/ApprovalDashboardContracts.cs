namespace CSweet.Contracts.Core;

public static class ApprovalDashboardKinds
{
    public const string ResourceChange = "ResourceChange";
    public const string AgentAction = "AgentAction";
    public const string HiringWorkflow = "HiringWorkflow";
    public const string Artifact = "Artifact";
    public const string RepositoryProvisioning = "RepositoryProvisioning";
    public const string Merge = "Merge";
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
    ResourceChangeRequestResponse? ResourceChange = null,
    HiringWorkflowApprovalResponse? HiringWorkflow = null,
    SourceControlApprovalCardResponse? SourceControl = null)
{
    public ManagedAgentActionApprovalResponse? AgentAction { get; init; }
}

public sealed record ManagedAgentActionApprovalResponse(
    Guid ProposalId,
    string ActionType,
    string ChannelId,
    string PayloadHash,
    long? ExpectedRevision,
    string IdempotencyKey,
    bool AlwaysRequiresApproval,
    string? ResourceId = null);

public sealed record DecideManagedAgentActionRequest(
    Guid ProposalId,
    string Decision,
    string? Comment,
    string PayloadHash,
    long? ExpectedRevision,
    string ActionIdempotencyKey,
    string DecisionIdempotencyKey,
    string? ResourceId = null);

public sealed record SourceControlApprovalCardResponse(
    Guid ApprovalId,
    string ApprovalKind,
    Guid? ProvisioningRequestId,
    Guid? MergeJobId,
    string CodeProjectName,
    string AccountLogin,
    bool PrivateOnly,
    string? TemplateName,
    string? DefaultTeamName,
    int? MaximumProjects,
    string Status,
    long Revision);

public sealed record ApprovalDashboardResponse(
    Guid CurrentOrganizationUserId,
    int PendingCount,
    IReadOnlyList<ApprovalDashboardItemResponse> Items);
