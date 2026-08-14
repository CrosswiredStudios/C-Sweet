using CSweet.SatelliteOffice.Contracts.ControlPlane;
using CSweet.Application.Setup;
using CSweet.ExecutionGateway;
using CSweet.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Https;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(kestrel => kestrel.ConfigureHttpsDefaults(https =>
{
    https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
    // Enrollment pins the exact certificate. TLS accepts the presented chain here so
    // private-deployment certificates can be validated against authoritative node state.
    https.ClientCertificateValidation = (_, _, _) => true;
}));
builder.AddServiceDefaults();
builder.AddCSweetInfrastructure();
builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = 16 * 1024 * 1024;
    options.MaxSendMessageSize = 16 * 1024 * 1024;
});
builder.Services.Configure<ExecutionGatewayOptions>(
    builder.Configuration.GetSection(ExecutionGatewayOptions.SectionName));
builder.Services.AddSingleton<ExecutionAssignmentSigner>();

var app = builder.Build();
app.MapGrpcService<SatelliteOfficeGatewayService>();
app.MapGet("/api/satellite-offices/assignment-trust", (ExecutionAssignmentSigner signer) =>
    Results.Ok(new HeadquartersAssignmentTrustResponse(
        signer.KeyId,
        signer.ExportPublicKeyBase64())));
app.MapPost("/api/satellite-offices/claim", async (
    ClaimSatelliteOfficeRequest request,
    IExecutionFleetService fleet,
    ExecutionAssignmentSigner signer,
    CancellationToken cancellationToken) =>
{
    var result = await fleet.ClaimNodeAsync(request, cancellationToken);
    if (result.Succeeded)
        result = result with
        {
            AssignmentSigningKeyId = signer.KeyId,
            AssignmentVerificationPublicKeyBase64 = signer.ExportPublicKeyBase64()
        };
    return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
});
app.MapPost("/api/satellite-offices/{satelliteOfficeId:guid}/heartbeat", async (
    Guid satelliteOfficeId,
    SatelliteOfficeHeartbeatRequest request,
    IExecutionFleetService fleet,
    CancellationToken cancellationToken) =>
    await fleet.RecordHeartbeatAsync(satelliteOfficeId, request, cancellationToken)
        ? Results.NoContent() : Results.Unauthorized());
app.MapPost("/api/satellite-offices/{satelliteOfficeId:guid}/certificate", async (
    Guid satelliteOfficeId,
    SatelliteOfficeCertificateRequest request,
    HttpContext context,
    IExecutionFleetService fleet,
    CancellationToken cancellationToken) =>
{
    SatelliteOfficeCertificateResponse result;
    if (!string.IsNullOrWhiteSpace(request.EnrollmentReceipt))
    {
        result = await fleet.GetOperationalCertificateAsync(satelliteOfficeId, request, cancellationToken);
    }
    else
    {
        var certificate = context.Connection.ClientCertificate;
        result = certificate is null
            ? new(false, "node_certificate_rejected", "A current operational Satellite Office certificate is required.", null, null, null)
            : await fleet.RotateOperationalCertificateAsync(
                satelliteOfficeId, certificate.Thumbprint, certificate.SerialNumber, cancellationToken);
    }
    return result.Succeeded ? Results.Ok(result) : Results.Unauthorized();
});
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Ok(new
{
    service = "CSweet.ExecutionGateway",
    protocol = "csweet-satellite-office-v1",
    status = "ok"
}));
app.Run();

public partial class Program;
