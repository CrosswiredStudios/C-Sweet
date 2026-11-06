using CSweet.Contracts.Core;

namespace CSweet.Application.Core;

public interface IChatTurnService
{
    Task<ChatTurnStartResponse?> StartAsync(Guid organizationId, Guid conversationId, string message, Guid? retryOfTurnId = null, CancellationToken cancellationToken = default);
    Task<ChatTurnStartResponse?> StartForAgentAsync(Guid organizationId, Guid conversationId, Guid targetAgentOrganizationUserId, string message, Guid? senderOrganizationUserId = null, string sourceProvider = "InApp", string? sourceChannelExternalId = null, string? idempotencyKey = null, IReadOnlyList<Guid>? attachmentMediaAssetIds = null, CancellationToken cancellationToken = default);
    Task<ChatTurnResponse?> GetAsync(Guid organizationId, Guid turnId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatTurnResponse>> ListForConversationAsync(Guid organizationId, Guid conversationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatTurnTraceEventResponse>> ListEventsAsync(Guid organizationId, Guid turnId, long afterSequence = -1, CancellationToken cancellationToken = default);
    Task<bool> CancelAsync(Guid organizationId, Guid turnId, CancellationToken cancellationToken = default);
    Task<ChatTurnStartResponse?> RetryAsync(Guid organizationId, Guid turnId, CancellationToken cancellationToken = default);
    Task<Guid?> ClaimNextAsync(string leaseOwner, CancellationToken cancellationToken = default);
    Task DeferForRuntimeAsync(Guid turnId, TimeSpan retryDelay, CancellationToken cancellationToken = default);
    Task<ChatTurnTraceEventResponse> TraceAsync(Guid turnId, string category, string eventType, string status, string title, string? summary = null, object? details = null, string sensitivity = "Internal", long? durationMs = null, CancellationToken cancellationToken = default);
    /// <summary>Moves an open turn to a worker status. Returns false when the stored turn is already
    /// terminal (for example cancelled concurrently); the caller must stop processing it.</summary>
    Task<bool> SetStatusAsync(Guid turnId, string status, string? errorCode = null, string? errorMessage = null, CancellationToken cancellationToken = default);
    Task AppendOutputAsync(Guid turnId, string delta, CancellationToken cancellationToken = default);
    Task ReplaceOutputAsync(Guid turnId, string content, CancellationToken cancellationToken = default);
    /// <summary>Completes an open turn. Returns false when the stored turn was already terminal.</summary>
    Task<bool> CompleteAsync(Guid turnId, Guid assistantMessageId, bool memoryWarning, CancellationToken cancellationToken = default);
}
