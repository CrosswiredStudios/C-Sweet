namespace CSweet.Domain.Core;

public enum ChatTurnStatus
{
    Queued,
    RecallingMemory,
    Dispatching,
    Running,
    FinalizingMemory,
    Completed,
    CompletedWithWarnings,
    Failed,
    Cancelled
}

public sealed class ChatTurn
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid TargetAgentOrganizationUserId { get; set; }
    public Guid UserMessageId { get; set; }
    public Guid? AssistantMessageId { get; set; }
    public Guid? RetryOfTurnId { get; set; }
    public ChatTurnStatus Status { get; set; }
    public int Attempt { get; set; }
    public long NextTraceSequence { get; set; }
    public string PartialResponse { get; set; } = string.Empty;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FirstOutputAt { get; set; }
    public DateTimeOffset? ResponseReadyAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    public DateTimeOffset? MemoryErasedAt { get; set; }

    public Conversation? Conversation { get; set; }
    public ConversationMessage? UserMessage { get; set; }
    public ConversationMessage? AssistantMessage { get; set; }
    public ICollection<ChatTurnTraceEvent> TraceEvents { get; set; } = new List<ChatTurnTraceEvent>();
}

public sealed class ChatTurnTraceEvent
{
    public Guid Id { get; set; }
    public Guid ChatTurnId { get; set; }
    public long Sequence { get; set; }
    public string Category { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? DetailsJson { get; set; }
    public string Sensitivity { get; set; } = "Internal";
    public long? DurationMs { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    public ChatTurn? ChatTurn { get; set; }
}
