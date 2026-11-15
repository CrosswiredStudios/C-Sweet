using System.ComponentModel.DataAnnotations;

namespace CSweet.Contracts.Core;

public sealed record RequestStaffingCorrection(long ExpectedSourceRevision,
    [property: Required] string Reason,
    [property: Required, MaxLength(160)] string IdempotencyKey)
{
    public Guid? ExpectedSourceRequestId { get; init; }
}

public sealed record StaffingCorrectionResponse(Guid Id, Guid SourceRequestId, Guid RecommendationId,
    Guid RequesterOrganizationUserId, Guid? TeamId, Guid? WorkstreamId, string RoleKey, string Reason,
    string Status, Guid? ReplacementRequestId, long Revision, DateTimeOffset CreatedAt)
{
    public Guid RequestedByOrganizationUserId { get; init; }
}
