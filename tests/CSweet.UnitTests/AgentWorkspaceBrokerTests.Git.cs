using CSweet.Domain.Setup;
using CSweet.Infrastructure.SourceControl;
using CSweet.TrustedServices;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentWorkspaceBrokerTests
{
    [Theory]
    [InlineData("git-upload-pack", "snapshot-pull", true)]
    [InlineData("git-receive-pack", "publish", false)]
    public async Task GitExchangeConfinesTheAgentToItsWorkspaceBranchAndBase(string service, string operation, bool advertise)
    {
        await using var db = CreateDb();
        var request = await SeedOperationAsync(db, operation);
        var host = new FakeHost(); var volumes = new FakeVolumes();
        byte[] body = advertise ? [] : [1, 2, 3];

        var result = await new AgentWorkspaceBroker(db, host, volumes, WorkspaceSyncTestOptions.Value)
            .GitAsync(new(request, service, advertise, body));

        var workspace = await db.SourceControlWorkspaces.AsNoTracking().SingleAsync();
        var git = host.GitRequest!;
        Assert.Equal($"application/x-{service}-result", result.ContentType);
        Assert.Equal((workspace.OrganizationId, workspace.RepositoryId, service, advertise),
            (git.OrganizationId, git.RepositoryId, git.Service, git.Advertise));
        Assert.Equal(body, git.Body);
        Assert.Equal(workspace.BranchName, git.AgentBranch);
        Assert.Equal(new string('a', 40), git.AgentBaseCommitSha);
        Assert.Equal(WorkspaceSyncTestOptions.Value.Value.MaximumGitBlobBytes, git.AgentMaximumBlobBytes);
        Assert.Empty(git.ProtectedBranches);
        Assert.Equal((await db.CoreOrganizationUsers.AsNoTracking().SingleAsync()).Id, git.ActorId);
        Assert.Null(host.Operation); Assert.Null(volumes.Lease);
    }

    [Fact]
    public async Task PublishingAPushedCommitUsesTheWorkspaceBranchAndBaseAndReadsNoSnapshot()
    {
        await using var db = CreateDb();
        var request = await SeedOperationAsync(db, "publish");
        var host = new FakeHost(); var volumes = new FakeVolumes();
        var pushed = new string('c', 40);

        var result = await new AgentWorkspaceBroker(db, host, volumes, WorkspaceSyncTestOptions.Value)
            .ExecuteAsync(request with { PushedCommitSha = pushed }, "http://localhost");

        var workspace = await db.SourceControlWorkspaces.AsNoTracking().SingleAsync();
        var publication = host.PushedPublication!;
        Assert.Equal(("Published", pushed, workspace.BranchName), (result.Status, result.CommitSha, result.Branch));
        Assert.Contains(Uri.EscapeDataString("refs/heads/" + workspace.BranchName), result.ReviewUrl);
        Assert.Equal((workspace.OrganizationId, workspace.RepositoryId, workspace.Id), (publication.OrganizationId, publication.RepositoryId, publication.WorkspaceId));
        Assert.Equal((new string('a', 40), workspace.BranchName, pushed, request.IdempotencyKey),
            (publication.BaseSha, publication.Branch, publication.PushedSha, publication.IdempotencyKey));
        Assert.Equal((await db.CoreOrganizationUsers.AsNoTracking().SingleAsync()).Id, publication.LockOwnerId);
        Assert.Null(host.Operation); Assert.Null(volumes.Lease);
    }

    [Theory]
    [InlineData("inspect")]
    [InlineData("refresh")]
    [InlineData("cleanup")]
    [InlineData("snapshot-pull")]
    public async Task APushedCommitIsAcceptedOnlyForPublication(string operation)
    {
        await using var db = CreateDb();
        var request = await SeedOperationAsync(db, operation);
        var host = new FakeHost(); var volumes = new FakeVolumes();

        await Assert.ThrowsAsync<ArgumentException>(() => new AgentWorkspaceBroker(db, host, volumes, WorkspaceSyncTestOptions.Value)
            .ExecuteAsync(request with { PushedCommitSha = new string('c', 40) }, "http://localhost"));

        Assert.Null(host.PushedPublication); Assert.Null(volumes.Lease);
    }

    [Theory]
    [InlineData("git-receive-pack", "snapshot-pull")]
    [InlineData("git-upload-pack", "publish")]
    [InlineData("git-upload-archive", "snapshot-pull")]
    [InlineData("git-receive-pack", "inspect")]
    public async Task GitExchangeRejectsAServiceThatDoesNotMatchItsAuthorizedOperation(string service, string operation)
    {
        await using var db = CreateDb();
        var request = await SeedOperationAsync(db, operation);
        var host = new FakeHost(); var volumes = new FakeVolumes();
        await Assert.ThrowsAsync<ArgumentException>(() => new AgentWorkspaceBroker(db, host, volumes, WorkspaceSyncTestOptions.Value)
            .GitAsync(new(request, service, false, [1])));
        Assert.Null(host.GitRequest);
    }

    [Fact]
    public async Task GitExchangeRejectsAnAdvertisementThatCarriesABody()
    {
        await using var db = CreateDb();
        var request = await SeedOperationAsync(db, "snapshot-pull");
        var host = new FakeHost(); var volumes = new FakeVolumes();
        await Assert.ThrowsAsync<ArgumentException>(() => new AgentWorkspaceBroker(db, host, volumes, WorkspaceSyncTestOptions.Value)
            .GitAsync(new(request, "git-upload-pack", true, [1])));
        Assert.Null(host.GitRequest);
    }

    [Theory]
    [InlineData("assignment")]
    [InlineData("policy")]
    [InlineData("membership")]
    [InlineData("identity")]
    public async Task GitExchangeStopsWhenWorkspaceAccessIsRevoked(string revoked)
    {
        await using var db = CreateDb();
        var request = await SeedOperationAsync(db, "publish");
        if (revoked == "assignment") (await db.CoreWorkTasks.SingleAsync()).AssignmentRevision++;
        if (revoked == "policy") (await db.TeamRepositoryPolicies.SingleAsync()).DisabledAt = DateTimeOffset.UtcNow;
        if (revoked == "membership") (await db.TeamMemberships.SingleAsync()).EndedAt = DateTimeOffset.UtcNow;
        if (revoked == "identity") (await db.CoreOrganizationUsers.SingleAsync()).IsActive = false;
        await db.SaveChangesAsync();
        var host = new FakeHost(); var volumes = new FakeVolumes();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new AgentWorkspaceBroker(db, host, volumes, WorkspaceSyncTestOptions.Value)
            .GitAsync(new(request, "git-receive-pack", false, [1])));
        Assert.Null(host.GitRequest);
    }

    [Fact]
    public async Task GitExchangeIsRefusedForGitHubRepositories()
    {
        await using var db = CreateDb();
        var request = await SeedOperationAsync(db, "snapshot-pull");
        (await db.SourceControlConnections.SingleAsync()).Provider = SourceControlProvider.GitHub;
        await db.SaveChangesAsync();
        var host = new FakeHost(); var volumes = new FakeVolumes();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentWorkspaceBroker(db, host, volumes, WorkspaceSyncTestOptions.Value)
            .GitAsync(new(request, "git-upload-pack", true, [])));
        Assert.Null(host.GitRequest);
    }
}
