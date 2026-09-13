using CSweet.Infrastructure.Compute;

#if COMPUTE_GATEWAY
namespace CSweet.ExecutionGateway.Compute;
#else
namespace CSweet.Api.Compute;
#endif

/// <summary>Platform-owned recovery of committed notification outbox records.</summary>
internal sealed class ComputeProviderWakeWorker(IServiceScopeFactory scopes, ILogger<ComputeProviderWakeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ComputeProviderWakeDispatcher>().DispatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Provider wake publication failed; durable hints remain pending."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
