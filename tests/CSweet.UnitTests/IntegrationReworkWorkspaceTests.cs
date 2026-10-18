using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class IntegrationReworkWorkspaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorWorkspaceIsPinnedToTrustedMergedInputsAndRejectsDifferentPublication(bool wrongSource)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var repository = Guid.NewGuid(); var installation = Guid.NewGuid();
        var task = new CSweet.Domain.Core.WorkTask { Id = Guid.NewGuid(), OrganizationId = org, AssignmentRevision = 2 };
        var old = new SourceControlWorkspace { Id = Guid.NewGuid(), OrganizationId = org, WorkItemId = task.Id,
            RepositoryId = repository, AgentInstallationId = installation, AssignmentRevision = 1, TeamId = Guid.NewGuid() };
        var publication = new SourceControlPublication { Id = Guid.NewGuid(), OrganizationId = org, WorkspaceId = old.Id,
            RepositoryId = repository, CommitSha = new string('a', 40), TargetBranch = "csweet/story/one" };
        db.AddRange(task, old, publication); await db.SaveChangesAsync();
        var execution = new WorkItemExecution { WorkItem = task };
        var completed = new WorkStageExecution { StageKey = "task-integration", PlatformAction = TaskIntegrationWorkActionExecutor.ActionName,
            LastOutcomeCode = "integration_conflicts", ItemExecution = execution };
        var author = new WorkStageExecution { AgentInstallationId = installation };
        var json = JsonSerializer.Serialize(new { publicationId = publication.Id, repositoryId = repository,
            sourceCommitSha = new string(wrongSource ? 'b' : 'a', 40), reworkCommitSha = new string('c', 40),
            targetBranch = publication.TargetBranch });
        if (wrongSource)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => TaskIntegrationRework.SeedWorkspaceAsync(db, completed, author, json, DateTimeOffset.UtcNow, default));
            Assert.Single(await db.SourceControlWorkspaces.ToListAsync()); return;
        }
        await TaskIntegrationRework.SeedWorkspaceAsync(db, completed, author, json, DateTimeOffset.UtcNow, default);
        await db.SaveChangesAsync();
        var next = await db.SourceControlWorkspaces.SingleAsync(x => x.AssignmentRevision == 2);
        Assert.Equal(new string('c', 40), next.BaseCommitSha); Assert.Equal(installation, next.AgentInstallationId);
        Assert.Equal(old.TeamId, next.TeamId); Assert.Equal(publication.TargetBranch, next.IntegrationTargetBranch);
        Assert.Equal($"csweet/task/{task.Id:N}/r2", next.BranchName);
        Assert.Equal(SourceControlWorkspaceStatus.Preparing, next.Status);
        Assert.Equal(new string('a', 40), publication.CommitSha);
    }

    [Fact]
    public void IntegrationRecoveryCannotUseAConfiguredShortcutToQa()
    {
        var policy = new CSweet.Domain.WorkManagement.WorkOrchestrationPolicyRevision();
        policy.Transitions.Add(new WorkOrchestrationTransition { FromStageKey = "task-integration", OutcomeCode = "integration_conflicts", ToStageKey = "quality" });
        var stage = new WorkStageExecution { StageKey = "task-integration", PlatformAction = TaskIntegrationWorkActionExecutor.ActionName };
        Assert.Null(TaskIntegrationRework.Transition(policy, stage, "integration_conflicts"));
    }

    [Fact]
    public void RepeatedIdenticalConflictsStopForManagerWhileChangedTargetsResetBudget()
    {
        var execution = new WorkItemExecution { Traversal = 4 };
        var transition = new WorkOrchestrationTransition { MaximumTraversals = 2, ToStageKey = "development" };
        JsonElement Output(string target) => JsonSerializer.SerializeToElement(new { sourceCommitSha = new string('a', 40), targetCommitSha = target });
        for (var traversal = 0; traversal < 4; traversal++)
        {
            var stage = new WorkStageExecution { Id = Guid.NewGuid(), StageKey = "task-integration", Traversal = traversal,
                Status = WorkStageExecutionStatus.Completed };
            var attempt = new WorkExecutionAttempt { Id = Guid.NewGuid(), Status = WorkExecutionAttemptStatus.Completed };
            attempt.ResultJson = JsonSerializer.Serialize(new WorkExecutionOutcomeV1(stage.Id, attempt.Id, WorkExecutionDispositions.Completed, "integration_conflicts",
                "Resolve conflicts", Output("target"), [], []), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            stage.Attempts.Add(attempt); execution.Stages.Add(stage);
        }
        var current = new WorkStageExecution { Id = Guid.NewGuid(), StageKey = "task-integration", Traversal = 4, ItemExecution = execution };
        var stalled = WorkReworkPolicy.Evaluate(current, transition, "integration_conflicts", Output("target"));
        Assert.False(stalled.Allowed); Assert.True(stalled.ReviewRework);
        Assert.True(WorkReworkPolicy.Evaluate(current, transition, "integration_conflicts", Output("new-target")).Allowed);
    }
}
