using CSweet.Api.Auth;
using CSweet.Application.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.WorkManagement;

public static class WorkDeliveryEndpoints
{
    public static IEndpointRouteBuilder MapWorkDeliveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/work/delivery").RequireAuthorization();
        group.MapPost("/read", (Guid organizationId, ReadWorkDeliveryPlansRequest request, HttpContext http, IWorkDeliveryService service, CancellationToken ct) =>
            Handle(http, actor => service.ReadAsync(organizationId, actor, request, ct)));
        group.MapPost("/configure", (Guid organizationId, ConfigureWorkDeliveryPlanRequest request, HttpContext http, IWorkDeliveryService service, CancellationToken ct) =>
            Handle(http, actor => service.ConfigureAsync(organizationId, actor, request, ct)));
        group.MapPost("/control", (Guid organizationId, ControlWorkDeliveryPlanRequest request, HttpContext http, IWorkDeliveryService service, CancellationToken ct) =>
            Handle(http, actor => service.ControlAsync(organizationId, actor, request, ct)));
        group.MapPost("/accept", (Guid organizationId, DecideWorkDeliveryAcceptanceRequest request, HttpContext http, IWorkDeliveryService service, CancellationToken ct) =>
            Handle(http, actor => service.AcceptAsync(organizationId, actor, request, ct)));
        group.MapPost("/recover", (Guid organizationId, RecoverWorkDeliveryRequest request, HttpContext http, IWorkDeliveryService service, CancellationToken ct) =>
            Handle(http, actor => service.RecoverAsync(organizationId, actor, request, ct)));
        group.MapPost("/review", (Guid organizationId, CompleteWorkDeliveryReviewRequest request, HttpContext http, IWorkDeliveryService service, CancellationToken ct) =>
            Handle(http, actor => service.CompleteReviewAsync(organizationId, actor, request, ct)));
        group.MapPost("/evidence", (Guid organizationId, ReadWorkDeliveryEvidenceRequest request, HttpContext http, IWorkDeliveryService service, CancellationToken ct) =>
            Handle(http, actor => service.ReadEvidenceAsync(organizationId, actor, request, ct)));
        group.MapPost("/tasks/finalize", (Guid organizationId, FinalizeWorkItemDeliveryRequest request, HttpContext http, IWorkDeliveryService service, CancellationToken ct) =>
            Handle(http, actor => service.FinalizeTaskAsync(organizationId, actor, request, ct)));
        group.MapGet("/plans/{planId:guid}/executions/{executionId:guid}/repositories/{repositoryId:guid}/archive",
            async (Guid organizationId, Guid planId, Guid executionId, Guid repositoryId, HttpContext http, IWorkDeliveryService service, CancellationToken ct) =>
            {
                var actor = http.User.GetApplicationUserId();
                if (!actor.HasValue) return Results.Unauthorized();
                try
                {
                    var evidence = await service.ReadEvidenceAsync(organizationId, actor.Value, new(planId, executionId, repositoryId), ct);
                    return evidence.Archive is { } archive
                        ? Results.File(archive, "application/zip", $"candidate-{repositoryId:N}-{evidence.CommitSha}.zip")
                        : Results.NotFound();
                }
                catch (UnauthorizedAccessException) { return Results.Forbid(); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (InvalidOperationException error) { return Results.Conflict(new { error = "candidate_unavailable", message = error.Message }); }
            });
        return endpoints;
    }
    private static async Task<IResult> Handle<T>(HttpContext http, Func<Guid, Task<T>> action)
    {
        var actor = http.User.GetApplicationUserId();
        if (!actor.HasValue) return Results.Unauthorized();
        try { return Results.Ok(await action(actor.Value)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (DbUpdateConcurrencyException error) { return Results.Conflict(new { error = "revision_conflict", message = error.Message }); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { return Results.BadRequest(new { error = "delivery_blocked", message = error.Message }); }
    }
}
