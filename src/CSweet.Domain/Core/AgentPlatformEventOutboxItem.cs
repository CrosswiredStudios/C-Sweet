namespace CSweet.Domain.Core;

public enum AgentPlatformEventOutboxStatus
{
    Pending,
    Published,
    Failed
}

/// <summary>Durable platform event awaiting routing to subscribed organization agents.</summary>
public sealed class AgentPlatformEventOutboxItem
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? TargetInstallationId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string DataJson { get; set; } = "{}";
    public string IdempotencyKey { get; set; } = string.Empty;
    public AgentPlatformEventOutboxStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}
