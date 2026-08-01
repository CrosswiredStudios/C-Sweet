using CSweet.Application.WorkManagement;

namespace CSweet.Api.WorkManagement;

public sealed class WorkOrchestrationWorker(
    IServiceScopeFactory scopes,
    ILogger<WorkOrchestrationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IWorkOrchestrator>()
                    .PulseAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "Board work orchestration pulse failed.");
            }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
