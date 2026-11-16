using System.ComponentModel.DataAnnotations;

namespace CSweet.Contracts.Core;

public static class ResourceChangeCapabilities
{
    public const string Propose = "platform.management.resource-change.propose.v1";
    public const string Read = "platform.management.resource-change.read.v1";
    public const string Decide = "platform.management.resource-change.decide.v1";
}

public static class ResourceChangeEvents
{
    public const string Requested = "com.csweet.management.resource-change.requested.v1";
    public const string Decided = "com.csweet.management.resource-change.decided.v1";
}

public static class ResourceChangeDecisionKinds
{
    public const string Approve = "Approve";
    public const string RequestRevision = "RequestRevision";
    public const string Reject = "Reject";
}

public sealed record ResourceChangeRole(
    [property: Required, MaxLength(160)] string RoleKey,
    [property: Required] string Team,
    [property: Required] string Title,
    [property: Required] string Purpose,
    int Headcount,
    int Priority,
    [property: Required] string Timing,
    IReadOnlyList<string> RequiredCapabilities,
    bool HumanRequired,
    Guid? ReportsToOrganizationUserId,
    [property: MaxLength(160)] string? ReportsToRoleKey)
{
    public Guid? TeamId { get; init; }
    public string RoleCategoryKey { get; init; } = string.Empty;
    public IReadOnlyList<string> PreferredSpecializationKeys { get; init; } = [];
}

public sealed record ResourceChangeEvidence(
    [property: Required, MaxLength(80)] string Kind,
    [property: Required, MaxLength(128)] string SourceRevision,
    [property: Required] string Summary,
    decimal? Value = null,
    [property: MaxLength(40)] string? Unit = null,
    DateTimeOffset? WindowStart = null,
    DateTimeOffset? WindowEnd = null);

public sealed record ResourceChangeProposalRequest(
    Guid ConversationId,
    Guid ChatTurnId,
    [property: Required] string ProductGoal,
    [property: Required] string Rationale,
    long ContextRevision,
    IReadOnlyList<ResourceChangeRole> Roles,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Constraints,
    Guid? SupersedesRequestId,
    [property: Required, MaxLength(160)] string IdempotencyKey)
{
    public Guid? RevisesRequestId { get; init; }
    public Guid? CorrectionId { get; init; }
    [MaxLength(200)]
    public string? TeamKey { get; init; }
    [MaxLength(160)]
    public string? TeamName { get; init; }
    public string? TeamDescription { get; init; }
    public Guid? TeamId { get; init; }
    public Guid? WorkstreamId { get; init; }
    public long? ExpectedTeamRevision { get; init; }
    public IReadOnlyList<ResourceChangeEvidence> Evidence { get; init; } = [];
    public IReadOnlyList<string> AlternativesConsidered { get; init; } = [];
    public string? ExpectedEffect { get; init; }
}

public sealed record ResourceChangeRoleDelta(
    string ChangeKind,
    ResourceChangeRole Role,
    ResourceChangeRole? PreviousRole);

public sealed record ResourceChangeRequestResponse(
    Guid Id,
    Guid OrganizationId,
    Guid RequesterOrganizationUserId,
    Guid RequesterInstallationId,
    Guid ManagerOrganizationUserId,
    Guid ConversationId,
    Guid ChatTurnId,
    string ProductGoal,
    string Rationale,
    long ContextRevision,
    IReadOnlyList<ResourceChangeRole> Roles,
    IReadOnlyList<ResourceChangeRoleDelta> Deltas,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Constraints,
    Guid? SupersedesRequestId,
    string Status,
    string DeliveryStatus,
    string? DecisionComment,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt)
{
    public Guid? RevisesRequestId { get; init; }
    public Guid? CorrectionId { get; init; }
    public Guid? TeamId { get; init; }
    public string? TeamKey { get; init; }
    public string? TeamName { get; init; }
    public string? TeamDescription { get; init; }
    public Guid? WorkstreamId { get; init; }
    public long? ExpectedTeamRevision { get; init; }
    public IReadOnlyList<ResourceChangeEvidence> Evidence { get; init; } = [];
    public IReadOnlyList<string> AlternativesConsidered { get; init; } = [];
    public string? ExpectedEffect { get; init; }
    public Guid? DecidedByOrganizationUserId { get; init; }
}

public sealed record ResourceChangeReadRequest(
    Guid? RequestId = null,
    IReadOnlyList<string>? Statuses = null);

public sealed record ResourceChangeReadResponse(IReadOnlyList<ResourceChangeRequestResponse> Requests)
{
    public IReadOnlyList<StaffingCorrectionResponse> Corrections { get; init; } = [];
}

public sealed record ResourceChangeDecisionRequest(
    Guid RequestId,
    [property: Required, MaxLength(32)] string Decision,
    string? Comment,
    [property: Required, MaxLength(160)] string IdempotencyKey);

public sealed record ResourceChangeDecisionEvent(
    Guid RequestId,
    Guid OrganizationId,
    Guid RequesterOrganizationUserId,
    Guid ManagerOrganizationUserId,
    string Status,
    DateTimeOffset OccurredAt)
{
    public Guid? TeamId { get; init; }
    public Guid? WorkstreamId { get; init; }
    public Guid? DecidedByOrganizationUserId { get; init; }
}
