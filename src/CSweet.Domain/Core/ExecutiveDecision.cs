namespace CSweet.Domain.Core;

public enum ExecutiveDecisionStatus
{
    Pending,
    Answered,
    Superseded,
    Cancelled
}

public sealed class ExecutiveDecision
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid? ChatTurnId { get; set; }
    public Guid? ConversationMessageId { get; set; }
    public Guid RequestingInstallationId { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string OptionsJson { get; set; } = "[]";
    public string RecommendedOptionId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public ExecutiveDecisionStatus Status { get; set; }
    public Guid? SupersededByDecisionId { get; set; }
    public string? SelectedOptionId { get; set; }
    public string? FreeTextAnswer { get; set; }
    public Guid? AnsweredByOrganizationUserId { get; set; }
    public string? AnswerIdempotencyKey { get; set; }
    public Guid? NextChatTurnId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }

    public Conversation? Conversation { get; set; }
    public ChatTurn? ChatTurn { get; set; }
    public ConversationMessage? ConversationMessage { get; set; }
}
