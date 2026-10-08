using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Llm;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class MemoryRecallDispatchEvidence
{
    /// <summary>
    /// Checks, before a lease is granted, whether every memory read already retained by the runtime may
    /// be carried into the candidate work's audience. An incompatible candidate requests a fresh runtime
    /// instead: the work stays pending and undelivered, so it cannot be consumed by a reset after delivery.
    /// Dispatch authorization remains the final check; this only avoids delivering doomed work.
    /// </summary>
    public async Task RequireRetainedConsumerAsync(McpAgentSession session, AgentWorkItem candidate, CancellationToken token)
    {
        if (db.Database.IsNpgsql() && db.Database.CurrentTransaction is not null)
        {
            // Share the broker read lock so a concurrently recorded read cannot be missed.
            var lockKey = $"memory-read:{session.RuntimeInstanceId:D}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey},0))", token);
        }
        var receipts = await db.AgentMemoryReadReceipts.AsNoTracking().Where(x => x.RuntimeId == session.RuntimeInstanceId)
            .OrderBy(x => x.Id).Take(65).ToArrayAsync(token);
        if (receipts.Length == 0) return;
        if (receipts.Length > 64 || receipts.Sum(x => x.EvidenceJson.Length) > 2_097_152)
            throw new MemoryRuntimeResetRequiredException(MemoryRuntimeResetRequiredException.ReceiptCapacity);
        foreach (var receipt in receipts)
        {
            var validation = "claim.receipt-binding";
            try
            {
                if (receipt.InstallationId != session.AgentInstallationId || receipt.OrganizationId.ToString("D") != session.OrganizationId ||
                    receipt.GrantRevision != session.GrantRevision || candidate.AgentInstallationId != session.AgentInstallationId)
                    throw Denied();
                if (receipt.Capability == QueuedRecallCapability)
                {
                    validation = "claim.queued-recall";
                    await AuthorizeRetainedDeliveryAsync(receipt, candidate, token);
                    continue;
                }
                validation = "claim.read-evidence";
                var partitions = await ValidateReadAsync(receipt.EvidenceJson, token, preserveInfrastructureFailure: true);
                foreach (var partition in partitions)
                    if (partition.UserId is { } user)
                    {
                        validation = "claim.relationship-consumer";
                        await RequireRelationshipConsumerAsync(candidate, receipt.EmployeeId, receipt.WorkId, user, token);
                    }
                if (partitions.Any(MemorySharedAudiences.IsCanonical) && candidate.SourceType == "chat-turn")
                {
                    validation = "claim.shared-consumer";
                    if (!Guid.TryParse(candidate.SourceId, out var turn)) throw Denied();
                    await AuthorizeChatReadSharedAudienceAsync(receipt.EvidenceJson, turn, receipt.OrganizationId, receipt.EmployeeId, token);
                }
            }
            catch (Exception error) when (error is ProviderDispatchDeniedException or UnauthorizedAccessException or
                JsonException or FormatException or NullReferenceException or KeyNotFoundException or ArgumentException)
            {
                var code = error.Data["memory.validation"] is string specific ? $"claim.{specific}" : validation;
                throw new MemoryRuntimeResetRequiredException(MemoryRuntimeResetRequiredException.RetainedEvidence,
                    $"validation={code};receipt={receipt.Id:D};work={candidate.Id:D};error={error.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// Relationship-private context may only reach the same work item, or a direct chat turn from the
    /// same human to the same employee. Any other consumer, including agent coordination, is denied.
    /// </summary>
    public async Task RequireRelationshipConsumerAsync(AgentWorkItem consumer, Guid employee, Guid receiptWork, string user,
        CancellationToken token)
    {
        if (consumer.SourceType != "chat-turn")
        {
            if (consumer.Id != receiptWork) throw Denied("relationship.consumer-kind");
            return;
        }
        if (!Guid.TryParse(consumer.SourceId, out var turnId) || !Guid.TryParse(user, out var human) ||
            !await db.ChatTurns.AsNoTracking().AnyAsync(x => x.Id == turnId && x.TargetAgentOrganizationUserId == employee &&
                x.Conversation!.Kind == ConversationKind.DirectHumanAgent && x.Conversation.InitiatedByOrganizationUserId == human &&
                (x.UserMessage!.SenderOrganizationUserId ?? x.Conversation.InitiatedByOrganizationUserId) == human, token))
            throw Denied("relationship.consumer-audience");
    }
}
