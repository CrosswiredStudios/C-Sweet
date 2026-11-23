using CSweet.Application.Setup;
using CSweet.Contracts.Setup;
using CSweet.Office.Contracts.ControlPlane;

namespace CSweet.ExecutionGateway;

public static class OfficeLocalSetupEndpoints
{
    public static void MapOfficeLocalSetupEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/offices/local-sessions/compute-plan", async (HttpContext http, OfficeComputeSetupRequest request, IExecutionFleetService fleet, CancellationToken ct) =>
        {
            if (!http.Request.IsHttps) return Results.BadRequest();
            http.Response.Headers.CacheControl = "no-store";
            var plan = await fleet.PrepareHostedComputeAsync(request, ct);
            return plan is null ? Results.Unauthorized() : Results.Ok(plan);
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(4096));
        app.MapPost("/api/offices/local-sessions/update-prepare", async (
            BeginOfficeUpdatePreparationRequest request, IExecutionFleetService fleet, CancellationToken ct) =>
        {
            var result = await fleet.BeginOfficeUpdatePreparationAsync(request, ct);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });
        app.MapPost("/api/offices/local-sessions/update-progress", async (
            OfficeUpdatePreparationRequest request, IExecutionFleetService fleet, CancellationToken ct) =>
        {
            var result = await fleet.AdvanceOfficeUpdatePreparationAsync(request, ct);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });
        app.MapPost("/api/offices/local-sessions/redeem", async (
            RedeemAssistedOfficeSetupRequest request,
            IExecutionFleetService fleet,
            CancellationToken cancellationToken) =>
        {
            var result = await fleet.RedeemLocalSetupSessionAsync(request, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });
        app.MapPost("/api/offices/local-sessions/preflight", async (
            AssistedOfficePreflightRequest request,
            IExecutionFleetService fleet,
            CancellationToken cancellationToken) =>
        {
            var result = await fleet.PreflightLocalSetupSessionAsync(request, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });
        app.MapPost("/api/offices/local-sessions/result", async (
            ReportAssistedOfficeSetupResultRequest request,
            IExecutionFleetService fleet,
            CancellationToken cancellationToken) =>
            await fleet.ReportLocalSetupResultAsync(request, cancellationToken)
                ? Results.NoContent() : Results.Unauthorized());
        app.MapPost("/api/offices/local-sessions/removal-complete", async (
            CompleteAssistedOfficeRemovalRequest request,
            IExecutionFleetService fleet,
            CancellationToken cancellationToken) =>
            await fleet.CompleteLocalOfficeRemovalAsync(request, cancellationToken)
                ? Results.NoContent() : Results.Unauthorized());
        app.MapPost("/api/offices/local-sessions/enrollment-ready", async (
            RefreshLocalOfficeEnrollmentRequest request,
            IExecutionFleetService fleet,
            CancellationToken cancellationToken) =>
            await fleet.RefreshLocalSetupEnrollmentAsync(request, cancellationToken)
                ? Results.NoContent() : Results.Unauthorized());
    }
}
