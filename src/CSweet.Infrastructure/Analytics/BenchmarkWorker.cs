using CSweet.Application.Analytics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Analytics;

/// <summary>Bounded platform recovery consumes durable wake hints; agents never poll benchmark state.</summary>
public sealed class BenchmarkWorker(IServiceScopeFactory scopes, ILogger<BenchmarkWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IBenchmarkService>().AdvanceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Benchmark recovery failed; durable state retained."); }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
