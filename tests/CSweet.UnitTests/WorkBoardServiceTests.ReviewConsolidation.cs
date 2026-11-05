using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class WorkBoardServiceTests
{
    [Theory]
    [InlineData("valid")] [InlineData("stale")] [InlineData("criteria")] [InlineData("running-dependent")]
    [InlineData("started-review")] [InlineData("missing-stage")] [InlineData("cycle")]
    [InlineData("aggregate-started")] [InlineData("not-manager")]
    public async Task Consolidation_preserves_scope_and_native_quality_while_removing_both_visible_and_hidden_duplicates(string scenario)
    {
        await using var db = CreateDb();
        var owner = SeedOrganization(db, scenario == "not-manager" ? OrganizationPermissionLevel.Contributor : OrganizationPermissionLevel.Owner);
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestAuditEventWriter());
        if (scenario == "not-manager")
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ConsolidateReviewsAsync(owner.OrganizationId, Guid.NewGuid(), owner.ApplicationUserId,
                new(Guid.NewGuid(), 1, [], "test", "test"))); return;
        }
        var board = Assert.Single((await service.ListDirectoryAsync(owner.OrganizationId, owner.ApplicationUserId, new())).Boards);
        var column = await db.WorkBoardColumns.Where(x => x.BoardId == board.Id && x.Category==WorkBoardColumnCategory.ToDo).OrderBy(x=>x.Position).Select(x => x.Id).FirstAsync();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        WorkTask Task(string name, string criterion) => new() { Id = Guid.NewGuid(), OrganizationId = owner.OrganizationId,
            BoardId = board.Id, BoardColumnId = column, Identifier = name, Title = name, Status = WorkTaskStatus.Ready,
            Revision = 1, PlanningRevision = 2, AssignmentRevision = 7,
            PlanningSpecificationJson = JsonSerializer.Serialize(new Shared.WorkItemPlanningSpecification([name], [criterion]), json) };
        var canonical = Task("GAME-1", "Render the scene"); canonical.Status = WorkTaskStatus.Failed;
        var review = Task("GAME-2", "Independently observe the scene");
        var dependent = Task("GAME-3", "Add gameplay");
        if (scenario == "running-dependent") dependent.Status = WorkTaskStatus.Running;
        dependent.Dependencies.Add(new() { WorkItemId = dependent.Id, DependsOnWorkItemId = review.Id });
        dependent.PlanningSpecificationJson = JsonSerializer.Serialize(new Shared.WorkItemPlanningSpecification(["Add gameplay"], ["Add gameplay"]) { DependencyItemIds = [review.Id] }, json);
        dependent.DeliverySpecificationJson = JsonSerializer.Serialize(JsonSerializer.Deserialize<Shared.WorkItemDeliverySpecification>("{}")! with { DependencyItemIds = [review.Id] }, json);
        if (scenario == "cycle") canonical.Dependencies.Add(new() { WorkItemId = canonical.Id, DependsOnWorkItemId = review.Id });
        var workflowTicket = Task("GAME-4", "Prepare the Story source");
        workflowTicket.Dependencies.Add(new() { WorkItemId = workflowTicket.Id, DependsOnWorkItemId = canonical.Id });
        workflowTicket.Dependencies.Add(new() { WorkItemId = workflowTicket.Id, DependsOnWorkItemId = review.Id });
        var workflow = new Shared.WorkDeliveryTicketWorkflow(workflowTicket.Id, []) { EvidenceItemIds = [canonical.Id, review.Id] };
        var workflowPlanning = new Shared.WorkItemPlanningSpecification(["Prepare"], ["Prepare"]) {
            Workflow = workflow.Definition(), DependencyItemIds = [canonical.Id, review.Id] };
        workflowTicket.PlanningSpecificationJson = JsonSerializer.Serialize(workflowPlanning, json);
        var plan = new WorkDeliveryPlan { Id = Guid.NewGuid(), OrganizationId = owner.OrganizationId,
            WorkstreamId = Guid.Empty, Status = "Active", ScopeRevision = 1 };
        var scopeItem = Guid.NewGuid();
        var snapshot = new Shared.WorkDeliveryScopeSnapshot("Story", scopeItem, board.Id, 2, [canonical.Id, review.Id], workflowPlanning, []) {
            TicketWorkflow = workflow, TicketPlanningRevision = workflowTicket.PlanningRevision, PredecessorItemIds = [canonical.Id, review.Id],
            ChildPlanningRevisions = new Dictionary<Guid,long> { [canonical.Id] = 2, [review.Id] = 2 },
            ChildDeliveryDigests = new Dictionary<Guid,string> { [canonical.Id] = "unchanged-code-pin", [review.Id] = "review-pin" } };
        var parentTicket=Task("GAME-5","Prepare the release");
        var parentWorkflow=new Shared.WorkDeliveryTicketWorkflow(parentTicket.Id,[]) { EvidenceItemIds=[workflowTicket.Id] };
        var parentPlanning=new Shared.WorkItemPlanningSpecification(["Prepare release"],["Prepare release"]) { Workflow=parentWorkflow.Definition(),DependencyItemIds=[workflowTicket.Id] };
        parentTicket.PlanningSpecificationJson=JsonSerializer.Serialize(parentPlanning,json);
        var parentSnapshot=new Shared.WorkDeliveryScopeSnapshot("Release",null,board.Id,2,[workflowTicket.Id],parentPlanning,[]) {
            TicketWorkflow=parentWorkflow,TicketPlanningRevision=parentTicket.PlanningRevision,PredecessorItemIds=[workflowTicket.Id],
            ChildPlanningRevisions=new Dictionary<Guid,long>{[workflowTicket.Id]=workflowTicket.PlanningRevision} };
        plan.ScopesJson = JsonSerializer.Serialize(new[] { snapshot,parentSnapshot }, json);
        plan.Executions.Add(new() { Id = Guid.NewGuid(), Scope = "Story", WorkItemId = scopeItem,
            BoardId = board.Id, ScopeRevision=1, Status = scenario == "aggregate-started" ? "Running" : "WaitingForChildren" });
        canonical.DeliverySpecificationJson = JsonSerializer.Serialize(JsonSerializer.Deserialize<Shared.WorkItemDeliverySpecification>("{}")! with { DeliveryPlanId = plan.Id }, json);
        var sprint = new WorkSprintExecution { Id = Guid.NewGuid(), BoardId = board.Id, OrganizationId = owner.OrganizationId, Status = WorkSprintExecutionStatus.Active };
        var canonicalItem = new WorkItemExecution { Id = Guid.NewGuid(), WorkItem = canonical, CurrentStageKey = "quality", Status = WorkItemExecutionStatus.Failed };
        var qa = new WorkStageExecution { Id = Guid.NewGuid(), StageKey = scenario == "missing-stage" ? "other" : "quality", Status = WorkStageExecutionStatus.Failed };
        canonicalItem.Stages.Add(qa); sprint.Items.Add(canonicalItem);
        var reviewItem = new WorkItemExecution { Id = Guid.NewGuid(), WorkItem = review, CurrentStageKey = "specialist-execution" };
        var pending = new WorkStageExecution { Id = Guid.NewGuid(), StageKey = "specialist-execution", Status = WorkStageExecutionStatus.Pending };
        if (scenario == "started-review") pending.Attempts.Add(new() { Id = Guid.NewGuid(), Attempt = 1 });
        reviewItem.Stages.Add(pending); sprint.Items.Add(reviewItem);
        db.AddRange(canonical, review, dependent, workflowTicket, parentTicket, plan, sprint);
        // A projectless fixture still needs exact project equality in the plan guard.
        (await db.WorkBoards.SingleAsync(x => x.Id == board.Id)).WorkstreamId = Guid.Empty;
        await db.SaveChangesAsync();
        var request = new ConsolidateWorkReviewsRequest(canonical.Id, scenario == "stale" ? 99 : canonical.Revision,
            [new(review.Id, review.Revision, "quality", [scenario == "criteria" ? "Different criterion" : "Independently observe the scene"])],
            "Reuse native exact-source QA, preserve the full scope.", "same-review-once");
        if (scenario != "valid")
        {
            await Assert.ThrowsAnyAsync<Exception>(() => service.ConsolidateReviewsAsync(owner.OrganizationId, board.Id, owner.ApplicationUserId, request));
            db.ChangeTracker.Clear();
            Assert.Equal(WorkTaskStatus.Ready, (await db.CoreWorkTasks.SingleAsync(x => x.Id == review.Id)).Status);
            Assert.Empty(await db.WorkOrchestrationEvents.ToListAsync()); return;
        }
        var result = await service.ConsolidateReviewsAsync(owner.OrganizationId, board.Id, owner.ApplicationUserId, request);
        Assert.Contains(review.Id, result.CancelledReviewIds); Assert.Equal(WorkTaskStatus.Cancelled, review.Status);
        Assert.Equal(WorkBoardColumnCategory.Done,(await db.WorkBoardColumns.SingleAsync(x=>x.Id==review.BoardColumnId)).Category);
        Assert.Equal(WorkItemExecutionStatus.Cancelled, reviewItem.Status);
        Assert.Equal(WorkStageExecutionStatus.Failed, qa.Status); Assert.Equal(7, canonical.AssignmentRevision);
        Assert.Equal(canonical.Id, Assert.Single(dependent.Dependencies).DependsOnWorkItemId);
        Assert.Equal(canonical.Id, Assert.Single(JsonSerializer.Deserialize<Shared.WorkItemDeliverySpecification>(dependent.DeliverySpecificationJson!, json)!.DependencyItemIds));
        var finalScopes=JsonSerializer.Deserialize<Shared.WorkDeliveryScopeSnapshot[]>(plan.ScopesJson,json)!;
        var updated = finalScopes.Single(x=>x.Scope=="Story");
        Assert.Equal(workflowTicket.PlanningRevision,finalScopes.Single(x=>x.Scope=="Release").ChildPlanningRevisions[workflowTicket.Id]);
        Assert.Equal(canonical.Id, Assert.Single(updated.ChildIds)); Assert.Equal(canonical.Id, Assert.Single(updated.PredecessorItemIds));
        Assert.Equal(canonical.Id, Assert.Single(updated.TicketWorkflow!.EvidenceItemIds));
        Assert.Equal("unchanged-code-pin", updated.ChildDeliveryDigests[canonical.Id]);
        Assert.Equal(1, plan.ScopeRevision); Assert.Equal(workflowTicket.PlanningRevision, updated.TicketPlanningRevision);
        Assert.Equal("Prepare", Assert.Single(updated.Planning.AcceptanceCriteria));
        Assert.NotEmpty(await db.ApplicationRealtimeOutbox.ToListAsync());
        await service.ConsolidateReviewsAsync(owner.OrganizationId, board.Id, owner.ApplicationUserId, request);
        Assert.Single(await db.WorkOrchestrationEvents.Where(x => x.EventType == "reviews.consolidated").ToListAsync());
        review.BoardColumnId=column;await db.SaveChangesAsync();
        await service.ConsolidateReviewsAsync(owner.OrganizationId,board.Id,owner.ApplicationUserId,request);
        Assert.Equal(WorkBoardColumnCategory.Done,(await db.WorkBoardColumns.SingleAsync(x=>x.Id==review.BoardColumnId)).Category);
        Assert.Single(await db.WorkOrchestrationEvents.Where(x=>x.EventType=="reviews.consolidated.columns-reconciled").ToListAsync());
        // A replay repairs only this older implementation's derived nested pin, with
        // unchanged criteria and no started candidate; it does not reopen QA.
        finalScopes[1]=finalScopes[1] with {ChildPlanningRevisions=new Dictionary<Guid,long>{[workflowTicket.Id]=workflowTicket.PlanningRevision-1}};
        plan.ScopesJson=JsonSerializer.Serialize(finalScopes,json);
        var aggregate=plan.Executions.Single();aggregate.Status="Blocked";
        aggregate.BlockedReason="Declared evidence planning changed or was cancelled; reconcile the workflow ticket.";
        await db.SaveChangesAsync();
        await service.ConsolidateReviewsAsync(owner.OrganizationId,board.Id,owner.ApplicationUserId,request);
        Assert.Equal("WaitingForChildren",aggregate.Status);Assert.Null(aggregate.BlockedReason);
        Assert.Equal(WorkStageExecutionStatus.Failed,qa.Status);
        Assert.Single(await db.WorkOrchestrationEvents.Where(x=>x.EventType=="reviews.consolidated.references-reconciled").ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConsolidateReviewsAsync(owner.OrganizationId, board.Id, owner.ApplicationUserId,
            request with { Reason = "Changed replay" }));
    }
}
