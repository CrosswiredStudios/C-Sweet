namespace CSweet.Domain.Setup;

public sealed class WebPreviewFindingRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid PreviewId { get; set; }
    public Guid BuildId { get; set; }
    public string Fingerprint { get; set; } = "";
    public string EvidenceJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset RetainUntil { get; set; }
    public Guid? TriageWorkId { get; set; }
    public Guid? TriageInstallationId { get; set; }
    public Guid? BoardId { get; set; }
    public Guid? TicketId { get; set; }
    public long Revision { get; set; } = 1;
}
public sealed class WebPreviewTriageRoute
{
    public Guid ProjectId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid InstallationId { get; set; }
    public Guid BoardId { get; set; }
    public Guid ParentItemId { get; set; }
    public long Revision { get; set; } = 1;
}
