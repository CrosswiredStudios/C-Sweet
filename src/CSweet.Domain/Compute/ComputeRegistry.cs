namespace CSweet.Domain.Compute;

public sealed class ComputeNodeRegistration
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string KeyId { get; set; } = "";
    public string VerificationPublicKeyBase64 { get; set; } = "";
    public bool Enabled { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ComputeTemplateRegistration
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string TemplateId { get; set; } = "";
    public string TemplateJson { get; set; } = "{}";
    public bool Enabled { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ComputeTemplatePlacement
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TemplateRegistrationId { get; set; }
    public Guid NodeId { get; set; }
    public bool Enabled { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
}
