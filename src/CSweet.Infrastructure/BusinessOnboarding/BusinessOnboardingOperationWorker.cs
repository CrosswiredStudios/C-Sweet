using CSweet.Application.BusinessOnboarding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.BusinessOnboarding;

public sealed class BusinessOnboardingOperationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BusinessOnboardingOperationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IBusinessOnboardingOperationService>();
                await service.ProcessNextAsync(_leaseOwner, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The durable business onboarding worker iteration failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
