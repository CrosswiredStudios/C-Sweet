using System.Text.Json;

namespace CSweet.Contracts.Communications;

/// <summary>Versioned broker events that describe every persisted conversation-state mutation.</summary>
public static class CommunicationEvents
{
    public const string ChatCreated = "com.csweet.communication.chat.created.v1";
    public const string ChatUpdated = "com.csweet.communication.chat.updated.v1";
    public const string ChatArchived = "com.csweet.communication.chat.archived.v1";
    public const string ChatDeleted = "com.csweet.communication.chat.deleted.v1";
    public const string ParticipantAdded = "com.csweet.communication.participant.added.v1";
    public const string ParticipantUpdated = "com.csweet.communication.participant.updated.v1";
    public const string ParticipantRemoved = "com.csweet.communication.participant.removed.v1";
    public const string MessageCreated = "com.csweet.communication.message.created.v1";
    public const string MessageUpdated = "com.csweet.communication.message.updated.v1";
    public const string MessageDeleted = "com.csweet.communication.message.deleted.v1";
    public const string MessageMentioned = "com.csweet.communication.message.mentioned.v1";
    public const string ReadUpdated = "com.csweet.communication.read.updated.v1";

    public static readonly IReadOnlyList<string> All =
    [
        ChatCreated, ChatUpdated, ChatArchived, ChatDeleted,
        ParticipantAdded, ParticipantUpdated, ParticipantRemoved,
        MessageCreated, MessageUpdated, MessageDeleted, MessageMentioned, ReadUpdated
    ];

    public static string Subject(Guid organizationId, Guid chatId) =>
        $"organizations/{organizationId:D}/communications/chats/{chatId:D}";
}

/// <summary>
/// Stable event envelope. EventId is the consumer idempotency key and Sequence provides a
/// deterministic global ordering for replay or replication.
/// </summary>
public sealed record CommunicationEventEnvelope(
    Guid EventId,
    Guid OrganizationId,
    long Sequence,
    string EventType,
    string Subject,
    DateTimeOffset OccurredAt,
    JsonElement Data);

public sealed record CommunicationChatEvent(
    Guid ChatId,
    Guid OrganizationId,
    string Kind,
    Guid InitiatedByOrganizationUserId,
    Guid? AgentOrganizationUserId,
    Guid? TeamId,
    Guid? WorkstreamId,
    string? Title,
    string? Description,
    bool IsPrivate,
    bool IsDeletionProtected,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

public sealed record CommunicationParticipantEvent(
    Guid ParticipantId,
    Guid ChatId,
    Guid OrganizationUserId,
    string Role,
    DateTimeOffset JoinedAt,
    DateTimeOffset? LeftAt);

public sealed record CommunicationMessageEvent(
    Guid MessageId,
    Guid ChatId,
    Guid? SenderOrganizationUserId,
    Guid? ReplyToMessageId,
    string Role,
    string Content,
    Guid CorrelationId,
    Guid? CausationId,
    string DeliveryIntent,
    string SourceProvider,
    string? SourceChannelExternalId,
    DateTimeOffset CreatedAt);

public sealed record CommunicationReadEvent(
    Guid ChatId,
    Guid OrganizationUserId,
    long LastReadMessageSequence,
    DateTimeOffset UpdatedAt);

public sealed record CommunicationMessageMentionedEvent(
    Guid MentionId,
    Guid MessageId,
    Guid ChatId,
    Guid MentionedOrganizationUserId,
    Guid? SenderOrganizationUserId,
    string SenderDisplayName,
    string Content,
    int Offset,
    int Length,
    DateTimeOffset CreatedAt);
