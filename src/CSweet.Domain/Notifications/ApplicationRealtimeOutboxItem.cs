namespace CSweet.Domain.Notifications;

public enum ApplicationRealtimeOutboxStatus
{
    Pending,
    Published,
    DeadLettered
}

public sealed class ApplicationRealtimeOutboxItem
{
    public Guid Id { get; set; }
    public long Sequence { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? RecipientOrganizationUserId { get; set; }
    public Guid? RecipientApplicationUserId { get; set; }
    public string RecipientOrganizationUserIdsJson { get; set; } = "[]";
    public Guid? ChatId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string DataJson { get; set; } = "{}";
    public ApplicationRealtimeOutboxStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}
