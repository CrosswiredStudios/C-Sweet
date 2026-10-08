using System.Text;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryService
{
    internal async Task<MemoryEpisodeEnrichmentJob> PrepareReextractionAsync(MemoryEpisodeEnrichmentJob original,
        Guid user, Guid actor, DateTimeOffset now, CancellationToken token)
    {
        var snapshot = await SnapshotRecoveryInputAsync(original.OrganizationId, original.EmployeeId, original.EpisodeId, token);
        // Validate the reviewed current input independently; never reuse unverifiable output.
        InspectEpisodeForErasure(snapshot.Job, includeAccepted: false);
        await RequireEpisodeRecoverySourceAsync(snapshot.Job, user, actor, token);
        var chain = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().Where(x => x.EpisodeId == original.EpisodeId)
            .OrderBy(x => x.InputGeneration).Take(10).ToArrayAsync(token);
        if (chain.Length is < 1 or > 9 || chain.Length != chain[^1].InputGeneration + 1)
            throw new InvalidOperationException("memory_reextraction_history_unavailable");
        long bytes = 0;
        for (var index = 0; index < chain.Length; index++)
        {
            var item = chain[index];
            bytes += Encoding.UTF8.GetByteCount(item.SourceJson) + Encoding.UTF8.GetByteCount(item.AcceptedExtractionJson ?? "");
            if (bytes > 18_874_368 || item.InputGeneration != index || item.OrganizationId != original.OrganizationId ||
                item.EmployeeId != original.EmployeeId || item.InstallationId != snapshot.Job.InstallationId ||
                item.ReviewerApplicationUserId != snapshot.Job.ReviewerApplicationUserId ||
                (index == 0 ? item.PreviousJobId is not null : item.PreviousJobId != chain[index - 1].Id))
                throw new InvalidOperationException("memory_reextraction_history_unavailable");
            var evidence = InspectEpisodeForErasure(item, includeAccepted: false);
            if (evidence.Episode.Partition != snapshot.Episode.Partition ||
                evidence.Episode.SourceFingerprint != snapshot.Episode.SourceFingerprint ||
                snapshot.Episode.Sensitivity < evidence.Episode.Sensitivity)
                throw new InvalidOperationException("memory_reextraction_source_unavailable");
            var retained = await db.Database.SqlQuery<string>($"""
                SELECT payload::text AS "Value" FROM csweet_memory_revisions WHERE revision={evidence.Revision}
                  AND partition_key={evidence.Episode.Partition.StorageKey} AND record_id={original.EpisodeId} AND kind=0
                  AND octet_length(payload::text)<=1048576
                """).SingleOrDefaultAsync(token);
            if (retained is null || JsonSerializer.Serialize(JsonSerializer.Deserialize<MemoryEpisode>(retained,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))) != JsonSerializer.Serialize(evidence.Episode) ||
                index < chain.Length - 1 && !await MemoryEpisodeReextractionEvidence.VerifyArchivedAsync(db, item, token))
                throw new InvalidOperationException("memory_reextraction_history_unavailable");
            if (index > 0)
            {
                var source = JsonSerializer.Deserialize<EpisodeJobSource>(item.SourceJson)!;
                if (!await db.MemoryEpisodeReextractionReceipts.AsNoTracking().AnyAsync(x =>
                    x.Id == source.Reextraction!.ReviewReceiptId && x.JobId == item.Id && x.PreviousJobId == item.PreviousJobId &&
                    x.SourceHash == item.SourceHash && x.InputGeneration == index && x.OrganizationId == original.OrganizationId &&
                    x.EmployeeId == original.EmployeeId && x.EpisodeId == original.EpisodeId, token))
                    throw new InvalidOperationException("memory_reextraction_history_unavailable");
            }
        }
        var reconciliation = await ReadEpisodeReconciliationAsync(snapshot.Episode, token);
        var sourceJson = JsonSerializer.Serialize(new EpisodeJobSource(snapshot.Episode, snapshot.Revision)
        {
            Reconciliation = reconciliation
        });
        if (Encoding.UTF8.GetByteCount(sourceJson) > 1_048_576) throw new InvalidOperationException("memory_reextraction_source_unavailable");
        return new() { Id = Guid.NewGuid(), OrganizationId = original.OrganizationId, EmployeeId = original.EmployeeId,
            InstallationId = snapshot.Job.InstallationId, ReviewerApplicationUserId = snapshot.Job.ReviewerApplicationUserId,
            EpisodeId = original.EpisodeId, InputGeneration = original.InputGeneration + 1, PreviousJobId = original.Id,
            SourceJson = sourceJson, SourceHash = SourceChecksum(sourceJson), Status = MemoryCaptureStatus.Pending,
            CreatedAt = now, NextAttemptAt = now };
    }

    internal static (MemoryEpisode Episode, int ExistingRecords) ReextractionPreviewInput(MemoryEpisodeEnrichmentJob candidate)
    {
        var source = JsonSerializer.Deserialize<EpisodeJobSource>(candidate.SourceJson)!;
        return (source.Episode, source.Reconciliation?.RecordCount ?? 0);
    }
    internal static void BindReextractionReview(MemoryEpisodeEnrichmentJob candidate, Guid receiptId)
    {
        var source = JsonSerializer.Deserialize<EpisodeJobSource>(candidate.SourceJson)!;
        candidate.SourceJson = JsonSerializer.Serialize(source with { Reextraction = new(1, candidate.InputGeneration,
            candidate.PreviousJobId!.Value, receiptId) });
        if (Encoding.UTF8.GetByteCount(candidate.SourceJson) > 1_048_576) throw new InvalidOperationException("memory_reextraction_source_unavailable");
        candidate.SourceHash = SourceChecksum(candidate.SourceJson);
    }
}
