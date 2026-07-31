using CSweet.Application.WorkManagement;

namespace CSweet.Api.WorkManagement;

public sealed class WorkDeliveryPipelineWorker(
    IServiceScopeFactory scopes,
    ILogger<WorkDeliveryPipelineWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var coordinator = scope.ServiceProvider
                    .GetRequiredService<IWorkDeliveryPipelineService>();
                await coordinator.PulseAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The delivery-pipeline worker pulse failed.");
            }
            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }
}
