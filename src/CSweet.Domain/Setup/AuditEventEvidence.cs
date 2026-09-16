namespace CSweet.Domain.Setup;

/// <summary>Indexed participants in a single canonical audit record.</summary>
public sealed class AuditEventEmployee
{
    public Guid AuditEventId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid EmployeeId { get; set; }
    public string Role { get; set; } = "Affected";
}

/// <summary>Full sanitized evidence, protected at rest and sealed by its ledger record.</summary>
public sealed class AuditEventPayload
{
    public Guid AuditEventId { get; set; }
    public byte[] ProtectedContent { get; set; } = [];
}
