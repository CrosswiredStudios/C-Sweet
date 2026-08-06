using CSweet.AgentRuntime.LocalRpc;

namespace CSweet.RuntimeHost;

public sealed class RuntimeHostWorker(
    RuntimeHostRpcServer server,
    ILogger<RuntimeHostWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting the privileged C-Sweet runtime host service.");
        await server.RunAsync(stoppingToken);
    }
}
