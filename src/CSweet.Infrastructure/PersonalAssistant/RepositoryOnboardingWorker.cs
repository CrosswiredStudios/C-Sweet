using CSweet.Application.PersonalAssistant;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.PersonalAssistant;

/// <summary>Platform queue maintenance; agents consume the standard SDK work inbox.</summary>
public sealed class RepositoryOnboardingWorker(IServiceScopeFactory scopes, ILogger<RepositoryOnboardingWorker> logger) : BackgroundService
{
    private readonly string workerId = $"repository-onboarding:{Guid.NewGuid():N}";
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IRepositoryOnboardingService>();
                await service.ProcessNextAsync(workerId, token);
                await RecoverResultsAsync(scope.ServiceProvider, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Repository onboarding maintenance will retry."); }
            if (!await timer.WaitForNextTickAsync(token)) break;
        }
    }

    private static async Task RecoverResultsAsync(IServiceProvider services, CancellationToken token)
    {
        var db = services.GetRequiredService<CSweetDbContext>();
        var inbox = services.GetRequiredService<AgentWorkInbox>();
        var observer = services.GetRequiredService<PersonalAssistantResultObserver>();
        var now = DateTimeOffset.UtcNow;
        var ids = db.PersonalAssistantMessages.Where(x => x.Status == "Pending" && x.AgentWorkItemId != null)
            .Select(x => x.AgentWorkItemId!.Value).Union(db.RepositoryOnboardingDrafts.Where(x => x.Status == "Analyzing" && x.AgentWorkItemId != null)
                .Select(x => x.AgentWorkItemId!.Value));
        var finished = await db.AgentWorkItems.Where(x => ids.Contains(x.Id) &&
            (x.Status != AgentWorkStatus.Pending && x.Status != AgentWorkStatus.Leased || x.DeadlineAt <= now))
            .OrderBy(x => x.CreatedAt).Take(40).ToListAsync(token);
        foreach (var work in finished)
        {
            if (work.Status is AgentWorkStatus.Pending or AgentWorkStatus.Leased)
                await inbox.CancelAsync(work.Id, "The Personal Assistant work deadline elapsed.", token);
            var state = await inbox.ReadStateAsync(work.Id, token);
            await observer.ObserveAsync(work, state.Completion?.Succeeded == true, state.Completion?.Value, token);
        }
        await db.SaveChangesAsync(token);
        var installations = await db.AgentWorkItems.Where(x => ids.Contains(x.Id) && x.Status == AgentWorkStatus.Pending &&
            x.DeadlineAt > now && x.AgentInstallation!.PackageVersion!.Status == AgentPackageVersionStatus.Built)
            .Select(x => x.AgentInstallationId).Distinct().Take(20).ToListAsync(token);
        var scheduler = services.GetRequiredService<IPersonalAssistantRuntimeScheduler>();
        foreach (var installation in installations) await scheduler.WakeAsync(installation, token);
    }
}
