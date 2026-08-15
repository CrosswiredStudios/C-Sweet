using CSweet.Application.Setup;
using CSweet.Office.Contracts.Workloads;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentRuntimeCleanupService(
    CSweetDbContext dbContext,
    IAgentWorkloadRunner workloads,
    IAuditEventWriter auditWriter,
    ILogger<AgentRuntimeCleanupService> logger) : IAgentRuntimeCleanupService
{
    public async Task<AgentRuntimeCleanupResult> CleanupAsync(CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.AgentRuntimeGlobalSettings.SingleAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var workloadsRemoved = await CleanupWorkloadsAsync(settings, cancellationToken);
        var workspacesRemoved = CleanupWorkspaces(settings, now);
        var logsRemoved = CleanupBuildLogs(settings, now);
        var historiesRemoved = await CleanupRuntimeHistoryAsync(settings, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var result = new AgentRuntimeCleanupResult(
            workloadsRemoved,
            workspacesRemoved,
            logsRemoved,
            historiesRemoved);
        AgentRuntimeMetrics.Cleaned("workload", workloadsRemoved);
        AgentRuntimeMetrics.Cleaned("workspace", workspacesRemoved);
        AgentRuntimeMetrics.Cleaned("build_log", logsRemoved);
        AgentRuntimeMetrics.Cleaned("runtime_history", historiesRemoved);
        if (workloadsRemoved + workspacesRemoved + logsRemoved + historiesRemoved > 0)
        {
            logger.LogInformation("Agent runtime cleanup removed {Workloads} workloads, {Workspaces} workspace locators, {BuildLogs} build-log locators, and {RuntimeHistories} runtime histories.", workloadsRemoved, workspacesRemoved, logsRemoved, historiesRemoved);
            await auditWriter.WriteAsync("agent-runtime.cleanup.completed", nameof(AgentRuntimeInstance), null,
                $"Removed {workloadsRemoved} workloads, {workspacesRemoved} workspace locators, {logsRemoved} build-log locators, and {historiesRemoved} runtime histories.", cancellationToken: cancellationToken);
        }
        return result;
    }

    private async Task<int> CleanupWorkloadsAsync(AgentRuntimeGlobalSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.RemoveWorkloadsAfterCompletion) return 0;
        var instances = await dbContext.AgentRuntimeInstances
            .Where(x => x.CompletedAt != null && x.ProviderInstanceId != null)
            .ToListAsync(cancellationToken);
        var removed = 0;
        foreach (var instance in instances)
        {
            try
            {
                var handle = new IsolationWorkloadHandle(
                    instance.IsolationProviderId!, instance.Id, instance.ProviderInstanceId!, WorkloadKind.Runtime);
                var status = await workloads.InspectAsync(handle, cancellationToken);
                if (status is not null) await workloads.DestroyAsync(handle, cancellationToken);
                instance.ProviderInstanceId = null;
                instance.IsolationProviderId = null;
                removed++;
            }
            catch (AgentWorkloadException exception)
            {
                logger.LogWarning(exception, "Deferred workload cleanup failed for runtime {RuntimeInstanceId}.", instance.Id);
            }
        }
        return removed;
    }

    private int CleanupWorkspaces(AgentRuntimeGlobalSettings settings, DateTimeOffset now)
    {
        var retentionCutoff = now.AddDays(-settings.BuildLogRetentionDays);
        var jobs = dbContext.AgentBuildJobs.Local.Concat(dbContext.AgentBuildJobs
            .Where(x => x.CompletedAt != null && x.SourceWorkspacePath != null).ToList())
            .DistinctBy(x => x.Id);
        var removed = 0;
        foreach (var job in jobs)
        {
            var removeImmediately = job.Status == AgentBuildStatus.Succeeded
                ? settings.RemoveWorkspacesAfterCompletion
                : !settings.KeepFailedBuildWorkspaces;
            if (!removeImmediately && job.CompletedAt >= retentionCutoff) continue;
            job.SourceWorkspacePath = null;
            removed++;
        }
        return removed;
    }

    private int CleanupBuildLogs(AgentRuntimeGlobalSettings settings, DateTimeOffset now)
    {
        var cutoff = now.AddDays(-settings.BuildLogRetentionDays);
        var jobs = dbContext.AgentBuildJobs
            .Where(x => x.CompletedAt != null && x.CompletedAt < cutoff && x.LogPath != null).ToList();
        var removed = 0;
        foreach (var job in jobs)
        {
            job.LogPath = null;
            removed++;
        }
        return removed;
    }

    private async Task<int> CleanupRuntimeHistoryAsync(AgentRuntimeGlobalSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var completedCutoff = now.AddDays(-settings.CompletedRuntimeRetentionDays);
        var failedCutoff = now.AddDays(-settings.FailedRuntimeRetentionDays);
        var completedStatuses = new[] { AgentRuntimeStatus.Completed, AgentRuntimeStatus.Skipped, AgentRuntimeStatus.Cancelled };
        var instances = await dbContext.AgentRuntimeInstances
            .Where(x => x.CompletedAt != null &&
                ((completedStatuses.Contains(x.Status) && x.CompletedAt < completedCutoff) ||
                 (!completedStatuses.Contains(x.Status) && x.CompletedAt < failedCutoff)))
            .ToListAsync(cancellationToken);
        dbContext.AgentRuntimeInstances.RemoveRange(instances);
        return instances.Count;
    }

}
