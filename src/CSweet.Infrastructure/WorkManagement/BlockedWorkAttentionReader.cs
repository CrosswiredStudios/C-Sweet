using CSweet.Application.WorkManagement;
using CSweet.Contracts.Core;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Projects canonical blocked work into the same recovery queue for every role and business type.</summary>
public sealed class BlockedWorkAttentionReader(CSweetDbContext db, IPersonalTodoService todos, TimeProvider clock, WorkRecoveryService recovery)
{
    public async Task<IReadOnlyList<BlockedWorkAttentionResponse>> ReadAsync(
        Guid organizationId, PersonalTodoActor actor, CancellationToken token = default)
    {
        // Reuse the authorized directory. Visibility alone does not authorize a retry.
        var directory = await todos.ListAsync(organizationId, actor, cancellationToken: token);
        return await ReadVisibleAsync(organizationId, actor, directory, token);
    }

    internal async Task<IReadOnlyList<BlockedWorkAttentionResponse>> ReadVisibleAsync(Guid organizationId,
        PersonalTodoActor actor, Wire.PersonalTodoDirectory directory, CancellationToken token)
    {
        var blocked = directory.Boards.SelectMany(board => board.Items
                .Where(item => item.ArchivedAt is null && item.Status == Wire.PersonalTodoStatuses.Blocked)
                .Select(item => (Board: board, Item: item))).DistinctBy(x => x.Item.Id).ToArray();
        if (blocked.Length == 0) return [];
        var now = clock.GetUtcNow();
        var kind = actor.AgentInstallationId.HasValue ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser;
        var subject = actor.AgentInstallationId ?? actor.OrganizationUserId;
        var retryBoards = await db.ScopedActionGrants.AsNoTracking().Where(x =>
                x.OrganizationId == organizationId && x.SubjectKind == kind && x.SubjectId == subject &&
                x.ScopeKind == GrantScopeKind.Board && x.Action == PersonalTodoActions.Requeue &&
                x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > now))
            .Select(x => x.ScopeId).ToListAsync(token);
        var visible = directory.Boards.SelectMany(b => b.Items).DistinctBy(t => t.Id).ToDictionary(t => t.Id);
        var visibleIds = visible.Keys.ToArray();
        var dependencies = await db.WorkItemDependencies.AsNoTracking().Where(d => visibleIds.Contains(d.WorkItemId) &&
                d.WorkItem!.OrganizationId == organizationId && d.DependsOnWorkItem!.OrganizationId == organizationId &&
                d.DependsOnWorkItem.Status != CSweet.Domain.Core.WorkTaskStatus.Completed)
            .Select(d => new { d.WorkItemId, d.DependsOnWorkItemId }).ToListAsync(token);
        var prerequisites = dependencies.ToLookup(d => d.WorkItemId, d => d.DependsOnWorkItemId);
        var result = new List<BlockedWorkAttentionResponse>();
        foreach (var (board, item) in blocked.OrderBy(x => x.Item.UpdatedAt))
        {
            string? retryKey = null, retryReason = null;
            if (item.IsExecutable && (item.PlanRootId is null || item.PlanRootId == item.Id) &&
                (actor.OrganizationUserId == board.OwnerOrganizationUserId || actor.OrganizationUserId == board.ManagerOrganizationUserId) &&
                retryBoards.Contains(item.BoardId) && !prerequisites.Contains(item.Id) && item.Wait is null && item.WorkContext?.DecisionId is null &&
                item.WorkContext?.CoordinationSessionId is null && !CSweet.Agent.SDK.ManagerWorkRecovery.IsCommitment(item))
            {
                var (facts, updatedPackage) = await recovery.AssessVisibleItemAsync(organizationId, item, token);
                if (facts.CanResume || updatedPackage)
                {
                    var key = $"overview-retry:{facts.Basis}";
                    if (!await db.WorkItemActivities.AnyAsync(a => a.OrganizationId == organizationId && a.WorkItemId == item.Id &&
                        a.EventType == "overview.retried" && a.IdempotencyKey == key, token))
                    {
                        retryKey = key;
                        retryReason = updatedPackage ? "The agent was updated after this attempt failed." :
                            "The agent now has a way to carry out the step that failed.";
                    }
                }
            }
            result.Add(new(item.Id, board.OwnerOrganizationUserId, board.OwnerDisplayName, board.ManagerDisplayName,
                item.Title, item.BlockReason ?? "This work is blocked.", item.Revision, retryKey is not null,
                $"/organizations/{organizationId:D}/employees/{board.OwnerOrganizationUserId:D}?tab=personal-board&item={item.Id:D}",
                item.WorkContext?.DecisionId) { Blockers = FindBlockers(organizationId, item, visible, prerequisites), RetryKey = retryKey, RetryReason = retryReason });
        }
        return result;
    }

    private static IReadOnlyList<OverviewBlockerSource> FindBlockers(Guid org, Wire.PersonalTodoItem item,
        IReadOnlyDictionary<Guid, Wire.PersonalTodoItem> visible, ILookup<Guid, Guid> prerequisites)
    {
        OverviewBlockerSource Source(Wire.PersonalTodoItem task) => new(SourceKey(task.Id, visible), task.Id, task.Title,
            $"/organizations/{org}/employees/{task.OwnerOrganizationUserId}?tab=personal-board&item={task.Id}");
        var result = new List<OverviewBlockerSource>();
        var visited = new HashSet<Guid>();
        var pending = new Stack<Wire.PersonalTodoItem>();
        pending.Push(item);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current.Id)) continue;
            if (CSweet.Agent.SDK.ManagerWorkRecovery.IsCommitment(current) && current.WorkContext?.WorkItemId is Guid source &&
                visible.TryGetValue(source, out var original))
            {
                pending.Push(original);
                continue;
            }
            var dependencies = prerequisites[current.Id].Where(visible.ContainsKey).ToArray();
            if (dependencies.Length == 0)
                result.Add(Source(current));
            else foreach (var dependency in dependencies) pending.Push(visible[dependency]);
        }
        return result.Count > 0 ? result : [Source(item)];
    }

    internal static string SourceKey(Guid id, IReadOnlyDictionary<Guid, Wire.PersonalTodoItem> visible,
        Func<string, Guid, string?>? linked = null)
    {
        var seen = new HashSet<Guid>();
        while (true)
        {
            if (!seen.Add(id)) return "work:" + seen.Min().ToString("N");
            if (linked?.Invoke("WorkItem", id) is { } workKey) return workKey;
            if (!visible.TryGetValue(id, out var item)) break;
            if (item.WorkContext?.IssueId is Guid issue && linked?.Invoke("Issue", issue) is { } caseKey) return caseKey;
            if (CSweet.Agent.SDK.ManagerWorkRecovery.IsCommitment(item) && item.WorkContext?.WorkItemId is Guid source)
            { id = source; continue; }
            if (item.WorkContext?.CoordinationSessionId is Guid session) return linked?.Invoke("Coordination", session) ?? "coordination:" + session.ToString("N");
            if (item.WorkContext?.DecisionId is Guid decision) return linked?.Invoke("Decision", decision) ?? "decision:" + decision.ToString("N");
            if (item.Wait?.Dependency is { } dependency) return dependency.Kind + ":" + dependency.ResourceId.ToString("N");
            if (item.PlanRootId is Guid root && root != id && visible.TryGetValue(root, out var plan) && plan.Status == Wire.PersonalTodoStatuses.Blocked) { id = root; continue; }
            break;
        }
        return "work:" + id.ToString("N");
    }

    public async Task<Wire.PersonalTodoItem> RetryAsync(Guid org, PersonalTodoActor actor, Wire.RequeuePersonalTodoItemRequest request, CancellationToken ct)
    {
        var directory = await todos.ListAsync(org, actor, cancellationToken: ct);
        var board = directory.Boards.SingleOrDefault(b => b.Items.Any(i => i.Id == request.ItemId))
            ?? throw new UnauthorizedAccessException();
        var item = board.Items.Single(i => i.Id == request.ItemId);
        var current = await ReadVisibleAsync(org, actor, new([board with { Items = [item] }]), ct);
        if (current.SingleOrDefault() is not { CanRetry: true } retry || retry.Revision != request.ExpectedRevision || retry.RetryKey != request.IdempotencyKey)
            throw new DbUpdateConcurrencyException("The reason to retry this work has changed.");
        // Requeue saves this receipt and its revision-checked state change in the same transaction.
        var receipt = db.WorkItemActivities.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, WorkItemId = item.Id, BoardId = item.BoardId,
            EventType = "overview.retried", Action = PersonalTodoActions.Requeue, ActorKind = GrantSubjectKind.OrganizationUser,
            ActorSubjectId = actor.OrganizationUserId, IdempotencyKey = retry.RetryKey, OccurredAt = clock.GetUtcNow() });
        try
        {
            return await todos.RequeueAsync(org, actor, request, ct);
        }
        catch
        {
            receipt.State = EntityState.Detached;
            throw;
        }
    }
}
