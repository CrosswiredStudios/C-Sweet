using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

// Caller holds generic queue, provider, authority and store barriers until commit.
// Plans cannot be staged in another transaction; deleting jobs fences every old lease
// and cascades dispatch receipts, while immutable content-free retry/review evidence survives.
internal sealed class MemoryEpisodeErasure(CSweetDbContext db)
{
    internal sealed record Entry(MemoryEpisodeEnrichmentJob Job, AgentMemoryService.EpisodeErasureEvidence Evidence);
    internal sealed record Plan(MemoryEpisodeErasure Owner, Guid Transaction, Entry[] Entries, string? BlockedReason, string RetentionHash,
        MemoryEpisode[] RetainedSources);
    private readonly HashSet<Plan> prepared=[];

    internal async Task<Plan> PrepareAsync(Guid organization, IReadOnlyCollection<MemoryErasureTarget> targets, PostgreSqlMemoryStore store, CancellationToken token)
    {
        try { return await PrepareCoreAsync(organization,targets,store,token); }
        catch(Exception error) when(error is JsonException or FormatException or ArgumentException or NullReferenceException)
        { throw new InvalidOperationException("memory_erasure_generic_lineage_review_required",error); }
    }

    private async Task<Plan> PrepareCoreAsync(Guid organization, IReadOnlyCollection<MemoryErasureTarget> targets, PostgreSqlMemoryStore store, CancellationToken token)
    {
        var transaction=db.Database.CurrentTransaction ?? throw new InvalidOperationException("Generic erasure requires the caller's transaction.");
        var rows=db.MemoryEpisodeEnrichmentJobs.AsNoTracking().Where(x=>x.OrganizationId==organization).OrderBy(x=>x.Id).Take(4097);
        var selected=targets.ToHashSet(); var affected=new List<Entry>(); long bytes=0; var count=0; var held=false;
        var retainedSources=new Dictionary<(MemoryPartition,Guid),MemoryEpisode>();
        async Task<MemoryEpisode> RetainedAsync(MemoryPartition partition,Guid id)
        {
            if(retainedSources.TryGetValue((partition,id),out var known)) return known;
            if(retainedSources.Count>=1024) throw new InvalidOperationException("memory_erasure_scan_limit");
            var retained=await store.GetEpisodeAsync(partition,id,token);
            if(retained is null || !MemorySourceIntegrity.IsVerified(retained)) throw Blocked();
            bytes+=Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(retained));
            if(bytes>33_554_432) throw new InvalidOperationException("memory_erasure_scan_limit");
            retainedSources.Add((partition,id),retained); return retained;
        }
        await foreach(var job in rows.AsAsyncEnumerable().WithCancellation(token))
        {
            bytes+=Encoding.UTF8.GetByteCount(job.SourceJson)+Encoding.UTF8.GetByteCount(job.AcceptedExtractionJson ?? "");
            if (++count>4096 || bytes>33_554_432) throw new InvalidOperationException("memory_erasure_scan_limit");
            var evidence=AgentMemoryService.InspectEpisodeForErasure(job, includeAccepted: job.SupersededAt is null);
            if (!evidence.References.Any(selected.Contains)) continue;
            if(affected.Count>=128) throw new InvalidOperationException("memory_erasure_scan_limit");
            affected.Add(new(job,evidence));
        }
        // Finish the streaming reader before using the transaction's same connection
        // for retained source/history/receipt reads. Only bounded affected jobs are kept.
        foreach(var entry in affected)
        {
            var job=entry.Job; var evidence=entry.Evidence;
            if (job.SupersededAt is not null && !await MemoryEpisodeReextractionEvidence.VerifyArchivedAsync(db, job, token)) throw Blocked();
            await RetainedAsync(evidence.Episode.Partition,evidence.Episode.Id);
            var payload=await db.Database.SqlQuery<string>($"""
                SELECT payload::text AS "Value" FROM csweet_memory_revisions WHERE partition_key={evidence.Episode.Partition.StorageKey}
                  AND record_id={job.EpisodeId} AND kind=0 AND revision={evidence.Revision} AND octet_length(payload::text)<=1048576
                """).SingleOrDefaultAsync(token);
            if (payload is null || JsonSerializer.Serialize(JsonSerializer.Deserialize<MemoryEpisode>(payload,new JsonSerializerOptions(JsonSerializerDefaults.Web))) !=
                JsonSerializer.Serialize(evidence.Episode)) throw Blocked();
            foreach(var reference in evidence.References.Where(x=>x.Kind==MemoryErasureKind.Episode))
            {
                var retained=await RetainedAsync(reference.Partition,reference.Id);
                held|=retained.LegalHold;
            }
            var receipts=await db.MemoryEpisodeExtractionReceipts.AsNoTracking().Where(x=>x.JobId==job.Id).Take(65).ToArrayAsync(token);
            if (receipts.Length>64 || receipts.Any(x=>x.SourceHash!=job.SourceHash || x.LeaseToken==Guid.Empty) ||
                job.Status==MemoryCaptureStatus.Processing && !receipts.Any(x=>x.LeaseToken==job.LeaseToken)) throw Blocked();
        }
        var retention=JsonSerializer.Serialize(retainedSources.OrderBy(x=>x.Key.Item1.StorageKey,StringComparer.Ordinal).ThenBy(x=>x.Key.Item2).Select(x=>x.Value));
        var retentionHash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(retention))).ToLowerInvariant();
        var plan=new Plan(this,transaction.TransactionId,affected.ToArray(),held ? "memory_legal_hold_prevents_deletion" : null,retentionHash,
            retainedSources.OrderBy(x=>x.Key.Item1.StorageKey,StringComparer.Ordinal).ThenBy(x=>x.Key.Item2).Select(x=>x.Value).ToArray());
        prepared.Add(plan); return plan;
    }

    internal async Task<int> StageAsync(Plan plan,CancellationToken token)
    {
        if (!ReferenceEquals(plan.Owner,this) || !prepared.Contains(plan) || db.Database.CurrentTransaction?.TransactionId!=plan.Transaction)
            throw new InvalidOperationException("Generic erasure plan belongs to another transaction.");
        if (plan.BlockedReason is { } reason) throw new InvalidOperationException(reason);
        var ids=plan.Entries.Select(x=>x.Job.Id).ToArray();
        if (db.ChangeTracker.Entries<MemoryEpisodeEnrichmentJob>().Any(x=>ids.Contains(x.Entity.Id)) ||
            db.ChangeTracker.Entries<MemoryEnrichmentProviderLease>().Any(x=>ids.Contains(x.Entity.JobId)))
            throw new InvalidOperationException("Generic erasure requires untracked job rows.");
        var deleted=await db.MemoryEpisodeEnrichmentJobs.Where(x=>ids.Contains(x.Id)).ExecuteDeleteAsync(token);
        if (deleted!=ids.Length) throw new DbUpdateConcurrencyException("Generic extraction changed. Refresh the erasure review.");
        await db.MemoryEnrichmentProviderLeases.Where(x=>ids.Contains(x.JobId)).ExecuteDeleteAsync(token);
        return deleted;
    }
    private static InvalidOperationException Blocked()=>new("memory_erasure_generic_lineage_review_required");
}
