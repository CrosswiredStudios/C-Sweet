using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentRuntimeCleanupService(
    CSweetDbContext dbContext,
    IAgentContainerRunner containers,
    IAuditEventWriter auditWriter,
    IOptions<AgentRuntimeManagerOptions> runtimeOptions,
    ILogger<AgentRuntimeCleanupService> logger) : IAgentRuntimeCleanupService
{
    public async Task<AgentRuntimeCleanupResult> CleanupAsync(CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.AgentRuntimeGlobalSettings.SingleAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var containersRemoved = await CleanupContainersAsync(settings, cancellationToken);
        var networksRemoved = await CleanupOrphanNetworksAsync(cancellationToken);
        var workspacesRemoved = CleanupWorkspaces(settings, now);
        var logsRemoved = CleanupBuildLogs(settings, now);
        var historiesRemoved = await CleanupRuntimeHistoryAsync(settings, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var result = new AgentRuntimeCleanupResult(
            containersRemoved,
            networksRemoved,
            workspacesRemoved,
            logsRemoved,
            historiesRemoved);
        AgentRuntimeMetrics.Cleaned("container", containersRemoved);
        AgentRuntimeMetrics.Cleaned("network", networksRemoved);
        AgentRuntimeMetrics.Cleaned("workspace", workspacesRemoved);
        AgentRuntimeMetrics.Cleaned("build_log", logsRemoved);
        AgentRuntimeMetrics.Cleaned("runtime_history", historiesRemoved);
        if (containersRemoved + networksRemoved + workspacesRemoved + logsRemoved + historiesRemoved > 0)
        {
            logger.LogInformation("Agent runtime cleanup removed {Containers} containers, {Networks} orphan networks, {Workspaces} workspaces, {BuildLogs} build logs, and {RuntimeHistories} runtime histories.", containersRemoved, networksRemoved, workspacesRemoved, logsRemoved, historiesRemoved);
            await auditWriter.WriteAsync("agent-runtime.cleanup.completed", nameof(AgentRuntimeInstance), null,
                $"Removed {containersRemoved} containers, {networksRemoved} orphan networks, {workspacesRemoved} workspaces, {logsRemoved} build logs, and {historiesRemoved} runtime histories.", cancellationToken: cancellationToken);
        }
        return result;
    }

    private async Task<int> CleanupContainersAsync(AgentRuntimeGlobalSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.RemoveContainersAfterCompletion) return 0;
        var instances = await dbContext.AgentRuntimeInstances
            .Where(x => x.CompletedAt != null && x.ContainerId != null)
            .ToListAsync(cancellationToken);
        var removed = 0;
        foreach (var instance in instances)
        {
            try
            {
                var status = await containers.InspectAsync(instance.ContainerId!, cancellationToken);
                if (status is not null) await containers.RemoveAsync(instance.ContainerId!, force: true, cancellationToken: cancellationToken);
                await containers.RemoveNetworkAsync(
                    $"{runtimeOptions.Value.DockerNetworkName}-{instance.Id:N}",
                    runtimeOptions.Value.McpGatewayContainer,
                    cancellationToken);
                instance.ContainerId = null;
                removed++;
            }
            catch (AgentContainerException exception)
            {
                logger.LogWarning(exception, "Deferred container cleanup failed for runtime {RuntimeInstanceId}.", instance.Id);
            }
        }
        return removed;
    }

    private async Task<int> CleanupOrphanNetworksAsync(CancellationToken cancellationToken)
    {
        var activeRuntimeIds = (await dbContext.AgentRuntimeInstances
                .AsNoTracking()
                .Where(x => x.CompletedAt == null)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var networks = await containers.ListManagedNetworksAsync(
            runtimeOptions.Value.DockerNetworkName,
            cancellationToken);
        var removed = 0;
        foreach (var network in networks.Where(x => !activeRuntimeIds.Contains(x.RuntimeInstanceId)))
        {
            try
            {
                await containers.RemoveNetworkAsync(
                    network.Name,
                    runtimeOptions.Value.McpGatewayContainer,
                    cancellationToken);
                removed++;
            }
            catch (AgentContainerException exception)
            {
                logger.LogWarning(
                    exception,
                    "Deferred orphan-network cleanup failed for runtime {RuntimeInstanceId}.",
                    network.RuntimeInstanceId);
            }
        }
        return removed;
    }

    private int CleanupWorkspaces(AgentRuntimeGlobalSettings settings, DateTimeOffset now)
    {
        var retentionCutoff = now.AddDays(-settings.BuildLogRetentionDays);
        var sourceRoot = ResolveStorageRoot(settings.AgentSourceRootPath, "CSWEET_AGENT_SOURCE_ROOT", "sources");
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
            if (!IsInsideRoot(job.SourceWorkspacePath!, sourceRoot))
            {
                logger.LogWarning("Refused to remove build workspace outside the approved source root: {WorkspacePath}", job.SourceWorkspacePath);
                continue;
            }
            try
            {
                if (TryDeleteDirectory(job.SourceWorkspacePath!)) removed++;
                job.SourceWorkspacePath = null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not remove retained build workspace for job {BuildJobId}.", job.Id);
            }
        }
        return removed;
    }

    private int CleanupBuildLogs(AgentRuntimeGlobalSettings settings, DateTimeOffset now)
    {
        var cutoff = now.AddDays(-settings.BuildLogRetentionDays);
        var packageRoot = ResolveStorageRoot(settings.AgentPackageCachePath, "CSWEET_AGENT_PACKAGE_CACHE", "packages");
        var jobs = dbContext.AgentBuildJobs
            .Where(x => x.CompletedAt != null && x.CompletedAt < cutoff && x.LogPath != null).ToList();
        var removed = 0;
        foreach (var job in jobs)
        {
            if (!IsInsideRoot(job.LogPath!, packageRoot))
            {
                logger.LogWarning("Refused to remove build log outside the approved package root: {LogPath}", job.LogPath);
                continue;
            }
            try
            {
                if (File.Exists(job.LogPath)) { File.Delete(job.LogPath); removed++; }
                job.LogPath = null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not remove retained build log for job {BuildJobId}.", job.Id);
            }
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

    private static bool TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return false;
        Directory.Delete(path, recursive: true);
        return true;
    }

    private static string ResolveStorageRoot(string configuredPath, string environmentVariable, string childName)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath)) return Path.GetFullPath(configuredPath);
        var environmentPath = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentPath)) return Path.GetFullPath(environmentPath);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var state = string.IsNullOrWhiteSpace(localAppData) ? Path.Combine(AppContext.BaseDirectory, ".csweet") : Path.Combine(localAppData, "CSweet");
        return Path.GetFullPath(Path.Combine(state, "agents", childName));
    }

    private static bool IsInsideRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        return relative != "." && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
