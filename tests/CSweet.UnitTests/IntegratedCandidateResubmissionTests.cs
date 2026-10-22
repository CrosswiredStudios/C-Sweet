using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shared = CSweet.WorkManagement.Contracts;
using Fixture = CSweet.UnitTests.HierarchicalDeliveryServiceTests.Fixture;

namespace CSweet.UnitTests;

public sealed class IntegratedCandidateResubmissionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CarriedStoppedQaRetainsIntegrationAndRechecksTheCurrentBranch(bool advanced)
    {
        var (f, original, candidate) = await Seed(); await using var cleanup = f;
        var prior = original.ItemExecution!;
        prior.Status = WorkItemExecutionStatus.Cancelled; prior.CurrentStageKey = "quality";
        prior.SprintExecution!.Status = WorkSprintExecutionStatus.Cancelled;
        await f.Db.SaveChangesAsync();
        var sprint = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = f.Board.Id,
            Status = WorkSprintExecutionStatus.Active, PolicyRevisionId = prior.SprintExecution.PolicyRevisionId,
            AssignmentSnapshotJson = prior.SprintExecution.AssignmentSnapshotJson };
        var current = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = f.Task.Id, WorkItem = f.Task,
            SprintExecutionId = sprint.Id, SprintExecution = sprint, CurrentStageKey = "development" };
        var author = WorkOrchestrationService.CreateStageExecution(current,
            new WorkOrchestrationStage { Key = "development", Name = "Development", Type = WorkOrchestrationStageType.AgentExecution },
            f.Task.StageAssignments, f.Manager.Id, DateTimeOffset.UtcNow);
        Assert.Same(current, author.ItemExecution);
        sprint.Items.Add(current); f.Db.Add(sprint);
        if (advanced) f.Host.AdvanceStory(f.Repositories[0], "csweet/story/one");
        if (advanced)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => IntegratedCandidateResubmission.ResumeCarriedCandidateAsync(
                f.Db, f.Client, author, f.Manager.Id, DateTimeOffset.UtcNow, default));
            Assert.DoesNotContain(current.Stages, s => s.StageKey == "quality");
        }
        else
        {
            Assert.True(await IntegratedCandidateResubmission.ResumeCarriedCandidateAsync(f.Db, f.Client, author,
                f.Manager.Id, DateTimeOffset.UtcNow, default));
            await f.Db.SaveChangesAsync();
            Assert.Equal("quality", current.CurrentStageKey);
            Assert.Equal(WorkStageExecutionStatus.WaitingForHuman, current.Stages.Single(s => s.StageKey == "quality").Status);
            Assert.Contains(candidate, current.Stages.Single(s => s.StageKey == "task-integration").Attempts.Single().ResultJson);
            Assert.Single(await f.Db.WorkTaskIntegrationReceipts.ToListAsync());
            Assert.Single(await f.Db.SourceControlPublications.ToListAsync());
            Assert.Equal(WorkItemExecutionStatus.Cancelled, prior.Status);
        }
    }

    [Fact]
    public async Task Resubmission_reuses_exact_receipt_and_queues_fresh_QA_without_new_publication_or_pass()
    {
        var (fixture, author, candidate) = await Seed(); await using var cleanup = fixture;
        var service = new WorkOrchestrationService(fixture.Db, TimeProvider.System, sourceControlHost: fixture.Client);
        var preview = await service.GetIntegratedCandidateAsync(fixture.Org, fixture.Board.Id, fixture.Task.Id, fixture.Manager.Id);
        Assert.Equal(candidate, preview.IntegratedCommitSha);
        Assert.NotEqual(preview.PublishedCommitSha, preview.IntegratedCommitSha);
        var promotions = fixture.Host.SuccessfulPromotions[fixture.Repositories[0]];
        var request = new ResubmitIntegratedCandidateRequest(fixture.Task.Revision, candidate, "resubmit-once", "Current criteria need no code change; independently test the same integrated candidate.");
        await service.ResubmitIntegratedCandidateAsync(fixture.Org, fixture.Board.Id, fixture.Task.Id, fixture.Manager.Id, request);
        await service.ResubmitIntegratedCandidateAsync(fixture.Org, fixture.Board.Id, fixture.Task.Id, fixture.Manager.Id, request);
        fixture.Db.ChangeTracker.Clear();
        var current = await fixture.Db.WorkItemExecutions.Include(entry => entry.Stages).ThenInclude(entry => entry.Attempts).SingleAsync();
        Assert.Equal("quality", current.CurrentStageKey);
        Assert.Single(current.Stages.Where(stage => stage.StageKey == "quality" && stage.Traversal == 1));
        Assert.DoesNotContain(current.Stages, stage => stage.StageKey == "quality" && stage.LastOutcomeCode == "passed");
        Assert.Single(await fixture.Db.SourceControlPublications.ToListAsync());
        Assert.Single(await fixture.Db.WorkTaskIntegrationReceipts.ToListAsync());
        Assert.Single(await fixture.Db.WorkOrchestrationEvents.Where(entry => entry.EventType == IntegratedCandidateResubmission.EventType).ToListAsync());
        Assert.Equal(promotions, fixture.Host.SuccessfulPromotions[fixture.Repositories[0]]);
        Assert.Contains("Prior unchanged rework", current.Stages.Single(stage => stage.Id == author.Id).Attempts.Single().ResultJson);
        var item = await fixture.Db.CoreWorkTasks.SingleAsync(entry => entry.Id == fixture.Task.Id);
        Assert.Equal(preview.PublicationId, (await IntegratedCandidateResubmission.CurrentPublicationAsync(fixture.Db, item, default))!.Id);
        var qa = current.Stages.Single(stage => stage.StageKey == "quality");
        current.WorkItem = item;
        var evidence = Assert.Single(await IntegratedCandidateResubmission.QaEvidenceAsync(fixture.Db, qa, default));
        Assert.Contains(request.Reason, evidence.Value);
        Assert.Contains("no verdict granted", evidence.Name);
        var integration = current.Stages.Single(stage => stage.StageKey == "task-integration").Attempts.Single();
        Assert.Contains(candidate, integration.ResultJson);
        Assert.Empty(await fixture.Db.SourceControlValidations.ToListAsync());
        Assert.NotEmpty(await fixture.Db.ApplicationRealtimeOutbox.ToListAsync());
        item.AssignmentRevision++;
        Assert.Null(await IntegratedCandidateResubmission.CurrentPublicationAsync(fixture.Db, item, default));
    }

    [Theory]
    [InlineData("stale-revision")]
    [InlineData("wrong-sha")]
    [InlineData("wrong-actor")]
    [InlineData("active-author")]
    [InlineData("paused-sprint")]
    [InlineData("story-advanced")]
    [InlineData("source-advanced")]
    [InlineData("revoked-repository")]
    [InlineData("unintegrated-publication")]
    [InlineData("foreign-receipt")]
    public async Task Invalid_resubmission_fails_closed_without_new_QA(string scenario)
    {
        var (fixture, author, candidate) = await Seed(); await using var cleanup = fixture;
        var revision = fixture.Task.Revision;
        var actor = fixture.Manager.Id;
        if (scenario == "stale-revision") revision--;
        if (scenario == "wrong-sha") candidate = new string('0', 64);
        if (scenario == "wrong-actor") actor = fixture.Author.Id;
        if (scenario == "active-author") author.Status = WorkStageExecutionStatus.Running;
        if (scenario == "paused-sprint") author.ItemExecution!.SprintExecution!.Status = WorkSprintExecutionStatus.Paused;
        if (scenario == "story-advanced") fixture.Host.AdvanceStory(fixture.Repositories[0], "csweet/story/one");
        if (scenario == "source-advanced") fixture.Host.AdvanceStory(fixture.Repositories[0], "csweet/task/original/r1");
        if (scenario == "revoked-repository") (await fixture.Db.TeamRepositoryPolicies.SingleAsync()).DisabledAt = DateTimeOffset.UtcNow;
        if (scenario == "unintegrated-publication") (await fixture.Db.SourceControlPublications.SingleAsync()).Status = SourceControlPublicationStatus.Published;
        if (scenario == "foreign-receipt") (await fixture.Db.WorkTaskIntegrationReceipts.SingleAsync()).WorkItemId = Guid.NewGuid();
        await fixture.Db.SaveChangesAsync();
        var service = new WorkOrchestrationService(fixture.Db, TimeProvider.System, sourceControlHost: fixture.Client);
        Assert.NotNull(await Record.ExceptionAsync(() => service.ResubmitIntegratedCandidateAsync(fixture.Org, fixture.Board.Id,
            fixture.Task.Id, actor, new(revision, candidate, "invalid", "Reassess unchanged build."))));
        fixture.Db.ChangeTracker.Clear();
        Assert.Empty(await fixture.Db.WorkOrchestrationEvents.Where(entry => entry.EventType == IntegratedCandidateResubmission.EventType).ToListAsync());
        Assert.DoesNotContain(await fixture.Db.WorkStageExecutions.ToListAsync(), stage => stage.StageKey == "quality");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Agent_resubmission_requires_exact_integrated_identity_and_current_successful_validation(bool invalid)
    {
        var (fixture, author, candidate) = await Seed(); await using var cleanup = fixture;
        var orchestrator = new WorkOrchestrator(fixture.Db, null!, null!, null!, [], TimeProvider.System, null!, fixture.Client);
        var output = JsonSerializer.SerializeToElement(new { ResubmitsIntegratedCandidate = true, RepositoryId = fixture.Repositories[0], CommitSha = candidate,
            ChangedFiles = Array.Empty<string>(), Validations = new[] { new { Command = "npm test", Succeeded = !invalid, ExitCode = invalid ? 1 : 0 } } });
        var result = new Shared.WorkExecutionOutcomeV1(author.Id, author.Attempts.Single().Id, Shared.WorkExecutionDispositions.Completed,
            "code-published", "Unchanged, validated integrated candidate.", output, [new("commit", "Exact integrated commit", candidate)], []);
        if (invalid) await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ValidateIntegratedResubmissionAsync(author, result, default));
        else Assert.Equal(candidate, (await orchestrator.ValidateIntegratedResubmissionAsync(author, result, default)).IntegratedCommitSha);
    }

    private static async Task<(Fixture Fixture, WorkStageExecution Author, string Candidate)> Seed()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;
        var fixture = new Fixture(false, options); await fixture.Seed();
        foreach (var person in new[] { fixture.Manager, fixture.Author, fixture.Qa, fixture.Technical }) person.ApplicationUserId = person.Id;
        fixture.Task.AssignmentRevision = 3; fixture.Task.PlanningRevision = 3;
        await fixture.Db.SaveChangesAsync(); await fixture.Activate(await fixture.Configure());
        var policy = new WorkOrchestrationPolicyRevision { Id = Guid.NewGuid(), OrganizationId = fixture.Org, BoardId = fixture.Board.Id, PolicyId = Guid.NewGuid(), IsPublished = true };
        policy.Stages = [new() { Id = Guid.NewGuid(), PolicyRevisionId = policy.Id, Key = "development", Type = WorkOrchestrationStageType.ManualWork },
            new() { Id = Guid.NewGuid(), PolicyRevisionId = policy.Id, Key = "task-integration", Type = WorkOrchestrationStageType.TrustedPlatformAction, PlatformAction = TaskIntegrationWorkActionExecutor.ActionName },
            new() { Id = Guid.NewGuid(), PolicyRevisionId = policy.Id, Key = "quality", Type = WorkOrchestrationStageType.ManualWork }];
        policy.Transitions = [new() { Id = Guid.NewGuid(), PolicyRevisionId = policy.Id, FromStageKey = "task-integration", OutcomeCode = "merged", ToStageKey = "quality" }];
        var sprint = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = fixture.Org, BoardId = fixture.Board.Id, Status = WorkSprintExecutionStatus.Active,
            PolicyRevisionId = policy.Id, AssignmentSnapshotJson = JsonSerializer.Serialize((await fixture.Db.WorkItemStageAssignments.ToListAsync()).Select(assignment =>
                new WorkOrchestrationService.AssignmentSnapshot(fixture.Task.Id, assignment.StageKey, assignment.PrincipalKind, assignment.OrganizationUserId,
                    assignment.AgentInstallationId, assignment.PlatformAction, assignment.RequirementsJson, assignment.SelectionEvidenceJson)), Json) };
        var item = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = fixture.Task.Id, WorkItem = fixture.Task, SprintExecutionId = sprint.Id,
            SprintExecution = sprint, Traversal = 1, CurrentStageKey = "development", Status = WorkItemExecutionStatus.Blocked };
        var author = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = item.Id, ItemExecution = item, StageKey = "development",
            Traversal = 1, OrganizationUserId = fixture.Author.Id, Status = WorkStageExecutionStatus.Blocked };
        author.Attempts.Add(new() { Id = Guid.NewGuid(), StageExecutionId = author.Id, Attempt = 1, Status = WorkExecutionAttemptStatus.Completed, ResultJson = "Prior unchanged rework" });
        item.Stages.Add(author); sprint.Items.Add(item);
        var workspace = new SourceControlWorkspace { Id = Guid.NewGuid(), OrganizationId = fixture.Org, WorkItemId = fixture.Task.Id, RepositoryId = fixture.Repositories[0], AssignmentRevision = 1 };
        var branch = new CSweet.Contracts.SourceControl.DeliveryBranchOperation(fixture.Org, fixture.Repositories[0], "InternalGit", null, "", "", "candidate",
            "csweet/task/original/r1", "csweet/story/one", "prepare-integrated");
        var integrated = await fixture.Client.DeliveryBranchAsync(branch);
        await fixture.Client.DeliveryBranchAsync(branch with { Operation = "promote", CandidateCommitSha = integrated.CandidateCommitSha });
        var publication = new SourceControlPublication { Id = Guid.NewGuid(), OrganizationId = fixture.Org, WorkspaceId = workspace.Id, RepositoryId = fixture.Repositories[0],
            TicketBranch = branch.SourceBranch, TargetBranch = branch.TargetBranch, CommitSha = integrated.SourceCommitSha, Status = SourceControlPublicationStatus.Merged };
        fixture.Task.MergeCommitSha = integrated.CandidateCommitSha; fixture.Task.MergeStatus = "Merged";
        fixture.Db.AddRange(policy, sprint, workspace, publication, new WorkTaskIntegrationReceipt { Id = Guid.NewGuid(), OrganizationId = fixture.Org,
            PlanId = fixture.PlanId, ScopeRevision = 1, ItemExecutionId = item.Id, WorkItemId = fixture.Task.Id, PublicationId = publication.Id, RepositoryId = fixture.Repositories[0],
            SourceCommitSha = publication.CommitSha, TargetCommitSha = integrated.TargetCommitSha, CandidateCommitSha = integrated.CandidateCommitSha!, Status = "Completed" });
        await fixture.Db.SaveChangesAsync();
        return (fixture, author, integrated.CandidateCommitSha!);
    }
}
