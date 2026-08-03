namespace CSweet.Domain.Core;

public sealed class ConversationMessage
{
    public Guid Id { get; set; }
    public long Sequence { get; set; }
    public Guid ConversationId { get; set; }
    public ConversationRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ChatTurnId { get; set; }
    public Guid? CoordinationSessionId { get; set; }
    public Guid? SenderOrganizationUserId { get; set; }
    public Guid? ReplyToMessageId { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public CommunicationDeliveryIntent DeliveryIntent { get; set; } = CommunicationDeliveryIntent.Inform;
    public string SourceProvider { get; set; } = "InApp";
    public string? SourceChannelExternalId { get; set; }
    public string? IdempotencyKey { get; set; }
    public int HopCount { get; set; }

    // Navigation
    public Conversation? Conversation { get; set; }
}

public sealed class SuggestedUserAction
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid OriginatingInstallationId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid? ConversationMessageId { get; set; }
    public Guid? ChatTurnId { get; set; }
    public string WorkflowType { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ParametersJson { get; set; } = "{}";
    public string NavigationUri { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public DateTimeOffset CreatedAt { get; set; }
}

public enum CommunicationDeliveryIntent
{
    Inform,
    RequestResponse,
    Response
}
