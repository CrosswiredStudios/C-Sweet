namespace CSweet.Api.Chat;

internal static class AgentChatEvents
{
    public const string UserMessageReceivedEvent = "com.csweet.user.message.received.v1";

    public const string AssistantResponseCreatedEvent = "com.csweet.assistant.response.created.v1";

    public const string AssistantResponseChunkEvent = "com.csweet.assistant.response.chunk.v1";
}

internal static class AgentChatContextKeys
{
    public const string SenderOrganizationUserId = "senderOrganizationUserId";
    public const string SenderDisplayName = "senderDisplayName";
    public const string SenderEmployeeType = "senderEmployeeType";
    public const string SenderRole = "senderRole";
}

internal sealed record ChatMessageSender(
    Guid OrganizationUserId,
    string DisplayName,
    string EmployeeType,
    string? Role);

internal sealed record RecentConversationMessage(
    long Sequence,
    string Role,
    string Content);

internal sealed record UserMessageReceived(
    Guid ProviderProfileId,
    string ConversationId,
    string UserId,
    string Message,
    IReadOnlyDictionary<string, string>? Context,
    Guid TurnId = default,
    int Attempt = 0,
    Guid MessageId = default);

internal sealed record AssistantResponseChunk(
    string ConversationId,
    int Sequence,
    string Delta,
    bool IsFinal,
    string? Error = null,
    Guid TurnId = default,
    string Kind = "output",
    IReadOnlyDictionary<string, string>? Metadata = null,
    int Attempt = 0);
