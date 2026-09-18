namespace CSweet.Domain.Setup;

/// <summary>One immutable published candidate, its independent review, and integration state.</summary>
public sealed class TaskDeliveryReview
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BoardId { get; set; }
    public Guid EpicId { get; set; }
    public Guid StoryId { get; set; }
    public Guid TaskId { get; set; }
    public Guid RepositoryId { get; set; }
    public Guid PublicationId { get; set; }
    public Guid DeveloperInstallationId { get; set; }
    public Guid ManagerOrganizationUserId { get; set; }
    public Guid? QaInstallationId { get; set; }
    public string CommitSha { get; set; } = "";
    public string Status { get; set; } = "Testing";
    public string QualityStatus { get; set; } = "Pending";
    public string Summary { get; set; } = "";
    public string? QualityEvidenceJson { get; set; }
    public Guid? DecisionId { get; set; }
    public Guid? ApprovedByOrganizationUserId { get; set; }
    public string? ApprovedCommitSha { get; set; }
    public string? MergeCommitSha { get; set; }
    public string? Failure { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Manager-owned, revocable story/epic preference shared by its task developers.</summary>
public sealed class TaskMergePreference
{
    public Guid OrganizationId { get; set; }
    public Guid ScopeWorkItemId { get; set; }
    public string Mode { get; set; } = "Ask";
    public Guid ManagerOrganizationUserId { get; set; }
    public Guid? SourceMessageId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
}
