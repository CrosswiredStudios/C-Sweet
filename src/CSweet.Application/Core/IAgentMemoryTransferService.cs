using CSweet.Contracts.Memory;

namespace CSweet.Application.Core;

public interface IAgentMemoryTransferService
{
    Task<MemoryTransferResult> PrepareAsync(Guid organizationId, Guid employeeId, Guid applicationUserId,
        PrepareMemoryTransferRequest request, CancellationToken token = default);
    Task<MemoryTransferResponse> GetAsync(Guid organizationId, Guid employeeId, Guid packageId, Guid applicationUserId,
        CancellationToken token = default);
    Task<MemoryTransferPage> ListAsync(Guid organizationId, Guid employeeId, Guid applicationUserId, Guid? cursor = null, CancellationToken token = default);
    Task<MemoryTransferResult> TransitionAsync(Guid organizationId, Guid employeeId, Guid packageId, Guid applicationUserId,
        TransitionMemoryTransferRequest request, CancellationToken token = default);
}
