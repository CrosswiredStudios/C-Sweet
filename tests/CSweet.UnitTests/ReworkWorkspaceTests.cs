using CSweet.AgentHost.Broker;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ReworkWorkspaceTests
{
    [Theory]
    [InlineData("unintegrated", true)]
    [InlineData("merged", false)]
    [InlineData("receipt", false)]
    [InlineData("superseded", false)]
    [InlineData("failed", false)]
    [InlineData("other-task", false)]
    [InlineData("other-repository", false)]
    [InlineData("other-organization", false)]
    [InlineData("other-target", false)]
    [InlineData("older-revision", false)]
    public async Task ReworkCarriesOnlyItsImmediateUnintegratedCandidate(string scenario, bool carries)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var repo = Guid.NewGuid(); var task = Guid.NewGuid();
        var workspace = new SourceControlWorkspace { Id = Guid.NewGuid(), OrganizationId = org,
            RepositoryId = repo, WorkItemId = task, AssignmentRevision = 1 };
        var publication = new SourceControlPublication { Id = Guid.NewGuid(), OrganizationId = org,
            RepositoryId = repo, WorkspaceId = workspace.Id, CommitSha = new string('a', 40),
            TargetBranch = "codex/story/one", Status = SourceControlPublicationStatus.AwaitingValidation };
        switch (scenario)
        {
            case "merged": publication.Status = SourceControlPublicationStatus.Merged; break;
            case "superseded": publication.Status = SourceControlPublicationStatus.Superseded; break;
            case "failed": publication.Status = SourceControlPublicationStatus.Failed; break;
            case "other-task": workspace.WorkItemId = Guid.NewGuid(); break;
            case "other-repository": workspace.RepositoryId = publication.RepositoryId = Guid.NewGuid(); break;
            case "other-organization": workspace.OrganizationId = publication.OrganizationId = Guid.NewGuid(); break;
            case "other-target": publication.TargetBranch = "codex/story/other"; break;
            case "older-revision": workspace.AssignmentRevision = 0; break;
            case "receipt": db.WorkTaskIntegrationReceipts.Add(new WorkTaskIntegrationReceipt {
                Id = Guid.NewGuid(), OrganizationId = org, RepositoryId = repo, WorkItemId = task,
                PublicationId = publication.Id, Status = "Completed" }); break;
        }
        db.AddRange(workspace, publication); await db.SaveChangesAsync();
        var result = await GitWorkspaceCapabilityHandler.ResolveReworkBaseAsync(db, org, repo, task, 2, "codex/story/one", default);
        Assert.Equal(carries ? publication.CommitSha : null, result);
    }
}
