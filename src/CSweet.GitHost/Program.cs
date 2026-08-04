using CSweet.TrustedServices;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddTrustedServiceAuthentication(builder.Configuration);
builder.Services.AddGitHubAppClient(builder.Configuration);
builder.Services.AddSingleton<WorkspaceArtifactValidator>();
builder.Services.AddTransient<GitHubWorkspaceSnapshotService>();

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
    var payload = GitHubAppConfigurationEnvelope.Open(request, authentication.Value, "source-access");
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
    var payload = GitHubAppConfigurationEnvelope.Open(request, authentication.Value, "source-access");
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

app.MapPost("/internal/v2/pull-requests/merge-exact", async (
    GitHubMergeRequest request,
    GitHubAppClient github,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
        return Results.BadRequest(new { error = "invalid_idempotency_key" });
    var result = await github.MergePullRequestAsync(request, cancellationToken);
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

app.MapPost("/internal/v2/workspaces/prepare", async (
    GitHubWorkspacePrepareRequest request,
    GitHubWorkspaceSnapshotService workspaces,
    HttpContext http,
    CancellationToken cancellationToken) =>
{
    try
    {
        var snapshot = await workspaces.PrepareAsync(request, cancellationToken);
        http.Response.Headers[WorkspaceSnapshotHeaders.WorkspaceKey] = snapshot.WorkspaceKey;
        http.Response.Headers[WorkspaceSnapshotHeaders.BaseCommitSha] = snapshot.BaseCommitSha;
        http.Response.Headers[WorkspaceSnapshotHeaders.Resumed] = snapshot.Resumed.ToString();
        http.Response.Headers[WorkspaceSnapshotHeaders.ArtifactSha256] = snapshot.Manifest.Sha256;
        http.Response.Headers[WorkspaceSnapshotHeaders.ArtifactFileCount] = snapshot.Manifest.FileCount.ToString();
        http.Response.Headers[WorkspaceSnapshotHeaders.ArtifactTotalBytes] = snapshot.Manifest.TotalBytes.ToString();
        return Results.File(snapshot.Archive, "application/zip", "workspace.zip");
    }
    catch (ArgumentException)
    {
        return Results.BadRequest(new { error = "workspace_request_invalid" });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Json(new { error = "repository_not_authorized" }, statusCode: StatusCodes.Status403Forbidden);
    }
    catch (InvalidDataException)
    {
        return Results.UnprocessableEntity(new { error = "repository_snapshot_rejected" });
    }
    catch (InvalidOperationException)
    {
        return Results.Conflict(new { error = "workspace_prepare_failed" });
    }
});

app.Run();

public partial class Program;
