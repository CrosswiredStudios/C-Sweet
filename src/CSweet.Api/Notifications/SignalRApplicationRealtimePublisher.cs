using CSweet.Application.Notifications;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Notifications;

public sealed class SignalRApplicationRealtimePublisher(
    IHubContext<AppEventsHub> hub,
    IServiceScopeFactory scopeFactory) : IApplicationRealtimePublisher
{
    public async Task PublishAsync(ApplicationRealtimePublication publication, CancellationToken cancellationToken = default)
    {
        if (publication.RecipientOrganizationUserIds.Count == 0) return;
        var recipientIds = publication.RecipientOrganizationUserIds.Distinct().ToList();
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var identities = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => recipientIds.Contains(x.Id) && x.IsActive)
            .Select(x => new { x.Id, x.ApplicationUserId })
            .ToListAsync(cancellationToken);
        var applicationUserIds = identities.Where(x => x.ApplicationUserId.HasValue)
            .Select(x => x.ApplicationUserId!.Value).Distinct().ToList();
        var applicationBackedOrganizationUserIds = identities.Where(x => x.ApplicationUserId.HasValue)
            .Select(x => x.Id).ToHashSet();
        var perspectiveGroups = publication.Envelope.OrganizationId is Guid organizationId &&
                                publication.Envelope.EventType.StartsWith(
                                    "com.csweet.communication.", StringComparison.Ordinal)
            ? identities.Select(x => AppEventGroups.CommunicationPerspective(organizationId, x.Id))
            : [];
        var groups = applicationUserIds.Select(AppEventGroups.ApplicationUser)
            .Concat(recipientIds.Where(x => !applicationBackedOrganizationUserIds.Contains(x))
                .Select(AppEventGroups.OrganizationUser))
            .Concat(perspectiveGroups)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (groups.Count > 0)
            await hub.Clients.Groups(groups).SendAsync("AppEvent", publication.Envelope, cancellationToken);
    }
}

public sealed class ApplicationRealtimeOutboxWorker(
    IServiceProvider services,
    ILogger<ApplicationRealtimeOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IApplicationRealtimeOutboxDispatcher>();
                var publisher = scope.ServiceProvider.GetRequiredService<IApplicationRealtimePublisher>();
                var count = await dispatcher.DispatchBatchAsync(publisher, cancellationToken: stoppingToken);
                await Task.Delay(count > 0 ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Application realtime dispatch failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
