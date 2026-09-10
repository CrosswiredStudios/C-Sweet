using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Communications;

public sealed partial class ExecutiveDecisionService
{
    public async Task<IReadOnlyList<PendingAgentQuestionResponse>> ListPendingForUserAsync(
        Guid organizationId, Guid actorOrganizationUserId, CancellationToken cancellationToken = default)
    {
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.Id == actorOrganizationUserId && x.IsActive &&
            x.EmployeeType == EmployeeType.Human, cancellationToken);
        if (actor is null) return [];
        var rows = await (from decision in db.ExecutiveDecisions.AsNoTracking()
            join chat in db.CoreConversations.AsNoTracking() on decision.ConversationId equals chat.Id
            join agent in db.CoreOrganizationUsers.AsNoTracking() on chat.AgentOrganizationUserId equals agent.Id
            where decision.OrganizationId == organizationId && chat.OrganizationId == organizationId &&
                agent.OrganizationId == organizationId && agent.IsActive &&
                agent.AgentInstallationId == decision.RequestingInstallationId &&
                chat.ArchivedAt == null && chat.MergedIntoConversationId == null &&
                decision.Status == ExecutiveDecisionStatus.Pending &&
                db.ConversationParticipants.Any(p => p.ConversationId == chat.Id &&
                    p.OrganizationUserId == actorOrganizationUserId && p.LeftAt == null)
            orderby decision.CreatedAt, decision.Id
            select new { Decision = decision, AgentId = agent.Id, AgentName = agent.DisplayName }).ToListAsync(cancellationToken);
        var linkedIds = rows.Select(x => ReadDecisionOptions(x.Decision.OptionsJson).WorkstreamDecisionId)
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var sources = await db.WorkstreamDecisions.AsNoTracking().Where(x =>
            x.OrganizationId == organizationId && linkedIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        return rows.Where(x =>
        {
            var stored = ReadDecisionOptions(x.Decision.OptionsJson);
            return stored.WorkstreamDecisionId is not { } id ||
                (actor.PermissionLevel == OrganizationPermissionLevel.Owner && sources.TryGetValue(id, out var source) &&
                 source.Status == "Pending" && source.Revision == stored.WorkstreamDecisionRevision);
        }).Select(x => new PendingAgentQuestionResponse(x.Decision.ConversationId,
            x.AgentId, x.AgentName, ToCard(x.Decision))).ToArray();
    }
}
