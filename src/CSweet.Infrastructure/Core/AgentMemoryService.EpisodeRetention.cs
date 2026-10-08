using System.Text;
using System.Text.Json;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryService
{
    private sealed class EpisodeRetentionRevision
    {
        public long Revision { get; set; }
        public int Operation { get; set; }
        public bool OnlyHoldChanged { get; set; }
        public string? Payload { get; set; }
    }

    // Retention does not change extraction input. Preserve the original immutable
    // snapshot/output, and prove that every intervening revision changed only a hold.
    private async Task<bool> HasOnlyEpisodeHoldChangesAsync(EpisodeJobSource source, MemoryEpisode current,
        long revision, CancellationToken token)
    {
        var original = source.Episode;
        if (revision <= source.Revision ||
            JsonSerializer.Serialize(current with { LegalHold = original.LegalHold }) != JsonSerializer.Serialize(original)) return false;
        var rows = db.Database.SqlQuery<EpisodeRetentionRevision>($"""
            SELECT h.revision AS "Revision",h.operation AS "Operation",
              h.payload-'legalHold'=baseline.payload-'legalHold' AS "OnlyHoldChanged",
              CASE WHEN octet_length(h.payload::text)<=1048576 THEN h.payload::text ELSE NULL END AS "Payload"
            FROM csweet_memory_revisions h CROSS JOIN
              (SELECT payload FROM csweet_memory_revisions WHERE revision={source.Revision}
                AND partition_key={original.Partition.StorageKey} AND record_id={original.Id}
                AND kind={(int)MemoryRecordKind.Episode}) baseline
            WHERE h.partition_key={original.Partition.StorageKey} AND h.record_id={original.Id}
              AND h.kind={(int)MemoryRecordKind.Episode} AND h.revision>={source.Revision} AND h.revision<={revision}
            ORDER BY h.revision LIMIT 66
            """).AsAsyncEnumerable();
        var baseline = JsonSerializer.Serialize(original);
        var index = 0; long bytes = 0; long previous = 0;
        try
        {
            await foreach (var row in rows.WithCancellation(token))
            {
                // Revisions use a global sequence; other records and rolled-back writes
                // legitimately leave gaps. Count this episode's retained snapshots.
                if (row.Revision <= previous || row.Payload is null || !row.OnlyHoldChanged ||
                    (index == 0 ? row.Revision != source.Revision || row.Operation is < 0 or > 2 : row.Operation != 2)) return false;
                bytes += Encoding.UTF8.GetByteCount(row.Payload);
                if (++index > 65 || bytes > 4_194_304) return false;
                var retained = JsonSerializer.Deserialize<MemoryEpisode>(row.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (retained is null || !MemorySourceIntegrity.IsVerified(retained) ||
                    JsonSerializer.Serialize(retained with { LegalHold = original.LegalHold }) != baseline) return false;
                if (index == 1 && JsonSerializer.Serialize(retained) != baseline ||
                    row.Revision == revision && JsonSerializer.Serialize(retained) != JsonSerializer.Serialize(current)) return false;
                previous = row.Revision;
            }
        }
        catch (JsonException) { return false; }
        return index >= 2 && previous == revision;
    }
}
