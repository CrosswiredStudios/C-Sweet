namespace CSweet.Domain.Communications;

public enum AgentCoordinationStatus
{
    Active,
    Summarizing,
    Completed,
    Blocked,
    Cancelled,
    Failed
}

public sealed class AgentCoordinationSession
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SourceConversationId { get; set; }
    public Guid SourceChatTurnId { get; set; }
    public Guid SourceMessageId { get; set; }
    public Guid InitiatorOrganizationUserId { get; set; }
    public Guid InitiatorInstallationId { get; set; }
    public Guid TargetOrganizationUserId { get; set; }
    public Guid TargetInstallationId { get; set; }
    public Guid? CurrentOrganizationUserId { get; set; }
    public Guid? CurrentAgentWorkItemId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Objective { get; set; } = string.Empty;
    public string SuccessCriteriaJson { get; set; } = "[]";
    public AgentCoordinationStatus Status { get; set; } = AgentCoordinationStatus.Active;
    public long Revision { get; set; } = 1;
    public int NextTurnOrdinal { get; set; } = 1;
    public bool IsFinalization { get; set; }
    public string? FinalSummary { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? LastResumeIdempotencyKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public ICollection<AgentCoordinationTurn> Turns { get; set; } = [];
}

public sealed class AgentCoordinationTurn
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid EventId { get; set; }
    public Guid SpeakerOrganizationUserId { get; set; }
    public Guid? AgentWorkItemId { get; set; }
    public Guid? ConversationMessageId { get; set; }
    public int Ordinal { get; set; }
    public string Disposition { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public AgentCoordinationSession? Session { get; set; }
}
