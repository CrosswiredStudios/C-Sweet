using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReviewService : IAgentMemorySuppressionService
{
    public async Task<MemorySuppressionPreview> GetSuppressionAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, CancellationToken cancellationToken = default)
    {
        await RequireBackendAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var actor = await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, true, cancellationToken);
        await AcquireSuppressionBarrierAsync(cancellationToken);
        var source = await LockLegacyEpisodeAsync(episodeId, cancellationToken);
        var audienceHash = await RequireSourceOperatorAudienceAsync(organizationId, employeeId, actor, source.Episode, cancellationToken);
        var revision = await RevisionAsync(source.Episode.Partition, episodeId, cancellationToken, MemoryRecordKind.Episode);
        await transaction.CommitAsync(cancellationToken);
        return new(episodeId, revision, SourceOperatorToken(Hash(new { revision, source.Payload }), audienceHash), source.Episode.Content,
            source.Episode.IsSuppressed, source.Episode.LegalHold);
    }

    public async Task<SuppressMemorySourceResponse> SuppressSourceAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, SuppressMemorySourceRequest request, CancellationToken cancellationToken = default)
    {
        if (request.OperationId == Guid.Empty || request.ExpectedRevision <= 0 || request.EvidenceToken?.Length != 64)
            throw new ArgumentException("Invalid suppression review.");
        await RequireBackendAsync(cancellationToken);
        await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, false, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var actor = await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, true, cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({organizationId.ToString("D") + ":" + request.OperationId.ToString("D")}, 0))", cancellationToken);
            await AcquireSuppressionBarrierAsync(cancellationToken);
            var source = await LockLegacyEpisodeAsync(episodeId, cancellationToken);
            var episode = source.Episode;
            var audienceHash = await RequireSourceOperatorAudienceAsync(organizationId, employeeId, actor, episode, cancellationToken);
            var hash = Hash(new { organizationId, employeeId, episodeId, applicationUserId, actor, request });
            var receipt = await db.MemoryReviewReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.OperationId == request.OperationId, cancellationToken);
            if (receipt is not null)
            {
                if (receipt.RecordKind != "Episode" || receipt.Action != "suppress" || receipt.RequestHash != hash) throw Changed();
                return SuppressionResponse(receipt, true);
            }
            var revision = await RevisionAsync(episode.Partition, episodeId, cancellationToken, MemoryRecordKind.Episode);
            if (episode.IsSuppressed || revision != request.ExpectedRevision ||
                SourceOperatorToken(Hash(new { revision, source.Payload }), audienceHash) != request.EvidenceToken) throw Changed();
            await using var store = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
            await store.SuppressEpisodeAsync(episode.Partition, episodeId, cancellationToken);
            receipt = new MemoryReviewReceipt { Id = Guid.NewGuid(), OrganizationId = organizationId, EmployeeId = employeeId,
                RecordKind = "Episode", MemoryId = episodeId, ResultMemoryId = episodeId, OperationId = request.OperationId,
                ActorApplicationUserId = applicationUserId, ActorOrganizationUserId = actor, RequestHash = hash, Action = "suppress",
                PreviousRevision = revision, ResultRevision = await RevisionAsync(episode.Partition, episodeId, cancellationToken, MemoryRecordKind.Episode),
                CreatedAt = clock.GetUtcNow() };
            db.MemoryReviewReceipts.Add(receipt);
            db.QueueAudit(new AuditEventWriteRequest("memory.source.suppressed.v1", "Memory", OrganizationId: organizationId,
                EntityType: "MemoryEpisode", EntityId: episodeId, Summary: "A human reviewer suppressed a memory source and its dependent recall.",
                OccurredAt: receipt.CreatedAt, CorrelationId: request.OperationId.ToString("D"),
                Actor: new AuditActor("Human", ApplicationUserId: applicationUserId, OrganizationUserId: actor),
                EventId: receipt.Id, Employees: [new(employeeId, "Affected")], UseAmbientOrganization: false));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return SuppressionResponse(receipt, false);
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    private async Task AcquireSuppressionBarrierAsync(CancellationToken token)
    {
        // Enrichment holds source row locks before writing derivatives. Wait for it
        // before acquiring the other memory-table barriers, preserving lock order.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE csweet_memory_episodes IN EXCLUSIVE MODE", token);
        await MemoryReviewWriteBarrier.AcquireAsync(db, token);
    }

    private static SuppressMemorySourceResponse SuppressionResponse(MemoryReviewReceipt receipt, bool replay) =>
        new(receipt.Id, receipt.MemoryId, receipt.ResultRevision, receipt.CreatedAt, replay);
}
