using CSweet.Contracts.Communications;

namespace CSweet.Application.Communications;

public interface ICommunicationHubService
{
    Task<Guid?> ResolveOrganizationUserIdAsync(Guid organizationId, Guid applicationUserId, CancellationToken cancellationToken = default);
    Task<CommunicationHubResponse?> GetAsync(
        Guid organizationId,
        Guid actorOrganizationUserId,
        Guid? perspectiveOrganizationUserId = null,
        CancellationToken cancellationToken = default);
    Task<bool> CanAccessChatAsync(Guid organizationId, Guid chatId, Guid actorOrganizationUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommunicationHubMessageResponse>?> ListMessagesAsync(
        Guid organizationId,
        Guid chatId,
        Guid actorOrganizationUserId,
        Guid? perspectiveOrganizationUserId = null,
        CancellationToken cancellationToken = default);
    Task<CommunicationUnreadSummaryResponse?> GetUnreadSummaryAsync(Guid organizationId, Guid actorOrganizationUserId, CancellationToken cancellationToken = default);
    Task<CommunicationUnreadSummaryResponse?> MarkReadAsync(Guid organizationId, Guid chatId, Guid actorOrganizationUserId, long throughMessageSequence, CancellationToken cancellationToken = default);
    Task<CommunicationHubActionResponse> CreateAsync(Guid organizationId, Guid actorOrganizationUserId, CreateCommunicationChatRequest request, CancellationToken cancellationToken = default);
    Task<CommunicationHubActionResponse> UpdateAsync(Guid organizationId, Guid chatId, Guid actorOrganizationUserId, UpdateCommunicationChatRequest request, CancellationToken cancellationToken = default);
    Task<CommunicationHubActionResponse> ArchiveAsync(Guid organizationId, Guid chatId, Guid actorOrganizationUserId, CancellationToken cancellationToken = default);
    Task<CommunicationMessageSendResponse?> SendAsync(Guid organizationId, Guid chatId, Guid actorOrganizationUserId, SendCommunicationMessageRequest request, CancellationToken cancellationToken = default);
}
