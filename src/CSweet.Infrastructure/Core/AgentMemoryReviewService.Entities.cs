using System.Text.Json;
using CSweet.Contracts.Memory;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReviewService
{
    private sealed record CorrectionTarget(MemoryEntity Entity, IReadOnlyDictionary<Guid, MemoryEpisode> Sources,
        MemorySensitivity Sensitivity, long Revision, string Token);

    public async Task<IReadOnlyList<MemoryClaimCorrectionTarget>> FindClaimCorrectionTargetsAsync(Guid organizationId,
        Guid employeeId, Guid claimId, Guid applicationUserId, string search, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(search) || search.Trim().Length < 2 || search.Length > 200)
            throw new ArgumentException("Enter between 2 and 200 characters.");
        await RequireBackendAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var actor = await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, true, cancellationToken);
        await MemoryReviewWriteBarrier.AcquireAsync(db, cancellationToken);
        await using var store = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
        var claim = await LockClaimAsync(store, claimId, cancellationToken);
        await MemoryManagerAuthorization.RequirePartitionAsync(db, organizationId, employeeId, actor, claim.Partition, cancellationToken);
        var evidence = await ReadEvidenceAsync(store, claim, cancellationToken);
        if (claim.ObjectEntityId is null || !evidence.Valid) return [];
        var ids = new List<Guid>();
        await using (var command = Command("""
            SELECT id FROM csweet_memory_entities e WHERE partition_key=@partition AND id<>@current
                AND strpos(lower(canonical_name),lower(@search))>0
                AND NOT EXISTS(SELECT 1 FROM csweet_memory_partition_migration_rows q WHERE q.table_name='csweet_memory_entities'
                    AND q.record_id=e.id::text AND q.disposition='Quarantine')
            ORDER BY (lower(canonical_name)=lower(@search)) DESC, lower(canonical_name),id LIMIT 20
            """))
        {
            command.Parameters.AddWithValue("partition", claim.Partition.StorageKey);
            command.Parameters.AddWithValue("current", claim.ObjectEntityId.Value);
            command.Parameters.AddWithValue("search", search.Trim());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0));
        }
        var results = new List<MemoryClaimCorrectionTarget>();
        foreach (var id in ids)
        {
            var target = await ReadCorrectionTargetAsync(store, claim, id, cancellationToken);
            if (target is null || evidence.SourceIds.Concat(target.Entity.SourceEpisodeIds).Distinct().Count() > MemoryProvenance.MaximumSourceEpisodes) continue;
            results.Add(new(id, target.Entity.CanonicalName, target.Entity.Type, target.Sensitivity.ToString(), target.Token, target.Entity.SourceEpisodeIds));
        }
        await transaction.CommitAsync(cancellationToken);
        return results;
    }

    private async Task<CorrectionTarget?> ReadCorrectionTargetAsync(PostgreSqlMemoryStore store, MemoryClaim claim, Guid id, CancellationToken token)
    {
        if (id == claim.ObjectEntityId) return null;
        await using var command = Command("""
            SELECT payload::text FROM csweet_memory_entities e WHERE id=@id AND partition_key=@partition
                AND NOT EXISTS(SELECT 1 FROM csweet_memory_partition_migration_rows q WHERE q.table_name='csweet_memory_entities'
                    AND q.record_id=e.id::text AND q.disposition='Quarantine')
            """);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("partition", claim.Partition.StorageKey);
        var entity = JsonSerializer.Deserialize<MemoryEntity>((string?)await command.ExecuteScalarAsync(token) ?? "null", JsonOptions);
        if (entity is null || entity.Id != id || entity.Partition != claim.Partition || !Enum.IsDefined(entity.Sensitivity) ||
            !MemoryProvenance.HasBoundedSources(entity.SourceEpisodeIds) || entity.SourceEpisodeIds.Count == 0) return null;
        var sources = new Dictionary<Guid, MemoryEpisode>();
        foreach (var sourceId in entity.SourceEpisodeIds.Distinct().Order())
        {
            var source = await store.GetEpisodeAsync(claim.Partition, sourceId, token);
            if (!MemoryProvenance.IsCurrent(source, claim.Partition, sourceId, clock.GetUtcNow())) return null;
            sources.Add(sourceId, source!);
        }
        var sensitivity = MemoryProvenance.Maximum(sources.Values.Select(x => x.Sensitivity).Append(entity.Sensitivity).ToArray());
        var revision = await RevisionAsync(claim.Partition, id, token, MemoryRecordKind.Entity);
        // Bind the choice to this claim and the exact target revision plus current source policy/evidence.
        return new(entity, sources, sensitivity, revision, Hash(new { claimId = claim.Id, entity, revision, sources = sources.Values.ToArray() }));
    }
}
