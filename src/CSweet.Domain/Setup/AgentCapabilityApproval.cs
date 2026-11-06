namespace CSweet.Domain.Setup;

public sealed class AgentCapabilityApproval
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid AgentInstallationId { get; set; }
    public string OperationKey { get; set; } = "";
    public string Capability { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string Risk { get; set; } = "Write";
    public long GrantRevision { get; set; }
    public long PostureRevision { get; set; }
    public string Status { get; set; } = "Pending";
    public string? ResultsJson { get; set; }
    public Guid? DecidedByApplicationUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;
}
