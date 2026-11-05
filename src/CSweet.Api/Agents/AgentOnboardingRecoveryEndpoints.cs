using CSweet.Api.Auth;
using CSweet.Contracts.Agents;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Agents;

public static class AgentOnboardingRecoveryEndpoints
{
    public static IEndpointRouteBuilder MapAgentOnboardingRecoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/agents/installations/{installationId:guid}/onboarding-recovery")
            .RequireAuthorization("PluginAdministration");
        group.MapGet("/", (Guid installationId, Guid organizationId, HttpContext http,
            AgentOnboardingRecoveryService service, CancellationToken ct) =>
            Handle(http, actor => service.PreviewAsync(actor, organizationId, installationId, ct)));
        group.MapGet("/receipt", (Guid installationId, Guid organizationId, HttpContext http,
            AgentOnboardingRecoveryService service, CancellationToken ct) =>
            Handle(http, actor => service.ReadReceiptAsync(actor, organizationId, installationId, ct)));
        group.MapPost("/", (Guid installationId, RecoverAgentOnboardingRequest request, HttpContext http,
            AgentOnboardingRecoveryService service, CancellationToken ct) =>
            Handle(http, actor => service.RecoverAsync(actor, request.OrganizationId, installationId, request.ReviewToken, ct)))
            .RequireRateLimiting(AgentRateLimiting.RunPolicy);
        return endpoints;
    }

    private static async Task<IResult> Handle<T>(HttpContext http, Func<Guid, Task<T>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actor = http.User.GetApplicationUserId();
        if (actor is null) return Results.Unauthorized();
        try { return Results.Ok(await action(actor.Value)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.Text.Json.JsonException or
            DbUpdateException or System.Data.Common.DbException)
        { return Results.Conflict(new { error = "onboarding_recovery_conflict", message = "The recovery state changed or is unavailable. Open a fresh review." }); }
    }
}
