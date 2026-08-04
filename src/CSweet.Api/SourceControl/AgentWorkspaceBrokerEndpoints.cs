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
        return endpoints;
    }
}
