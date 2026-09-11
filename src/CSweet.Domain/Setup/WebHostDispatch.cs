namespace CSweet.Domain.Setup;

public sealed class WebHostCommandRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WebHostId { get; set; }
    public Guid PreviewId { get; set; }
    public Guid? BrowserSessionId { get; set; }
    public string Action { get; set; } = "";
    public string BodyJson { get; set; } = "{}";
    public string Status { get; set; } = "Pending";
    public string? ResponseJson { get; set; }
    public string? ResponseDigest { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Revision { get; set; } = 1;
}

// All admissions to a project touch this row, including requests using different grants or hosts.
public sealed class WebPreviewProjectAdmission
{
    public Guid WorkstreamId { get; set; }
    public Guid OrganizationId { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class WebPreviewEvidenceRecord
{
    public Guid Id { get; set; }
    public Guid PreviewId { get; set; }
    public Guid OrganizationId { get; set; }
    public long HostSequence { get; set; }
    public string DiagnosticJson { get; set; } = "{}";
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset RetainUntil { get; set; }
}
