using CSweet.Infrastructure.SourceControl;
using CSweet.TrustedServices;

namespace CSweet.Api.SourceControl;

public static class AgentWorkspaceBrokerEndpoints
{
    public static IEndpointRouteBuilder MapAgentWorkspaceBrokerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/agent-broker/v2/workspaces/prepare", async (
                AgentBrokerWorkspacePrepareRequest request,
                IAgentWorkspaceBroker broker,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    return Results.Ok(await broker.PrepareAsync(request, cancellationToken));
                }
                catch (UnauthorizedAccessException)
                {
                    return Results.Json(
                        new { error = "workspace_assignment_rejected" },
                        statusCode: StatusCodes.Status403Forbidden);
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest(new { error = "workspace_request_invalid" });
                }
                catch (InvalidDataException)
                {
                    return Results.UnprocessableEntity(new { error = "workspace_artifact_rejected" });
                }
                catch (InvalidOperationException)
                {
                    return Results.Conflict(new { error = "workspace_prepare_unavailable" });
                }
            })
            .AllowAnonymous();
        endpoints.MapPost("/agent-broker/v2/workspaces/operate", async (
            AgentBrokerWorkspaceOperationRequest request, IAgentWorkspaceBroker broker, HttpContext http,
            IConfiguration configuration, CancellationToken ct) =>
        {
            try
            {
                var publicBase = configuration["CSweet:PublicAppUrl"] ?? configuration["CSweet:Smtp:PublicAppUrl"]
                    ?? $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
                return Results.Ok(await broker.ExecuteAsync(request, publicBase, ct));
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (ArgumentException) { return Results.BadRequest(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException)
            { return Results.Conflict(new { error = "workspace_operation_failed" }); }
        }).AllowAnonymous();
        return endpoints;
    }
}
