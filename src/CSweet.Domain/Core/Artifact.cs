using CSweet.Domain.Security;

namespace CSweet.Domain.Core;

public sealed class Artifact
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? TaskId { get; set; }
    public Guid? TaskRunId { get; set; }
    public Guid? WorkstreamId { get; set; }
    public Guid? TeamId { get; set; }
    public ArtifactType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int Version { get; set; }
    public ApprovalStatus ApprovalStatus { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? FolderId { get; set; }
    public Guid? PackageId { get; set; }
    public Guid? OriginConversationId { get; set; }
    public Guid? OriginWorkItemId { get; set; }
    public Guid? CreatedByOrganizationUserId { get; set; }
    public Guid? StewardOrganizationUserId { get; set; }
    public string CreatorDisplayName { get; set; } = "Unknown";
    public string? CreatorAgentId { get; set; }
    public string? CreatorAgentVersion { get; set; }
    public string DocumentType { get; set; } = "document";
    public ArtifactDocumentStatus DocumentStatus { get; set; } = ArtifactDocumentStatus.Draft;
    public Guid? LatestRevisionId { get; set; }
    public Guid? SubmittedRevisionId { get; set; }
    public Guid? AcceptedRevisionId { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    // Navigation
    public Organization? Organization { get; set; }
    public WorkTask? Task { get; set; }
    public TaskRun? TaskRun { get; set; }
    public ArtifactFolder? Folder { get; set; }
    public ArtifactPackage? Package { get; set; }
    public OrganizationUser? CreatedByOrganizationUser { get; set; }
    public OrganizationUser? StewardOrganizationUser { get; set; }
    public ICollection<ArtifactRevision> Revisions { get; set; } = [];
}

public enum ArtifactDocumentStatus { Draft, InReview, Approved, ChangesRequested, Archived }
public enum ArtifactRevisionStatus { Draft, Submitted, Accepted, Rejected, Superseded }

public sealed class ArtifactRevision
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ArtifactId { get; set; }
    public int Number { get; set; }
    public Guid? BaseRevisionId { get; set; }
    public string Content { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
    public ArtifactRevisionStatus Status { get; set; } = ArtifactRevisionStatus.Draft;
    public Guid? CreatedByOrganizationUserId { get; set; }
    public Guid? CreatedByAgentInstallationId { get; set; }
    public string CreatorDisplayName { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public Artifact? Artifact { get; set; }
}

public sealed class ArtifactFolder
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? ParentFolderId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public ArtifactFolder? ParentFolder { get; set; }
}

public sealed class ArtifactPackage
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? WorkstreamId { get; set; }
    public Guid? TeamId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string PackageType { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? LastSubmissionIdempotencyKey { get; set; }
    public string? LastDecisionIdempotencyKey { get; set; }
    public ArtifactDocumentStatus Status { get; set; } = ArtifactDocumentStatus.Draft;
    public Guid? CreatedByOrganizationUserId { get; set; }
    public Guid? AcceptedByOrganizationUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public ICollection<ArtifactPackageMember> Members { get; set; } = [];
}

public sealed class ArtifactPackageMember
{
    public Guid Id { get; set; }
    public Guid PackageId { get; set; }
    public Guid ArtifactId { get; set; }
    public Guid? AcceptedRevisionId { get; set; }
    public int Position { get; set; }
    public string RequiredDocumentType { get; set; } = string.Empty;
    public ArtifactPackage? Package { get; set; }
    public Artifact? Artifact { get; set; }
}

public enum ArtifactAccessRequestStatus { Pending, Approved, Rejected, Cancelled, Expired }

public sealed class ArtifactAccessRequest
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ArtifactId { get; set; }
    public GrantSubjectKind SubjectKind { get; set; }
    public Guid SubjectId { get; set; }
    public Guid? RequestingInstallationId { get; set; }
    public string ActionsJson { get; set; } = "[]";
    public string Justification { get; set; } = string.Empty;
    public ArtifactAccessRequestStatus Status { get; set; } = ArtifactAccessRequestStatus.Pending;
    public string IdempotencyKey { get; set; } = string.Empty;
    public Guid? DecidedByOrganizationUserId { get; set; }
    public Guid? EvidenceConversationMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public Artifact? Artifact { get; set; }
}

public enum ArtifactReviewJobStatus { Pending, Processing, Completed, Failed }

public sealed class ArtifactReviewJob
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ArtifactId { get; set; }
    public Guid RevisionId { get; set; }
    public Guid? ConversationId { get; set; }
    public Guid? ReviewerOrganizationUserId { get; set; }
    public Guid? ReviewerInstallationId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public ArtifactReviewJobStatus Status { get; set; } = ArtifactReviewJobStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class ArtifactReview
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ArtifactId { get; set; }
    public Guid RevisionId { get; set; }
    public string RevisionDigest { get; set; } = string.Empty;
    public string RubricTypeKey { get; set; } = string.Empty;
    public string Disposition { get; set; } = string.Empty;
    public string FindingsJson { get; set; } = "[]";
    public string? Comment { get; set; }
    public Guid ReviewerOrganizationUserId { get; set; }
    public Guid? ReviewerInstallationId { get; set; }
    public Guid? EvidenceConversationMessageId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ConversationMessageArtifact
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid MessageId { get; set; }
    public Guid ArtifactId { get; set; }
    public Guid? RevisionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ConversationMessage? Message { get; set; }
    public Artifact? Artifact { get; set; }
}
