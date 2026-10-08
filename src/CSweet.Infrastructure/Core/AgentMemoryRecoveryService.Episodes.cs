using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryRecoveryService
{
    private async Task<RetryMemoryEnrichmentResponse> RetryEpisodeAsync(Guid organization, Guid employee, Guid jobId,
        Guid user, RetryMemoryEnrichmentRequest request, CancellationToken token)
    {
        if (!db.Database.IsNpgsql() || memory is null) throw new NotSupportedException("Episode recovery requires the durable memory pipeline.");
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"MemoryEpisodeEnrichmentJobs\" WHERE \"Id\"={jobId} FOR UPDATE", token);
            var actor = await MemoryManagerAuthorization.RequireAsync(db, organization, employee, user, true, token, failOnLockContention: true);
            var item = await db.MemoryEpisodeEnrichmentJobs.SingleOrDefaultAsync(x => x.Id == jobId &&
                x.OrganizationId == organization && x.EmployeeId == employee, token) ?? throw new KeyNotFoundException();
            await db.Entry(item).ReloadAsync(token);
            // Replays are authorized against the current input too. They report current state
            // and never reset a newer failure, a lease or completed work.
            await memory.RequireEpisodeRecoverySourceAsync(item, user, actor, token);
            var receipt = await db.MemoryEpisodeRetryReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.JobId == jobId && x.OperationId == request.OperationId, token);
            if (receipt is not null)
            {
                if (receipt.OrganizationId != organization || receipt.EmployeeId != employee ||
                    receipt.ActorOrganizationUserId != actor || receipt.ActorApplicationUserId != user ||
                    receipt.PreviousGeneration != request.ExpectedRetryGeneration || receipt.SourceHash != item.SourceHash)
                    throw new DbUpdateConcurrencyException("This retry identity has already been used for a different request.");
                return new(MapEpisode(item), true);
            }
            if (item.Status != MemoryCaptureStatus.Failed || item.RetryGeneration != request.ExpectedRetryGeneration ||
                item.LeaseToken.HasValue || item.CompletedAt.HasValue)
                throw new DbUpdateConcurrencyException("The memory job changed. Refresh its state before retrying.");
            if (item.LastError is "memory_enrichment_source_invalidated" or "memory_enrichment_unverifiable_output" or
                "memory_enrichment_input_receipt_capacity" or "memory_enrichment_authority_revoked" or "memory_transfer_source_unavailable" ||
                item.AcceptedExtractionJson is null && await db.MemoryEpisodeExtractionReceipts.CountAsync(x => x.JobId == jobId, token) >= 64)
                throw new InvalidOperationException("The saved extraction cannot be safely retried.");
            var now = clock.GetUtcNow();
            var evidence = new MemoryEpisodeRetryReceipt
            {
                Id = Guid.NewGuid(), JobId = jobId, OperationId = request.OperationId, OrganizationId = organization,
                EmployeeId = employee, ActorApplicationUserId = user, ActorOrganizationUserId = actor,
                PreviousGeneration = item.RetryGeneration, RetryGeneration = item.RetryGeneration + 1,
                PreviousAttempts = item.Attempts, ReusesAcceptedExtraction = item.AcceptedExtractionJson is not null,
                SourceHash = item.SourceHash, CreatedAt = now
            };
            item.RetryGeneration = evidence.RetryGeneration;
            item.Attempts = 0;
            item.Status = MemoryCaptureStatus.Pending;
            item.NextAttemptAt = now;
            item.LastError = null;
            item.LeaseExpiresAt = null;
            db.MemoryEpisodeRetryReceipts.Add(evidence);
            db.QueueAudit(new AuditEventWriteRequest("memory.enrichment.retry-requested.v1", "Memory",
                OrganizationId: organization, EntityType: nameof(MemoryEpisodeEnrichmentJob), EntityId: jobId,
                Summary: "A human manager requested another attempt to process a failed episode memory job.",
                MetadataJson: JsonSerializer.Serialize(new { jobId, evidence.PreviousGeneration, evidence.RetryGeneration,
                    evidence.PreviousAttempts, evidence.ReusesAcceptedExtraction, evidence.SourceHash }),
                OccurredAt: now, CorrelationId: request.OperationId.ToString("D"),
                Actor: new AuditActor("Human", ApplicationUserId: user, OrganizationUserId: actor),
                EventId: evidence.Id, Employees: [new(employee, "Affected")], UseAmbientOrganization: false));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new(MapEpisode(item), false);
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    private static MemoryEnrichmentJobResponse MapEpisode(MemoryEpisodeEnrichmentJob item) =>
        new(item.Id, Guid.Empty, item.Status.ToString(), item.Attempts, item.RetryGeneration, item.CreatedAt,
            item.NextAttemptAt, item.AcceptedExtractionJson is not null, SafeFailureCode(item.LastError), "episode", item.EpisodeId);
}
