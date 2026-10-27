using System.Text.Json;
using CSweet.Api.Auth;
using CSweet.Contracts.Compute;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Compute;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Compute;

public static class ComputeDashboardEndpoints
{
    public static IEndpointRouteBuilder MapComputeDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapComputeNetworkGrantEndpoints();
        endpoints.MapComputeManagementEndpoints();
        endpoints.MapGet("/api/organizations/{organizationId:guid}/compute", async (Guid organizationId,
            HttpContext http, CSweetDbContext db, IConfiguration configuration, IWebHostEnvironment environment,
            ComputeDownloadProgress downloads, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } userId) return Results.Unauthorized();
            var root = configuration["CSweet:Compute:RepositoryRoot"] ?? Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", ".."));
            var result = await ReadAsync(db, organizationId, userId, root,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), token, downloads);
            return result is null ? Results.Forbid() : Results.Ok(result);
        }).RequireAuthorization();
        endpoints.MapPost("/api/organizations/{organizationId:guid}/compute/setup/update", async (Guid organizationId,
            HttpContext http, CSweetDbContext db, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } userId) return Results.Unauthorized();
            try { await QueueRuntimeUpdateAsync(db, organizationId, userId, token); return Results.Accepted(); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "Compute must be ready with no outstanding operation before updating its service." }); }
        }).RequireAuthorization();
        endpoints.MapPost("/api/organizations/{organizationId:guid}/compute/setup/repair", async (Guid organizationId,
            HttpContext http, CSweetDbContext db, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } userId) return Results.Unauthorized();
            if (!await db.CoreOrganizationUsers.AnyAsync(x => x.OrganizationId == organizationId &&
                x.ApplicationUserId == userId && x.EmployeeType == EmployeeType.Human && x.IsActive &&
                x.ArchivedAt == null && x.PermissionLevel == OrganizationPermissionLevel.Owner, token))
                return Results.Forbid();
            var setup = await db.Set<ComputeLocalSetup>().SingleOrDefaultAsync(x => x.OrganizationId == organizationId, token);
            if (setup is null) return Results.NotFound();
            var setupDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CSweet", "ComputeSetup", setup.Id.ToString("N"));
            if (!IsRepairable(setup, setupDirectory, DateTimeOffset.UtcNow))
                return Results.Conflict(new { error = "Compute preparation is not failed or stalled." });
            setup.State = "Pending";
            setup.ErrorCode = null;
            setup.HandoffHash = null;
            setup.HandoffExpiresAt = null;
            setup.Revision++;
            setup.UpdatedAt = DateTimeOffset.UtcNow;
            ComputeManagementEndpoints.QueueOwnerAudit(db, organizationId, userId, setup.Id, "compute.workspace.preparation-retried.v1");
            await db.SaveChangesAsync(token);
            return Results.Accepted();
        }).RequireAuthorization();
        return endpoints;
    }

    internal static async Task QueueRuntimeUpdateAsync(CSweetDbContext db, Guid business, Guid user, CancellationToken token)
    {
        await ComputeManagementEndpoints.RequireOwnerAsync(db, business, user, token);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token) : null;
        await new CSweet.Infrastructure.Core.ProjectWorkPolicy(db, TimeProvider.System).LockAsync(business, token);
        var setup = await db.Set<ComputeLocalSetup>().SingleOrDefaultAsync(x => x.OrganizationId == business, token);
        if (setup is null || setup.State != "Ready" || await db.ComputeOperations.AnyAsync(x => x.OrganizationId == business &&
            (x.Status == "Pending" || x.Status == "Dispatching" || x.Status == "Reconciling" || x.Status == "Blocked"), token))
            throw new InvalidOperationException("Compute is not ready for a runtime update.");
        setup.State = "Pending"; setup.ErrorCode = null; setup.HandoffHash = null; setup.HandoffExpiresAt = null;
        setup.Revision++; setup.UpdatedAt = DateTimeOffset.UtcNow;
        ComputeManagementEndpoints.QueueOwnerAudit(db, business, user, setup.Id, "compute.workspace.runtime-update-requested.v1");
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
    }

    internal static bool IsRepairable(ComputeLocalSetup setup, string setupDirectory, DateTimeOffset now)
    {
        var observation = ComputeSetupObservation.Read(setup, setupDirectory);
        if (observation.InstallerRunning == true) return false;
        if (setup.State == "Failed") return true;
        if (setup.State != "Running") return false;
        return observation.FailureCode is not null;
    }

    internal static async Task<ComputeDashboard?> ReadAsync(CSweetDbContext db, Guid organizationId, Guid userId,
        string repositoryRoot, string localData, CancellationToken token, ComputeDownloadProgress? downloads = null)
    {
        if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.OrganizationId == organizationId &&
                x.ApplicationUserId == userId && x.EmployeeType == EmployeeType.Human && x.IsActive && x.ArchivedAt == null, token)) return null;
        var environments = await db.ComputeEnvironments.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(101).ToListAsync(token);
        var installations = environments.Select(x => x.InstallationId).ToArray();
        var workstreams = environments.Select(x => x.WorkstreamId).ToArray();
        var agents = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            x.AgentInstallationId != null && installations.Contains(x.AgentInstallationId.Value)).ToListAsync(token);
        var installedAgents = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant).Include(x => x.PackageVersion)
            .Where(x => installations.Contains(x.Id) && x.BusinessId == organizationId.ToString("D")).ToDictionaryAsync(x => x.Id, token);
        var projects = await db.Workstreams.AsNoTracking().Where(x => x.OrganizationId == organizationId && workstreams.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, token);
        var networkGrants = await db.ScopedActionGrants.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            (x.Action == "network.inbound.v1" || x.Action == "network.publish-port.v1") && x.RevokedAt == null &&
            x.GrantedAt <= DateTimeOffset.UtcNow && x.ExpiresAt > DateTimeOffset.UtcNow).ToListAsync(token);
        networkGrants.RemoveAll(x => ComputeLocalLinkGrants.IsHiringGrant(x) &&
            (!installedAgents.TryGetValue(x.SubjectId, out var installation) || !ComputeLocalLinkGrants.IsApproved(installation)));
        var resources = environments.Take(100).Select(x =>
        {
            ComputeSpecification? spec = null;
            try { spec = JsonSerializer.Deserialize<ComputeSpecification>(x.SpecificationJson, CSweet.Compute.Contracts.ComputeProtocol.Json); }
            catch (JsonException) { }
            var employee = agents.FirstOrDefault(a => a.AgentInstallationId == x.InstallationId);
            var installation = installedAgents.GetValueOrDefault(x.InstallationId);
            var approved = JsonSerializer.Deserialize<string[]>(installation?.Grant?.RequiredCapabilitiesJson ?? "[]") ?? [];
            return new ComputeResourceStatus(x.Id, employee?.DisplayName ?? "Former agent",
                projects.GetValueOrDefault(x.WorkstreamId, "Application testing"), x.State.ToString(), x.DesiredState.ToString(),
                spec?.OperatingSystem ?? "Unknown", spec?.Resources?.CpuCount ?? 0, spec?.Resources?.MemoryMiB ?? 0,
                spec?.Resources?.DiskMiB ?? 0, x.UpdatedAt, x.LeaseExpiresAt, x.LastFailureCode,
                ComputeNetworkGrantEndpoints.Actions.All(a => networkGrants.Any(g => g.Id == ComputeNetworkGrantEndpoints.GrantId(x.Id, a))))
            {
                EmployeeId = employee?.Id,
                Name = x.DesiredEnvironmentKey,
                LocalLinkRequested = installation is { IsEnabled: true } && employee is { IsActive: true, ArchivedAt: null } &&
                    ComputeLocalLinkGrants.Actions.All(approved.Contains)
            };
        }).ToArray();
        var setup = await db.Set<ComputeLocalSetup>().AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId, token);
        string? downloadFile = null;
        var progress = setup is null ? null : ReadProgress(setup, repositoryRoot, localData, file => downloadFile = file);
        if (setup is not null && progress is not null && downloads is not null)
            progress = await downloads.EnrichAsync(setup.Id, setup.UpdatedAt, downloadFile, progress, token);
        var canManage = await db.CoreOrganizationUsers.AnyAsync(x => x.OrganizationId == organizationId && x.ApplicationUserId == userId &&
            x.EmployeeType == EmployeeType.Human && x.IsActive && x.ArchivedAt == null && x.PermissionLevel == OrganizationPermissionLevel.Owner, token);
        return new(DateTimeOffset.UtcNow, progress, resources, environments.Count > 100, canManage);
    }

    internal static ComputeSetupStatus ReadProgress(ComputeLocalSetup setup, string repositoryRoot, string localData, Action<string>? observeFile = null)
    {
        var stage = setup.State switch { "Pending" => "Waiting to prepare Linux", "Running" => "Preparing Linux",
            "Ready" => "Ready for compute requests", "Failed" => "Preparation failed", _ => "Checking preparation" };
        DateTimeOffset? progressAt = setup.UpdatedAt;
        long? downloaded = null;
        var step = 0;
        var directory = Path.Combine(localData, "CSweet", "ComputeSetup", setup.Id.ToString("N"));
        var observation = ComputeSetupObservation.Read(setup, directory);
        if (setup.PreparationAttemptId is not null)
        {
            var structured = ComputePreparationProgress.Read(setup, directory) ?? new(setup.State, stage, null, null, setup.ErrorCode);
            if (setup.State == "Running" && structured.Phase == "download-ubuntu" && observation.FailureCode is null)
            {
                try
                {
                    var cache = new DirectoryInfo(Path.GetFullPath(Path.Combine(repositoryRoot, "..", "CSweet.Isolation", "artifacts", "linux-images", "cache")));
                    var image = cache.Exists ? cache.EnumerateFiles("ubuntu-*-live-server-amd64.iso")
                        .Where(file => file.LastWriteTimeUtc >= structured.StartedAt?.UtcDateTime)
                        .OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault() : null;
                    if (image is not null)
                    {
                        image.Refresh();
                        structured = structured with { DownloadedBytes = image.Length };
                        observeFile?.Invoke(image.Name);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            return ComputePreparationProgress.Observe(structured, setup, observation, DateTimeOffset.UtcNow);
        }
        if (setup.State == "Running")
        {
            try
            {
                var setupDirectory = Path.Combine(localData, "CSweet", "ComputeSetup", setup.Id.ToString("N"));
                var progress = new FileInfo(Path.Combine(setupDirectory, "progress.json"));
                if (progress.Exists && progress.Length <= 16384 && progress.LastWriteTimeUtc >= setup.UpdatedAt.UtcDateTime)
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(progress.FullName));
                    if (json.RootElement.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 and <= 512 } text) stage = text;
                    progressAt = progress.LastWriteTimeUtc;
                }
                else
                {
                    // Compatibility for an installer already running when stage reporting was added.
                    // Return only recognized stage labels, never transcript content or filesystem paths.
                    var log = new FileInfo(Path.Combine(setupDirectory, "installation.log"));
                    if (log.Exists && log.Length <= 1_048_576 && log.LastWriteTimeUtc >= setup.UpdatedAt.UtcDateTime)
                    {
                        using var stream = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(stream);
                        var transcript = reader.ReadToEnd();
                        var building = transcript.LastIndexOf("Ubuntu is installing in a Secure Boot VM.", StringComparison.Ordinal);
                        var installed = transcript.LastIndexOf("Linux image created:", StringComparison.Ordinal);
                        if (installed >= 0 && installed > building) { stage = "Installing the compute service."; progressAt = log.LastWriteTimeUtc; }
                        else if (building >= 0) { stage = "Ubuntu is installing in a Secure Boot VM."; progressAt = log.LastWriteTimeUtc; }
                    }
                }
                if (stage.StartsWith("Linux image created:", StringComparison.Ordinal)) stage = "Linux image prepared.";
                step = stage.StartsWith("Ubuntu is installing", StringComparison.Ordinal) ? 2 :
                    stage is "Checking the compute service." or "Installing the compute service." or "Linux image prepared." ? 3 : 0;
                // Also observes installers started before structured progress reporting was available.
                var cache = Path.GetFullPath(Path.Combine(repositoryRoot, "..", "CSweet.Isolation", "artifacts", "linux-images", "cache"));
                if (Directory.Exists(cache))
                {
                    var images = new DirectoryInfo(cache).EnumerateFiles("ubuntu-*-live-server-amd64.iso").ToArray();
                    // Directory enumeration can retain stale metadata for an open download on Windows.
                    foreach (var candidate in images) candidate.Refresh();
                    var image = images
                        .Where(x => x.LastWriteTimeUtc >= setup.UpdatedAt.UtcDateTime && x.LastWriteTimeUtc > progressAt?.UtcDateTime)
                        .OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
                    if (image is not null && step < 2) { stage = "Downloading the Linux installation image"; step = 1; progressAt = image.LastWriteTimeUtc; downloaded = image.Length; observeFile?.Invoke(image.Name); }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return ComputePreparationProgress.Observe(new(setup.State, stage, progressAt, downloaded, setup.ErrorCode, PreparationStep: step)
        {
            Milestone = step switch { 1 => 2, 2 => 3, 3 => 5, _ => 0 }
        }, setup, observation, DateTimeOffset.UtcNow);
    }
}
