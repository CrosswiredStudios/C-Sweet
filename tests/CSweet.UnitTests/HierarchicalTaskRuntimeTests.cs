using System.Reflection;
using System.Text.Json;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.SourceControl;
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

    [Fact]
    public async Task CodeJourneyAdvancesThroughIntegrationQaReworkAndDoneWithIdenticalStageTimestamps()
    {
        await using var f = await Fixture.Create(false);
        await RunJourney(f, false, identicalStageTimestamps: true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IntegrationConflictReturnsToAuthorThenRequiresFreshTechnicalReviewAndQa(bool game, bool oldPolicy)
    {
        await using var f = await Fixture.Create(false);
        f.Host.IntegrationConflictOnce = true;
        await RunJourney(f, false, game, integrationConflict: true, oldPolicy: oldPolicy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedStoryRequiresFreshReviewWithoutReauthoringOrReusingOldApproval(bool oldPolicy)
    {
        await using var f = await Fixture.Create(false);
        await RunJourney(f, false, oldPolicy: oldPolicy, targetChange: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskRunsThroughReviewIntegrationAndExactQaWithReviewedRework(bool artifact)
    {
        await using var f = await Fixture.Create(artifact);
        await RunJourney(f, artifact);
    }

    [Fact]
    public async Task LegacyPascalCaseArtifactSurvivesExactQaAndDependencyAcceptance()
    {
        await using var f = await Fixture.Create(true);
        await RunJourney(f, true, legacyArtifact: true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ArtifactQaUsesSuccessfulRetryRegardlessOfAttemptLoadOrder(bool game, bool newestAttemptFirst)
    {
        await using var f = await Fixture.Create(true);
        await RunJourney(f, true, game, authorRetryHistory: true, newestAttemptFirst: newestAttemptFirst);
    }

    [Theory]
    [InlineData(WorkExecutionAttemptStatus.Pending)]
    [InlineData(WorkExecutionAttemptStatus.Running)]
    [InlineData(WorkExecutionAttemptStatus.Failed)]
    [InlineData(WorkExecutionAttemptStatus.Cancelled)]
    [InlineData(WorkExecutionAttemptStatus.Completed)]
    public async Task ArtifactQaDoesNotFallBackToAnOlderDelivery(WorkExecutionAttemptStatus latestStatus)
    {
        await using var f = await Fixture.Create(true);
        await f.Configure();
        var artifactId = Guid.NewGuid(); var revisionId = Guid.NewGuid(); const string content = "Earlier delivery";
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
        f.Db.CoreArtifacts.Add(new() { Id = artifactId, OrganizationId = f.Org, OriginWorkItemId = f.Task.Id, Title = "Earlier artifact" });
        f.Db.ArtifactRevisions.Add(new() { Id = revisionId, ArtifactId = artifactId, OrganizationId = f.Org,
            CreatedByOrganizationUserId = f.Author.Id, Content = content, ContentSha256 = digest });
        await f.Db.SaveChangesAsync();
        var item = new WorkItemExecution { WorkItem = f.Task };
        var author = new WorkStageExecution { Id = Guid.NewGuid(), StageKey = "development",
            Status = WorkStageExecutionStatus.Completed, OrganizationUserId = f.Author.Id };
        var earlier = new WorkExecutionAttempt { Id = Guid.NewGuid(), Attempt = 1, Status = WorkExecutionAttemptStatus.Completed };
        earlier.ResultJson = JsonSerializer.Serialize(new WorkExecutionOutcomeV1(author.Id, earlier.Id,
            WorkExecutionDispositions.Completed, "artifact-delivered", "Earlier delivery",
            new WorkArtifactDelivery(artifactId, revisionId, digest).ToOutput(), [], []), Json);
        var latest = new WorkExecutionAttempt { Id = Guid.NewGuid(), Attempt = 2, Status = latestStatus,
            ResultJson = JsonSerializer.Serialize(new WorkExecutionOutcomeV1(author.Id, Guid.NewGuid(),
                WorkExecutionDispositions.Blocked, "blocked", "No current delivery", JsonSerializer.SerializeToElement(new { }), [], []), Json) };
        // Put the earlier delivery last, reproducing an unordered EF navigation collection.
        author.Attempts = [latest, earlier]; item.Stages.Add(author);
        var quality = new WorkStageExecution { Id = Guid.NewGuid(), StageKey = "quality", ItemExecution = item,
            OrganizationUserId = f.Qa.Id, AgentInstallationId = Guid.NewGuid() };
        var result = new WorkArtifactQualityResult(artifactId, revisionId, digest, true, "Checked earlier delivery",
            [new("Requirement works", true, "Observed")], []);
        var outcome = new WorkExecutionOutcomeV1(quality.Id, Guid.NewGuid(), WorkExecutionDispositions.Completed,
            "passed", "Checked earlier delivery", JsonSerializer.SerializeToElement(result, Json), [], []);
        var runtime = new WorkOrchestrator(f.Db, null!, null!, null!, [], TimeProvider.System, NullLogger<WorkOrchestrator>.Instance);
        Assert.Equal("The author must deliver an exact artifact revision and digest.",
            await runtime.RecordQualityValidationAsync(new() { OrganizationId = f.Org }, quality, outcome, DateTimeOffset.UtcNow, default));
    }

    internal static async Task RunJourney(Fixture f, bool artifact, bool game = false, bool legacyArtifact = false,
        bool authorRetryHistory = false, bool newestAttemptFirst = false,
        Func<Fixture,string,Task<DeliveryBranchResult>>? publishCandidate = null,
        Func<Fixture,SourceControlPublication,DeliveryBranchResult,Task<WorkExecutionOutcomeV1>>? technicalReview = null,
        Func<Fixture,string,bool,Task<WorkExecutionOutcomeV1>>? qualityReview = null,
        bool identicalStageTimestamps = false, bool integrationConflict = false, bool oldPolicy = false, bool targetChange = false,
        int publicSprintSequence = 0, AgentInstallation? claimAs = null,
        Func<Task<CSweet.WorkManagement.Contracts.WorkSprint>>? prepareAgentSprint = null)
    {
        foreach (var person in new[] { f.Author, f.Qa, f.Technical }.Where(person => person.EmployeeType == EmployeeType.Human))
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
        var retainedPolicy = publicSprintSequence > 0 ? await f.Db.WorkOrchestrationPolicies.SingleOrDefaultAsync(p => p.BoardId == f.Board.Id) : null;
        if (retainedPolicy is null) f.Db.WorkOrchestrationPolicies.Add(new() { Id = policy.PolicyId, OrganizationId = f.Org, BoardId = f.Board.Id, Name = "Hierarchical task policy" });
        else { policy.PolicyId = retainedPolicy.Id; policy.Revision = publicSprintSequence; }
        var columns = publicSprintSequence > 0 ? await f.Db.WorkBoardColumns.Where(c => c.BoardId == f.Board.Id).ToArrayAsync() : [];
        var definitions = publicSprintSequence > 0 ? HierarchicalWorkflows.Software(
            columns.First(c => c.Category == WorkBoardColumnCategory.ToDo).Id,
            columns.First(c => c.Category == WorkBoardColumnCategory.InProgress).Id,
            columns.First(c => c.Category == WorkBoardColumnCategory.InProgress).Id,
            columns.First(c => c.Category == WorkBoardColumnCategory.InProgress).Id,
            columns.First(c => c.Category == WorkBoardColumnCategory.Done).Id)
            : HierarchicalWorkflows.Software(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        foreach (var stage in definitions.Stages)
            policy.Stages.Add(new() { Id = Guid.NewGuid(), PolicyRevisionId = policy.Id, Key = stage.Key == "development" ? authorKey : stage.Key, Name = stage.Name,
                Type = Enum.Parse<WorkOrchestrationStageType>(stage.StageType), PlatformAction = stage.PlatformAction, IsSuccessfulTerminal = stage.IsSuccessfulTerminal });
        foreach (var edge in definitions.Transitions.Where(x => !oldPolicy || x.OutcomeCode is not ("integration_conflicts" or "target_changed")))
            policy.Transitions.Add(new() { Id = Guid.NewGuid(), PolicyRevisionId = policy.Id,
                ToStageKey = edge.ToStageKey == "development" ? authorKey : edge.ToStageKey, OutcomeCode = edge.OutcomeCode, MaximumTraversals = edge.MaximumTraversals,
                FromStageKey = edge.FromStageKey == "development" ? authorKey : edge.FromStageKey });
        f.Db.WorkOrchestrationPolicyRevisions.Add(policy);
        var plan = await f.Configure(publicSprintSequence == 0 ? null : f.Request() with { IdempotencyKey = "configure-increment-" + publicSprintSequence });
        if (publicSprintSequence == 0) await f.Activate(plan);
        else await f.Service.ControlAsync(f.Org, f.Manager.Id, new(plan.Id, plan.Revision, "activate", "activate-increment-" + publicSprintSequence));
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
        if (publicSprintSequence == 0)
        {
            var authorStage = Initial(); item.Stages.Add(authorStage); f.Db.WorkStageExecutions.Add(authorStage);
            await f.Db.SaveChangesAsync();
        }
        else
        {
            // This acceptance path starts through the same public readiness service used by the broker.
            f.Db.Entry(execution).State = EntityState.Detached; f.Db.Entry(sprint).State = EntityState.Detached;
            foreach (var entry in f.Db.ChangeTracker.Entries<WorkItemExecution>().Where(e => e.Entity.Id == item.Id).ToArray()) entry.State = EntityState.Detached;
            f.Manager.ApplicationUserId ??= Guid.NewGuid();
            if (!await f.Db.Users.AnyAsync(u => u.Id == f.Manager.ApplicationUserId)) f.Db.Users.Add(new CSweet.Infrastructure.Auth.ApplicationUser { Id = f.Manager.ApplicationUserId.Value, UserName = f.Manager.Id.ToString("N") });
            foreach (var action in CSweet.Contracts.WorkManagement.WorkOrchestrationActions.All.Concat(CSweet.Contracts.WorkManagement.WorkSprintActions.All).Append(CSweet.Contracts.WorkManagement.WorkBoardActions.Read))
                if (!await f.Db.ScopedActionGrants.AnyAsync(g => g.SubjectId == f.Manager.Id && g.Action == action && g.ScopeId == f.Board.Id))
                    f.Db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, SubjectKind = CSweet.Domain.Security.GrantSubjectKind.OrganizationUser,
                        SubjectId = f.Manager.Id, Action = action, ScopeKind = CSweet.Domain.Security.GrantScopeKind.Board, ScopeId = f.Board.Id });
            (await f.Db.WorkOrchestrationPolicies.SingleAsync(p => p.Id == policy.PolicyId)).PublishedRevisionId = policy.Id;
            f.Task.Status = WorkTaskStatus.Ready;
            await f.Db.SaveChangesAsync();
            (Guid Id, long Revision) created;
            if (prepareAgentSprint is not null)
            {
                var planned = await prepareAgentSprint();
                created = (planned.Id, planned.Revision);
            }
            else
            {
                var sprints = new WorkSprintService(f.Db, new CSweet.Infrastructure.Security.ScopedActionAuthorizationService(f.Db), new TestAuditEventWriter());
                var planned = await sprints.CreateAsync(f.Org, f.Board.Id, f.Manager.ApplicationUserId.Value,
                    new("Sprint " + publicSprintSequence, "Deliver the selected approved work", null, null, "journey-sprint-" + publicSprintSequence));
                await sprints.SetItemSprintAsync(f.Org, f.Board.Id, f.Task.Id, f.Manager.ApplicationUserId.Value,
                    new(planned.Id, f.Task.Revision, "journey-scope-" + publicSprintSequence));
                created = (planned.Id, planned.Revision);
            }
            var starter = new WorkOrchestrationService(f.Db, TimeProvider.System, new CSweet.Infrastructure.Security.ScopedActionAuthorizationService(f.Db), f.Client);
            var currentManager = await f.Db.CoreOrganizationUsers.SingleAsync(u => u.Id == f.Board.ManagerOrganizationUserId);
            var startPrincipal = currentManager.AgentInstallationId ?? currentManager.ApplicationUserId ?? throw new InvalidOperationException("The current manager needs an authenticated principal.");
            var preflight = await starter.PreflightAsync(f.Org, f.Board.Id, created.Id, startPrincipal);
            Assert.True(preflight.IsValid, string.Join("; ", preflight.Errors));
            var started = await starter.StartAsync(f.Org, f.Board.Id, created.Id, startPrincipal, new(created.Revision, "journey-start-" + publicSprintSequence));
            var replay = await starter.StartAsync(f.Org, f.Board.Id, created.Id, startPrincipal, new(created.Revision, "journey-start-" + publicSprintSequence));
            Assert.Equal(started.Id, replay.Id);
            sprint = await f.Db.WorkSprints.SingleAsync(s => s.Id == created.Id);
            execution = await f.Db.WorkSprintExecutions.Include(e => e.Items).ThenInclude(i => i.Stages).SingleAsync(e => e.Id == started.Id);
            item = execution.Items.Single();
            Assert.Equal(WorkSprintStatus.Active, sprint.Status); Assert.Single(execution.Items);
        }
        TimeProvider clock = identicalStageTimestamps ? new FrozenClock(DateTimeOffset.UtcNow.AddMinutes(1)) : TimeProvider.System;
        var manual = new WorkOrchestrationService(f.Db, clock);
        var inbox = new AgentWorkInbox(f.Db, new EphemeralDataProtectionProvider(), clock);
        var runtime = new WorkOrchestrator(f.Db, inbox, null!, claimAs is null ? null! : new WorkDispatchRecoveryTests.Runtime(f.Db),
            [new TaskIntegrationWorkActionExecutor(f.Db, f.Client, clock)], clock, NullLogger<WorkOrchestrator>.Instance);
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
        if (claimAs is not null)
        {
            var process = new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(), AgentInstallationId = claimAs.Id,
                QueuedAt = clock.GetUtcNow(), RuntimeDeadlineAt = clock.GetUtcNow().AddMinutes(10) };
            f.Db.AgentRuntimeInstances.Add(process);
            await f.Db.SaveChangesAsync();
            await runtime.PulseAsync();
            var stage = Current();
            Assert.Equal(authorKey, stage.StageKey);
            var lease = await inbox.ClaimAsync(new McpAgentSession { Id = Guid.NewGuid(), RuntimeInstanceId = process.Id, TickId = process.TickId,
                OrganizationId = f.Org.ToString(), AgentInstallationId = claimAs.Id }, default);
            Assert.NotNull(lease);
            Assert.Equal(lease.WorkId, Assert.Single(stage.Attempts).AgentWorkItemId);
            Assert.Equal(f.Author.Id, stage.OrganizationUserId);
            Assert.Equal(claimAs.Id, stage.AgentInstallationId);
            Assert.Equal(WorkStageExecutionStatus.Running, stage.Status);
            Assert.Equal(f.Task.Id, item.WorkItemId);
            Assert.Equal(execution.Id, item.SprintExecutionId);
            Assert.Equal(sprint.Id, execution.SprintId);
            Assert.Equal(WorkSprintStatus.Active, sprint.Status);
            Assert.Equal(1, sprint.Sequence);
            Assert.True(f.Task.EstimatePoints > 0 && f.Task.EstimatePoints <= sprint.CapacityPoints);
            var receipt = await f.Db.AgentWorkAttempts.SingleAsync(a => a.AgentWorkItemId == lease.WorkId);
            Assert.True(receipt.ClaimedAt >= sprint.StartedAt);
            return;
        }
        if (publicSprintSequence > 0) {
            await Pulse(); Assert.Equal(authorKey, item.CurrentStageKey);
            Assert.Equal(WorkStageExecutionStatus.WaitingForHuman, Current().Status);
        }
        var conflictReworks = 0;
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
                authorOutput = legacyArtifact
                    ? new { ArtifactId = artifactId, RevisionId = revisionId, Sha256 = digest }
                    : new WorkArtifactDelivery(artifactId, revisionId, digest);
            }
            else
            {
                var branch = "csweet/task/one/r" + cycle + "-" + f.Task.AssignmentRevision;
                var refs = publishCandidate is not null ? await publishCandidate(f,branch) :
                    await f.Client.DeliveryBranchAsync(new(f.Org, f.Repositories[0], "InternalGit", null, "", "", "inspect", branch, "csweet/story/one", "inspect"));
                var workspace = new SourceControlWorkspace { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkItemId = f.Task.Id,
                    WorkspaceKey = "task:" + f.Task.Id.ToString("N") + ":" + cycle,
                    AssignmentRevision = f.Task.AssignmentRevision, RepositoryId = f.Repositories[0], BranchName = branch, IntegrationTargetBranch = "csweet/story/one", BaseCommitSha = refs.TargetCommitSha };
                publication = new() { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkspaceId = workspace.Id, RepositoryId = f.Repositories[0],
                    CommitSha = refs.SourceCommitSha, TicketBranch = branch, TargetBranch = "csweet/story/one", CreatedAt = DateTimeOffset.UtcNow };
                f.Db.AddRange(workspace, publication); authorOutput = new { commitSha = publication.CommitSha };
            }
            await f.Db.SaveChangesAsync();
            await Complete(f.Author, artifact ? "artifact-delivered" : "completed", authorOutput);
            if (authorRetryHistory)
            {
                var producingStage = item.Stages.Single(x => x.StageKey == authorKey && x.Traversal == item.Traversal);
                var successful = producingStage.Attempts.Single();
                successful.Attempt = 2;
                // A blocked outcome is still a completed delivery of an execution result.
                var blocked = new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecutionId = producingStage.Id,
                    Attempt = 1, Status = WorkExecutionAttemptStatus.Completed, IdempotencyKey = Guid.NewGuid().ToString("N") };
                blocked.ResultJson = JsonSerializer.Serialize(new WorkExecutionOutcomeV1(producingStage.Id, blocked.Id,
                    WorkExecutionDispositions.Blocked, "blocked", "The MCP service rejected the request: Bad Gateway",
                    JsonSerializer.SerializeToElement(new { }), [], []), Json);
                f.Db.WorkExecutionAttempts.Add(blocked);
                producingStage.Attempts.Clear();
                foreach (var attempt in newestAttemptFirst ? new[] { successful, blocked } : new[] { blocked, successful })
                    producingStage.Attempts.Add(attempt);
                await f.Db.SaveChangesAsync();
            }
            if (!artifact)
            {
                Assert.Equal("technical-review", item.CurrentStageKey);
                var refs = await f.Client.DeliveryBranchAsync(new(f.Org, f.Repositories[0], "InternalGit", null, "", "", "inspect", publication!.TicketBranch, publication.TargetBranch, "inspect"));
                if (technicalReview is not null)
                {
                    var reviewed=await technicalReview(f,publication,refs);
                    Assert.Equal("approved",reviewed.OutcomeCode);
                    await Complete(f.Technical,reviewed.OutcomeCode,reviewed.Output,reviewed.Evidence);
                }
                else await Complete(f.Technical, "approved", new { }, [new("commit", "Reviewed source", publication.CommitSha), new("target-commit", "Story target", refs.TargetCommitSha)]);
                Assert.Null(cycle == 0 ? f.Task.MergeCommitSha : null);
                var assignmentBeforeIntegration = f.Task.AssignmentRevision;
                if (targetChange && cycle == 0) f.Host.AdvanceStory(publication.RepositoryId, publication.TargetBranch);
                await Pulse();
                if (targetChange && cycle == 0)
                {
                    Assert.Equal("technical-review", item.CurrentStageKey); Assert.Null(f.Task.MergeCommitSha);
                    Assert.Equal(assignmentBeforeIntegration, f.Task.AssignmentRevision);
                    Assert.Empty(await f.Db.WorkTaskIntegrationReceipts.ToListAsync());
                    await Complete(f.Technical, "approved", new { },
                        [new("commit", "Source", publication.CommitSha), new("target-commit", "Old Story", refs.TargetCommitSha)]);
                    await Pulse();
                    Assert.Equal("technical-review", item.CurrentStageKey); Assert.Null(f.Task.MergeCommitSha);
                    Assert.Empty(await f.Db.WorkTaskIntegrationReceipts.ToListAsync());
                    refs = await f.Client.DeliveryBranchAsync(new(f.Org, publication.RepositoryId, "InternalGit", null, "", "", "inspect",
                        publication.TicketBranch, publication.TargetBranch, "current-story"));
                    await Complete(f.Technical, "approved", new { },
                        [new("commit", "Source", publication.CommitSha), new("target-commit", "Current Story", refs.TargetCommitSha)]);
                    await Pulse();
                }
                if (integrationConflict && conflictReworks == 0)
                {
                    Assert.Equal(authorKey, item.CurrentStageKey); Assert.Equal(1, item.Traversal);
                    Assert.Equal(assignmentBeforeIntegration + 1, f.Task.AssignmentRevision);
                    Assert.Null(f.Task.MergeCommitSha); Assert.Empty(await f.Db.WorkTaskIntegrationReceipts.ToListAsync());
                    var conflict = Assert.Single(item.Stages.Where(x => x.LastOutcomeCode == "integration_conflicts"));
                    Assert.Equal(WorkStageExecutionStatus.Completed, conflict.Status);
                    Assert.Contains("conflict markers", conflict.LastSummary);
                    var comment = Assert.Single(await f.Db.WorkItemComments.Where(x => x.AuthorDisplayName == "C-Sweet integration").ToListAsync());
                    Assert.Equal(CSweet.Domain.Security.GrantSubjectKind.AutomationIdentity, comment.AuthorKind);
                    Assert.Contains("fresh technical review", comment.Body);
                    conflictReworks++; cycle--; continue;
                }
                Assert.Equal("quality", item.CurrentStageKey); Assert.NotNull(f.Task.MergeCommitSha);
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
                var agentQuality = new WorkStageExecution { StageKey = "quality", ItemExecution = item,
                    OrganizationUserId = f.Qa.Id, AgentInstallationId = Guid.NewGuid() };
                var agentOutcome = new WorkExecutionOutcomeV1(Current().Id, Guid.NewGuid(), WorkExecutionDispositions.Completed,
                    passed ? "passed" : "failed", exact.Summary, JsonSerializer.SerializeToElement(exact, Json), [], []);
                Assert.Null(await runtime.RecordQualityValidationAsync(execution, agentQuality, agentOutcome, DateTimeOffset.UtcNow, default));
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
                WorkExecutionOutcomeV1? agentQualityOutcome = null;
                if (qualityReview is not null)
                {
                    var reviewed=await qualityReview(f,sha,passed);
                    Assert.Equal(passed?"passed":"failed",reviewed.OutcomeCode);
                    Assert.Equal(sha,Assert.Single(reviewed.Evidence).Value);
                    quality=reviewed.Output.Deserialize<WorkDeliveryReviewResult>(Json)!;
                    agentQualityOutcome=reviewed;
                }
                var claimedAgentStage = new WorkStageExecution { StageKey = "quality", ItemExecution = item,
                    OrganizationUserId = f.Qa.Id, AgentInstallationId = Guid.NewGuid() };
                if (agentQualityOutcome is not null)
                    Assert.Null(await runtime.RecordQualityValidationAsync(execution,claimedAgentStage,agentQualityOutcome,DateTimeOffset.UtcNow,default));
                var omittedCriteria = new WorkExecutionOutcomeV1(claimedAgentStage.Id, Guid.NewGuid(), WorkExecutionDispositions.Completed,
                    passed ? "passed" : "failed", "Omitted requirement evidence", JsonSerializer.SerializeToElement(quality with { Criteria = [] }, Json),
                    [new("commit", "Exact integrated story", sha)], []);
                Assert.Contains("every criterion", await runtime.RecordQualityValidationAsync(execution, claimedAgentStage, omittedCriteria, DateTimeOffset.UtcNow, default));
                await Assert.ThrowsAsync<InvalidOperationException>(() => Complete(f.Qa, passed ? "passed" : "failed", quality, [new("commit", "Wrong revision", publication!.CommitSha)]));
                await Assert.ThrowsAsync<InvalidOperationException>(() => Complete(f.Qa, passed ? "passed" : "failed", quality with
                    { Validations = [new(f.Repositories[0], sha, "test", passed ? 0 : 1, passed, "")] }, [new("commit", "Integrated story", sha)]));
                await Complete(f.Qa, passed ? "passed" : "failed", quality, [new("commit", "Integrated story", sha)]);
            }
            if (!passed) { Assert.Equal(authorKey, item.CurrentStageKey); Assert.Equal(1 + conflictReworks + (targetChange ? 2 : 0), item.Traversal); }
        }
        await Pulse();
        Assert.Equal(WorkTaskStatus.Completed, f.Task.Status);
        Assert.Equal(WorkSprintStatus.Completed, sprint.Status);
        Assert.NotEqual(WorkTaskStatus.Completed, f.Epic.Status);
        Assert.Equal(artifact ? 0 : 2, await f.Db.WorkTaskIntegrationReceipts.CountAsync(x => x.Status == "Completed"));
        await f.Service.PulseAsync();
        Assert.Equal("WaitingForHuman", (await f.Read()).Executions.Single(x => x.Scope == "Story").Status);
    }
    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
