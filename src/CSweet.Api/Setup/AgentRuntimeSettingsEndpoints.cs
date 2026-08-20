using CSweet.Application.Setup;
using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Setup;

public static class AgentRuntimeSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAgentRuntimeSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/agent-runtime/settings");

        group.MapGet("", async (
            IAgentRuntimeSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var settings = await settingsService.GetAsync(cancellationToken);
            return Results.Ok(settings);
        });

        group.MapPut("", async (
            UpdateAgentRuntimeSettingsRequest request,
            IAgentRuntimeSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var result = await settingsService.UpdateAsync(request, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result)
                : Results.BadRequest(result);
        });

        group.MapPost("/recover", async (
            IAgentRuntimeManager runtimeManager,
            CSweetDbContext db,
            CancellationToken cancellationToken) =>
        {
            var changed = await runtimeManager.ReconcileAsync(cancellationToken);
            var settings = await db.AgentRuntimeGlobalSettings.AsNoTracking().SingleAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var stoppingCutoff = now.AddSeconds(-(settings.WorkloadStopGraceSeconds + 5));
            var startingCutoff = now.AddSeconds(-(settings.WorkloadStartTimeoutSeconds + 5));
            var remainingStuck = await db.AgentRuntimeInstances.AsNoTracking().CountAsync(instance =>
                (instance.Status == AgentRuntimeStatus.Stopping &&
                    (instance.Events.Any(e => e.Status == AgentRuntimeStatus.Stopping && e.OccurredAt <= stoppingCutoff) ||
                     (!instance.Events.Any(e => e.Status == AgentRuntimeStatus.Stopping) && instance.QueuedAt <= stoppingCutoff))) ||
                (instance.Status == AgentRuntimeStatus.Starting &&
                    (instance.Events.Any(e => e.Status == AgentRuntimeStatus.Starting && e.OccurredAt <= startingCutoff) ||
                     (!instance.Events.Any(e => e.Status == AgentRuntimeStatus.Starting) && instance.QueuedAt <= startingCutoff))),
                cancellationToken);
            var succeeded = remainingStuck == 0;
            var result = new AgentRuntimeSettingsActionResponse(
                succeeded,
                succeeded
                    ? changed == 0
                        ? "Runtime reconciliation completed; no stuck or queued runtimes required action."
                        : $"Runtime reconciliation completed and advanced {changed} runtime{(changed == 1 ? string.Empty : "s")}."
                    : $"Runtime reconciliation advanced {changed} runtime{(changed == 1 ? string.Empty : "s")}, but {remainingStuck} stale runtime{(remainingStuck == 1 ? string.Empty : "s")} still require attention. Review the runtime logs and retry.",
                null);
            return succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });

        return endpoints;
    }
}
