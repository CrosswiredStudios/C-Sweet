using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Communications;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Core;
using CSweet.Domain.Notifications;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.SourceControl;

public sealed partial class TaskDeliveryService(CSweetDbContext db, TimeProvider clock)
{
    // All approval changes and the transition to Merging serialize on the same durable scope.
    public async Task LockAsync(Guid org, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(org.ToByteArray(), 0)})", ct);
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RecordFailureAsync(Guid org, Guid id, string diagnostic, CancellationToken ct)
    {
        var review = await db.TaskDeliveryReviews.SingleAsync(x => x.OrganizationId == org && x.Id == id, ct);
        diagnostic = diagnostic.Length <= 2048 ? diagnostic : diagnostic[..2048];
        review.UpdatedAt = clock.GetUtcNow(); // Fair bounded discovery across failed and ready operations.
        if (review.Failure != diagnostic)
        {
            review.Failure = diagnostic; review.Revision++;
            if (review.DecisionId is { } questionId)
            {
                var question = await db.ExecutiveDecisions.SingleOrDefaultAsync(x => x.Id == questionId && x.OrganizationId == org && x.Status == ExecutiveDecisionStatus.Pending, ct);
                if (question is not null) { question.Status = ExecutiveDecisionStatus.Superseded; question.UpdatedAt = clock.GetUtcNow(); }
                review.DecisionId = null;
            }
            var task = await TaskAsync(org, review.TaskId, ct);
            task.BlockReason = diagnostic; task.Revision++; task.UpdatedAt = clock.GetUtcNow();
            Queue(review, review.DeveloperInstallationId, TaskDeliveryCapabilities.Changed);
            await QueueUiAsync(task, ct);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<TaskReviewResult> SubmitAsync(Guid org, Guid installation, SubmitTaskReviewRequest request, CancellationToken ct)
    {
        if (request.Summary is not { Length: > 0 and <= 4096 } || string.IsNullOrWhiteSpace(request.IdempotencyKey)) throw new ArgumentException("Provide bounded task evidence and a stable key.");
        var task = await TaskAsync(org, request.TaskItemId, ct);
        var root = await TaskAsync(org, request.RootItemId, ct);
        var plan = Plan(task);
        await RequireDeveloperAsync(org, installation, root, ct);
        if (plan?.RootItemId != root.Id || task.Kind != WorkItemKind.Task || task.ParentWorkTaskId is null ||
            task.AssignedAgentInstallationId != installation || task.BoardId != root.BoardId)
            throw new UnauthorizedAccessException("The task is not part of the claimed plan.");
        var publication = await db.SourceControlPublications.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == request.PublicationId && x.OrganizationId == org, ct) ?? throw new KeyNotFoundException("Task publication not found.");
        var workspace = await db.SourceControlWorkspaces.AsNoTracking().SingleAsync(x => x.Id == publication.WorkspaceId && x.OrganizationId == org, ct);
        if (workspace.WorkItemId != task.Id || workspace.AgentInstallationId != installation || workspace.AssignmentRevision != task.AssignmentRevision ||
            publication.Status is SourceControlPublicationStatus.Failed or SourceControlPublicationStatus.Superseded)
            throw new UnauthorizedAccessException("Review requires this task's current published branch.");
        var existing = await db.TaskDeliveryReviews.SingleOrDefaultAsync(x => x.OrganizationId == org && x.TaskId == task.Id && x.PublicationId == publication.Id, ct);
        if (existing is not null) return Result(existing, task);
        if (task.Status != WorkTaskStatus.Running) throw new InvalidOperationException("Only a running task can enter testing.");
        var developer = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.OrganizationId == org && x.AgentInstallationId == installation && x.IsActive && x.ArchivedAt == null, ct);
        var manager = await ManagerAsync(org, developer, ct, root.BoardId);
        var projectId = await db.WorkBoards.Where(x => x.Id == root.BoardId).Select(x => x.WorkstreamId).SingleAsync(ct);
        var teamInstallations = await (from member in db.TeamMemberships.AsNoTracking()
            join person in db.CoreOrganizationUsers.AsNoTracking() on member.OrganizationUserId equals person.Id
            join agent in db.AgentInstallations.AsNoTracking().Include(x => x.Grant) on person.AgentInstallationId equals agent.Id
            where member.OrganizationId == org && member.TeamId == workspace.TeamId && member.EndedAt == null &&
                person.OrganizationId == org && person.IsActive && person.ArchivedAt == null && agent.IsEnabled && agent.RevisionStatus == PluginRevisionStatus.Active &&
                (projectId == null || db.ProjectParticipants.Any(x => x.WorkstreamId == projectId && x.OrganizationUserId == person.Id && x.RemovedAt == null))
            select agent).ToListAsync(ct);
        var qa = teamInstallations.Where(x => x.Id != installation &&
            (JsonSerializer.Deserialize<string[]>(x.Grant?.ProvidedCapabilitiesJson ?? "[]", Json) ?? []).Contains("software-quality.validate.v1"))
            .OrderBy(x => x.Id).FirstOrDefault();
        if (qa is not null && !(JsonSerializer.Deserialize<string[]>(qa.Grant?.RequiredCapabilitiesJson ?? "[]", Json) ?? []).Contains(TaskDeliveryCapabilities.Quality))
            throw new InvalidOperationException("The team's QA agent needs the task-review update before it can review this branch.");
        var prior = await db.TaskDeliveryReviews.Where(x => x.OrganizationId == org && x.TaskId == task.Id && x.Status != "Merged" && x.Status != "Superseded").ToListAsync(ct);
        foreach (var old in prior) { old.Status = "Superseded"; old.Revision++; }
        var now = clock.GetUtcNow();
        var review = new TaskDeliveryReview { Id = StableId($"task-review:{task.Id:N}:{publication.Id:N}"), OrganizationId = org,
            BoardId = task.BoardId!.Value, EpicId = root.Id, StoryId = task.ParentWorkTaskId.Value, TaskId = task.Id,
            PublicationId = publication.Id, RepositoryId = publication.RepositoryId, DeveloperInstallationId = installation,
            ManagerOrganizationUserId = manager.Id, QaInstallationId = qa?.Id, CommitSha = publication.CommitSha,
            Status = qa is null ? "AwaitingApproval" : "Testing", QualityStatus = qa is null ? "NotAssigned" : "Pending",
            Summary = request.Summary, CreatedAt = now, UpdatedAt = now };
        db.TaskDeliveryReviews.Add(review);
        var testing = await db.WorkBoardColumns.SingleOrDefaultAsync(x => x.BoardId == task.BoardId && x.Category == WorkBoardColumnCategory.Testing, ct);
        if (testing is null)
        {
            testing = new() { Id = Guid.NewGuid(), BoardId = task.BoardId!.Value, Name = "Testing", Category = WorkBoardColumnCategory.Testing, Position = (await db.WorkBoardColumns.Where(x => x.BoardId == task.BoardId).Select(x => (int?)x.Position).MaxAsync(ct) ?? -1) + 1 };
            db.WorkBoardColumns.Add(testing);
        }
        task.BoardColumnId = testing.Id;
        task.Status = WorkTaskStatus.WaitingForApproval; task.MergeStatus = "Testing"; task.ResultSummary = request.Summary;
        task.Revision++; task.UpdatedAt = now;
        if (qa is not null) Queue(review, qa.Id, TaskDeliveryCapabilities.ReviewRequested);
        await QueueUiAsync(task, ct);
        await db.SaveChangesAsync(ct);
        return Result(review, task);
    }

    public async Task<TaskReviewResult> ReadAsync(Guid org, Guid installation, Guid taskId, CancellationToken ct)
    {
        var review = await db.TaskDeliveryReviews.AsNoTracking().Where(x => x.OrganizationId == org && x.TaskId == taskId && x.Status != "Superseded")
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct) ?? throw new KeyNotFoundException("Task review not found.");
        if (review.DeveloperInstallationId != installation && review.QaInstallationId != installation)
            throw new UnauthorizedAccessException("This review is assigned to another agent.");
        var task = await TaskAsync(org, taskId, ct);
        await RequireScopeReaderAsync(org, installation, task, ct);
        return Result(review, task);
    }

    public async Task<IReadOnlyList<TaskReviewResult>> ListAsync(Guid org, Guid installation, CancellationToken ct)
    {
        var reviews = await db.TaskDeliveryReviews.AsNoTracking().Where(x => x.OrganizationId == org && (x.QaInstallationId == installation && x.Status == "Testing" || x.DeveloperInstallationId == installation && x.Status != "Merged" && x.Status != "Superseded"))
            .OrderBy(x => x.CreatedAt).Take(32).ToListAsync(ct);
        var taskIds = reviews.Select(x => x.TaskId).ToArray();
        var tasks = await db.CoreWorkTasks.AsNoTracking().Where(x => x.OrganizationId == org && taskIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        return reviews.Select(x => Result(x, tasks[x.TaskId])).ToArray();
    }

    public async Task<TaskReviewResult> QualityAsync(Guid org, Guid installation, ReportTaskQualityRequest request, CancellationToken ct)
    {
        var review = await db.TaskDeliveryReviews.SingleAsync(x => x.OrganizationId == org && x.Id == request.ReviewId, ct);
        var publication = await db.SourceControlPublications.AsNoTracking().SingleAsync(x => x.Id == review.PublicationId && x.OrganizationId == org, ct);
        var workspace = await db.SourceControlWorkspaces.AsNoTracking().SingleAsync(x => x.Id == publication.WorkspaceId && x.OrganizationId == org, ct);
        var reviewer = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.AgentInstallationId == installation && x.OrganizationId == org && x.IsActive && x.ArchivedAt == null, ct);
        if (reviewer is null || !await db.TeamMemberships.AnyAsync(x => x.OrganizationId == org && x.TeamId == workspace.TeamId && x.OrganizationUserId == reviewer.Id && x.EndedAt == null, ct) ||
            !await db.TeamRepositoryPolicies.AnyAsync(x => x.OrganizationId == org && x.RepositoryId == review.RepositoryId && x.TeamId == workspace.TeamId && x.DisabledAt == null, ct))
            throw new UnauthorizedAccessException("QA project access is no longer active.");
        if (await db.WorkBoards.AnyAsync(x => x.Id == review.BoardId && x.WorkstreamId != null, ct))
            await new CSweet.Infrastructure.Core.ProjectWorkPolicy(db, clock).RequireAsync(org, reviewer.Id, review.BoardId, ct);
        if (review.QaInstallationId != installation || review.CommitSha != request.CommitSha)
            throw new UnauthorizedAccessException("QA evidence does not match this assigned revision.");
        if (request.Verdict is not ("Passed" or "Failed" or "Blocked") || request.Summary is not { Length: > 0 and <= 4096 } ||
            request.Validations is null || request.Validations.Count > 100 || request.Verdict == "Passed" && (request.Validations.Count == 0 || request.Validations.Any(x => !x.Succeeded || x.ExitCode != 0)))
            throw new ArgumentException("QA pass requires fresh passing validation for the exact source revision.");
        var task = await TaskAsync(org, review.TaskId, ct);
        if (review.Status != "Testing")
        {
            if (review.QualityStatus == request.Verdict) return Result(review, task);
            throw new InvalidOperationException("This candidate is no longer awaiting QA.");
        }
        review.QualityStatus = request.Verdict; review.QualityEvidenceJson = JsonSerializer.Serialize(request, Json);
        review.Status = request.Verdict == "Passed" ? "AwaitingApproval" : "ChangesRequested";
        review.Failure = request.Verdict == "Passed" ? null : request.Summary;
        review.UpdatedAt = clock.GetUtcNow(); review.Revision++;
        if (request.Verdict != "Passed")
        {
            task.Status = WorkTaskStatus.Running; task.MergeStatus = "ChangesRequested"; task.BlockReason = request.Summary; task.Revision++;
            task.BoardColumnId = await db.WorkBoardColumns.Where(x => x.BoardId == task.BoardId && x.Category == WorkBoardColumnCategory.InProgress).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct) ?? task.BoardColumnId;
        }
        Queue(review, review.DeveloperInstallationId, TaskDeliveryCapabilities.Changed);
        await QueueUiAsync(task, ct); await db.SaveChangesAsync(ct);
        return Result(review, task);
    }

    public async Task<MergePreferenceResult> PreferenceAsync(Guid org, Guid installation, Guid scopeId, CancellationToken ct)
    {
        var scope = await TaskAsync(org, scopeId, ct);
        await RequireScopeReaderAsync(org, installation, scope, ct);
        var own = await db.TaskMergePreferences.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org && x.ScopeWorkItemId == scopeId, ct);
        var epicId = Plan(scope)?.RootItemId;
        if (scope.Kind == WorkItemKind.Task && own is null && scope.ParentWorkTaskId is { } storyId)
            own = await db.TaskMergePreferences.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org && x.ScopeWorkItemId == storyId && x.Mode != "Inherit", ct);
        var inherited = epicId is { } id && id != scopeId
            ? await db.TaskMergePreferences.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org && x.ScopeWorkItemId == id, ct) : null;
        var effective = own is { Mode: not "Inherit" } ? own : inherited;
        var owner = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.OrganizationId == org && x.AgentInstallationId == scope.AssignedAgentInstallationId && x.IsActive && x.ArchivedAt == null, ct);
        var manager = await ManagerAsync(org, owner, ct, scope.BoardId);
        return new(scope.Id, scope.Title, scope.Kind.ToString(), own?.Mode ?? "Inherit", own?.Revision ?? 0,
            own is null || own.Mode == "Inherit" ? inherited?.ScopeWorkItemId : own.ScopeWorkItemId != scope.Id ? own.ScopeWorkItemId : null,
            effective is { Mode: "Auto" } && effective.ManagerOrganizationUserId == manager.Id ? "Auto" : "Ask");
    }

    public async Task<MergePreferenceResult> ChangePreferenceAsync(Guid org, Guid installation, ChangeMergePreferenceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160) throw new ArgumentException("A bounded stable key is required.");
        if (request.Mode is not ("Ask" or "Auto" or "Inherit")) throw new ArgumentException("Choose Ask, Auto, or Inherit.");
        var scope = await TaskAsync(org, request.ScopeWorkItemId, ct);
        await RequireScopeReaderAsync(org, installation, scope, ct);
        if (scope.Kind is not (WorkItemKind.Story or WorkItemKind.Epic)) throw new ArgumentException("A saved preference belongs to a story or epic.");
        var actor = await ChatManagerAsync(org, installation, request.SourceMessageId, ct, scope.BoardId);
        var scopeDeveloper = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.OrganizationId == org && x.AgentInstallationId == scope.AssignedAgentInstallationId && x.IsActive, ct);
        if ((await ManagerAsync(org, scopeDeveloper, ct, scope.BoardId)).Id != actor.Id)
            throw new UnauthorizedAccessException("Only the scope owner's manager can change this preference.");
        var existing = await db.TaskMergePreferences.SingleOrDefaultAsync(x => x.OrganizationId == org && x.ScopeWorkItemId == scope.Id, ct);
        if (existing?.IdempotencyKey == request.IdempotencyKey) return await PreferenceAsync(org, installation, scope.Id, ct);
        if ((existing?.Revision ?? 0) != request.ExpectedRevision) throw new DbUpdateConcurrencyException("The merge preference changed; reread it.");
        await SetPreferenceAsync(org, scope.Id, request.Mode, actor.Id, request.SourceMessageId, request.IdempotencyKey, ct);
        // Revocation applies to unstarted task approvals as well as queued automatic merges.
        var pending = await db.TaskDeliveryReviews.Where(x => x.OrganizationId == org && (x.EpicId == scope.Id || x.StoryId == scope.Id) &&
            x.Status != "Merged" && x.Status != "Merging" && x.Status != "Superseded").ToListAsync(ct);
        foreach (var review in pending)
        {
            if (review.DecisionId is { } previousQuestion)
            {
                var card = await db.ExecutiveDecisions.SingleOrDefaultAsync(x => x.OrganizationId == org && x.Id == previousQuestion && x.Status == ExecutiveDecisionStatus.Pending, ct);
                if (card is not null) { card.Status = ExecutiveDecisionStatus.Superseded; card.UpdatedAt = clock.GetUtcNow(); }
            }
            if (review.Status == "ManualReview") review.Status = "AwaitingApproval";
            review.ApprovedCommitSha = null; review.ApprovedByOrganizationUserId = null; review.DecisionId = null;
            review.Revision++; review.UpdatedAt = clock.GetUtcNow();
            Queue(review, review.DeveloperInstallationId, TaskDeliveryCapabilities.Changed);
        }
        await db.SaveChangesAsync(ct);
        return await PreferenceAsync(org, installation, scope.Id, ct);
    }

    internal async Task SetPreferenceAsync(Guid org, Guid scopeId, string mode, Guid manager, Guid? message, string key, CancellationToken ct)
    {
        var preference = await db.TaskMergePreferences.SingleOrDefaultAsync(x => x.OrganizationId == org && x.ScopeWorkItemId == scopeId, ct);
        if (preference is null) { preference = new() { OrganizationId = org, ScopeWorkItemId = scopeId }; db.TaskMergePreferences.Add(preference); }
        else preference.Revision++;
        preference.Mode = mode; preference.ManagerOrganizationUserId = manager; preference.SourceMessageId = message;
        preference.IdempotencyKey = key; preference.UpdatedAt = clock.GetUtcNow();
        var scope = await TaskAsync(org, scopeId, ct);
        db.WorkItemActivities.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, BoardId = scope.BoardId!.Value,
            WorkItemId = scopeId, EventType = "merge.preference.changed", Action = "merge.preference.change",
            ActorKind = CSweet.Domain.Security.GrantSubjectKind.OrganizationUser, ActorSubjectId = manager,
            ActorDisplayName = "Manager", IdempotencyKey = key,
            DataJson = JsonSerializer.Serialize(new { mode, preference.Revision, sourceMessageId = message }, Json), OccurredAt = clock.GetUtcNow() });
    }

    internal async Task<bool> AutoApprovedAsync(TaskDeliveryReview review, CancellationToken ct)
    {
        var preferences = await db.TaskMergePreferences.AsNoTracking().Where(x => x.OrganizationId == review.OrganizationId &&
            (x.ScopeWorkItemId == review.StoryId || x.ScopeWorkItemId == review.EpicId)).ToListAsync(ct);
        var effective = preferences.FirstOrDefault(x => x.ScopeWorkItemId == review.StoryId && x.Mode != "Inherit") ?? preferences.FirstOrDefault(x => x.ScopeWorkItemId == review.EpicId);
        return effective is { Mode: "Auto" } && effective.ManagerOrganizationUserId == review.ManagerOrganizationUserId;
    }

    internal async Task<OrganizationUser> ManagerAsync(Guid org, OrganizationUser developer, CancellationToken ct, Guid? boardId = null)
    {
        var id = developer.ReportsToOrganizationUserId;
        if (boardId.HasValue)
        {
            var projectManager = await db.WorkBoards.Where(x => x.Id == boardId && x.OrganizationId == org && x.WorkstreamId != null).Select(x => x.ManagerOrganizationUserId).SingleOrDefaultAsync(ct);
            if (projectManager.HasValue) id = projectManager;
        }
        var seen = new HashSet<Guid>();
        while (id is { } managerId && seen.Add(managerId) && seen.Count <= 32)
        {
            var manager = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == managerId && x.OrganizationId == org && x.IsActive && x.ArchivedAt == null, ct);
            if (manager is null) break;
            if (manager.EmployeeType == EmployeeType.Human) return manager;
            id = manager.ReportsToOrganizationUserId;
        }
        throw new InvalidOperationException("Assign an active human manager before requesting code review or merge approval.");
    }
    private async Task RequireScopeReaderAsync(Guid org, Guid installation, WorkTask scope, CancellationToken ct)
    {
        var owner = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.AgentInstallationId == installation && x.OrganizationId == org && x.IsActive, ct);
        if (scope.AssignedAgentInstallationId == installation) return;
        var board = await db.WorkBoards.AsNoTracking().SingleAsync(x => x.Id == scope.BoardId && x.OrganizationId == org, ct);
        if (board.TeamId is null || !await db.TeamMemberships.AnyAsync(x => x.TeamId == board.TeamId && x.OrganizationUserId == owner.Id && x.EndedAt == null, ct))
            throw new UnauthorizedAccessException("This agent does not work on the selected scope.");
    }
    private async Task RequireDeveloperAsync(Guid org, Guid installation, WorkTask root, CancellationToken ct)
    {
        var owner = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.OrganizationId == org && x.AgentInstallationId == installation && x.IsActive && x.ArchivedAt == null, ct);
        var board = await db.WorkBoards.AsNoTracking().SingleAsync(x => x.Id == root.BoardId && x.OrganizationId == org, ct);
        if (root.AssignedAgentInstallationId != installation || (board.Kind == WorkBoardKind.Personal ? board.OwnerOrganizationUserId != owner.Id : root.AssignedEmployeeId != owner.Id) ||
            root.Status != WorkTaskStatus.Running || root.ClaimEventId is null || root.ClaimExpiresAt <= clock.GetUtcNow() || root.ClaimExpiresAt is null)
            throw new UnauthorizedAccessException("A live owned project claim is required.");
        await new CSweet.Infrastructure.Core.ProjectWorkPolicy(db, clock).RequireIfConfiguredAsync(root, ct);
    }
    internal async Task<WorkTask> TaskAsync(Guid org, Guid id, CancellationToken ct) =>
        await db.CoreWorkTasks.SingleOrDefaultAsync(x => x.OrganizationId == org && x.Id == id && x.ArchivedAt == null, ct)
            ?? throw new KeyNotFoundException("Work item not found.");
    internal static W.PersonalWorkPlanLink? Plan(WorkTask task) => string.IsNullOrWhiteSpace(task.PlanningSpecificationJson) ? null : JsonSerializer.Deserialize<W.WorkItemPlanningSpecification>(task.PlanningSpecificationJson, Json)?.PersonalPlan;
    internal static Guid StableId(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
    internal static TaskReviewResult Result(TaskDeliveryReview review, WorkTask task) => new(review.Id, task.Id, review.EpicId, review.RepositoryId,
        task.Title, task.Description, JsonSerializer.Deserialize<W.WorkItemPlanningSpecification>(task.PlanningSpecificationJson ?? "{}", Json)?.AcceptanceCriteria ?? [],
        review.CommitSha, review.Status, review.QualityStatus, review.Failure, review.QaInstallationId, task.AssignmentRevision, review.Revision);
    internal void Queue(TaskDeliveryReview review, Guid installation, string type)
    {
        db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = review.OrganizationId,
            TargetInstallationId = installation, EventType = type, DataJson = JsonSerializer.Serialize(new TaskReviewChanged(review.TaskId, review.EpicId, review.Revision), Json),
            IdempotencyKey = $"task-review:{review.Id:N}:{type.Split('.').Reverse().Skip(1).First()}:{review.Revision}",
            Status = AgentPlatformEventOutboxStatus.Pending, NextAttemptAt = clock.GetUtcNow(), OccurredAt = clock.GetUtcNow() });
    }
    internal async Task QueueUiAsync(WorkTask task, CancellationToken ct)
    {
        var readers = await db.ScopedActionGrants.AsNoTracking().Where(x => x.OrganizationId == task.OrganizationId && x.ScopeId == task.BoardId &&
            x.ScopeKind == CSweet.Domain.Security.GrantScopeKind.Board && x.Action == CSweet.Contracts.WorkManagement.PersonalTodoActions.Read &&
            x.SubjectKind == CSweet.Domain.Security.GrantSubjectKind.OrganizationUser && x.RevokedAt == null && (!x.ExpiresAt.HasValue || x.ExpiresAt > clock.GetUtcNow()))
            .Select(x => x.SubjectId).Distinct().ToListAsync(ct);
        readers = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == task.OrganizationId && readers.Contains(x.Id) && x.EmployeeType == EmployeeType.Human && x.IsActive && x.ArchivedAt == null).Select(x => x.Id).ToListAsync(ct);
        db.ApplicationRealtimeOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = task.OrganizationId,
            RecipientOrganizationUserIdsJson = JsonSerializer.Serialize(readers, Json), EventType = AppRealtimeEvents.WorkBoardChanged,
            Subject = $"organizations/{task.OrganizationId:D}/work/boards/{task.BoardId:D}",
            DataJson = JsonSerializer.Serialize(new { boardId = task.BoardId, itemId = task.Id, revision = task.Revision }, Json),
            Status = ApplicationRealtimeOutboxStatus.Pending, NextAttemptAt = clock.GetUtcNow(), OccurredAt = clock.GetUtcNow() });
    }
}
