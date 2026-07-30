using CSweet.Api.Auth;
using CSweet.Application.Core;

namespace CSweet.Api.Core;

public static class ApprovalEndpoints
{
    public static IEndpointRouteBuilder MapApprovalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/core/organizations/{organizationId:guid}/approvals",
            async (
                Guid organizationId,
                HttpContext http,
                IApprovalDashboardService service,
                CancellationToken cancellationToken) =>
            {
                var applicationUserId = http.User.GetApplicationUserId();
                if (!applicationUserId.HasValue) return Results.Forbid();
                try
                {
                    return Results.Ok(await service.GetAsync(
                        organizationId,
                        applicationUserId.Value,
                        cancellationToken));
                }
                catch (UnauthorizedAccessException exception)
                {
                    return Results.Json(
                        new { error = "approval_access_denied", message = exception.Message },
                        statusCode: StatusCodes.Status403Forbidden);
                }
            });
        return endpoints;
    }
}
