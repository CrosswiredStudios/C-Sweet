using System.Text.Json;
using CSweet.Domain.Analytics;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Security;

public sealed partial class SecurityAuditService
{
    private sealed record ActivityTarget(Guid? WorkItemId = null, Guid? ConversationId = null,
        Guid? PerspectiveEmployeeId = null);

    private async Task<Dictionary<Guid, string>> ResolveActivityLinksAsync(Guid organizationId,
        IReadOnlyList<AuditEvent> page, IReadOnlyDictionary<Guid, Guid?> actors, CancellationToken token)
    {
        // Terminal tool receipts inherit the exact start context, never the agent's current assignment.
        var parentIds = page.Where(x => x.ParentEventId.HasValue).Select(x => x.ParentEventId!.Value).Distinct().ToArray();
        var parents = parentIds.Length == 0 ? [] : await db.AuditEvents.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && parentIds.Contains(x.Id)).ToListAsync(token);
        var sources = page.Concat(parents).DistinctBy(x => x.Id).ToArray();
        var targets = sources.ToDictionary(x => x.Id, ReadActivityTarget);
        Guid[] Ids(params string[] types) => sources.Where(x => types.Contains(x.EntityType) && x.EntityId.HasValue)
            .Select(x => x.EntityId!.Value).Distinct().ToArray();
        void Assign(string type, Guid id, ActivityTarget value)
        {
            foreach (var source in sources.Where(x => x.EntityType == type && x.EntityId == id))
            {
                var recorded = targets[source.Id];
                targets[source.Id] = new(recorded.WorkItemId ?? value.WorkItemId,
                    recorded.ConversationId ?? value.ConversationId,
                    recorded.PerspectiveEmployeeId ?? value.PerspectiveEmployeeId);
            }
        }

        foreach (var source in sources.Where(x => x.EntityType is "WorkTask" or "WorkItem"))
            targets[source.Id] = targets[source.Id] with { WorkItemId = source.EntityId };
        var ids = Ids("ConversationMessage");
        if (ids.Length > 0)
            foreach (var message in await db.Set<ConversationMessage>().AsNoTracking()
                .Where(x => ids.Contains(x.Id) && x.Conversation!.OrganizationId == organizationId)
                .Select(x => new { x.Id, x.ConversationId, x.SenderOrganizationUserId }).ToListAsync(token))
                Assign("ConversationMessage", message.Id, new(ConversationId: message.ConversationId,
                    PerspectiveEmployeeId: message.SenderOrganizationUserId));
        ids = Ids("AgentRunLog");
        if (ids.Length > 0)
            foreach (var run in await db.AgentRunLogs.AsNoTracking().Where(x => ids.Contains(x.Id) && x.OrganizationId == organizationId)
                .Select(x => new { x.Id, x.WorkItemId, x.ConversationId, x.EmployeeId }).ToListAsync(token))
                Assign("AgentRunLog", run.Id, new(run.WorkItemId, run.ConversationId, PerspectiveEmployeeId: run.EmployeeId));
        ids = Ids("WorkItemActivity");
        if (ids.Length > 0)
            foreach (var activity in await db.Set<WorkItemActivity>().AsNoTracking().Where(x => ids.Contains(x.Id) && x.OrganizationId == organizationId)
                .Select(x => new { x.Id, x.WorkItemId }).ToListAsync(token))
                Assign("WorkItemActivity", activity.Id, new(activity.WorkItemId));
        ids = Ids("WorkExecutionInterval");
        if (ids.Length > 0)
            foreach (var interval in await db.Set<WorkExecutionInterval>().AsNoTracking().Where(x => ids.Contains(x.Id) && x.OrganizationId == organizationId)
                .Select(x => new { x.Id, x.WorkItemId }).ToListAsync(token))
                Assign("WorkExecutionInterval", interval.Id, new(interval.WorkItemId));
        ids = Ids("WorkLifecycleEvent");
        if (ids.Length > 0)
            foreach (var lifecycle in await db.Set<WorkLifecycleEvent>().AsNoTracking()
                .Where(x => ids.Contains(x.Id) && x.OrganizationId == organizationId && x.ResourceKind == "WorkItem")
                .Select(x => new { x.Id, x.ResourceId }).ToListAsync(token))
                Assign("WorkLifecycleEvent", lifecycle.Id, new(lifecycle.ResourceId));
        ids = Ids("WorkOrchestrationEvent");
        if (ids.Length > 0)
            foreach (var orchestration in await db.Set<WorkOrchestrationEvent>().AsNoTracking()
                .Where(x => ids.Contains(x.Id) && x.OrganizationId == organizationId)
                .Join(db.Set<WorkItemExecution>(), x => x.ItemExecutionId, x => (Guid?)x.Id,
                    (x, execution) => new { x.Id, execution.WorkItemId }).ToListAsync(token))
                Assign("WorkOrchestrationEvent", orchestration.Id, new(orchestration.WorkItemId));
        var attemptIds = Ids("Capability", "AgentWorkAttempt");
        var workIds = Ids("AgentWorkItem");
        if (workIds.Length > 0)
        {
            var organizationKey = organizationId.ToString("D");
            var queuedWork = await db.AgentWorkItems.AsNoTracking().Where(x => workIds.Contains(x.Id) && x.OrganizationId == organizationKey &&
                (x.SourceType == "WorkStageExecution" || x.SourceType == "WorkDeliveryStage"))
                .Select(x => new { x.Id, x.SourceId, x.AgentInstallationId }).ToListAsync(token);
            var stageIds = queuedWork.Select(x => Guid.TryParse(x.SourceId, out var id) ? id : Guid.Empty).Where(x => x != Guid.Empty).Distinct().ToArray();
            if (stageIds.Length > 0)
            {
                var stages = await db.Set<WorkStageExecution>().AsNoTracking().Where(x => stageIds.Contains(x.Id) &&
                    (x.ItemExecution != null && x.ItemExecution.WorkItem!.OrganizationId == organizationId ||
                     x.DeliveryExecution != null && x.DeliveryExecution.Plan!.OrganizationId == organizationId))
                    .Select(x => new { x.Id, x.AgentInstallationId, WorkItemId = x.ItemExecution != null ? (Guid?)x.ItemExecution.WorkItemId : x.DeliveryExecution!.WorkItemId }).ToListAsync(token);
                foreach (var work in queuedWork)
                    if (Guid.TryParse(work.SourceId, out var stageId) && stages.FirstOrDefault(x => x.Id == stageId && x.AgentInstallationId == work.AgentInstallationId) is { } stage)
                        Assign("AgentWorkItem", work.Id, new(stage.WorkItemId));
            }
        }
        var progressIds = Ids("AgentWorkProgress");
        if (progressIds.Length > 0)
        {
            var progress = await db.AgentWorkProgress.AsNoTracking().Where(x => progressIds.Contains(x.Id))
                .Select(x => new { x.Id, x.AgentWorkAttemptId }).ToListAsync(token);
            attemptIds = attemptIds.Concat(progress.Select(x => x.AgentWorkAttemptId)).Distinct().ToArray();
            var contexts = await db.WorkExecutionContexts.AsNoTracking().Where(x => x.OrganizationId == organizationId && attemptIds.Contains(x.Id))
                .Select(x => new { x.Id, WorkItemId = x.RootWorkItemId ?? x.WorkItemId }).ToListAsync(token);
            foreach (var entry in progress)
                Assign("AgentWorkProgress", entry.Id, new(contexts.FirstOrDefault(x => x.Id == entry.AgentWorkAttemptId)?.WorkItemId));
        }
        if (attemptIds.Length > 0 || workIds.Length > 0)
        {
            var contexts = await db.WorkExecutionContexts.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && (attemptIds.Contains(x.Id) || workIds.Contains(x.AgentWorkItemId)))
                .Select(x => new { x.Id, x.AgentWorkItemId, WorkItemId = x.RootWorkItemId ?? x.WorkItemId }).ToListAsync(token);
            foreach (var context in contexts)
            {
                Assign("Capability", context.Id, new(context.WorkItemId));
                Assign("AgentWorkAttempt", context.Id, new(context.WorkItemId));
            }
            foreach (var group in contexts.GroupBy(x => x.AgentWorkItemId))
            {
                var items = group.Select(x => x.WorkItemId).Distinct().ToArray();
                if (items.Length == 1) Assign("AgentWorkItem", group.Key, new(items[0]));
            }
        }

        var workItemIds = targets.Values.Where(x => x.WorkItemId.HasValue).Select(x => x.WorkItemId!.Value).Distinct().ToArray();
        var workItems = workItemIds.Length == 0 ? [] : await db.CoreWorkTasks.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && workItemIds.Contains(x.Id))
            .Join(db.Set<WorkBoard>().Where(x => x.OrganizationId == organizationId), x => x.BoardId, x => (Guid?)x.Id,
                (item, board) => new { item.Id, BoardId = board.Id, board.Kind, board.OwnerOrganizationUserId }).ToListAsync(token);
        var conversationIds = targets.Values.Where(x => x.ConversationId.HasValue).Select(x => x.ConversationId!.Value).Distinct().ToArray();
        var conversations = conversationIds.Length == 0 ? [] : await db.Set<Conversation>().AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && conversationIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(token);
        var perspectiveIds = targets.Values.Where(x => x.PerspectiveEmployeeId.HasValue).Select(x => x.PerspectiveEmployeeId!.Value)
            .Concat(actors.Values.Where(x => x.HasValue).Select(x => x!.Value)).Distinct().ToArray();
        var agentIds = perspectiveIds.Length == 0 ? [] : await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployeeType == EmployeeType.Agent && perspectiveIds.Contains(x.Id))
            .Select(x => x.Id).ToListAsync(token);
        var links = new Dictionary<Guid, string>();
        foreach (var item in page)
        {
            var target = targets[item.Id];
            if (item.ParentEventId is { } parent && targets.TryGetValue(parent, out var inherited))
                target = new(target.WorkItemId ?? inherited.WorkItemId, target.ConversationId ?? inherited.ConversationId,
                    target.PerspectiveEmployeeId ?? inherited.PerspectiveEmployeeId);
            var chatActivity = item.EventType.StartsWith("communication.", StringComparison.Ordinal) ||
                item.Category == "AgentCapability" && target.ConversationId.HasValue;
            if ((!chatActivity || !target.ConversationId.HasValue) && workItems.FirstOrDefault(x => x.Id == target.WorkItemId) is { } workItem)
                links[item.Id] = workItem.Kind == WorkBoardKind.Personal && workItem.OwnerOrganizationUserId is { } owner
                    ? $"/organizations/{organizationId:D}/employees/{owner:D}?tab=personal-board&item={workItem.Id:D}"
                    : $"/organizations/{organizationId:D}/work/boards/{workItem.BoardId:D}?item={workItem.Id:D}";
            else if (target.ConversationId is { } chat && conversations.Contains(chat))
            {
                var perspective = actors.GetValueOrDefault(item.Id) ?? target.PerspectiveEmployeeId;
                var query = new List<string>();
                if (perspective.HasValue && agentIds.Contains(perspective.Value)) query.Add($"perspective={perspective:D}");
                else if (item.ActorKind == "Agent") continue; // Never silently open an agent's chat as the human viewer.
                links[item.Id] = $"/organizations/{organizationId:D}/communications/{chat:D}" + (query.Count > 0 ? "?" + string.Join('&', query) : "");
            }
        }
        return links;
    }

    private static ActivityTarget ReadActivityTarget(AuditEvent item)
    {
        var target = new ActivityTarget();
        var isSend = item.EventType == "communication.message.sent" || item.Summary?.Contains(" invoked send_communication_message (", StringComparison.Ordinal) == true;
        try
        {
            using var metadata = JsonDocument.Parse(item.MetadataJson ?? "{}");
            var root = metadata.RootElement;
            target = new(ActivityGuid(root, "activityWorkItemId"), ActivityGuid(root, "activityConversationId"),
                ActivityGuid(root, "activityPerspectiveEmployeeId"));
            isSend |= root.ValueKind == JsonValueKind.Object && root.TryGetProperty("toolName", out var tool) &&
                tool.ValueKind == JsonValueKind.String && tool.GetString() == "send_communication_message";
        }
        catch (JsonException) { }
        try
        {
            // Legacy native receipts already contain these exact IDs in their recorded source values.
            // A truncated or missing preview falls back to bounded, organization-scoped source reads.
            using var preview = JsonDocument.Parse(item.PayloadPreview ?? "{}");
            var root = preview.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return target;
            if (item.Category == "AgentCapability" && isSend)
            {
                var arguments = root.TryGetProperty("arguments", out var args) ? args : root;
                return target with { ConversationId = target.ConversationId ?? ActivityGuid(arguments, "chatId") };
            }
            if (!root.TryGetProperty("values", out var values)) return target;
            var workItem = item.EntityType switch
            {
                "WorkTask" => item.EntityId,
                "WorkItemActivity" or "WorkExecutionInterval" or "AgentRunLog" => ActivityGuid(values, "WorkItemId"),
                _ => null
            };
            var conversation = item.EntityType is "ConversationMessage" or "ChatTurn" or "AgentRunLog" ? ActivityGuid(values, "ConversationId") : null;
            var perspective = item.EntityType switch
            {
                "ConversationMessage" => ActivityGuid(values, "SenderOrganizationUserId"),
                "ChatTurn" => ActivityGuid(values, "TargetAgentOrganizationUserId"),
                "AgentRunLog" => ActivityGuid(values, "EmployeeId"),
                _ => null
            };
            return new(target.WorkItemId ?? workItem, target.ConversationId ?? conversation, target.PerspectiveEmployeeId ?? perspective);
        }
        catch (JsonException) { return target; }
    }

    private static Guid? ActivityGuid(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String && property.TryGetGuid(out var id) ? id : null;
}
