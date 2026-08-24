using System.ComponentModel.DataAnnotations;

namespace CSweet.Contracts.Core;

public static class StaffingReplenishmentCapabilities
{
    public const string Propose = "platform.management.staffing-replenishment.propose.v1";
    public const string Read = "platform.management.staffing-replenishment.read.v1";
    public const string Decide = "platform.management.staffing-replenishment.decide.v1";
}

public static class StaffingReplenishmentEvents
{
    public const string Requested = "com.csweet.management.staffing-replenishment.requested.v1";
    public const string Decided = "com.csweet.management.staffing-replenishment.decided.v1";
}

public sealed record StaffingReplenishmentGap(
    [property: Required, MaxLength(160)] string RoleKey,
    [property: Required, MaxLength(256)] string RoleTitle,
    int DesiredHeadcount,
    int EffectiveHeadcount,
    int MissingHeadcount,
    IReadOnlyList<string> EligibilityEvidence);

public sealed record StaffingReplenishmentProposalRequest(
    Guid SourceResourceChangeRequestId,
    Guid TeamId,
    Guid ConversationId,
    IReadOnlyList<StaffingReplenishmentGap> Gaps,
    [property: Required, MaxLength(4096)] string OperationalImpact,
    IReadOnlyList<string> InterimControls,
    [property: Required, MaxLength(128)] string DecisionFingerprint,
    [property: Required, MaxLength(160)] string IdempotencyKey);

public sealed record StaffingReplenishmentReadRequest(
    Guid? RequestId = null,
    Guid? SourceResourceChangeRequestId = null,
    IReadOnlyList<string>? Statuses = null);

public sealed record StaffingReplenishmentReadResponse(
    IReadOnlyList<StaffingReplenishmentResponse> Requests);

public sealed record StaffingReplenishmentDecisionRequest(
    Guid RequestId,
    [property: Required, MaxLength(32)] string Decision,
    [property: MaxLength(4000)] string? Comment,
    [property: Required, MaxLength(160)] string IdempotencyKey);

public sealed record StaffingReplenishmentResponse(
    Guid Id,
    Guid OrganizationId,
    Guid RequesterOrganizationUserId,
    Guid RequesterInstallationId,
    Guid ManagerOrganizationUserId,
    Guid SourceResourceChangeRequestId,
    Guid TeamId,
    Guid ConversationId,
    IReadOnlyList<StaffingReplenishmentGap> Gaps,
    string OperationalImpact,
    IReadOnlyList<string> InterimControls,
    string DecisionFingerprint,
    string Status,
    string? DecisionComment,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt);

public sealed record StaffingReplenishmentDecisionEvent(
    Guid RequestId,
    Guid OrganizationId,
    Guid RequesterOrganizationUserId,
    Guid ManagerOrganizationUserId,
    string Status,
    DateTimeOffset OccurredAt);
