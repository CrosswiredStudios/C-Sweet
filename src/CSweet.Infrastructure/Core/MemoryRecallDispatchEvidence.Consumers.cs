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
    /// Validates retained memory before a lease is granted. While delivered work is still running,
    /// validate its consumers and return false to hold all new pickups. Its durable leases are the
    /// drain boundary; an incompatible next consumer must not reset otherwise valid running work.
    /// Once idle, validate the candidate and rotate before delivering incompatible work. Invalid
    /// retained evidence and receipt limits still require an immediate reset. Dispatch rechecks remain
    /// authoritative; this admission check never grants provider execution.
    /// </summary>
    public async Task<bool> RequireRetainedConsumerAsync(McpAgentSession session, AgentWorkItem candidate, CancellationToken token)
    {
        if (db.Database.IsNpgsql() && db.Database.CurrentTransaction is not null)
        {
            // Share the broker read lock so a concurrently recorded read cannot be missed.
            var lockKey = $"memory-read:{session.RuntimeInstanceId:D}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey},0))", token);
        }
        var receipts = await db.AgentMemoryReadReceipts.AsNoTracking().Where(x => x.RuntimeId == session.RuntimeInstanceId)
            .OrderBy(x => x.Id).Take(65).ToArrayAsync(token);
        if (receipts.Length == 0) return true;
        if (receipts.Length > 64 || receipts.Sum(x => x.EvidenceJson.Length) > 2_097_152)
            throw new MemoryRuntimeResetRequiredException(MemoryRuntimeResetRequiredException.ReceiptCapacity);
        if (await db.AgentRuntimeInstances.AsNoTracking().AnyAsync(x => x.Id == session.RuntimeInstanceId &&
                x.MemoryReadEvidenceVersion != AgentRuntimeInstance.CurrentMemoryReadEvidenceVersion, token))
            throw new MemoryRuntimeResetRequiredException(MemoryRuntimeResetRequiredException.LegacyEvidence);
        var now = DateTimeOffset.UtcNow;
        var running = await db.AgentWorkAttempts.AsNoTracking().Where(x => x.RuntimeInstanceId == session.RuntimeInstanceId &&
                x.FinishedAt == null && x.LeaseExpiresAt > now && x.AgentWorkItem!.Status == AgentWorkStatus.Leased &&
                x.AgentWorkItem.DeadlineAt > now && x.AgentWorkItem.AgentInstallationId == session.AgentInstallationId &&
                x.AgentWorkItem.OrganizationId == session.OrganizationId)
            .Select(x => x.AgentWorkItem!).Distinct().Take(1001).ToArrayAsync(token);
        if (running.Length > 1000) throw new InvalidOperationException("Retained memory consumer validation exceeded its bound.");
        var consumers = running.Length == 0 ? [candidate] : running;
        foreach (var consumer in consumers)
        foreach (var receipt in receipts)
        {
            var validation = "claim.receipt-binding";
            try
            {
                if (receipt.InstallationId != session.AgentInstallationId || receipt.OrganizationId.ToString("D") != session.OrganizationId ||
                    receipt.GrantRevision != session.GrantRevision || consumer.AgentInstallationId != session.AgentInstallationId)
                    throw Denied();
                if (receipt.Capability == QueuedRecallCapability)
                {
                    validation = "claim.queued-recall";
                    await AuthorizeRetainedDeliveryAsync(receipt, consumer, token);
                    continue;
                }
                validation = "claim.read-evidence";
                var partitions = await ValidateReadAsync(receipt.EvidenceJson, token, preserveInfrastructureFailure: true);
                validation = "claim.scoped-consumer";
                await AuthorizeScopedReadConsumerAsync(receipt.EvidenceJson, consumer, receipt.EmployeeId, token);
                foreach (var partition in partitions)
                    if (partition.UserId is { } user)
                    {
                        validation = "claim.relationship-consumer";
                        await RequireRelationshipConsumerAsync(consumer, receipt.EmployeeId, receipt.WorkId, user, token);
                    }
                if (partitions.Any(MemorySharedAudiences.IsCanonical) && consumer.SourceType == "chat-turn")
                {
                    validation = "claim.shared-consumer";
                    if (!Guid.TryParse(consumer.SourceId, out var turn)) throw Denied();
                    await AuthorizeChatReadSharedAudienceAsync(receipt.EvidenceJson, turn, receipt.OrganizationId, receipt.EmployeeId, token);
                }
            }
            catch (Exception error) when (error is ProviderDispatchDeniedException or UnauthorizedAccessException or
                JsonException or FormatException or NullReferenceException or KeyNotFoundException or ArgumentException)
            {
                var code = error.Data["memory.validation"] is string specific ? $"claim.{specific}" : validation;
                throw new MemoryRuntimeResetRequiredException(MemoryRuntimeResetRequiredException.RetainedEvidence,
                    $"validation={code};receipt={receipt.Id:D};work={consumer.Id:D};error={error.GetType().Name}");
            }
        }
        return running.Length == 0;
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
