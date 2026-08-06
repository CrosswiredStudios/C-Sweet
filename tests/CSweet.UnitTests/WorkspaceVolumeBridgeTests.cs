using System.IO.Compression;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.SourceControl;
using CSweet.TrustedServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class WorkspaceVolumeBridgeTests : IDisposable
{
    private readonly string _storeRoot = Path.Combine(Path.GetTempPath(), $"csweet-workspace-test-{Guid.NewGuid():N}");

    [Fact]
    public async Task ImportPersistsOnlyAnOpaqueValidatedBrokerSnapshot()
    {
        await using var db = CreateDb();
        var seeded = await SeedAsync(db, SourceControlWorkspaceStatus.Preparing);
        var bridge = CreateBridge(db);
        await using var archive = CreateArchive(("src/app.cs", "sealed class App { }"));

        var manifest = await bridge.ImportAsync(seeded.Lease, archive);

        Assert.Equal(1, manifest.FileCount);
        Assert.Single(Directory.GetFiles(_storeRoot, "*.zip", SearchOption.AllDirectories));
        Assert.DoesNotContain(seeded.Workspace.WorkspaceKey, Directory.GetFiles(_storeRoot, "*", SearchOption.AllDirectories).Single(path => path.EndsWith(".zip")));
    }

    [Fact]
    public async Task MismatchedLeaseIsRejectedBeforeSnapshotWrite()
    {
        await using var db = CreateDb();
        var seeded = await SeedAsync(db, SourceControlWorkspaceStatus.Preparing);
        var bridge = CreateBridge(db);
        await using var archive = CreateArchive(("README.md", "safe"));
        var mismatched = seeded.Lease with { OrganizationId = Guid.NewGuid() };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bridge.ImportAsync(mismatched, archive));

        Assert.False(Directory.Exists(_storeRoot));
    }

    [Fact]
    public async Task MaliciousArchiveIsRejectedBeforeSnapshotWrite()
    {
        await using var db = CreateDb();
        var seeded = await SeedAsync(db, SourceControlWorkspaceStatus.Preparing);
        var bridge = CreateBridge(db);
        await using var archive = CreateArchive(("src/.git/config", "credential = secret"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            bridge.ImportAsync(seeded.Lease, archive));

        Assert.Empty(Directory.Exists(_storeRoot) ? Directory.GetFiles(_storeRoot, "*.zip", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task ExportRevalidatesAndReturnsSanitizedSnapshot()
    {
        await using var db = CreateDb();
        var seeded = await SeedAsync(db, SourceControlWorkspaceStatus.Ready);
        var bridge = CreateBridge(db);
        await using var archive = CreateArchive(("src/result.txt", "tested"));
        await bridge.ImportAsync(seeded.Lease, archive);

        var exported = await bridge.ExportAsync(seeded.Lease);

        Assert.Equal(1, exported.Manifest.FileCount);
        await using var stream = new MemoryStream(exported.Archive);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Equal("src/result.txt", Assert.Single(zip.Entries).FullName);
    }

    [Fact]
    public async Task PreparingWorkspaceCannotBeExported()
    {
        await using var db = CreateDb();
        var seeded = await SeedAsync(db, SourceControlWorkspaceStatus.Preparing);
        var bridge = CreateBridge(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.ExportAsync(seeded.Lease));

    }

    private WorkspaceVolumeBridge CreateBridge(CSweetDbContext db) =>
        new(
            db,
            new WorkspaceArtifactValidator(),
            Options.Create(new AgentRuntimeManagerOptions
            {
                WorkspaceSnapshotStorePath = _storeRoot
            }));

    public void Dispose()
    {
        if (Directory.Exists(_storeRoot)) Directory.Delete(_storeRoot, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private static async Task<SeededWorkspace> SeedAsync(
        CSweetDbContext db,
        SourceControlWorkspaceStatus status)
    {
        var organizationId = Guid.NewGuid();
        var workItemId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var installationKey = Guid.NewGuid();
        var installation = new AgentInstallation
        {
            Id = installationId,
            InstallationKey = installationKey,
            PackageVersionId = Guid.NewGuid(),
            BusinessId = organizationId.ToString("D"),
            IsEnabled = true
        };
        var workItem = new WorkTask
        {
            Id = workItemId,
            OrganizationId = organizationId,
            AssignedAgentInstallationId = installationId,
            AssignmentRevision = 1,
            Title = "Implement feature"
        };
        var workspace = new SourceControlWorkspace
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            TeamId = Guid.NewGuid(),
            RepositoryId = Guid.NewGuid(),
            AgentInstallationId = installationId,
            WorkItemId = workItemId,
            AssignmentRevision = 1,
            WorkspaceKey = "opaque-workspace-key",
            BaseCommitSha = new string('a', 40),
            BranchName = "csweet/work-item",
            Status = status
        };
        db.AgentInstallations.Add(installation);
        db.CoreWorkTasks.Add(workItem);
        db.SourceControlWorkspaces.Add(workspace);
        await db.SaveChangesAsync();
        return new SeededWorkspace(
            workspace,
            installationKey,
            new WorkspaceVolumeLease(
                organizationId, installationId, workspace.Id, workItemId, 1));
    }

    private static MemoryStream CreateArchive(params (string Path, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = zip.CreateEntry(item.Path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(item.Content);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private sealed record SeededWorkspace(
        SourceControlWorkspace Workspace,
        Guid InstallationKey,
        WorkspaceVolumeLease Lease);

}
