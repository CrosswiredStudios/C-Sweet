using System.Data;
using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkDeliveryService
{
    private static readonly HashSet<string> TicketHandlers = new(StringComparer.Ordinal)
        { "review", "source-review", "quality", "build-readiness", "compute-test", "acceptance", "completion" };

    private static WorkDeliveryScopeSnapshot ScopeFor(WorkDeliveryPlan plan, WorkDeliveryExecution execution) =>
        Scopes(plan).Single(s => s.Scope == execution.Scope && s.ItemId == execution.WorkItemId);

    private static WorkDeliveryTicketStep? TicketStep(WorkDeliveryScopeSnapshot scope, string key) =>
        scope.TicketWorkflow?.Steps.SingleOrDefault(s => s.Key == key)
            ?? throw new InvalidOperationException("The execution step is absent from its declared workflow ticket.");

    private static string Handler(WorkDeliveryScopeSnapshot scope, string key) => TicketStep(scope, key)?.Handler ?? key;

    private async Task<WorkDeliveryPlanResponse> ConfigureTicketsAsync(Guid org, Guid actorId,
        ConfigureWorkDeliveryPlanRequest request, CancellationToken ct)
    {
        await AuthorizeProjectAsync(org, actorId, request.WorkstreamId, WorkDeliveryCapabilities.Configure, ct);
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var replay = await ReplayAsync(org, actorId, request.IdempotencyKey, "configure", request, ct);
        if (replay is not null) return Response(replay);
        var actor = await MemberAsync(org, actorId, ct);
        var project = await db.Workstreams.AsNoTracking().SingleAsync(p => p.Id == request.WorkstreamId && p.OrganizationId == org, ct);
        if (project.Status is not (WorkstreamStatus.Active or WorkstreamStatus.Approved)) throw new InvalidOperationException("The project is not active.");
        if (actor.PermissionLevel != OrganizationPermissionLevel.Owner && actor.Id != project.AccountableManagerOrganizationUserId && actor.Id != request.ManagerOrganizationUserId)
            throw new UnauthorizedAccessException("The delegated project manager defines its ticket workflow.");
        var manager = await db.CoreOrganizationUsers.SingleAsync(u => u.Id == request.ManagerOrganizationUserId && u.OrganizationId == org && u.IsActive, ct);
        if (!await db.ProjectParticipants.AnyAsync(p => p.OrganizationId == org && p.WorkstreamId == project.Id && p.OrganizationUserId == manager.Id && p.RemovedAt == null, ct))
            throw new UnauthorizedAccessException("The workflow manager must participate in the project.");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 160 || request.TicketWorkflows.Count > 500 ||
            request.TicketWorkflows.Select(w => w.TicketId).Distinct().Count() != request.TicketWorkflows.Count)
            throw new ArgumentException("Provide a name and distinct workflow tickets.");
        var plan = request.PlanId is { } id ? await Load().SingleAsync(p => p.Id == id && p.OrganizationId == org, ct) :
            new WorkDeliveryPlan { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = project.Id, CreatedAt = clock.GetUtcNow() };
        if (plan.WorkstreamId != project.Id || request.PlanId.HasValue && plan.Revision != request.ExpectedRevision || plan.Status is "Completed" or "Cancelled")
            throw new DbUpdateConcurrencyException("The workflow plan changed.");
        if (plan.Executions.Count > 0)
            throw new InvalidOperationException("An activated ticket graph is immutable; declare follow-up work in a new plan.");
        var scopes = new List<WorkDeliveryScopeSnapshot>();
        var items = await db.CoreWorkTasks.Include(t => t.Board).Where(t => t.OrganizationId == org &&
            t.Board != null && t.Board.WorkstreamId == project.Id && t.ArchivedAt == null).ToListAsync(ct);
        foreach (var workflow in request.TicketWorkflows)
        {
            var ticket = items.SingleOrDefault(t => t.Id == workflow.TicketId && t.TypeKey == WorkItemTypeKeys.WorkflowTicketV1)
                ?? throw new ArgumentException("Each workflow requires a canonical workflow ticket on this project.");
            await AuthorizeBoardAsync(org, actorId, ticket.BoardId!.Value, WorkBoardActions.Configure, ct);
            var planning = Decode<WorkItemPlanningSpecification>(ticket.PlanningSpecificationJson ?? "null")
                ?? throw new ArgumentException("A workflow ticket needs declared planning and acceptance criteria.");
            var definition = workflow.Definition();
            if (planning.Workflow is null)
                throw new ArgumentException("Define the workflow on its canonical ticket before configuring execution.");
            else if (Encode(planning.Workflow) != Encode(definition))
                throw new InvalidOperationException("The execution declaration must match the workflow owned by its canonical ticket.");
            var predecessors = planning.DependencyItemIds.ToArray();
            if (predecessors.Distinct().Count() != predecessors.Length || predecessors.Contains(ticket.Id) || predecessors.Any(p => items.All(t => t.Id != p)))
                throw new ArgumentException("Ticket predecessors must be distinct existing project work.");
            if (workflow.Steps.Count == 0 || workflow.Steps.Count > 100 || workflow.Steps.Select(s => s.Key).Distinct().Count() != workflow.Steps.Count ||
                workflow.Steps.Any(s => string.IsNullOrWhiteSpace(s.Key) || s.Key != s.Assignment.StageKey || !TicketHandlers.Contains(s.Handler)))
                throw new ArgumentException("Declare distinct assigned steps and their execution handlers; no steps are added implicitly.");
            if(workflow.EvidenceItemIds.Contains(ticket.Id)||workflow.EvidenceItemIds.Distinct().Count()!=workflow.EvidenceItemIds.Count||
                workflow.FinalReleaseDependencyItemIds.Contains(ticket.Id)||workflow.FinalReleaseDependencyItemIds.Any(id=>items.All(t=>t.Id!=id)))
                throw new ArgumentException("Declare distinct evidence inputs and existing release dependencies without self-reference.");
                if (ticket.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled || workflow.EvidenceItemIds.Any(e => items.All(t => t.Id != e)))
                    throw new ArgumentException("Select unfinished workflow tickets and existing evidence inputs.");
                var scopeItem = workflow.Scope == WorkExecutionScopes.Ticket ? ticket.Id : workflow.ScopeItemId;
                if (workflow.Scope is not (WorkExecutionScopes.Ticket or WorkExecutionScopes.Story or WorkExecutionScopes.Epic or WorkExecutionScopes.Release) ||
                    workflow.Scope is WorkExecutionScopes.Story or WorkExecutionScopes.Epic && !items.Any(t => t.Id == scopeItem && t.BoardId == ticket.BoardId))
                    throw new ArgumentException("Bind source scopes explicitly to existing project work.");
                var scope = new WorkDeliveryScopeSnapshot(workflow.Scope, scopeItem, ticket.BoardId.Value,
                    scopeItem is { } sourceId && sourceId != ticket.Id ? items.Single(t => t.Id == sourceId).PlanningRevision : ticket.PlanningRevision,
                    workflow.EvidenceItemIds, planning, workflow.Steps.Select(s => s.Assignment).ToArray())
                { TicketWorkflow = workflow, PredecessorItemIds = predecessors, TicketPlanningRevision = ticket.PlanningRevision,
                    ChildPlanningRevisions = workflow.EvidenceItemIds.ToDictionary(e => e, e => items.Single(t => t.Id == e).PlanningRevision) };
                scopes.Add(scope);
            await ValidateAssignmentsAsync(org, scope, ct, request.TaskAssignments);
            if (workflow.CompleteContainerItemIds.Any(id => !items.Any(t => t.Id == id && !t.IsExecutable && t.BoardId == ticket.BoardId && t.TypeKey != WorkItemTypeKeys.WorkflowTicketV1)))
                throw new ArgumentException("Completion effects may name only existing board-local containers.");
        }
        ValidateTicketGraph(scopes);
        if (scopes.Select(s => (s.Scope, s.ItemId)).Distinct().Count() != scopes.Count)
            throw new ArgumentException("Each source binding belongs to one declared workflow ticket.");
        await ValidateDeclaredBranchesAsync(org, scopes, request.Branches, ct);
        plan.Name = request.Name.Trim(); plan.ManagerOrganizationUserId = manager.Id;
        plan.EpicItemIdsJson = Encode(request.EpicItemIds); plan.BranchesJson = Encode(request.Branches); plan.Status = "Draft";
        if (!request.PlanId.HasValue) db.WorkDeliveryPlans.Add(plan);
        plan.ScopesJson = Encode(scopes); plan.Revision++; plan.UpdatedAt = clock.GetUtcNow();
        foreach (var execution in plan.Executions.Where(e => e.ScopeRevision == plan.ScopeRevision))
            await SynchronizeTicketAsync(plan, execution, ct);
        Receipt(plan, actorId, request.IdempotencyKey, "configure", request); Queue(plan, "tickets.declared");
        await SaveDeliveryAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
        return Response(plan);
    }

    private static void ValidateTicketGraph(IReadOnlyList<WorkDeliveryScopeSnapshot> scopes)
    {
        var graph = scopes.ToDictionary(s => s.TicketWorkflow!.TicketId, s => s.PredecessorItemIds.Concat(s.ChildIds).Distinct().ToArray());
        var visiting = new HashSet<Guid>(); var visited = new HashSet<Guid>();
        void Visit(Guid id)
        {
            if (visited.Contains(id) || !graph.TryGetValue(id, out var dependencies)) return;
            if (!visiting.Add(id)) throw new ArgumentException("The declared ticket dependency graph contains a cycle.");
            foreach (var predecessor in dependencies) Visit(predecessor);
            visiting.Remove(id); visited.Add(id);
        }
        foreach (var id in graph.Keys) Visit(id);
    }

    private static IReadOnlyList<WorkDeliveryExecution> OrderedTicketExecutions(WorkDeliveryPlan plan)
    {
        var executions=plan.Executions.Where(e=>e.ScopeRevision==plan.ScopeRevision).ToDictionary(e=>ScopeFor(plan,e).TicketWorkflow!.TicketId);
        var ordered=new List<WorkDeliveryExecution>();var visited=new HashSet<Guid>();
        void Visit(Guid ticket)
        {
            if(!visited.Add(ticket)||!executions.TryGetValue(ticket,out var execution))return;
            foreach(var predecessor in ScopeFor(plan,execution).PredecessorItemIds)Visit(predecessor);
            ordered.Add(execution);
        }
        foreach(var ticket in executions.Keys.Order())Visit(ticket);
        return ordered;
    }

    private async Task ValidateDeclaredTicketAsync(WorkDeliveryPlan plan, WorkDeliveryScopeSnapshot scope, CancellationToken ct)
    {
        var ticket = await db.CoreWorkTasks.AsNoTracking().SingleAsync(t => t.Id == scope.TicketWorkflow!.TicketId && t.OrganizationId == plan.OrganizationId, ct);
        var planning = Decode<WorkItemPlanningSpecification>(ticket.PlanningSpecificationJson!);
        if (ticket.ArchivedAt is not null || ticket.Status == WorkTaskStatus.Cancelled || ticket.BoardId != scope.BoardId ||
            ticket.PlanningRevision != scope.TicketPlanningRevision || !planning.DependencyItemIds.Order().SequenceEqual(scope.PredecessorItemIds.Order()) ||
            Encode(planning with { DependencyItemIds = [], Workflow = null }) != Encode(scope.Planning with { DependencyItemIds = [], Workflow = null }) ||
            Encode(planning.Workflow) != Encode(scope.TicketWorkflow.Definition()))
            throw new InvalidOperationException("The declared workflow ticket changed; reconcile its plan before executing it.");
        foreach (var pair in scope.ChildPlanningRevisions)
            if (!await db.CoreWorkTasks.AnyAsync(t => t.Id == pair.Key && t.OrganizationId == plan.OrganizationId &&
                t.ArchivedAt == null && t.PlanningRevision == pair.Value && t.Status != WorkTaskStatus.Cancelled,ct))
                throw new InvalidOperationException("Declared evidence planning changed or was cancelled; reconcile the workflow ticket.");
    }

    private async Task<bool> TicketPredecessorsCompleteAsync(WorkDeliveryPlan plan, WorkDeliveryScopeSnapshot scope, CancellationToken ct)
    {
        foreach (var id in scope.PredecessorItemIds)
        {
            var predecessorScope = Scopes(plan).SingleOrDefault(s => s.TicketWorkflow?.TicketId == id);
            if (predecessorScope is not null && !plan.Executions.Any(e => e.ScopeRevision == plan.ScopeRevision &&
                e.Scope == predecessorScope.Scope && e.WorkItemId == predecessorScope.ItemId && e.Status == "Completed")) return false;
            // Read tracked state so predecessor completion can release a successor
            // in this atomic reconciliation, without another timer tick or partial commit.
            var predecessor=await db.CoreWorkTasks.SingleOrDefaultAsync(t=>t.Id==id&&t.OrganizationId==plan.OrganizationId,ct);
            if(predecessor is null||predecessor.ArchivedAt is not null||predecessor.Status!=WorkTaskStatus.Completed)return false;
        }
        return true;
    }

    private async Task AdvanceTicketAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution, WorkDeliveryScopeSnapshot scope,
        WorkStageExecution completed, CancellationToken ct)
    {
        var steps = scope.TicketWorkflow!.Steps;
        var index = steps.ToList().FindIndex(s => s.Key == completed.StageKey);
        if (index < 0) throw new InvalidOperationException("The completed step is absent from the declared ticket.");
        if (index + 1 < steps.Count) await CreateReviewAsync(plan, execution, scope, steps[index + 1].Key, ct);
        else
        {
            execution.AcceptanceJson = completed.Attempts.MaxBy(a => a.Attempt)?.ResultJson ?? completed.LastSummary;
            execution.Status = scope.TicketWorkflow.PromoteOnCompletion ? "Promoting" : "Completed";
            execution.BlockedReason = null;
        }
        execution.Revision++; execution.UpdatedAt = clock.GetUtcNow();
        await SynchronizeTicketAsync(plan, execution, ct);
    }



    private async Task SynchronizeTicketAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution, CancellationToken ct)
    {
        var scope = ScopeFor(plan, execution);
        if (scope.TicketWorkflow is not { } workflow) return;
        var ticket = await db.CoreWorkTasks.SingleAsync(t => t.Id == workflow.TicketId && t.OrganizationId == plan.OrganizationId, ct);
        var status = execution.Status switch { "Completed" => WorkTaskStatus.Completed, "Blocked" or "PartiallyPromoted" => WorkTaskStatus.Blocked,
            "WaitingForChildren" => WorkTaskStatus.Backlog, "Ready" => WorkTaskStatus.Ready,
            "Running" or "Promoting" => WorkTaskStatus.Running, "Cancelled" => WorkTaskStatus.Cancelled, _ => WorkTaskStatus.WaitingForApproval };
        var current = execution.Stages.LastOrDefault(s => s.StageKey == execution.CurrentStageKey);
        var owner = current?.OrganizationUserId ?? ticket.AccountableOrganizationUserId;
        var waiting = execution.Status == "WaitingForChildren" ? "Waiting for predecessor tickets" : execution.CurrentStageKey;
        if (ticket.Status == status && ticket.AssignedEmployeeId == owner && ticket.BlockReason == execution.BlockedReason && ticket.WaitingReason == waiting) return;
        ticket.Status = status; ticket.AssignedEmployeeId = owner; ticket.AssignedAgentInstallationId = current?.AgentInstallationId;
        ticket.WaitingReason = waiting; ticket.BlockReason = execution.BlockedReason;
        var columns = await db.WorkBoardColumns.Where(c => c.BoardId == scope.BoardId).ToListAsync(ct);
        var category = status == WorkTaskStatus.Completed ? WorkBoardColumnCategory.Done : status is WorkTaskStatus.Backlog or WorkTaskStatus.Ready ? WorkBoardColumnCategory.ToDo :
            status == WorkTaskStatus.Blocked ? WorkBoardColumnCategory.Blocked : WorkBoardColumnCategory.InProgress;
        ticket.BoardColumnId = columns.FirstOrDefault(c => c.Category == category)?.Id ?? ticket.BoardColumnId;
        ticket.Revision++; ticket.UpdatedAt = clock.GetUtcNow();
        if (execution.Status == "Completed")
            foreach (var id in workflow.CompleteContainerItemIds)
            {
                var container = await db.CoreWorkTasks.SingleAsync(t => t.Id == id && t.OrganizationId == plan.OrganizationId && !t.IsExecutable, ct);
                if (container.Status == WorkTaskStatus.Completed) continue;
                container.Status = WorkTaskStatus.Completed; container.BoardColumnId = columns.FirstOrDefault(c => c.Category == WorkBoardColumnCategory.Done)?.Id ?? container.BoardColumnId;
                container.Revision++; container.UpdatedAt = clock.GetUtcNow();
            }
        db.WorkItemActivities.Add(new() { Id = Guid.NewGuid(), OrganizationId = plan.OrganizationId, BoardId = scope.BoardId,
            WorkItemId = ticket.Id, Action = "workflow.updated", EventType = "work.item.workflow.updated", ActorKind = GrantSubjectKind.AutomationIdentity,
            ActorSubjectId = execution.Id, ActorDisplayName = "Declared ticket workflow", OccurredAt = clock.GetUtcNow(), DataJson = Encode(new {
                executionId = execution.Id, execution.Status, stage = execution.CurrentStageKey, execution.BlockedReason }) });
        var now = clock.GetUtcNow();
        var readers = await db.ScopedActionGrants.AsNoTracking().Where(g => g.OrganizationId == plan.OrganizationId &&
            g.SubjectKind == GrantSubjectKind.OrganizationUser && g.RevokedAt == null && (g.ExpiresAt == null || g.ExpiresAt > now) &&
            (g.ScopeKind == GrantScopeKind.Organization || g.ScopeKind == GrantScopeKind.Board && g.ScopeId == scope.BoardId) &&
            (g.Action == WorkBoardActions.Read || g.Action == WorkItemActions.Read)).ToListAsync(ct);
        var recipients = readers.Where(g => g.Action == WorkBoardActions.Read).Select(g => g.SubjectId)
            .Intersect(readers.Where(g => g.Action == WorkItemActions.Read).Select(g => g.SubjectId)).ToArray();
        db.ApplicationRealtimeOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = plan.OrganizationId,
            RecipientOrganizationUserIdsJson = Encode(recipients), EventType = CSweet.Contracts.Realtime.AppRealtimeEvents.WorkBoardChanged,
            Subject = $"organizations/{plan.OrganizationId:D}/work/boards/{scope.BoardId:D}",
            DataJson = Encode(new { boardId = scope.BoardId, itemId = ticket.Id, changeType = "item.updated", revision = ticket.Revision }),
            Status = CSweet.Domain.Notifications.ApplicationRealtimeOutboxStatus.Pending, OccurredAt = now, NextAttemptAt = now });
    }

    private async Task ValidateDeclaredBranchesAsync(Guid org, IReadOnlyList<WorkDeliveryScopeSnapshot> scopes,
        IReadOnlyList<WorkDeliveryBranchBinding> branches, CancellationToken ct)
    {
        if (branches.Select(b => (b.Scope, b.ItemId, b.RepositoryId)).Distinct().Count() != branches.Count)
            throw new ArgumentException("A ticket binds each repository once.");
        foreach (var branch in branches)
        {
            var scope = scopes.SingleOrDefault(s => s.Scope == branch.Scope && s.ItemId == branch.ItemId)
                ?? throw new ArgumentException("The branch has no declared workflow ticket.");
            if (string.IsNullOrWhiteSpace(branch.SourceBranch) || string.IsNullOrWhiteSpace(branch.TargetBranch) ||
                branch.SourceBranch.Length > 255 || branch.TargetBranch.Length > 255 ||
                branch.TargetBaseBranch is { } baseline && (string.IsNullOrWhiteSpace(baseline) || baseline.Length > 255 || baseline == branch.TargetBranch) ||
                branch.SourceBranch != branch.TargetBranch && scope.TicketWorkflow?.PromoteOnCompletion != true)
                throw new ArgumentException("Declare promotion explicitly, or review a single immutable branch.");
            await RepositoryAsync(org, branch.RepositoryId, ct);
            foreach (var assignment in scope.Stages)
            {
                var teams = await db.TeamMemberships.Where(m => m.OrganizationId == org && m.OrganizationUserId == assignment.OrganizationUserId && m.EndedAt == null)
                    .Select(m => m.TeamId).ToListAsync(ct);
                if (!await db.TeamRepositoryPolicies.AnyAsync(p => p.OrganizationId == org && teams.Contains(p.TeamId) && p.RepositoryId == branch.RepositoryId && p.DisabledAt == null, ct))
                    throw new UnauthorizedAccessException("Assigned workers require explicit repository access.");
            }
        }
    }
}
