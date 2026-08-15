using CSweet.Application.Setup;
using CSweet.Office.Contracts.Workloads;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentRuntimeStartupCleanupService(
    CSweetDbContext dbContext,
    IAgentWorkloadRunner workloads,
    IOptions<AgentRuntimeManagerOptions> options,
    ILogger<AgentRuntimeStartupCleanupService> logger)
{
    public async Task<int> CleanupAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Value.CleanupWorkloadsOnStartup)
        {
            logger.LogInformation("Agent isolated-workload cleanup on startup is disabled.");
            return 0;
        }

        var stale = await dbContext.AgentRuntimeInstances.AsNoTracking()
            .Where(instance => instance.IsolationProviderId != null && instance.ProviderInstanceId != null)
            .Select(instance => new { instance.Id, instance.IsolationProviderId, instance.ProviderInstanceId })
            .ToListAsync(cancellationToken);
        var removed = 0;
        foreach (var instance in stale)
        {
            var handle = new IsolationWorkloadHandle(
                instance.IsolationProviderId!, instance.Id, instance.ProviderInstanceId!, WorkloadKind.Runtime);
            try
            {
                if (await workloads.InspectAsync(handle, cancellationToken) is not null)
                    await workloads.DestroyAsync(handle, cancellationToken);
                removed++;
            }
            catch (AgentWorkloadException exception)
            {
                logger.LogWarning(exception,
                    "Could not clean up isolated workload {ProviderId}/{ProviderInstanceId} from a previous control-plane lifetime.",
                    handle.ProviderId, handle.ProviderInstanceId);
            }
        }
        if (removed > 0)
            logger.LogInformation("Destroyed {WorkloadCount} stale isolated workloads from a previous control-plane lifetime.", removed);
        return removed;
    }
}
