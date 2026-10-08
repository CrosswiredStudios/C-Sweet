namespace CSweet.Contracts.Core;

public static class ApprovalDashboardKinds
{
    public const string ResourceChange = "ResourceChange";
    public const string AgentAction = "AgentAction";
    public const string HiringWorkflow = "HiringWorkflow";
    public const string Artifact = "Artifact";
    public const string ArtifactAccess = "ArtifactAccess";
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
    public ArtifactApprovalCardResponse? Artifact { get; init; }
    public ArtifactAccessRequestResponse? ArtifactAccess { get; init; }
    public string? RequestingTeam { get; init; }
    public string? ActualDecisionMaker { get; init; }
    public Guid? SourceResourceChangeRequestId { get; init; }
    public bool CanManageStandingPolicy { get; init; }
    public ProjectCreationApprovalResponse? ProjectCreation { get; init; }
    public string? ReviewError { get; init; }
    public string? DecisionComment { get; init; }
    public string? DecisionKind { get; init; }
    public Guid? CreatedProjectId { get; init; }
}

public sealed record ProjectApprovalMilestone(string Name, string Stage, DateTimeOffset? TargetDate,
    IReadOnlyList<string> RequiredEvidence, IReadOnlyList<string> Reviewers);
public sealed record ProjectApprovalDocument(string Name, string Uri, string Revision);
public sealed record ProjectCreationApprovalResponse(string Name, string Outcome, string Rationale,
    string Lead, string? Team, string Stage, decimal? Budget, string? Currency, DateTimeOffset? TargetDate,
    IReadOnlyList<string> SuccessCriteria, IReadOnlyList<string> Supervisors,
    IReadOnlyList<string> StaffingRoles, IReadOnlyList<string> AgentPermissions,
    IReadOnlyList<string> HumanDecisions, decimal? BudgetVariance, int? ScheduleVarianceDays,
    DateTimeOffset? AuthorityExpiresAt, IReadOnlyList<ProjectApprovalMilestone> Milestones,
    IReadOnlyList<ProjectApprovalDocument> Documents, bool CreatesBoard);

public sealed record ArtifactApprovalCardResponse(
    Guid ArtifactId,
    Guid? SubmittedRevisionId);

public sealed record ManagedAgentActionApprovalResponse(
    Guid ProposalId,
    string ActionType,
    string ChannelId,
    string PayloadHash,
    long? ExpectedRevision,
    string IdempotencyKey,
    bool AlwaysRequiresApproval,
    string? ResourceId = null,
    string? FiscalSummary = null,
    string? ApprovalRoute = null,
    DateTimeOffset? ExpiresAt = null,
    string? AccountName = null,
    string? ReviewPayloadJson = null);

public sealed record DecideManagedAgentActionRequest(
    Guid ProposalId,
    string Decision,
    string? Comment,
    string PayloadHash,
    long? ExpectedRevision,
    string ActionIdempotencyKey,
    string DecisionIdempotencyKey,
    string? ResourceId = null);

public sealed record ConnectorActionApprovalCardResponse(ManagedAgentActionApprovalResponse Action,
    string Summary, string Status, string ExecutionStatus, bool CanDecide, string? DecisionComment = null)
{
    public bool CanManageStandingPolicy { get; init; }
}

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
