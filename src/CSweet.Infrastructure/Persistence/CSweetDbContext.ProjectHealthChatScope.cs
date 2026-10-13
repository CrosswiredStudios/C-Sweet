using CSweet.Domain.Core;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    internal static bool IsInterruptedHealthTurn(ChatTurn turn) => turn.Status == ChatTurnStatus.Failed ||
        turn.Status == ChatTurnStatus.CompletedWithWarnings && turn.ErrorCode == "memory.runtime_reset";

    private Guid[] HealthProjectsForChatTurn(ChatTurn turn, HashSet<Guid>? seen = null)
    {
        seen ??= [];
        if (seen.Count >= 16 || !seen.Add(turn.Id)) return [];
        var projects = new HashSet<Guid>();
        if (turn.RetryOfTurnId is { } retryOf && ChatTurns.Find(retryOf) is { } parent &&
            parent.OrganizationId == turn.OrganizationId && parent.ConversationId == turn.ConversationId &&
            parent.TargetAgentOrganizationUserId == turn.TargetAgentOrganizationUserId)
            projects.UnionWith(HealthProjectsForChatTurn(parent, seen));
        projects.UnionWith(ProjectHealthSignals.Where(x => x.OrganizationId == turn.OrganizationId &&
            x.SourceId == turn.Id && x.SourceKind == CSweet.Infrastructure.Core.ProjectChatTurnAttribution.SourceKind).Select(x => x.WorkstreamId));
        projects.UnionWith(ProjectHealthSignals.Local.Where(x => x.OrganizationId == turn.OrganizationId &&
            x.SourceId == turn.Id && x.SourceKind == CSweet.Infrastructure.Core.ProjectChatTurnAttribution.SourceKind).Select(x => x.WorkstreamId));
        if (CoreConversations.Find(turn.ConversationId) is { } chat && chat.OrganizationId == turn.OrganizationId &&
            chat.WorkstreamId is { } project) projects.Add(project);
        projects.UnionWith(AgentCoordinationSessions.Where(x => x.OrganizationId == turn.OrganizationId &&
            x.SourceChatTurnId == turn.Id && x.WorkstreamId != null).Select(x => x.WorkstreamId!.Value));
        projects.UnionWith(AgentCoordinationSessions.Local.Where(x => x.OrganizationId == turn.OrganizationId &&
            x.SourceChatTurnId == turn.Id && x.WorkstreamId != null).Select(x => x.WorkstreamId!.Value));
        projects.UnionWith(ProjectIntakes.Where(x => x.OrganizationId == turn.OrganizationId &&
            x.SourceChatTurnId == turn.Id && x.ProjectId != null).Select(x => x.ProjectId!.Value));
        projects.UnionWith(ProjectIntakes.Local.Where(x => x.OrganizationId == turn.OrganizationId &&
            x.SourceChatTurnId == turn.Id && x.ProjectId != null).Select(x => x.ProjectId!.Value));
        var tasks = CoreWorkTasks.Where(x => x.OrganizationId == turn.OrganizationId &&
            x.SourceMessageId == turn.UserMessageId && x.SourceConversationId == turn.ConversationId).ToList();
        tasks.AddRange(CoreWorkTasks.Local.Where(x => x.OrganizationId == turn.OrganizationId &&
            x.SourceMessageId == turn.UserMessageId && x.SourceConversationId == turn.ConversationId));
        var source = turn.Id.ToString();
        var workIds = AgentWorkItems.Where(x => x.OrganizationId == turn.OrganizationId.ToString() &&
            x.SourceType == "chat-turn" && x.SourceId == source).Select(x => x.Id).ToArray();
        var contexts = WorkExecutionContexts.Where(x => x.OrganizationId == turn.OrganizationId &&
            workIds.Contains(x.AgentWorkItemId)).ToList();
        foreach (var context in contexts)
            if ((context.RootWorkItemId ?? context.WorkItemId) is { } taskId &&
                CoreWorkTasks.Find(taskId) is { } task && task.OrganizationId == turn.OrganizationId)
                tasks.Add(task);
        foreach (var task in tasks)
            if (HealthProjectForTask(task) is { } taskProject) projects.Add(taskProject);
        // Re-check tenancy even for a malformed legacy source link.
        return Workstreams.Where(x => x.OrganizationId == turn.OrganizationId && projects.Contains(x.Id)).Select(x => x.Id).ToArray();
    }
}
