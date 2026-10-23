using System.Data;
using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkOrchestrationService
{
    // A corrective Bug is new work. The completed implementation and its evidence
    // stay immutable; the blocked QA ticket must depend on the new integration.
    public async Task<Guid> CreateLinkedDefectAsync(Guid organizationId, Guid boardId, Guid itemId,
        Guid applicationUserId, CreateLinkedDefectRequest request, CancellationToken cancellationToken = default)
    {
        ValidateIdempotencyKey(request.IdempotencyKey);
        if (string.IsNullOrWhiteSpace(request.Description) || string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 500)
            throw new ArgumentException("Provide a concise bug title and the observed defect with reproduction steps.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        var board = await db.WorkBoards.SingleAsync(x => x.Id == boardId && x.OrganizationId == organizationId, cancellationToken);
        if (board.ArchivedAt.HasValue || (board.ManagerOrganizationUserId != member.Id &&
            (member.EmployeeType != EmployeeType.Human || !await HoldsBoardGrantAsync(organizationId, member, WorkOrchestrationActions.Retry, boardId, cancellationToken))))
            throw new UnauthorizedAccessException("Current board recovery authority is required.");
        if (!await HoldsBoardGrantAsync(organizationId, member, WorkItemActions.Create, boardId, cancellationToken))
            throw new UnauthorizedAccessException("Creating the Bug requires the board's create grant.");
        var prior = await db.WorkOrchestrationEvents.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.EventType == "item.defect.created" && x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (prior is not null)
        {
            using var data = JsonDocument.Parse(prior.DataJson);
            var priorPlan = await db.WorkDeliveryPlans.AsNoTracking().SingleAsync(x => x.Id == data.RootElement.GetProperty("planId").GetGuid() && x.OrganizationId == organizationId, cancellationToken);
            await RequireDefectScopeAuthorityAsync(member, priorPlan, cancellationToken);
            if (prior.BoardId != boardId || data.RootElement.GetProperty("itemId").GetGuid() != itemId ||
                data.RootElement.GetProperty("id").GetGuid() != member.Id ||
                data.RootElement.GetProperty("expectedRevision").GetInt64() != request.ExpectedRevision ||
                data.RootElement.GetProperty("reason").GetString() != request.Description ||
                data.RootElement.GetProperty("title").GetString() != request.Title ||
                data.RootElement.GetProperty("qaItemId").GetGuid() != request.QaItemId ||
                data.RootElement.GetProperty("expectedQaRevision").GetInt64() != request.ExpectedQaRevision)
                throw new InvalidOperationException("The rework key is already bound to a different request.");
            await transaction.CommitAsync(cancellationToken); return data.RootElement.GetProperty("bugId").GetGuid();
        }
        var item = await db.WorkItemExecutions.Include(x => x.WorkItem).ThenInclude(x => x!.Approvals)
            .Include(x => x.SprintExecution).Include(x => x.Stages).ThenInclude(x => x.Attempts)
            .SingleOrDefaultAsync(x => x.WorkItemId == itemId && x.SprintExecution!.OrganizationId == organizationId &&
                x.SprintExecution.BoardId == boardId && x.SprintExecution.Status == WorkSprintExecutionStatus.Active, cancellationToken)
            ?? throw new InvalidOperationException("Engineering rework requires the ticket's active sprint.");
        var task = item.WorkItem!;
        if (task.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("The ticket changed. Refresh before reopening.");
        if (task.Status != WorkTaskStatus.Completed || item.Status != WorkItemExecutionStatus.Completed)
            throw new InvalidOperationException("Only a completed ticket can start completed-task rework.");
        var (authorizedPlan, _) = await WorkDeliveryTaskAuthorization.RequireAsync(db, task, cancellationToken);
        var plan = await db.WorkDeliveryPlans.Include(x => x.Executions).ThenInclude(x => x.Stages)
            .SingleAsync(x => x.Id == authorizedPlan.Id, cancellationToken);
        await RequireDefectScopeAuthorityAsync(member, plan, cancellationToken);
        var delivery = JsonSerializer.Deserialize<Shared.WorkItemDeliverySpecification>(task.DeliverySpecificationJson!, JsonOptions)!;
        if (delivery.DeliveryKind != "Code") throw new InvalidOperationException("Completed-task engineering rework requires code delivery.");
        if (!await db.WorkTaskIntegrationReceipts.AnyAsync(x => x.WorkItemId == itemId && x.ItemExecutionId == item.Id &&
            x.PlanId == plan.Id && x.Status == "Completed" && x.CandidateCommitSha == task.MergeCommitSha, cancellationToken))
            throw new InvalidOperationException("The completed ticket must retain its exact trusted integration receipt.");
        if (task.Approvals.Any(x => x.PlanningRevision != task.PlanningRevision || x.Status is not ("Approved" or "Waived")))
            throw new InvalidOperationException("Current planning approvals are required for rework.");
        // Aggregate candidates and accepted downstream work need explicit scope recovery.
        // This action is deliberately limited to defects discovered before aggregation.
        if (await db.WorkDeliveryExecutions.AnyAsync(x => x.PlanId == plan.Id &&
            x.Status != "WaitingForChildren" && x.Status != "Superseded" && x.Status != "Cancelled", cancellationToken) ||
            await db.WorkDeliveryPromotions.AnyAsync(x => x.Execution!.PlanId == plan.Id, cancellationToken))
            throw new InvalidOperationException("A delivery candidate already exists. Reconcile its scope before reopening engineering work.");
        if (await db.WorkItemExecutions.AnyAsync(x => x.SprintExecution!.BoardId == boardId &&
            x.SprintExecution.OrganizationId == organizationId && x.SprintExecution.Status == WorkSprintExecutionStatus.Active &&
            x.Status != WorkItemExecutionStatus.Completed && x.Status != WorkItemExecutionStatus.Blocked &&
            x.Status != WorkItemExecutionStatus.Failed && x.Status != WorkItemExecutionStatus.Cancelled, cancellationToken) ||
            await db.WorkExecutionAttempts.AnyAsync(x => x.StageExecution!.ItemExecution!.SprintExecution!.BoardId == boardId &&
                x.StageExecution.ItemExecution.SprintExecution.OrganizationId == organizationId &&
                (x.Status == WorkExecutionAttemptStatus.Pending || x.Status == WorkExecutionAttemptStatus.Running), cancellationToken))
            throw new InvalidOperationException("Stop and drain affected sprint work before reopening a completed dependency.");
        var affected = new HashSet<Guid> { itemId };
        var dependencies = await db.WorkItemDependencies.AsNoTracking().Where(x => x.WorkItem!.OrganizationId == organizationId).ToListAsync(cancellationToken);
        while (true)
        {
            var count = affected.Count;
            foreach (var edge in dependencies.Where(x => affected.Contains(x.DependsOnWorkItemId))) affected.Add(edge.WorkItemId);
            if (count == affected.Count) break;
        }
        if (await db.CoreWorkTasks.AnyAsync(x => affected.Contains(x.Id) && x.Id != itemId && x.Status == WorkTaskStatus.Completed, cancellationToken))
            throw new InvalidOperationException("Accepted downstream work must be reconciled before reopening its dependency.");
        var execution = item.SprintExecution!;
        var policy = await db.WorkOrchestrationPolicyRevisions.Include(x => x.Stages).SingleAsync(x => x.Id == execution.PolicyRevisionId, cancellationToken);
        var author = policy.Stages.Single(x => x.Key == "specialist-execution" || x.Key == "development");
        var assignments = DeserializeAssignments(execution.AssignmentSnapshotJson).Where(x => x.WorkItemId == itemId)
            .Select(x => new WorkItemStageAssignment { StageKey = x.StageKey, PrincipalKind = x.PrincipalKind,
                OrganizationUserId = x.OrganizationUserId, AgentInstallationId = x.AgentInstallationId,
                PlatformAction = x.PlatformAction, RequirementsJson = x.RequirementsJson, SelectionEvidenceJson = x.SelectionEvidenceJson }).ToList();
        if (!assignments.Any(x => x.StageKey == author.Key && (x.AgentInstallationId.HasValue || x.PrincipalKind == WorkOrchestrationPrincipalKind.Human && x.OrganizationUserId.HasValue)))
            throw new InvalidOperationException("The existing engineering assignment must be staffed before reopening.");
        var qaExecution = await db.WorkItemExecutions.Include(x => x.WorkItem).ThenInclude(x => x!.StageAssignments)
            .Include(x => x.WorkItem).ThenInclude(x => x!.Approvals).Include(x => x.Stages)
            .SingleOrDefaultAsync(x => x.WorkItemId == request.QaItemId && x.SprintExecutionId == execution.Id, cancellationToken)
            ?? throw new InvalidOperationException("Select the blocked QA ticket in this sprint.");
        var qa = qaExecution.WorkItem!;
        if (qa.Revision != request.ExpectedQaRevision) throw new DbUpdateConcurrencyException("The QA ticket changed. Refresh before filing the bug.");
        if (qa.Status != WorkTaskStatus.Blocked || qaExecution.Status != WorkItemExecutionStatus.Blocked ||
            qa.ParentWorkTaskId != task.ParentWorkTaskId || qa.Approvals.Count != 0 ||
            JsonSerializer.Deserialize<Shared.WorkItemDeliverySpecification>(qa.DeliverySpecificationJson!, JsonOptions)?.DeliveryKind != "Artifact")
            throw new InvalidOperationException("Link stopped artifact QA in the same Story; additional planning approvals require the full planning workflow.");
        await WorkDeliveryTaskAuthorization.RequireAsync(db, qa, cancellationToken);
        var registeredBug = await RequireRegisteredBugAsync(board, task.ParentWorkTaskId!.Value, cancellationToken);
        if (registeredBug.RequiredApprovalPolicyKeys.Count != 0)
            throw new InvalidOperationException("This Bug type requires the full specialist planning workflow before execution.");
        var currentScopes = JsonSerializer.Deserialize<Shared.WorkDeliveryScopeSnapshot[]>(plan.ScopesJson, JsonOptions)!;
        if (!currentScopes.Any(x => x.Scope == "Story" && x.ItemId == task.ParentWorkTaskId && x.ChildIds.Contains(task.Id) && x.ChildIds.Contains(qa.Id)))
            throw new InvalidOperationException("Both implementation and QA must be declared inputs of the same approved Story.");
        var deliveryService = new WorkDeliveryService(db, authorization!, sourceControlHost!, timeProvider);
        await deliveryService.ValidateCurrentScopeAsync(plan, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var bugId = Guid.NewGuid();
        var planning = JsonSerializer.Deserialize<Shared.WorkItemPlanningSpecification>(task.PlanningSpecificationJson!, JsonOptions)!;
        var bug = new WorkTask { Id = bugId, OrganizationId = organizationId, BoardId = boardId, SprintId = execution.SprintId,
            ParentWorkTaskId = task.ParentWorkTaskId, AccountableOrganizationUserId = task.AccountableOrganizationUserId,
            CreatedByOrganizationUserId = member.Id, Kind = WorkItemKind.Bug, TypeKey = registeredBug.Key, IsExecutable = true,
            IdentifierSequence = board.NextItemSequence, Identifier = $"{board.Key}-{board.NextItemSequence}",
            Title = request.Title.Trim(), Description = $"Defect in {task.Identifier}; blocks {qa.Identifier}.\n\n{request.Description}",
            PlanningRevision = 1, AssignmentRevision = 1, Revision = 1, Status = WorkTaskStatus.Ready, Priority = WorkTaskPriority.High,
            CreatedAt = now, UpdatedAt = now,
            PlanningSpecificationJson = JsonSerializer.Serialize(planning with { Requirements = [request.Description], DependencyItemIds = [task.Id] }, JsonOptions),
            DeliverySpecificationJson = JsonSerializer.Serialize(delivery with { Requirements = [request.Description], DependencyItemIds = [task.Id] }, JsonOptions) };
        board.NextItemSequence++; board.Revision++; board.UpdatedAt = now;
        bug.StageAssignments = assignments.Select(x => new WorkItemStageAssignment { Id = Guid.NewGuid(), OrganizationId = organizationId,
            BoardId = boardId, WorkItemId = bug.Id, StageKey = x.StageKey, PrincipalKind = x.PrincipalKind,
            OrganizationUserId = x.OrganizationUserId, AgentInstallationId = x.AgentInstallationId, PlatformAction = x.PlatformAction,
            RequirementsJson = x.RequirementsJson, SelectionEvidenceJson = x.SelectionEvidenceJson, CreatedAt = now }).ToList();
        db.CoreWorkTasks.Add(bug);
        db.WorkItemDependencies.Add(new() { WorkItemId = bug.Id, DependsOnWorkItemId = task.Id });
        db.WorkItemDependencies.Add(new() { WorkItemId = qa.Id, DependsOnWorkItemId = bug.Id });
        var qaPlanning = JsonSerializer.Deserialize<Shared.WorkItemPlanningSpecification>(qa.PlanningSpecificationJson!, JsonOptions)!;
        var qaDelivery = JsonSerializer.Deserialize<Shared.WorkItemDeliverySpecification>(qa.DeliverySpecificationJson!, JsonOptions)!;
        qa.PlanningSpecificationJson = JsonSerializer.Serialize(qaPlanning with { DependencyItemIds = qaPlanning.DependencyItemIds.Append(bug.Id).ToArray() }, JsonOptions);
        qa.DeliverySpecificationJson = JsonSerializer.Serialize(qaDelivery with { DependencyItemIds = qaDelivery.DependencyItemIds.Append(bug.Id).ToArray() }, JsonOptions);
        qa.PlanningRevision++; qa.AssignmentRevision++; qa.Revision++; qa.UpdatedAt = now;
        WorkOrchestrationBoardState.CurrentStage(qaExecution).Status = WorkStageExecutionStatus.Cancelled;
        qaExecution.Traversal++;
        var qaStage = CreateStageExecution(qaExecution, author, qa.StageAssignments, board.ManagerOrganizationUserId!.Value, now);
        qaStage.Status = WorkStageExecutionStatus.Blocked; qaStage.LastError = $"Waiting for {bug.Identifier}: {bug.Title}. Retry QA after its trusted integration and independent QA.";
        qaExecution.Status = WorkItemExecutionStatus.Blocked; qaExecution.BlockedReason = qaStage.LastError;
        qa.BlockReason = qaStage.LastError; qa.WaitingReason = null;
        db.WorkStageExecutions.Add(qaStage);
        var bugExecution = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = bug.Id, WorkItem = bug,
            SprintExecutionId = execution.Id, SprintExecution = execution, ItemIdentifier = bug.Identifier!, CreatedAt = now, UpdatedAt = now };
        db.WorkItemExecutions.Add(bugExecution);
        var stage = CreateStageExecution(bugExecution, author, bug.StageAssignments, board.ManagerOrganizationUserId!.Value, now);
        db.WorkStageExecutions.Add(stage);
        WorkOrchestrationBoardState.SynchronizeAgentCard(policy, stage, now);
        execution.AssignmentSnapshotJson = JsonSerializer.Serialize(DeserializeAssignments(execution.AssignmentSnapshotJson).Concat(
            assignments.Select(x => new AssignmentSnapshot(bug.Id, x.StageKey, x.PrincipalKind, x.OrganizationUserId,
                x.AgentInstallationId, x.PlatformAction, x.RequirementsJson, x.SelectionEvidenceJson))), JsonOptions);
        await IncludeCorrectiveBugAsync(plan, currentScopes, bug, qa, now, cancellationToken);
        execution.Revision++; execution.UpdatedAt = now;
        AddEvent(organizationId, boardId, execution.Id, bugExecution.Id, stage.Id, null, "item.defect.created",
            new { id = member.Id, itemId, expectedRevision = request.ExpectedRevision, reason = request.Description, title = request.Title,
                qaItemId = qa.Id, expectedQaRevision = request.ExpectedQaRevision, bugId = bug.Id, planId = plan.Id }, request.IdempotencyKey);
        var wake = new Shared.GenericResourceEvent(Guid.NewGuid(), now,
            new(organizationId, plan.WorkstreamId, board.TeamId, boardId, bug.Id, null, null, execution.Id, null, null),
            "WorkItem", bug.Id, bug.Revision, bug.TypeKey, "item.defect.created",
            JsonSerializer.SerializeToElement(new { stageId = stage.Id, bug.AssignmentRevision }, JsonOptions));
        db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = organizationId,
            EventType = Shared.WorkstreamEventNames.WorkItemChangedV1, DataJson = JsonSerializer.Serialize(wake, JsonOptions),
            IdempotencyKey = $"linked-defect:{stage.Id:N}", OccurredAt = now, NextAttemptAt = now });
        deliveryService.Queue(plan, "defect.created");
        await WorkOrchestrationBoardState.SaveBoardChangesAsync(db, execution, now, cancellationToken);
        await deliveryService.ValidateCurrentScopeAsync(plan, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return bug.Id;
    }
}
