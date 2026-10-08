using CSweet.Contracts.Memory;

namespace CSweet.Application.Core;

public interface IAgentMemoryReextractionService
{
    Task<MemoryReextractionCandidatePage> ListAsync(Guid organizationId, Guid employeeId, Guid applicationUserId,
        string? cursor = null, int limit = 20, CancellationToken token = default);
    Task<MemoryReextractionPreview> PreviewAsync(Guid organizationId, Guid employeeId, Guid jobId,
        Guid applicationUserId, CancellationToken token = default);
    Task<ReviewMemoryReextractionResponse> ReviewAsync(Guid organizationId, Guid employeeId, Guid jobId,
        Guid applicationUserId, ReviewMemoryReextractionRequest request, CancellationToken token = default);
}
