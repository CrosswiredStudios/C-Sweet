using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Serializes source uploads with lease settlement and VM replacement across replicas.</summary>
public static class RuntimeCheckpointGate
{
    public static async Task<T> ExecuteAsync<T>(CSweetDbContext db, Guid installationId, string organizationId,
        Guid runtimeId, long grantRevision, Func<Task<T>> upload, CancellationToken token)
    {
        var gate = AgentWorkInbox.ClaimLocks.GetOrAdd(installationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            if (db.Database.IsNpgsql())
            {
                var key = $"agent-work-claim:{organizationId}:{installationId:D}";
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key},0))", token);
            }
            if (!await db.AgentRuntimeInstances.AsNoTracking().AnyAsync(x => x.Id == runtimeId && x.AgentInstallationId == installationId &&
                x.Status == AgentRuntimeStatus.Running && x.MemoryResetRequestedAt == null && x.AgentInstallation!.BusinessId == organizationId &&
                x.AgentInstallation.IsEnabled && x.AgentInstallation.RevisionStatus == PluginRevisionStatus.Active &&
                x.AgentInstallation.Grant != null && x.AgentInstallation.Grant.GrantRevision == grantRevision, token))
                throw new UnauthorizedAccessException("The runtime is fenced; its checkpoint can no longer change the retained source.");
            var result = await upload();
            await transaction.CommitAsync(token);
            return result;
        }
        finally { gate.Release(); }
    }
}
