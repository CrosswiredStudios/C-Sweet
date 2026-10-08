using CSweet.Contracts.Memory;

namespace CSweet.Application.Core;

public interface IAgentMemoryHoldService
{
    Task<MemoryHoldPreview> GetHoldAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, CancellationToken cancellationToken = default);
    Task<ReviewMemoryHoldResponse> ReviewHoldAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, ReviewMemoryHoldRequest request, CancellationToken cancellationToken = default);
}
