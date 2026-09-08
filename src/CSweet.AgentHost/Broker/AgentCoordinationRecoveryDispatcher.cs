using CSweet.Application.Communications;
using CSweet.Infrastructure.Communications;

namespace CSweet.AgentHost.Broker;

public sealed class AgentCoordinationRecoveryDispatcher(
    IServiceScopeFactory scopeFactory, TimeProvider clock,
    ILogger<AgentCoordinationRecoveryDispatcher> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        AgentDispatchLoop.RunAsync(DispatchAsync, clock, logger, stoppingToken);

    private async Task DispatchAsync(CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = (AgentCoordinationService)scope.ServiceProvider.GetRequiredService<IAgentCoordinationService>();
        var count = await service.RecoverTransientFailuresAsync(clock.GetUtcNow(), token);
        if (count > 0) logger.LogInformation("Resumed {Count} coordination sessions after transient delivery failures.", count);
    }
}
