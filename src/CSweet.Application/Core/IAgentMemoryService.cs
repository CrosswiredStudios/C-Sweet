using CSweet.Contracts.Memory;

namespace CSweet.Application.Core;

public interface IAgentMemoryService
{
    Task<PreparedMemoryRecall> PrepareTurnRecallAsync(Guid turnId, bool includeMemory = true, CancellationToken cancellationToken = default);
    Task<bool> CanExploreAsync(Guid organizationId, Guid? applicationUserId, CancellationToken cancellationToken = default);
    Task<string?> RecallForConversationAsync(Guid conversationId, string query, CancellationToken cancellationToken = default);
    Task<string?> RecallForConversationRecipientAsync(Guid conversationId, Guid employeeId, Guid userId, string query,
        CancellationToken cancellationToken = default);
    Task CaptureMessageAsync(Guid messageId, bool enrich = false, CancellationToken cancellationToken = default);
    Task<int> ProcessPendingAsync(int limit = 20, CancellationToken cancellationToken = default);
    Task<AgentMemorySummaryResponse?> GetSummaryAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default);
    Task<AgentMemoryPageResponse?> BrowseAsync(Guid organizationId, Guid employeeId, AgentMemoryQuery query, CancellationToken cancellationToken = default);
    Task<AgentMemoryGraphResponse?> GetGraphAsync(Guid organizationId, Guid employeeId, string? search, Guid? userId, int limit = 100, CancellationToken cancellationToken = default);
    Task<AgentMemoryItemResponse?> GetItemAsync(Guid organizationId, Guid employeeId, Guid memoryId, CancellationToken cancellationToken = default);
}
