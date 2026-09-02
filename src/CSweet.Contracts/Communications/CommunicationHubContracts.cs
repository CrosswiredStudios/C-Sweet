using System.ComponentModel.DataAnnotations;

namespace CSweet.Contracts.Communications;

public sealed record CommunicationHubResponse(
    Guid CurrentOrganizationUserId,
    Guid ViewedOrganizationUserId,
    bool IsReadOnlyPerspective,
    bool CanManageChats,
    IReadOnlyList<CommunicationChatResponse> Chats,
    IReadOnlyList<CommunicationPersonResponse> People,
    IReadOnlyList<CommunicationAudienceResponse> Audiences);

public sealed record CommunicationChatResponse(
    Guid Id,
    string Title,
    string? Description,
    bool IsDirect,
    bool IsPrivate,
    bool IsDeletionProtected,
    bool CanManage,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<CommunicationParticipantResponse> Participants,
    string? LastMessage,
    DateTimeOffset? LastMessageAt,
    int UnreadCount)
{
    public Guid? WorkstreamId { get; init; }
    public Guid? TeamId { get; init; }
}

public sealed record CommunicationParticipantResponse(
    Guid OrganizationUserId,
    string DisplayName,
    string EmployeeType,
    string Role,
    string PresenceStatus = CommunicationPresenceStatuses.Available,
    string? PresenceDetail = null);

public sealed record CommunicationPersonResponse(
    Guid Id,
    string DisplayName,
    string EmployeeType,
    Guid? RoleId,
    string? RoleName,
    string PresenceStatus = CommunicationPresenceStatuses.Available,
    string? PresenceDetail = null);

public static class CommunicationPresenceStatuses
{
    public const string Available = "Available";
    public const string ReadyOnDemand = "ReadyOnDemand";
    public const string Scheduled = "Scheduled";
    public const string PendingActivation = "PendingActivation";
    public const string Starting = "Starting";
    public const string Unhealthy = "Unhealthy";
    public const string Offline = "Offline";
}

public sealed record CommunicationAudienceResponse(
    string Kind,
    Guid Id,
    string Name,
    IReadOnlyList<Guid> OrganizationUserIds);

public sealed record CommunicationHubMessageResponse(
    Guid Id,
    long Sequence,
    Guid ChatId,
    Guid? SenderOrganizationUserId,
    string SenderDisplayName,
    string SenderEmployeeType,
    string Content,
    DateTimeOffset CreatedAt,
    Guid? ChatTurnId = null,
    ExecutiveDecisionCardResponse? Decision = null,
    IReadOnlyList<SuggestedUserActionResponse>? Actions = null,
    CSweet.Contracts.Core.ResourceChangeRequestResponse? ResourceChange = null,
    CSweet.Contracts.Core.HiringWorkflowApprovalResponse? HiringWorkflow = null)
{
    public string MessageType { get; init; } = CommunicationMessageTypes.Standard;
    public Guid? CoordinationSessionId { get; init; }
    public IReadOnlyList<CommunicationMessageMentionResponse> Mentions { get; init; } = [];
    public IReadOnlyList<CommunicationMessageAttachmentResponse> Attachments { get; init; } = [];
    public IReadOnlyList<CommunicationMessageArtifactResponse> Artifacts { get; init; } = [];
}

public sealed record CommunicationMessageAttachmentResponse(
    Guid Id,
    Guid MessageId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256);

public sealed record CommunicationMessageArtifactResponse(
    Guid Id,
    Guid MessageId,
    Guid ArtifactId,
    Guid? RevisionId)
{
    public string? Title { get; init; }
    public string? Status { get; init; }
    public Guid? SubmittedRevisionId { get; init; }
    public Guid? AcceptedRevisionId { get; init; }
}

public sealed record CommunicationMessageMentionResponse(
    Guid OrganizationUserId,
    string DisplayName,
    string EmployeeType,
    int Offset,
    int Length,
    string DisplayText);

public sealed record CommunicationMessageMentionInput(
    Guid OrganizationUserId,
    int Offset,
    int Length);

public sealed record AgentCoordinationParticipantResponse(
    Guid OrganizationUserId,
    Guid InstallationId,
    string DisplayName,
    string Role);

public sealed record AgentCoordinationTurnResponse(
    Guid Id,
    long Ordinal,
    Guid SpeakerOrganizationUserId,
    string Disposition,
    string Content,
    DateTimeOffset CreatedAt,
    AgentCoordinationArtifactResponse? Artifact = null);

public sealed record AgentCoordinationArtifactResponse(
    string Type,
    string SchemaVersion,
    string Key,
    int PageOrdinal,
    bool IsFinalPage,
    System.Text.Json.JsonElement Payload,
    string Digest);

public sealed record AgentCoordinationSessionResponse(
    Guid Id,
    Guid ConversationId,
    Guid SourceConversationId,
    AgentCoordinationParticipantResponse Initiator,
    AgentCoordinationParticipantResponse Target,
    string Subject,
    string Objective,
    IReadOnlyList<string> SuccessCriteria,
    string Status,
    long Revision,
    long NextTurnOrdinal,
    Guid? CurrentOrganizationUserId,
    bool IsFinalization,
    string? FinalSummary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AgentCoordinationTurnResponse> Turns);

public sealed record StopAgentCoordinationRequest(
    long ExpectedRevision,
    [property: Required, MaxLength(1000)] string Reason,
    [property: Required, MaxLength(160)] string IdempotencyKey);

public static class CommunicationMessageTypes
{
    public const string Standard = "Standard";
    public const string SystemAction = "SystemAction";
    public const string ResourceChangeApproval = "ResourceChangeApproval";
    public const string HiringWorkflowApproval = "HiringWorkflowApproval";
}

public static class SuggestedUserActionCapabilities
{
    public const string Suggest = "platform.user-action.suggest.v1";
}

public static class SuggestedUserActionWorkflows
{
    public const string BrowseHiringMarketplace = "hiring.marketplace.browse.v1";
}

public sealed record SuggestUserActionRequest(
    Guid? MessageId,
    Guid? ChatTurnId,
    [property: Required, MaxLength(160)] string WorkflowType,
    [property: Required, MaxLength(120)] string Label,
    [property: MaxLength(500)] string? Description,
    System.Text.Json.JsonElement Parameters,
    [property: Required, MaxLength(160)] string IdempotencyKey);

public sealed record SuggestedUserActionResponse(
    Guid Id,
    string WorkflowType,
    string Label,
    string? Description,
    string NavigationUri,
    string Status,
    DateTimeOffset CreatedAt)
{
    public Guid? HiringRecommendationId { get; init; }
    public Guid? ResultOrganizationUserId { get; init; }
    public string? ResultOrganizationUserDisplayName { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed record ExecutiveDecisionOptionResponse(
    string Id,
    string Label,
    string? Description,
    bool Recommended);

public sealed record ExecutiveDecisionCardResponse(
    Guid Id,
    string Prompt,
    string Status,
    IReadOnlyList<ExecutiveDecisionOptionResponse> Options,
    string RecommendedOptionId,
    string? SelectedOptionId,
    string? FreeTextAnswer,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AnsweredAt);

public sealed record AnswerExecutiveDecisionRequest(
    [property: MaxLength(80)] string? OptionId,
    [property: MaxLength(4000)] string? SomethingElse,
    [property: Required, MaxLength(160)] string IdempotencyKey);

public sealed record AnswerExecutiveDecisionResponse(
    bool Succeeded,
    string? ErrorCode,
    string Message,
    ExecutiveDecisionCardResponse? Decision = null,
    CSweet.Contracts.Core.ChatTurnResponse? Turn = null);

public sealed record CommunicationMessageSendResponse(
    CommunicationHubMessageResponse Message,
    CSweet.Contracts.Core.ChatTurnResponse? Turn = null);

public sealed record CommunicationUnreadSummaryResponse(
    int TotalUnreadCount,
    IReadOnlyDictionary<Guid, int> ChatUnreadCounts);

public sealed record CommunicationChatMessagesResponse(
    IReadOnlyList<CommunicationHubMessageResponse> Messages);

public sealed record MarkCommunicationChatReadRequest(long ThroughMessageSequence);

public sealed record CreateCommunicationChatRequest(
    [property: MaxLength(256)] string? Title,
    [property: MaxLength(2048)] string? Description,
    bool IsDirect,
    bool IsPrivate,
    IReadOnlyList<Guid> ParticipantOrganizationUserIds,
    IReadOnlyList<Guid>? AudienceRoleIds = null,
    IReadOnlyList<Guid>? AudienceWorkstreamIds = null)
{
    public Guid? WorkstreamId { get; init; }
    public Guid? TeamId { get; init; }
}

public sealed record UpdateCommunicationChatRequest(
    [property: Required, MaxLength(256)] string Title,
    [property: MaxLength(2048)] string? Description,
    bool IsPrivate,
    IReadOnlyList<Guid> ParticipantOrganizationUserIds,
    IReadOnlyList<Guid>? AudienceRoleIds = null,
    IReadOnlyList<Guid>? AudienceWorkstreamIds = null);

public sealed record SendCommunicationMessageRequest(
    [property: MaxLength(32768)] string Content,
    [property: MaxLength(160)] string? IdempotencyKey = null,
    IReadOnlyList<CommunicationMessageMentionInput>? Mentions = null,
    IReadOnlyList<Guid>? AttachmentMediaAssetIds = null);

public sealed record CommunicationHubActionResponse(
    bool Succeeded,
    string? ErrorCode,
    string Message,
    CommunicationChatResponse? Chat = null);

public static class CommunicationHubCapabilities
{
    public const string Read = "communication.chat.read.v1";
    public const string Create = "communication.chat.create.v1";
    public const string Modify = "communication.chat.modify.v1";
    public const string Delete = "communication.chat.delete.v1";
    public const string SendMessage = "communication.message.send.v1";
    public const string AskUser = "platform.user-input.request.v1";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Read, Create, Modify, Delete, SendMessage, AskUser
    };
}
