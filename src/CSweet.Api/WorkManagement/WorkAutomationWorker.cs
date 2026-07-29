using CSweet.Application.WorkManagement;

namespace CSweet.Api.WorkManagement;

public sealed class WorkAutomationWorker(
    IServiceProvider services,
    ILogger<WorkAutomationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var dispatcher =
                    scope.ServiceProvider.GetRequiredService<IWorkAutomationDispatcher>();
                var count = await dispatcher.DispatchBatchAsync(
                    cancellationToken: stoppingToken);
                await Task.Delay(
                    count > 0 ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(2),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Work automation dispatch failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
