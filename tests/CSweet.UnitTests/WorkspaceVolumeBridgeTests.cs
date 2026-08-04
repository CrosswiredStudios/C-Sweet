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

public sealed class WorkspaceVolumeBridgeTests
{
    [Fact]
    public async Task ImportUsesOnlyDerivedInstallationVolumeAndNetworklessHelper()
    {
        await using var db = CreateDb();
        var seeded = await SeedAsync(db, SourceControlWorkspaceStatus.Preparing);
        var docker = new FakeDockerExecutor();
        var bridge = CreateBridge(db, docker);
        await using var archive = CreateArchive(("src/app.cs", "sealed class App { }"));

        var manifest = await bridge.ImportAsync(seeded.Lease, archive);

        Assert.Equal(1, manifest.FileCount);
        var run = Assert.Single(docker.Commands, command => command[0] == "run");
        Assert.Contains("--network", run);
        Assert.Contains("none", run);
        Assert.Contains("--read-only", run);
        Assert.Contains("--cap-drop", run);
        Assert.Contains(
            $"type=volume,source=csweet-workspace-{seeded.InstallationKey:N},target=/workspace",
            run);
        Assert.DoesNotContain(run, value => value.Contains(seeded.Workspace.WorkspaceKey, StringComparison.Ordinal));
        Assert.Contains(docker.Commands, command =>
            command[0] == "exec" && command.Contains($"/workspace/{seeded.Lease.WorkItemId:N}/1"));
        Assert.Contains(docker.Commands, command => command is ["rm", "--force", ..]);
    }

    [Fact]
    public async Task MismatchedLeaseIsRejectedBeforeDocker()
    {
        await using var db = CreateDb();
        var seeded = await SeedAsync(db, SourceControlWorkspaceStatus.Preparing);
        var docker = new FakeDockerExecutor();
        var bridge = CreateBridge(db, docker);
        await using var archive = CreateArchive(("README.md", "safe"));
        var mismatched = seeded.Lease with { OrganizationId = Guid.NewGuid() };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bridge.ImportAsync(mismatched, archive));

        Assert.Empty(docker.Commands);
    }

    [Fact]
    public async Task MaliciousArchiveIsRejectedBeforeDocker()
    {
        await using var db = CreateDb();
        var seeded = await SeedAsync(db, SourceControlWorkspaceStatus.Preparing);
        var docker = new FakeDockerExecutor();
        var bridge = CreateBridge(db, docker);
        await using var archive = CreateArchive(("src/.git/config", "credential = secret"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            bridge.ImportAsync(seeded.Lease, archive));

        Assert.Empty(docker.Commands);
    }

    [Fact]
    public async Task ExportRevalidatesAndReturnsSanitizedSnapshot()
    {
        await using var db = CreateDb();
        var seeded = await SeedAsync(db, SourceControlWorkspaceStatus.Ready);
        var docker = new FakeDockerExecutor(populateExport: true);
        var bridge = CreateBridge(db, docker);

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
        var docker = new FakeDockerExecutor();
        var bridge = CreateBridge(db, docker);

        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.ExportAsync(seeded.Lease));

        Assert.Empty(docker.Commands);
    }

    private static WorkspaceVolumeBridge CreateBridge(CSweetDbContext db, IDockerCommandExecutor docker) =>
        new(
            db,
            docker,
            new WorkspaceArtifactValidator(),
            Options.Create(new AgentRuntimeManagerOptions
            {
                SoftwareDevelopmentPolyglotImage = "trusted-runtime@sha256:" + new string('a', 64)
            }));

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

    private sealed class FakeDockerExecutor(bool populateExport = false) : IDockerCommandExecutor
    {
        public List<string[]> Commands { get; } = [];

        public Task<DockerCommandResult> ExecuteAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default,
            string? standardInput = null)
        {
            var command = arguments.ToArray();
            Commands.Add(command);
            if (populateExport && command is ["cp", var source, var destination] && source.Contains(':'))
            {
                var resultDirectory = Path.Combine(destination, "src");
                Directory.CreateDirectory(resultDirectory);
                File.WriteAllText(Path.Combine(resultDirectory, "result.txt"), "tested");
            }
            return Task.FromResult(new DockerCommandResult(0, string.Empty, string.Empty));
        }
    }
}
