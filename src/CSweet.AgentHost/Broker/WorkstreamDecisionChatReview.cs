using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

internal static class WorkstreamDecisionChatReview
{
    internal static async Task PresentAsync(CSweetDbContext db, ICommunicationHubService hub,
        IExecutiveDecisionService decisions, WorkstreamDecisionRecord source, CancellationToken token)
    {
        if (source.Status != W.DecisionStatuses.Pending || source.RequestedByInstallationId is not { } installation) return;
        var authority = await db.WorkstreamAuthorityEnvelopes.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == source.OrganizationId && x.WorkstreamId == source.WorkstreamId, token);
        if (authority is not null && (authority.ExpiresAt is null || authority.ExpiresAt > DateTimeOffset.UtcNow))
        {
            var humanRequired = JsonSerializer.Deserialize<string[]>(authority.HumanRequiredActionKeysJson) ?? [];
            var agentAuthorized = JsonSerializer.Deserialize<string[]>(authority.AgentAuthorizedActionKeysJson) ?? [];
            // Agent-authorized decisions are delivered to responsible agents through the durable
            // project event. A parallel owner card becomes stale as soon as an agent decides.
            if (!humanRequired.Contains(source.AuthorityRuleKey, StringComparer.Ordinal) &&
                !humanRequired.Contains("*", StringComparer.Ordinal) &&
                (agentAuthorized.Contains(source.AuthorityRuleKey, StringComparer.Ordinal) ||
                 agentAuthorized.Contains("*", StringComparer.Ordinal)))
                return;
        }
        var requester = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == source.RequestedByOrganizationUserId && x.OrganizationId == source.OrganizationId &&
            x.AgentInstallationId == installation && x.IsActive, token);
        if (requester?.ReportsToOrganizationUserId is not { } managerId) return;
        // A manager agent receives the durable decision event and decides whether
        // to resolve or escalate it. Never bypass that reporting relationship.
        var owner = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == managerId && x.OrganizationId == source.OrganizationId && x.IsActive &&
            x.EmployeeType == EmployeeType.Human &&
            x.PermissionLevel == OrganizationPermissionLevel.Owner, token);
        if (owner is null) return;
        var options = JsonSerializer.Deserialize<List<W.DecisionOption>>(source.OptionsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        var key = $"workstream-review:{source.Id:N}:{owner.Id:N}";
        if (await db.ExecutiveDecisions.AnyAsync(x => x.RequestingInstallationId == installation && x.IdempotencyKey == key, token))
            return;
        var chat = await hub.CreateAsync(source.OrganizationId, source.RequestedByOrganizationUserId,
            new CreateCommunicationChatRequest(null, "Project decision review", true, true, [owner.Id],
                AudienceWorkstreamIds: [source.WorkstreamId]) { WorkstreamId = source.WorkstreamId }, token);
        if (!chat.Succeeded || chat.Chat is null) throw new InvalidOperationException(chat.Message);
        var sent = await hub.SendAsync(source.OrganizationId, chat.Chat.Id, source.RequestedByOrganizationUserId,
            new SendCommunicationMessageRequest(source.Summary + "\n\nPlease review this project decision. Your response will be recorded against the project. " +
                (options.Any(x => x.Id == "provide-direction") ? "Use Something else to provide specific direction." : "Select an option below."), key), token)
            ?? throw new InvalidOperationException("The project decision message could not be created.");
        await decisions.CreateAsync(new CreateExecutiveDecisionCommand(source.OrganizationId, chat.Chat.Id, null,
            sent.Message.Id, installation, source.Summary,
            options.Select(x => new CreateExecutiveDecisionOption(x.Id, x.Label, x.Description)).ToArray(),
            source.RecommendedOptionId, key) { WorkstreamDecisionId = source.Id }, token);
    }
}
