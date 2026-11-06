using CSweet.Api.Auth;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Core;

namespace CSweet.Api.Agents;

public static class RunInspectorEndpoints
{
    public static IEndpointRouteBuilder MapRunInspectorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/core/organizations/{organizationId:guid}/runs/{employeeId:guid}",
            async (Guid organizationId, Guid employeeId, Guid? attemptId, Guid? modelRunId, Guid? workItemId,
                long? afterSequence, HttpContext http, RunInspectorService inspector, IAuditEventWriter audit, CancellationToken token) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                var user = http.User.GetApplicationUserId();
                if (user is null) return Results.Unauthorized();
                try
                {
                    var result = await inspector.ReadAsync(organizationId, user.Value, employeeId, attemptId, modelRunId, workItemId, afterSequence ?? 0, token);
                    await audit.AppendAsync(new AuditEventWriteRequest("security.run-inspector.read", "SecurityAccess",
                        OrganizationId: organizationId, EntityType: attemptId.HasValue ? "AgentWorkAttempt" : "AgentRunLog",
                        EntityId: attemptId ?? modelRunId, Summary: "Authorized execution evidence inspected.",
                        Actor: new AuditActor("Human", ApplicationUserId: user)), token);
                    return Results.Ok(result);
                }
                catch (UnauthorizedAccessException) { return Results.Forbid(); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
            }).RequireAuthorization();
        return endpoints;
    }
}
