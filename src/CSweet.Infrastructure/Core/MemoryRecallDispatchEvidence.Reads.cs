using System.Text.Json;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.Infrastructure.Core;

public sealed record CapturedMemoryRead(string EvidenceJson, IReadOnlyList<MemoryPartition> Partitions);

public sealed partial class MemoryRecallDispatchEvidence
{
    private sealed record Package(Guid Id, string Hash);
    private sealed record ReadReceipt(int Version, Root[] Roots, Record[] Records, Package[] Packages, MemoryPartition[] Partitions);

    public async Task<CapturedMemoryRead?> CaptureReadAsync(object? result, MemoryPartition? searchPartition, CancellationToken token)
    {
        if (result is null) return null;
        var roots = new List<Root>(); var packages = new List<Package>(); var partitions = new HashSet<MemoryPartition>();
        void Add(MemoryPartition partition, MemoryRecordKind kind, Guid id, object record)
        {
            partitions.Add(partition);
            roots.Add(new(partition, kind, id, Hash(JsonSerializer.Serialize(record, record.GetType(), Json)), [], "record"));
        }
        async Task Visit(object value)
        {
            if (roots.Count > 512) throw Denied();
            switch (value)
            {
                case MemoryCandidate candidate:
                    if (searchPartition is null) throw Denied();
                    partitions.Add(searchPartition);
                    roots.Add(new(searchPartition, await ReadKindAsync(searchPartition, candidate.Id, candidate.Layer, token), candidate.Id,
                        Hash(candidate.Content), candidate.EpisodeIds.Distinct().Order().ToArray())); break;
                case MemoryEpisode x: Add(x.Partition, MemoryRecordKind.Episode, x.Id, x); break;
                case MemoryEntity x: Add(x.Partition, MemoryRecordKind.Entity, x.Id, x); break;
                case MemoryClaim x: Add(x.Partition, MemoryRecordKind.Claim, x.Id, x); break;
                case MemoryEdge x: Add(x.Partition, MemoryRecordKind.Edge, x.Id, x); break;
                case MemoryBlock x: Add(x.Partition, MemoryRecordKind.Block, x.Id, x); break;
                case ProceduralMemory x: Add(x.Partition, MemoryRecordKind.Procedure, x.Id, x); break;
                case MemoryEmbedding x: Add(x.Partition, MemoryRecordKind.Embedding, x.Id, x); break;
                case MemoryExport export:
                    foreach (var x in export.Episodes.Cast<object>().Concat(export.Entities).Concat(export.Claims).Concat(export.Edges)
                        .Concat(export.Blocks).Concat(export.Procedures).Concat(export.Embeddings ?? [])) await Visit(x);
                    break;
                case KnowledgeTransferPackage package:
                    if (package.Items.Count > 32 || package.SourceNamespaces.Count > 16) throw Denied();
                    var raw = await ReadPackageAsync(package.Id, token);
                    var original = JsonSerializer.Deserialize<KnowledgeTransferPackage>(raw, Json) ?? throw Denied();
                    if (JsonSerializer.Serialize(package with { Items = original.Items, DebriefSensitivity = original.DebriefSensitivity }, Json) !=
                        JsonSerializer.Serialize(original, Json)) throw Denied();
                    packages.Add(new(package.Id, Hash(raw)));
                    partitions.Add(package.TargetNamespace.Partition);
                    foreach (var ns in package.SourceNamespaces) partitions.Add(ns.Partition);
                    foreach (var item in package.Items)
                    {
                        partitions.Add(item.SourcePartition);
                        roots.Add(new(item.SourcePartition, await ReadKindAsync(item.SourcePartition, item.MemoryId, item.Layer, token), item.MemoryId,
                            Hash(JsonSerializer.Serialize(item, Json)), item.EpisodeIds.Distinct().Order().ToArray(), "transfer", item.Sensitivity));
                    }
                    break;
                case IEnumerable<MemoryCandidate> values: foreach (var x in values) await Visit(x); break;
                case IEnumerable<MemoryClaim> values: foreach (var x in values) await Visit(x); break;
                default: throw Denied();
            }
        }
        if (!db.Database.IsNpgsql()) throw Denied();
        await Visit(result);
        if (roots.Count == 0 && packages.Count == 0) return null;
        var selected = roots.DistinctBy(x => (x.Partition, x.Kind, x.Id, x.Format)).ToArray();
        var records = selected.Length == 0 ? [] : await ReadEvidenceAsync(selected, token, 512);
        partitions.UnionWith(SharedAudienceClosure(records));
        // Package state must not change while its returned source closure is being captured.
        foreach (var package in packages) if (Hash(await ReadPackageAsync(package.Id, token)) != package.Hash) throw Denied();
        var audiences = partitions.OrderBy(x => x.StorageKey, StringComparer.Ordinal).ToArray();
        if (audiences.Length > 32) throw Denied();
        var evidence = JsonSerializer.Serialize(new ReadReceipt(1, selected, records, packages.ToArray(), audiences), Json);
        if (evidence.Length > 262144) throw Denied();
        return new(evidence, audiences);
    }

    public async Task<IReadOnlyList<MemoryPartition>> ValidateReadAsync(string evidenceJson, CancellationToken token,
        bool preserveInfrastructureFailure = false)
    {
        try
        {
            if (evidenceJson.Length > 262144) throw Denied("read.receipt-size");
            var receipt = JsonSerializer.Deserialize<ReadReceipt>(evidenceJson, Json) ?? throw Denied("read.receipt-empty");
            if (receipt.Version != 1 || receipt.Roots.Length > 512 || receipt.Records.Length > 512 || receipt.Packages.Length > 1 || receipt.Partitions.Length > 32)
                throw Denied("read.receipt-shape");
            var current = receipt.Roots.Length == 0 ? [] : await ReadEvidenceAsync(receipt.Roots, token, 512);
            if (!current.SequenceEqual(receipt.Records)) throw Denied("read.source-closure-changed");
            if (SharedAudienceClosure(current).Any(x => !receipt.Partitions.Contains(x))) throw Denied("read.shared-audience-changed");
            foreach (var package in receipt.Packages) if (Hash(await ReadPackageAsync(package.Id, token)) != package.Hash) throw Denied("read.package-changed");
            return receipt.Partitions;
        }
        catch (Exception e) when (e is not OperationCanceledException && e is not CSweet.Infrastructure.Llm.ProviderDispatchDeniedException &&
            (!preserveInfrastructureFailure || e is JsonException or FormatException or NullReferenceException or KeyNotFoundException or ArgumentException))
        { throw Denied(); }
    }

    private async Task<MemoryRecordKind> ReadKindAsync(MemoryPartition partition, Guid id, MemoryLayer layer, CancellationToken token)
    {
        if (layer != MemoryLayer.Semantic) return layer switch
        {
            MemoryLayer.Episodic => MemoryRecordKind.Episode, MemoryLayer.Core => MemoryRecordKind.Block,
            MemoryLayer.Procedural => MemoryRecordKind.Procedure, _ => throw Denied()
        };
        if (db.Database.GetDbConnection().State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT 2 FROM csweet_memory_claims WHERE partition_key=@partition AND id=@id
            UNION ALL SELECT 3 FROM csweet_memory_edges WHERE partition_key=@partition AND id=@id
            """, (NpgsqlConnection)db.Database.GetDbConnection(), db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction);
        command.Parameters.AddWithValue("partition", partition.StorageKey); command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw Denied();
        var kind = (MemoryRecordKind)reader.GetInt32(0);
        if (await reader.ReadAsync(token)) throw Denied();
        return kind;
    }

    private async Task<string> ReadPackageAsync(Guid id, CancellationToken token)
    {
        if (db.Database.GetDbConnection().State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT payload::text FROM csweet_memory_transfers WHERE id=@id AND NOT EXISTS
                (SELECT 1 FROM csweet_memory_partition_migration_rows WHERE table_name='csweet_memory_transfers'
                    AND record_id=@id::text AND disposition='Quarantine')
            """, (NpgsqlConnection)db.Database.GetDbConnection(), db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction);
        command.Parameters.AddWithValue("id", id);
        return (string?)await command.ExecuteScalarAsync(token) ?? throw Denied();
    }
}
