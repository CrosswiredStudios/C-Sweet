namespace CSweet.Domain.Core;

public sealed class Approval
{
    public Guid Id { get; set; }
    public Guid ArtifactId { get; set; }
    public Guid? ArtifactRevisionId { get; set; }
    public ApprovalStatus Status { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? DecidedByOrganizationUserId { get; set; }
    public Guid? DecidedByAgentInstallationId { get; set; }
    public Guid? EvidenceConversationMessageId { get; set; }

    // Navigation
    public Artifact? Artifact { get; set; }
    public ArtifactRevision? ArtifactRevision { get; set; }
}
