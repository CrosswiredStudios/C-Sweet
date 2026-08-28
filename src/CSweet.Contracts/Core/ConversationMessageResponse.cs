using CSweet.Contracts.Communications;

namespace CSweet.Contracts.Core;

public sealed record ConversationMessageResponse(
    Guid Id,
    Guid ConversationId,
    int Role,          // 0 = User, 1 = Assistant (matches ConversationRole)
    string Content,
    DateTimeOffset CreatedAt,
    Guid? ChatTurnId = null)
{
    public long Sequence { get; init; }
    public Guid? SenderOrganizationUserId { get; init; }
    public Guid? ReplyToMessageId { get; init; }
    public Guid CorrelationId { get; init; }
    public string DeliveryIntent { get; init; } = "Inform";
    public string SourceProvider { get; init; } = "InApp";
    public string? SourceChannelExternalId { get; init; }
    public IReadOnlyList<CommunicationMessageAttachmentResponse> Attachments { get; init; } = [];
    public IReadOnlyList<CommunicationMessageArtifactResponse> Artifacts { get; init; } = [];
}
