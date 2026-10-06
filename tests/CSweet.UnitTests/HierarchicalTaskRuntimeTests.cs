using System.Reflection;
using System.Text.Json;
using CSweet.Application.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Fixture = CSweet.UnitTests.HierarchicalDeliveryServiceTests.Fixture;
using WorkSprint = CSweet.Domain.WorkManagement.WorkSprint;
using WorkOrchestrationPolicyRevision = CSweet.Domain.WorkManagement.WorkOrchestrationPolicyRevision;

namespace CSweet.UnitTests;

public sealed class HierarchicalTaskRuntimeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskRunsThroughReviewIntegrationAndExactQaWithReviewedRework(bool artifact)
    {
        await using var f = await Fixture.Create(artifact);
        await RunJourney(f, artifact);
    }

    internal static async Task RunJourney(Fixture f, bool artifact, bool game = false)
    {
        foreach (var person in new[] { f.Author, f.Qa, f.Technical })
        {
            person.ApplicationUserId = Guid.NewGuid();
            f.Db.Users.Add(new CSweet.Infrastructure.Auth.ApplicationUser { Id = person.ApplicationUserId.Value, UserName = person.Id.ToString("N") });
        }
        var authorKey = game ? "specialist-execution" : "development";
        if (game)
        {
            f.Board.ProfileKey = WorkBoardProfileKeys.ProjectDeliveryV2;
            (await f.Db.WorkItemStageAssignments.SingleAsync(x => x.WorkItemId == f.Task.Id && x.StageKey == "development")).StageKey = authorKey;
        }
        var policy = new WorkOrchestrationPolicyRevision { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = f.Board.Id,
            PolicyId = Guid.NewGuid(), InitialStageKey = "ready", IsPublished = true };
        f.Db.WorkOrchestrationPolicies.Add(new() { Id = policy.PolicyId, OrganizationId = f.Org, BoardId = f.Board.Id, Name = "Hierarchical task policy" });
        var definitions = HierarchicalWorkflows.Software(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        foreach (var stage in definitions.Stages)
            policy.Stages.Add(new() { Id = Guid.NewGuid(), PolicyRevisionId = policy.Id, Key = stage.Key == "development" ? authorKey : stage.Key, Name = stage.Name,
                Type = Enum.Parse<WorkOrchestrationStageType>(stage.StageType), PlatformAction = stage.PlatformAction, IsSuccessfulTerminal = stage.IsSuccessfulTerminal });
        foreach (var edge in definitions.Transitions)
            policy.Transitions.Add(new() { Id = Guid.NewGuid(), PolicyRevisionId = policy.Id,
                ToStageKey = edge.ToStageKey == "development" ? authorKey : edge.ToStageKey, OutcomeCode = edge.OutcomeCode, MaximumTraversals = edge.MaximumTraversals,
                FromStageKey = edge.FromStageKey == "development" ? authorKey : edge.FromStageKey });
        f.Db.WorkOrchestrationPolicyRevisions.Add(policy);
        var plan = await f.Configure(); await f.Activate(plan);
        var sprint = new WorkSprint { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = f.Board.Id, Name = "Task sprint", Status = WorkSprintStatus.Active };
        var execution = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = f.Board.Id,
            SprintId = sprint.Id, PolicyRevisionId = policy.Id, Status = WorkSprintExecutionStatus.Active };
        var item = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = f.Task.Id, WorkItem = f.Task, SprintExecutionId = execution.Id,
            SprintExecution = execution, Status = WorkItemExecutionStatus.WaitingForHuman, CurrentStageKey = authorKey };
        var assignments = await f.Db.WorkItemStageAssignments.Where(x => x.WorkItemId == f.Task.Id).ToListAsync();
        execution.AssignmentSnapshotJson = JsonSerializer.Serialize(assignments.Select(x => new { workItemId = f.Task.Id, stageKey = x.StageKey,
            principalKind = x.PrincipalKind, organizationUserId = x.OrganizationUserId, agentInstallationId = x.AgentInstallationId, platformAction = x.PlatformAction }), Json);
        execution.Items.Add(item); f.Db.AddRange(sprint, execution);
        WorkStageExecution Initial() => new() { Id = Guid.NewGuid(), StageKey = authorKey, StageType = WorkOrchestrationStageType.MemberExecution,
            PrincipalKind = WorkOrchestrationPrincipalKind.Human, OrganizationUserId = f.Author.Id, Status = WorkStageExecutionStatus.WaitingForHuman,
            CreatedAt = DateTimeOffset.UtcNow, ItemExecutionId = item.Id, ItemExecution = item };
        var authorStage = Initial(); item.Stages.Add(authorStage); f.Db.WorkStageExecutions.Add(authorStage);
        await f.Db.SaveChangesAsync();
        var manual = new WorkOrchestrationService(f.Db, TimeProvider.System);
        var inbox = new AgentWorkInbox(f.Db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var runtime = new WorkOrchestrator(f.Db, inbox, null!, null!,
            [new TaskIntegrationWorkActionExecutor(f.Db, f.Client, TimeProvider.System)], TimeProvider.System, NullLogger<WorkOrchestrator>.Instance);
        async Task Pulse() => await (Task)typeof(WorkOrchestrator).GetMethod("ReconcileExecutionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [execution.Id, CancellationToken.None])!;
        WorkStageExecution Current() => item.Stages.Single(x => x.StageKey == item.CurrentStageKey && x.Traversal == item.Traversal);
        async Task Complete(OrganizationUser person, string outcome, object output, IReadOnlyList<WorkExecutionEvidence>? evidence = null)
        {
            var stage = Current();
            await manual.CompleteManualAsync(f.Org, f.Board.Id, stage.Id, person.ApplicationUserId!.Value,
                new(f.Board.Id, execution.Id, stage.Id, outcome, "Exact evidence observed", JsonSerializer.SerializeToElement(output, Json), Guid.NewGuid().ToString("N"))
                { Evidence = evidence ?? [] });
        }
        for (var cycle = 0; cycle < 2; cycle++)
        {
            object authorOutput;
            Guid artifactId = Guid.Empty, revisionId = Guid.Empty; string digest = "";
            SourceControlPublication? publication = null;
            if (artifact)
            {
                artifactId = Guid.NewGuid(); revisionId = Guid.NewGuid(); var content = "Exact deliverable revision " + cycle;
                digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
                f.Db.CoreArtifacts.Add(new() { Id = artifactId, OrganizationId = f.Org, OriginWorkItemId = f.Task.Id, WorkstreamId = f.Project,
                    CreatedByOrganizationUserId = f.Author.Id, LatestRevisionId = revisionId, Title = "Deliverable" });
                f.Db.ArtifactRevisions.Add(new() { Id = revisionId, ArtifactId = artifactId, OrganizationId = f.Org, Content = content, ContentSha256 = digest,
                    CreatedByOrganizationUserId = f.Author.Id, Number = 1, IdempotencyKey = "delivered:" + revisionId.ToString("N") });
                authorOutput = new { artifactId, revisionId, sha256 = digest };
            }
            else
            {
                var branch = "codex/task/one/r" + cycle;
                var refs = await f.Client.DeliveryBranchAsync(new(f.Org, f.Repositories[0], "InternalGit", null, "", "", "inspect", branch, "codex/story/one", "inspect"));
                var workspace = new SourceControlWorkspace { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkItemId = f.Task.Id,
                    WorkspaceKey = "task:" + f.Task.Id.ToString("N") + ":" + cycle,
                    AssignmentRevision = f.Task.AssignmentRevision, RepositoryId = f.Repositories[0], BranchName = branch, IntegrationTargetBranch = "codex/story/one", BaseCommitSha = refs.TargetCommitSha };
                publication = new() { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkspaceId = workspace.Id, RepositoryId = f.Repositories[0],
                    CommitSha = refs.SourceCommitSha, TicketBranch = branch, TargetBranch = "codex/story/one", CreatedAt = DateTimeOffset.UtcNow };
                f.Db.AddRange(workspace, publication); authorOutput = new { commitSha = publication.CommitSha };
            }
            await f.Db.SaveChangesAsync();
            await Complete(f.Author, artifact ? "artifact-delivered" : "completed", authorOutput);
            if (!artifact)
            {
                Assert.Equal("technical-review", item.CurrentStageKey);
                var refs = await f.Client.DeliveryBranchAsync(new(f.Org, f.Repositories[0], "InternalGit", null, "", "", "inspect", publication!.TicketBranch, publication.TargetBranch, "inspect"));
                await Complete(f.Technical, "approved", new { }, [new("commit", "Reviewed source", publication.CommitSha), new("target-commit", "Story target", refs.TargetCommitSha)]);
                Assert.Null(cycle == 0 ? f.Task.MergeCommitSha : null);
                await Pulse(); Assert.Equal("quality", item.CurrentStageKey); Assert.NotNull(f.Task.MergeCommitSha);
            }
            Assert.Equal("quality", item.CurrentStageKey);
            if (artifact)
            {
                var qualityStageId = Current().Id;
                Assert.Single(await f.Db.ScopedActionGrants.Where(x => x.GrantedBySubjectId == qualityStageId && x.SubjectId == f.Qa.Id &&
                    x.ScopeId == artifactId && x.Action == "artifact.read" && x.RevokedAt == null).ToListAsync());
            }
            Assert.NotEqual(WorkTaskStatus.Completed, f.Task.Status);
            var passed = cycle == 1;
            var criteria = new[] { new WorkDeliveryCriterionResult("Requirement works", passed, "Observed against immutable revision") };
            var findings = passed ? Array.Empty<string>() : ["Criterion failed; reviewed fix required"];
            if (artifact)
            {
                var exact = new WorkArtifactQualityResult(artifactId, revisionId, digest, passed, "Checked exact content", criteria, findings);
                await Assert.ThrowsAsync<InvalidOperationException>(() => Complete(f.Qa, passed ? "passed" : "failed", exact with { RevisionId = Guid.NewGuid() }));
                await Complete(f.Qa, passed ? "passed" : "failed", exact);
                Assert.Empty(await f.Db.ScopedActionGrants.Where(x => x.ScopeId == artifactId && x.GrantedBySubjectKind == CSweet.Domain.Security.GrantSubjectKind.AutomationIdentity &&
                    x.Action == "artifact.read" && x.RevokedAt == null).ToListAsync());
            }
            else
            {
                var sha = f.Task.MergeCommitSha!;
                var quality = new WorkDeliveryReviewResult(sha, passed, "Ran current story tests", criteria, findings)
                    { Validations = [new(f.Repositories[0], sha, "test", passed ? 0 : 1, passed, "Observed test output")] };
                var claimedAgentStage = new WorkStageExecution { StageKey = "quality", ItemExecution = item,
                    OrganizationUserId = f.Qa.Id, AgentInstallationId = Guid.NewGuid() };
                var omittedCriteria = new WorkExecutionOutcomeV1(claimedAgentStage.Id, Guid.NewGuid(), WorkExecutionDispositions.Completed,
                    passed ? "passed" : "failed", "Omitted requirement evidence", JsonSerializer.SerializeToElement(quality with { Criteria = [] }, Json),
                    [new("commit", "Exact integrated story", sha)], []);
                Assert.Contains("every criterion", await runtime.RecordQualityValidationAsync(execution, claimedAgentStage, omittedCriteria, DateTimeOffset.UtcNow, default));
                await Assert.ThrowsAsync<InvalidOperationException>(() => Complete(f.Qa, passed ? "passed" : "failed", quality, [new("commit", "Wrong revision", publication!.CommitSha)]));
                await Assert.ThrowsAsync<InvalidOperationException>(() => Complete(f.Qa, passed ? "passed" : "failed", quality with
                    { Validations = [new(f.Repositories[0], sha, "test", passed ? 0 : 1, passed, "")] }, [new("commit", "Integrated story", sha)]));
                await Complete(f.Qa, passed ? "passed" : "failed", quality, [new("commit", "Integrated story", sha)]);
            }
            if (!passed) { Assert.Equal(authorKey, item.CurrentStageKey); Assert.Equal(1, item.Traversal); }
        }
        await Pulse();
        Assert.Equal(WorkTaskStatus.Completed, f.Task.Status);
        Assert.Equal(WorkSprintStatus.Completed, sprint.Status);
        Assert.NotEqual(WorkTaskStatus.Completed, f.Epic.Status);
        Assert.Equal(artifact ? 0 : 2, await f.Db.WorkTaskIntegrationReceipts.CountAsync(x => x.Status == "Completed"));
        await f.Service.PulseAsync();
        Assert.Equal("WaitingForHuman", (await f.Read()).Executions.Single(x => x.Scope == "Story").Status);
    }
}
