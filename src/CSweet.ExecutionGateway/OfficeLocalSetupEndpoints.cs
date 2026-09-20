using CSweet.Application.Setup;
using CSweet.Contracts.Setup;
using CSweet.Office.Contracts.ControlPlane;

namespace CSweet.ExecutionGateway;

public static class OfficeLocalSetupEndpoints
{
    public static void MapOfficeLocalSetupEndpoints(this IEndpointRouteBuilder app)
    {
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
