using CSweet.Application.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentRuntimeStartupCleanupWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<AgentRuntimeStartupCleanupWorker> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider
                .GetRequiredService<AgentRuntimeStartupCleanupService>()
                .CleanupAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Agent runtime startup cleanup failed; normal reconciliation will continue.");
        }

        // Build recovery is independent of workload cleanup: a restart that
        // interrupts an agent install must not leave the definition stuck in
        // Updating behind a job no worker will ever pick up.
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var reconciled = await scope.ServiceProvider
                .GetRequiredService<IAgentBuildService>()
                .RecoverInterruptedAsync(cancellationToken);
            if (reconciled > 0)
            {
                logger.LogInformation(
                    "Recovered {Count} agent build job(s) interrupted by the restart.",
                    reconciled);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Agent build recovery failed; interrupted installs can be retried from the Agents settings page.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
