using CSweet.Agent.SDK;
using CSweet.Api.Auth;
using CSweet.Application.Agents;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Agents;

public static class AgentCatalogEndpoints
{
    public static IEndpointRouteBuilder MapAgentCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/agents/catalog-profile", (
            string agentReference, IAgentCatalogProfileService profiles, CancellationToken cancellationToken) =>
            ReadProfileAsync(null, agentReference, profiles, cancellationToken));

        endpoints.MapGet("/api/core/organizations/{organizationId:guid}/agents/catalog-profile", async (
            Guid organizationId, string agentReference, HttpContext http, CSweetDbContext db,
            IAgentCatalogProfileService profiles, CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetApplicationUserId();
            if (!userId.HasValue || !await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organizationId && x.ApplicationUserId == userId && x.IsActive, cancellationToken))
                return Results.Forbid();
            return await ReadProfileAsync(organizationId, agentReference, profiles, cancellationToken);
        });

        endpoints.MapGet("/api/agents/available", (
            string? role,
            string? q,
            string? capabilities,
            string? category,
            decimal? maxPrice,
            string? currency,
            string? sort,
            int? limit,
            string? roleCategory,
            string? specializations,
            IAgentCatalogService catalog,
            CancellationToken cancellationToken) =>
            catalog.GetAvailableAgentsAsync(
                null,
                Query(role, q, capabilities, category, maxPrice, currency, sort, limit, roleCategory, specializations),
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
            string? roleCategory,
            string? specializations,
            HttpContext http,
            CSweetDbContext db,
            IAgentCatalogService catalog,
            IHiringService hiring,
            Guid? recommendationId,
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
            var query = Query(role, q, capabilities, category, maxPrice, currency, sort, limit, roleCategory, specializations);
            if (recommendationId.HasValue)
            {
                var context = await hiring.GetCandidateSearchContextAsync(organizationId, recommendationId.Value, cancellationToken);
                if (context is null)
                    return Results.Problem("The hiring recommendation is unavailable.", statusCode: 404);
                query = query with
                {
                    Role = context.RoleCategoryKey is null ? context.RoleTitle : null,
                    RoleCategoryKey = context.RoleCategoryKey,
                    PreferredSpecializationKeys = context.PreferredSpecializationKeys
                };
            }
            var result = await catalog.GetAvailableAgentsAsync(
                organizationId,
                query,
                cancellationToken);
            return Results.Ok(result);
        });

        return endpoints;
    }

    private static async Task<IResult> ReadProfileAsync(Guid? organizationId, string agentReference,
        IAgentCatalogProfileService profiles, CancellationToken cancellationToken)
    {
        try
        {
            var profile = await profiles.GetAsync(organizationId, agentReference, cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        }
        catch (Exception exception) when (exception is AgentImportPreviewException or ArgumentException or System.Text.Json.JsonException or HttpRequestException or IOException)
        {
            return Results.Problem("The agent's access details could not be loaded. Try again or continue to the hire review.", statusCode: 502);
        }
    }

    private static AvailableAgentSearchQuery Query(
        string? role,
        string? search,
        string? capabilities,
        string? category,
        decimal? maximumPrice,
        string? currency,
        string? sort,
        int? limit,
        string? roleCategory,
        string? specializations) =>
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
            Math.Clamp(limit ?? 25, 1, 100),
            roleCategory,
            string.IsNullOrWhiteSpace(specializations)
                ? []
                : specializations.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
