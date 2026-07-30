using CSweet.Application.Setup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentRuntimeStartupCleanupService(
    IAgentContainerRunner containers,
    IOptions<AgentRuntimeManagerOptions> options,
    ILogger<AgentRuntimeStartupCleanupService> logger)
{
    public async Task<int> CleanupAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Value.CleanupContainersOnStartup)
        {
            logger.LogInformation("Agent runtime container cleanup on startup is disabled.");
            return 0;
        }

        var managed = await containers.ListManagedAsync(cancellationToken);
        var networks = await containers.ListManagedNetworksAsync(
            options.Value.DockerNetworkName,
            cancellationToken);
        var containerRuntimeIds = managed.Select(x => x.RuntimeInstanceId).ToHashSet();
        var removed = 0;
        foreach (var container in managed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await containers.RemoveAsync(container.ContainerId, force: true, cancellationToken: cancellationToken);
                await containers.RemoveNetworkAsync(
                    $"{options.Value.DockerNetworkName}-{container.RuntimeInstanceId:N}",
                    options.Value.McpGatewayContainer,
                    cancellationToken);
                removed++;
            }
            catch (AgentContainerException exception)
            {
                logger.LogWarning(
                    exception,
                    "Could not clean up agent runtime container {ContainerName} from a previous worker lifetime.",
                    container.Name);
            }
        }

        var orphanNetworksRemoved = 0;
        foreach (var network in networks.Where(x => !containerRuntimeIds.Contains(x.RuntimeInstanceId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await containers.RemoveNetworkAsync(
                    network.Name,
                    options.Value.McpGatewayContainer,
                    cancellationToken);
                orphanNetworksRemoved++;
            }
            catch (AgentContainerException exception)
            {
                logger.LogWarning(
                    exception,
                    "Could not clean up orphan agent runtime network {NetworkName} from a previous worker lifetime.",
                    network.Name);
            }
        }

        if (removed > 0)
        {
            logger.LogInformation(
                "Removed {ContainerCount} agent runtime containers left by a previous worker lifetime.",
                removed);
        }
        if (orphanNetworksRemoved > 0)
        {
            logger.LogInformation(
                "Removed {NetworkCount} orphan agent runtime networks left by previous worker lifetimes.",
                orphanNetworksRemoved);
        }
        return removed;
    }
}
