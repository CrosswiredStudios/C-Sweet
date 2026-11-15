using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.WorkManagement;

public sealed class WorkIssueDispatchWorker(IServiceScopeFactory scopes, ILogger<WorkIssueDispatchWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<WorkIssueCommissionDispatcher>().DispatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Issue commission dispatch will resume from durable intent."); }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
