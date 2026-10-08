using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryService
{
    internal sealed class RecoveryEpisodeRow
    {
        public string Partition { get; set; } = "";
        public string Payload { get; set; } = "";
    }

    // This is a snapshot of an existing accepted record, never a replacement write.
    // Caller owns the writer barrier and authorizes the human before exposing content.
    internal async Task<(MemoryEpisode Episode, MemoryEpisodeEnrichmentJob Job, long Revision)> SnapshotRecoveryInputAsync(
        Guid organization, Guid employee, Guid episodeId, CancellationToken token)
    {
        var row = await db.Database.SqlQuery<RecoveryEpisodeRow>($"""
            SELECT partition_key AS "Partition",payload::text AS "Payload" FROM csweet_memory_episodes e
            WHERE id={episodeId} AND octet_length(payload::text)<=1048576 AND partition_key LIKE 'mp2:%'
              AND NOT EXISTS(SELECT 1 FROM csweet_memory_partition_migration_rows q
                WHERE q.table_name='csweet_memory_episodes' AND q.record_id=e.id::text AND q.disposition='Quarantine')
            """).SingleOrDefaultAsync(token) ?? throw new KeyNotFoundException();
        var episode = JsonSerializer.Deserialize<MemoryEpisode>(row.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("memory_ingestion_source_unavailable");
        using var document = JsonDocument.Parse(row.Payload);
        // Match the store's conservative projection; absent legacy classification must
        // never acquire the record constructor's lower default during recovery.
        if (!document.RootElement.TryGetProperty("sensitivity", out _)) episode = episode with { Sensitivity = MemorySensitivity.Restricted };
        if (episode.Id != episodeId || episode.Partition?.StorageKey != row.Partition || episode.Source is null ||
            episode.Partition.TenantId != organization.ToString("D")) throw new UnauthorizedAccessException();
        var installation = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.Id == employee && x.OrganizationId == organization)
            .Select(x => x.AgentInstallationId).SingleAsync(token) ?? throw new UnauthorizedAccessException();
        var revision = await db.Database.SqlQuery<long>($"""
            SELECT COALESCE(MAX(revision),0) AS "Value" FROM csweet_memory_revisions
            WHERE partition_key={row.Partition} AND record_id={episodeId} AND kind={(int)MemoryRecordKind.Episode}
            """).SingleAsync(token);
        Guid? reviewer = null;
        if (episode.Source.Type == "knowledge-transfer")
        {
            var receipts = await db.MemoryTransferReceipts.AsNoTracking().Where(x => x.OrganizationId == organization &&
                x.TargetEmployeeId == employee && x.AppliedEpisodeId == episodeId && x.Action == "apply" && x.Status == "Applied")
                .Take(2).ToListAsync(token);
            if (receipts.Count == 1 && receipts[0].PackageId == episode.TransferEvidence?.PackageId)
                reviewer = receipts[0].ActorApplicationUserId;
        }
        var json = JsonSerializer.Serialize(new EpisodeJobSource(episode, revision));
        return (episode, new MemoryEpisodeEnrichmentJob { OrganizationId = organization, EmployeeId = employee,
            InstallationId = installation, ReviewerApplicationUserId = reviewer, EpisodeId = episodeId,
            SourceJson = json, SourceHash = SourceChecksum(json) }, revision);
    }

    internal Task LockIngestionRecoveryBarrierAsync(CancellationToken token) => LockEpisodeJobBarrierAsync(token);
}
