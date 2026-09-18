using CSweet.Application.Communications;
using CSweet.Application.SourceControl;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.SourceControl;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.SourceControl;

public sealed class TaskDeliveryWorker(IServiceScopeFactory scopes, ILogger<TaskDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var discovery = scopes.CreateScope();
                var db = discovery.ServiceProvider.GetRequiredService<CSweetDbContext>();
                var work = await db.TaskDeliveryReviews.AsNoTracking().Where(x => x.Status == "AwaitingApproval" || x.Status == "Merging")
                    .OrderBy(x => x.UpdatedAt).Select(x => new { x.OrganizationId, x.Id }).Take(32).ToListAsync(stoppingToken);
                foreach (var next in work)
                {
                    using var scope = scopes.CreateScope();
                    var services = scope.ServiceProvider;
                    var store = services.GetRequiredService<CSweetDbContext>();
                    var service = services.GetRequiredService<TaskDeliveryService>();
                    try
                    {
                        await service.AdvanceMergeAsync(next.OrganizationId, next.Id, services.GetRequiredService<ITrustedSourceControlHostClient>(), stoppingToken);
                        var review = await store.TaskDeliveryReviews.SingleAsync(x => x.Id == next.Id, stoppingToken);
                        await service.PresentDecisionAsync(review, services.GetRequiredService<ICommunicationHubService>(), services.GetRequiredService<IExecutiveDecisionService>(), stoppingToken);
                        // Rotate bounded discovery even when a human has not answered yet.
                        review.UpdatedAt = DateTimeOffset.UtcNow;
                        await store.SaveChangesAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        logger.LogWarning(error, "Task review {ReviewId} could not advance", next.Id);
                        // Retain an actionable error for current-state reads; an uncertain provider outcome
                        // remains Merging and is reconciled using the same key rather than republished.
                        store.ChangeTracker.Clear();
                        await service.RecordFailureAsync(next.OrganizationId, next.Id, error.Message, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Task delivery recovery could not run"); }
        }
    }
}
