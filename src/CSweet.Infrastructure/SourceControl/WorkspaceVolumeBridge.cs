using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.TrustedServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.SourceControl;

public sealed record WorkspaceVolumeLease(
    Guid OrganizationId,
    Guid AgentInstallationId,
    Guid WorkspaceId,
    Guid WorkItemId,
    long AssignmentRevision);

public sealed record WorkspaceVolumeExport(
    byte[] Archive,
    WorkspaceArtifactManifest Manifest);

public interface IWorkspaceVolumeBridge
{
    Task<WorkspaceArtifactManifest> ImportAsync(
        WorkspaceVolumeLease lease,
        Stream archive,
        WorkspaceArtifactManifest? expectedManifest = null,
        CancellationToken cancellationToken = default);

    Task<WorkspaceVolumeExport> ExportAsync(
        WorkspaceVolumeLease lease,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Copies credential-free snapshots between trusted services and exactly one agent-installation
/// Docker volume. The helper has no network, Docker socket, provider credentials, or access to any
/// other volume. Repository selection is always re-derived from persisted Core state.
/// </summary>
public sealed class WorkspaceVolumeBridge(
    CSweetDbContext db,
    IDockerCommandExecutor docker,
    WorkspaceArtifactValidator artifacts,
    IOptions<AgentRuntimeManagerOptions> runtimeOptions) : IWorkspaceVolumeBridge
{
    private const string ContainerWorkspaceRoot = "/workspace";
    private const string RuntimeUser = "1654:1654";
    private readonly AgentRuntimeManagerOptions _runtimeOptions = runtimeOptions.Value;

    public async Task<WorkspaceArtifactManifest> ImportAsync(
        WorkspaceVolumeLease lease,
        Stream archive,
        WorkspaceArtifactManifest? expectedManifest = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var target = await ResolveTargetAsync(lease, allowPreparing: true, cancellationToken);
        var temporaryRoot = CreateTemporaryRoot();
        var extracted = Path.Combine(temporaryRoot, "snapshot");
        try
        {
            var manifest = await artifacts.ExtractZipAsync(archive, extracted, cancellationToken);
            if (expectedManifest is not null && manifest != expectedManifest)
                throw new InvalidDataException("The workspace artifact does not match its trusted manifest.");
            await WithHelperAsync(target, async helperName =>
            {
                await ExecuteRequiredAsync(
                    ["exec", helperName, "/bin/sh", "-c",
                        "rm -rf -- \"$1\" && mkdir -p -- \"$1\"", "csweet-bridge", target.ContainerPath],
                    "prepare the isolated workspace target",
                    cancellationToken);
                await ExecuteRequiredAsync(
                    ["cp", extracted + Path.DirectorySeparatorChar + ".", $"{helperName}:{target.ContainerPath}"],
                    "copy the validated workspace snapshot",
                    cancellationToken);
                await ExecuteRequiredAsync(
                    ["exec", helperName, "chown", "-R", RuntimeUser, target.ContainerPath],
                    "set workspace ownership",
                    cancellationToken);
            }, cancellationToken);
            return manifest;
        }
        finally
        {
            DeleteTemporaryRoot(temporaryRoot);
        }
    }

    public async Task<WorkspaceVolumeExport> ExportAsync(
        WorkspaceVolumeLease lease,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveTargetAsync(lease, allowPreparing: false, cancellationToken);
        var temporaryRoot = CreateTemporaryRoot();
        var snapshot = Path.Combine(temporaryRoot, "snapshot");
        Directory.CreateDirectory(snapshot);
        try
        {
            await WithHelperAsync(target, async helperName =>
            {
                await ExecuteRequiredAsync(
                    ["exec", helperName, "test", "-d", target.ContainerPath],
                    "locate the isolated workspace target",
                    cancellationToken);
                await ExecuteRequiredAsync(
                    ["cp", $"{helperName}:{target.ContainerPath}/.", snapshot],
                    "copy the workspace snapshot",
                    cancellationToken);
            }, cancellationToken);

            await using var archive = new MemoryStream();
            var manifest = await artifacts.CreateZipAsync(snapshot, archive, cancellationToken);
            return new WorkspaceVolumeExport(archive.ToArray(), manifest);
        }
        finally
        {
            DeleteTemporaryRoot(temporaryRoot);
        }
    }

    private async Task<ResolvedTarget> ResolveTargetAsync(
        WorkspaceVolumeLease lease,
        bool allowPreparing,
        CancellationToken cancellationToken)
    {
        if (lease.AssignmentRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(lease), "An active assignment revision is required.");

        var workspace = await db.SourceControlWorkspaces.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == lease.WorkspaceId &&
            x.OrganizationId == lease.OrganizationId &&
            x.AgentInstallationId == lease.AgentInstallationId &&
            x.WorkItemId == lease.WorkItemId &&
            x.AssignmentRevision == lease.AssignmentRevision,
            cancellationToken);
        if (workspace is null)
            throw new UnauthorizedAccessException("The workspace lease does not match persisted source-control state.");

        var allowed = workspace.Status == SourceControlWorkspaceStatus.Ready ||
            (allowPreparing && workspace.Status == SourceControlWorkspaceStatus.Preparing);
        if (!allowed)
            throw new InvalidOperationException("The source-control workspace is not available for this operation.");

        var workItemMatches = await db.CoreWorkTasks.AsNoTracking().AnyAsync(x =>
            x.Id == lease.WorkItemId &&
            x.OrganizationId == lease.OrganizationId &&
            x.AssignedAgentInstallationId == lease.AgentInstallationId &&
            x.AssignmentRevision == lease.AssignmentRevision,
            cancellationToken);
        if (!workItemMatches)
            throw new UnauthorizedAccessException("The workspace lease is stale or assigned to another agent.");

        var installation = await db.AgentInstallations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == lease.AgentInstallationId &&
            x.IsEnabled &&
            x.BusinessId == lease.OrganizationId.ToString("D"),
            cancellationToken);
        if (installation is null)
            throw new UnauthorizedAccessException("The assigned agent installation is unavailable.");

        var volumeName = $"csweet-workspace-{installation.InstallationKey:N}";
        var containerPath = $"{ContainerWorkspaceRoot}/{lease.WorkItemId:N}/{lease.AssignmentRevision}";
        return new ResolvedTarget(volumeName, containerPath);
    }

    private async Task WithHelperAsync(
        ResolvedTarget target,
        Func<string, Task> action,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_runtimeOptions.SoftwareDevelopmentPolyglotImage))
            throw new InvalidOperationException("The trusted workspace helper image is not configured.");

        var helperName = $"csweet-workspace-bridge-{Guid.NewGuid():N}";
        await ExecuteRequiredAsync(
            [
                "run", "--detach", "--name", helperName,
                "--network", "none",
                "--read-only",
                "--cap-drop", "ALL",
                "--cap-add", "CHOWN",
                "--cap-add", "DAC_OVERRIDE",
                "--pids-limit", "64",
                "--memory", "256m",
                "--mount", $"type=volume,source={target.VolumeName},target={ContainerWorkspaceRoot}",
                "--entrypoint", "/bin/sh",
                _runtimeOptions.SoftwareDevelopmentPolyglotImage,
                "-c", "sleep 600"
            ],
            "start the isolated workspace bridge",
            cancellationToken);
        try
        {
            await action(helperName);
        }
        finally
        {
            await docker.ExecuteAsync(["rm", "--force", helperName], CancellationToken.None);
        }
    }

    private async Task ExecuteRequiredAsync(
        IReadOnlyList<string> arguments,
        string operation,
        CancellationToken cancellationToken)
    {
        var result = await docker.ExecuteAsync(arguments, cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"The trusted workspace bridge could not {operation}.");
    }

    private static string CreateTemporaryRoot()
    {
        var bridgeRoot = Path.Combine(Path.GetTempPath(), "csweet-workspace-bridge");
        Directory.CreateDirectory(bridgeRoot);
        var temporaryRoot = Path.Combine(bridgeRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        return temporaryRoot;
    }

    private static void DeleteTemporaryRoot(string temporaryRoot)
    {
        var bridgeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "csweet-workspace-bridge"));
        var resolved = Path.GetFullPath(temporaryRoot);
        var prefix = bridgeRoot.EndsWith(Path.DirectorySeparatorChar)
            ? bridgeRoot
            : bridgeRoot + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a temporary path outside the workspace bridge root.");
        if (Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
    }

    private sealed record ResolvedTarget(string VolumeName, string ContainerPath);
}
