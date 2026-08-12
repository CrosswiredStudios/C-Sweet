using CSweet.Application.Setup;
using CSweet.Contracts.Setup;
using CSweet.ExecutionGateway;
using CSweet.Infrastructure;
using System.Security.Cryptography;
using System.Text;
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
app.MapGrpcService<ExecutionNodeGatewayService>();
app.MapPost("/api/execution-nodes/claim", async (
    ClaimExecutionNodeRequest request,
    IExecutionFleetService fleet,
    CancellationToken cancellationToken) =>
{
    var result = await fleet.ClaimNodeAsync(request, cancellationToken);
    return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
});
app.MapPost("/api/execution-nodes/development-loopback-claim", async (
    HttpContext context,
    DevelopmentExecutionNodeClaimRequest request,
    IExecutionFleetService fleet,
    IHostEnvironment environment,
    Microsoft.Extensions.Options.IOptions<ExecutionGatewayOptions> gatewayOptions,
    CancellationToken cancellationToken) =>
{
    var remote = context.Connection.RemoteIpAddress;
    var expected = gatewayOptions.Value.DevelopmentBootstrapKey;
    var supplied = request.BootstrapKey ?? string.Empty;
    var keyMatches = expected.Length >= 32 && supplied.Length == expected.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied));
    if (!environment.IsDevelopment() || remote is null || !System.Net.IPAddress.IsLoopback(remote) || !keyMatches)
        return Results.NotFound();
    await fleet.SelectOnboardingModeAsync(new SelectExecutionOnboardingModeRequest("local"), cancellationToken);
    var enrollment = await fleet.CreateEnrollmentAsync(cancellationToken);
    var token = enrollment.Enrollment?.EnrollmentToken;
    if (string.IsNullOrWhiteSpace(token)) return Results.Problem("Development enrollment could not be created.");
    var claim = await fleet.ClaimNodeAsync(request.Node with { EnrollmentToken = token }, cancellationToken);
    if (!claim.Succeeded || claim.NodeId is null) return Results.BadRequest(claim);
    var approval = await fleet.ApproveNodeAsync(claim.NodeId.Value, cancellationToken);
    return Results.Ok(approval.Succeeded
        ? claim with { Message = "Development loopback node enrolled and auto-approved." }
        : claim with { Message = "Development loopback node enrolled and is awaiting certified provider readiness and approval." });
});
app.MapPost("/api/execution-nodes/{nodeId:guid}/heartbeat", async (
    Guid nodeId,
    ExecutionNodeHeartbeatRequest request,
    IExecutionFleetService fleet,
    CancellationToken cancellationToken) =>
    await fleet.RecordHeartbeatAsync(nodeId, request, cancellationToken)
        ? Results.NoContent() : Results.Unauthorized());
app.MapPost("/api/execution-nodes/{nodeId:guid}/certificate", async (
    Guid nodeId,
    ExecutionNodeCertificateRequest request,
    HttpContext context,
    IExecutionFleetService fleet,
    CancellationToken cancellationToken) =>
{
    ExecutionNodeCertificateResponse result;
    if (!string.IsNullOrWhiteSpace(request.EnrollmentReceipt))
    {
        result = await fleet.GetOperationalCertificateAsync(nodeId, request, cancellationToken);
    }
    else
    {
        var certificate = context.Connection.ClientCertificate;
        result = certificate is null
            ? new(false, "node_certificate_rejected", "A current operational node certificate is required.", null, null, null)
            : await fleet.RotateOperationalCertificateAsync(
                nodeId, certificate.Thumbprint, certificate.SerialNumber, cancellationToken);
    }
    return result.Succeeded ? Results.Ok(result) : Results.Unauthorized();
});
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Ok(new
{
    service = "CSweet.ExecutionGateway",
    protocol = "csweet-execution-node-v1",
    status = "ok"
}));
app.Run();

public partial class Program;
