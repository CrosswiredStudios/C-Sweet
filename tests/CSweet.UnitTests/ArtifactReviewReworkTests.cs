using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using Fixture = CSweet.UnitTests.HierarchicalDeliveryServiceTests.Fixture;
using Stage = CSweet.Domain.WorkManagement.WorkStageExecution;

namespace CSweet.UnitTests;

public sealed class ArtifactReviewReworkTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("development", false)]
    [InlineData("development", true)]
    [InlineData("specialist-execution", false)]
    [InlineData("specialist-execution", true)]
    public void DispatchBindsOnlyCurrentAuthorDespiteShuffledCyclesAndAttempts(string authorKey, bool reverse)
    {
        var item = Item();
        var old = Author(authorKey, 0); var current = Author(authorKey, 1);
        current.Attempts.Add(new() { Id = Guid.NewGuid(), Attempt = 1, Status = WorkExecutionAttemptStatus.Failed });
        item.Stages = reverse ? [current, old] : [old, current];
        var qa = new Stage { StageKey = "quality", Traversal = 1, ItemExecution = item };
        var expected = current.Attempts.Single(x => x.Attempt == 2);
        Assert.Equal(expected.Id, Assert.Single(WorkOrchestrator.AssignmentOutcomes(qa)).AttemptId);
        current.Attempts = current.Attempts.Reverse().ToList();
        Assert.Equal(expected.Id, Assert.Single(WorkOrchestrator.AssignmentOutcomes(qa)).AttemptId);
    }

    [Theory]
    [InlineData(WorkExecutionAttemptStatus.Pending)]
    [InlineData(WorkExecutionAttemptStatus.Running)]
    [InlineData(WorkExecutionAttemptStatus.Failed)]
    [InlineData(WorkExecutionAttemptStatus.Cancelled)]
    [InlineData(WorkExecutionAttemptStatus.Completed)]
    public void DispatchNeverFallsBackFromAnUndeliveredLatestAttempt(WorkExecutionAttemptStatus status)
    {
        var item = Item(); var author = Author("specialist-execution", 1); item.Stages.Add(author);
        author.Attempts.Add(new() { Id = Guid.NewGuid(), Attempt = 3, Status = status });
        Assert.Throws<InvalidOperationException>(() => WorkOrchestrator.AssignmentOutcomes(
            new() { StageKey = "quality", Traversal = 1, ItemExecution = item }));
    }

    [Theory]
    [InlineData("development")]
    [InlineData("specialist-execution")]
    public async Task FreshClaimPreservesCurrentGrantAndRevokesOldCycleGrant(string authorKey)
    {
        await using var f = await Fixture.Create(true);
        var plan = await f.Configure(); await f.Activate(plan);
        var now = DateTimeOffset.UtcNow;
        var policy = new CSweet.Domain.WorkManagement.WorkOrchestrationPolicyRevision
        { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = f.Board.Id,
          Stages = [new() { Id = Guid.NewGuid(), Key = "quality", Type = WorkOrchestrationStageType.AgentExecution }] };
        var execution = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = f.Org,
            BoardId = f.Board.Id, PolicyRevisionId = policy.Id, Status = WorkSprintExecutionStatus.Active };
        var item = Item(); item.Id = Guid.NewGuid(); item.WorkItem = f.Task; item.WorkItemId = f.Task.Id;
        item.SprintExecution = execution; item.SprintExecutionId = execution.Id;
        var installation = Guid.NewGuid();
        var old = Author(authorKey, 0); var author = Author(authorKey, 1);
        author.OrganizationUserId = f.Author.Id;
        var qa = new Stage { Id = Guid.NewGuid(), StageKey = "quality", Traversal = 1, OrganizationUserId = f.Qa.Id,
            AgentInstallationId = installation, PrincipalKind = WorkOrchestrationPrincipalKind.AgentInstallation,
            StageType = WorkOrchestrationStageType.AgentExecution, Status = WorkStageExecutionStatus.Dispatching };
        item.Stages = [qa, old, author]; execution.Items.Add(item);
        var output = JsonSerializer.Deserialize<WorkExecutionOutcomeV1>(author.Attempts.Single().ResultJson!, Json)!;
        Assert.True(WorkArtifactDelivery.TryRead(output.Output, out var reference));
        f.Db.CoreArtifacts.Add(new() { Id = reference.ArtifactId, OrganizationId = f.Org, OriginWorkItemId = f.Task.Id,
            CreatedByOrganizationUserId = f.Author.Id, Title = "Current replacement" });
        f.Db.ArtifactRevisions.Add(new() { Id = reference.RevisionId, ArtifactId = reference.ArtifactId,
            OrganizationId = f.Org, CreatedByOrganizationUserId = f.Author.Id, ContentSha256 = reference.Sha256 });
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(), AgentInstallationId = installation,
            SourceType = "WorkStageExecution", SourceId = qa.Id.ToString() };
        qa.Attempts.Add(new() { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, Status = WorkExecutionAttemptStatus.Pending, Attempt = 1 });
        var oldQa = new Stage { Id = Guid.NewGuid(), StageKey = "quality", Traversal = 0, Status = WorkStageExecutionStatus.Completed };
        item.Stages.Add(oldQa);
        var currentGrant = new ScopedActionGrant { Id = Guid.NewGuid(), OrganizationId = f.Org, SubjectKind = GrantSubjectKind.AgentInstallation,
            SubjectId = installation, Action = "artifact.read", ScopeKind = GrantScopeKind.Artifact, ScopeId = reference.ArtifactId,
            GrantedBySubjectKind = GrantSubjectKind.AutomationIdentity, GrantedBySubjectId = qa.Id, GrantedAt = now, ExpiresAt = now.AddHours(1) };
        var oldGrant = new ScopedActionGrant { Id = Guid.NewGuid(), OrganizationId = f.Org, SubjectKind = GrantSubjectKind.AgentInstallation,
            SubjectId = installation, Action = "artifact.read", ScopeKind = GrantScopeKind.Artifact, ScopeId = Guid.NewGuid(),
            GrantedBySubjectKind = GrantSubjectKind.AutomationIdentity, GrantedBySubjectId = oldQa.Id, GrantedAt = now, ExpiresAt = now.AddHours(1) };
        f.Db.AddRange(policy, execution, work, currentGrant, oldGrant); await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear(); // Actual inbox claim starts without the producer navigation graph.
        await WorkOrchestrationBoardState.RecordClaimAsync(f.Db, work, now.AddSeconds(1), default);
        Assert.Null((await f.Db.ScopedActionGrants.SingleAsync(x => x.Id == currentGrant.Id)).RevokedAt);
        Assert.NotNull((await f.Db.ScopedActionGrants.SingleAsync(x => x.Id == oldGrant.Id)).RevokedAt);
        Assert.Equal(WorkStageExecutionStatus.Running, (await f.Db.WorkStageExecutions.FindAsync(qa.Id))!.Status);
        f.Db.ChangeTracker.Clear();
        await WorkOrchestrationBoardState.RecordClaimAsync(f.Db, work, now.AddSeconds(2), default);
        Assert.Single(await f.Db.ScopedActionGrants.Where(x => x.GrantedBySubjectId == qa.Id && x.RevokedAt == null).ToListAsync());
        var loaded = await f.Db.WorkSprintExecutions.FindAsync(execution.Id);
        loaded!.Status = WorkSprintExecutionStatus.Paused;
        await WorkOrchestrationBoardState.SaveBoardChangesAsync(f.Db, loaded, now.AddSeconds(3), default);
        Assert.NotNull((await f.Db.ScopedActionGrants.FindAsync(currentGrant.Id))!.RevokedAt);
    }

    private static WorkItemExecution Item() => new() { Traversal = 1, CurrentStageKey = "quality",
        WorkItem = new WorkTask { DeliverySpecificationJson = JsonSerializer.Serialize(new WorkItemDeliverySpecification(Guid.Empty, [], []) { DeliveryKind = "Artifact" }, Json) } };

    private static Stage Author(string key, int traversal)
    {
        var stage = new Stage { Id = Guid.NewGuid(), StageKey = key, Traversal = traversal, Status = WorkStageExecutionStatus.Completed };
        var attempt = new WorkExecutionAttempt { Id = Guid.NewGuid(), Attempt = 2, Status = WorkExecutionAttemptStatus.Completed };
        attempt.ResultJson = JsonSerializer.Serialize(new WorkExecutionOutcomeV1(stage.Id, attempt.Id, WorkExecutionDispositions.Completed,
            "completed", "Delivered", new WorkArtifactDelivery(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64)).ToOutput(), [], []), Json);
        stage.Attempts.Add(attempt); return stage;
    }
}
