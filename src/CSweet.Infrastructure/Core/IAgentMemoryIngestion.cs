using CSweet.Memory;

namespace CSweet.Infrastructure.Core;

/// <summary>Only server-attributed, already authorized broker proposals enter this boundary.</summary>
public interface IAgentMemoryIngestion
{
    Task<MemoryWriteResult> AcceptProposalAsync(Guid organizationId, Guid employeeId, Guid installationId,
        MemoryEpisode episode, CancellationToken cancellationToken = default);
}
