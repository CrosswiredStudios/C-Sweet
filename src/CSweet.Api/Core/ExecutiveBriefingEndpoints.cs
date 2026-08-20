using CSweet.Application.Core;
using CSweet.Contracts.Core;
using CSweet.Api.Auth;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Core;

public static class ExecutiveBriefingEndpoints
{
    public static IEndpointRouteBuilder MapExecutiveBriefingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/core/organizations/{organizationId:guid}/executive-briefings");
        group.AddEndpointFilter(async (context, next) =>
        {
            var environment = context.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
            if (environment.IsEnvironment("Testing")) return await next(context);
            if (!Guid.TryParse(context.HttpContext.Request.RouteValues["organizationId"]?.ToString(), out var organizationId))
                return Results.NotFound();
            var memory = context.HttpContext.RequestServices.GetRequiredService<IAgentMemoryService>();
            return await memory.CanExploreAsync(organizationId, context.HttpContext.User.GetApplicationUserId(), context.HttpContext.RequestAborted)
                ? await next(context) : Results.Forbid();
        });
        group.MapGet("/settings", async (Guid organizationId, IExecutiveBriefingService service, CancellationToken token) =>
        {
            var settings = await service.GetSettingsAsync(organizationId, token);
            return settings is null ? Results.NoContent() : Results.Ok(settings);
        });
        var updateSettings = group.MapPut("/settings", async (Guid organizationId, UpdateExecutiveBriefingSettingsRequest request,
            IExecutiveBriefingService service, CancellationToken token) =>
        {
            var result = await service.UpdateSettingsAsync(organizationId, request, token);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });
        updateSettings.AddEndpointFilter(RequireLeadershipAsync);
        var run = group.MapPost("/run", async (Guid organizationId, IExecutiveBriefingService service, CancellationToken token) =>
        {
            var result = await service.QueueManualAsync(organizationId, token);
            return result.Succeeded ? Results.Accepted(value: result) : Results.BadRequest(result);
        });
        run.AddEndpointFilter(RequireLeadershipAsync);
        group.MapGet("/history", async (Guid organizationId, int? take, IExecutiveBriefingService service, CancellationToken token) =>
            Results.Ok(await service.ListHistoryAsync(organizationId, take ?? 20, token)));
        return endpoints;
    }

    private static async ValueTask<object?> RequireLeadershipAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var environment = context.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
        if (environment.IsEnvironment("Testing")) return await next(context);
        var applicationUserId = context.HttpContext.User.GetApplicationUserId();
        if (!applicationUserId.HasValue || !Guid.TryParse(
                context.HttpContext.Request.RouteValues["organizationId"]?.ToString(), out var organizationId))
            return Results.Forbid();
        var db = context.HttpContext.RequestServices.GetRequiredService<CSweetDbContext>();
        var allowed = await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId && x.IsActive &&
            (x.PermissionLevel == OrganizationPermissionLevel.Owner || x.PermissionLevel == OrganizationPermissionLevel.Manager),
            context.HttpContext.RequestAborted);
        return allowed ? await next(context) : Results.Forbid();
    }
}
