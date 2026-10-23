using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class LinkedDeliveryDefectTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("valid")]
    [InlineData("stale")]
    [InlineData("stale-qa")]
    [InlineData("unauthorized")]
    [InlineData("active")]
    [InlineData("candidate")]
    [InlineData("receipt")]
    [InlineData("planning")]
    public async Task Linked_bug_preserves_completed_work_and_gates_fresh_qa(string condition)
    {
        await using var f = new HierarchicalDeliveryServiceTests.Fixture(false,
            new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        await f.Seed(); await f.Activate(await f.Configure()); await f.CompleteTasks();
        var db = f.Db;
        f.Manager.ApplicationUserId = f.Manager.Id;
        var plan = await db.WorkDeliveryPlans.SingleAsync();
        var project = await db.Workstreams.SingleAsync();
        project.ProfileKey = "defect-test"; project.ProfileVersion = 1; project.ProfileDefinitionDigest = "pinned";
        db.WorkstreamProfileDefinitions.Add(new() { Id = Guid.NewGuid(), Key = project.ProfileKey, Version = 1,
            DefinitionDigest = "pinned", DefaultBoardProfileKey = f.Board.ProfileKey,
            DefinitionJson = JsonSerializer.Serialize(new { key = "defect-test", workItemTypes = new[] {
                new { key = "game.bug.v1", kind = "Bug", displayName = "Game bug", executionMode = "Executable",
                    permittedParentTypeKeys = new[] { f.Story.TypeKey }, requiredApprovalPolicyKeys = Array.Empty<string>() } } }) });
        db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, SubjectKind = GrantSubjectKind.OrganizationUser,
            SubjectId = f.Manager.Id, ScopeKind = GrantScopeKind.Board, ScopeId = f.Board.Id, Action = WorkItemActions.Create });
        var policy = new WorkOrchestrationPolicyRevision { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = f.Board.Id };
        policy.Stages.Add(new() { Id = Guid.NewGuid(), Key = "development", Type = WorkOrchestrationStageType.MemberExecution });
        var sprint = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = f.Board.Id,
            SprintId = Guid.NewGuid(), PolicyRevisionId = policy.Id, Status = WorkSprintExecutionStatus.Active };
        var originalExecution = await db.WorkItemExecutions.Include(x => x.Stages).SingleAsync();
        originalExecution.SprintExecutionId = sprint.Id;
        f.Task.SprintId = sprint.SprintId;
        f.Task.MergeStatus = "Merged"; f.Task.MergeCommitSha = new string('a', 40);
        var originalReceipt = new WorkTaskIntegrationReceipt { Id = Guid.NewGuid(), OrganizationId = f.Org,
            PlanId = plan.Id, WorkItemId = f.Task.Id, ItemExecutionId = originalExecution.Id,
            Status = "Completed", CandidateCommitSha = f.Task.MergeCommitSha };
        db.WorkTaskIntegrationReceipts.Add(originalReceipt);
        var qa = new WorkTask { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = f.Board.Id, ParentWorkTaskId = f.Story.Id,
            SprintId = sprint.SprintId, Title = "Independent foundation QA", Status = WorkTaskStatus.Blocked, PlanningRevision = 1,
            PlanningSpecificationJson = JsonSerializer.Serialize(new Shared.WorkItemPlanningSpecification(["Observe Chromium"], ["Foundation works"])
                { DeliveryKind = "Artifact", DependencyItemIds = [f.Task.Id] }, Json),
            DeliverySpecificationJson = JsonSerializer.Serialize(new Shared.WorkItemDeliverySpecification(Guid.Empty, ["Observe Chromium"], ["Foundation works"])
                { DeliveryKind = "Artifact", DeliveryPlanId = plan.Id, DependencyItemIds = [f.Task.Id] }, Json) };
        var sourceAssignments = await db.WorkItemStageAssignments.Where(x => x.WorkItemId == f.Task.Id).ToListAsync();
        qa.StageAssignments = sourceAssignments.Select(x => new WorkItemStageAssignment { Id = Guid.NewGuid(), OrganizationId = f.Org,
            BoardId = f.Board.Id, WorkItemId = qa.Id, StageKey = x.StageKey, PrincipalKind = x.PrincipalKind, OrganizationUserId = x.OrganizationUserId }).ToList();
        var qaExecution = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = qa.Id, WorkItem = qa,
            SprintExecutionId = sprint.Id, Status = WorkItemExecutionStatus.Blocked, CurrentStageKey = "development" };
        qaExecution.Stages.Add(new() { Id = Guid.NewGuid(), StageKey = "development", Status = WorkStageExecutionStatus.Blocked });
        db.AddRange(policy, sprint, qa, qaExecution);
        db.WorkItemDependencies.Add(new() { WorkItemId = qa.Id, DependsOnWorkItemId = f.Task.Id });
        sprint.AssignmentSnapshotJson = JsonSerializer.Serialize(sourceAssignments.Select(x => new {
            workItemId = f.Task.Id, stageKey = x.StageKey, principalKind = x.PrincipalKind, organizationUserId = x.OrganizationUserId,
            agentInstallationId = x.AgentInstallationId, platformAction = x.PlatformAction, requirementsJson = x.RequirementsJson, selectionEvidenceJson = x.SelectionEvidenceJson }), Json);
        var scopes = WorkDeliveryService.Decode<Shared.WorkDeliveryScopeSnapshot[]>(plan.ScopesJson);
        var declared = scopes.Single(x => x.Scope == "Story");
        var workflow = declared.TicketWorkflow! with { EvidenceItemIds = declared.ChildIds.Append(qa.Id).ToArray() };
        var ticket = await db.CoreWorkTasks.SingleAsync(x => x.Id == workflow.TicketId);
        var planning = WorkDeliveryService.Decode<Shared.WorkItemPlanningSpecification>(ticket.PlanningSpecificationJson!);
        planning = planning with { Workflow = workflow.Definition() };
        ticket.PlanningSpecificationJson = WorkDeliveryService.Encode(planning);
        plan.ScopesJson = WorkDeliveryService.Encode(scopes.Select(x => x.Scope != "Story" ? x : x with {
            ChildIds = workflow.EvidenceItemIds, TicketWorkflow = workflow, Planning = planning,
            ChildPlanningRevisions = x.ChildPlanningRevisions.Append(new KeyValuePair<Guid,long>(qa.Id, 1)).ToDictionary(p => p.Key, p => p.Value),
            ChildDeliveryDigests = x.ChildDeliveryDigests.Append(new KeyValuePair<Guid,string>(qa.Id,
                WorkDeliveryService.TaskDeliveryDigest(qa, qa.StageAssignments.ToList()))).ToDictionary(p => p.Key, p => p.Value) }));
        switch (condition)
        {
            case "unauthorized": f.Board.ManagerOrganizationUserId = f.Author.Id; break;
            case "active": qaExecution.Status = WorkItemExecutionStatus.Running; break;
            case "candidate": (await db.WorkDeliveryExecutions.FirstAsync()).Status = "Running"; break;
            case "receipt": originalReceipt.CandidateCommitSha = "wrong"; break;
            case "planning": f.Task.PlanningRevision++; break;
        }
        await db.SaveChangesAsync();
        var originalRevision = f.Task.Revision; var originalAssignment = f.Task.AssignmentRevision;
        var originalScope = plan.ScopeRevision;
        var service = new WorkOrchestrationService(db, TimeProvider.System, new ScopedActionAuthorizationService(db), f.Client);
        var request = new CreateLinkedDefectRequest(condition == "stale" ? -1 : f.Task.Revision, qa.Id,
            condition == "stale-qa" ? -1 : qa.Revision, "linked-defect", "Block portrait pointer leakage", "Trusted portrait click reached Menu and fired on landscape resume. Fix and verify in Chromium.");
        if (condition != "valid")
        {
            await Assert.ThrowsAnyAsync<Exception>(() => service.CreateLinkedDefectAsync(f.Org, f.Board.Id, f.Task.Id, f.Manager.Id, request));
            Assert.Empty(await db.CoreWorkTasks.Where(x => x.Kind == WorkItemKind.Bug).ToListAsync()); return;
        }
        var id = await service.CreateLinkedDefectAsync(f.Org, f.Board.Id, f.Task.Id, f.Manager.Id, request);
        Assert.Equal(id, await service.CreateLinkedDefectAsync(f.Org, f.Board.Id, f.Task.Id, f.Manager.Id, request));
        var bug = await db.CoreWorkTasks.SingleAsync(x => x.Id == id);
        Assert.Equal(WorkItemKind.Bug, bug.Kind); Assert.Equal("game.bug.v1", bug.TypeKey);
        Assert.Equal(WorkTaskStatus.Completed, f.Task.Status); Assert.Equal(originalRevision, f.Task.Revision);
        Assert.Equal(originalAssignment, f.Task.AssignmentRevision); Assert.Equal(originalScope + 1, plan.ScopeRevision);
        Assert.Equal(2, qa.PlanningRevision); Assert.Equal(WorkTaskStatus.Blocked, qa.Status);
        Assert.Contains(bug.Identifier!, qa.BlockReason);
        Assert.True(await db.WorkItemDependencies.AnyAsync(x => x.WorkItemId == qa.Id && x.DependsOnWorkItemId == bug.Id));
        Assert.True(await db.WorkItemDependencies.AnyAsync(x => x.WorkItemId == bug.Id && x.DependsOnWorkItemId == f.Task.Id));
        Assert.Single(await db.WorkTaskIntegrationReceipts.ToListAsync());
        await f.Service.ValidateCurrentScopeAsync(plan, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateLinkedDefectAsync(f.Org, f.Board.Id, f.Task.Id, f.Manager.Id, request with { Description = "different" }));
    }
}
