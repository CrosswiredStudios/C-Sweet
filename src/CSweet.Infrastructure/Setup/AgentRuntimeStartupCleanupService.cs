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

        if (removed > 0)
        {
            logger.LogInformation(
                "Removed {ContainerCount} agent runtime containers left by a previous worker lifetime.",
                removed);
        }
        return removed;
    }
}
