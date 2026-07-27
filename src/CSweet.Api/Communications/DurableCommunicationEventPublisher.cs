using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Infrastructure.Setup;

namespace CSweet.Api.Communications;

public sealed class DurableCommunicationEventPublisher(
    AgentWorkRouter router) : ICommunicationEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(
        CommunicationEventPublication publication,
        CancellationToken cancellationToken = default)
    {
        await router.EnqueueEventAsync(
            publication.Envelope.OrganizationId.ToString("D"),
            publication.EventType,
            JsonSerializer.SerializeToElement(publication.Envelope, JsonOptions),
            publication.Envelope.EventId.ToString("N"),
            publication.TargetInstallationId,
            requireSubscription: true,
            cancellationToken: cancellationToken);
    }
}

public sealed class CommunicationEventOutboxWorker(
    IServiceProvider services,
    ILogger<CommunicationEventOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<ICommunicationEventOutboxDispatcher>();
                var publisher = scope.ServiceProvider.GetRequiredService<ICommunicationEventPublisher>();
                var count = await dispatcher.DispatchBatchAsync(
                    publisher,
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
                logger.LogWarning(exception, "Communication event dispatch failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
