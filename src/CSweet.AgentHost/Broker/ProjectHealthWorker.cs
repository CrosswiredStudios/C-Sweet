using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed class ProjectHealthWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ProjectHealthWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<ProjectHealthService>();
                await service.ReviewDueAsync(stoppingToken);
                await service.DeliverAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (DbUpdateException error) { logger.LogWarning(error, "Project health state changed concurrently; durable work will be rediscovered."); }
            catch (Exception error) { logger.LogError(error, "Project health pass failed; persisted incidents and deadlines remain pending."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
