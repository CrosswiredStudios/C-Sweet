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
    [property: Required, MaxLength(160)] string Team,
    [property: Required, MaxLength(256)] string Title,
    [property: Required, MaxLength(2048)] string Purpose,
    int Headcount,
    int Priority,
    [property: Required, MaxLength(32)] string Timing,
    IReadOnlyList<string> RequiredCapabilities,
    bool HumanRequired,
    Guid? ReportsToOrganizationUserId,
    [property: MaxLength(160)] string? ReportsToRoleKey)
{
    public Guid? TeamId { get; init; }
    public string RoleCategoryKey { get; init; } = string.Empty;
    public IReadOnlyList<string> PreferredSpecializationKeys { get; init; } = [];
}

public sealed record ResourceChangeProposalRequest(
    Guid ConversationId,
    Guid ChatTurnId,
    [property: Required, MaxLength(2048)] string ProductGoal,
    [property: Required, MaxLength(4096)] string Rationale,
    long ContextRevision,
    IReadOnlyList<ResourceChangeRole> Roles,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Constraints,
    Guid? SupersedesRequestId,
    [property: Required, MaxLength(160)] string IdempotencyKey)
{
    [MaxLength(200)]
    public string? TeamKey { get; init; }
    [MaxLength(160)]
    public string? TeamName { get; init; }
    [MaxLength(2048)]
    public string? TeamDescription { get; init; }
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
    public Guid? TeamId { get; init; }
    public string? TeamKey { get; init; }
    public string? TeamName { get; init; }
    public string? TeamDescription { get; init; }
}

public sealed record ResourceChangeReadRequest(
    Guid? RequestId = null,
    IReadOnlyList<string>? Statuses = null);

public sealed record ResourceChangeReadResponse(IReadOnlyList<ResourceChangeRequestResponse> Requests);

public sealed record ResourceChangeDecisionRequest(
    Guid RequestId,
    [property: Required, MaxLength(32)] string Decision,
    [property: MaxLength(4000)] string? Comment,
    [property: Required, MaxLength(160)] string IdempotencyKey);

public sealed record ResourceChangeDecisionEvent(
    Guid RequestId,
    Guid OrganizationId,
    Guid RequesterOrganizationUserId,
    Guid ManagerOrganizationUserId,
    string Status,
    DateTimeOffset OccurredAt);
