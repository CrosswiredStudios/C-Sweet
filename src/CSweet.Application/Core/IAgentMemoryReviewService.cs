using CSweet.Contracts.Memory;

namespace CSweet.Application.Core;

public interface IAgentMemoryReviewService
{
    Task<MemoryLegacyReviewResponse> GetLegacyEpisodeAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, CancellationToken cancellationToken = default);
    Task<ReviewMemoryLegacyResponse> ReviewLegacyEpisodeAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, ReviewMemoryLegacyRequest request, CancellationToken cancellationToken = default);
    Task<MemoryCoreReviewResponse> GetCoreAsync(Guid organizationId, Guid employeeId, Guid blockId,
        Guid applicationUserId, CancellationToken cancellationToken = default);
    Task<ReviewMemoryCoreResponse> ReviewCoreAsync(Guid organizationId, Guid employeeId, Guid blockId,
        Guid applicationUserId, ReviewMemoryCoreRequest request, CancellationToken cancellationToken = default);
    Task<MemoryHistoryPage> ReadHistoryAsync(Guid organizationId, Guid employeeId, string kind, Guid recordId,
        Guid applicationUserId, long afterRevision = 0, int limit = 20, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryClaimCorrectionTarget>> FindClaimCorrectionTargetsAsync(Guid organizationId, Guid employeeId, Guid claimId,
        Guid applicationUserId, string search, CancellationToken cancellationToken = default);
    Task<MemoryProcedureReviewResponse> GetProcedureAsync(Guid organizationId, Guid employeeId, Guid procedureId,
        Guid applicationUserId, CancellationToken cancellationToken = default);
    Task<ReviewMemoryProcedureResponse> ReviewProcedureAsync(Guid organizationId, Guid employeeId, Guid procedureId,
        Guid applicationUserId, ReviewMemoryProcedureRequest request, CancellationToken cancellationToken = default);
    Task<MemoryClaimReviewResponse> GetClaimAsync(Guid organizationId, Guid employeeId, Guid claimId,
        Guid applicationUserId, CancellationToken cancellationToken = default);
    Task<ReviewMemoryClaimResponse> ReviewClaimAsync(Guid organizationId, Guid employeeId, Guid claimId,
        Guid applicationUserId, ReviewMemoryClaimRequest request, CancellationToken cancellationToken = default);
}
