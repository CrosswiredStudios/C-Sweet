using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CSweet.Contracts.Plugins;

namespace CSweet.Contracts.Core;

public static class HiringCapabilities
{
    public const string ListRecommendations = "platform.hiring-recommendation.list.v1";
    public const string UpsertRecommendation = "platform.hiring-recommendation.upsert.v1";
    public const string ResolveRecommendation = "platform.hiring-recommendation.resolve.v1";
    public const string WithdrawRecommendation = "platform.hiring-recommendation.withdraw.v1";
    public const string StageWorkflow = "platform.hiring-workflow.stage.v1";
}

public static class HiringEvents
{
    public const string EmployeeHired = "com.csweet.employee.hired.v1";
    public const string RecommendationFulfilled = "com.csweet.hiring-recommendation.fulfilled.v1";
}

public sealed record HiringCandidateResponse(
    string CandidateReference,
    string Source,
    string DisplayName,
    string ResourceType,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Credentials,
    decimal FitScore,
    decimal? Price,
    string? Currency,
    string Trust,
    bool Available,
    string InstallationState,
    IReadOnlyList<string> RequiredGrants,
    string Rationale);

public sealed record HiringRecommendationResponse(
    Guid Id,
    Guid? WorkstreamId,
    string Title,
    string Objective,
    string Status,
    string? RecommendedCandidateReference,
    IReadOnlyList<HiringCandidateResponse> Candidates,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public int Priority { get; init; } = 50;
    public string HiringUrl { get; init; } = string.Empty;
    public string? SuggestedBy { get; init; }
    public string? RoleKey { get; init; }
    public int Headcount { get; init; } = 1;
    public Guid? SourceResourceChangeRequestId { get; init; }
    public Guid? TeamId { get; init; }
    public int FulfilledHeadcount { get; init; }
    public int RemainingHeadcount { get; init; }
}

public sealed record HiringBacklogResponse(IReadOnlyList<HiringRecommendationResponse> Recommendations);

public sealed record UpsertHiringRecommendationRequest(
    [property: Required, MaxLength(256)] string Title,
    [property: Required, MaxLength(2048)] string Objective,
    Guid? WorkstreamId,
    [property: MaxLength(3)] IReadOnlyList<string> CandidateReferences,
    string? RecommendedCandidateReference,
    [property: Required, MaxLength(160)] string IdempotencyKey)
{
    [Range(1, 100)]
    public int Priority { get; init; } = 50;
    [MaxLength(160)]
    public string? RoleKey { get; init; }
    [Range(1, 100)]
    public int Headcount { get; init; } = 1;
    public Guid? SourceResourceChangeRequestId { get; init; }
    public Guid? TeamId { get; init; }
}

public sealed record ResolveHiringRecommendationRequest(
    Guid RecommendationId,
    Guid ResultOrganizationUserId,
    [property: Required, MaxLength(160)] string IdempotencyKey);

public sealed record WithdrawHiringRecommendationRequest(
    Guid RecommendationId,
    [property: Required, MaxLength(2048)] string Reason,
    [property: Required, MaxLength(160)] string IdempotencyKey);

public sealed record EmployeeHiredEvent(
    Guid OrganizationId,
    Guid OrganizationUserId,
    string EmployeeType,
    Guid? RoleId,
    string? RoleTitle,
    Guid? AgentInstallationId,
    Guid? WorkerId,
    Guid? ReportsToOrganizationUserId,
    Guid? HiringOrganizationUserId,
    string Source,
    DateTimeOffset OccurredAt);

public sealed record HiringRecommendationFulfilledEvent(
    Guid OrganizationId,
    Guid RecommendationId,
    Guid? SourceResourceChangeRequestId,
    Guid RequestingInstallationId,
    string? RoleKey,
    string RoleTitle,
    Guid? TeamId,
    Guid? WorkstreamId,
    int RequestedHeadcount,
    int FulfilledHeadcount,
    IReadOnlyList<Guid> ResultOrganizationUserIds,
    DateTimeOffset OccurredAt);

public sealed record StageHiringWorkflowRequest(
    Guid RecommendationId,
    [property: Required] string CandidateReference,
    [property: Required, MaxLength(160)] string RoleTitle,
    Guid? ReportsToOrganizationUserId,
    IReadOnlyList<string>? RequiredGrants,
    [property: Required, MaxLength(160)] string IdempotencyKey)
{
    public Guid? ConversationId { get; init; }
    public Guid? ChatTurnId { get; init; }
}

public sealed record HiringWorkflowResponse(
    Guid Id,
    Guid RecommendationId,
    string CandidateReference,
    string RoleTitle,
    string Status,
    string Message,
    DateTimeOffset CreatedAt,
    Guid? ResultOrganizationUserId = null)
{
    public Guid? ResultAgentInstallationId { get; init; }
    public bool ResultAgentRequiresSetup { get; init; }
}

public sealed record ConfirmHiringWorkflowRequest(
    [property: Required, MaxLength(160)] string IdempotencyKey)
{
    public IReadOnlyDictionary<string, JsonElement> ConfigurationSettings { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}

public static class HiringWorkflowDecisionKinds
{
    public const string Approve = "Approve";
    public const string Reject = "Reject";
}

public sealed record DecideHiringWorkflowRequest(
    [property: Required, MaxLength(16)] string Decision,
    [property: MaxLength(2048)] string? Comment,
    [property: Required, MaxLength(160)] string IdempotencyKey)
{
    public IReadOnlyDictionary<string, JsonElement> ConfigurationSettings { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}

public sealed record HiringWorkflowApprovalResponse(
    Guid Id,
    string RoleTitle,
    string CandidateReference,
    string CandidateName,
    string CandidateSource,
    string EmployeeDisplayName,
    Guid? ReportsToOrganizationUserId,
    string? ReportsToDisplayName,
    string Status,
    string InstallationConsequence,
    IReadOnlyList<string> RequestedCapabilities,
    IReadOnlyList<string> Subscriptions,
    IReadOnlyList<string> NetworkAccess,
    IReadOnlyList<PluginConfigurationField> ConfigurationFields,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? DecidedAt,
    string? DecisionComment);

public sealed record HiringDashboardResponse(
    IReadOnlyList<HiringRecommendationResponse> Recommendations,
    IReadOnlyList<HiringWorkflowResponse> Workflows)
{
    public IReadOnlyList<ResourceChangeRequestResponse> ResourceChanges { get; init; } = [];
    public Guid? CurrentOrganizationUserId { get; init; }
}

public sealed record PreviewMarketplaceHireRequest(
    [property: Required, MaxLength(512)] string AgentReference,
    [property: Required, MaxLength(160)] string RoleTitle,
    [property: Required, MaxLength(160)] string EmployeeDisplayName,
    Guid? ReportsToOrganizationUserId,
    [property: Required, MaxLength(160)] string IdempotencyKey)
{
    public Guid? RecommendationId { get; init; }
    public Guid? TeamId { get; init; }
    public Guid? SupersedesWorkflowId { get; init; }
}

public sealed record MarketplaceHirePreviewResponse(
    Guid WorkflowId,
    string AgentReference,
    string AgentName,
    string EmployeeDisplayName,
    string RoleTitle,
    Guid? ReportsToOrganizationUserId,
    string Source,
    string Trust,
    decimal? Price,
    string? Currency,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> RequestedCapabilities,
    IReadOnlyList<string> Subscriptions,
    IReadOnlyList<string> NetworkAccess,
    string InstallationConsequence,
    string Status)
{
    public IReadOnlyList<PluginConfigurationField> ConfigurationFields { get; init; } = [];
    public Guid? TeamId { get; init; }
}
