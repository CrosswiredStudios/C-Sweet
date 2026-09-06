using CSweet.Contracts.SourceControl;
using CSweet.TrustedServices;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var trustedKeyFile = builder.Configuration["TrustedServiceAuthentication:KeyFile"];
if (string.IsNullOrWhiteSpace(builder.Configuration["TrustedServiceAuthentication:SharedKeyBase64"]) && !string.IsNullOrWhiteSpace(trustedKeyFile))
    builder.Configuration["TrustedServiceAuthentication:SharedKeyBase64"] = TrustedServiceKeyFile.GetOrCreate(trustedKeyFile);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddTrustedServiceAuthentication(builder.Configuration);
builder.Services.AddGitHubAppClient(builder.Configuration);
builder.Services.AddSingleton<WorkspaceArtifactValidator>();
builder.Services.AddTransient<GitHubWorkspaceSnapshotService>();
builder.Services.AddTransient<IGitHubRepositoryTransport, GitHubRepositoryTransport>();
builder.Services.AddTransient<GitHubWorkspaceOperationsService>();

builder.Services.Configure<InternalGitStorageOptions>(builder.Configuration.GetSection(InternalGitStorageOptions.SectionName));
builder.Services.AddSingleton<InternalGitRepositoryStore>();
builder.Services.AddSingleton<InternalGitBackupJobs>();
builder.Services.AddHostedService<InternalGitBackupWorker>();
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

app.MapGet("/internal/v3/backups/{business:guid}", async (Guid business, InternalGitRepositoryStore store, CancellationToken ct) => Results.Ok(await store.ListBackupsAsync(business, ct)));
app.MapGet("/internal/v3/backup-jobs/{business:guid}", async (Guid business, InternalGitBackupJobs jobs, CancellationToken ct) => Results.Ok(await jobs.ListAsync(business, ct)));
app.MapPost("/internal/v3/backup-jobs/queue", async (InternalGitBackupRequest request, InternalGitBackupJobs jobs, CancellationToken ct) => Results.Ok(await jobs.QueueAsync(request, ct)));
app.MapDelete("/internal/v3/backup-jobs/{business:guid}/{id:guid}", async (Guid business, Guid id, InternalGitBackupJobs jobs, CancellationToken ct) => { await jobs.DismissAsync(business, id, ct); return Results.NoContent(); });
app.MapGet("/internal/v3/backup-schedules/{business:guid}/{repository:guid}", async (Guid business, Guid repository, InternalGitBackupJobs jobs, CancellationToken ct) => Results.Ok(await jobs.ScheduleAsync(business, repository, ct)));
app.MapPost("/internal/v3/backup-schedules", async (InternalGitBackupScheduleCommand request, InternalGitBackupJobs jobs, CancellationToken ct) => Results.Ok(await jobs.SaveScheduleAsync(request, ct)));
app.MapPost("/internal/v3/backups/create", async (InternalGitBackupRequest request, InternalGitRepositoryStore store, CancellationToken ct) => Results.Ok(await store.CreateBackupAsync(request, ct)));
app.MapPost("/internal/v3/backups/restore", async (InternalGitBackupRestoreRequest request, InternalGitRepositoryStore store, CancellationToken ct) => Results.Ok(await store.RestoreBackupAsync(request, ct)));
app.MapPost("/internal/v3/backups/delete", async (InternalGitBackupRequest request, InternalGitRepositoryStore store, CancellationToken ct) => { await store.DeleteBackupAsync(request, ct); return Results.NoContent(); });

app.MapPost("/internal/v3/lfs/locks", async (InternalGitLockRequest request, InternalGitRepositoryStore store, CancellationToken ct) => Results.Ok(await store.LocksAsync(request, ct)));

app.MapPost("/internal/v3/lfs", async (InternalGitLfsTransfer request, InternalGitRepositoryStore store, CancellationToken ct) =>
    Results.Ok(await store.TransferLfsAsync(request, ct))).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(180L * 1024 * 1024));

app.MapPost("/internal/v3/git", async (InternalGitHttpRequest request, InternalGitRepositoryStore store, CancellationToken ct) =>
    Results.Ok(await store.ExchangeAsync(request, ct))).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(180L * 1024 * 1024));

app.MapGet("/internal/v3/storage", async (InternalGitRepositoryStore store, CancellationToken ct) =>
    Results.Ok(await store.StatusAsync(ct)));
app.MapPost("/internal/v3/repositories/execute", async (InternalGitRepositoryRequest request,
    InternalGitRepositoryStore store, CancellationToken ct) =>
{
    try { return Results.Ok(await store.ExecuteAsync(request, ct)); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_repository_operation" }); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (Exception ex) when (ex is IOException or InvalidOperationException)
    { return Results.Conflict(new { error = "repository_operation_failed" }); }
});
app.MapPost("/internal/v3/workspaces/prepare", async (InternalGitWorkspaceRequest request,
    InternalGitRepositoryStore store, WorkspaceArtifactValidator artifacts, CancellationToken ct) =>
{
    try { return Results.Ok(await store.PrepareAsync(request, artifacts, ct)); }
    catch (ArgumentException) { return Results.BadRequest(); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (Exception ex) when (ex is IOException or InvalidOperationException) { return Results.Conflict(); }
});
app.MapPost("/internal/v3/github/workspaces/apply", async (GitHubSnapshotOperation request, GitHubWorkspaceOperationsService service, CancellationToken ct) =>
{
    try { return Results.Ok(await service.ApplyAsync(request, ct)); }
    catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
    catch (ArgumentException) { return Results.BadRequest(); }
    catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException) { return Results.Conflict(); }
}).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(850L * 1024 * 1024));

app.MapPost("/internal/v3/workspaces/apply", async (InternalGitSnapshotOperation request,
    InternalGitRepositoryStore store, WorkspaceArtifactValidator artifacts, CancellationToken ct) =>
{
    try { return Results.Ok(await store.ApplySnapshotAsync(request, artifacts, ct)); }
    catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
    catch (ArgumentException) { return Results.BadRequest(); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (Exception ex) when (ex is IOException or InvalidOperationException) { return Results.Conflict(); }
}).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(850L * 1024 * 1024));
app.MapPost("/internal/v3/merge", async (InternalGitMergeRequest request, InternalGitRepositoryStore store, CancellationToken ct) =>
{
    try { return Results.Ok(await store.MergeInternalAsync(request, ct)); }
    catch (ArgumentException) { return Results.BadRequest(); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (Exception ex) when (ex is IOException or InvalidOperationException) { return Results.Conflict(); }
});
app.Run();

public partial class Program;
