using System.Text;
using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReextractionService(CSweetDbContext db, IMemoryStore store,
    AgentMemoryService memory, TimeProvider clock) : IAgentMemoryReextractionService
{
    public const string PreservationPolicy = "preserve-existing-v1";

    public async Task<MemoryReextractionPreview> PreviewAsync(Guid organizationId, Guid employeeId, Guid jobId,
        Guid applicationUserId, CancellationToken token = default)
    {
        await BackendAsync(token);
        await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, false, token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            var (actor, original) = await LockAsync(organizationId, employeeId, jobId, applicationUserId, token);
            var candidate = await memory.PrepareReextractionAsync(original, applicationUserId, actor, clock.GetUtcNow(), token);
            var input = AgentMemoryService.ReextractionPreviewInput(candidate);
            var authority = await AuthorityHashAsync(organizationId, employeeId, actor, original.InstallationId, input.Episode.Partition, token);
            var blocked = Blocker(original);
            await transaction.CommitAsync(token);
            return new(jobId, original.EpisodeId, original.InputGeneration,
                Evidence(organizationId, employeeId, applicationUserId, actor, original, candidate.SourceHash, authority),
                input.Episode.Content, input.Episode.Source.Type, MemoryEpisodeOperatorAuthorization.Label(input.Episode.Partition),
                input.Episode.Sensitivity.ToString(), blocked is null, blocked, input.ExistingRecords,
                original.AcceptedExtractionJson is not null);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.LockNotAvailable)
        { throw Changed(error); }
    }

    public async Task<ReviewMemoryReextractionResponse> ReviewAsync(Guid organizationId, Guid employeeId, Guid jobId,
        Guid applicationUserId, ReviewMemoryReextractionRequest request, CancellationToken token = default)
    {
        if (jobId == Guid.Empty || request.OperationId == Guid.Empty || request.ExpectedInputGeneration is < 0 or > 8 ||
            request.EvidenceToken?.Length != 64 || request.PreservationPolicy != PreservationPolicy)
            throw new ArgumentException("A reviewed replacement and preservation choice are required.");
        await BackendAsync(token);
        await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, false, token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({organizationId.ToString("D") + ":" + request.OperationId.ToString("D")},0))", token);
            var (actor, original) = await LockAsync(organizationId, employeeId, jobId, applicationUserId, token);
            var now = clock.GetUtcNow();
            var candidate = await memory.PrepareReextractionAsync(original, applicationUserId, actor, now, token);
            var hash = MemoryEpisodeReextractionEvidence.Hash(new { organizationId, employeeId, jobId, applicationUserId, actor, request });
            var prior = await db.MemoryEpisodeReextractionReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.OperationId == request.OperationId, token);
            if (prior is not null)
            {
                if (prior.RequestHash != hash || prior.PreviousJobId != original.Id ||
                    prior.ActorApplicationUserId != applicationUserId || prior.ActorOrganizationUserId != actor ||
                    !await MemoryEpisodeReextractionEvidence.VerifyArchivedAsync(db, original, token)) throw Changed();
                var result = await ResultAsync(prior, true, token);
                await transaction.CommitAsync(token);
                return result;
            }
            var input = AgentMemoryService.ReextractionPreviewInput(candidate);
            var authority = await AuthorityHashAsync(organizationId, employeeId, actor, original.InstallationId, input.Episode.Partition, token);
            if (request.ExpectedInputGeneration != original.InputGeneration || request.EvidenceToken !=
                Evidence(organizationId, employeeId, applicationUserId, actor, original, candidate.SourceHash, authority)) throw Changed();
            if (Blocker(original) is { } blocked) throw new InvalidOperationException(blocked);
            var receipt = new MemoryEpisodeReextractionReceipt { Id = Guid.NewGuid(), OrganizationId = organizationId,
                EmployeeId = employeeId, EpisodeId = original.EpisodeId, PreviousJobId = original.Id, JobId = candidate.Id,
                InputGeneration = candidate.InputGeneration, OperationId = request.OperationId,
                ActorOrganizationUserId = actor, ActorApplicationUserId = applicationUserId, RequestHash = hash,
                PreviousJobHash = MemoryEpisodeReextractionEvidence.JobHash(original),
                PreviousAcceptedHash = MemoryEpisodeReextractionEvidence.AcceptedHash(original), CreatedAt = now };
            AgentMemoryService.BindReextractionReview(candidate, receipt.Id);
            receipt.SourceHash = candidate.SourceHash;
            // Release only the active-slot index. The old source, accepted bytes and attempts
            // stay frozen; receipt and replacement become visible in this same commit.
            if (await db.MemoryEpisodeEnrichmentJobs.Where(x => x.Id == original.Id && x.SupersededAt == null)
                .ExecuteUpdateAsync(x => x.SetProperty(j => j.SupersededAt, (DateTimeOffset?)now), token) != 1) throw Changed();
            db.MemoryEpisodeEnrichmentJobs.Add(candidate);
            db.MemoryEpisodeReextractionReceipts.Add(receipt);
            db.QueueAudit(new AuditEventWriteRequest("memory.enrichment.reextraction-reviewed.v1", "Memory",
                OrganizationId: organizationId, EntityType: nameof(MemoryEpisodeEnrichmentJob), EntityId: candidate.Id,
                Summary: "A human manager reviewed a new extraction while preserving retained evidence and existing records.",
                MetadataJson: JsonSerializer.Serialize(new { receipt.PreviousJobId, receipt.JobId, receipt.InputGeneration,
                    receipt.PreviousJobHash, receipt.PreviousAcceptedHash, receipt.SourceHash, input.ExistingRecords, request.PreservationPolicy }),
                OccurredAt: now, CorrelationId: request.OperationId.ToString("D"),
                Actor: new AuditActor("Human", ApplicationUserId: applicationUserId, OrganizationUserId: actor),
                EventId: receipt.Id, Employees: [new(employeeId, "Affected")], UseAmbientOrganization: false));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new(receipt.Id, original.Id, candidate.Id, original.EpisodeId, candidate.InputGeneration, "Pending", false);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.LockNotAvailable)
        { db.ChangeTracker.Clear(); throw Changed(error); }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    private async Task<(Guid Actor, MemoryEpisodeEnrichmentJob Job)> LockAsync(Guid org, Guid employee, Guid id, Guid user, CancellationToken token)
    {
        // Fail for refresh instead of waiting in reverse worker/append lock order.
        await db.Database.ExecuteSqlRawAsync("""
            LOCK TABLE "MemoryEpisodeEnrichmentJobs", "MemoryEpisodeReextractionReceipts", "MemoryEnrichmentProviderLeases"
              IN SHARE ROW EXCLUSIVE MODE NOWAIT;
            LOCK TABLE csweet_memory_episodes,csweet_memory_transfers,csweet_memory_revisions,csweet_memory_claims,
              csweet_memory_entities,csweet_memory_edges,csweet_memory_procedures,csweet_memory_blocks,csweet_memory_embeddings
              IN SHARE ROW EXCLUSIVE MODE NOWAIT;
            """, token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"MemoryEpisodeEnrichmentJobs\" WHERE \"Id\"={id} FOR UPDATE NOWAIT", token);
        var actor = await MemoryManagerAuthorization.RequireAsync(db, org, employee, user, true, token, true);
        var original = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == id && x.OrganizationId == org && x.EmployeeId == employee, token) ?? throw new KeyNotFoundException();
        if (Encoding.UTF8.GetByteCount(original.SourceJson) > 1_048_576 ||
            Encoding.UTF8.GetByteCount(original.AcceptedExtractionJson ?? "") > 1_048_576 ||
            await db.MemoryEnrichmentProviderLeases.AnyAsync(x => x.JobId == id && x.ExpiresAt > clock.GetUtcNow(), token))
            throw new InvalidOperationException("memory_reextraction_job_unavailable");
        return (actor, original);
    }
    private async Task<ReviewMemoryReextractionResponse> ResultAsync(MemoryEpisodeReextractionReceipt receipt, bool replay, CancellationToken token)
    {
        var job = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync(x => x.Id == receipt.JobId, token);
        return new(receipt.Id, receipt.PreviousJobId, job.Id, job.EpisodeId, job.InputGeneration,
            job.SupersededAt is null ? job.Status.ToString() : "Superseded", replay);
    }
    private static string? Blocker(MemoryEpisodeEnrichmentJob original) => original.SupersededAt is not null
        ? "memory_reextraction_already_replaced" : original.InputGeneration >= 8 ? "memory_reextraction_generation_limit" :
        original.Status is not (MemoryCaptureStatus.Failed or MemoryCaptureStatus.Completed) || original.LeaseToken is not null
        ? "memory_reextraction_job_unavailable" : null;
    private static string Evidence(Guid org, Guid employee, Guid user, Guid actor, MemoryEpisodeEnrichmentJob original,
        string sourceHash, string authority) => MemoryEpisodeReextractionEvidence.Hash(new { org, employee, user, actor,
            PreviousJobHash = MemoryEpisodeReextractionEvidence.JobHash(original), original.SupersededAt, sourceHash, authority });
    private async Task BackendAsync(CancellationToken token)
    {
        if (!db.Database.IsNpgsql() || store is not PostgreSqlMemoryStore) throw new NotSupportedException();
        await store.InitializeAsync(token);
    }
    private static DbUpdateConcurrencyException Changed(Exception? error = null) => new("The replacement review changed. Refresh before applying.", error);
}
