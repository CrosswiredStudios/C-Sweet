using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentInteractiveRuntimeService(
    CSweetDbContext dbContext,
    IAgentRuntimeManager runtimeManager) : IAgentInteractiveRuntimeService
{
    public async Task<AgentRuntimeReadinessResponse> EnsureReadyAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var installation = await dbContext.AgentInstallations
            .AsNoTracking()
            .Include(x => x.Schedule)
            .Include(x => x.Grant)
            .Include(x => x.PackageVersion)
            .SingleOrDefaultAsync(x => x.Id == installationId, cancellationToken)
            ?? throw new AgentInstallationException("The agent installation was not found.");

        if (!installation.IsEnabled || installation.Schedule?.IsEnabled != true)
        {
            throw new AgentInstallationException("The agent installation or its schedule is disabled.");
        }

        if (installation.Grant is null)
        {
            throw new AgentInstallationException("The agent installation has no approved grant.");
        }

        if (installation.PackageVersion?.Status != AgentPackageVersionStatus.Built)
        {
            throw new AgentInstallationException("The agent package is not built and ready to run.");
        }

        var status = await GetStatusAsync(installationId, cancellationToken);
        if (!status.IsTerminal &&
            !string.Equals(status.Stage, AgentRuntimeReadinessStages.Offline, StringComparison.Ordinal))
        {
            await runtimeManager.EnsureRuntimeQueuedAsync(
                installationId,
                "Refreshed by an interactive employee request.",
                interactive: true,
                cancellationToken);
            return status;
        }

        await runtimeManager.EnsureRuntimeQueuedAsync(
            installationId,
            "Queued for an interactive employee request.",
            interactive: true,
            cancellationToken);
        await runtimeManager.ReconcileAsync(cancellationToken);
        return await GetStatusAsync(installationId, cancellationToken);
    }

    public async Task<AgentRuntimeReadinessResponse> GetStatusAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var installationExists = await dbContext.AgentInstallations
            .AsNoTracking()
            .AnyAsync(x => x.Id == installationId, cancellationToken);
        if (!installationExists)
        {
            throw new AgentInstallationException("The agent installation was not found.");
        }

        var runtimes = dbContext.AgentRuntimeInstances
            .AsNoTracking()
            .Where(x => x.AgentInstallationId == installationId);
        var activeStatuses = new[]
        {
            AgentRuntimeStatus.Queued,
            AgentRuntimeStatus.Starting,
            AgentRuntimeStatus.WaitingForMcpSession,
            AgentRuntimeStatus.Running,
            AgentRuntimeStatus.CompletionReported,
            AgentRuntimeStatus.Stopping
        };
        var runtime = await runtimes
            .Where(x => activeStatuses.Contains(x.Status))
            .OrderByDescending(x => x.QueuedAt)
            .FirstOrDefaultAsync(cancellationToken)
            ?? await runtimes
                .OrderByDescending(x => x.QueuedAt)
                .FirstOrDefaultAsync(cancellationToken);
        if (runtime is null)
        {
            return new AgentRuntimeReadinessResponse(
                installationId, null, AgentRuntimeReadinessStages.Offline, null, null,
                null, null, null, IsReady: false, IsTerminal: false);
        }

        var stage = runtime.Status switch
        {
            AgentRuntimeStatus.Queued => AgentRuntimeReadinessStages.Queued,
            AgentRuntimeStatus.Starting => AgentRuntimeReadinessStages.StartingContainer,
            AgentRuntimeStatus.WaitingForMcpSession => AgentRuntimeReadinessStages.WaitingForMcpSession,
            AgentRuntimeStatus.Running => AgentRuntimeReadinessStages.Ready,
            AgentRuntimeStatus.CompletionReported => AgentRuntimeReadinessStages.Stopping,
            AgentRuntimeStatus.Stopping => AgentRuntimeReadinessStages.Stopping,
            AgentRuntimeStatus.Completed or AgentRuntimeStatus.Cancelled or AgentRuntimeStatus.Skipped =>
                AgentRuntimeReadinessStages.Offline,
            _ when AgentRuntimeInstance.IsTerminal(runtime.Status) => AgentRuntimeReadinessStages.Failed,
            _ => AgentRuntimeReadinessStages.StartingContainer
        };
        return new AgentRuntimeReadinessResponse(
            installationId,
            runtime.Id,
            stage,
            runtime.Status.ToString(),
            runtime.Reason,
            runtime.QueuedAt,
            runtime.StartedAt,
            runtime.McpSessionEstablishedAt,
            IsReady: runtime.Status == AgentRuntimeStatus.Running,
            IsTerminal: AgentRuntimeInstance.IsTerminal(runtime.Status));
    }
}
