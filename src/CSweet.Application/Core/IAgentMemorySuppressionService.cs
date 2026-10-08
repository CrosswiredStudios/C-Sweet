using CSweet.Contracts.Memory;

namespace CSweet.Application.Core;

public interface IAgentMemorySuppressionService
{
    Task<MemorySuppressionPreview> GetSuppressionAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, CancellationToken cancellationToken = default);
    Task<SuppressMemorySourceResponse> SuppressSourceAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, SuppressMemorySourceRequest request, CancellationToken cancellationToken = default);
}
