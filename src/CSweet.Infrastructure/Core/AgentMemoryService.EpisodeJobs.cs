using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CSweet.Domain.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryService
{
    private sealed record EpisodeJobSource(MemoryEpisode Episode, long Revision)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EpisodeReconciliation? Reconciliation { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EpisodeReextraction? Reextraction { get; init; }
    }
    internal sealed record EpisodeReextraction(int Version, int Generation, Guid PreviousJobId, Guid ReviewReceiptId);
    private static bool ValidReextraction(EpisodeReextraction? value) => value is
        { Version: 1, Generation: > 0 and <= 8 } && value.PreviousJobId != Guid.Empty && value.ReviewReceiptId != Guid.Empty;

    public async Task<MemoryWriteResult> AcceptProposalAsync(Guid organizationId, Guid employeeId, Guid installationId,
        MemoryEpisode episode, CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsNpgsql()) throw new InvalidOperationException("Durable episode ingestion requires PostgreSQL.");
        if (episode.Source != new MemorySource("agent-proposal", episode.Id.ToString("D"), employeeId.ToString("D")) ||
            episode.TransferEvidence is not null || episode.LegalHold || episode.Sensitivity < MemorySensitivity.Personal ||
            ReadGuid(episode.Metadata, "installationId") != installationId || episode.Content.Length > 32000)
            throw new UnauthorizedAccessException();
        await store.InitializeAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await LockEpisodeJobBarrierAsync(cancellationToken);
            await using var enlisted = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
            var authority = new MemoryEpisodeEnrichmentJob { OrganizationId = organizationId, EmployeeId = employeeId,
                InstallationId = installationId };
            await AuthorizeEpisodeJobAsync(authority, episode, cancellationToken);
            var result = await enlisted.AppendEpisodeAsync(episode, cancellationToken);
            var retained = await ((IMemorySourceReader)enlisted).GetEpisodeAsync(episode.Partition, result.Id, cancellationToken)
                ?? throw new MemorySourceInvalidatedException();
            await StageEpisodeJobAsync(db, retained, organizationId, employeeId, installationId, null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    internal static async Task StageEpisodeJobAsync(Persistence.CSweetDbContext context, MemoryEpisode episode, Guid organization,
        Guid employee, Guid installation, Guid? reviewer, CancellationToken token, EpisodeReconciliation? reconciliation = null)
    {
        if (!context.Database.IsNpgsql() || context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Episode acceptance and its durable job must share a PostgreSQL transaction.");
        var revision = await context.Database.SqlQuery<long>($"""
            SELECT COALESCE(MAX(revision),0) AS "Value" FROM csweet_memory_revisions
            WHERE partition_key={episode.Partition.StorageKey} AND record_id={episode.Id} AND kind={(int)MemoryRecordKind.Episode}
            """).SingleAsync(token);
        if (revision <= 0 || !MemorySourceIntegrity.IsVerified(episode)) throw new MemorySourceInvalidatedException();
        var json = JsonSerializer.Serialize(new EpisodeJobSource(episode, revision) { Reconciliation = reconciliation });
        if (Encoding.UTF8.GetByteCount(json) > 1_048_576) throw new ArgumentException("Episode input exceeds the durable storage bound.");
        var hash = SourceChecksum(json);
        var existing = await context.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleOrDefaultAsync(x => x.EpisodeId == episode.Id && x.SupersededAt == null, token);
        if (existing is not null)
        {
            // Replays never reset a completed/failed job or replace its original accepted evidence.
            if (existing.OrganizationId != organization || existing.EmployeeId != employee || existing.InstallationId != installation ||
                existing.ReviewerApplicationUserId != reviewer) throw new InvalidOperationException("memory_write_conflict");
            return;
        }
        var now = DateTimeOffset.UtcNow;
        context.MemoryEpisodeEnrichmentJobs.Add(new() { Id = Guid.NewGuid(), OrganizationId = organization, EmployeeId = employee,
            InstallationId = installation, ReviewerApplicationUserId = reviewer, EpisodeId = episode.Id,
            SourceJson = json, SourceHash = hash, Status = MemoryCaptureStatus.Pending, CreatedAt = now, NextAttemptAt = now });
        await context.SaveChangesAsync(token);
    }

    private async Task<int> ProcessEpisodeJobsAsync(int limit, CancellationToken token)
    {
        if (!db.Database.IsNpgsql()) return 0;
        var visited = new List<Guid>(); var completed = 0;
        for (var index = 0; index < limit; index++)
        {
            var now = DateTimeOffset.UtcNow;
            var next = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().Where(x => x.SupersededAt == null && !visited.Contains(x.Id) &&
                (x.Status == MemoryCaptureStatus.Pending && x.NextAttemptAt <= now ||
                 x.Status == MemoryCaptureStatus.Processing && (x.LeaseExpiresAt == null || x.LeaseExpiresAt <= now)))
                .Select(x => new { x.Id, x.CreatedAt, LastServed = db.MemoryEpisodeEnrichmentJobs
                    .Where(other => other.OrganizationId == x.OrganizationId).Max(other => other.LastAttemptAt) })
                .OrderBy(x => x.LastServed ?? DateTimeOffset.MinValue).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(token);
            if (next is null) break;
            visited.Add(next.Value);
            if (await ProcessEpisodeJobAsync(next.Value, token)) completed++;
        }
        return completed;
    }

    private async Task<bool> ProcessEpisodeJobAsync(Guid id, CancellationToken token)
    {
        var lease = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var claimed = await db.MemoryEpisodeEnrichmentJobs.Where(x => x.Id == id && x.SupersededAt == null &&
            (x.Status == MemoryCaptureStatus.Pending && x.NextAttemptAt <= now ||
             x.Status == MemoryCaptureStatus.Processing && (x.LeaseExpiresAt == null || x.LeaseExpiresAt <= now))).ExecuteUpdateAsync(set => set
                .SetProperty(x => x.Status, MemoryCaptureStatus.Processing).SetProperty(x => x.LeaseToken, lease)
                .SetProperty(x => x.LeaseExpiresAt, now + EnrichmentLeaseDuration).SetProperty(x => x.LastAttemptAt, now)
                .SetProperty(x => x.Attempts, x => x.Attempts + 1), token);
        if (claimed == 0) return false;
        db.ChangeTracker.Clear();
        var job = await db.MemoryEpisodeEnrichmentJobs.SingleOrDefaultAsync(x => x.Id == id, token);
        // Reviewed erasure/purge can commit after the claim and before this read.
        // Missing work is a settled outcome, rather than an exception that stalls the batch.
        if (job is null) return false;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        attempt.CancelAfter(EnrichmentLeaseDuration - TimeSpan.FromSeconds(30));
        try
        {
            if (job.Attempts > MaximumEnrichmentAttempts) throw new MemorySourceInvalidatedException("memory_enrichment_attempts_exhausted");
            var source = JsonSerializer.Deserialize<EpisodeJobSource>(job.SourceJson) ?? throw new MemorySourceInvalidatedException();
            AcceptedMemoryExtraction? accepted;
            if (job.AcceptedExtractionJson is null)
            {
                await CheckEpisodeJobAsync(job, lease, source, receipt: false, attempt.Token);
                var provider = await ResolveInstallationEnrichmentProviderAsync(job.InstallationId, attempt.Token);
                await AcquireProviderLeaseAsync(provider.Id, job.Id, lease, attempt.Token);
                try
                {
                    await CheckEpisodeJobAsync(job, lease, source, receipt: true, attempt.Token);
                    var (enrichment, version) = await EnrichWithTelemetryAsync(source.Episode, provider.Id, provider.Model, async ct =>
                    {
                        await CheckEpisodeJobAsync(job, lease, source, receipt: false, ct);
                        if (await ResolveInstallationEnrichmentProviderAsync(job.InstallationId, ct) != provider)
                            throw new InvalidOperationException("The enrichment provider configuration changed before dispatch.");
                    }, attempt.Token);
                    accepted = new(source.Reextraction is not null ? 6 : source.Reconciliation is null ? 4 : 5, source.Episode, enrichment, version)
                    { GenericSourceHash = job.SourceHash, Provider = provider, Reconciliation = source.Reconciliation, Reextraction = source.Reextraction };
                    var json = JsonSerializer.Serialize(accepted);
                    if (Encoding.UTF8.GetByteCount(json) > 1_048_576) throw new InvalidOperationException("Accepted output exceeds the storage bound.");
                    await CheckEpisodeJobAsync(job, lease, source, receipt: false, attempt.Token);
                    // Fence acceptance itself; a lost lease may never persist a late provider response.
                    if (await db.MemoryEpisodeEnrichmentJobs.Where(x => x.Id == id && x.Status == MemoryCaptureStatus.Processing &&
                        x.LeaseToken == lease && x.LeaseExpiresAt > DateTimeOffset.UtcNow).ExecuteUpdateAsync(set => set
                            .SetProperty(x => x.AcceptedExtractionJson, json).SetProperty(x => x.ExtractionAcceptedAt, DateTimeOffset.UtcNow), attempt.Token) != 1)
                        throw new MemoryLeaseLostException();
                }
                finally { await ReleaseProviderLeaseAsync(provider.Id, lease); }
            }
            else accepted = JsonSerializer.Deserialize<AcceptedMemoryExtraction>(job.AcceptedExtractionJson);
            if (accepted is null || !MatchesEpisodeExtraction(accepted, source) || accepted.GenericSourceHash != job.SourceHash ||
                JsonSerializer.Serialize(accepted.Episode) != JsonSerializer.Serialize(source.Episode) || !HasVerifiableEnvelope(accepted))
                throw new MemorySourceInvalidatedException("memory_enrichment_unverifiable_output");
            await using var transaction = await db.Database.BeginTransactionAsync(attempt.Token);
            await LockEpisodeJobAsync(id, attempt.Token);
            await RequireEpisodeLeaseAsync(id, lease, attempt.Token);
            await LockEpisodeJobBarrierAsync(attempt.Token);
            await using var enlisted = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
            await ValidateEpisodeJobSourceAsync(job, source, enlisted, attempt.Token);
            var acceptedAt = await db.MemoryEpisodeEnrichmentJobs.Where(x => x.Id == id).Select(x => x.ExtractionAcceptedAt).SingleAsync(attempt.Token);
            await ApplyExtractionAsync(enlisted, accepted, acceptedAt!.Value, attempt.Token);
            await db.MemoryEpisodeEnrichmentJobs.Where(x => x.Id == id && x.LeaseToken == lease).ExecuteUpdateAsync(set => set
                .SetProperty(x => x.Status, MemoryCaptureStatus.Completed).SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow)
                .SetProperty(x => x.LastError, (string?)null).SetProperty(x => x.LeaseToken, (Guid?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null), attempt.Token);
            await transaction.CommitAsync(attempt.Token);
            EnrichedEpisodes.Add(1);
            return true;
        }
        catch (Exception error)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            db.ChangeTracker.Clear();
            var deferral = error is MemoryProviderBusyException || token.IsCancellationRequested;
            // Busy providers and worker shutdown refund this claim. Decide exhaustion
            // from that same count, otherwise the final refundable claim becomes a
            // terminal job with attempts still available. Semantic denials stay final.
            var spentAttempts = deferral ? Math.Max(0, job.Attempts - 1) : job.Attempts;
            var permanent = error is MemorySourceInvalidatedException or UnauthorizedAccessException || spentAttempts >= MaximumEnrichmentAttempts;
            var code = error is MemorySourceInvalidatedException invalid ? invalid.Code : error is UnauthorizedAccessException
                ? "memory_enrichment_authority_revoked" : token.IsCancellationRequested ? "memory_enrichment_interrupted" :
                error is MemoryProviderBusyException ? "memory_provider_busy" : "memory_enrichment_failed";
            try
            {
                await db.MemoryEpisodeEnrichmentJobs.Where(x => x.Id == id && x.LeaseToken == lease && x.Status == MemoryCaptureStatus.Processing)
                    .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, permanent ? MemoryCaptureStatus.Failed : MemoryCaptureStatus.Pending)
                        .SetProperty(x => x.LastError, code).SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(deferral ? 15 : Math.Min(300, Math.Pow(2, job.Attempts))))
                        .SetProperty(x => x.Attempts, x => deferral ? x.Attempts - 1 : x.Attempts)
                        .SetProperty(x => x.LeaseToken, (Guid?)null).SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null), cleanup.Token);
            }
            catch (Exception cleanupError) { logger.LogWarning("Generic memory lease cleanup failed ({FailureType}).", cleanupError.GetType().Name); }
            if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
            logger.LogWarning("Episode enrichment failed for {JobId} ({FailureType}).", id, error.GetType().Name);
            return false;
        }
    }

    private async Task CheckEpisodeJobAsync(MemoryEpisodeEnrichmentJob job, Guid lease, EpisodeJobSource source, bool receipt, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await LockEpisodeJobAsync(job.Id, token);
        await RequireEpisodeLeaseAsync(job.Id, lease, token);
        await LockEpisodeJobBarrierAsync(token);
        await using var enlisted = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
        await ValidateEpisodeJobSourceAsync(job, source, enlisted, token);
        if (receipt && !await db.MemoryEpisodeExtractionReceipts.AnyAsync(x => x.JobId == job.Id && x.LeaseToken == lease, token))
        {
            if (await db.MemoryEpisodeExtractionReceipts.CountAsync(x => x.JobId == job.Id, token) >= 64)
                throw new MemorySourceInvalidatedException("memory_enrichment_input_receipt_capacity");
            db.MemoryEpisodeExtractionReceipts.Add(new() { Id = Guid.NewGuid(), JobId = job.Id, LeaseToken = lease,
                SourceHash = job.SourceHash, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    private async Task ValidateEpisodeJobSourceAsync(MemoryEpisodeEnrichmentJob job, EpisodeJobSource source, IMemoryStore target, CancellationToken token)
    {
        var episode = source.Episode;
        if (job.SupersededAt is not null || job.InputGeneration == 0 && (job.PreviousJobId is not null || source.Reextraction is not null) ||
            job.InputGeneration != 0 && (!ValidReextraction(source.Reextraction) || source.Reextraction!.Generation != job.InputGeneration ||
                source.Reextraction.PreviousJobId != job.PreviousJobId ||
                !await db.MemoryEpisodeReextractionReceipts.AsNoTracking().AnyAsync(x => x.Id == source.Reextraction.ReviewReceiptId &&
                    x.JobId == job.Id && x.PreviousJobId == job.PreviousJobId && x.OrganizationId == job.OrganizationId &&
                    x.EmployeeId == job.EmployeeId && x.EpisodeId == job.EpisodeId && x.InputGeneration == job.InputGeneration &&
                    x.SourceHash == job.SourceHash, token))) throw new MemorySourceInvalidatedException();
        if (episode is null || source.Revision <= 0 || job.SourceHash != SourceChecksum(job.SourceJson) || episode.Id != job.EpisodeId ||
            episode.Partition.TenantId != job.OrganizationId.ToString("D") || !MemorySourceIntegrity.IsVerified(episode))
            throw new MemorySourceInvalidatedException();
        await AuthorizeEpisodeJobAsync(job, episode, token);
        // These store reads recursively verify transfer certificates and their original revisions.
        var current = await ((IMemorySourceReader)target).GetEpisodeAsync(episode.Partition, episode.Id, token);
        var revision = await db.Database.SqlQuery<long>($"""
            SELECT COALESCE(MAX(revision),0) AS "Value" FROM csweet_memory_revisions
            WHERE partition_key={episode.Partition.StorageKey} AND record_id={episode.Id} AND kind={(int)MemoryRecordKind.Episode}
            """).SingleAsync(token);
        if (current is null || !MemoryProvenance.IsCurrent(current, episode.Partition, episode.Id, DateTimeOffset.UtcNow))
            throw new MemorySourceInvalidatedException();
        if ((revision != source.Revision || JsonSerializer.Serialize(current) != JsonSerializer.Serialize(episode)) &&
            !await HasOnlyEpisodeHoldChangesAsync(source, current, revision, token)) throw new MemorySourceInvalidatedException();
        if (source.Reconciliation is not null)
        {
            if (!ValidReconciliation(source.Reconciliation)) throw new MemorySourceInvalidatedException();
            if (job.Status != MemoryCaptureStatus.Completed)
            {
                try
                {
                    var baseline = await ReadEpisodeReconciliationAsync(episode, token);
                    if (JsonSerializer.Serialize(baseline) != JsonSerializer.Serialize(source.Reconciliation))
                        throw new MemorySourceInvalidatedException();
                }
                catch (Exception error) when (error is JsonException or FormatException ||
                    error is InvalidOperationException { Message: "memory_ingestion_legacy_output_unavailable" })
                { throw new MemorySourceInvalidatedException(); }
            }
        }
        if (episode.TransferEvidence is not null)
        {
            var package = await ((IKnowledgeTransferStore)target).GetKnowledgeTransferAsync(episode.TransferEvidence.PackageId, token);
            if (package is null || job.ReviewerApplicationUserId is not Guid reviewer || !Guid.TryParse(package.SourceEmployeeId, out var origin) ||
                package.TargetEmployeeId != job.EmployeeId.ToString("D")) throw new MemorySourceInvalidatedException();
            var actor = await MemoryManagerAuthorization.RequireAsync(db, job.OrganizationId, origin, reviewer, true, token, true);
            if (actor != await MemoryManagerAuthorization.RequireAsync(db, job.OrganizationId, job.EmployeeId, reviewer, true, token, true))
                throw new UnauthorizedAccessException();
            try
            {
                await new AgentMemoryReviewService(db, target, TimeProvider.System).RequireTransferAudienceAsync(
                    job.OrganizationId, job.EmployeeId, reviewer, actor, current, token);
            }
            catch (InvalidOperationException error) when (error.Message == "memory_transfer_source_unavailable")
            { throw new MemorySourceInvalidatedException("memory_transfer_source_unavailable"); }
        }
    }

    // The recovery service owns the job row lock and transaction. Reuse the worker's
    // exact source/revision/producer authority checks, then authorize the retrying human
    // for the audience and every transferred contributor, without rebinding the source.
    internal async Task RequireEpisodeRecoverySourceAsync(MemoryEpisodeEnrichmentJob job, Guid user, Guid actor, CancellationToken token)
    {
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is null) throw new NotSupportedException();
        await LockEpisodeJobBarrierAsync(token);
        await using var enlisted = new PostgreSqlMemoryStore((NpgsqlTransaction)db.Database.CurrentTransaction.GetDbTransaction());
        try
        {
            var source = JsonSerializer.Deserialize<EpisodeJobSource>(job.SourceJson) ?? throw new MemorySourceInvalidatedException();
            await ValidateEpisodeJobSourceAsync(job, source, enlisted, token);
            await MemoryEpisodeOperatorAuthorization.RequirePartitionAsync(db, job.OrganizationId, job.EmployeeId,
                actor, source.Episode.Partition, token);
            if (source.Episode.TransferEvidence is not null)
                await new AgentMemoryReviewService(db, enlisted, TimeProvider.System).RequireTransferAudienceAsync(
                    job.OrganizationId, job.EmployeeId, user, actor, source.Episode, token);
            if (job.AcceptedExtractionJson is not null)
            {
                var accepted = JsonSerializer.Deserialize<AcceptedMemoryExtraction>(job.AcceptedExtractionJson);
                if (accepted is null || !MatchesEpisodeExtraction(accepted, source) || accepted.GenericSourceHash != job.SourceHash ||
                    JsonSerializer.Serialize(accepted.Episode) != JsonSerializer.Serialize(source.Episode) || !HasVerifiableEnvelope(accepted))
                    throw new MemorySourceInvalidatedException("memory_enrichment_unverifiable_output");
            }
        }
        catch (Exception error) when (error is MemorySourceInvalidatedException or JsonException)
        { throw new InvalidOperationException("The saved episode input or extraction cannot be safely retried.", error); }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.LockNotAvailable)
        { throw new DbUpdateConcurrencyException("The episode audience is changing. Refresh before retrying.", error); }
    }

    private static bool MatchesEpisodeExtraction(AcceptedMemoryExtraction accepted, EpisodeJobSource source) =>
        accepted.SchemaVersion == (source.Reextraction is not null ? 6 : source.Reconciliation is null ? 4 : 5) &&
        JsonSerializer.Serialize(accepted.Reconciliation) == JsonSerializer.Serialize(source.Reconciliation) &&
        JsonSerializer.Serialize(accepted.Reextraction) == JsonSerializer.Serialize(source.Reextraction);

    private Task LockEpisodeJobAsync(Guid id, CancellationToken token) => db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT 1 FROM \"MemoryEpisodeEnrichmentJobs\" WHERE \"Id\"={id} FOR UPDATE", token);

    private async Task RequireEpisodeLeaseAsync(Guid id, Guid lease, CancellationToken token)
    {
        if (!await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().AnyAsync(x => x.Id == id && x.SupersededAt == null && x.Status == MemoryCaptureStatus.Processing &&
            x.LeaseToken == lease && x.LeaseExpiresAt > DateTimeOffset.UtcNow, token)) throw new MemoryLeaseLostException();
    }

    private Task LockEpisodeJobBarrierAsync(CancellationToken token) => db.Database.ExecuteSqlRawAsync(
        "LOCK TABLE csweet_memory_episodes, csweet_memory_transfers, csweet_memory_revisions, csweet_memory_claims, csweet_memory_entities, csweet_memory_edges, csweet_memory_procedures IN SHARE ROW EXCLUSIVE MODE", token);
}
