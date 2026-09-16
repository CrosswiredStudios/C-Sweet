using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkItemMutationEngine
{
    private static void RequireIndependentExecution(WorkTask item)
    {
        if (ReadPlanSpecification(item)?.PersonalPlan is { } plan && plan.RootItemId != item.Id)
            throw new InvalidOperationException("This task is executed through its parent epic. Requeue the epic to resume its plan.");
    }

    private async Task BlockRunningPlanChildrenAsync(WorkTask root, string reason, CancellationToken ct)
    {
        if (ReadPlanSpecification(root)?.PersonalPlan?.Execution != "Coordinator") return;
        var board = await db.WorkBoards.Include(x => x.Columns).SingleAsync(x => x.Id == root.BoardId, ct);
        var running = await db.CoreWorkTasks.Where(x => x.BoardId == root.BoardId && x.Status == WorkTaskStatus.Running).ToListAsync(ct);
        foreach (var child in running.Where(x => x.Id != root.Id && ReadPlanSpecification(x)?.PersonalPlan?.RootItemId == root.Id))
        {
            child.Status = WorkTaskStatus.Blocked;
            child.BoardColumnId = ColumnForStatus(board, WorkTaskStatus.Blocked).Id;
            child.BlockReason = reason; child.Revision++; child.UpdatedAt = clock.GetUtcNow();
        }
    }

    private async Task ReopenBlockedPlanChildrenAsync(WorkTask root, CancellationToken ct)
    {
        if (ReadPlanSpecification(root)?.PersonalPlan?.Execution != "Coordinator") return;
        var board = await db.WorkBoards.Include(x => x.Columns).SingleAsync(x => x.Id == root.BoardId, ct);
        var children = await db.CoreWorkTasks.Where(x => x.BoardId == root.BoardId &&
            x.Status == WorkTaskStatus.Blocked).ToListAsync(ct);
        foreach (var child in children.Where(x => x.Id != root.Id &&
            ReadPlanSpecification(x)?.PersonalPlan?.RootItemId == root.Id))
        {
            child.Status = WorkTaskStatus.Backlog;
            child.BoardColumnId = ColumnForStatus(board, WorkTaskStatus.Backlog).Id;
            child.BlockReason = null;
            child.Revision++;
            child.UpdatedAt = clock.GetUtcNow();
        }
    }
    private static Wire.WorkItemPlanningSpecification? ReadPlanSpecification(WorkTask item) =>
        string.IsNullOrWhiteSpace(item.PlanningSpecificationJson) ? null :
            JsonSerializer.Deserialize<Wire.WorkItemPlanningSpecification>(item.PlanningSpecificationJson, JsonOptions);

    public async Task<Wire.PersonalWorkPlan> CreatePlanAsync(Guid organizationId, PersonalTodoActor actor,
        Wire.CreatePersonalWorkPlanRequest request, CancellationToken cancellationToken = default)
    {
        ValidatePlan(request);
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var root = await RequirePlanClaimAsync(organizationId, actor, request.RootItemId,
            PersonalTodoActions.CreatePlan, allowReady: true, request.ExpectedRevision, cancellationToken);
        var digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { request.EpicTitle, request.Stories }, JsonOptions)));
        var prior = ReadPlanSpecification(root)?.PersonalPlan;
        if (prior is not null)
        {
            if (prior.RootItemId != root.Id || prior.Digest != digest)
                throw new InvalidOperationException("This request already has a different plan. Preserve the existing stories and tasks.");
            return await ReadPlanAsync(root, cancellationToken);
        }
        if (await db.CoreWorkTasks.AnyAsync(x => x.ParentWorkTaskId == root.Id, cancellationToken))
            throw new InvalidOperationException("This request already has child tickets. Continue its existing plan.");
        var count = request.Stories.Count + request.Stories.Sum(x => x.Tasks.Count);
        if (await db.CoreWorkTasks.CountAsync(x => x.BoardId == root.BoardId && x.ArchivedAt == null &&
                x.Status != WorkTaskStatus.Completed && x.Status != WorkTaskStatus.Cancelled, cancellationToken) + count > HardOpenItemLimit)
            throw new InvalidOperationException("The complete plan would exceed the personal board's open-ticket limit.");
        var board = await db.WorkBoards.Include(x => x.Columns).SingleAsync(x => x.Id == root.BoardId, cancellationToken);
        var rank = await db.CoreWorkTasks.Where(x => x.BoardId == root.BoardId).MaxAsync(x => x.BoardRank, cancellationToken);
        var now = clock.GetUtcNow();
        var order = 0;
        Guid? previousTask = null;
        root.Kind = WorkItemKind.Epic;
        root.TypeKey = "general.epic.v1";
        root.Title = request.EpicTitle.Trim();
        root.PlanningRevision++;
        root.Revision++;
        root.UpdatedAt = now;
        root.PlanningSpecificationJson = JsonSerializer.Serialize(new Wire.WorkItemPlanningSpecification(
            [root.Description], request.Stories.SelectMany(x => x.AcceptanceCriteria).ToArray())
            { PersonalPlan = new(root.Id, 0, "Coordinator", digest) }, JsonOptions);
        foreach (var story in request.Stories)
        {
            var storyItem = Add(story.Key, root.Id, WorkItemKind.Story, story.Title, story.Description,
                story.AcceptanceCriteria, "Story", []);
            foreach (var task in story.Tasks)
            {
                var taskItem = Add(task.Key, storyItem.Id, WorkItemKind.Task, task.Title, task.Description,
                    task.AcceptanceCriteria, task.Execution, previousTask is { } before ? [before] : []);
                previousTask = taskItem.Id;
            }
        }
        await SaveChangesWithRealtimeAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return await ReadPlanAsync(root, cancellationToken);

        WorkTask Add(string key, Guid parent, WorkItemKind kind, string title, string description,
            IReadOnlyList<string> acceptance, string execution, IReadOnlyList<Guid> dependencies)
        {
            var item = new WorkTask
            {
                Id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"personal-plan:{root.Id:N}:{key}")).AsSpan(0, 16)),
                OrganizationId = organizationId, BoardId = root.BoardId, ParentWorkTaskId = parent,
                BoardColumnId = ColumnForStatus(board, WorkTaskStatus.Backlog).Id,
                Kind = kind, TypeKey = kind == WorkItemKind.Story ? "general.story.v1" : "general.task.v1",
                // The owning request holds the execution lease; child work is run one task per callback.
                IsExecutable = false, Title = title.Trim(), Description = description.Trim(),
                AssignedEmployeeId = root.AssignedEmployeeId, AssignedAgentInstallationId = actor.AgentInstallationId,
                CreatedByOrganizationUserId = actor.OrganizationUserId,
                SourceConversationId = root.SourceConversationId, SourceMessageId = root.SourceMessageId,
                CreationIdempotencyKey = $"plan:{root.Id:N}:{key}", Status = WorkTaskStatus.Backlog,
                Priority = root.Priority, BoardRank = rank += 1024, CreatedAt = now, UpdatedAt = now,
                PlanningSpecificationJson = JsonSerializer.Serialize(new Wire.WorkItemPlanningSpecification([description], acceptance)
                { PersonalPlan = new(root.Id, ++order, execution), DependencyItemIds = dependencies }, JsonOptions)
            };
            db.CoreWorkTasks.Add(item);
            return item;
        }
    }

    public async Task<Wire.PersonalTodoItem> ReportPlanTaskAsync(Guid organizationId, PersonalTodoActor actor,
        Wire.ReportPersonalWorkPlanTaskRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Status is not ("Running" or "Completed" or "Blocked") ||
            request.Evidence?.Length > 4096 || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("A valid plan-task transition and bounded evidence are required.");
        if (request.Status != "Running" && string.IsNullOrWhiteSpace(request.Evidence))
            throw new ArgumentException("Completion or blocking requires evidence.");
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var root = await RequirePlanClaimAsync(organizationId, actor, request.RootItemId,
            PersonalTodoActions.ReportPlanTask, allowReady: false, expectedRevision: null, cancellationToken);
        var all = await db.CoreWorkTasks.Where(x => x.OrganizationId == organizationId && x.BoardId == root.BoardId).ToListAsync(cancellationToken);
        var tasks = all.Where(x => x.Kind == WorkItemKind.Task && ReadPlanSpecification(x)?.PersonalPlan?.RootItemId == root.Id)
            .OrderBy(x => ReadPlanSpecification(x)!.PersonalPlan!.Order).ToList();
        var item = tasks.SingleOrDefault(x => x.Id == request.TaskItemId && x.ArchivedAt == null)
            ?? throw new UnauthorizedAccessException("The task does not belong to this request's plan.");
        var target = Enum.Parse<WorkTaskStatus>(request.Status);
        var evidence = request.Evidence?.Trim();
        if (item.Status == target && (target == WorkTaskStatus.Running ||
                (target == WorkTaskStatus.Completed ? item.ResultSummary : item.BlockReason) == evidence))
            return await MapItemAsync(item, cancellationToken);
        RequireRevision(item, request.ExpectedRevision);
        if (tasks.TakeWhile(x => x.Id != item.Id).Any(x => x.Status != WorkTaskStatus.Completed || x.ArchivedAt != null))
            throw new InvalidOperationException("Complete preceding plan tasks before starting this task.");
        if (target == WorkTaskStatus.Completed && item.Status != WorkTaskStatus.Running || item.Status == WorkTaskStatus.Completed)
            throw new InvalidOperationException("Only the current running task can be completed.");
        var board = await db.WorkBoards.Include(x => x.Columns).SingleAsync(x => x.Id == root.BoardId, cancellationToken);
        Set(item, target);
        item.ResultSummary = target == WorkTaskStatus.Completed ? evidence : null;
        item.BlockReason = target == WorkTaskStatus.Blocked ? evidence : null;
        var story = all.Single(x => x.Id == item.ParentWorkTaskId);
        var storyTasks = tasks.Where(x => x.ParentWorkTaskId == story.Id).ToList();
        Set(story, storyTasks.All(x => x.Status == WorkTaskStatus.Completed && x.ArchivedAt == null)
            ? WorkTaskStatus.Completed : target == WorkTaskStatus.Blocked ? WorkTaskStatus.Blocked : WorkTaskStatus.Running);
        story.BlockReason = target == WorkTaskStatus.Blocked ? evidence : null;
        await SaveChangesWithRealtimeAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return await MapItemAsync(item, cancellationToken);

        void Set(WorkTask value, WorkTaskStatus status)
        {
            value.Status = status; value.BoardColumnId = ColumnForStatus(board, status).Id;
            value.Revision++; value.UpdatedAt = clock.GetUtcNow();
        }
    }

    private async Task<WorkTask> RequirePlanClaimAsync(Guid organizationId, PersonalTodoActor actor, Guid rootId,
        string action, bool allowReady, long? expectedRevision, CancellationToken ct)
    {
        var root = await LoadPersonalItemAsync(organizationId, rootId, ct);
        await RequireGrantAsync(organizationId, root.BoardId!.Value, actor, action, ct);
        var ownsItem = actor.AgentInstallationId is not null && root.AssignedAgentInstallationId == actor.AgentInstallationId &&
            root.AssignedEmployeeId == actor.OrganizationUserId && root.ArchivedAt is null;
        var hasLiveClaim = root.Status == WorkTaskStatus.Running && root.ClaimEventId is not null &&
            root.ClaimExpiresAt > clock.GetUtcNow();
        var mayPlanReady = allowReady && root.Status == WorkTaskStatus.Ready && root.IsExecutable &&
            (expectedRevision.HasValue && root.Revision == expectedRevision.Value || ReadPlanSpecification(root)?.PersonalPlan is not null);
        if (!ownsItem || (!hasLiveClaim && !mayPlanReady))
            throw new UnauthorizedAccessException("Planning requires the owned Ready request at its expected revision or a live claim; task execution requires the live claim.");
        return root;
    }

    private async Task<Wire.PersonalWorkPlan> ReadPlanAsync(WorkTask root, CancellationToken ct)
    {
        var all = await db.CoreWorkTasks.AsNoTracking().Where(x => x.BoardId == root.BoardId && x.OrganizationId == root.OrganizationId).ToListAsync(ct);
        var items = new List<Wire.PersonalTodoItem>();
        foreach (var item in all.Where(x => ReadPlanSpecification(x)?.PersonalPlan?.RootItemId == root.Id).OrderBy(x => x.BoardRank))
            items.Add(await MapItemAsync(item, ct));
        return new(root.Id, root.Revision, items);
    }

    private static void ValidatePlan(Wire.CreatePersonalWorkPlanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160 ||
            string.IsNullOrWhiteSpace(request.EpicTitle) || request.EpicTitle.Length > 160 ||
            request.Stories is not { Count: >= 2 and <= 12 } || JsonSerializer.SerializeToUtf8Bytes(request).Length > 65536)
            throw new ArgumentException("Provide a bounded MVP epic with 2–12 testable stories.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var tasks = new List<Wire.PersonalWorkPlanTask>();
        foreach (var story in request.Stories)
        {
            Check(story.Key, story.Title, story.Description, story.AcceptanceCriteria);
            if (story.Tasks is not { Count: >= 2 and <= 12 }) throw new ArgumentException("Each story requires 2–12 small tasks.");
            foreach (var task in story.Tasks)
            {
                Check(task.Key, task.Title, task.Description, task.AcceptanceCriteria);
                if (task.Execution is not ("Implementation" or "Validation" or "Deployment")) throw new ArgumentException("Unknown task execution type.");
                tasks.Add(task);
            }
        }
        if (tasks.Count > 60 || tasks.Count(x => x.Execution == "Deployment") != 1 || tasks[^1].Execution != "Deployment" ||
            tasks[^2].Execution != "Validation")
            throw new ArgumentException("Plans require integration validation and exactly one final deployment task, with at most 60 tasks.");

        void Check(string key, string title, string description, IReadOnlyList<string> criteria)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 64 || !keys.Add(key) ||
                key.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not '-' and not '_') ||
                string.IsNullOrWhiteSpace(title) || title.Length > 160 || string.IsNullOrWhiteSpace(description) || description.Length > 4000 ||
                criteria is not { Count: >= 1 and <= 8 } || criteria.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 1000))
                throw new ArgumentException("Every story and task needs a unique key, bounded scope, and testable acceptance criteria.");
        }
    }
}
