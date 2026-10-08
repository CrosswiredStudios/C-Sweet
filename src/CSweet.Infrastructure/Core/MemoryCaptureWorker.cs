using CSweet.Application.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Core;

/// <summary>One bounded pass at a time; provider leases coordinate background capacity across replicas.</summary>
public sealed class MemoryCaptureWorker(IServiceScopeFactory scopeFactory, ILogger<MemoryCaptureWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var memory = scope.ServiceProvider.GetRequiredService<IAgentMemoryService>();
                // Continuous interactive traffic must not veto background work globally.
                // Foreground requests do not acquire the background provider lease.
                var processed = await memory.ProcessPendingAsync(limit: 1, cancellationToken: stoppingToken);
                await Task.Delay(processed > 0 ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "The memory capture worker failed a processing pass.");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }
}
