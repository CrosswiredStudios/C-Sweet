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
    /// Completed work's receipts remain audit evidence, not inputs to the candidate. Invalid
    /// active evidence and per-attempt receipt limits still require an immediate reset. Dispatch rechecks remain
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
        if (await db.AgentRuntimeInstances.AsNoTracking().AnyAsync(x => x.Id == session.RuntimeInstanceId &&
                x.MemoryReadEvidenceVersion != AgentRuntimeInstance.CurrentMemoryReadEvidenceVersion, token))
            throw new MemoryRuntimeResetRequiredException(MemoryRuntimeResetRequiredException.LegacyEvidence);
        var now = DateTimeOffset.UtcNow;
        var running = await db.AgentWorkAttempts.AsNoTracking().Where(x => x.RuntimeInstanceId == session.RuntimeInstanceId &&
                x.FinishedAt == null && x.LeaseExpiresAt > now && x.AgentWorkItem!.Status == AgentWorkStatus.Leased &&
                x.AgentWorkItem.DeadlineAt > now && x.AgentWorkItem.AgentInstallationId == session.AgentInstallationId &&
                x.AgentWorkItem.OrganizationId == session.OrganizationId)
            .Select(x => new { Work = x.AgentWorkItem!, x.Attempt }).Take(1001).ToArrayAsync(token);
        if (running.Length > 1000) throw new InvalidOperationException("Memory consumer validation exceeded its bound.");
        var activeContext = false;
        foreach (var execution in running)
        {
            var consumer = execution.Work;
            var receipts = await db.AgentMemoryReadReceipts.AsNoTracking().Where(x => x.RuntimeId == session.RuntimeInstanceId &&
                    x.WorkId == consumer.Id && x.Attempt == execution.Attempt)
                .OrderBy(x => x.Id).Take(65).ToArrayAsync(token);
            if (receipts.Length == 0) continue;
            activeContext = true;
            if (receipts.Length > 64 || receipts.Sum(x => x.EvidenceJson.Length) > 2_097_152)
                throw new MemoryRuntimeResetRequiredException(MemoryRuntimeResetRequiredException.ReceiptCapacity);
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
                    var currentTime = DateTimeOffset.UtcNow;
                    if (!await db.AgentWorkAttempts.AsNoTracking().AnyAsync(x => x.RuntimeInstanceId == session.RuntimeInstanceId &&
                            x.AgentWorkItemId == consumer.Id && x.Attempt == execution.Attempt && x.FinishedAt == null &&
                            x.LeaseExpiresAt > currentTime && x.AgentWorkItem!.Status == AgentWorkStatus.Leased &&
                            x.AgentWorkItem.DeadlineAt > currentTime, token)) continue;
                    var code = error.Data["memory.validation"] is string specific ? $"claim.{specific}" : validation;
                    throw new MemoryRuntimeResetRequiredException(MemoryRuntimeResetRequiredException.RetainedEvidence,
                        $"validation={code};receipt={receipt.Id:D};work={consumer.Id:D};error={error.GetType().Name}");
                }
            }
        }
        return !activeContext;
    }

    /// <summary>
    /// A relationship read belongs to this work context. A chat consumer additionally requires
    /// the same human/employee audience. Later chats must retrieve their own authorized context.
    /// </summary>
    public async Task RequireRelationshipConsumerAsync(AgentWorkItem consumer, Guid employee, Guid receiptWork, string user,
        CancellationToken token)
    {
        if (consumer.Id != receiptWork) throw Denied("relationship.context-binding");
        if (consumer.SourceType != "chat-turn")
        {
            return;
        }
        if (!Guid.TryParse(consumer.SourceId, out var turnId) || !Guid.TryParse(user, out var human) ||
            !await db.ChatTurns.AsNoTracking().AnyAsync(x => x.Id == turnId && x.TargetAgentOrganizationUserId == employee &&
                x.Conversation!.Kind == ConversationKind.DirectHumanAgent && x.Conversation.InitiatedByOrganizationUserId == human &&
                (x.UserMessage!.SenderOrganizationUserId ?? x.Conversation.InitiatedByOrganizationUserId) == human, token))
            throw Denied("relationship.consumer-audience");
    }
}
