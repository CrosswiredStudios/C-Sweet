using System.Text;
using System.Security.Cryptography;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReviewService
{
    private sealed record ErasureRoot(MemoryPartition Partition, Guid Id);
    private sealed record PreparedErasure(MemoryErasureImpactResponse Impact, MemoryErasurePreview Inventory,
        IReadOnlyList<ErasureRoot> Roots, ErasureExecution? Execution, IReadOnlyList<ChatTurn> Turns,
        IReadOnlyList<MemoryPartition> Audiences);

    private async Task AcquireErasureBarriersAsync(MemoryWorkErasure work, MemoryCaptureErasure capture, CancellationToken token)
    {
        await work.AcquireAsync(token);
        await capture.AcquireAsync(token);
        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                LOCK TABLE "ChatTurns", "ChatTurnTraceEvents", "AgentMemoryRecallUses", "ComputeAuditOutbox", "CommunicationDeliveries" IN EXCLUSIVE MODE NOWAIT;
                LOCK TABLE "AuditEvents", "AuditEventPayloads" IN SHARE MODE NOWAIT;
                """, token);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.LockNotAvailable)
        { throw new DbUpdateConcurrencyException("Chat diagnostics are changing. Refresh the erasure review.", error); }
        await AcquireSuppressionBarrierAsync(token);
    }

    private async Task<PreparedErasure> PrepareErasureAsync(Guid organization, Guid employee, Guid user, Guid actor, Guid episode,
        PostgreSqlMemoryStore store, MemoryWorkErasure work, MemoryCaptureErasure capture, CancellationToken token)
    {
        var source = await LockLegacyEpisodeAsync(episode, token);
        await MemoryManagerAuthorization.RequirePartitionAsync(db, organization, employee, actor, source.Episode.Partition, token, true);
        var roots = new HashSet<ErasureRoot> { new(source.Episode.Partition, episode) };
        var outputs = new HashSet<MemoryCaptureErasure.Source>();
        MemoryErasurePreview inventory = null!; ErasureExecution? execution = null;
        ChatTurn[] turns = []; string? blocked = null; var stable = false;
        for (var pass = 0; pass < 8; pass++)
        {
            if (roots.Count > 128 || outputs.Count > 1024) throw new InvalidOperationException("memory_erasure_scan_limit");
            var previews = new List<MemoryErasurePreview>();
            foreach (var root in roots.OrderBy(x => x.Partition.StorageKey, StringComparer.Ordinal).ThenBy(x => x.Id))
                previews.Add(await store.PreviewEpisodeErasureAsync(root.Partition, root.Id, token));
            var targets = previews.SelectMany(x => x.Targets).Distinct().ToArray();
            inventory = new(source.Episode.Partition, episode, Hash(previews.Select(x => x.EvidenceToken).ToArray()), targets,
                previews.Select(x => x.BlockedReason).FirstOrDefault(x => x is not null));
            await AuthorizeErasureAudiencesAsync(organization, employee, user, actor, targets.Select(x => x.Partition), token);
            blocked = inventory.BlockedReason;
            try { execution = await ReadErasureExecutionAsync(organization, employee, user, actor, inventory, store, work, capture, token, outputs.ToArray()); }
            catch (InvalidOperationException error) when (IsErasureReviewBlocker(error.Message))
            { blocked ??= error.Message; execution = null; stable = true; break; }
            blocked ??= execution.Impact.BlockedReason;
            var sourceIds = execution.Capture.Sources.Select(x => x.MessageId).ToArray();
            var turnIds = execution.Reads.Where(x => x.Binding is not null).Select(x => x.Binding!.TurnId).Distinct().ToArray();
            turns = await db.ChatTurns.AsNoTracking().Where(x => turnIds.Contains(x.Id) || sourceIds.Contains(x.UserMessageId) ||
                    x.AssistantMessageId != null && sourceIds.Contains(x.AssistantMessageId.Value))
                .OrderBy(x => x.Id).Take(1025).ToArrayAsync(token);
            if (turns.Length > 1024) throw new InvalidOperationException("memory_erasure_scan_limit");
            var before = roots.Count + outputs.Count;
            foreach (var turn in turns)
            {
                var conversation = await db.CoreConversations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == turn.ConversationId, token);
                if (turn.OrganizationId != organization || conversation is null || conversation.OrganizationId != organization ||
                    conversation.Kind != ConversationKind.DirectHumanAgent || conversation.InitiatedByOrganizationUserId != actor ||
                    conversation.AgentOrganizationUserId != turn.TargetAgentOrganizationUserId) throw new UnauthorizedAccessException();
                await MemoryManagerAuthorization.RequireAsync(db, organization, turn.TargetAgentOrganizationUserId, user, true, token, true);
                if (turn.AssistantMessageId is not { } output) continue;
                var row = await db.CoreConversationMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == output, token);
                if (row is null || row.ConversationId != turn.ConversationId || row.Role != ConversationRole.Assistant ||
                    row.SenderOrganizationUserId is { } sender && sender != turn.TargetAgentOrganizationUserId)
                    throw new InvalidOperationException("memory_erasure_work_prompt_review_required");
                outputs.Add(new(output, turn.ConversationId, turn.TargetAgentOrganizationUserId, actor));
                var partition = EmployeeMemoryNamespaces.UserRelationship(organization.ToString("D"), turn.TargetAgentOrganizationUserId.ToString("D"), actor.ToString("D"), "csweet").Partition;
                // Include captured generated answers, transferred copies and later readers.
                if (await store.GetEpisodeAsync(partition, output, token) is not null) roots.Add(new(partition, output));
            }
            if (before == roots.Count + outputs.Count) { stable = true; break; }
        }
        if (!stable) throw new InvalidOperationException("memory_erasure_scan_limit");
        var audiences = inventory.Targets.Select(x => x.Partition).Concat(execution?.Audiences ?? [])
            .Concat(turns.Select(x => EmployeeMemoryNamespaces.UserRelationship(organization.ToString("D"), x.TargetAgentOrganizationUserId.ToString("D"), actor.ToString("D"), "csweet").Partition))
            .Distinct().OrderBy(x => x.StorageKey, StringComparer.Ordinal).ToArray();
        await AuthorizeErasureAudiencesAsync(organization, employee, user, actor, audiences, token);
        var rows = new List<MemoryErasureAudienceImpact>();
        var shared = EmployeeMemoryNamespaces.Organization(organization.ToString("D"), "csweet").Partition;
        foreach (var partition in inventory.Targets.Select(x => x.Partition).Distinct().OrderBy(x => x.StorageKey, StringComparer.Ordinal))
        {
            var owner = partition == shared ? employee : Guid.Parse(partition.AgentId!);
            var scope = partition == shared ? "Organization" : partition.UserId is null ? "Employee" : "Relationship";
            var name = partition == shared ? "Shared organization" : await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.Id == owner).Select(x => x.DisplayName).SingleAsync(token);
            rows.Add(new(scope, partition == shared ? null : owner, name, inventory.Targets.Where(x => x.Partition == partition).GroupBy(x => x.Kind)
                .OrderBy(x => x.Key).Select(x => new MemoryErasureKindCount(x.Key.ToString(), x.Select(y => y.Id).Distinct().Count())).ToArray()));
        }
        var applyBlocked = blocked ?? (turns.Any(x => x.Status is not (ChatTurnStatus.Completed or ChatTurnStatus.CompletedWithWarnings or ChatTurnStatus.Failed or ChatTurnStatus.Cancelled))
            ? "memory_erasure_chat_turn_in_progress" : null);
        if (applyBlocked is null && execution!.Reads.Any(x => x.Binding is not null && !turns.Any(t => t.Id == x.Binding.TurnId)))
            applyBlocked = "memory_erasure_diagnostics_review_required";
        if (applyBlocked is null) applyBlocked = await CheckErasureDiagnosticCopiesAsync(turns, token);
        var fingerprint = applyBlocked is null ? await ErasureInventoryHashAsync(organization, employee, user, actor, inventory, execution!, turns, token) : null;
        var impact = new MemoryErasureImpactResponse(episode, inventory.Targets.DistinctBy(x => (x.Kind, x.Id)).Count(), rows, blocked)
        { Execution = execution?.Impact, DiagnosticTurns = turns.Length, EvidenceToken = fingerprint, ApplyBlockedReason = applyBlocked };
        return new(impact, inventory, roots.OrderBy(x => x.Partition.StorageKey, StringComparer.Ordinal).ThenBy(x => x.Id).ToArray(), execution, turns, audiences);
    }

    private async Task AuthorizeErasureAudiencesAsync(Guid organization, Guid employee, Guid user, Guid actor,
        IEnumerable<MemoryPartition> partitions, CancellationToken token)
    {
        var audiences = partitions.Distinct().ToArray();
        if (audiences.Length > 64) throw new InvalidOperationException("memory_erasure_scan_limit");
        var shared = EmployeeMemoryNamespaces.Organization(organization.ToString("D"), "csweet").Partition;
        foreach (var partition in audiences)
        {
            var owner = partition == shared ? employee : Guid.TryParse(partition.AgentId, out var id) ? id : throw new UnauthorizedAccessException();
            if (await MemoryManagerAuthorization.RequireAsync(db, organization, owner, user, true, token, true) != actor) throw new UnauthorizedAccessException();
            await MemoryManagerAuthorization.RequirePartitionAsync(db, organization, owner, actor, partition, token, true);
        }
    }

    private static bool IsErasureReviewBlocker(string code) => code is "memory_erasure_source_review_required" or
        "memory_erasure_work_lineage_review_required" or "memory_erasure_work_audience_review_required" or "memory_erasure_work_retention_review_required" or
        "memory_erasure_lineage_review_required" or "memory_erasure_capture_lineage_review_required" or "memory_erasure_capture_retention_review_required" or
        "memory_erasure_work_prompt_review_required" or "memory_erasure_work_attachment_review_required";

    private async Task<string> ErasureInventoryHashAsync(Guid organization, Guid employee, Guid user, Guid actor,
        MemoryErasurePreview inventory, ErasureExecution execution, IReadOnlyList<ChatTurn> turns, CancellationToken token)
    {
        // Equal counts are not equal reviews. Stream canonical rows and retain only a digest.
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var jobs = execution.Capture.Jobs.ToArray(); var works = execution.Work.Works.Select(x => x.Id).ToArray();
        var runtimes = execution.Work.Runtimes.Select(x => x.Id).ToArray(); var turnIds = turns.Select(x => x.Id).ToArray();
        var sources = execution.Capture.Sources.Select(x => x.MessageId).ToArray();
        digest.AppendData(Encoding.UTF8.GetBytes(Hash(new { organization, employee, user, actor, inventory, sources, jobs, works, runtimes, turnIds })));
        using var command = Command("""
            SELECT 'capture', to_jsonb(t)::text FROM "MemoryCaptureOutbox" t WHERE t."Id"=ANY(@jobs)
            UNION ALL SELECT 'extraction',to_jsonb(t)::text FROM "MemoryExtractionInputReceipts" t WHERE t."JobId"=ANY(@jobs)
            UNION ALL SELECT 'provider',to_jsonb(t)::text FROM "MemoryEnrichmentProviderLeases" t WHERE t."JobId"=ANY(@jobs)
            UNION ALL SELECT 'work',to_jsonb(t)::text FROM "AgentWorkItems" t WHERE t."Id"=ANY(@works)
            UNION ALL SELECT 'attempt',to_jsonb(t)::text FROM "AgentWorkAttempts" t WHERE t."AgentWorkItemId"=ANY(@works)
            UNION ALL SELECT 'progress',to_jsonb(t)::text FROM "AgentWorkProgress" t WHERE t."AgentWorkItemId"=ANY(@works)
            UNION ALL SELECT 'runtime',to_jsonb(t)::text FROM "AgentRuntimeInstances" t WHERE t."Id"=ANY(@runtimes)
            UNION ALL SELECT 'session',to_jsonb(t)::text FROM "McpAgentSessions" t WHERE t."RuntimeInstanceId"=ANY(@runtimes)
            UNION ALL SELECT 'read',to_jsonb(t)::text FROM "AgentMemoryReadReceipts" t WHERE t."RuntimeId"=ANY(@runtimes)
            UNION ALL SELECT 'turn',to_jsonb(t)::text FROM "ChatTurns" t WHERE t."Id"=ANY(@turns)
            UNION ALL SELECT 'trace',to_jsonb(t)::text FROM "ChatTurnTraceEvents" t WHERE t."ChatTurnId"=ANY(@turns)
            UNION ALL SELECT 'source',to_jsonb(t)::text FROM "CoreConversationMessages" t WHERE t."Id"=ANY(@sources)
            UNION ALL SELECT 'audience',to_jsonb(t)::text FROM "CoreConversations" t WHERE t."OrganizationId"=@organization
            UNION ALL SELECT 'authority',to_jsonb(t)::text FROM "CoreOrganizationUsers" t WHERE t."OrganizationId"=@organization
            UNION ALL SELECT 'use',to_jsonb(t)::text FROM "AgentMemoryRecallUses" t WHERE t."OrganizationId"=@organization
            ORDER BY 1,2 LIMIT 32769
            """);
        command.Parameters.AddWithValue("jobs", jobs); command.Parameters.AddWithValue("works", works);
        command.Parameters.AddWithValue("runtimes", runtimes); command.Parameters.AddWithValue("turns", turnIds);
        command.Parameters.AddWithValue("sources", sources); command.Parameters.AddWithValue("organization", organization);
        using var reader = await command.ExecuteReaderAsync(token); int count = 0; long bytes = 0;
        while (await reader.ReadAsync(token))
        {
            var row = Encoding.UTF8.GetBytes(reader.GetString(0) + ":" + reader.GetString(1)); bytes += row.Length;
            if (++count > 32768 || bytes > 33_554_432) throw new InvalidOperationException("memory_erasure_scan_limit");
            digest.AppendData(BitConverter.GetBytes(row.Length)); digest.AppendData(row);
        }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }
}
