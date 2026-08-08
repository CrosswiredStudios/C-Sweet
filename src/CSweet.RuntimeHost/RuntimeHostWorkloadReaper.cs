using CSweet.AgentRuntime.Abstractions;

namespace CSweet.RuntimeHost;

public sealed class RuntimeHostWorkloadReaper(
    IEnumerable<IPlatformIsolationBackend> backends,
    ILogger<RuntimeHostWorkloadReaper> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            foreach (var reaper in backends.OfType<IPlatformWorkloadReaper>())
            {
                try
                {
                    var removed = await reaper.ReapAbandonedWorkloadsAsync(stoppingToken);
                    if (removed > 0)
                        logger.LogInformation(
                            "RuntimeHost destroyed {WorkloadCount} stopped, expired, or legacy runtime workloads.",
                            removed);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "RuntimeHost workload cleanup failed; the next periodic pass will retry.");
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
