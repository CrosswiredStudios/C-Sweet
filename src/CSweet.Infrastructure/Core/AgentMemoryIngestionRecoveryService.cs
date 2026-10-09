using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed class AgentMemoryIngestionRecoveryService(CSweetDbContext db, IMemoryStore store,
    AgentMemoryService memory, TimeProvider clock) : IAgentMemoryIngestionRecoveryService
{
    private const string RecordKind = "EpisodeIngestion";
    private sealed record Cursor(Guid Organization, Guid Employee, Guid Actor, Guid Id);
    public sealed class CandidateRow
    {
        public Guid Id { get; set; }
        public string Source { get; set; } = "";
        public DateTimeOffset OccurredAt { get; set; }
        public string Audience { get; set; } = "";
    }

    public async Task<MemoryIngestionCandidatePage> ListAsync(Guid organizationId, Guid employeeId, Guid applicationUserId,
        string? cursor = null, int limit = 20, CancellationToken token = default)
    {
        await BackendAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var actor = await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, true, token, true);
        var employee = employeeId.ToString("D");
        var keys = await MemoryEpisodeOperatorAuthorization.ReadableKeysAsync(db, organizationId, employeeId, actor, token);
        Guid? after = cursor is null ? null : Decode(cursor, organizationId, employeeId, actor).Id;
        var size = Math.Clamp(limit, 1, 100);
        // Metadata only, bounded in SQL. Do not deserialize content or scan an export.
        // A prior recovery receipt permanently prevents discovery from silently requeueing an erased/deleted job.
        var rows = await db.Database.SqlQuery<CandidateRow>($"""
            SELECT e.id AS "Id",e.payload->'source'->>'type' AS "Source",e.occurred_at AS "OccurredAt",
              CASE WHEN e.payload->'partition'->>'applicationId'<>'csweet' THEN 'Installation-private'
                   WHEN e.payload->'partition'->>'customNamespace' LIKE 'team:%' THEN 'Team'
                   WHEN e.payload->'partition'->>'customNamespace' LIKE 'role:%' THEN 'Role'
                   WHEN e.payload->'partition'->>'customNamespace' LIKE 'relationship:%' THEN 'Private relationship'
                   WHEN e.payload->'partition'->>'customNamespace'='organization' THEN 'Organization'
                   WHEN e.payload->'partition'->>'customNamespace' LIKE 'case:%' THEN 'Case'
                   WHEN e.payload->'partition'->>'conversationId' IS NOT NULL THEN 'Conversation'
                   ELSE 'Employee' END AS "Audience"
            FROM csweet_memory_episodes e
            WHERE e.partition_key=ANY({keys})
              AND e.payload->'source'->>'type' IN ('agent-proposal','knowledge-transfer')
              AND (e.payload->'source'->>'type'<>'agent-proposal' OR e.payload->'source'->>'author'={employee})
              AND ({after}::uuid IS NULL OR e.id<{after}::uuid)
              AND NOT EXISTS(SELECT 1 FROM "MemoryEpisodeEnrichmentJobs" j WHERE j."EpisodeId"=e.id)
              AND NOT EXISTS(SELECT 1 FROM "MemoryReviewReceipts" r WHERE r."RecordKind"={RecordKind} AND r."ClaimId"=e.id)
              AND NOT EXISTS(SELECT 1 FROM "MemoryEpisodeReextractionReceipts" r WHERE r."EpisodeId"=e.id)
              AND NOT EXISTS(SELECT 1 FROM csweet_memory_partition_migration_rows q
                  WHERE q.table_name='csweet_memory_episodes' AND q.record_id=e.id::text AND q.disposition='Quarantine')
            ORDER BY e.id DESC LIMIT {size + 1}
            """).ToListAsync(token);
        await transaction.CommitAsync(token);
        var items = rows.Take(size).Select(x => new MemoryIngestionCandidate(x.Id, x.Source, x.OccurredAt, x.Audience)).ToList();
        return new(items, rows.Count > size ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new Cursor(organizationId, employeeId, actor, items[^1].EpisodeId))) : null);
    }

    public async Task<MemoryIngestionRecoveryPreview> PreviewAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, CancellationToken token = default)
    {
        await BackendAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var actor = await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, true, token, true);
        await memory.LockIngestionRecoveryBarrierAsync(token);
        var source = await ReadAsync(organizationId, employeeId, episodeId, applicationUserId, actor, token);
        var blocked = ShapeBlocker(source.Episode, source.Job.SourceJson) ?? await BlockerAsync(source.Job, source.Revision, applicationUserId, actor, token);
        AgentMemoryService.EpisodeReconciliation? reconciliation = null;
        if (blocked is null)
        {
            try
            {
                reconciliation = await memory.ReadEpisodeReconciliationAsync(source.Episode, token);
                AgentMemoryService.BindRecoveryReconciliation(source.Job, reconciliation);
                blocked = ShapeBlocker(source.Episode, source.Job.SourceJson);
            }
            catch (Exception error) when (error is InvalidOperationException or JsonException or FormatException)
            { blocked = "memory_ingestion_legacy_output_unavailable"; }
        }
        await transaction.CommitAsync(token);
        var content = source.Episode.Source.Type == "knowledge-transfer" && blocked is not null && blocked != "memory_ingestion_already_processed"
            ? "Transferred evidence is unavailable until its retained source lineage can be verified." : source.Episode.Content;
        return new(episodeId, source.Revision, Evidence(organizationId, employeeId, applicationUserId, actor, source.Job, source.Revision),
            content, source.Episode.Source.Type, source.Episode.Sensitivity.ToString(), blocked is null, blocked,
            MemoryEpisodeOperatorAuthorization.Label(source.Episode.Partition), reconciliation?.RecordCount ?? 0, reconciliation?.Policy);
    }

    public async Task<RecoverMemoryIngestionResponse> RecoverAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, RecoverMemoryIngestionRequest request, CancellationToken token = default)
    {
        if (episodeId == Guid.Empty || request.OperationId == Guid.Empty || request.ExpectedRevision <= 0 || request.EvidenceToken?.Length != 64)
            throw new ArgumentException("Invalid ingestion recovery review.");
        await BackendAsync(token);
        await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, false, token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({organizationId.ToString("D") + ":" + request.OperationId.ToString("D")},0))", token);
            var actor = await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, true, token, true);
            await memory.LockIngestionRecoveryBarrierAsync(token);
            var source = await ReadAsync(organizationId, employeeId, episodeId, applicationUserId, actor, token);
            if (ShapeBlocker(source.Episode, source.Job.SourceJson) is not null) throw new InvalidOperationException("memory_ingestion_source_unavailable");
            await memory.RequireEpisodeRecoverySourceAsync(source.Job, applicationUserId, actor, token);
            // Keep the pre-reconciliation identity for ordinary requests and their saved receipts.
            var requestHash = request.ReconciliationPolicy is null
                ? Hash(new { organizationId, employeeId, episodeId, applicationUserId, actor,
                    request = new { request.OperationId, request.ExpectedRevision, request.EvidenceToken } })
                : Hash(new { organizationId, employeeId, episodeId, applicationUserId, actor, request });
            var prior = await db.MemoryReviewReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.OperationId == request.OperationId, token);
            if (prior is not null)
            {
                if (prior.RecordKind != RecordKind || prior.RequestHash != requestHash) throw Changed();
                return await ResultAsync(prior, true, applicationUserId, actor, token);
            }
            var reconciliation = await memory.ReadEpisodeReconciliationAsync(source.Episode, token);
            AgentMemoryService.BindRecoveryReconciliation(source.Job, reconciliation);
            if (ShapeBlocker(source.Episode, source.Job.SourceJson) is not null)
                throw new InvalidOperationException("memory_ingestion_source_unavailable");
            if (request.ExpectedRevision != source.Revision || request.EvidenceToken != Evidence(organizationId, employeeId,
                applicationUserId, actor, source.Job, source.Revision)) throw Changed();
            if (request.ReconciliationPolicy != reconciliation?.Policy)
                throw new ArgumentException("The existing records require the previewed reconciliation choice.");
            if (await BlockerAsync(source.Job, source.Revision, applicationUserId, actor, token) is not null)
                throw new InvalidOperationException("memory_ingestion_source_unavailable");
            await AgentMemoryService.StageEpisodeJobAsync(db, source.Episode, organizationId, employeeId,
                source.Job.InstallationId, source.Job.ReviewerApplicationUserId, token, reconciliation);
            var receipt = new MemoryReviewReceipt { Id = Guid.NewGuid(), OrganizationId = organizationId, EmployeeId = employeeId,
                RecordKind = RecordKind, MemoryId = episodeId, ResultMemoryId = episodeId, OperationId = request.OperationId,
                ActorApplicationUserId = applicationUserId, ActorOrganizationUserId = actor, RequestHash = requestHash,
                Action = "queue-enrichment", PreviousRevision = source.Revision, ResultRevision = source.Revision, CreatedAt = clock.GetUtcNow() };
            db.MemoryReviewReceipts.Add(receipt);
            db.QueueAudit(new AuditEventWriteRequest("memory.ingestion.recovered.v1", "Memory", OrganizationId: organizationId,
                EntityType: "MemoryEpisode", EntityId: episodeId, Summary: "A human manager queued verified existing memory for durable enrichment.",
                MetadataJson: JsonSerializer.Serialize(new { receipt.Action, receipt.PreviousRevision, source.Job.SourceHash,
                    ReconciliationPolicy = reconciliation?.Policy, ExistingRecords = reconciliation?.RecordCount ?? 0 }),
                OccurredAt: receipt.CreatedAt, CorrelationId: request.OperationId.ToString("D"),
                Actor: new AuditActor("Human", ApplicationUserId: applicationUserId, OrganizationUserId: actor), EventId: receipt.Id,
                Employees: [new(employeeId, "Affected")], UseAmbientOrganization: false));
            await db.SaveChangesAsync(token);
            var result = await ResultAsync(receipt, false, applicationUserId, actor, token);
            await transaction.CommitAsync(token);
            return result;
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    private async Task<(MemoryEpisode Episode, MemoryEpisodeEnrichmentJob Job, long Revision)> ReadAsync(Guid organization, Guid employee,
        Guid id, Guid user, Guid actor, CancellationToken token)
    {
        var source = await memory.SnapshotRecoveryInputAsync(organization, employee, id, token);
        await MemoryEpisodeOperatorAuthorization.RequirePartitionAsync(db, organization, employee, actor, source.Episode.Partition, token);
        if (source.Episode.Source.Type is not ("agent-proposal" or "knowledge-transfer")) throw new InvalidOperationException("memory_ingestion_source_unavailable");
        return source;
    }

    private async Task<string?> BlockerAsync(MemoryEpisodeEnrichmentJob job, long revision, Guid user, Guid actor, CancellationToken token)
    {
        if (revision <= 0) return "memory_ingestion_source_unavailable";
        try { await memory.RequireEpisodeRecoverySourceAsync(job, user, actor, token); }
        catch (UnauthorizedAccessException) { return "memory_ingestion_authority_unavailable"; }
        catch (InvalidOperationException) { return "memory_ingestion_source_unavailable"; }
        if (await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().AnyAsync(x => x.EpisodeId == job.EpisodeId, token) ||
            await db.MemoryReviewReceipts.AsNoTracking().AnyAsync(x => x.RecordKind == RecordKind && x.MemoryId == job.EpisodeId, token) ||
            await db.MemoryEpisodeReextractionReceipts.AsNoTracking().AnyAsync(x => x.EpisodeId == job.EpisodeId, token))
            return "memory_ingestion_already_processed";
        return null;
    }
    private static string? ShapeBlocker(MemoryEpisode episode, string sourceJson) => Encoding.UTF8.GetByteCount(sourceJson) > 1_048_576 || !Enum.IsDefined(episode.Sensitivity) ||
        string.IsNullOrWhiteSpace(episode.Content) || episode.Content.Length > 32000 ||
        episode.Source.Type == "agent-proposal" && (episode.Sensitivity < MemorySensitivity.Personal || episode.OperationalReferences is not null)
        ? "memory_ingestion_source_unavailable" : null;

    private async Task<RecoverMemoryIngestionResponse> ResultAsync(MemoryReviewReceipt receipt, bool replay, Guid user, Guid actor, CancellationToken token)
    {
        var job = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleOrDefaultAsync(x => x.EpisodeId == receipt.MemoryId &&
            x.OrganizationId == receipt.OrganizationId && x.EmployeeId == receipt.EmployeeId && x.InputGeneration == 0, token)
            ?? throw new InvalidOperationException("memory_ingestion_recovery_job_unavailable");
        // A replay must validate the original queued snapshot, not a newly synthesized
        // snapshot of a source that changed after the reviewed operation.
        await memory.RequireEpisodeRecoverySourceAsync(job, user, actor, token);
        return new(receipt.Id, receipt.MemoryId, job.Id, job.Status.ToString(), replay);
    }
    private async Task BackendAsync(CancellationToken token)
    {
        if (!db.Database.IsNpgsql() || store is not PostgreSqlMemoryStore) throw new NotSupportedException();
        await store.InitializeAsync(token);
    }
    private static string Evidence(Guid org, Guid employee, Guid user, Guid actor, MemoryEpisodeEnrichmentJob job, long revision) =>
        Hash(new { org, employee, user, actor, job.SourceHash, job.ReviewerApplicationUserId, revision });
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value))).ToLowerInvariant();
    private static DbUpdateConcurrencyException Changed() => new("The ingestion review changed. Refresh before recovering.");
    private static Cursor Decode(string value, Guid org, Guid employee, Guid actor)
    {
        try
        {
            if (value.Length > 512) throw new FormatException();
            var position = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(value));
            if (position is null || position.Organization != org || position.Employee != employee || position.Actor != actor || position.Id == Guid.Empty)
                throw new FormatException();
            return position;
        }
        catch (Exception error) when (error is JsonException or FormatException)
        { throw new ArgumentException("Invalid ingestion recovery cursor.", nameof(value)); }
    }
}
