using System.Data;
using CSweet.Contracts.SourceControl;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkDeliveryService
{
    public async Task<WorkDeliveryPlanResponse> AmendTaskAsync(Guid org, Guid actorId,
        AmendWorkDeliveryTaskRequest request, CancellationToken ct = default)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var plan = await GetAuthorizedAsync(org, actorId, request.PlanId, WorkDeliveryCapabilities.Configure, ct);
        var member = await MemberAsync(org, actorId, ct);
        var projectManager = await db.Workstreams.Where(p => p.Id == plan.WorkstreamId && p.OrganizationId == org)
            .Select(p => p.AccountableManagerOrganizationUserId).SingleAsync(ct);
        if (member.Id != plan.ManagerOrganizationUserId && member.Id != projectManager && member.PermissionLevel != OrganizationPermissionLevel.Owner)
            throw new UnauthorizedAccessException("Only the project/release manager or organization owner can approve changed task criteria.");
        var replay = await ReplayAsync(org, actorId, request.IdempotencyKey, "amend-task", request, ct);
        if (replay is not null) return Response(replay);
        if (plan.Status != "Paused" || plan.Revision != request.ExpectedPlanRevision)
            throw new DbUpdateConcurrencyException("Pause the current delivery plan and review its latest revision before amending task scope.");
        static bool InvalidText(IReadOnlyList<string>? values, bool required) => values is null ? required :
            values.Count > 100 || required && values.Count == 0 || values.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 4000);
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2000 || request.Planning is null ||
            InvalidText(request.Planning.Requirements,true) || InvalidText(request.Planning.AcceptanceCriteria,true) ||
            InvalidText(request.Planning.Constraints,false))
            throw new ArgumentException("Provide the exact approved requirements, acceptance criteria and amendment reason.");
        if (plan.Executions.Any(e => e.Promotions.Any(p => p.Status == "Completed")))
            throw new InvalidOperationException("Promoted release scope is immutable; create a follow-up release.");
        if (plan.Executions.SelectMany(e => e.Stages).SelectMany(s => s.Attempts)
            .Any(a => a.Status is WorkExecutionAttemptStatus.Pending or WorkExecutionAttemptStatus.Running))
            throw new InvalidOperationException("Drain aggregate work before amending scope.");
        var item = await db.CoreWorkTasks.Include(i => i.StageAssignments).Include(i => i.Approvals)
            .SingleAsync(i => i.Id == request.ItemId && i.OrganizationId == org, ct);
        var delivery = Decode<WorkItemDeliverySpecification>(item.DeliverySpecificationJson ?? "null")
            ?? throw new InvalidOperationException("Finalize this task's delivery before amending scope.");
        var accepted = Decode<WorkItemPlanningSpecification>(item.PlanningSpecificationJson!);
        if (delivery.DeliveryPlanId != plan.Id || !item.IsExecutable || item.Status == WorkTaskStatus.Completed ||
            item.Revision != request.ExpectedItemRevision || item.PlanningRevision != request.ExpectedPlanningRevision)
            throw new DbUpdateConcurrencyException("The task or its accepted planning changed.");
        if (Encode(accepted with { Requirements=request.Planning.Requirements,AcceptanceCriteria=request.Planning.AcceptanceCriteria,
                Constraints=request.Planning.Constraints }) != Encode(request.Planning))
            throw new ArgumentException("This amendment preserves dependencies, staffing and accepted source-document identities.");
        var scopes = Scopes(plan).ToList();
        var storyIndex = scopes.FindIndex(s => s.Scope == "Story" && s.ItemId == item.ParentWorkTaskId && s.ChildIds.Contains(item.Id));
        if (storyIndex < 0) throw new UnauthorizedAccessException("Task is outside the approved release hierarchy.");
        var execution = await db.WorkItemExecutions.Include(e => e.SprintExecution).Include(e => e.Stages).ThenInclude(s => s.Attempts)
            .SingleAsync(e => e.WorkItemId == item.Id && e.Status == WorkItemExecutionStatus.Blocked &&
                (e.SprintExecution!.Status == WorkSprintExecutionStatus.Active || e.SprintExecution.Status == WorkSprintExecutionStatus.Paused), ct);
        if (execution.Stages.SelectMany(s => s.Attempts).Any(a => a.Status is WorkExecutionAttemptStatus.Pending or WorkExecutionAttemptStatus.Running))
            throw new InvalidOperationException("A task attempt is still active; stop its writer before amending scope.");
        var branches = Branches(plan).ToList();
        if (request.UsePlatformPreview)
        {
            var repository = await db.SourceControlRepositories.Include(r => r.Connection).SingleAsync(r => r.Id == delivery.RepositoryId && r.OrganizationId == org, ct);
            if (repository.Connection?.Provider != SourceControlProvider.InternalGit ||
                repository.Connection.Status != SourceControlConnectionStatus.Connected || repository.Status != SourceControlRepositoryStatus.Ready)
                throw new InvalidOperationException("The selected platform-preview amendment requires the current Ready InternalGit repository.");
            if (!scopes.Single(s => s.Scope == "Release").Stages.Any(s => s.StageKey == "build-readiness"))
                throw new InvalidOperationException("Keep an assigned release build-readiness stage for exact-source preview evidence.");
            branches = branches.Select(b => b.Scope == "Release" ? b with { ReadinessMode = "compute-test", Build = null } : b).ToList();
        }
        var now = clock.GetUtcNow();
        item.PlanningSpecificationJson = Encode(request.Planning); item.PlanningRevision++; item.AssignmentRevision++; item.Revision++; item.UpdatedAt = now;
        item.DeliverySpecificationJson = Encode(delivery with { Requirements = request.Planning.Requirements,
            AcceptanceCriteria = request.Planning.AcceptanceCriteria, Constraints = request.Planning.Constraints });
        foreach (var approval in item.Approvals)
        { approval.Status = "Pending"; approval.PlanningRevision = item.PlanningRevision; approval.UpdatedAt = now; }
        var revisions = scopes[storyIndex].ChildPlanningRevisions.ToDictionary(p => p.Key,p => p.Value); revisions[item.Id] = item.PlanningRevision;
        var digests = scopes[storyIndex].ChildDeliveryDigests.ToDictionary(p => p.Key,p => p.Value);
        digests[item.Id] = TaskDeliveryDigest(item, item.StageAssignments.ToList());
        scopes[storyIndex] = scopes[storyIndex] with { ChildPlanningRevisions = revisions, ChildDeliveryDigests = digests };
        plan.ScopesJson = Encode(scopes); plan.BranchesJson = Encode(branches); plan.ScopeRevision++; plan.Revision++; plan.UpdatedAt = now;
        foreach (var aggregate in plan.Executions.Where(e => e.Status != "Superseded").ToArray())
        {
            foreach (var stage in aggregate.Stages)
            {
                if (stage.Status != WorkStageExecutionStatus.Completed) stage.Status = WorkStageExecutionStatus.Cancelled;
                await RevokeDeliveryGrantsAsync(aggregate, stage, ct);
            }
            aggregate.Status = "Superseded";
            var replacement = new WorkDeliveryExecution { Id = Guid.NewGuid(), PlanId = plan.Id, Scope = aggregate.Scope,
                WorkItemId = aggregate.WorkItemId, BoardId = aggregate.BoardId, ScopeRevision = plan.ScopeRevision, CreatedAt = now, UpdatedAt = now };
            db.WorkDeliveryExecutions.Add(replacement);
        }
        var policy = await db.WorkOrchestrationPolicyRevisions.Include(p => p.Stages)
            .SingleAsync(p => p.Id == execution.SprintExecution!.PolicyRevisionId, ct);
        var author = policy.Stages.Single(s => s.Key is "development" or "specialist-execution");
        execution.Stages.Single(s => s.StageKey == execution.CurrentStageKey && s.Traversal == execution.Traversal).Status = WorkStageExecutionStatus.Cancelled;
        execution.Traversal++;
        var next = WorkOrchestrationService.CreateStageExecution(execution, author, item.StageAssignments.ToList(),
            execution.SprintExecution!.StartedByOrganizationUserId, now);
        next.Status = WorkStageExecutionStatus.Blocked;
        next.LastError = "Task scope amended. Install the verified agents, resume delivery, then retry the author against the preserved candidate.";
        next.LastSummary = request.Reason; execution.CurrentStageKey = author.Key; execution.BlockedReason = next.LastError; execution.UpdatedAt = now;
        item.BlockReason = next.LastError; item.Status = WorkTaskStatus.Blocked;
        db.WorkStageExecutions.Add(next);
        Receipt(plan, actorId, request.IdempotencyKey, "amend-task", request); Queue(plan, "task.scope-amended");
        await SaveDeliveryAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
        return Response(plan);
    }
}
