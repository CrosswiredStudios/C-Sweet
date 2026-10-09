using CSweet.Contracts.Memory;

namespace CSweet.Application.Core;

public interface IAgentMemoryErasureImpactService
{
    Task<MemoryErasureImpactResponse> GetErasureImpactAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, CancellationToken cancellationToken = default);
    Task<MemoryErasureResponse> EraseSourceAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, EraseMemorySourceRequest request, CancellationToken cancellationToken = default);
    Task<MemoryErasureResponse> GetErasureStatusAsync(Guid organizationId, Guid employeeId, Guid operationId,
        Guid applicationUserId, CancellationToken cancellationToken = default);
    Task<MemoryErasureOperationPage> ListErasureOperationsAsync(Guid organizationId, Guid employeeId,
        Guid applicationUserId, Guid? beforeReceiptId = null, int limit = 10, CancellationToken cancellationToken = default);
}
