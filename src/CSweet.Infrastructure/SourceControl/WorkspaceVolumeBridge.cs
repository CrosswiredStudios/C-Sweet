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

public sealed class WorkspaceSnapshotUnavailableException() : InvalidOperationException("The brokered workspace snapshot is unavailable.");

public interface IWorkspaceVolumeBridge
{
    Task<WorkspaceArtifactManifest> ImportAsync(
        WorkspaceVolumeLease lease,
        Stream archive,
        WorkspaceArtifactManifest? expectedManifest = null,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(WorkspaceVolumeLease lease, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Workspace cleanup is unavailable.");

    Task<WorkspaceVolumeExport> ExportAsync(
        WorkspaceVolumeLease lease,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores credential-free workspace snapshots for broker streaming. Guests never
/// receive a host path or volume mount; the authenticated broker transfers the
/// validated archive over the sole host/guest channel.
/// </summary>
public sealed class WorkspaceVolumeBridge(
    CSweetDbContext db,
    WorkspaceArtifactValidator artifacts,
    IOptions<AgentRuntimeManagerOptions> runtimeOptions) : IWorkspaceVolumeBridge
{
    private readonly string _storeRoot = ResolveStoreRoot(runtimeOptions.Value.WorkspaceSnapshotStorePath);

    public async Task<WorkspaceArtifactManifest> ImportAsync(
        WorkspaceVolumeLease lease,
        Stream archive,
        WorkspaceArtifactManifest? expectedManifest = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        await AuthorizeAsync(lease, allowPreparing: true, cancellationToken);
        var temporaryRoot = CreateTemporaryRoot();
        var extracted = Path.Combine(temporaryRoot, "snapshot");
        var quarantine = Path.Combine(temporaryRoot, "snapshot.zip");
        try
        {
            WorkspaceArtifactManifest manifest;
            await using (var file = new FileStream(
                quarantine,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await archive.CopyToAsync(file, cancellationToken);
                file.Position = 0;
                manifest = await artifacts.ExtractZipAsync(file, extracted, cancellationToken);
                if (expectedManifest is not null && manifest != expectedManifest)
                    throw new InvalidDataException("The workspace artifact does not match its trusted manifest.");
                await file.FlushAsync(cancellationToken);
            }
            var destination = SnapshotPath(lease);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(quarantine, destination, overwrite: true);
            await WriteManifestAsync(destination, manifest, cancellationToken);
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
        await AuthorizeAsync(lease, allowPreparing: false, cancellationToken);
        var path = SnapshotPath(lease);
        if (!File.Exists(path)) throw new WorkspaceSnapshotUnavailableException();
        var manifest = await ReadManifestAsync(path, cancellationToken);
        var archive = await File.ReadAllBytesAsync(path, cancellationToken);
        return new WorkspaceVolumeExport(archive, manifest);
    }

    public async Task RemoveAsync(WorkspaceVolumeLease lease, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(lease, allowPreparing: false, cancellationToken);
        var path = SnapshotPath(lease);
        File.Delete(path);
        File.Delete(path + ".manifest.json");
    }

    private async Task AuthorizeAsync(
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
            cancellationToken) ?? throw new UnauthorizedAccessException(
                "The workspace lease does not match persisted source-control state.");
        if (workspace.Status != SourceControlWorkspaceStatus.Ready && workspace.Status != SourceControlWorkspaceStatus.Published &&
            !(allowPreparing && workspace.Status == SourceControlWorkspaceStatus.Preparing))
            throw new InvalidOperationException("The source-control workspace is not available for this operation.");
        if (!await db.CoreWorkTasks.AsNoTracking().AnyAsync(x =>
                x.Id == lease.WorkItemId && x.OrganizationId == lease.OrganizationId &&
                x.AssignedAgentInstallationId == lease.AgentInstallationId &&
                x.AssignmentRevision == lease.AssignmentRevision,
                cancellationToken))
            throw new UnauthorizedAccessException("The workspace lease is stale or assigned to another agent.");
        if (!await db.AgentInstallations.AsNoTracking().AnyAsync(x =>
                x.Id == lease.AgentInstallationId && x.IsEnabled &&
                x.BusinessId == lease.OrganizationId.ToString("D"),
                cancellationToken))
            throw new UnauthorizedAccessException("The assigned agent installation is unavailable.");
    }

    private string SnapshotPath(WorkspaceVolumeLease lease)
    {
        var directory = Path.Combine(
            _storeRoot,
            lease.OrganizationId.ToString("N"),
            lease.AgentInstallationId.ToString("N"),
            lease.WorkspaceId.ToString("N"));
        return Path.Combine(directory, $"{lease.WorkItemId:N}-{lease.AssignmentRevision}.zip");
    }

    private static async Task WriteManifestAsync(string snapshotPath, WorkspaceArtifactManifest manifest, CancellationToken cancellationToken)
    {
        var value = System.Text.Json.JsonSerializer.Serialize(manifest);
        await File.WriteAllTextAsync(snapshotPath + ".manifest.json", value, cancellationToken);
    }

    private static async Task<WorkspaceArtifactManifest> ReadManifestAsync(string snapshotPath, CancellationToken cancellationToken)
    {
        var value = await File.ReadAllTextAsync(snapshotPath + ".manifest.json", cancellationToken);
        return System.Text.Json.JsonSerializer.Deserialize<WorkspaceArtifactManifest>(value)
            ?? throw new InvalidDataException("The workspace snapshot manifest is invalid.");
    }

    private static string ResolveStoreRoot(string configured)
    {
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSweet", "workspace-snapshots")
            : configured;
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("The workspace snapshot store path must be absolute.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string CreateTemporaryRoot()
    {
        var parent = Path.Combine(Path.GetTempPath(), "csweet-workspace-broker");
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryRoot(string temporaryRoot)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "csweet-workspace-broker"));
        var resolved = Path.GetFullPath(temporaryRoot);
        var prefix = root + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a temporary path outside the workspace broker root.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
