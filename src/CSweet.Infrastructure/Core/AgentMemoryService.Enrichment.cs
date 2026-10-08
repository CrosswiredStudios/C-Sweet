using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryService
{
    private const int MaximumEnrichmentAttempts = 10;
    private static readonly TimeSpan EnrichmentLeaseDuration = TimeSpan.FromMinutes(5);
    private sealed record AcceptedMemoryExtraction(int SchemaVersion, MemoryEpisode Episode,
        MemoryEnrichment Enrichment, string ExtractorVersion)
    {
        public EnrichmentSources? Sources { get; init; }
        public EnrichmentProvider? Provider { get; init; }
    }

    private async Task<bool> ProcessMessageAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var item = await db.MemoryCaptureOutbox.SingleOrDefaultAsync(x => x.ConversationMessageId == messageId, cancellationToken);
        if (item is null) return false;
        await db.Entry(item).ReloadAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (!((item.Status == MemoryCaptureStatus.Pending && item.NextAttemptAt <= now) ||
              (item.Status == MemoryCaptureStatus.Processing && (item.LeaseExpiresAt == null || item.LeaseExpiresAt <= now))))
            return false;

        var token = Guid.NewGuid();
        item.LeaseToken = token;
        item.LeaseExpiresAt = now + EnrichmentLeaseDuration;
        item.Status = item.Attempts >= MaximumEnrichmentAttempts ? MemoryCaptureStatus.Failed : MemoryCaptureStatus.Processing;
        item.LastAttemptAt = now;
        if (item.Status == MemoryCaptureStatus.Processing) item.Attempts++;
        else
        {
            item.LastError = "memory_enrichment_attempts_exhausted";
            item.LeaseToken = null;
            item.LeaseExpiresAt = null;
        }
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            await db.Entry(item).ReloadAsync(cancellationToken);
            return false;
        }
        if (item.Status == MemoryCaptureStatus.Failed) return false;
        EnrichmentQueueAge.Record(Math.Max(0, (now - item.CreatedAt).TotalSeconds));

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(EnrichmentLeaseDuration - TimeSpan.FromSeconds(30));
        try
        {
            var (episode, turnId) = await CaptureEpisodeAsync(messageId, attempt.Token);
            AcceptedMemoryExtraction? accepted = null;
            if (episode.Source.Type == "user")
            {
                if (item.AcceptedExtractionJson is null)
                {
                    await EnsureEnrichmentContextAsync(messageId, attempt.Token);
                    var paired = await PairEpisodeAsync(episode, attempt.Token);
                    accepted = await ExtractEpisodeAsync(paired, ct => RequireLeaseAsync(item, token, ct), item.Id, token, attempt.Token);
                    var json = JsonSerializer.Serialize(accepted);
                    if (Encoding.UTF8.GetByteCount(json) > 1_048_576)
                        throw new InvalidOperationException("Accepted memory extraction exceeds the storage limit.");
                    await RequireLeaseAsync(item, token, attempt.Token);
                    await ValidateSourcesAsync(accepted.Episode, accepted.Sources, store, attempt.Token);
                    item.AcceptedExtractionJson = json;
                    item.ExtractionAcceptedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(attempt.Token);
                }
                else accepted = JsonSerializer.Deserialize<AcceptedMemoryExtraction>(item.AcceptedExtractionJson)
                    ?? throw new InvalidOperationException("Accepted memory extraction is missing.");
            }

            await CommitExtractionAsync(item, token, episode, accepted, attempt.Token);
            if (accepted is not null)
            {
                EnrichedEpisodes.Add(1);
                await RecordEnrichmentTraceAsync(turnId, "enrichment.completed", "completed", "Turn memory enriched",
                    "Accepted memory extraction was applied durably.", attempt.Token);
            }
            return true;
        }
        catch (Exception exception)
        {
            // Reload even after an ambiguous commit: a committed job must never be requeued.
            await ReleaseFailedAttemptAsync(item, token, exception, cancellationToken.IsCancellationRequested);
            if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
            if (exception is not MemoryLeaseLostException)
                logger.LogWarning("Memory enrichment attempt failed for message {MessageId} ({FailureType}).", messageId, exception.GetType().Name);
            return false;
        }
    }

    private async Task<EnrichmentInput> PairEpisodeAsync(MemoryEpisode episode, CancellationToken cancellationToken)
    {
        var message = await db.CoreConversationMessages.AsNoTracking().SingleAsync(x => x.Id == episode.Id, cancellationToken);
        var assistants = db.CoreConversationMessages.AsNoTracking().Where(x => x.ConversationId == message.ConversationId && x.Role == ConversationRole.Assistant &&
            !db.MemoryCaptureExclusions.Any(e => e.SourceMessageId == x.Id) &&
            !db.MemorySourceInvalidations.Any(e => e.SourceMessageId == x.Id));
        assistants = message.ChatTurnId.HasValue
            ? assistants.Where(x => x.ChatTurnId == message.ChatTurnId)
            : assistants.Where(x => x.CreatedAt > message.CreatedAt);
        var paired = await assistants.Where(x => !db.MemoryCaptureOutbox.Any(o => o.ConversationMessageId == x.Id &&
                o.Status == MemoryCaptureStatus.Completed && o.EpisodeCapturedAt == null && o.LastError != null))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        var includeAssistant = !string.IsNullOrWhiteSpace(paired?.Content);
        if (includeAssistant)
        {
            // Capture every input before dispatch. Its own durable marker prevents a retry
            // from recreating an episode removed after this extraction observed it.
            await EnsurePairedCaptureJobAsync(paired!, cancellationToken);
            await CaptureEpisodeAsync(paired!.Id, cancellationToken);
        }
        var sources = new EnrichmentSources(message.ConversationId,
            ReadGuid(episode.Metadata, "installationId") ?? throw new MemorySourceInvalidatedException(),
            includeAssistant ? [Fingerprint(message), Fingerprint(paired!)] : [Fingerprint(message)]);
        return new EnrichmentInput(includeAssistant ? episode with
        {
            Content = $"<user_turn>\n{episode.Content}\n</user_turn>\n<assistant_turn>\n{paired!.Content}\n</assistant_turn>"
        } : episode, sources);
    }

    private async Task EnsurePairedCaptureJobAsync(ConversationMessage message, CancellationToken token)
    {
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MemoryCaptureOutbox" ("Id", "ConversationMessageId", "Status", "Attempts", "CreatedAt", "NextAttemptAt")
                VALUES ({Guid.NewGuid()}, {message.Id}, {"Pending"}, {0}, {message.CreatedAt}, {DateTimeOffset.UtcNow})
                ON CONFLICT ("ConversationMessageId") DO NOTHING
                """, token);
        else if (!await db.MemoryCaptureOutbox.AnyAsync(x => x.ConversationMessageId == message.Id, token))
        {
            db.MemoryCaptureOutbox.Add(new MemoryCaptureOutboxItem
            {
                Id = Guid.NewGuid(), ConversationMessageId = message.Id, Status = MemoryCaptureStatus.Pending,
                CreatedAt = message.CreatedAt, NextAttemptAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(token);
        }
    }

    private static bool HasVerifiableEnvelope(AcceptedMemoryExtraction accepted) =>
        accepted.Sources?.Messages is { Length: >= 1 and <= 2 } && accepted.Provider is not null &&
        (accepted.SchemaVersion == 3 || (accepted.SchemaVersion == 2 && accepted.Sources.Messages is { Length: 1 }));

    private async Task EnsureEnrichmentContextAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var conversationId = await db.CoreConversationMessages.AsNoTracking().Where(x => x.Id == messageId)
            .Select(x => (Guid?)x.ConversationId).SingleOrDefaultAsync(cancellationToken);
        if (conversationId is null || await LoadConversationContextAsync(conversationId.Value, cancellationToken) is null)
            throw new MemorySourceInvalidatedException();
    }

    private async Task RequireLeaseAsync(MemoryCaptureOutboxItem item, Guid token, CancellationToken cancellationToken)
    {
        await db.Entry(item).ReloadAsync(cancellationToken);
        if (db.Entry(item).State == EntityState.Detached || item.Status != MemoryCaptureStatus.Processing ||
            item.LeaseToken != token || item.LeaseExpiresAt <= DateTimeOffset.UtcNow || item.LeaseExpiresAt is null)
            throw new MemoryLeaseLostException();
    }

    private async Task CommitExtractionAsync(MemoryCaptureOutboxItem item, Guid token, MemoryEpisode source,
        AcceptedMemoryExtraction? accepted, CancellationToken cancellationToken)
    {
        // Production memory and the outbox use the same PostgreSQL database. Lock the job,
        // then enlist every derived write and completion in that exact transaction.
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        PostgreSqlMemoryStore? enlisted = null;
        try
        {
            if (transaction is not null)
            {
                if (transaction.GetDbTransaction() is not NpgsqlTransaction postgres)
                    throw new InvalidOperationException("Durable memory enrichment requires PostgreSQL.");
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"MemoryCaptureOutbox\" WHERE \"Id\" = {item.Id} FOR UPDATE", cancellationToken);
                enlisted = new PostgreSqlMemoryStore(postgres);
            }
            await RequireLeaseAsync(item, token, cancellationToken);
            await EnsureEnrichmentContextAsync(source.Id, cancellationToken);
            if (accepted is not null)
            {
                if (!HasVerifiableEnvelope(accepted))
                    throw new MemorySourceInvalidatedException("memory_enrichment_unverifiable_output");
                if (accepted.Episode.Id != source.Id || accepted.Episode.Partition != source.Partition ||
                    accepted.Episode.Checksum != source.Checksum)
                    throw new MemorySourceInvalidatedException();
                var target = (IMemoryStore?)enlisted ?? store; // In-memory EF is used only by unit tests.
                if (enlisted is not null) await LockSourcesAsync(accepted.Episode, accepted.Sources!, cancellationToken);
                await ValidateSourcesAsync(accepted.Episode, accepted.Sources, target, cancellationToken);
                await ApplyExtractionAsync(target, accepted, item.ExtractionAcceptedAt!.Value, cancellationToken);
            }
            item.Status = MemoryCaptureStatus.Completed;
            item.CompletedAt = DateTimeOffset.UtcNow;
            item.EnrichedAt = accepted is null ? null : item.CompletedAt;
            item.LastError = null;
            item.LeaseToken = null;
            item.LeaseExpiresAt = null;
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        finally { if (enlisted is not null) await enlisted.DisposeAsync(); }
    }

    private async Task ReleaseFailedAttemptAsync(MemoryCaptureOutboxItem item, Guid token, Exception exception, bool interrupted)
    {
        // Cancellation of an interactive worker is recoverable; release with a separate bounded token.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var transaction = db.Database.IsNpgsql()
                ? await db.Database.BeginTransactionAsync(cleanup.Token) : null;
            if (transaction is not null)
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"MemoryCaptureOutbox\" WHERE \"Id\" = {item.Id} FOR UPDATE", cleanup.Token);
            await db.Entry(item).ReloadAsync(cleanup.Token);
            if (db.Entry(item).State == EntityState.Detached || item.Status != MemoryCaptureStatus.Processing || item.LeaseToken != token) return;
            if (interrupted || exception is MemoryProviderBusyException) item.Attempts = Math.Max(0, item.Attempts - 1);
            if (exception is MemoryProviderBusyException) ProviderDeferrals.Add(1);
            item.Status = exception is MemorySourceInvalidatedException or MemoryInputReceiptCapacityException || item.Attempts >= MaximumEnrichmentAttempts
                ? MemoryCaptureStatus.Failed : MemoryCaptureStatus.Pending;
            item.LastError = exception is MemoryProviderBusyException ? "memory_provider_busy" : exception is MemoryInputReceiptCapacityException ? "memory_enrichment_input_receipt_capacity" : exception is MemorySourceInvalidatedException invalid ? invalid.Code : interrupted ? "memory_enrichment_interrupted" : exception is OperationCanceledException
                ? "memory_enrichment_timeout" : "memory_enrichment_failed";
            item.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(exception is MemoryProviderBusyException ? 15 : Math.Min(300, Math.Pow(2, item.Attempts)));
            item.LeaseToken = null;
            item.LeaseExpiresAt = null;
            if (exception is MemorySourceInvalidatedException)
                await ExcludeCaptureAsync(item, cleanup.Token);
            await db.SaveChangesAsync(cleanup.Token);
            if (transaction is not null) await transaction.CommitAsync(cleanup.Token);
            RetryFailures.Add(1);
            var turnId = await db.CoreConversationMessages.Where(x => x.Id == item.ConversationMessageId)
                .Select(x => x.ChatTurnId).SingleOrDefaultAsync(cleanup.Token);
            await RecordEnrichmentTraceAsync(turnId, "enrichment.retry", item.Status == MemoryCaptureStatus.Failed ? "failed" : "warning",
                item.Status == MemoryCaptureStatus.Failed ? "Memory enrichment needs attention" : "Memory enrichment will retry",
                item.LastError, cleanup.Token);
        }
        catch (Exception cleanupError)
        {
            // A failed exclusion/audit write must not leak into a later job's SaveChanges.
            db.ChangeTracker.Clear();
            // The persisted lease still expires if cleanup loses the database or another worker wins.
            logger.LogWarning(cleanupError, "Could not release memory enrichment lease for {MessageId}.", item.ConversationMessageId);
        }
    }

    private async Task ExcludeCaptureAsync(MemoryCaptureOutboxItem item, CancellationToken token)
    {
        // Serialize exclusion with any apply transaction holding shared input-message locks.
        // Future reviewed exclusion writers must use this same source-row locking protocol.
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"CoreConversationMessages\" WHERE \"Id\" = {item.ConversationMessageId} FOR UPDATE", token);
        if (await db.MemoryCaptureExclusions.AnyAsync(x => x.SourceMessageId == item.ConversationMessageId, token)) return;
        var owner = await db.CoreConversationMessages.AsNoTracking().Where(x => x.Id == item.ConversationMessageId)
            .Select(x => new { x.Conversation!.OrganizationId, EmployeeId = x.Conversation.AgentOrganizationUserId })
            .SingleAsync(token);
        if (owner.EmployeeId is not Guid employeeId) throw new MemorySourceInvalidatedException();
        var exclusion = new MemoryCaptureExclusion
        {
            SourceMessageId = item.ConversationMessageId, OrganizationId = owner.OrganizationId, EmployeeId = employeeId,
            TriggerJobId = item.Id, ReasonCode = item.LastError!, ExcludedAt = DateTimeOffset.UtcNow
        };
        db.MemoryCaptureExclusions.Add(exclusion);
        db.QueueAudit(new AuditEventWriteRequest("memory.capture.excluded.v1", "Memory",
            OrganizationId: owner.OrganizationId, EntityType: nameof(MemoryCaptureExclusion), EntityId: exclusion.SourceMessageId,
            Summary: "Memory capture was excluded because its source evidence could not be validated.",
            MetadataJson: JsonSerializer.Serialize(new { exclusion.SourceMessageId, exclusion.TriggerJobId, exclusion.ReasonCode }),
            OccurredAt: exclusion.ExcludedAt, Actor: new AuditActor("System"),
            EventId: Guid.NewGuid(), Employees: [new(employeeId, "Affected")], UseAmbientOrganization: false));
        // Caller saves exclusion, audit outbox, and terminal job state in one atomic SaveChanges.
    }

    private async Task RecordEnrichmentTraceAsync(Guid? turnId, string eventType, string status, string title,
        string summary, CancellationToken cancellationToken)
    {
        try
        {
            await AppendTurnMemoryTraceAsync(turnId, eventType, status, title, summary, cancellationToken);
            if (status == "failed" && turnId is Guid failedTurnId)
            {
                var turn = await db.ChatTurns.SingleOrDefaultAsync(x => x.Id == failedTurnId, cancellationToken);
                if (turn?.Status == ChatTurnStatus.Completed) turn.Status = ChatTurnStatus.CompletedWithWarnings;
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            foreach (var entry in db.ChangeTracker.Entries<ChatTurnTraceEvent>()
                .Where(x => x.State == EntityState.Added && x.Entity.Category == "memory").ToList())
                entry.State = EntityState.Detached;
            logger.LogWarning(exception, "Could not record memory trace for turn {TurnId}.", turnId);
        }
    }

    private sealed class MemoryLeaseLostException : Exception;
}
