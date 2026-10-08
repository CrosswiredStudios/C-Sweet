using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReviewService
{
    private sealed record CoreEvidence(IReadOnlyDictionary<Guid, MemoryEpisode> Sources, bool Valid,
        MemorySensitivity Sensitivity, long Revision, string Token);

    public async Task<MemoryCoreReviewResponse> GetCoreAsync(Guid organizationId, Guid employeeId, Guid blockId,
        Guid applicationUserId, CancellationToken cancellationToken = default)
    {
        await RequireBackendAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var actor = await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, true, cancellationToken);
        await MemoryReviewWriteBarrier.AcquireAsync(db, cancellationToken);
        await using var store = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
        var block = await LockCoreAsync(store, blockId, cancellationToken);
        await MemoryManagerAuthorization.RequirePartitionAsync(db, organizationId, employeeId, actor, block.Partition, cancellationToken);
        var evidence = await ReadCoreEvidenceAsync(store, block, cancellationToken);
        var sharedHash = await RequireSharedReviewSourcesAsync(organizationId, employeeId, applicationUserId, actor, evidence.Sources.Values, cancellationToken);
        evidence = evidence with { Token = SourceOperatorToken(evidence.Token, sharedHash) };
        await transaction.CommitAsync(cancellationToken);
        var current = block.UpdatedAt <= clock.GetUtcNow() && block.Revision is > 0 and < int.MaxValue && Enum.IsDefined(block.Confirmation);
        return new(block.Id, evidence.Revision, evidence.Token, block.Name, block.Content, block.Revision, block.IsPinned,
            block.Confirmation.ToString(), evidence.Sensitivity.ToString(), evidence.Valid && current,
            sharedHash is null && evidence.Valid && current && block.SourceEpisodeIds.Distinct().Count() < MemoryProvenance.MaximumSourceEpisodes,
            current, block.SourceEpisodeIds);
    }

    public async Task<ReviewMemoryCoreResponse> ReviewCoreAsync(Guid organizationId, Guid employeeId, Guid blockId,
        Guid applicationUserId, ReviewMemoryCoreRequest request, CancellationToken cancellationToken = default)
    {
        if (request.OperationId == Guid.Empty || request.ExpectedRevision <= 0 || request.EvidenceToken?.Length != 64 ||
            request.Action is not ("confirm" or "reject" or "correct") ||
            (request.Action == "correct" ? request.Correction is not { } correction || string.IsNullOrWhiteSpace(correction.Content) ||
                correction.Content.Length > 16000 : request.Correction is not null)) throw new ArgumentException("Invalid core review.");
        await RequireBackendAsync(cancellationToken);
        await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, false, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var actor = await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, true, cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({organizationId.ToString("D") + ":" + request.OperationId.ToString("D")}, 0))", cancellationToken);
            await MemoryReviewWriteBarrier.AcquireAsync(db, cancellationToken);
            await using var store = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
            var block = await LockCoreAsync(store, blockId, cancellationToken);
            await MemoryManagerAuthorization.RequirePartitionAsync(db, organizationId, employeeId, actor, block.Partition, cancellationToken);
            var evidence = await ReadCoreEvidenceAsync(store, block, cancellationToken);
            var sharedHash = await RequireSharedReviewSourcesAsync(organizationId, employeeId, applicationUserId, actor, evidence.Sources.Values, cancellationToken);
            evidence = evidence with { Token = SourceOperatorToken(evidence.Token, sharedHash) };
            var hash = Hash(new { organizationId, employeeId, blockId, applicationUserId, actor, request });
            var receipt = await db.MemoryReviewReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.OperationId == request.OperationId, cancellationToken);
            if (receipt is not null)
            {
                if (receipt.RecordKind != "Block" || receipt.RequestHash != hash) throw Changed();
                return CoreResponse(receipt, true);
            }

            if (evidence.Revision != request.ExpectedRevision || evidence.Token != request.EvidenceToken) throw Changed();
            var now = clock.GetUtcNow();
            if (block.UpdatedAt > now || block.Revision is <= 0 or int.MaxValue || !Enum.IsDefined(block.Confirmation) ||
                (request.Action != "reject" && !evidence.Valid)) throw new InvalidOperationException("memory_review_source_unavailable");
            if ((request.Action == "confirm" && block.Confirmation == MemoryConfirmationState.Confirmed) ||
                (request.Action == "reject" && block.Confirmation == MemoryConfirmationState.Rejected)) throw Changed();
            var updated = block with { Revision = block.Revision + 1, UpdatedAt = now, Confirmation = request.Action == "reject"
                ? MemoryConfirmationState.Rejected : MemoryConfirmationState.Confirmed };
            if (request.Action == "correct")
            {
                if (sharedHash is not null) throw new InvalidOperationException("memory_shared_correction_requires_restricted_source_lineage");
                var sourceId = Guid.NewGuid(); var contributors = block.SourceEpisodeIds.Append(sourceId).Distinct().Order().ToArray();
                MemoryProvenance.ValidateSourceEpisodes(contributors);
                var expiry = evidence.Sources.Values.Select(x => x.ExpiresAt).Where(x => x.HasValue).Min();
                await store.AppendEpisodeAsync(new(sourceId, block.Partition, InferScope(block.Partition), request.Correction!.Content, "text/plain",
                    new("user", request.OperationId.ToString("D"), actor.ToString("D")), Hash(request.Correction.Content), now, now,
                    ExpiresAt: expiry, LegalHold: evidence.Sources.Values.Any(x => x.LegalHold), Sensitivity: evidence.Sensitivity,
                    OperationalReferences: [new("memory-block", block.Id.ToString("D"), evidence.Revision.ToString())]), cancellationToken);
                updated = updated with { Content = request.Correction.Content, IsPinned = request.Correction.IsPinned,
                    Trust = MemoryTrustTier.ConfirmedUser, Sensitivity = evidence.Sensitivity, SourceEpisodeIds = contributors };
            }
            await using (var command = Command("UPDATE csweet_memory_blocks SET pinned=@pinned,payload=@payload WHERE id=@id AND partition_key=@partition"))
            {
                command.Parameters.AddWithValue("id", block.Id); command.Parameters.AddWithValue("partition", block.Partition.StorageKey);
                command.Parameters.AddWithValue("pinned", updated.IsPinned);
                command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(updated, JsonOptions));
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw Changed();
            }
            receipt = new MemoryReviewReceipt { Id = Guid.NewGuid(), OrganizationId = organizationId, EmployeeId = employeeId,
                RecordKind = "Block", MemoryId = blockId, ResultMemoryId = blockId, OperationId = request.OperationId,
                ActorApplicationUserId = applicationUserId, ActorOrganizationUserId = actor, RequestHash = hash, Action = request.Action,
                PreviousRevision = evidence.Revision, ResultRevision = await RevisionAsync(block.Partition, blockId, cancellationToken, MemoryRecordKind.Block), CreatedAt = now };
            db.MemoryReviewReceipts.Add(receipt);
            db.QueueAudit(new AuditEventWriteRequest("memory.core.reviewed.v1", "Memory", OrganizationId: organizationId,
                EntityType: "MemoryBlock", EntityId: blockId, Summary: "A human reviewer changed a core memory block.",
                MetadataJson: JsonSerializer.Serialize(new { receipt.Action, receipt.PreviousRevision, receipt.ResultRevision }),
                OccurredAt: now, CorrelationId: request.OperationId.ToString("D"),
                Actor: new AuditActor("Human", ApplicationUserId: applicationUserId, OrganizationUserId: actor),
                EventId: receipt.Id, Employees: [new(employeeId, "Affected")], UseAmbientOrganization: false));
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            return CoreResponse(receipt, false);
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    private async Task<MemoryBlock> LockCoreAsync(PostgreSqlMemoryStore store, Guid id, CancellationToken token)
    {
        await using var command = Command("""
            SELECT partition_key,payload::text FROM csweet_memory_blocks b WHERE id=@id AND partition_key LIKE 'mp2:%'
                AND NOT EXISTS(SELECT 1 FROM csweet_memory_partition_migration_rows q WHERE q.table_name='csweet_memory_blocks'
                    AND q.record_id=b.id::text AND q.disposition='Quarantine') FOR UPDATE
            """);
        command.Parameters.AddWithValue("id", id); MemoryBlock block; string partitionKey;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) throw new KeyNotFoundException();
            partitionKey = reader.GetString(0); block = JsonSerializer.Deserialize<MemoryBlock>(reader.GetString(1), JsonOptions) ?? throw new KeyNotFoundException();
        }
        if (block.Id != id || block.Partition.StorageKey != partitionKey ||
            !(await store.ReadRevisionsAsync(block.Partition, MemoryRecordKind.Block, id, limit: 1, cancellationToken: token)).Items.Any())
            throw new InvalidOperationException("memory_review_source_unavailable");
        return block;
    }

    private async Task<CoreEvidence> ReadCoreEvidenceAsync(PostgreSqlMemoryStore store, MemoryBlock block, CancellationToken token)
    {
        if (!MemoryProvenance.HasBoundedSources(block.SourceEpisodeIds)) throw new InvalidOperationException("memory_review_source_unavailable");
        var sources = new Dictionary<Guid, MemoryEpisode>();
        foreach (var id in block.SourceEpisodeIds.Distinct().Order())
            if (await store.GetEpisodeAsync(block.Partition, id, token) is { } source) sources[id] = source;
        var valid = sources.Count > 0 && sources.Count == block.SourceEpisodeIds.Distinct().Count() &&
            sources.All(x => MemoryProvenance.IsCurrent(x.Value, block.Partition, x.Key, clock.GetUtcNow())) &&
            Enum.IsDefined(block.Sensitivity) && Enum.IsDefined(block.Trust) && Enum.IsDefined(block.Confirmation) && block.UpdatedAt <= clock.GetUtcNow();
        var sensitivity = sources.Count == block.SourceEpisodeIds.Distinct().Count()
            ? MemoryProvenance.Maximum(sources.Values.Select(x => x.Sensitivity).Append(block.Sensitivity).ToArray()) : MemorySensitivity.Restricted;
        var revision = await RevisionAsync(block.Partition, block.Id, token, MemoryRecordKind.Block);
        return new(sources, valid, sensitivity, revision, Hash(new { block, sources = sources.Values.ToArray(), revision, valid }));
    }
    private static ReviewMemoryCoreResponse CoreResponse(MemoryReviewReceipt receipt, bool replay) =>
        new(receipt.Id, receipt.MemoryId, receipt.ResultRevision, receipt.Action, receipt.CreatedAt, replay);
}
