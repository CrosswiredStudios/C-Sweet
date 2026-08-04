using CSweet.TrustedServices;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddTrustedServiceAuthentication(builder.Configuration);
builder.Services.AddGitHubAppClient(builder.Configuration);

var app = builder.Build();
app.UseTrustedServiceAuthentication();
app.MapHealthChecks("/health");

app.MapGet("/internal/v2/configuration/status", (GitHubAppCredentialProvider credentials) =>
{
    var current = credentials.Current;
    return Results.Ok(new GitHubAppConfigurationStatus(
        current is not null, current?.AppId, current?.Revision ?? 0,
        current?.AppSlug, current?.AppName));
});

app.MapPost("/internal/v2/configuration/validate", async (
    SealedGitHubAppConfiguration request,
    IOptions<TrustedServiceAuthenticationOptions> authentication,
    GitHubAppClient github,
    CancellationToken cancellationToken) =>
{
    var payload = GitHubAppConfigurationEnvelope.Open(request, authentication.Value, "provisioner");
    var identity = await github.ValidateCredentialAsync(
        payload.AppId, payload.PrivateKeyBase64, cancellationToken);
    return Results.Ok(new GitHubAppConfigurationStatus(
        true, identity.AppId, payload.Revision, identity.AppSlug, identity.AppName));
});

app.MapPost("/internal/v2/configuration/activate", async (
    SealedGitHubAppConfiguration request,
    IOptions<TrustedServiceAuthenticationOptions> authentication,
    GitHubAppClient github,
    GitHubAppCredentialProvider credentials,
    CancellationToken cancellationToken) =>
{
    var payload = GitHubAppConfigurationEnvelope.Open(request, authentication.Value, "provisioner");
    var identity = await github.ValidateCredentialAsync(
        payload.AppId, payload.PrivateKeyBase64, cancellationToken);
    var active = credentials.Activate(
        payload.AppId, payload.PrivateKeyBase64, payload.Revision,
        identity.AppSlug, identity.AppName);
    return Results.Ok(new GitHubAppConfigurationStatus(
        true, active.AppId, active.Revision, active.AppSlug, active.AppName));
});

app.MapPost("/internal/v2/installations/describe", async (
    GitHubInstallationRequest request,
    GitHubAppClient github,
    CancellationToken cancellationToken) =>
{
    if (request.InstallationId <= 0)
        return Results.BadRequest(new { error = "invalid_installation" });
    var installation = await github.DescribeInstallationAsync(request.InstallationId, cancellationToken);
    return Results.Ok(installation);
});

app.MapPost("/internal/v2/repositories/provision-private", async (
    GitHubProvisionRepositoryRequest request,
    GitHubAppClient github,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
        return Results.BadRequest(new { error = "invalid_idempotency_key" });
    var result = await github.ProvisionPrivateRepositoryAsync(request, cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/internal/v2/repositories/list", async (
    GitHubInstallationRequest request,
    GitHubAppClient github,
    CancellationToken cancellationToken) =>
{
    if (request.InstallationId <= 0)
        return Results.BadRequest(new { error = "invalid_installation" });
    return Results.Ok(await github.ListInstallationRepositoriesAsync(
        request.InstallationId, cancellationToken));
});

app.Run();

public partial class Program;
