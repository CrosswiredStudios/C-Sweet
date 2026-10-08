using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class AgentWorkInbox
{
    // Server-only settlement. Delivered work may already have caused effects, so a reset
    // cannot be an implicit retry grant. Pending, never-delivered work remains queued.
    public async Task SettleMemoryResetAsync(Guid runtimeId, CancellationToken token)
    {
        var binding = await db.AgentRuntimeInstances.AsNoTracking().Where(x => x.Id == runtimeId && x.MemoryResetRequestedAt != null)
            .Select(x => new { x.AgentInstallationId, OrganizationId = x.AgentInstallation!.BusinessId }).SingleOrDefaultAsync(token);
        if (binding is null) throw new InvalidOperationException("The runtime has no durable memory reset request.");
        var gate = ClaimLocks.GetOrAdd(binding.AgentInstallationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
            if (db.Database.IsNpgsql())
            {
                var key = $"agent-work-claim:{binding.OrganizationId}:{binding.AgentInstallationId:D}";
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key},0))", token);
            }
            var attempts = await db.AgentWorkAttempts.Include(x => x.AgentWorkItem).Where(x =>
                x.RuntimeInstanceId == runtimeId && (x.FinishedAt == null ||
                    x.Error == MemoryRuntimeResetRequiredException.FailureCode && x.AgentWorkItem!.Status == AgentWorkStatus.Leased))
                .Take(1001).ToListAsync(token);
            if (attempts.Count > 1000) throw new InvalidOperationException("Runtime reset work settlement exceeded its bound.");
            var now = timeProvider.GetUtcNow();
            foreach (var attempt in attempts)
            {
                var item = attempt.AgentWorkItem!;
                if (item.AgentInstallationId != binding.AgentInstallationId || item.OrganizationId != binding.OrganizationId)
                    throw new InvalidOperationException("Runtime reset work binding is inconsistent.");
                attempt.FinishedAt ??= now;
                attempt.Error = MemoryRuntimeResetRequiredException.FailureCode;
                if (item.Status != AgentWorkStatus.Leased) continue;
                var completion = new AgentWorkCompletion(false, null, MemoryRuntimeResetRequiredException.SafeMessage,
                    MemoryRuntimeResetRequiredException.FailureCode, false);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(completion);
                item.ProtectedResult = _protector.Protect(bytes);
                item.ResultHash = Convert.ToHexString(SHA256.HashData(bytes));
                attempt.CompletionHash = item.ResultHash;
                item.Status = AgentWorkStatus.DeadLetter;
                item.CompletedAt = now;
                item.LastError = completion.Error;
                await RecordTicketFailureAsync(item, attempt.Attempt, completion.Error!, now, token);
                await FailCoordinationForWorkAsync(item, completion.Error, now, token);
                if (item.Kind == AgentWorkKind.ConfigurationUpdate)
                {
                    var installation = await db.AgentInstallations.SingleAsync(x => x.Id == item.AgentInstallationId, token);
                    installation.ConfigurationSyncStatus = AgentConfigurationSyncStatus.PendingNextStart;
                }
            }
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
        }
        finally { gate.Release(); }
    }
}
