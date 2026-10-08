using CSweet.Contracts.Memory;

namespace CSweet.Application.Core;

public interface IAgentMemoryIngestionRecoveryService
{
    Task<MemoryIngestionCandidatePage> ListAsync(Guid organizationId, Guid employeeId, Guid applicationUserId,
        string? cursor = null, int limit = 20, CancellationToken token = default);
    Task<MemoryIngestionRecoveryPreview> PreviewAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, CancellationToken token = default);
    Task<RecoverMemoryIngestionResponse> RecoverAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, RecoverMemoryIngestionRequest request, CancellationToken token = default);
}
