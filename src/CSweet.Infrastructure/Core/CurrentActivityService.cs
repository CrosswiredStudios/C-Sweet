using CSweet.Application.Security;
using CSweet.Contracts.Core;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.Core;

/// <summary>Read-only projection of current execution. No employee-wide inference guesses.</summary>
public sealed partial class CurrentActivityService(CSweetDbContext db, IScopedActionAuthorizationService authorization,
    CurrentActivityFeedReader feed, TimeProvider clock)
{
    private sealed record Candidate(WorkTask? Ticket, OrganizationUser Employee, AgentWorkItem? Work,
        AgentWorkAttempt? Attempt, string Category, string Context, string? Stage, AgentRunLog? StandaloneRun = null,
        AgentCoordinationSession? Coordination = null, DeliveryActivity? Delivery = null, ComputeActivity? Compute = null)
    {
        public string Key => Ticket is null ? StandaloneRun is not null ? $"model:{StandaloneRun.Id}" : $"work:{Work!.Id}" : $"task:{Ticket.Id}:{Employee.Id}";
    }

    private sealed record DeliveryActivity(Guid WorkId, Guid StageId, Guid? InstallationId, Guid? EmployeeId,
        Guid BoardId, Guid? WorkItemId, Guid ProjectId, string Name, string Scope, string Stage, string ScopesJson);

    private sealed record TicketSource(AgentWorkItem Work, Guid BoardId, Guid ItemId, Guid? OwnerId = null,
        long? AssignmentRevision = null, bool RequireAssignment = true);

    private static bool IsTicketEvent(string name) => name is PersonalTodoEvents.Available or WorkItemEvents.Assigned or WorkItemDiscussion.Changed;

    // These are source associations, not execution contexts or grants. A queued
    // delivery can name its ticket before the first attempt exists.
    private async Task<Dictionary<Guid, Guid>> ReadSourceTicketsAsync(Guid organizationId, IReadOnlyList<AgentWorkItem> works,
        IReadOnlyList<OrganizationUser> employees, CancellationToken token)
    {
        var result = new Dictionary<Guid, Guid>();
        var keys = works.Select(ChiefWorkCards.Key).ToArray();
        var cards = await db.CoreWorkTasks.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            !x.IsExecutable && x.Board != null && x.Board.OrganizationId == organizationId &&
            x.CreationIdempotencyKey != null && keys.Contains(x.CreationIdempotencyKey))
            .Select(x => new { x.Id, x.CreationIdempotencyKey, x.AssignedEmployeeId, x.AssignedAgentInstallationId,
                x.CreatedByOrganizationUserId, x.Board!.Kind, x.Board.OwnerOrganizationUserId }).ToListAsync(token);
        foreach (var work in works)
        {
            var employee = employees.FirstOrDefault(x => x.AgentInstallationId == work.AgentInstallationId);
            var card = cards.SingleOrDefault(x => x.CreationIdempotencyKey == ChiefWorkCards.Key(work) &&
                x.AssignedAgentInstallationId == work.AgentInstallationId && x.AssignedEmployeeId == employee?.Id &&
                x.CreatedByOrganizationUserId == employee?.Id && x.Kind == WorkBoardKind.Personal && x.OwnerOrganizationUserId == employee?.Id);
            if (card is not null) result[work.Id] = card.Id;
        }
        var eventWorks = works.Where(x => x.SourceType == "platform-event" && IsTicketEvent(x.Name) &&
            Guid.TryParse(x.SourceId, out _)).ToArray();
        var eventIds = eventWorks.Select(x => Guid.Parse(x.SourceId!)).ToArray();
        var events = await db.AgentPlatformEventOutbox.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            eventIds.Contains(x.Id))
            .Select(x => new { x.Id, x.EventType, x.TargetInstallationId, x.DataJson }).ToListAsync(token);
        var bindings = new List<TicketSource>();
        foreach (var work in eventWorks)
        {
            var source = events.SingleOrDefault(x => x.Id == Guid.Parse(work.SourceId!) && x.EventType == work.Name && x.TargetInstallationId == work.AgentInstallationId);
            if (source is null) continue;
            try
            {
                var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                if (work.Name == PersonalTodoEvents.Available)
                {
                    var payload = JsonSerializer.Deserialize<PersonalTodoAvailableEvent>(source.DataJson, json);
                    if (payload is not null && employees.Any(x => x.Id == payload.OwnerOrganizationUserId && x.AgentInstallationId == work.AgentInstallationId))
                        bindings.Add(new(work, payload.BoardId, payload.TriggerItemId, payload.OwnerOrganizationUserId));
                }
                else if (work.Name == WorkItemEvents.Assigned)
                {
                    var payload = JsonSerializer.Deserialize<WorkItemAssignedEvent>(source.DataJson, json);
                    if (payload?.AssignedInstallationId == work.AgentInstallationId)
                        bindings.Add(new(work, payload.BoardId, payload.ItemId, AssignmentRevision: payload.AssignmentRevision));
                }
                else
                {
                    using var document = JsonDocument.Parse(source.DataJson);
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("boardId", out var board) && board.TryGetGuid(out var boardId) &&
                        root.TryGetProperty("itemId", out var item) && item.TryGetGuid(out var itemId) &&
                        root.TryGetProperty("recipientEmployeeId", out var recipient) && recipient.TryGetGuid(out var employeeId) &&
                        employees.Any(x => x.Id == employeeId && x.AgentInstallationId == work.AgentInstallationId))
                        bindings.Add(new(work, boardId, itemId, RequireAssignment: false));
                }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { }
        }
        var itemIds = bindings.Select(x => x.ItemId).ToArray();
        var items = await db.CoreWorkTasks.AsNoTracking().Where(x => x.OrganizationId == organizationId && itemIds.Contains(x.Id))
            .Select(x => new { x.Id, x.BoardId, x.AssignedEmployeeId, x.AssignedAgentInstallationId, x.AssignmentRevision }).ToListAsync(token);
        foreach (var binding in bindings)
            if (items.Any(x => x.Id == binding.ItemId && x.BoardId == binding.BoardId &&
                (!binding.OwnerId.HasValue || x.AssignedEmployeeId == binding.OwnerId) &&
                (!binding.AssignmentRevision.HasValue || x.AssignmentRevision == binding.AssignmentRevision) &&
                (!binding.RequireAssignment || x.AssignedAgentInstallationId == binding.Work.AgentInstallationId)))
                result[binding.Work.Id] = binding.ItemId;
        return result;
    }

    // The canonical execution attempt binds a delivery to its review stage. Never
    // inspect the protected assignment or guess a release from the employee's role.
    private async Task<List<DeliveryActivity>> ReadDeliveriesAsync(Guid organizationId, Guid[] workIds, CancellationToken token)
    {
        if (workIds.Length == 0) return [];
        var deliveries = await (from attempt in db.WorkExecutionAttempts.AsNoTracking()
                      let stage = attempt.StageExecution!
                      let execution = stage.DeliveryExecution!
                      let plan = execution.Plan!
                      join board in db.WorkBoards on execution.BoardId equals board.Id
                      where attempt.AgentWorkItemId.HasValue && workIds.Contains(attempt.AgentWorkItemId!.Value) &&
                          plan.OrganizationId == organizationId && board.OrganizationId == organizationId &&
                          board.ArchivedAt == null && board.WorkstreamId == plan.WorkstreamId
                      select new DeliveryActivity(attempt.AgentWorkItemId!.Value, stage.Id, stage.AgentInstallationId,
                          stage.OrganizationUserId, board.Id, execution.WorkItemId, plan.WorkstreamId, plan.Name, execution.Scope, stage.StageKey, plan.ScopesJson))
            .ToListAsync(token);
        return deliveries.Select(delivery => delivery with {
            WorkItemId = JsonSerializer.Deserialize<WorkDeliveryScopeSnapshot[]>(delivery.ScopesJson,new JsonSerializerOptions(JsonSerializerDefaults.Web))?
                .SingleOrDefault(s=>s.Scope==delivery.Scope&&s.ItemId==delivery.WorkItemId)?.TicketWorkflow?.TicketId ?? delivery.WorkItemId
        }).ToList();
    }

    public async Task<CurrentActivityPage> ReadAsync(Guid organizationId, Guid userId, int offset = 0,
        bool projectsOnly = false, CancellationToken token = default)
    {
        var (actor, people) = await ActorAsync(organizationId, userId, token);
        var now = clock.GetUtcNow();
        var employees = people.Where(x => x.EmployeeType == EmployeeType.Agent && x.AgentInstallationId.HasValue).ToArray();
        var installations = employees.Select(x => x.AgentInstallationId!.Value).ToArray();
        var org = organizationId.ToString();
        var resultWorkIds = db.WorkExecutionAttempts.AsNoTracking().Where(x => x.AgentWorkItemId.HasValue &&
            (x.Status == WorkExecutionAttemptStatus.Pending || x.Status == WorkExecutionAttemptStatus.Running) &&
            (x.StageExecution!.Status == WorkStageExecutionStatus.Dispatching || x.StageExecution.Status == WorkStageExecutionStatus.Running) &&
            (x.StageExecution.ItemExecution != null && x.StageExecution.ItemExecution.SprintExecution!.OrganizationId == organizationId &&
                (x.StageExecution.ItemExecution.SprintExecution.Status == WorkSprintExecutionStatus.Active ||
                 x.StageExecution.ItemExecution.SprintExecution.Status == WorkSprintExecutionStatus.Paused) ||
             x.StageExecution.DeliveryExecution != null && x.StageExecution.DeliveryExecution.Plan!.OrganizationId == organizationId))
            .Select(x => x.AgentWorkItemId!.Value);
        // Only current work is discovered. Payloads and historical attempts are not materialized.
        var works = await db.AgentWorkItems.AsNoTracking().Where(x => x.OrganizationId == org &&
            installations.Contains(x.AgentInstallationId) && (x.Status == AgentWorkStatus.Leased || x.Status == AgentWorkStatus.Pending ||
                resultWorkIds.Contains(x.Id)))
            .Select(x => new AgentWorkItem { Id = x.Id, AgentInstallationId = x.AgentInstallationId, Status = x.Status,
                SourceType = x.SourceType, SourceId = x.SourceId, Name = x.Name, AttemptCount = x.AttemptCount,
                CreatedAt = x.CreatedAt, AvailableAt = x.AvailableAt, CompletedAt = x.CompletedAt }).ToListAsync(token);
        var workIds = works.Select(x => x.Id).ToArray();
        var sourceTickets = await ReadSourceTicketsAsync(organizationId, works, employees, token);
        var deliveries = await ReadDeliveriesAsync(organizationId, works.Where(x => x.SourceType == "WorkDeliveryStage").Select(x => x.Id).ToArray(), token);
        // The session's current delivery is the exact binding. Never infer a partner
        // or task from an employee's latest conversation or from protected payloads.
        var collaborations = await db.AgentCoordinationSessions.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            x.CurrentAgentWorkItemId.HasValue && workIds.Contains(x.CurrentAgentWorkItemId.Value))
            .Select(x => new AgentCoordinationSession { CurrentAgentWorkItemId = x.CurrentAgentWorkItemId,
                CurrentOrganizationUserId = x.CurrentOrganizationUserId, InitiatorOrganizationUserId = x.InitiatorOrganizationUserId,
                InitiatorInstallationId = x.InitiatorInstallationId, TargetOrganizationUserId = x.TargetOrganizationUserId,
                TargetInstallationId = x.TargetInstallationId, SourceWorkItemId = x.SourceWorkItemId,
                Subject = x.Subject }).ToListAsync(token);
        var standaloneRuns = await db.AgentRunLogs.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            x.AgentInstallationId.HasValue && installations.Contains(x.AgentInstallationId.Value) && x.AgentWorkAttemptId == null &&
            x.CompletedAt == null && (x.Status == "Running" || x.Status == "Queued") &&
            (!x.AgentWorkItemId.HasValue || !workIds.Contains(x.AgentWorkItemId.Value)))
            .Select(x => new AgentRunLog { Id = x.Id, AgentInstallationId = x.AgentInstallationId, EmployeeId = x.EmployeeId,
                WorkItemId = x.WorkItemId, StartedAt = x.StartedAt, Status = x.Status, ProviderProfileId = x.ProviderProfileId, Model = x.Model }).ToListAsync(token);
        var attempts = await db.AgentWorkAttempts.AsNoTracking().Where(x => workIds.Contains(x.AgentWorkItemId) &&
            (x.FinishedAt == null || resultWorkIds.Contains(x.AgentWorkItemId))).ToListAsync(token);
        var contexts = await db.WorkExecutionContexts.AsNoTracking().Where(x => x.OrganizationId == organizationId && workIds.Contains(x.AgentWorkItemId)).ToListAsync(token);
        var executions = await db.WorkItemExecutions.AsNoTracking().Include(x => x.Stages).Include(x => x.SprintExecution)
            .Where(x => x.SprintExecution!.OrganizationId == organizationId &&
                (x.SprintExecution.Status == WorkSprintExecutionStatus.Active || x.SprintExecution.Status == WorkSprintExecutionStatus.Paused)).ToListAsync(token);
        var stages = executions.ToDictionary(x => x.WorkItemId, x => x.Stages.Where(s => s.StageKey == x.CurrentStageKey)
            .OrderByDescending(s => s.CreatedAt).FirstOrDefault());
        var computeStages = stages.Where(x => x.Value is { LastOutcomeCode: "awaiting-compute",
            Status: WorkStageExecutionStatus.Backoff or WorkStageExecutionStatus.Pending or WorkStageExecutionStatus.Dispatching,
            ItemExecution.Status: WorkItemExecutionStatus.Pending or WorkItemExecutionStatus.Running } &&
            x.Value.Traversal == x.Value.ItemExecution.Traversal).ToDictionary(x => x.Key, x => x.Value!);
        var computeIds = computeStages.Keys.ToArray();
        var focusedIds = contexts.Select(x => x.WorkItemId ?? x.RootWorkItemId).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var tickets = await db.CoreWorkTasks.AsNoTracking().Include(x => x.Board).Where(x => x.OrganizationId == organizationId &&
            x.ArchivedAt == null && x.Board != null && x.Board.ArchivedAt == null &&
            (x.Status == WorkTaskStatus.Running || x.Status == WorkTaskStatus.WaitingForApproval || x.Status == WorkTaskStatus.Blocked || focusedIds.Contains(x.Id) || computeIds.Contains(x.Id)))
            .ToListAsync(token);
        var stageIds = works.Where(x => x.SourceType == "WorkStageExecution" && Guid.TryParse(x.SourceId, out _)).Select(x => Guid.Parse(x.SourceId!)).ToArray();
        var stageTasks = await db.WorkStageExecutions.AsNoTracking().Where(x => stageIds.Contains(x.Id) && x.ItemExecution!.SprintExecution!.OrganizationId == organizationId)
            .Select(x => new { x.Id, x.ItemExecution!.WorkItemId }).ToDictionaryAsync(x => x.Id, x => x.WorkItemId, token);
        var missingIds = stageTasks.Values.Concat(standaloneRuns.Where(x => x.WorkItemId.HasValue).Select(x => x.WorkItemId!.Value))
            .Concat(sourceTickets.Values)
            .Concat(deliveries.Where(x => x.WorkItemId.HasValue).Select(x => x.WorkItemId!.Value))
            .Concat(collaborations.Where(x => x.SourceWorkItemId.HasValue).Select(x => x.SourceWorkItemId!.Value))
            .Except(tickets.Select(x => x.Id)).ToArray();
        tickets.AddRange(await db.CoreWorkTasks.AsNoTracking().Include(x => x.Board).Where(x => missingIds.Contains(x.Id) &&
            x.OrganizationId == organizationId && x.ArchivedAt == null && x.Board != null && x.Board.ArchivedAt == null).ToListAsync(token));
        var projects = await db.Workstreams.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .Select(x => new { x.Id, x.Name }).ToDictionaryAsync(x => x.Id, x => x.Name, token);
        var boardsAllowed = new Dictionary<Guid, bool>();
        var deliveryBoardIds = deliveries.Select(x => x.BoardId).ToArray();
        var deliveryBoards = await db.WorkBoards.AsNoTracking().Where(x => deliveryBoardIds.Contains(x.Id)).ToListAsync(token);
        foreach (var board in tickets.Select(x => x.Board!).Concat(deliveryBoards).DistinctBy(x => x.Id))
        {
            var action = board.Kind == WorkBoardKind.Personal ? PersonalTodoActions.Read : WorkBoardActions.Read;
            boardsAllowed[board.Id] = (await authorization.AuthorizeAsync(organizationId, GrantSubjectKind.OrganizationUser,
                actor.Id, action, GrantScopeKind.Board, board.Id, token)).Allowed;
        }
        var candidates = new List<Candidate>();
        var compute = new Dictionary<Guid, ComputeActivity>();
        foreach (var ticket in tickets.Where(x => computeStages.ContainsKey(x.Id) && boardsAllowed.GetValueOrDefault(x.BoardId!.Value) &&
            x.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled)))
        {
            var stage = computeStages[ticket.Id];
            if (employees.Any(x => x.Id == stage.OrganizationUserId && x.AgentInstallationId == stage.AgentInstallationId))
                compute[ticket.Id] = await ReadComputeAsync(organizationId, ticket, stage, now, token) ??
                    new("Waiting", stage.ItemExecution!.SprintExecution!.Status == WorkSprintExecutionStatus.Paused ?
                        "Workflow is paused; automatic agent continuation is paused" :
                        "Waiting for compute; confirmed progress details are unavailable", stage.UpdatedAt, null);
        }
        foreach (var work in works.OrderByDescending(x => x.Status == AgentWorkStatus.Leased).ThenByDescending(x => x.CreatedAt))
        {
            var employee = employees.First(x => x.AgentInstallationId == work.AgentInstallationId);
            var delivery = deliveries.FirstOrDefault(x => x.WorkId == work.Id && x.InstallationId == work.AgentInstallationId &&
                x.EmployeeId == employee.Id && Guid.TryParse(work.SourceId, out var sourceStage) && x.StageId == sourceStage);
            if (work.SourceType == "WorkDeliveryStage" && (delivery is null || !boardsAllowed.GetValueOrDefault(delivery.BoardId))) continue;
            var collaboration = work.SourceType == "agent-coordination" ? collaborations.FirstOrDefault(x =>
                x.CurrentAgentWorkItemId == work.Id && x.CurrentOrganizationUserId == employee.Id &&
                (x.InitiatorOrganizationUserId == employee.Id && x.InitiatorInstallationId == work.AgentInstallationId ||
                 x.TargetOrganizationUserId == employee.Id && x.TargetInstallationId == work.AgentInstallationId)) : null;
            var attempt = attempts.SingleOrDefault(x => x.AgentWorkItemId == work.Id && x.Attempt == work.AttemptCount);
            var context = contexts.FirstOrDefault(x => x.Id == attempt?.Id);
            Guid? ticketId = context?.WorkItemId ?? context?.RootWorkItemId;
            // Retried deliveries retain a durable root even between claims. Never reuse
            // an earlier attempt's model/progress stream as the new attempt's activity.
            ticketId ??= contexts.FirstOrDefault(x => x.AgentWorkItemId == work.Id)?.RootWorkItemId;
            ticketId ??= collaboration?.SourceWorkItemId;
            ticketId ??= delivery?.WorkItemId;
            if (ticketId is null && work.SourceType == "WorkStageExecution" && Guid.TryParse(work.SourceId, out var stageId))
                ticketId = stageTasks.GetValueOrDefault(stageId) is var mapped && mapped != Guid.Empty ? mapped : null;
            if (ticketId is null && Guid.TryParse(work.SourceId, out var eventId))
                ticketId = tickets.FirstOrDefault(x => x.ClaimEventId == eventId && x.AssignedAgentInstallationId == work.AgentInstallationId)?.Id;
            ticketId ??= sourceTickets.TryGetValue(work.Id, out var sourceTicket) ? sourceTicket : null;
            if (ticketId is null && work.SourceType == "platform-event" && IsTicketEvent(work.Name)) continue;
            var ticket = tickets.FirstOrDefault(x => x.Id == ticketId);
            // Never relabel inaccessible or unresolved ticket work as unrestricted background work.
            if (ticketId.HasValue && (ticket is null || !boardsAllowed.GetValueOrDefault(ticket.BoardId!.Value))) continue;
            if (ticket is null && (work.SourceType == "WorkStageExecution" || !EmployeeAuditAccess.CanRead(people, employee.Id, actor.Id))) continue;
            if (delivery is null && ticket?.Status is (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled)) continue;
            var candidate = Create(ticket, employee, work, attempt) with { Coordination = collaboration, Delivery = delivery };
            if (delivery is not null) candidate = candidate with { Category = "Project", Context = projects.GetValueOrDefault(delivery.ProjectId, "Project"), Stage = delivery.Stage };
            candidates.Add(candidate);
        }
        foreach (var run in standaloneRuns.OrderByDescending(x => x.StartedAt))
        {
            var employee = employees.FirstOrDefault(x => x.AgentInstallationId == run.AgentInstallationId);
            if (employee is null) continue;
            var ticket = tickets.FirstOrDefault(x => x.Id == run.WorkItemId);
            if (run.WorkItemId.HasValue && (ticket is null || !boardsAllowed.GetValueOrDefault(ticket.BoardId!.Value))) continue;
            if (ticket is null && !EmployeeAuditAccess.CanRead(people, employee.Id, actor.Id)) continue;
            if (ticket?.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled) continue;
            candidates.Add(Create(ticket, employee, null, null) with { StandaloneRun = run, Compute = null });
        }
        foreach (var ticket in tickets.Where(x => x.Status is WorkTaskStatus.Running or WorkTaskStatus.WaitingForApproval or WorkTaskStatus.Blocked || compute.ContainsKey(x.Id)))
        {
            if (!boardsAllowed.GetValueOrDefault(ticket.BoardId!.Value)) continue;
            stages.TryGetValue(ticket.Id, out var stage);
            var employeeId = stage is null ? ticket.AssignedEmployeeId : stage.OrganizationUserId;
            var installationId = stage is null ? ticket.AssignedAgentInstallationId : stage.AgentInstallationId;
            var employee = employees.FirstOrDefault(x => employeeId.HasValue ? x.Id == employeeId : x.AgentInstallationId == installationId);
            if (employee is not null && !candidates.Any(x => x.Ticket?.Id == ticket.Id &&
                (!compute.ContainsKey(ticket.Id) || x.Employee.Id == employee.Id))) candidates.Add(Create(ticket, employee, null, null));
        }
        var eligible = candidates.DistinctBy(x => x.Key).Where(x => !projectsOnly || x.Category == "Project").ToList();
        var execution = new Dictionary<string, ExecutionSnapshot>();
        // Resolve queued model calls and stale leases/progress before pagination, so
        // genuinely executing work wins even when there are many waiting project rows.
        // Reuse these snapshots when rendering; provider stream bodies remain on demand.
        foreach (var candidate in eligible.Where(x => BaseState(x, now) == "Executing"))
            execution[candidate.Key] = await ReadExecutionAsync(candidate, actor, people, now, token);
        var ordered = eligible.OrderBy(x => (execution.GetValueOrDefault(x.Key)?.State ?? BaseState(x, now)) == "Executing" ? 0 : 1)
            .ThenBy(x => x.Category == "Project" ? 0 : 1).ThenBy(x => x.Ticket?.CreatedAt ?? x.Work?.CreatedAt ?? x.StandaloneRun!.StartedAt).ThenBy(x => x.Key).ToList();
        var rows = new List<CurrentActivityItem>();
        foreach (var candidate in ordered.Skip(Math.Max(0, offset)).Take(50))
            rows.Add(await MapAsync(candidate, actor, people, now, token, execution.GetValueOrDefault(candidate.Key)));
        return new(now, ordered.Count, rows, offset + rows.Count < ordered.Count ? offset + rows.Count : null);

        Candidate Create(WorkTask? ticket, OrganizationUser employee, AgentWorkItem? work, AgentWorkAttempt? attempt)
        {
            var category = ticket?.Board?.WorkstreamId is not null ? "Project" : ticket is not null ? ticket.Board?.Kind == WorkBoardKind.Personal ? "Personal" : "Work" : work?.SourceType == "chat-turn" ? "Chat" : "Background";
            return new(ticket, employee, work, attempt, category,
                ticket?.Board?.WorkstreamId is Guid project ? projects.GetValueOrDefault(project, "Project") : ticket?.Board?.Name ?? (category == "Chat" ? "Communications" : "Background work"),
                ticket is not null && stages.TryGetValue(ticket.Id, out var stage) ? stage?.StageKey : null,
                Compute: ticket is not null && compute.TryGetValue(ticket.Id, out var activity) &&
                    stages[ticket.Id]?.AgentInstallationId == employee.AgentInstallationId &&
                    work?.Status != AgentWorkStatus.Leased ? activity : null);
        }
    }

    private sealed record ExecutionSnapshot(List<AgentRunLog> Runs, List<CurrentActivityStep> Steps, DateTimeOffset? LastProgress, string State);

    private async Task<ExecutionSnapshot> ReadExecutionAsync(Candidate c, OrganizationUser actor, IReadOnlyList<OrganizationUser> people,
        DateTimeOffset now, CancellationToken token)
    {
        var inspect = EmployeeAuditAccess.CanRead(people, c.Employee.Id, actor.Id);
        List<AgentRunLog> runs = c.StandaloneRun is not null ? [c.StandaloneRun] : c.Attempt is null ? [] : await db.AgentRunLogs.AsNoTracking().Where(x => x.OrganizationId == actor.OrganizationId &&
            x.AgentWorkAttemptId == c.Attempt.Id && x.AgentInstallationId == c.Employee.AgentInstallationId && x.WorkItemId == (c.Ticket == null ? null : c.Ticket.Id))
            .OrderByDescending(x => x.StartedAt).Take(3).ToListAsync(token);
        var run = runs.FirstOrDefault();
        var steps = new List<CurrentActivityStep>();
        DateTimeOffset? lastProgress = null;
        if (inspect && c.Attempt is not null)
        {
            var progress = await feed.ProgressAsync(c.Attempt.Id, c.Ticket?.Id, 3, token);
            steps.AddRange(progress);
            lastProgress = progress.Select(x => (DateTimeOffset?)x.OccurredAt).Max();
        }
        if (inspect)
            foreach (var r in runs)
                steps.Add(new($"model:{r.Id}", "Model", r.CompletedAt.HasValue ? $"Model call {r.Status.ToLowerInvariant()}" : "Waiting for model response", r.CompletedAt ?? r.StartedAt));
        if (run is not null)
        {
            // Provider lifecycle transitions are progress even before the first streamed
            // chunk. A later call must not inherit the original callback's age.
            var modelTime = runs.Max(x => x.CompletedAt ?? x.ProviderStartedAt ?? x.StartedAt);
            if (lastProgress is null || modelTime > lastProgress) lastProgress = modelTime;
            var ids = runs.Select(x => x.Id).ToArray();
            var initialReceipts = ids.Select(id => CSweetDbContext.AuditSourceId($"source:AgentRunLog:{id:D}")).ToArray();
            var chunkTime = await db.AuditEvents.AsNoTracking().Where(x => x.OrganizationId == actor.OrganizationId && x.EntityType == "AgentRunLog" &&
                x.EntityId.HasValue && ids.Contains(x.EntityId.Value) && x.EventType == "model.response.chunk").MaxAsync(x => (DateTimeOffset?)x.OccurredAt, token);
            if (chunkTime > lastProgress || lastProgress is null) lastProgress = chunkTime ?? lastProgress;
            // Source receipts commit with each fragment. Ledger delivery can lag under
            // load; use only timestamps from these exactly bound receipts, never their
            // protected content, to establish freshness before sealing catches up.
            var pendingTime = await db.AuditOutbox.AsNoTracking().Where(x => x.SourceEntityType == nameof(AgentRunLog) &&
                x.SourceEntityId.HasValue && ids.Contains(x.SourceEntityId.Value) && x.DeliveredAt == null && !initialReceipts.Contains(x.Id))
                .MaxAsync(x => (DateTimeOffset?)x.CreatedAt, token);
            if (pendingTime > lastProgress) lastProgress = pendingTime;
        }
        var state = BaseState(c, now);
        if (c.Compute is { } compute) return new(runs, steps, compute.ObservedAt, compute.State);
        if (HasReturnedResult(c)) lastProgress = c.Work!.CompletedAt ?? c.Attempt?.FinishedAt ?? lastProgress;
        if (state == "Executing" && run?.Status == "Queued" && run.CompletedAt is null) state = "Waiting";
        else if (state == "Executing" && now - (lastProgress ?? c.Attempt?.ClaimedAt ?? c.StandaloneRun!.StartedAt) > TimeSpan.FromMinutes(5)) state = "Unconfirmed";
        return new(runs, steps, lastProgress, state);
    }

    private async Task<CurrentActivityItem> MapAsync(Candidate c, OrganizationUser actor, IReadOnlyList<OrganizationUser> people,
        DateTimeOffset now, CancellationToken token, ExecutionSnapshot? snapshot = null)
    {
        var inspect = EmployeeAuditAccess.CanRead(people, c.Employee.Id, actor.Id);
        snapshot ??= await ReadExecutionAsync(c, actor, people, now, token);
        var run = snapshot.Runs.FirstOrDefault();
        var state = snapshot.State;
        var lastProgress = snapshot.LastProgress;
        var recent = snapshot.Steps.OrderByDescending(x => x.OccurredAt).ToArray();
        var (kind, label) = DescribeActivity(c);
        var action = state switch
        {
            "Waiting" => (c.Coordination is null && c.Delivery is null ? Safe(c.Ticket?.WaitingReason) : null) ??
                (c.Coordination is null && c.Delivery is null && c.Ticket?.Status == WorkTaskStatus.WaitingForApproval ? "Waiting for review or approval" :
                    c.Work is null && c.StandaloneRun is null && c.Ticket?.Status == WorkTaskStatus.Running &&
                    c.Ticket.Board?.Kind == WorkBoardKind.Personal && c.Ticket.ClaimEventId is null && c.Ticket.ClaimExpiresAt is null
                        ? "Waiting for follow-up" : run?.Status == "Queued" ? "Waiting for model capacity" : "Waiting for an agent claim"),
            "Finalizing" => "Agent finished; awaiting workflow result processing",
            "Needs attention" when HasReturnedResult(c) => "Agent finished; workflow result processing is delayed",
            "Needs attention" => Safe(c.Ticket?.BlockReason) ?? "Work is blocked",
            "Recovering" => "Execution lease expired; awaiting recovery",
            "Unconfirmed" => "No task progress reported in the last five minutes",
            _ => inspect ? recent.FirstOrDefault(x => x.Kind == "Workspace")?.Text ?? recent.FirstOrDefault(x => x.Kind == "Progress")?.Text ??
                recent.FirstOrDefault()?.Text ?? label ?? "Executing work" : label ?? "Executing work"
        };
        if (c.Compute is { } compute) action = compute.Action;
        var provider = inspect && run is not null ? await db.LlmProviderProfiles.AsNoTracking().Where(x => x.Id == run.ProviderProfileId).Select(x => x.Name).SingleOrDefaultAsync(token) : null;
        if (inspect && c.Compute is null && state == "Waiting" && run?.Status == "Queued")
        {
            var active = await db.AgentRunLogs.AsNoTracking().Where(x => x.OrganizationId == actor.OrganizationId &&
                x.ProviderProfileId == run.ProviderProfileId && x.CompletedAt == null && x.Status == "Running" &&
                x.AgentInstallationId != null && x.AgentWorkItemId != run.AgentWorkItemId)
                .OrderBy(x => x.StartedAt).Take(8).ToListAsync(token);
            var visible = active.FirstOrDefault(x => people.Any(p => p.AgentInstallationId == x.AgentInstallationId &&
                EmployeeAuditAccess.CanRead(people, p.Id, actor.Id)));
            // A task's title still requires board access; describe only the authorized
            // employee and model activity, without reading another task's content.
            var blocker = people.FirstOrDefault(x => x.AgentInstallationId == visible?.AgentInstallationId);
            action = blocker is null ? "Waiting for model capacity" : $"Waiting for model capacity; {blocker.DisplayName} is generating a response";
        }
        else if (inspect && c.Compute is null && c.Work?.Status == AgentWorkStatus.Pending)
        {
            var busy = await db.AgentWorkItems.AsNoTracking().AnyAsync(x => x.OrganizationId == actor.OrganizationId.ToString() &&
                x.AgentInstallationId == c.Employee.AgentInstallationId && x.Status == AgentWorkStatus.Leased && x.Id != c.Work.Id, token);
            if (busy) action = "Queued while this employee handles another request";
        }
        CurrentActivityParticipant? collaborator = null;
        if (inspect && c.Coordination is { } session)
        {
            var otherId = session.InitiatorOrganizationUserId == c.Employee.Id ? session.TargetOrganizationUserId : session.InitiatorOrganizationUserId;
            var otherInstallation = session.InitiatorOrganizationUserId == c.Employee.Id ? session.TargetInstallationId : session.InitiatorInstallationId;
            var other = people.FirstOrDefault(x => x.Id == otherId && x.AgentInstallationId == otherInstallation && x.EmployeeType == EmployeeType.Agent);
            if (other is not null && EmployeeAuditAccess.CanRead(people, other.Id, actor.Id))
                collaborator = new(other.Id, other.DisplayName, other.Role?.Name);
        }
        var title = c.Ticket?.Title ?? (c.Delivery is { } delivery ? Safe($"{delivery.Scope}: {delivery.Name}") : null) ?? label ?? (kind switch { "Collaboration" => "Collaborating", "Review" => "Reviewing work",
            "Chat" => "Responding in Communications", "Model" => "Generating a response", _ => "Background activity" });
        return new(c.Key, c.Ticket?.Id, c.Ticket?.BoardId ?? c.Delivery?.BoardId, c.Work?.Id, c.Attempt?.Id, c.Attempt?.Attempt ?? 0,
            c.Employee.Id, c.Employee.DisplayName, title,
            c.Ticket?.Identifier, c.Category, c.Context, state, action, c.Attempt?.ClaimedAt ?? c.StandaloneRun?.StartedAt ?? c.Ticket?.UpdatedAt ?? c.Work!.CreatedAt,
            lastProgress, HasReturnedResult(c) ? null : c.Attempt?.LeaseExpiresAt, provider, inspect ? run?.Model : null, inspect,
            recent.Where(x => x.Text != action).OrderByDescending(x => x.Kind == "Workspace").Take(2).Reverse().ToArray(), c.StandaloneRun?.Id,
            kind, c.Employee.Role?.Name, collaborator, inspect ? c.Work?.Name : null,
            c.Ticket?.Board?.Kind == WorkBoardKind.Personal ? c.Ticket.Board.OwnerOrganizationUserId : null,
            c.Delivery?.ProjectId ?? c.Ticket?.Board?.WorkstreamId, inspect ? Safe(c.Coordination?.Subject) : null, label,
            Safe(c.Ticket?.Description), inspect ? c.Compute?.Progress : null);
    }

    private static (string Kind, string? Label) DescribeActivity(Candidate c)
    {
        if (c.Work?.SourceType == "agent-coordination") return ("Collaboration", null);
        if (c.Category == "Chat") return ("Chat", null);
        // Stage names describe the assignment, never model reasoning or completion.
        var stage = c.Stage switch
        {
            "technical-review" => ("Review", "Technical review"),
            "manager-review" or "producer-review" => ("Review", "Delivery acceptance review"),
            "build-readiness" => ("Build", "Checking build readiness"),
            "quality" => ("Testing", "Testing delivery"),
            _ => ((string, string)?)null
        };
        if (stage.HasValue) return stage.Value;
        return c.Work?.Name switch
        {
            "com.csweet.agent.attention.review-due.v1" => ("Review", "Reviewing work"),
            "com.csweet.project-health.review-due.v1" => ("Review", "Reviewing project health"),
            "com.csweet.management.incident.owner-report-requested.v1" => ("Update", "Preparing a project status report"),
            "com.csweet.work.item.discussion.changed.v1" => ("Discussion", "Responding to a task discussion"),
            "com.csweet.work-delivery.changed.v1" => ("Update", "Checking delivery updates"),
            "com.csweet.compute.changed.v1" or "com.csweet.compute.available.v1" => ("Update", "Checking compute updates"),
            "com.csweet.repository.provisioning.changed.v1" => ("Update", "Checking repository readiness"),
            "work.execution.build-readiness.v1" => ("Build", "Checking build readiness"),
            "work.execution.compute-test.v1" => ("Testing", "Testing delivery"),
            "software.architecture-review.v1" => ("Review", "Architecture review"),
            "work.execution.run" => ("Work", "Executing assigned work"),
            _ => (c.Ticket is not null ? "Work" : c.StandaloneRun is not null ? "Model" : "Background", null)
        };
    }

    // Collaboration and aggregate review can continue while the source task awaits
    // a decision. Their own delivery lease, rather than the ticket state, proves activity.
    private static bool HasReturnedResult(Candidate c) => c.Work?.Status is AgentWorkStatus.Completed or AgentWorkStatus.DeadLetter or AgentWorkStatus.Cancelled;

    private static string BaseState(Candidate c, DateTimeOffset now) => c.Compute is not null ? c.Compute.State : HasReturnedResult(c) ?
        now - (c.Work!.CompletedAt ?? c.Attempt?.FinishedAt ?? c.Work.CreatedAt) > TimeSpan.FromMinutes(5) ? "Needs attention" : "Finalizing" :
        c.Coordination is not null || c.Delivery is not null ?
        c.Work?.Status == AgentWorkStatus.Pending ? "Waiting" : c.Attempt is { FinishedAt: null } && c.Attempt.LeaseExpiresAt > now ? "Executing" : "Recovering" :
        c.Ticket?.Status == WorkTaskStatus.Blocked ? "Needs attention" :
        c.Ticket?.Status == WorkTaskStatus.WaitingForApproval || c.Ticket?.WaitingReason is not null || c.Ticket?.NextReviewAt is not null ? "Waiting" :
        c.Work is null && c.StandaloneRun is null && c.Ticket?.Status == WorkTaskStatus.Running &&
            c.Ticket.Board?.Kind == WorkBoardKind.Personal && c.Ticket.ClaimEventId is null && c.Ticket.ClaimExpiresAt is null ? "Waiting" :
        c.Work?.Status == AgentWorkStatus.Pending || c.StandaloneRun?.Status == "Queued" ? "Waiting" :
        c.StandaloneRun is not null ? "Executing" :
        c.Attempt is { FinishedAt: null } && c.Attempt.LeaseExpiresAt > now ? "Executing" : "Recovering";

    internal async Task<(OrganizationUser Actor, List<OrganizationUser> People)> ActorAsync(Guid organizationId, Guid userId, CancellationToken token)
    {
        var people = await db.CoreOrganizationUsers.AsNoTracking().Include(x => x.Role).Where(x => x.OrganizationId == organizationId && x.IsActive).ToListAsync(token);
        return (people.SingleOrDefault(x => x.ApplicationUserId == userId && x.EmployeeType == EmployeeType.Human)
            ?? throw new UnauthorizedAccessException(), people);
    }

    public async Task<CurrentActivityFeedPage> FeedAsync(Guid organizationId, Guid userId, Guid employeeId,
        Guid attemptId, Guid? workItemId, long afterSequence, CancellationToken token)
    {
        var (actor, people) = await ActorAsync(organizationId, userId, token);
        if (!EmployeeAuditAccess.CanRead(people, employeeId, actor.Id)) throw new UnauthorizedAccessException();
        var employee = people.Single(x => x.Id == employeeId);
        var attempt = await db.AgentWorkAttempts.AsNoTracking().Include(x => x.AgentWorkItem).SingleOrDefaultAsync(x => x.Id == attemptId &&
            x.AgentWorkItem!.OrganizationId == organizationId.ToString() && x.AgentWorkItem.AgentInstallationId == employee.AgentInstallationId, token)
            ?? throw new KeyNotFoundException();
        var context = await db.WorkExecutionContexts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attemptId && x.OrganizationId == organizationId, token);
        DeliveryActivity? delivery = null;
        if (attempt.AgentWorkItem!.SourceType == "WorkDeliveryStage")
        {
            delivery = (await ReadDeliveriesAsync(organizationId, [attempt.AgentWorkItemId], token)).FirstOrDefault(x =>
                x.InstallationId == employee.AgentInstallationId && x.EmployeeId == employeeId &&
                Guid.TryParse(attempt.AgentWorkItem.SourceId, out var stageId) && x.StageId == stageId)
                ?? throw new KeyNotFoundException();
            if (!(await authorization.AuthorizeAsync(organizationId, GrantSubjectKind.OrganizationUser, actor.Id,
                WorkBoardActions.Read, GrantScopeKind.Board, delivery.BoardId, token)).Allowed) throw new UnauthorizedAccessException();
        }
        var collaborationTask = attempt.AgentWorkItem!.SourceType == "agent-coordination" ? await db.AgentCoordinationSessions.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.CurrentAgentWorkItemId == attempt.AgentWorkItemId &&
                x.CurrentOrganizationUserId == employeeId &&
                (x.InitiatorOrganizationUserId == employeeId && x.InitiatorInstallationId == employee.AgentInstallationId ||
                 x.TargetOrganizationUserId == employeeId && x.TargetInstallationId == employee.AgentInstallationId))
            .Select(x => x.SourceWorkItemId).SingleOrDefaultAsync(token) : null;
        var sourceTickets = await ReadSourceTicketsAsync(organizationId, [attempt.AgentWorkItem], [employee], token);
        Guid? sourceTicket = sourceTickets.TryGetValue(attempt.AgentWorkItemId, out var boundTicket) ? boundTicket : null;
        // A task must be explicitly linked to this attempt, now or in an immutable effort interval.
        if (workItemId.HasValue)
        {
            if (context?.WorkItemId != workItemId && context?.RootWorkItemId != workItemId && collaborationTask != workItemId && delivery?.WorkItemId != workItemId && sourceTicket != workItemId && !await db.WorkExecutionIntervals.AnyAsync(x =>
                x.OrganizationId == organizationId && x.AgentWorkAttemptId == attemptId && x.WorkItemId == workItemId, token)) throw new KeyNotFoundException();
            var task = await db.CoreWorkTasks.AsNoTracking().Include(x => x.Board).SingleOrDefaultAsync(x => x.Id == workItemId && x.OrganizationId == organizationId && x.ArchivedAt == null, token);
            if (task?.Board is not { ArchivedAt: null } board) throw new KeyNotFoundException();
            if (!(await authorization.AuthorizeAsync(organizationId, GrantSubjectKind.OrganizationUser, actor.Id,
                board.Kind == WorkBoardKind.Personal ? PersonalTodoActions.Read : WorkBoardActions.Read, GrantScopeKind.Board, board.Id, token)).Allowed)
                throw new UnauthorizedAccessException();
        }
        else if (context?.RootWorkItemId is not null || context?.WorkItemId is not null || collaborationTask is not null || delivery?.WorkItemId is not null || sourceTicket is not null || attempt.AgentWorkItem!.SourceType == "WorkStageExecution")
            throw new UnauthorizedAccessException();
        return await feed.ReadAsync(organizationId, employeeId, attempt, workItemId, Math.Max(0, afterSequence), token);
    }

    private static string? Safe(string? text)
    {
        if (text is null) return null;
        var safe = AuditPayloadSanitizer.RedactText(text);
        return safe.Length <= 500 ? safe : safe[..500] + "…";
    }

    public async Task<CurrentActivityFeedPage> ModelFeedAsync(Guid organizationId, Guid userId, Guid employeeId,
        Guid runId, long afterSequence, CancellationToken token)
    {
        var (actor, people) = await ActorAsync(organizationId, userId, token);
        if (!EmployeeAuditAccess.CanRead(people, employeeId, actor.Id)) throw new UnauthorizedAccessException();
        var installation = people.Single(x => x.Id == employeeId).AgentInstallationId;
        var run = await db.AgentRunLogs.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.Id == runId && x.AgentInstallationId == installation && x.AgentWorkAttemptId == null, token) ?? throw new KeyNotFoundException();
        if (run.WorkItemId is Guid taskId)
        {
            var task = await db.CoreWorkTasks.AsNoTracking().Include(x => x.Board).SingleOrDefaultAsync(x => x.Id == taskId && x.OrganizationId == organizationId && x.ArchivedAt == null, token);
            if (task?.Board is not { ArchivedAt: null } board) throw new KeyNotFoundException();
            if (!(await authorization.AuthorizeAsync(organizationId, GrantSubjectKind.OrganizationUser, actor.Id,
                board.Kind == WorkBoardKind.Personal ? PersonalTodoActions.Read : WorkBoardActions.Read, GrantScopeKind.Board, board.Id, token)).Allowed)
                throw new UnauthorizedAccessException();
        }
        return await feed.ReadAsync(organizationId, employeeId, null, run.WorkItemId, Math.Max(0, afterSequence), token, runId);
    }
}
