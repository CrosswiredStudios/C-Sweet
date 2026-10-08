using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReviewService : IAgentMemoryHoldService
{
    public async Task<MemoryHoldPreview> GetHoldAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, CancellationToken cancellationToken = default)
    {
        await RequireBackendAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var actor = await RequireHoldAuthorityAsync(organizationId, employeeId, applicationUserId, cancellationToken);
        await AcquireSuppressionBarrierAsync(cancellationToken);
        var source = await LockLegacyEpisodeAsync(episodeId, cancellationToken);
        var audienceHash = await RequireSourceOperatorAudienceAsync(organizationId, employeeId, actor, source.Episode, cancellationToken);
        var revision = await RevisionAsync(source.Episode.Partition, episodeId, cancellationToken, MemoryRecordKind.Episode);
        var retention = await ReadTransferRetentionAsync(organizationId, employeeId, applicationUserId, actor, source.Episode, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var content = retention.IsTransferred && retention.Blocker == "memory_transfer_retention_review_required"
            ? "Transferred evidence is unavailable until its retained source lineage can be verified." : source.Episode.Content;
        return new(episodeId, revision, SourceOperatorToken(HoldToken(revision, source.Payload, retention), audienceHash), content,
            source.Episode.LegalHold, retention.Blocker is null)
        { IsTransferred = retention.IsTransferred, UpstreamSources = retention.Sources,
            HeldUpstreamSources = retention.Held, ReleaseBlocker = retention.Blocker };
    }

    public async Task<ReviewMemoryHoldResponse> ReviewHoldAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, ReviewMemoryHoldRequest request, CancellationToken cancellationToken = default)
    {
        if (request.OperationId == Guid.Empty || request.ExpectedRevision <= 0 || request.EvidenceToken?.Length != 64)
            throw new ArgumentException("Invalid hold review.");
        await RequireBackendAsync(cancellationToken);
        await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, false, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var actor = await RequireHoldAuthorityAsync(organizationId, employeeId, applicationUserId, cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({organizationId.ToString("D") + ":" + request.OperationId.ToString("D")}, 0))", cancellationToken);
            await AcquireSuppressionBarrierAsync(cancellationToken);
            var source = await LockLegacyEpisodeAsync(episodeId, cancellationToken);
            var episode = source.Episode;
            var audienceHash = await RequireSourceOperatorAudienceAsync(organizationId, employeeId, actor, episode, cancellationToken);
            var retention = await ReadTransferRetentionAsync(organizationId, employeeId, applicationUserId, actor, episode, cancellationToken);
            var hash = Hash(new { organizationId, employeeId, episodeId, applicationUserId, actor, request });
            var action = request.LegalHold ? "place-hold" : "release-hold";
            var receipt = await db.MemoryReviewReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.OperationId == request.OperationId, cancellationToken);
            if (receipt is not null)
            {
                if (receipt.RecordKind != "Episode" || receipt.Action != action || receipt.RequestHash != hash) throw Changed();
                if (!request.LegalHold && retention.Blocker == "memory_transfer_retention_review_required")
                    throw new InvalidOperationException(retention.Blocker);
                return HoldResponse(receipt, true);
            }
            var revision = await RevisionAsync(episode.Partition, episodeId, cancellationToken, MemoryRecordKind.Episode);
            if (episode.LegalHold == request.LegalHold || revision != request.ExpectedRevision ||
                SourceOperatorToken(HoldToken(revision, source.Payload, retention), audienceHash) != request.EvidenceToken)
                throw Changed();
            // A copied hold cannot be released without resolving its upstream retention
            // obligations. Source release never implicitly releases independently copied holds.
            if (!request.LegalHold && retention.Blocker is not null)
                throw new InvalidOperationException(retention.Blocker);
            // Retention is independent of the immutable fingerprint. Keep all other raw
            // evidence and unknown legacy fields, and let the history trigger record it.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{legalHold}}',to_jsonb({request.LegalHold})) WHERE id={episodeId} AND partition_key={episode.Partition.StorageKey}", cancellationToken);
            receipt = new MemoryReviewReceipt { Id = Guid.NewGuid(), OrganizationId = organizationId, EmployeeId = employeeId,
                RecordKind = "Episode", MemoryId = episodeId, ResultMemoryId = episodeId, OperationId = request.OperationId,
                ActorApplicationUserId = applicationUserId, ActorOrganizationUserId = actor, RequestHash = hash, Action = action,
                PreviousRevision = revision, ResultRevision = await RevisionAsync(episode.Partition, episodeId, cancellationToken, MemoryRecordKind.Episode),
                CreatedAt = clock.GetUtcNow() };
            db.MemoryReviewReceipts.Add(receipt);
            db.QueueAudit(new AuditEventWriteRequest(request.LegalHold ? "memory.hold.placed.v1" : "memory.hold.released.v1", "Memory",
                OrganizationId: organizationId, EntityType: "MemoryEpisode", EntityId: episodeId,
                Summary: request.LegalHold ? "A human reviewer placed a retention hold on a memory source." : retention.IsTransferred
                    ? "A human reviewer released a copied retention hold after reviewing its upstream obligations."
                    : "A human reviewer released a retention hold on a memory source.",
                OccurredAt: receipt.CreatedAt, CorrelationId: request.OperationId.ToString("D"),
                Actor: new AuditActor("Human", ApplicationUserId: applicationUserId, OrganizationUserId: actor),
                EventId: receipt.Id, Employees: [new(employeeId, "Affected")], UseAmbientOrganization: false));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return HoldResponse(receipt, false);
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    private async Task<Guid> RequireHoldAuthorityAsync(Guid organizationId, Guid employeeId, Guid applicationUserId, CancellationToken token)
    {
        var actor = await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, true, token);
        // Retention decisions require the top-level human in the employee's hierarchy,
        // in addition to the usual audience restrictions (including private relationships).
        if (await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.Id == actor && x.ReportsToOrganizationUserId != null, token))
            throw new UnauthorizedAccessException();
        return actor;
    }

    private static ReviewMemoryHoldResponse HoldResponse(MemoryReviewReceipt receipt, bool replay) =>
        new(receipt.Id, receipt.MemoryId, receipt.ResultRevision, receipt.Action == "place-hold", receipt.CreatedAt, replay);

}
