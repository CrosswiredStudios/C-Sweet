using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Llm;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class MemoryRecallDispatchEvidence
{
    // Server-owned receipt discriminator; this is not an agent capability grant.
    public const string QueuedRecallCapability = "platform.memory.queued-recall.v1";

    public async Task RecordDeliveryAsync(AgentWorkItem work, McpAgentSession session, CancellationToken token)
    {
        if (work.SourceType != "chat-turn" && work.MemoryRecallReceiptJson is null) return;
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is null) throw Denied();
        var now = DateTimeOffset.UtcNow;
        var runtime = await db.AgentRuntimeInstances.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == session.RuntimeInstanceId && x.TickId == session.TickId && x.AgentInstallationId == session.AgentInstallationId, token);
        if (runtime?.MemoryResetRequestedAt is not null) throw Denied();
        if (runtime is not null && runtime.MemoryReadEvidenceVersion != AgentRuntimeInstance.CurrentMemoryReadEvidenceVersion)
            throw new MemoryRuntimeResetRequiredException(MemoryRuntimeResetRequiredException.LegacyEvidence);
        if (work.AgentInstallationId != session.AgentInstallationId || work.OrganizationId != session.OrganizationId ||
            !await db.AgentRuntimeInstances.AsNoTracking().AnyAsync(x => x.Id == session.RuntimeInstanceId &&
                x.TickId == session.TickId && x.AgentInstallationId == session.AgentInstallationId && x.MemoryReadEvidenceVersion == AgentRuntimeInstance.CurrentMemoryReadEvidenceVersion &&
                x.Status == AgentRuntimeStatus.Running && (x.RuntimeDeadlineAt == null || x.RuntimeDeadlineAt > now), token) ||
            !await db.AgentInstallationGrants.AsNoTracking().AnyAsync(x => x.AgentInstallationId == session.AgentInstallationId &&
                x.GrantRevision == session.GrantRevision, token)) throw Denied();
        var receipt = await ValidateDeliveryWorkAsync(work, token);
        var evidence = work.MemoryRecallReceiptJson ?? throw new MemoryRecallDeliveryRejectedException();

        // Share the broker read lock and budget. Never discard earlier context to accept new work.
        var lockKey = $"memory-read:{session.RuntimeInstanceId:D}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey},0))", token);
        var attempt = work.AttemptCount + 1;
        var fingerprint = Hash(JsonSerializer.Serialize(new { work.Id, attempt, MemoryRecallReceiptJson = evidence, QueuedRecallCapability }, Json));
        if (await db.AgentMemoryReadReceipts.AnyAsync(x => x.RuntimeId == session.RuntimeInstanceId && x.ReceiptHash == fingerprint, token)) return;
        var sizes = await db.AgentMemoryReadReceipts.Where(x => x.RuntimeId == session.RuntimeInstanceId)
            .Select(x => x.EvidenceJson.Length).Take(65).ToArrayAsync(token);
        if (sizes.Length >= 64 || sizes.Sum() + evidence.Length > 2_097_152)
            throw new MemoryRuntimeResetRequiredException(MemoryRuntimeResetRequiredException.ReceiptCapacity);
        // A lock wait can outlive a source/authority change; recheck before recording delivery.
        await ValidateDeliveryWorkAsync(work, token);
        db.AgentMemoryReadReceipts.Add(new AgentMemoryReadReceipt
        {
            Id = Guid.NewGuid(), OrganizationId = receipt.Binding.OrganizationId, EmployeeId = receipt.Binding.EmployeeId,
            InstallationId = session.AgentInstallationId, RuntimeId = session.RuntimeInstanceId, WorkId = work.Id,
            Attempt = attempt, GrantRevision = session.GrantRevision, Capability = QueuedRecallCapability,
            EvidenceJson = evidence, AuthorityHash = receipt.Binding.AuthorityHash,
            ReceiptHash = fingerprint, CreatedAt = now
        });
        // The caller saves this receipt atomically with the lease before returning any payload.
    }

    private async Task<Receipt> ValidateDeliveryWorkAsync(AgentWorkItem work, CancellationToken token)
    {
        try
        {
            if (work.MemoryRecallReceiptJson is null || work.MemoryRecallReceiptJson.Length > 131072)
                throw new MemoryRecallDeliveryRejectedException();
            var receipt = ReadQueuedReceipt(work.MemoryRecallReceiptJson);
            if (receipt.Binding is null) throw new MemoryRecallDeliveryRejectedException();
            await AuthorizeWorkCoreAsync(work, receipt.Binding.EmployeeId.ToString("D"), token,
                retained: false, preserveInfrastructureFailure: true);
            return receipt;
        }
        catch (Exception error) when (error is ProviderDispatchDeniedException or JsonException)
        { throw new MemoryRecallDeliveryRejectedException(); }
    }

    public async Task AuthorizeRetainedDeliveryAsync(AgentMemoryReadReceipt delivered, AgentWorkItem currentWork,
        CancellationToken token)
    {
        if (delivered.Capability != QueuedRecallCapability || delivered.EvidenceJson.Length > 131072) throw Denied();
        var receipt = ReadQueuedReceipt(delivered.EvidenceJson);
        if (receipt.Binding.OrganizationId != delivered.OrganizationId || receipt.Binding.EmployeeId != delivered.EmployeeId ||
            receipt.Binding.InstallationId != delivered.InstallationId || receipt.Binding.AuthorityHash != delivered.AuthorityHash ||
            receipt.Prompt is null || receipt.PayloadHash is null || currentWork.AgentInstallationId != delivered.InstallationId ||
            currentWork.OrganizationId != delivered.OrganizationId.ToString("D")) throw Denied();
        // Work may have been deleted. Keep enough server-owned evidence to validate its original
        // audience and sources independently; terminal turn state alone does not revoke evidence.
        var original = new AgentWorkItem
        {
            Id = delivered.WorkId, OrganizationId = delivered.OrganizationId.ToString("D"), AgentInstallationId = delivered.InstallationId,
            SourceType = "chat-turn", SourceId = receipt.Binding.TurnId.ToString("D"), PayloadHash = receipt.PayloadHash,
            MemoryRecallReceiptJson = delivered.EvidenceJson
        };
        await AuthorizeWorkCoreAsync(original, delivered.EmployeeId.ToString("D"), token, retained: true, preserveInfrastructureFailure: true);
        if (currentWork.SourceType != "chat-turn" || !Guid.TryParse(currentWork.SourceId, out var currentTurn))
            throw Denied("queued-recall.consumer-kind");
        var binding = await ReadBindingAsync(currentTurn, token);
        if (binding.EmployeeId != delivered.EmployeeId || binding.HumanId != receipt.Binding.HumanId)
            throw Denied("queued-recall.consumer-audience");
        if (receipt.Roots.Length > 0) await RequireAudienceAsync(binding, receipt.Roots, token);
        await RequireSharedAudienceAsync(binding, receipt.Records, token);
    }
}
