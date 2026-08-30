using CSweet.Application.Communications;
using CSweet.Application.Core;
using CSweet.Communications.Abstractions;
using CSweet.Contracts.Communications;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Communications;

public sealed class CommunicationRouter(CSweetDbContext db, IChatTurnService turns) : ICommunicationRouter
{
    public async Task<CommunicationActionResponse> RouteInboundAsync(NormalizedCommunicationEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (envelope.IsBot || envelope.IsWebhook) return new(true, null, "Automated provider message ignored.");
        if (string.IsNullOrWhiteSpace(envelope.Content) || string.IsNullOrWhiteSpace(envelope.SenderExternalId) || string.IsNullOrWhiteSpace(envelope.MessageExternalId))
            return new(false, "unsupported_message", "A text message and sender are required.");

        var connection = await db.CommunicationConnections.SingleOrDefaultAsync(x =>
            x.ProviderKey == CommunicationProviderKeys.Discord && x.WorkspaceExternalId == envelope.WorkspaceExternalId &&
            x.Status != CommunicationConnectionStatus.Paused && x.Status != CommunicationConnectionStatus.Disconnected, cancellationToken);
        if (connection is null) return new(false, "workspace_not_connected", "The Discord workspace is not connected.");
        if (await db.ExternalMessageReferences.AnyAsync(x => x.ConnectionId == connection.Id && x.MessageExternalId == envelope.MessageExternalId, cancellationToken))
            return new(true, null, "Duplicate provider message ignored.");

        var identity = await db.ExternalIdentityLinks.SingleOrDefaultAsync(x => x.ConnectionId == connection.Id &&
            x.ExternalUserId == envelope.SenderExternalId && x.IsVerified && x.RevokedAt == null, cancellationToken);
        if (identity is null && connection.PluginInstallationId is Guid pluginInstallationId)
        {
            var applicationUserId = await db.ExternalIdentities.AsNoTracking().Where(x =>
                    x.PluginInstallationId == pluginInstallationId && x.ProviderKey == connection.ProviderKey &&
                    x.ExternalUserId == envelope.SenderExternalId && x.RevokedAt == null)
                .Select(x => (Guid?)x.ApplicationUserId).SingleOrDefaultAsync(cancellationToken);
            var member = applicationUserId is null ? null : await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
                x.OrganizationId == connection.OrganizationId && x.ApplicationUserId == applicationUserId &&
                x.EmployeeType == EmployeeType.Human && x.IsActive, cancellationToken);
            if (member is not null)
            {
                identity = new ExternalIdentityLink
                {
                    Id = Guid.NewGuid(), ConnectionId = connection.Id, OrganizationId = connection.OrganizationId,
                    ApplicationUserId = member.ApplicationUserId!.Value, OrganizationUserId = member.Id,
                    ExternalUserId = envelope.SenderExternalId, IsVerified = true, CreatedAt = DateTimeOffset.UtcNow
                };
                db.ExternalIdentityLinks.Add(identity);
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        if (identity is null) return new(false, "identity_not_linked", "Link this Discord account to a C-Sweet human employee first.");

        var channelResource = string.IsNullOrWhiteSpace(envelope.ChannelExternalId) ? null :
            await db.ManagedExternalResources.SingleOrDefaultAsync(x => x.ConnectionId == connection.Id &&
                x.ExternalId == envelope.ChannelExternalId && x.Kind == ManagedResourceKind.Channel && !x.IsArchived, cancellationToken);
        var isDirect = envelope.Metadata?.TryGetValue("isDirect", out var direct) == true && bool.TryParse(direct, out var parsed) && parsed;
        if (isDirect && envelope.Content.StartsWith("/talk", StringComparison.OrdinalIgnoreCase))
            return await SelectDirectTargetAsync(connection, identity, envelope.Content, cancellationToken);
        var targets = await ResolveTargetsAsync(connection, identity, channelResource, envelope, isDirect, cancellationToken);
        if (targets.Count == 0)
        {
            var hasAgents = await db.CoreOrganizationUsers.AnyAsync(x => x.OrganizationId == connection.OrganizationId &&
                x.EmployeeType == EmployeeType.Agent && x.IsActive, cancellationToken);
            return new(false, hasAgents ? "agent_selection_required" : "no_agent_employees",
                hasAgents ? "Select an employee with /talk or reply to an employee message." : "Add an agent employee in C-Sweet before starting a conversation.");
        }

        var conversation = await GetOrCreateConversationAsync(connection.OrganizationId, identity.OrganizationUserId,
            channelResource, isDirect, targets[0], cancellationToken);
        ConversationMessage? firstMessage = null;
        foreach (var target in targets.Distinct())
        {
            var result = await turns.StartForAgentAsync(connection.OrganizationId, conversation.Id, target,
                envelope.Content, identity.OrganizationUserId, "Discord", envelope.ChannelExternalId,
                $"discord:{connection.Id:D}:{envelope.MessageExternalId}:{target:D}",
                cancellationToken: cancellationToken);
            if (result is not null && firstMessage is null)
                firstMessage = await db.CoreConversationMessages.SingleAsync(x => x.Id == result.UserMessage.Id, cancellationToken);
        }
        if (firstMessage is null) return new(false, "agent_unavailable", "The selected employee could not accept a new turn.");
        db.ExternalMessageReferences.Add(new ExternalMessageReference
        {
            Id = Guid.NewGuid(), ConnectionId = connection.Id, ConversationMessageId = firstMessage.Id,
            ChannelExternalId = envelope.ChannelExternalId ?? string.Empty, MessageExternalId = envelope.MessageExternalId,
            ThreadExternalId = envelope.ThreadExternalId, IsInbound = true, CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        return new(true, null, $"Message routed to {targets.Count} employee(s).");
    }

    private async Task<List<Guid>> ResolveTargetsAsync(CommunicationConnection connection, ExternalIdentityLink identity,
        ManagedExternalResource? channel, NormalizedCommunicationEnvelope envelope, bool isDirect, CancellationToken cancellationToken)
    {
        if (isDirect)
        {
            if (!string.IsNullOrWhiteSpace(envelope.ReplyToExternalId))
            {
                var replyTarget = await db.ChatTurns.Where(turn => db.ExternalMessageReferences.Any(reference =>
                        reference.ConnectionId == connection.Id && reference.MessageExternalId == envelope.ReplyToExternalId &&
                        !reference.IsInbound && reference.ConversationMessageId == turn.AssistantMessageId))
                    .Select(x => x.TargetAgentOrganizationUserId).SingleOrDefaultAsync(cancellationToken);
                if (replyTarget != Guid.Empty) return await ActiveAgents(connection.OrganizationId, [replyTarget], cancellationToken);
            }
            if (!identity.ActiveDirectAgentOrganizationUserId.HasValue) return [];
            var selected = await ActiveAgents(connection.OrganizationId, [identity.ActiveDirectAgentOrganizationUserId.Value], cancellationToken);
            if (selected.Count == 0)
            {
                identity.ActiveDirectAgentOrganizationUserId = null;
                await db.SaveChangesAsync(cancellationToken);
            }
            return selected;
        }
        if (channel?.OrganizationUserId is Guid channelAgent)
            return await ActiveAgents(connection.OrganizationId, [channelAgent], cancellationToken);

        if (envelope.MentionExternalIds.Count > 0)
        {
            var mentioned = await db.ManagedExternalResources.Where(x => x.ConnectionId == connection.Id &&
                    x.Kind == ManagedResourceKind.Role && x.OrganizationUserId != null && envelope.MentionExternalIds.Contains(x.ExternalId))
                .Select(x => x.OrganizationUserId!.Value).ToListAsync(cancellationToken);
            if (mentioned.Count > 0) return await ActiveAgents(connection.OrganizationId, mentioned, cancellationToken);
        }

        if (channel?.TeamId is not null || channel?.WorkstreamId is not null)
        {
            var conversation = await db.CoreConversations.Include(x => x.Participants)
                .SingleOrDefaultAsync(x => x.OrganizationId == connection.OrganizationId &&
                    x.TeamId == channel.TeamId && x.WorkstreamId == channel.WorkstreamId &&
                    x.Kind == (channel.TeamId != null ? ConversationKind.Team : ConversationKind.Project), cancellationToken);
            var coordinator = conversation?.Participants.FirstOrDefault(x => x.LeftAt == null && x.Role == ConversationParticipantRole.Coordinator)?.OrganizationUserId;
            if (coordinator.HasValue) return await ActiveAgents(connection.OrganizationId, [coordinator.Value], cancellationToken);
            var senior = await db.CoreOrganizationUsers.Where(x => x.OrganizationId == connection.OrganizationId &&
                    x.EmployeeType == EmployeeType.Agent && x.IsActive)
                .OrderBy(x => x.ReportsToOrganizationUserId != null).ThenByDescending(x => x.PermissionLevel).ThenBy(x => x.CreatedAt)
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
            return senior.HasValue ? [senior.Value] : [];
        }
        return [];
    }

    private async Task<CommunicationActionResponse> SelectDirectTargetAsync(CommunicationConnection connection,
        ExternalIdentityLink identity, string command, CancellationToken cancellationToken)
    {
        var selection = command[5..].Trim();
        if (selection.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            identity.ActiveDirectAgentOrganizationUserId = null;
            await db.SaveChangesAsync(cancellationToken);
            return new(true, null, "Direct-message employee selection cleared.");
        }
        const string prefix = "employee:";
        if (!selection.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return new(false, "invalid_talk_command", "Use /talk employee:<name> or /talk clear.");
        var name = selection[prefix.Length..].Trim();
        var matches = await db.CoreOrganizationUsers.Where(x => x.OrganizationId == connection.OrganizationId &&
                x.EmployeeType == EmployeeType.Agent && x.IsActive && x.DisplayName.ToLower() == name.ToLower())
            .Select(x => new { x.Id, x.DisplayName }).Take(2).ToListAsync(cancellationToken);
        if (matches.Count == 0) return new(false, "agent_unavailable", "No active agent employee has that name.");
        if (matches.Count > 1) return new(false, "agent_ambiguous", "More than one employee has that name. Use the Discord employee selection menu.");
        identity.ActiveDirectAgentOrganizationUserId = matches[0].Id;
        await db.SaveChangesAsync(cancellationToken);
        return new(true, null, $"Direct messages will now go to {matches[0].DisplayName}.");
    }

    private async Task<List<Guid>> ActiveAgents(Guid organizationId, IEnumerable<Guid> ids, CancellationToken cancellationToken) =>
        await db.CoreOrganizationUsers.Where(x => ids.Contains(x.Id) && x.OrganizationId == organizationId &&
            x.EmployeeType == EmployeeType.Agent && x.IsActive).Select(x => x.Id).ToListAsync(cancellationToken);

    private async Task<Conversation> GetOrCreateConversationAsync(Guid organizationId, Guid humanId,
        ManagedExternalResource? channel, bool isDirect, Guid targetAgentId, CancellationToken cancellationToken)
    {
        var kind = isDirect ? ConversationKind.DirectHumanAgent : channel?.OrganizationUserId != null ? ConversationKind.AgentChannel :
            channel?.TeamId != null ? ConversationKind.Team : ConversationKind.Project;
        var conversation = await db.CoreConversations.Include(x => x.Participants).FirstOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.Kind == kind &&
            (isDirect ? x.AgentOrganizationUserId == targetAgentId && x.InitiatedByOrganizationUserId == humanId :
                x.TeamId == channel!.TeamId && x.WorkstreamId == channel.WorkstreamId), cancellationToken);
        if (conversation is not null) return conversation;
        var now = DateTimeOffset.UtcNow;
        conversation = new Conversation
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, Kind = kind,
            AgentOrganizationUserId = isDirect || channel?.OrganizationUserId != null ? targetAgentId : null,
            InitiatedByOrganizationUserId = humanId, TeamId = channel?.TeamId, WorkstreamId = channel?.WorkstreamId,
            Title = channel?.DisplayName, IsPrivate = isDirect, IsDeletionProtected = isDirect,
            CreatedAt = now, UpdatedAt = now
        };
        conversation.Participants.Add(new ConversationParticipant
        {
            Id = Guid.NewGuid(), OrganizationUserId = humanId, Role = ConversationParticipantRole.Member, JoinedAt = now
        });
        conversation.Participants.Add(new ConversationParticipant
        {
            Id = Guid.NewGuid(), OrganizationUserId = targetAgentId,
            Role = channel?.TeamId != null || channel?.WorkstreamId != null ? ConversationParticipantRole.Coordinator : ConversationParticipantRole.Member,
            JoinedAt = now
        });
        db.CoreConversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);
        return conversation;
    }
}
