using CSweet.Application.Core;
using CSweet.Contracts.Memory;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReviewService : IAgentMemoryErasureImpactService
{
    public async Task<MemoryErasureImpactResponse> GetErasureImpactAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, CancellationToken cancellationToken = default)
    {
        if (episodeId == Guid.Empty) throw new ArgumentException("Invalid memory source.");
        await RequireBackendAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var actor = await RequireHoldAuthorityAsync(organizationId, employeeId, applicationUserId, cancellationToken);
        using var work = new MemoryWorkErasure(db, diagnosticProtection: protection);
        var capture = new MemoryCaptureErasure(db);
        await AcquireErasureBarriersAsync(work, capture, cancellationToken);
        await using var store = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
        var prepared = await PrepareErasureAsync(organizationId, employeeId, applicationUserId, actor, episodeId, store, work, capture, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return prepared.Impact;
    }
}
