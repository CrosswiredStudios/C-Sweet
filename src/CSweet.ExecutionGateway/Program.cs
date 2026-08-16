using CSweet.Office.Contracts.ControlPlane;
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
app.MapGrpcService<OfficeGatewayService>();
app.MapGet("/api/offices/assignment-trust", (ExecutionAssignmentSigner signer) =>
    Results.Ok(new HeadquartersAssignmentTrustResponse(
        signer.KeyId,
        signer.ExportPublicKeyBase64())));
app.MapPost("/api/offices/claim", async (
    ClaimOfficeRequest request,
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
app.MapPost("/api/offices/local-sessions/redeem", async (
    RedeemAssistedOfficeSetupRequest request,
    IExecutionFleetService fleet,
    CancellationToken cancellationToken) =>
{
    var result = await fleet.RedeemLocalSetupSessionAsync(request, cancellationToken);
    return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
});
app.MapPost("/api/offices/{officeId:guid}/heartbeat", async (
    Guid officeId,
    OfficeHeartbeatRequest request,
    IExecutionFleetService fleet,
    CancellationToken cancellationToken) =>
    await fleet.RecordHeartbeatAsync(officeId, request, cancellationToken)
        ? Results.NoContent() : Results.Unauthorized());
app.MapPost("/api/offices/{officeId:guid}/certificate", async (
    Guid officeId,
    OfficeCertificateRequest request,
    HttpContext context,
    IExecutionFleetService fleet,
    CancellationToken cancellationToken) =>
{
    OfficeCertificateResponse result;
    if (!string.IsNullOrWhiteSpace(request.EnrollmentReceipt))
    {
        result = await fleet.GetOperationalCertificateAsync(officeId, request, cancellationToken);
    }
    else
    {
        var certificate = context.Connection.ClientCertificate;
        result = certificate is null
            ? new(false, "node_certificate_rejected", "A current operational Office certificate is required.", null, null, null)
            : await fleet.RotateOperationalCertificateAsync(
                officeId, certificate.Thumbprint, certificate.SerialNumber, cancellationToken);
    }
    return result.Succeeded ? Results.Ok(result) : Results.Unauthorized();
});
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Ok(new
{
    service = "CSweet.ExecutionGateway",
    protocol = "csweet-office-v1",
    status = "ok"
}));
app.Run();

public partial class Program;
