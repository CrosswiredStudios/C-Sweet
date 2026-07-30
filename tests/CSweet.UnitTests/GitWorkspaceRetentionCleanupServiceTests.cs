using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class GitWorkspaceRetentionCleanupServiceTests
{
    [Fact]
    public async Task CleanupAsync_RemovesOnlyExpiredFailedAssignmentDirectory()
    {
        await using var db = new CSweetDbContext(
            new DbContextOptionsBuilder<CSweetDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var installationKey = Guid.NewGuid();
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(),
            InstallationKey = installationKey,
            BusinessId = "business"
        };
        var workspace = new GitTicketWorkspace
        {
            Id = Guid.NewGuid(),
            OrganizationId = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            WorkItemId = Guid.NewGuid(),
            AssignmentRevision = 7,
            RepositoryConnectionId = Guid.NewGuid(),
            WorkspacePath = "/workspace/ignored",
            BranchName = "csweet/ticket",
            Status = GitTicketWorkspaceStatus.Failed,
            RetainUntil = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        db.AgentInstallations.Add(installation);
        db.GitTicketWorkspaces.Add(workspace);
        await db.SaveChangesAsync();
        var docker = new CapturingDocker(
            new DockerCommandResult(0, "[]", ""),
            new DockerCommandResult(0, "", ""));
        var service = new GitWorkspaceRetentionCleanupService(
            db,
            docker,
            Options.Create(new AgentRuntimeManagerOptions
            {
                SoftwareDevelopmentPolyglotImage =
                    "csweet/software-development-polyglot-v1:local"
            }),
            NullLogger<GitWorkspaceRetentionCleanupService>.Instance);

        var removed = await service.CleanupAsync();

        Assert.Equal(1, removed);
        Assert.Equal(GitTicketWorkspaceStatus.Removed, workspace.Status);
        Assert.Null(workspace.RetainUntil);
        Assert.Equal(
            ["volume", "inspect", $"csweet-workspace-{installationKey:N}"],
            docker.Commands[0]);
        var cleanup = docker.Commands[1];
        Assert.Contains("--network", cleanup);
        Assert.Contains("none", cleanup);
        Assert.Contains("--read-only", cleanup);
        Assert.Contains(
            cleanup,
            x => x.Contains(
                $"/workspace/{workspace.WorkItemId:N}/{workspace.AssignmentRevision}",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            cleanup,
            x => x.Contains("docker.sock", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CapturingDocker(params DockerCommandResult[] results)
        : IDockerCommandExecutor
    {
        private readonly Queue<DockerCommandResult> _results = new(results);
        public List<IReadOnlyList<string>> Commands { get; } = [];

        public Task<DockerCommandResult> ExecuteAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default,
            string? standardInput = null)
        {
            Commands.Add(arguments.ToArray());
            return Task.FromResult(_results.Dequeue());
        }
    }
}
