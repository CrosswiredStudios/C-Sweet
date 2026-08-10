using CSweet.Application.Core;
using CSweet.Api.Auth;
using CSweet.Contracts.Memory;
using System.Security.Claims;

namespace CSweet.Api.Core;

public static class AgentMemoryEndpoints
{
    public static IEndpointRouteBuilder MapAgentMemoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/core/organizations/{organizationId:guid}/employees/{employeeId:guid}/memory");

        group.MapGet("/summary", async (Guid organizationId, Guid employeeId, ClaimsPrincipal principal,
            IAgentMemoryService memory, IEmployeeHierarchyAccessService hierarchy, CancellationToken cancellationToken) =>
        {
            if (!await CanInspectAsync(organizationId, employeeId, principal, hierarchy, cancellationToken)) return Results.Forbid();
            var summary = await memory.GetSummaryAsync(organizationId, employeeId, cancellationToken);
            return summary is null ? Results.NotFound() : Results.Ok(summary);
        });

        group.MapGet("/items", async (
            Guid organizationId, Guid employeeId, string? kind, string? layer, string? search, Guid? userId,
            string? scope, string? @namespace, string? source, string? sensitivity, string? state, string? confirmationState,
            DateTimeOffset? from, DateTimeOffset? to, string? cursor, int? limit,
            ClaimsPrincipal principal, IAgentMemoryService memory, IEmployeeHierarchyAccessService hierarchy,
            CancellationToken cancellationToken) =>
        {
            if (!await CanInspectAsync(organizationId, employeeId, principal, hierarchy, cancellationToken)) return Results.Forbid();
            var page = await memory.BrowseAsync(organizationId, employeeId,
                new AgentMemoryQuery(kind ?? layer, search, userId, scope ?? @namespace, source, sensitivity,
                    state ?? confirmationState, from, to, cursor, limit ?? 50),
                cancellationToken);
            return page is null ? Results.NotFound() : Results.Ok(page);
        });

        group.MapGet("/items/{memoryId:guid}", async (
            Guid organizationId, Guid employeeId, Guid memoryId,
            ClaimsPrincipal principal, IAgentMemoryService memory, IEmployeeHierarchyAccessService hierarchy,
            CancellationToken cancellationToken) =>
        {
            if (!await CanInspectAsync(organizationId, employeeId, principal, hierarchy, cancellationToken)) return Results.Forbid();
            var item = await memory.GetItemAsync(organizationId, employeeId, memoryId, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        group.MapGet("/graph", async (
            Guid organizationId, Guid employeeId, string? search, Guid? userId, int? limit,
            ClaimsPrincipal principal, IAgentMemoryService memory, IEmployeeHierarchyAccessService hierarchy,
            CancellationToken cancellationToken) =>
        {
            if (!await CanInspectAsync(organizationId, employeeId, principal, hierarchy, cancellationToken)) return Results.Forbid();
            var graph = await memory.GetGraphAsync(organizationId, employeeId, search, userId, limit ?? 100, cancellationToken);
            return graph is null ? Results.NotFound() : Results.Ok(graph);
        });

        return endpoints;
    }

    private static async Task<bool> CanInspectAsync(Guid organizationId, Guid employeeId,
        ClaimsPrincipal principal, IEmployeeHierarchyAccessService hierarchy, CancellationToken token)
    {
        var applicationUserId = principal.GetApplicationUserId();
        if (!applicationUserId.HasValue) return false;
        var actorId = await hierarchy.ResolveOrganizationUserIdAsync(organizationId,
            applicationUserId.Value, token);
        return actorId.HasValue && await hierarchy.CanAccessSensitiveAsync(
            organizationId, actorId.Value, employeeId, token);
    }
}
