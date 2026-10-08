using System.Text;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.Infrastructure.Core;

/// <summary>
/// Host-owned extraction cleanup for an authorized memory erasure. The coordinator must
/// acquire this barrier before memory inventory, authorize the supplied historical source
/// identities, then commit StageAsync, store erasure, runtime/work cleanup and audit together.
/// This helper never owns a commit and is not an agent or HTTP capability.
/// </summary>
internal sealed partial class MemoryCaptureErasure(CSweetDbContext db)
{
    internal const string FailureCode = "memory_source_erased";
    private const int MaximumSources = 1024;
    private const int MaximumJobs = 4096;
    private const int MaximumBytes = 32 * 1024 * 1024;
    private Guid? transactionId;
    private readonly HashSet<Plan> prepared = [];

    internal sealed record Source(Guid MessageId, Guid ConversationId, Guid EmployeeId, Guid HumanId);
    internal sealed record Result(int InvalidatedSources, int ClearedJobs);
    internal sealed class Plan(MemoryCaptureErasure owner, Guid transaction, Guid organization,
        IReadOnlyList<Source> sources, IReadOnlyList<Guid> jobs)
    {
        internal MemoryCaptureErasure Owner { get; } = owner;
        internal Guid Transaction { get; } = transaction;
        internal Guid Organization { get; } = organization;
        internal IReadOnlyList<Source> Sources { get; } = sources;
        internal IReadOnlyList<Guid> Jobs { get; } = jobs;
    }

    internal async Task AcquireAsync(CancellationToken token)
    {
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is not { } transaction)
            throw new InvalidOperationException("Memory capture erasure requires a caller-owned PostgreSQL transaction.");
        if (db.ChangeTracker.HasChanges()) throw new InvalidOperationException("Acquire erasure locks before staging changes.");
        try
        {
            // Workers take job -> conversation/message -> memory locks. Lifecycle writers
            // take source/marker -> memory locks. NOWAIT also makes an accidentally late
            // acquisition fail rather than waiting in reverse order behind those writers.
            await db.Database.ExecuteSqlRawAsync("""
                LOCK TABLE "MemoryCaptureOutbox", "MemoryExtractionInputReceipts" IN EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "CoreConversations", "CoreConversationMessages" IN SHARE MODE NOWAIT;
                LOCK TABLE "MemorySourceInvalidations", "MemoryEnrichmentProviderLeases" IN SHARE ROW EXCLUSIVE MODE NOWAIT;
                """, token);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.LockNotAvailable)
        { throw new DbUpdateConcurrencyException("Memory capture is changing. Refresh the erasure review.", error); }
        transactionId = transaction.TransactionId;
    }

    internal async Task<Plan> PrepareAsync(Guid organizationId, IReadOnlyCollection<Source> sources, CancellationToken token)
    {
        var transaction = RequireTransaction();
        if (organizationId == Guid.Empty || sources.Count > MaximumSources || sources.Any(x =>
                x.MessageId == Guid.Empty || x.ConversationId == Guid.Empty || x.EmployeeId == Guid.Empty || x.HumanId == Guid.Empty))
            throw new ArgumentException("Invalid or unbounded erasure sources.");
        var selected = sources.Distinct().OrderBy(x => x.MessageId).ToArray();
        if (selected.Select(x => x.MessageId).Distinct().Count() != selected.Length)
            throw new InvalidOperationException("memory_erasure_lineage_review_required");
        var sourceIds = selected.Select(x => x.MessageId).ToArray();
        var conversationIds = selected.Select(x => x.ConversationId).Distinct().ToArray();
        foreach (var source in selected)
        {
            var message = await db.CoreConversationMessages.AsNoTracking().Where(x => x.Id == source.MessageId)
                .Select(x => new { x.ConversationId }).SingleOrDefaultAsync(token);
            if (message is not null && message.ConversationId != source.ConversationId) throw new UnauthorizedAccessException();
            var conversation = await db.CoreConversations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == source.ConversationId, token);
            if (conversation is not null && (conversation.OrganizationId != organizationId ||
                conversation.AgentOrganizationUserId != source.EmployeeId || conversation.InitiatedByOrganizationUserId != source.HumanId))
                throw new UnauthorizedAccessException();
            // Missing messages/parents are permitted only because the caller supplies
            // reviewed historical identities. Their independent marker survives deletion.
        }
        if (selected.Length == 0) return Remember(new(this, transaction, organizationId, Array.AsReadOnly(selected), Array.Empty<Guid>()));
        var receipts = await ReadDispatchEvidenceAsync(organizationId, null, sourceIds, token);
        var dispatched = receipts.Where(x => x.Evidence.Inputs.Any(i => sourceIds.Contains(i.Id)))
            .Select(x => x.Receipt.JobId).ToHashSet();
        var jobs = db.MemoryCaptureOutbox.AsNoTracking().Where(x =>
                x.ConversationMessage!.Conversation!.OrganizationId == organizationId || sourceIds.Contains(x.ConversationMessageId))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.ConversationMessageId, x.Status, x.AcceptedExtractionJson,
                x.ConversationMessage!.ConversationId, x.ConversationMessage.Conversation!.OrganizationId })
            .Take(MaximumJobs + 1);
        long bytes = 0; var count = 0; var affected = new List<Guid>();
        await foreach (var job in jobs.AsAsyncEnumerable().WithCancellation(token))
        {
            var size = job.AcceptedExtractionJson is null ? 0 : Encoding.UTF8.GetByteCount(job.AcceptedExtractionJson);
            bytes += size;
            if (++count > MaximumJobs || size > 1_048_576 || bytes > MaximumBytes) throw Limit();
            if (job.OrganizationId != organizationId) throw new UnauthorizedAccessException();
            var direct = sourceIds.Contains(job.ConversationMessageId);
            // A live provider request may have paired a source without saving its envelope
            // yet. Fence all currently processing jobs in these exact conversations.
            var inFlight = job.Status == MemoryCaptureStatus.Processing && conversationIds.Contains(job.ConversationId);
            var related = direct || inFlight || dispatched.Contains(job.Id);
            if (job.AcceptedExtractionJson is { } payload && !related)
            {
                var references = ReadSources(payload, job.ConversationMessageId, job.ConversationId);
                related = references.Any(sourceIds.Contains);
            }
            if (!related) continue;
            if (!conversationIds.Contains(job.ConversationId)) throw new UnauthorizedAccessException();
            affected.Add(job.Id);
        }
        return Remember(new(this, transaction, organizationId, Array.AsReadOnly(selected), affected.AsReadOnly()));
    }

    internal async Task<Result> StageAsync(Plan plan, DateTimeOffset now, CancellationToken token)
    {
        if (RequireTransaction() != plan.Transaction || !ReferenceEquals(plan.Owner, this) || !prepared.Contains(plan))
            throw new InvalidOperationException("The extraction erasure plan belongs to a different transaction.");
        if (await CheckRetentionAsync(plan, token) is { } blocked) throw new InvalidOperationException(blocked);
        // Raw updates must not coexist with stale tracked copies that a later SaveChanges
        // could restore. The coordinator uses a dedicated clean scope for this operation.
        if (db.ChangeTracker.Entries<MemoryCaptureOutboxItem>().Any(x => plan.Jobs.Contains(x.Entity.Id)) ||
            db.ChangeTracker.Entries<MemoryEnrichmentProviderLease>().Any(x => plan.Jobs.Contains(x.Entity.JobId)))
            throw new InvalidOperationException("Extraction erasure requires untracked queue rows.");
        var invalidated = 0;
        foreach (var source in plan.Sources)
            invalidated += await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MemorySourceInvalidations" ("SourceMessageId", "PreviousConversationId", "ReasonCode", "InvalidatedAt")
                VALUES ({source.MessageId}, {source.ConversationId}, {FailureCode}, {now})
                ON CONFLICT ("SourceMessageId") DO NOTHING
                """, token);
        var ids = plan.Jobs.ToArray();
        var cleared = await db.MemoryCaptureOutbox.Where(x => ids.Contains(x.Id) &&
            (x.LastError != FailureCode || x.AcceptedExtractionJson != null || x.ExtractionAcceptedAt != null ||
             x.LeaseToken != null || x.LeaseExpiresAt != null || x.Status != MemoryCaptureStatus.Failed)).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.AcceptedExtractionJson, (string?)null)
            .SetProperty(x => x.ExtractionAcceptedAt, (DateTimeOffset?)null)
            .SetProperty(x => x.LeaseToken, (Guid?)null)
            .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
            .SetProperty(x => x.RetryGeneration, x => x.RetryGeneration + 1)
            .SetProperty(x => x.Status, MemoryCaptureStatus.Failed)
            .SetProperty(x => x.LastError, FailureCode), token);
        await db.MemoryEnrichmentProviderLeases.Where(x => ids.Contains(x.JobId)).ExecuteDeleteAsync(token);
        return new(invalidated, cleared);
    }

    private Plan Remember(Plan plan) { prepared.Add(plan); return plan; }

    private Guid RequireTransaction() => transactionId is { } id && db.Database.CurrentTransaction?.TransactionId == id ? id :
        throw new InvalidOperationException("Acquire extraction erasure locks in the current transaction first.");

    private static Guid[] ReadSources(string payload, Guid primary, Guid conversation)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var version = Property(root, "SchemaVersion", "schemaVersion").GetInt32();
            var evidence = Property(root, "Sources", "sources");
            var messages = Property(evidence, "Messages", "messages").EnumerateArray()
                .Select(x => Property(x, "Id", "id").GetGuid()).ToArray();
            if (version is not (2 or 3) || messages.Length is < 1 or > 2 || (version == 2 && messages.Length != 1) ||
                messages[0] != primary || messages.Any(x => x == Guid.Empty) || messages.Distinct().Count() != messages.Length ||
                Property(Property(root, "Episode", "episode"), "Id", "id").GetGuid() != primary ||
                Property(evidence, "ConversationId", "conversationId").GetGuid() != conversation)
                throw new JsonException();
            return messages;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new InvalidOperationException("memory_erasure_lineage_review_required", error); }
    }

    private static JsonElement Property(JsonElement value, string name, string alternate)
    {
        var first = value.TryGetProperty(name, out var result);
        var second = value.TryGetProperty(alternate, out var other);
        if (first == second) throw new JsonException(); // Missing or ambiguous envelope.
        return first ? result : other;
    }

    private static InvalidOperationException Limit() => new("memory_erasure_scan_limit");
}
