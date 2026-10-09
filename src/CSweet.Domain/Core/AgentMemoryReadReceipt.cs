namespace CSweet.Domain.Core;

/// <summary>Content-free audit evidence of reads supplied to one work attempt on a runtime.
/// Retention does not make this receipt a prompt input for later attempts or tasks.</summary>
public sealed class AgentMemoryReadReceipt
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid InstallationId { get; set; }
    public Guid RuntimeId { get; set; }
    public Guid WorkId { get; set; }
    public int Attempt { get; set; }
    public long GrantRevision { get; set; }
    public string Capability { get; set; } = string.Empty;
    public string EvidenceJson { get; set; } = string.Empty;
    public string AuthorityHash { get; set; } = string.Empty;
    public string ReceiptHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
