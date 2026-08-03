using CSweet.Api.Auth;
using CSweet.Application.Analytics;
using CSweet.Contracts.Analytics;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Analytics;

public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/analytics");

        group.MapGet("/inference", async (
            Guid organizationId,
            string? window,
            HttpContext http,
            CSweetDbContext db,
            IInferenceAnalyticsService analytics,
            CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue)
            {
                return Results.Unauthorized();
            }

            var member = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId &&
                x.ApplicationUserId == applicationUserId.Value &&
                x.IsActive,
                cancellationToken);
            if (member is not
                {
                    EmployeeType: EmployeeType.Human,
                    PermissionLevel: OrganizationPermissionLevel.Owner or OrganizationPermissionLevel.Manager
                })
            {
                return Results.Forbid();
            }

            if (!TryParseWindow(window, out var parsedWindow))
            {
                return Results.BadRequest(new
                {
                    error = "The analytics window must be one of: 24h, 7d, or 30d."
                });
            }

            return Results.Ok(await analytics.GetAsync(
                organizationId,
                parsedWindow,
                cancellationToken));
        });

        return endpoints;
    }

    private static bool TryParseWindow(
        string? value,
        out InferenceAnalyticsWindow window)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "30d":
                window = InferenceAnalyticsWindow.Last30Days;
                return true;
            case "24h":
                window = InferenceAnalyticsWindow.Last24Hours;
                return true;
            case "7d":
                window = InferenceAnalyticsWindow.Last7Days;
                return true;
            default:
                window = default;
                return false;
        }
    }
}
