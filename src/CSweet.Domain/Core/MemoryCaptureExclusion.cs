namespace CSweet.Domain.Core;

/// <summary>
/// Content-free, permanent capture exclusion after source validation fails. Deliberately has
/// no foreign keys: deleting a source, job, or organization must not permit recapture.
/// This is not a deletion receipt or a legal-hold decision for existing memory.
/// </summary>
public sealed class MemoryCaptureExclusion
{
    public Guid SourceMessageId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid TriggerJobId { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public DateTimeOffset ExcludedAt { get; set; }
}
