using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthReader
{
    private async Task<Guid[]> ReadLinkedChatTurnIdsAsync(Workstream project, WorkTask[] tickets,
        Guid[] contextWorks, List<AgentCoordinationSession> sessions, CancellationToken token)
    {
        // Direct conversations deliberately have no project scope. Attribute individual turns
        // only through exact persisted source identities, never an employee's latest project.
        var ids = sessions.Where(x => x.SourceChatTurnId.HasValue).Select(x => x.SourceChatTurnId!.Value).ToHashSet();
        ids.UnionWith(await db.ProjectIntakes.AsNoTracking().Where(x => x.OrganizationId == project.OrganizationId &&
            x.ProjectId == project.Id && x.SourceChatTurnId != null).Select(x => x.SourceChatTurnId!.Value).ToListAsync(token));
        ids.UnionWith(await db.ProjectHealthSignals.AsNoTracking().Where(x => x.OrganizationId == project.OrganizationId &&
            x.WorkstreamId == project.Id && (x.SourceKind == nameof(ChatTurn) || x.SourceKind == ProjectChatTurnAttribution.SourceKind))
            .Select(x => x.SourceId).Distinct().ToListAsync(token));
        var sourceMessages = tickets.Where(x => x.SourceMessageId.HasValue && x.SourceConversationId.HasValue).ToArray();
        var messageIds = sourceMessages.Select(x => x.SourceMessageId!.Value).ToArray();
        var sourceTurns = await db.ChatTurns.AsNoTracking().Where(x => x.OrganizationId == project.OrganizationId &&
            messageIds.Contains(x.UserMessageId)).ToListAsync(token);
        ids.UnionWith(sourceTurns.Where(x => sourceMessages.Any(t => t.SourceMessageId == x.UserMessageId &&
            t.SourceConversationId == x.ConversationId)).Select(x => x.Id));
        var workSources = await db.AgentWorkItems.AsNoTracking().Where(x => x.OrganizationId == project.OrganizationId.ToString() &&
            contextWorks.Contains(x.Id) && x.SourceType == "chat-turn").Select(x => x.SourceId).ToListAsync(token);
        foreach (var source in workSources) if (Guid.TryParse(source, out var id)) ids.Add(id);
        var frontier = ids.ToArray();
        for (var depth = 0; depth < 16 && frontier.Length > 0; depth++)
        {
            var next = await db.ChatTurns.AsNoTracking().Where(x => x.OrganizationId == project.OrganizationId &&
                x.RetryOfTurnId != null && frontier.Contains(x.RetryOfTurnId.Value) && db.ChatTurns.Any(parent =>
                    parent.Id == x.RetryOfTurnId && parent.OrganizationId == x.OrganizationId &&
                    parent.ConversationId == x.ConversationId && parent.TargetAgentOrganizationUserId == x.TargetAgentOrganizationUserId))
                .Select(x => x.Id).ToArrayAsync(token);
            frontier = next.Where(x => ids.Add(x)).ToArray();
        }
        return ids.ToArray();
    }

    private static HashSet<Guid> SuccessfulChatRecovery(List<ChatTurn> turns)
    {
        var result = new HashSet<Guid>();
        foreach (var success in turns.Where(SuccessfulChatTurn))
        {
            result.Add(success.Id);
            var current = success; var seen = new HashSet<Guid> { success.Id };
            for (var depth = 0; depth < 16 && current.RetryOfTurnId is { } parentId; depth++)
            {
                var parent = turns.SingleOrDefault(x => x.Id == parentId && x.OrganizationId == success.OrganizationId &&
                    x.ConversationId == success.ConversationId && x.TargetAgentOrganizationUserId == success.TargetAgentOrganizationUserId);
                if (parent is null || !seen.Add(parent.Id)) break;
                result.Add(parent.Id); current = parent;
            }
        }
        return result;
    }
}
