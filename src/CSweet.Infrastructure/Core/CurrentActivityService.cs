using CSweet.Application.Security;
using CSweet.Contracts.Core;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

/// <summary>Read-only projection of current execution. No employee-wide inference guesses.</summary>
public sealed class CurrentActivityService(CSweetDbContext db, IScopedActionAuthorizationService authorization,
    CurrentActivityFeedReader feed, TimeProvider clock)
{
    private sealed record Candidate(WorkTask? Ticket, OrganizationUser Employee, AgentWorkItem? Work,
        AgentWorkAttempt? Attempt, string Category, string Context, string? Stage, AgentRunLog? StandaloneRun = null)
    {
        public string Key => Ticket is null ? StandaloneRun is not null ? $"model:{StandaloneRun.Id}" : $"work:{Work!.Id}" : $"task:{Ticket.Id}:{Employee.Id}";
    }

    public async Task<CurrentActivityPage> ReadAsync(Guid organizationId, Guid userId, int offset = 0,
        bool projectsOnly = false, CancellationToken token = default)
    {
        var (actor, people) = await ActorAsync(organizationId, userId, token);
        var now = clock.GetUtcNow();
        var employees = people.Where(x => x.EmployeeType == EmployeeType.Agent && x.AgentInstallationId.HasValue).ToArray();
        var installations = employees.Select(x => x.AgentInstallationId!.Value).ToArray();
        var org = organizationId.ToString();
        // Only current work is discovered. Payloads and historical attempts are not materialized.
        var works = await db.AgentWorkItems.AsNoTracking().Where(x => x.OrganizationId == org &&
            installations.Contains(x.AgentInstallationId) && (x.Status == AgentWorkStatus.Leased || x.Status == AgentWorkStatus.Pending))
            .Select(x => new AgentWorkItem { Id = x.Id, AgentInstallationId = x.AgentInstallationId, Status = x.Status,
                SourceType = x.SourceType, SourceId = x.SourceId, Name = x.Name, AttemptCount = x.AttemptCount,
                CreatedAt = x.CreatedAt, AvailableAt = x.AvailableAt }).ToListAsync(token);
        var workIds = works.Select(x => x.Id).ToArray();
        var standaloneRuns = await db.AgentRunLogs.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            x.AgentInstallationId.HasValue && installations.Contains(x.AgentInstallationId.Value) && x.AgentWorkAttemptId == null &&
            x.CompletedAt == null && (x.Status == "Running" || x.Status == "Queued") &&
            (!x.AgentWorkItemId.HasValue || !workIds.Contains(x.AgentWorkItemId.Value)))
            .Select(x => new AgentRunLog { Id = x.Id, AgentInstallationId = x.AgentInstallationId, EmployeeId = x.EmployeeId,
                WorkItemId = x.WorkItemId, StartedAt = x.StartedAt, Status = x.Status, ProviderProfileId = x.ProviderProfileId, Model = x.Model }).ToListAsync(token);
        var attempts = await db.AgentWorkAttempts.AsNoTracking().Where(x => workIds.Contains(x.AgentWorkItemId) && x.FinishedAt == null).ToListAsync(token);
        var contexts = await db.WorkExecutionContexts.AsNoTracking().Where(x => x.OrganizationId == organizationId && workIds.Contains(x.AgentWorkItemId)).ToListAsync(token);
        var executions = await db.WorkItemExecutions.AsNoTracking().Include(x => x.Stages)
            .Where(x => x.SprintExecution!.OrganizationId == organizationId &&
                (x.SprintExecution.Status == WorkSprintExecutionStatus.Active || x.SprintExecution.Status == WorkSprintExecutionStatus.Paused)).ToListAsync(token);
        var stages = executions.ToDictionary(x => x.WorkItemId, x => x.Stages.Where(s => s.StageKey == x.CurrentStageKey)
            .OrderByDescending(s => s.CreatedAt).FirstOrDefault());
        var focusedIds = contexts.Select(x => x.WorkItemId ?? x.RootWorkItemId).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var tickets = await db.CoreWorkTasks.AsNoTracking().Include(x => x.Board).Where(x => x.OrganizationId == organizationId &&
            x.ArchivedAt == null && x.Board != null && x.Board.ArchivedAt == null &&
            (x.Status == WorkTaskStatus.Running || x.Status == WorkTaskStatus.WaitingForApproval || x.Status == WorkTaskStatus.Blocked || focusedIds.Contains(x.Id)))
            .ToListAsync(token);
        var stageIds = works.Where(x => x.SourceType == "WorkStageExecution" && Guid.TryParse(x.SourceId, out _)).Select(x => Guid.Parse(x.SourceId!)).ToArray();
        var stageTasks = await db.WorkStageExecutions.AsNoTracking().Where(x => stageIds.Contains(x.Id) && x.ItemExecution!.SprintExecution!.OrganizationId == organizationId)
            .Select(x => new { x.Id, x.ItemExecution!.WorkItemId }).ToDictionaryAsync(x => x.Id, x => x.WorkItemId, token);
        var missingIds = stageTasks.Values.Concat(standaloneRuns.Where(x => x.WorkItemId.HasValue).Select(x => x.WorkItemId!.Value))
            .Except(tickets.Select(x => x.Id)).ToArray();
        tickets.AddRange(await db.CoreWorkTasks.AsNoTracking().Include(x => x.Board).Where(x => missingIds.Contains(x.Id) &&
            x.OrganizationId == organizationId && x.ArchivedAt == null && x.Board != null && x.Board.ArchivedAt == null).ToListAsync(token));
        var projects = await db.Workstreams.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .Select(x => new { x.Id, x.Name }).ToDictionaryAsync(x => x.Id, x => x.Name, token);
        var boardsAllowed = new Dictionary<Guid, bool>();
        foreach (var board in tickets.Select(x => x.Board!).DistinctBy(x => x.Id))
        {
            var action = board.Kind == WorkBoardKind.Personal ? PersonalTodoActions.Read : WorkBoardActions.Read;
            boardsAllowed[board.Id] = (await authorization.AuthorizeAsync(organizationId, GrantSubjectKind.OrganizationUser,
                actor.Id, action, GrantScopeKind.Board, board.Id, token)).Allowed;
        }
        var candidates = new List<Candidate>();
        foreach (var work in works.OrderByDescending(x => x.Status == AgentWorkStatus.Leased).ThenByDescending(x => x.CreatedAt))
        {
            var employee = employees.First(x => x.AgentInstallationId == work.AgentInstallationId);
            var attempt = attempts.SingleOrDefault(x => x.AgentWorkItemId == work.Id && x.Attempt == work.AttemptCount);
            var context = contexts.FirstOrDefault(x => x.Id == attempt?.Id);
            Guid? ticketId = context?.WorkItemId ?? context?.RootWorkItemId;
            // Retried deliveries retain a durable root even between claims. Never reuse
            // an earlier attempt's model/progress stream as the new attempt's activity.
            ticketId ??= contexts.FirstOrDefault(x => x.AgentWorkItemId == work.Id)?.RootWorkItemId;
            if (ticketId is null && work.SourceType == "WorkStageExecution" && Guid.TryParse(work.SourceId, out var stageId))
                ticketId = stageTasks.GetValueOrDefault(stageId) is var mapped && mapped != Guid.Empty ? mapped : null;
            if (ticketId is null && Guid.TryParse(work.SourceId, out var eventId))
                ticketId = tickets.FirstOrDefault(x => x.ClaimEventId == eventId && x.AssignedAgentInstallationId == work.AgentInstallationId)?.Id;
            var ticket = tickets.FirstOrDefault(x => x.Id == ticketId);
            // Never relabel inaccessible or unresolved ticket work as unrestricted background work.
            if (ticketId.HasValue && (ticket is null || !boardsAllowed.GetValueOrDefault(ticket.BoardId!.Value))) continue;
            if (ticket is null && (work.SourceType == "WorkStageExecution" || !EmployeeAuditAccess.CanRead(people, employee.Id, actor.Id))) continue;
            if (ticket?.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled) continue;
            candidates.Add(Create(ticket, employee, work, attempt));
        }
        foreach (var run in standaloneRuns.OrderByDescending(x => x.StartedAt))
        {
            var employee = employees.FirstOrDefault(x => x.AgentInstallationId == run.AgentInstallationId);
            if (employee is null) continue;
            var ticket = tickets.FirstOrDefault(x => x.Id == run.WorkItemId);
            if (run.WorkItemId.HasValue && (ticket is null || !boardsAllowed.GetValueOrDefault(ticket.BoardId!.Value))) continue;
            if (ticket is null && !EmployeeAuditAccess.CanRead(people, employee.Id, actor.Id)) continue;
            if (ticket?.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled) continue;
            candidates.Add(Create(ticket, employee, null, null) with { StandaloneRun = run });
        }
        foreach (var ticket in tickets.Where(x => x.Status is WorkTaskStatus.Running or WorkTaskStatus.WaitingForApproval or WorkTaskStatus.Blocked))
        {
            if (!boardsAllowed.GetValueOrDefault(ticket.BoardId!.Value)) continue;
            stages.TryGetValue(ticket.Id, out var stage);
            var employeeId = stage is null ? ticket.AssignedEmployeeId : stage.OrganizationUserId;
            var installationId = stage is null ? ticket.AssignedAgentInstallationId : stage.AgentInstallationId;
            var employee = employees.FirstOrDefault(x => employeeId.HasValue ? x.Id == employeeId : x.AgentInstallationId == installationId);
            if (employee is not null && !candidates.Any(x => x.Ticket?.Id == ticket.Id)) candidates.Add(Create(ticket, employee, null, null));
        }
        var ordered = candidates.DistinctBy(x => x.Key).Where(x => !projectsOnly || x.Category == "Project")
            .OrderBy(x => x.Category == "Project" ? 0 : 1).ThenBy(x => x.Ticket?.CreatedAt ?? x.Work?.CreatedAt ?? x.StandaloneRun!.StartedAt).ThenBy(x => x.Key).ToList();
        var rows = new List<CurrentActivityItem>();
        // Hydrate just the visible page, not every model stream in the business.
        foreach (var candidate in ordered.Skip(Math.Max(0, offset)).Take(50)) rows.Add(await MapAsync(candidate, actor, people, now, token));
        return new(now, ordered.Count, rows, offset + rows.Count < ordered.Count ? offset + rows.Count : null);

        Candidate Create(WorkTask? ticket, OrganizationUser employee, AgentWorkItem? work, AgentWorkAttempt? attempt)
        {
            var category = ticket?.Board?.WorkstreamId is not null ? "Project" : ticket is not null ? ticket.Board?.Kind == WorkBoardKind.Personal ? "Personal" : "Work" : work?.SourceType == "chat-turn" ? "Chat" : "Background";
            return new(ticket, employee, work, attempt, category,
                ticket?.Board?.WorkstreamId is Guid project ? projects.GetValueOrDefault(project, "Project") : ticket?.Board?.Name ?? (category == "Chat" ? "Communications" : "Background work"),
                ticket is not null && stages.TryGetValue(ticket.Id, out var stage) ? stage?.StageKey : null);
        }
    }

    private async Task<CurrentActivityItem> MapAsync(Candidate c, OrganizationUser actor, IReadOnlyList<OrganizationUser> people,
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
            var ids = runs.Select(x => x.Id).ToArray();
            var chunkTime = await db.AuditEvents.AsNoTracking().Where(x => x.OrganizationId == actor.OrganizationId && x.EntityType == "AgentRunLog" &&
                x.EntityId.HasValue && ids.Contains(x.EntityId.Value) && x.EventType == "model.response.chunk").MaxAsync(x => (DateTimeOffset?)x.OccurredAt, token);
            if (chunkTime > lastProgress || lastProgress is null) lastProgress = chunkTime ?? lastProgress;
        }
        var state = BaseState(c, now);
        if (state == "Executing" && run?.Status == "Queued" && run.CompletedAt is null) state = "Waiting";
        else if (state == "Executing" && now - (lastProgress ?? c.Attempt?.ClaimedAt ?? c.StandaloneRun!.StartedAt) > TimeSpan.FromMinutes(5)) state = "Unconfirmed";
        var recent = steps.OrderByDescending(x => x.OccurredAt).ToArray();
        var action = state switch
        {
            "Waiting" => Safe(c.Ticket?.WaitingReason) ?? (c.Ticket?.Status == WorkTaskStatus.WaitingForApproval ? "Waiting for review or approval" : run?.Status == "Queued" ? "Waiting for model capacity" : "Waiting for an agent claim"),
            "Needs attention" => Safe(c.Ticket?.BlockReason) ?? "Work is blocked",
            "Recovering" => "Execution lease expired; awaiting recovery",
            "Unconfirmed" => "No task progress reported in the last five minutes",
            _ => inspect ? recent.FirstOrDefault()?.Text ?? (c.Stage is null ? "Executing work" : $"Executing {c.Stage}") : "Executing work"
        };
        var provider = inspect && run is not null ? await db.LlmProviderProfiles.AsNoTracking().Where(x => x.Id == run.ProviderProfileId).Select(x => x.Name).SingleOrDefaultAsync(token) : null;
        if (inspect && state == "Waiting" && run?.Status == "Queued")
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
        else if (inspect && c.Work?.Status == AgentWorkStatus.Pending)
        {
            var busy = await db.AgentWorkItems.AsNoTracking().AnyAsync(x => x.OrganizationId == actor.OrganizationId.ToString() &&
                x.AgentInstallationId == c.Employee.AgentInstallationId && x.Status == AgentWorkStatus.Leased && x.Id != c.Work.Id, token);
            if (busy) action = "Queued while this employee handles another request";
        }
        return new(c.Key, c.Ticket?.Id, c.Ticket?.BoardId, c.Work?.Id, c.Attempt?.Id, c.Attempt?.Attempt ?? 0,
            c.Employee.Id, c.Employee.DisplayName, c.Ticket?.Title ?? (c.Category == "Chat" ? "Responding in Communications" : c.Work?.Name ?? "Agent model activity"),
            c.Ticket?.Identifier, c.Category, c.Context, state, action, c.Attempt?.ClaimedAt ?? c.StandaloneRun?.StartedAt ?? c.Ticket?.UpdatedAt ?? c.Work!.CreatedAt,
            lastProgress, c.Attempt?.LeaseExpiresAt, provider, inspect ? run?.Model : null, inspect,
            recent.Where(x => x.Text != action).Take(2).Reverse().ToArray(), c.StandaloneRun?.Id);
    }

    private static string BaseState(Candidate c, DateTimeOffset now) => c.Ticket?.Status == WorkTaskStatus.Blocked ? "Needs attention" :
        c.Ticket?.Status == WorkTaskStatus.WaitingForApproval || c.Ticket?.WaitingReason is not null || c.Ticket?.NextReviewAt is not null ? "Waiting" :
        c.Work?.Status == AgentWorkStatus.Pending || c.StandaloneRun?.Status == "Queued" ? "Waiting" :
        c.StandaloneRun is not null ? "Executing" :
        c.Attempt is { FinishedAt: null } && c.Attempt.LeaseExpiresAt > now ? "Executing" : "Recovering";

    internal async Task<(OrganizationUser Actor, List<OrganizationUser> People)> ActorAsync(Guid organizationId, Guid userId, CancellationToken token)
    {
        var people = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.IsActive).ToListAsync(token);
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
        // A task must be explicitly linked to this attempt, now or in an immutable effort interval.
        if (workItemId.HasValue)
        {
            if (context?.WorkItemId != workItemId && context?.RootWorkItemId != workItemId && !await db.WorkExecutionIntervals.AnyAsync(x =>
                x.OrganizationId == organizationId && x.AgentWorkAttemptId == attemptId && x.WorkItemId == workItemId, token)) throw new KeyNotFoundException();
            var task = await db.CoreWorkTasks.AsNoTracking().Include(x => x.Board).SingleOrDefaultAsync(x => x.Id == workItemId && x.OrganizationId == organizationId && x.ArchivedAt == null, token);
            if (task?.Board is not { ArchivedAt: null } board) throw new KeyNotFoundException();
            if (!(await authorization.AuthorizeAsync(organizationId, GrantSubjectKind.OrganizationUser, actor.Id,
                board.Kind == WorkBoardKind.Personal ? PersonalTodoActions.Read : WorkBoardActions.Read, GrantScopeKind.Board, board.Id, token)).Allowed)
                throw new UnauthorizedAccessException();
        }
        else if (context?.RootWorkItemId is not null || context?.WorkItemId is not null || attempt.AgentWorkItem!.SourceType == "WorkStageExecution")
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
