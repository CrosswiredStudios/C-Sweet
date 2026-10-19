using CSweet.Infrastructure.Core;

namespace CSweet.Api.Core;

public sealed class OrganizationDeletionWorker(IServiceScopeFactory scopes,
    ILogger<OrganizationDeletionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<OrganizationDeletionRecovery>().RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Business deletion recovery could not advance."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
