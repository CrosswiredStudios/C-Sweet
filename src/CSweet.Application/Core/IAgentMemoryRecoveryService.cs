using CSweet.Contracts.Memory;

namespace CSweet.Application.Core;

public interface IAgentMemoryRecoveryService
{
    Task<MemoryEnrichmentJobPageResponse> ListFailuresAsync(Guid organizationId, Guid employeeId,
        Guid applicationUserId, string? cursor = null, int limit = 20, CancellationToken cancellationToken = default);
    Task<RetryMemoryEnrichmentResponse> RetryAsync(Guid organizationId, Guid employeeId, Guid jobId,
        Guid applicationUserId, RetryMemoryEnrichmentRequest request, CancellationToken cancellationToken = default);
}
