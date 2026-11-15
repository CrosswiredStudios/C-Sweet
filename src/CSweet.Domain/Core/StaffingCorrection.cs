namespace CSweet.Domain.Core;

/// <summary>Owner/manager direction to replace an unfilled staffing slot; the original approval stays immutable.</summary>
public sealed class StaffingCorrection
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid SourceRequestId { get; set; }
    public Guid RecommendationId { get; set; }
    public string RoleKey { get; set; } = "";
    public string Reason { get; set; } = "";
    public Guid RequestedByOrganizationUserId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public Guid? ReplacementRequestId { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
