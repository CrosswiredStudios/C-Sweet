using System.Text;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryService
{
    internal const string PreserveExistingPolicy = "preserve-existing-v1";
    internal sealed record PreservedClaim(Guid Subject, string Predicate);
    internal sealed record PreservedEdge(Guid From, string Relationship, Guid To);
    internal sealed record EpisodeReconciliation(int Version, string Policy, string InventoryHash, int RecordCount,
        PreservedClaim[] Claims, PreservedEdge[] Edges, string[] Procedures, Guid[] SourceEpisodeIds);
    internal sealed class ReconciliationRow
    {
        public Guid Id { get; set; }
        public int Kind { get; set; }
        public string? Payload { get; set; }
        public long Revision { get; set; }
        public bool Quarantined { get; set; }
    }

    private static bool ValidReconciliation(EpisodeReconciliation? value) => value is
        { Version: 1, Policy: PreserveExistingPolicy, InventoryHash.Length: 64, RecordCount: > 0 and <= 256,
          Claims.Length: <= 256, Edges.Length: <= 256, Procedures.Length: <= 256, SourceEpisodeIds.Length: > 0 and <= 128 } &&
        !value.SourceEpisodeIds.Contains(Guid.Empty) &&
        value.Claims.All(x => x is not null && x.Subject != Guid.Empty && !string.IsNullOrWhiteSpace(x.Predicate)) &&
        value.Edges.All(x => x is not null && x.From != Guid.Empty && x.To != Guid.Empty && !string.IsNullOrWhiteSpace(x.Relationship)) &&
        value.Procedures.All(x => !string.IsNullOrWhiteSpace(x));

    // Caller owns the memory writer barrier. Include retained/rejected/expired records,
    // reference entities and linked embeddings, rather than inspecting a recall export.
    internal async Task<EpisodeReconciliation?> ReadEpisodeReconciliationAsync(MemoryEpisode episode, CancellationToken token)
    {
        try { return await ReadEpisodeReconciliationCoreAsync(episode, token); }
        catch (Exception error) when (error is JsonException or FormatException)
        { throw new InvalidOperationException("memory_ingestion_legacy_output_unavailable", error); }
    }

    private async Task<EpisodeReconciliation?> ReadEpisodeReconciliationCoreAsync(MemoryEpisode episode, CancellationToken token)
    {
        var partition = episode.Partition.StorageKey; var id = episode.Id; var key = id.ToString("D");
        var rows = await db.Database.SqlQuery<ReconciliationRow>($"""
            WITH linked AS MATERIALIZED (
              SELECT 1 AS kind,id,payload FROM csweet_memory_entities WHERE partition_key={partition} AND payload->'sourceEpisodeIds' ? {key}
              UNION ALL SELECT 2,id,payload FROM csweet_memory_claims WHERE partition_key={partition} AND (episode_id={id} OR payload->'sourceEpisodeIds' ? {key})
              UNION ALL SELECT 3,id,payload FROM csweet_memory_edges WHERE partition_key={partition} AND (episode_id={id} OR payload->'sourceEpisodeIds' ? {key})
              UNION ALL SELECT 4,id,payload FROM csweet_memory_blocks WHERE partition_key={partition} AND payload->'sourceEpisodeIds' ? {key}
              UNION ALL SELECT 5,id,payload FROM csweet_memory_procedures WHERE partition_key={partition} AND (episode_id={id} OR payload->'sourceEpisodeIds' ? {key})
              LIMIT 257
            ), base_records AS MATERIALIZED (
              SELECT * FROM linked
              UNION SELECT 1,e.id,e.payload FROM csweet_memory_entities e WHERE e.partition_key={partition} AND EXISTS (
                SELECT 1 FROM linked l WHERE e.id::text IN (l.payload->>'subjectEntityId',l.payload->>'objectEntityId',l.payload->>'fromEntityId',l.payload->>'toEntityId'))
            ), records AS (
              SELECT * FROM base_records
              UNION SELECT 6,e.id,e.payload FROM csweet_memory_embeddings e WHERE e.partition_key={partition} AND
                (e.memory_id={id} OR EXISTS(SELECT 1 FROM base_records l WHERE l.id=e.memory_id))
            )
            SELECT a.id AS "Id",a.kind AS "Kind",
              CASE WHEN octet_length(a.payload::text)<=65536 THEN a.payload::text ELSE NULL END AS "Payload",
              COALESCE((SELECT MAX(r.revision) FROM csweet_memory_revisions r WHERE r.partition_key={partition} AND r.record_id=a.id AND r.kind=a.kind),0) AS "Revision",
              EXISTS(SELECT 1 FROM csweet_memory_partition_migration_rows q WHERE q.record_id=a.id::text AND q.disposition='Quarantine') AS "Quarantined"
            FROM records a ORDER BY a.kind,a.id LIMIT 257
            """).ToListAsync(token);
        if (rows.Count == 0) return null;
        if (!await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid='"MemoryEpisodeEnrichmentJobs"'::regclass AND
              tgname='csweet_episode_reconciliation_guard' AND tgfoid=to_regprocedure('csweet_episode_reconciliation_guard()') AND
              NOT tgisinternal AND tgenabled IN ('O','A')) AS "Value"
            """).SingleAsync(token)) throw new InvalidOperationException("memory_ingestion_legacy_output_unavailable");
        if (rows.Count > 256 || rows.Any(x => x.Payload is null || x.Revision <= 0 || x.Quarantined) ||
            rows.Sum(x => Encoding.UTF8.GetByteCount(x.Payload!)) > 8 * 1024 * 1024)
            throw new InvalidOperationException("memory_ingestion_legacy_output_unavailable");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var claims = new List<PreservedClaim>(); var edges = new List<PreservedEdge>(); var procedures = new List<string>();
        var sources = new HashSet<Guid> { episode.Id }; var references = new HashSet<Guid>(); var entities = new HashSet<Guid>();
        foreach (var row in rows)
        {
            using var document = JsonDocument.Parse(row.Payload!); var payload = document.RootElement;
            if (!payload.TryGetProperty("id", out var identity) || identity.ValueKind != JsonValueKind.String || identity.GetGuid() != row.Id ||
                !payload.TryGetProperty("partition", out var location) || location.Deserialize<MemoryPartition>(options) != episode.Partition)
                throw new InvalidOperationException("memory_ingestion_legacy_output_unavailable");
            if (row.Kind is 1 or 2 or 4 && (!payload.TryGetProperty("sensitivity", out var classification) ||
                !classification.TryGetInt32(out var sensitivity) || !Enum.IsDefined((MemorySensitivity)sensitivity)))
                throw new InvalidOperationException("memory_ingestion_legacy_output_unavailable");
            if (row.Kind != 6)
            {
                if (!payload.TryGetProperty("sourceEpisodeIds", out var lineage) || lineage.ValueKind != JsonValueKind.Array ||
                    lineage.GetArrayLength() > MemoryProvenance.MaximumSourceEpisodes)
                    throw new InvalidOperationException("memory_ingestion_legacy_output_unavailable");
                foreach (var value in lineage.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.String) throw new InvalidOperationException("memory_ingestion_legacy_output_unavailable");
                    sources.Add(value.GetGuid());
                }
            }
            switch (row.Kind)
            {
                case 1:
                    var entity = payload.Deserialize<MemoryEntity>(options)!;
                    if (string.IsNullOrWhiteSpace(entity.CanonicalName) || !entity.IsProtectedType && entity.SourceEpisodeIds.Count == 0)
                        throw new InvalidOperationException("memory_ingestion_legacy_output_unavailable");
                    entities.Add(entity.Id); break;
                case 2:
                    var claim = payload.Deserialize<MemoryClaim>(options)!;
                    if (!Enum.IsDefined(claim.Confirmation) || !Enum.IsDefined(claim.Trust) || string.IsNullOrWhiteSpace(claim.Predicate))
                        throw new InvalidOperationException("memory_ingestion_legacy_output_unavailable");
                    sources.Add(claim.EpisodeId); references.Add(claim.SubjectEntityId);
                    if (claim.ObjectEntityId.HasValue) references.Add(claim.ObjectEntityId.Value);
                    claims.Add(new(claim.SubjectEntityId, claim.Predicate)); break;
                case 3:
                    var edge = payload.Deserialize<MemoryEdge>(options)!;
                    if (!Enum.IsDefined(edge.Trust) || string.IsNullOrWhiteSpace(edge.Relationship)) throw new InvalidOperationException("memory_ingestion_legacy_output_unavailable");
                    sources.Add(edge.EpisodeId); references.Add(edge.FromEntityId); references.Add(edge.ToEntityId);
                    edges.Add(new(edge.FromEntityId, edge.Relationship, edge.ToEntityId)); break;
                case 4:
                    var block = payload.Deserialize<MemoryBlock>(options)!;
                    if (!Enum.IsDefined(block.Trust) || !Enum.IsDefined(block.Confirmation)) throw new InvalidOperationException("memory_ingestion_legacy_output_unavailable");
                    break;
                case 5:
                    var procedure = payload.Deserialize<ProceduralMemory>(options)!;
                    if (!Enum.IsDefined(procedure.Trust) || !Enum.IsDefined(procedure.Confirmation) || string.IsNullOrWhiteSpace(procedure.Name))
                        throw new InvalidOperationException("memory_ingestion_legacy_output_unavailable");
                    sources.Add(procedure.EpisodeId); procedures.Add(procedure.Name); break;
                case 6:
                    var embedding = payload.Deserialize<MemoryEmbedding>(options)!;
                    var targets = rows.Where(x => x.Id == embedding.MemoryId && x.Kind != 6).ToArray();
                    if (targets.Length > 1) throw new InvalidOperationException("memory_ingestion_legacy_output_unavailable");
                    var targetKind = embedding.MemoryId == episode.Id ? 0 : targets.SingleOrDefault()?.Kind;
                    if (!(targetKind == 0 && embedding.Layer == MemoryLayer.Episodic || targetKind is 1 or 2 && embedding.Layer == MemoryLayer.Semantic ||
                        targetKind == 4 && embedding.Layer == MemoryLayer.Core || targetKind == 5 && embedding.Layer == MemoryLayer.Procedural) ||
                        embedding.Vector is null || embedding.Vector.Count > 65536 ||
                        embedding.Vector.Any(x => !float.IsFinite(x))) throw new InvalidOperationException("memory_ingestion_legacy_output_unavailable");
                    break;
                default: throw new InvalidOperationException("memory_ingestion_legacy_output_unavailable");
            }
        }
        if (references.Any(x => !entities.Contains(x)) || sources.Contains(Guid.Empty) || sources.Count > 128)
            throw new InvalidOperationException("memory_ingestion_legacy_output_unavailable");
        await using var retained = new PostgreSqlMemoryStore((NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());
        foreach (var sourceId in sources)
        {
            var source = await ((IMemorySourceReader)retained).GetEpisodeAsync(episode.Partition, sourceId, token);
            if (!MemoryProvenance.IsCurrent(source, episode.Partition, sourceId, DateTimeOffset.UtcNow))
                throw new InvalidOperationException("memory_ingestion_legacy_output_unavailable");
        }
        var inventory = SourceChecksum(JsonSerializer.Serialize(rows.Select(x => new { x.Kind, x.Id, x.Revision, Hash = SourceChecksum(x.Payload!) })));
        var plan = new EpisodeReconciliation(1, PreserveExistingPolicy, inventory, rows.Count, claims.Distinct().ToArray(), edges.Distinct().ToArray(),
            procedures.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), sources.OrderBy(x => x).ToArray());
        if (!ValidReconciliation(plan)) throw new InvalidOperationException("memory_ingestion_legacy_output_unavailable");
        return plan;
    }

    internal static EpisodeReconciliation? RecoveryReconciliation(MemoryEpisodeEnrichmentJob job) =>
        JsonSerializer.Deserialize<EpisodeJobSource>(job.SourceJson)?.Reconciliation;

    internal static void BindRecoveryReconciliation(MemoryEpisodeEnrichmentJob job, EpisodeReconciliation? plan)
    {
        var source = JsonSerializer.Deserialize<EpisodeJobSource>(job.SourceJson) ?? throw new InvalidOperationException();
        job.SourceJson = JsonSerializer.Serialize(source with { Reconciliation = plan }); job.SourceHash = SourceChecksum(job.SourceJson);
    }
}
