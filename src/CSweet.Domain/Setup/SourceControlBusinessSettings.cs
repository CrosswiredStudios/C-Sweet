namespace CSweet.Domain.Setup;

public sealed class SourceControlBusinessSettings
{
    public Guid OrganizationId { get; set; }
    // Null means the built-in empty internal repository template.
    public Guid? DefaultTemplateId { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
}
