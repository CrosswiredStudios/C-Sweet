using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using CSweet.Domain.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.Infrastructure.Core;

internal sealed partial class MemoryCaptureErasure
{
    // Shared by preview and staging. Rechecking at staging also protects callers that
    // inspected retention before acquiring the memory-store write barrier.
    internal async Task<string?> CheckRetentionAsync(Plan plan, CancellationToken token)
    {
        if (RequireTransaction() != plan.Transaction || !ReferenceEquals(plan.Owner, this) || !prepared.Contains(plan))
            throw new InvalidOperationException("The extraction erasure plan belongs to a different transaction.");
        var ids = plan.Jobs.ToArray();
        var jobs = await db.MemoryCaptureOutbox.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.ConversationMessageId, x.AcceptedExtractionJson, x.Status, x.LeaseToken, x.RetryGeneration, x.LastError,
                x.ExtractionAcceptedAt, x.LeaseExpiresAt,
                x.ConversationMessage!.ConversationId }).ToListAsync(token);
        if (jobs.Count != ids.Length) throw new DbUpdateConcurrencyException("Extraction jobs changed. Refresh the erasure review.");
        var inputs = new HashSet<(MemoryPartition Partition, AgentMemoryService.ExtractionErasureInput Source, Guid Conversation, Guid Installation)>();
        var receipts = await ReadDispatchEvidenceAsync(plan.Organization, ids, null, token);
        long bytes = 0;
        foreach (var job in jobs)
        {
            // Settled fences retain only content-free receipts. Replays must not
            // demand a live copy of the source that the first transaction erased.
            if (job.LastError == FailureCode && job.Status == MemoryCaptureStatus.Failed && job.AcceptedExtractionJson is null &&
                job.ExtractionAcceptedAt is null && job.LeaseToken is null && job.LeaseExpiresAt is null) continue;
            var dispatches = receipts.Where(x => x.Receipt.JobId == job.Id).ToArray();
            if (job.AcceptedExtractionJson is null && job.Status == MemoryCaptureStatus.Processing &&
                !dispatches.Any(x => x.Receipt.LeaseToken == job.LeaseToken && x.Receipt.RetryGeneration == job.RetryGeneration))
                return "memory_erasure_capture_retention_review_required";
            var evidenceItems = dispatches.Select(x => x.Evidence).ToList();
            if (job.AcceptedExtractionJson is { } json)
            {
                var size = Encoding.UTF8.GetByteCount(json); bytes += size;
                if (size > 1_048_576 || bytes > MaximumBytes) throw Limit();
                try { evidenceItems.Add(AgentMemoryService.InspectExtractionForErasure(json, job.ConversationMessageId, job.ConversationId)); }
                catch (InvalidOperationException error) when (error.Message == "memory_erasure_capture_lineage_review_required")
                { return error.Message; }
            }
            if (evidenceItems.Count == 0) continue;
            var owners = plan.Sources.Where(x => x.ConversationId == job.ConversationId)
                .Select(x => (x.EmployeeId, x.HumanId)).Distinct().ToArray();
            if (owners.Length != 1) throw new UnauthorizedAccessException();
            var owner = owners[0];
            var partition = EmployeeMemoryNamespaces.UserRelationship(plan.Organization.ToString("D"),
                owner.EmployeeId.ToString("D"), owner.HumanId.ToString("D"), "csweet").Partition;
            foreach (var evidence in evidenceItems)
            {
                if (evidence.Partition != partition || !await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                        x.OrganizationId == plan.Organization && x.Id == owner.EmployeeId && x.AgentInstallationId == evidence.InstallationId, token))
                    throw new UnauthorizedAccessException();
                foreach (var input in evidence.Inputs) inputs.Add((partition, input, job.ConversationId, evidence.InstallationId));
            }
            if (inputs.Count > MaximumSources) throw Limit();
        }
        if (inputs.Count == 0) return null;
        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                LOCK TABLE csweet_memory_episodes, csweet_memory_transfers IN SHARE ROW EXCLUSIVE MODE NOWAIT;
                """, token);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.LockNotAvailable)
        { throw new DbUpdateConcurrencyException("Input retention is changing. Refresh the erasure review.", error); }
        await using var store = new PostgreSqlMemoryStore((NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());
        var held = false; var snapshots = 0; long historyBytes = 0;
        foreach (var input in inputs)
        {
            var id = input.Source.Id;
            var messageConversation = await db.CoreConversationMessages.AsNoTracking().Where(x => x.Id == id)
                .Select(x => (Guid?)x.ConversationId).SingleOrDefaultAsync(token);
            if (messageConversation is not null && messageConversation != input.Conversation) throw new UnauthorizedAccessException();
            var episode = await store.GetEpisodeAsync(input.Partition, id, token);
            if (episode is null) return "memory_erasure_capture_retention_review_required";
            if (episode.Source is null || episode.Source.Id != id.ToString("D") || episode.Source.Type is not ("user" or "assistant") ||
                episode.Metadata?.GetValueOrDefault("conversationId") != input.Conversation.ToString("D"))
                return "memory_erasure_capture_lineage_review_required";
            bool Matches(MemoryEpisode? version) => version is not null && version.Content is not null && version.Id == id && version.Partition == input.Partition &&
                version.Source?.Id == id.ToString("D") && version.Source.Type == (input.Source.Role == ConversationRole.User ? "user" : "assistant") &&
                version.OccurredAt == input.Source.CreatedAt && version.Metadata?.GetValueOrDefault("conversationId") == input.Conversation.ToString("D") &&
                version.Metadata?.GetValueOrDefault("messageId") == id.ToString("D") &&
                version.Metadata?.GetValueOrDefault("installationId") == input.Installation.ToString("D") &&
                string.Equals(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(version.Content))), input.Source.Checksum, StringComparison.OrdinalIgnoreCase);
            var matched = Matches(episode);
            long after = 0;
            while (!matched)
            {
                var page = await store.ReadRevisionsAsync(input.Partition, MemoryRecordKind.Episode, id, after, 200, token);
                foreach (var snapshot in page.Items)
                {
                    historyBytes += Encoding.UTF8.GetByteCount(snapshot.PayloadJson);
                    if (++snapshots > 16384 || historyBytes > MaximumBytes) throw Limit();
                    MemoryEpisode? version;
                    try { version = JsonSerializer.Deserialize<MemoryEpisode>(snapshot.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
                    catch (JsonException) { return "memory_erasure_capture_lineage_review_required"; }
                    if (Matches(version)) { matched = true; break; }
                }
                if (matched || page.NextAfterRevision is null) break;
                if (page.NextAfterRevision <= after) throw Limit();
                after = page.NextAfterRevision.Value;
            }
            if (!matched) return "memory_erasure_capture_lineage_review_required";
            held |= episode.LegalHold;
        }
        return held ? "memory_legal_hold_prevents_deletion" : null;
    }
}
