using CSweet.Application.WorkManagement;

namespace CSweet.AgentHost.Broker;

public sealed class BusinessCalendarWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<BusinessCalendarWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IBusinessCalendarService>().DispatchDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Calendar dispatch failed; pending occurrences will retry."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
