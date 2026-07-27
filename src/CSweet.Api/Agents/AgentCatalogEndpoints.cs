using CSweet.Agent.SDK;
using CSweet.Api.Auth;
using CSweet.Application.Agents;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Agents;

public static class AgentCatalogEndpoints
{
    public static IEndpointRouteBuilder MapAgentCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/agents/available", (
            string? role,
            string? q,
            string? capabilities,
            string? category,
            decimal? maxPrice,
            string? currency,
            string? sort,
            int? limit,
            IAgentCatalogService catalog,
            CancellationToken cancellationToken) =>
            catalog.GetAvailableAgentsAsync(
                null,
                Query(role, q, capabilities, category, maxPrice, currency, sort, limit),
                cancellationToken));

        endpoints.MapGet("/api/core/organizations/{organizationId:guid}/agents/available", async (
            Guid organizationId,
            string? role,
            string? q,
            string? capabilities,
            string? category,
            decimal? maxPrice,
            string? currency,
            string? sort,
            int? limit,
            HttpContext http,
            CSweetDbContext db,
            IAgentCatalogService catalog,
            CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Forbid();
            var member = await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organizationId &&
                x.ApplicationUserId == applicationUserId &&
                x.IsActive,
                cancellationToken);
            if (!member) return Results.Forbid();
            var result = await catalog.GetAvailableAgentsAsync(
                organizationId,
                Query(role, q, capabilities, category, maxPrice, currency, sort, limit),
                cancellationToken);
            return Results.Ok(result);
        });

        return endpoints;
    }

    private static AvailableAgentSearchQuery Query(
        string? role,
        string? search,
        string? capabilities,
        string? category,
        decimal? maximumPrice,
        string? currency,
        string? sort,
        int? limit) =>
        new(
            role,
            search,
            string.IsNullOrWhiteSpace(capabilities)
                ? []
                : capabilities.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            category,
            maximumPrice,
            currency,
            sort,
            Math.Clamp(limit ?? 25, 1, 100));
}
