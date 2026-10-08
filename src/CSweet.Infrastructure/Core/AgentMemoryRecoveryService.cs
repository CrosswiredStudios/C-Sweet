using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryRecoveryService(CSweetDbContext db, TimeProvider clock, AgentMemoryService? memory = null) : IAgentMemoryRecoveryService
{
    public async Task<MemoryEnrichmentJobPageResponse> ListFailuresAsync(Guid organizationId, Guid employeeId,
        Guid applicationUserId, string? cursor = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        await RequireManagerAsync(organizationId, employeeId, applicationUserId, false, cancellationToken);
        // Both projections omit source text, accepted JSON and original reviewer identity.
        var query = Jobs(organizationId, employeeId).AsNoTracking().Where(x => x.Status == MemoryCaptureStatus.Failed)
            .Select(x => new { x.Id, ConversationId = x.ConversationMessage!.ConversationId, x.Status, x.Attempts,
                x.RetryGeneration, x.CreatedAt, x.NextAttemptAt, HasAcceptedExtraction = x.AcceptedExtractionJson != null,
                FailureCode = db.MemorySourceInvalidations.Any(e => e.SourceMessageId == x.ConversationMessageId)
                    ? "memory_source_changed" : db.MemoryCaptureExclusions.Any(e => e.SourceMessageId == x.ConversationMessageId)
                    ? "memory_capture_excluded" : x.LastError, JobKind = "conversation", EpisodeId = (Guid?)null })
            .Concat(db.MemoryEpisodeEnrichmentJobs.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
                x.EmployeeId == employeeId && x.Status == MemoryCaptureStatus.Failed && x.SupersededAt == null)
                .Select(x => new { x.Id, ConversationId = Guid.Empty, x.Status, x.Attempts, x.RetryGeneration,
                    x.CreatedAt, x.NextAttemptAt, HasAcceptedExtraction = x.AcceptedExtractionJson != null,
                    FailureCode = x.LastError, JobKind = "episode", EpisodeId = (Guid?)x.EpisodeId }));
        if (cursor is not null)
        {
            var position = DecodeCursor(cursor, organizationId, employeeId);
            query = query.Where(x => x.CreatedAt < position.CreatedAt ||
                (x.CreatedAt == position.CreatedAt && x.Id.CompareTo(position.Id) < 0));
        }
        var size = Math.Clamp(limit, 1, 100);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Take(size + 1).ToListAsync(cancellationToken);
        var items = rows.Take(size).Select(x => new MemoryEnrichmentJobResponse(x.Id, x.ConversationId,
            x.Status.ToString(), x.Attempts, x.RetryGeneration, x.CreatedAt, x.NextAttemptAt,
            x.HasAcceptedExtraction, SafeFailureCode(x.FailureCode), x.JobKind, x.EpisodeId)).ToList();
        var next = rows.Count > size ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new FailureCursor(organizationId, employeeId, items[^1].CreatedAt, items[^1].Id))) : null;
        return new(items, next);
    }

    public async Task<RetryMemoryEnrichmentResponse> RetryAsync(Guid organizationId, Guid employeeId, Guid jobId,
        Guid applicationUserId, RetryMemoryEnrichmentRequest request, CancellationToken cancellationToken = default)
    {
        if (request.OperationId == Guid.Empty || request.ExpectedRetryGeneration < 0 || request.ExpectedRetryGeneration == int.MaxValue)
            throw new ArgumentException("A retry identity and a valid retry generation are required.");
        // Authorize before looking up the job, and reauthorize under locks before changing it.
        await RequireManagerAsync(organizationId, employeeId, applicationUserId, false, cancellationToken);
        if (await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().AnyAsync(x => x.Id == jobId &&
            x.OrganizationId == organizationId && x.EmployeeId == employeeId, cancellationToken))
            return await RetryEpisodeAsync(organizationId, employeeId, jobId, applicationUserId, request, cancellationToken);
        if (!await Jobs(organizationId, employeeId).AnyAsync(x => x.Id == jobId, cancellationToken))
            throw new KeyNotFoundException("The memory job was not found.");
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"MemoryCaptureOutbox\" WHERE \"Id\" = {jobId} FOR UPDATE", cancellationToken);
            var actorId = await RequireManagerAsync(organizationId, employeeId, applicationUserId, true, cancellationToken);
            var item = await Jobs(organizationId, employeeId).SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken)
                ?? throw new KeyNotFoundException("The memory job was not found.");
            await db.Entry(item).ReloadAsync(cancellationToken);
            var receipt = await db.MemoryCaptureRetryReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.JobId == jobId && x.OperationId == request.OperationId, cancellationToken);
            if (receipt is not null)
            {
                if (receipt.OrganizationId != organizationId || receipt.EmployeeId != employeeId ||
                    receipt.ActorOrganizationUserId != actorId || receipt.ActorApplicationUserId != applicationUserId ||
                    receipt.PreviousGeneration != request.ExpectedRetryGeneration)
                    throw new DbUpdateConcurrencyException("This retry identity has already been used for a different request.");
                return new(await MapAsync(item, cancellationToken), true);
            }
            if (item.Status != MemoryCaptureStatus.Failed || item.RetryGeneration != request.ExpectedRetryGeneration ||
                item.LeaseToken.HasValue || item.CompletedAt.HasValue || item.EnrichedAt.HasValue)
                throw new DbUpdateConcurrencyException("The memory job changed. Refresh its state before retrying.");
            await RequireCurrentSourceAsync(item, cancellationToken);
            if (item.LastError is "memory_enrichment_source_invalidated" or "memory_enrichment_unverifiable_output" or "memory_enrichment_input_receipt_capacity" or "memory_capture_excluded" or "memory_source_changed" or "memory_source_suppressed" or MemoryCaptureErasure.FailureCode ||
                await db.MemorySourceInvalidations.AnyAsync(x => x.SourceMessageId == item.ConversationMessageId, cancellationToken) ||
                await db.MemoryCaptureExclusions.AnyAsync(x => x.SourceMessageId == item.ConversationMessageId, cancellationToken))
                throw new InvalidOperationException("The saved extraction cannot be safely retried.");

            var now = clock.GetUtcNow();
            var evidence = new MemoryCaptureRetryReceipt
            {
                Id = Guid.NewGuid(), JobId = item.Id, OperationId = request.OperationId,
                OrganizationId = organizationId, EmployeeId = employeeId,
                ActorApplicationUserId = applicationUserId, ActorOrganizationUserId = actorId,
                PreviousGeneration = item.RetryGeneration, RetryGeneration = item.RetryGeneration + 1,
                PreviousAttempts = item.Attempts, ReusesAcceptedExtraction = item.AcceptedExtractionJson is not null, CreatedAt = now
            };
            item.RetryGeneration = evidence.RetryGeneration;
            item.Attempts = 0;
            item.Status = MemoryCaptureStatus.Pending;
            item.NextAttemptAt = now;
            item.LastError = null;
            item.LeaseExpiresAt = null;
            // Keep AcceptedExtractionJson and ExtractionAcceptedAt unchanged: retry is not re-extraction.
            db.MemoryCaptureRetryReceipts.Add(evidence);
            db.QueueAudit(new AuditEventWriteRequest("memory.enrichment.retry-requested.v1", "Memory",
                OrganizationId: organizationId, EntityType: nameof(MemoryCaptureOutboxItem), EntityId: item.Id,
                Summary: "A human manager requested another attempt to process a failed memory job.",
                MetadataJson: JsonSerializer.Serialize(new { jobId, evidence.PreviousGeneration, evidence.RetryGeneration,
                    evidence.PreviousAttempts, evidence.ReusesAcceptedExtraction }),
                OccurredAt: now, CorrelationId: request.OperationId.ToString("D"),
                Actor: new AuditActor("Human", ApplicationUserId: applicationUserId, OrganizationUserId: actorId),
                EventId: evidence.Id, Employees: [new(employeeId, "Affected")], UseAmbientOrganization: false));
            await db.SaveChangesAsync(cancellationToken);
            var response = new RetryMemoryEnrichmentResponse(await MapAsync(item, cancellationToken), false);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return response;
        }
        catch
        {
            // This service owns its request scope; prevent failed mutations being flushed by later saves.
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private IQueryable<MemoryCaptureOutboxItem> Jobs(Guid organizationId, Guid employeeId) =>
        db.MemoryCaptureOutbox.Where(x => x.ConversationMessage!.Conversation!.OrganizationId == organizationId &&
            x.ConversationMessage.Conversation.AgentOrganizationUserId == employeeId);

    private Task<Guid> RequireManagerAsync(Guid organizationId, Guid employeeId, Guid applicationUserId,
        bool lockRows, CancellationToken token) =>
        MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, lockRows, token);

    private async Task RequireCurrentSourceAsync(MemoryCaptureOutboxItem item, CancellationToken token)
    {
        var source = await db.CoreConversationMessages.AsNoTracking().Where(x => x.Id == item.ConversationMessageId)
            .Select(x => new { x.Conversation!.OrganizationId, UserId = x.Conversation.InitiatedByOrganizationUserId,
                InstallationId = x.Conversation.AgentOrganizationUser!.AgentInstallationId }).SingleAsync(token);
        if (!source.InstallationId.HasValue)
            throw new InvalidOperationException("The agent installation must exist before retrying.");
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"CoreOrganizationUsers\" WHERE \"Id\" = {source.UserId} FOR SHARE", token);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"AgentInstallations\" WHERE \"Id\" = {source.InstallationId.Value} FOR SHARE", token);
        }
        if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.Id == source.UserId &&
                x.OrganizationId == source.OrganizationId && x.IsActive && x.ArchivedAt == null, token) ||
            !await db.AgentInstallations.AsNoTracking().AnyAsync(x => x.Id == source.InstallationId && x.IsEnabled &&
                x.BusinessId == source.OrganizationId.ToString(), token))
            throw new InvalidOperationException("The source participant and agent installation must be active before retrying.");
    }

    private async Task<MemoryEnrichmentJobResponse> MapAsync(MemoryCaptureOutboxItem item, CancellationToken token) =>
        new(item.Id, await db.CoreConversationMessages.Where(x => x.Id == item.ConversationMessageId)
            .Select(x => x.ConversationId).SingleAsync(token), item.Status.ToString(), item.Attempts, item.RetryGeneration,
            item.CreatedAt, item.NextAttemptAt, item.AcceptedExtractionJson is not null,
            await db.MemorySourceInvalidations.AnyAsync(x => x.SourceMessageId == item.ConversationMessageId, token)
                ? "memory_source_changed" : await db.MemoryCaptureExclusions.AnyAsync(x => x.SourceMessageId == item.ConversationMessageId, token)
                ? "memory_capture_excluded" : SafeFailureCode(item.LastError));

    private static string? SafeFailureCode(string? code) => code switch
    {
        null => null,
        "memory_enrichment_failed" or "memory_enrichment_timeout" or "memory_enrichment_interrupted" or
            "memory_enrichment_attempts_exhausted" or "memory_enrichment_source_invalidated" or
            "memory_enrichment_unverifiable_output" => code,
            "memory_enrichment_input_receipt_capacity" => code,
        "memory_capture_excluded" or "memory_source_changed" or "memory_source_suppressed" or MemoryCaptureErasure.FailureCode => code,
        "memory_enrichment_authority_revoked" or "memory_transfer_source_unavailable" => code,
        _ => "memory_enrichment_failed"
    };

    private sealed record FailureCursor(Guid OrganizationId, Guid EmployeeId, DateTimeOffset CreatedAt, Guid Id);
    private static FailureCursor DecodeCursor(string value, Guid organizationId, Guid employeeId)
    {
        try
        {
            if (value.Length > 512) throw new FormatException();
            var cursor = JsonSerializer.Deserialize<FailureCursor>(Convert.FromBase64String(value));
            if (cursor is null || cursor.OrganizationId != organizationId || cursor.EmployeeId != employeeId || cursor.Id == Guid.Empty)
                throw new FormatException();
            return cursor;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        { throw new ArgumentException("The memory recovery cursor is invalid.", nameof(value)); }
    }
}
