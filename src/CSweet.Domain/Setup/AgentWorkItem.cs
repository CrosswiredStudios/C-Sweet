namespace CSweet.Domain.Setup;

public enum AgentWorkKind
{
    Capability,
    Event,
    Shutdown
}

public enum AgentWorkStatus
{
    Pending,
    Leased,
    Completed,
    Cancelled,
    DeadLetter
}

public sealed class AgentWorkItem
{
    public Guid Id { get; set; }
    public string OrganizationId { get; set; } = string.Empty;
    public Guid AgentInstallationId { get; set; }
    public AgentWorkKind Kind { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte[] ProtectedPayload { get; set; } = [];
    public string PayloadHash { get; set; } = string.Empty;
    public byte[]? ProtectedResult { get; set; }
    public string? ResultHash { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string? CausationId { get; set; }
    public string? SourceType { get; set; }
    public string? SourceId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public AgentWorkStatus Status { get; set; } = AgentWorkStatus.Pending;
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset DeadlineAt { get; set; }
    public int MaximumAttempts { get; set; } = 3;
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LastError { get; set; }

    public AgentInstallation? AgentInstallation { get; set; }
    public ICollection<AgentWorkAttempt> Attempts { get; set; } = [];
    public ICollection<AgentWorkProgress> Progress { get; set; } = [];
}

public sealed class AgentWorkAttempt
{
    public Guid Id { get; set; }
    public Guid AgentWorkItemId { get; set; }
    public Guid RuntimeInstanceId { get; set; }
    public int Attempt { get; set; }
    public string LeaseTokenHash { get; set; } = string.Empty;
    public DateTimeOffset ClaimedAt { get; set; }
    public DateTimeOffset LeaseExpiresAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public long LastProgressSequence { get; set; }
    public string? CompletionHash { get; set; }
    public string? Error { get; set; }

    public AgentWorkItem? AgentWorkItem { get; set; }
    public AgentRuntimeInstance? RuntimeInstance { get; set; }
}

public sealed class AgentWorkProgress
{
    public Guid Id { get; set; }
    public Guid AgentWorkItemId { get; set; }
    public Guid AgentWorkAttemptId { get; set; }
    public long Sequence { get; set; }
    public byte[] ProtectedValue { get; set; } = [];
    public int SizeBytes { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    public AgentWorkItem? AgentWorkItem { get; set; }
    public AgentWorkAttempt? AgentWorkAttempt { get; set; }
}
