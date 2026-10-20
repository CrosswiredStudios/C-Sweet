using CSweet.Application.Setup;
using CSweet.Office.Contracts.Workloads;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CSweet.TrustedServices;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentRuntimeCleanupService(
    CSweetDbContext dbContext,
    IAgentWorkloadRunner workloads,
    IAuditEventWriter auditWriter,
    ILogger<AgentRuntimeCleanupService> logger,
    IOptions<AgentRuntimeManagerOptions>? runtimeOptions = null) : IAgentRuntimeCleanupService
{
    public async Task<AgentRuntimeCleanupResult> CleanupAsync(CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.AgentRuntimeGlobalSettings.SingleAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var workloadsRemoved = await CleanupWorkloadsAsync(settings, cancellationToken);
        var workspacesRemoved = CleanupWorkspaces(settings, now);
        var logsRemoved = CleanupBuildLogs(settings, now);
        // Persist physical cleanup evidence before querying which histories may be purged.
        await dbContext.SaveChangesAsync(cancellationToken);
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
                if (status is not null)
                {
                    await workloads.DestroyAsync(handle, cancellationToken);
                    status = await workloads.InspectAsync(handle, cancellationToken);
                }
                if (handle.ProviderId == "execution-fleet"
                    ? status?.State is not (IsolationWorkloadState.Stopped or IsolationWorkloadState.Destroyed)
                    : status is not null && status.State != IsolationWorkloadState.Destroyed) continue;
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
            try
            {
                var path = job.SourceWorkspacePath;
                if (path is null) continue;
                if (path.StartsWith("fleet-source:", StringComparison.Ordinal) || path.StartsWith("broker-source:", StringComparison.Ordinal))
                {
                    // These identify guest sources, not API-host files. Keep the locator until all builder attempts stopped.
                    var assignmentIds = dbContext.ExecutionWorkloadAssignments.Where(x => x.AgentBuildJobId == job.Id).Select(x => x.Id);
                    if (dbContext.ExecutionWorkloadAssignments.Any(x => x.AgentBuildJobId == job.Id && x.StopEvidenceVersion != 1) ||
                        dbContext.ExecutionAssignmentAttempts.Any(x => assignmentIds.Contains(x.AssignmentId) && x.StoppedAt == null)) continue;
                }
                else
                {
                    var root = (runtimeOptions?.Value ?? new()).SourceArchiveStorePath;
                    if (File.Exists(path)) throw new IOException("A workspace locator unexpectedly identifies a file.");
                    OwnedStorage.DeleteDirectory(root, path);
                }
                job.SourceWorkspacePath = null;
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { logger.LogWarning(exception, "Build workspace cleanup will retry for {BuildJobId}.", job.Id); }
        }
        return removed;
    }

    private int CleanupBuildLogs(AgentRuntimeGlobalSettings settings, DateTimeOffset now)
    {
        var cutoff = now.AddDays(-settings.BuildLogRetentionDays);
        var jobs = dbContext.AgentBuildJobs
            .Where(x => x.CompletedAt != null && x.CompletedAt < cutoff && x.LogPath != null).ToList();
        var expiredIds = jobs.Select(x => x.Id).ToArray();
        var removed = 0;
        foreach (var job in jobs)
        {
            try
            {
                if (job.LogPath!.StartsWith("broker-log:", StringComparison.Ordinal))
                    throw new IOException("The broker log store must confirm physical cleanup before its locator can be removed.");
                var root = (runtimeOptions?.Value ?? new()).BuildLogStorePath;
                if (!dbContext.AgentBuildJobs.Any(x => !expiredIds.Contains(x.Id) && x.LogPath == job.LogPath))
                    OwnedStorage.DeleteFile(root, job.LogPath);
                job.LogPath = null;
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { logger.LogWarning(exception, "Build log cleanup will retry for {BuildJobId}.", job.Id); }
        }
        return removed;
    }

    private async Task<int> CleanupRuntimeHistoryAsync(AgentRuntimeGlobalSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var completedCutoff = now.AddDays(-settings.CompletedRuntimeRetentionDays);
        var failedCutoff = now.AddDays(-settings.FailedRuntimeRetentionDays);
        var completedStatuses = new[] { AgentRuntimeStatus.Completed, AgentRuntimeStatus.Skipped, AgentRuntimeStatus.Cancelled };
        var instances = await dbContext.AgentRuntimeInstances
            .Where(x => x.CompletedAt != null && x.ProviderInstanceId == null &&
                !dbContext.ExecutionWorkloadAssignments.Any(a => a.AgentRuntimeInstanceId == x.Id &&
                    (a.StopEvidenceVersion != 1 || dbContext.ExecutionAssignmentAttempts.Any(t => t.AssignmentId == a.Id && t.StoppedAt == null))) &&
                ((completedStatuses.Contains(x.Status) && x.CompletedAt < completedCutoff) ||
                 (!completedStatuses.Contains(x.Status) && x.CompletedAt < failedCutoff)))
            .ToListAsync(cancellationToken);
        dbContext.AgentRuntimeInstances.RemoveRange(instances);
        return instances.Count;
    }

}
