using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Communications;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Communications;

public sealed class CommunicationHubService(
    CSweetDbContext db,
    IAuditEventWriter audit,
    IChatTurnService turns,
    IExecutiveDecisionService? decisions = null,
    IResourceChangeService? resourceChanges = null) : ICommunicationHubService
{
    public async Task<Guid?> ResolveOrganizationUserIdAsync(
        Guid organizationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default) =>
        await db.CoreOrganizationUsers
            .Where(x => x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId && x.IsActive)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<CommunicationHubResponse?> GetAsync(
        Guid organizationId,
        Guid actorOrganizationUserId,
        CancellationToken cancellationToken = default)
    {
        var actor = await ActiveUserAsync(organizationId, actorOrganizationUserId, cancellationToken);
        if (actor is null) return null;

        var people = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IsActive)
            .Include(x => x.Role)
            .OrderBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);

        var chats = await db.CoreConversations.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ArchivedAt == null &&
                x.Participants.Any(p => p.OrganizationUserId == actorOrganizationUserId && p.LeftAt == null))
            .Include(x => x.Participants).ThenInclude(x => x.OrganizationUser)
            .Include(x => x.Messages)
            .OrderByDescending(x => x.UpdatedAt)
            .ToListAsync(cancellationToken);

        var roles = await db.CoreRoles.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var workstreams = await db.Workstreams.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Status != WorkstreamStatus.Cancelled)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var responsibilities = await db.Responsibilities.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.WorkstreamId != null && x.Status == "Active")
            .ToListAsync(cancellationToken);

        var audiences = roles.Select(role => new CommunicationAudienceResponse(
                "Role", role.Id, role.Name, people.Where(x => x.RoleId == role.Id).Select(x => x.Id).ToList()))
            .Concat(workstreams.Select(workstream => new CommunicationAudienceResponse(
                "Workstream", workstream.Id, workstream.Name,
                responsibilities.Where(x => x.WorkstreamId == workstream.Id).Select(x => x.OrganizationUserId)
                    .Append(workstream.AccountableManagerOrganizationUserId ?? Guid.Empty)
                    .Where(x => x != Guid.Empty).Distinct().ToList())))
            .ToList();

        return new CommunicationHubResponse(
            actor.Id,
            actor.PermissionLevel >= OrganizationPermissionLevel.Manager,
            chats.Select(x => MapChat(x, actor)).ToList(),
            people.Select(x => new CommunicationPersonResponse(
                x.Id, x.DisplayName, x.EmployeeType.ToString(), x.RoleId, x.Role?.Name)).ToList(),
            audiences);
    }

    public Task<bool> CanAccessChatAsync(
        Guid organizationId,
        Guid chatId,
        Guid actorOrganizationUserId,
        CancellationToken cancellationToken = default) =>
        IsActiveMemberAsync(organizationId, chatId, actorOrganizationUserId, cancellationToken);

    public async Task<IReadOnlyList<CommunicationHubMessageResponse>?> ListMessagesAsync(
        Guid organizationId,
        Guid chatId,
        Guid actorOrganizationUserId,
        CancellationToken cancellationToken = default)
    {
        if (!await IsActiveMemberAsync(organizationId, chatId, actorOrganizationUserId, cancellationToken)) return null;

        var users = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var messages = await db.CoreConversationMessages.AsNoTracking()
            .Where(x => x.ConversationId == chatId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        var decisionCards = decisions is null
            ? new Dictionary<Guid, ExecutiveDecisionCardResponse>()
            : await decisions.ListForMessagesAsync(organizationId, chatId, cancellationToken);
        var actions = await db.SuggestedUserActions.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ConversationId == chatId && x.Status == "Pending")
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        var resourceChangeCards = resourceChanges is null
            ? new Dictionary<Guid, Contracts.Core.ResourceChangeRequestResponse>()
            : (await resourceChanges.ListForDashboardAsync(organizationId, cancellationToken))
                .ToDictionary(x => x.Id);
        return messages.Select(x => MapMessage(
            x,
            users,
            decisionCards,
            actions.Where(action =>
                action.ConversationMessageId == x.Id ||
                (x.Role == ConversationRole.Assistant &&
                 x.ChatTurnId.HasValue &&
                 action.ChatTurnId == x.ChatTurnId)).Select(ToAction).ToList(),
            x.CorrelationId != Guid.Empty && resourceChangeCards.TryGetValue(x.CorrelationId, out var resourceChange)
                ? resourceChange
                : null))
            .ToList();
    }

    public async Task<CommunicationUnreadSummaryResponse?> GetUnreadSummaryAsync(
        Guid organizationId,
        Guid actorOrganizationUserId,
        CancellationToken cancellationToken = default)
    {
        if (await ActiveUserAsync(organizationId, actorOrganizationUserId, cancellationToken) is null) return null;
        var chats = await db.CoreConversations.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ArchivedAt == null &&
                x.Participants.Any(p => p.OrganizationUserId == actorOrganizationUserId && p.LeftAt == null))
            .Select(x => new
            {
                x.Id,
                LastRead = x.Participants.Where(p => p.OrganizationUserId == actorOrganizationUserId && p.LeftAt == null)
                    .Select(p => p.LastReadMessageSequence).Single(),
                Messages = x.Messages.Where(m => m.SenderOrganizationUserId != actorOrganizationUserId)
                    .Select(m => m.Sequence)
            })
            .ToListAsync(cancellationToken);
        var counts = chats.ToDictionary(x => x.Id, x => x.Messages.Count(sequence => sequence > x.LastRead));
        return new CommunicationUnreadSummaryResponse(counts.Values.Sum(), counts);
    }

    public async Task<CommunicationUnreadSummaryResponse?> MarkReadAsync(
        Guid organizationId,
        Guid chatId,
        Guid actorOrganizationUserId,
        long throughMessageSequence,
        CancellationToken cancellationToken = default)
    {
        var participant = await db.ConversationParticipants
            .Include(x => x.Conversation)
            .SingleOrDefaultAsync(x => x.ConversationId == chatId && x.OrganizationUserId == actorOrganizationUserId &&
                x.LeftAt == null && x.Conversation!.OrganizationId == organizationId && x.Conversation.ArchivedAt == null,
                cancellationToken);
        if (participant is null) return null;
        var maximum = await db.CoreConversationMessages.Where(x => x.ConversationId == chatId)
            .Select(x => (long?)x.Sequence).MaxAsync(cancellationToken) ?? 0;
        var target = Math.Clamp(throughMessageSequence, 0, maximum);
        if (target > participant.LastReadMessageSequence)
        {
            participant.LastReadMessageSequence = target;
            await db.SaveChangesAsync(cancellationToken);
        }
        return await GetUnreadSummaryAsync(organizationId, actorOrganizationUserId, cancellationToken);
    }

    public async Task<CommunicationHubActionResponse> CreateAsync(
        Guid organizationId,
        Guid actorOrganizationUserId,
        CreateCommunicationChatRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await ActiveUserAsync(organizationId, actorOrganizationUserId, cancellationToken);
        if (actor is null) return Failure("actor_not_found", "The chat creator is not an active member of this organization.");
        if (!request.IsDirect && actor.PermissionLevel < OrganizationPermissionLevel.Manager && actor.EmployeeType != EmployeeType.Agent)
            return Failure("not_authorized", "Only managers and granted agents can create group chats.");

        var memberIds = await ExpandMembersAsync(organizationId, actor.Id, request.ParticipantOrganizationUserIds,
            request.AudienceRoleIds, request.AudienceWorkstreamIds, cancellationToken);
        var validation = await ValidateMembersAsync(organizationId, memberIds, request.IsDirect, cancellationToken);
        if (validation is not null) return validation;

        if (request.IsDirect)
        {
            var candidates = await db.CoreConversations
                .Where(x => x.OrganizationId == organizationId && x.ArchivedAt == null &&
                    x.Kind == ConversationKind.DirectHumanAgent && x.Participants.Count(p => p.LeftAt == null) == 2 &&
                    x.Participants.Any(p => p.OrganizationUserId == actor.Id && p.LeftAt == null))
                .Include(x => x.Participants).ThenInclude(x => x.OrganizationUser)
                .Include(x => x.Messages)
                .ToListAsync(cancellationToken);
            var existing = candidates.FirstOrDefault(x => x.Participants.Where(p => p.LeftAt == null)
                .Select(p => p.OrganizationUserId).ToHashSet().SetEquals(memberIds));
            if (existing is not null) return Success("Direct chat already exists.", MapChat(existing, actor));
        }

        var members = await db.CoreOrganizationUsers.Where(x => memberIds.Contains(x.Id)).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var otherAgent = request.IsDirect ? members.SingleOrDefault(x => x.Id != actor.Id && x.EmployeeType == EmployeeType.Agent) : null;
        var chat = new Conversation
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, InitiatedByOrganizationUserId = actor.Id,
            AgentOrganizationUserId = otherAgent?.Id,
            Kind = request.IsDirect ? ConversationKind.DirectHumanAgent : ConversationKind.Team,
            Title = request.IsDirect ? null : request.Title?.Trim(),
            Description = Clean(request.Description), IsPrivate = request.IsDirect || request.IsPrivate,
            IsDeletionProtected = otherAgent is not null,
            CreatedAt = now, UpdatedAt = now
        };
        if (!request.IsDirect && string.IsNullOrWhiteSpace(chat.Title))
            return Failure("title_required", "Group chats require a title.");

        foreach (var member in members)
            chat.Participants.Add(new ConversationParticipant
            {
                Id = Guid.NewGuid(), OrganizationUserId = member.Id,
                OrganizationUser = member,
                Role = member.Id == actor.Id ? ConversationParticipantRole.Coordinator : ConversationParticipantRole.Member,
                JoinedAt = now
            });

        db.CoreConversations.Add(chat);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("communication.chat.created", "Conversation", chat.Id,
            $"{actor.DisplayName} created {(request.IsDirect ? "a direct chat" : $"#{chat.Title}")}.", cancellationToken: cancellationToken);
        return Success("Chat created.", MapChat(chat, actor));
    }

    public async Task<CommunicationHubActionResponse> UpdateAsync(
        Guid organizationId,
        Guid chatId,
        Guid actorOrganizationUserId,
        UpdateCommunicationChatRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await ActiveUserAsync(organizationId, actorOrganizationUserId, cancellationToken);
        var chat = await db.CoreConversations
            .Where(x => x.Id == chatId && x.OrganizationId == organizationId && x.ArchivedAt == null)
            .Include(x => x.Participants).ThenInclude(x => x.OrganizationUser)
            .Include(x => x.Messages)
            .SingleOrDefaultAsync(cancellationToken);
        if (actor is null || chat is null) return Failure("chat_not_found", "The chat was not found.");
        if (chat.IsDeletionProtected) return Failure("protected_chat_immutable", "This agent-instance conversation cannot be modified.");
        if (chat.Kind == ConversationKind.DirectHumanAgent) return Failure("direct_chat_immutable", "Direct-chat membership cannot be modified.");
        if (!CanManage(chat, actor)) return Failure("not_authorized", "You do not have permission to modify this chat.");
        if (string.IsNullOrWhiteSpace(request.Title)) return Failure("title_required", "A chat title is required.");

        var memberIds = await ExpandMembersAsync(organizationId, actor.Id, request.ParticipantOrganizationUserIds,
            request.AudienceRoleIds, request.AudienceWorkstreamIds, cancellationToken);
        var validation = await ValidateMembersAsync(organizationId, memberIds, false, cancellationToken);
        if (validation is not null) return validation;
        var memberLookup = await db.CoreOrganizationUsers.Where(x => memberIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        foreach (var participant in chat.Participants)
        {
            if (memberIds.Contains(participant.OrganizationUserId))
            {
                participant.LeftAt = null;
                if (participant.OrganizationUserId == actor.Id) participant.Role = ConversationParticipantRole.Coordinator;
            }
            else participant.LeftAt ??= now;
        }
        foreach (var memberId in memberIds.Where(id => chat.Participants.All(x => x.OrganizationUserId != id)))
            chat.Participants.Add(new ConversationParticipant
            {
                Id = Guid.NewGuid(), OrganizationUserId = memberId,
                OrganizationUser = memberLookup[memberId],
                Role = memberId == actor.Id ? ConversationParticipantRole.Coordinator : ConversationParticipantRole.Member,
                JoinedAt = now
            });

        chat.Title = request.Title.Trim();
        chat.Description = Clean(request.Description);
        chat.IsPrivate = request.IsPrivate;
        chat.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("communication.chat.modified", "Conversation", chat.Id,
            $"{actor.DisplayName} updated #{chat.Title}.", cancellationToken: cancellationToken);
        return Success("Chat updated.", MapChat(chat, actor));
    }

    public async Task<CommunicationHubActionResponse> ArchiveAsync(
        Guid organizationId,
        Guid chatId,
        Guid actorOrganizationUserId,
        CancellationToken cancellationToken = default)
    {
        var actor = await ActiveUserAsync(organizationId, actorOrganizationUserId, cancellationToken);
        var chat = await db.CoreConversations.Include(x => x.Participants)
            .SingleOrDefaultAsync(x => x.Id == chatId && x.OrganizationId == organizationId && x.ArchivedAt == null, cancellationToken);
        if (actor is null || chat is null) return Failure("chat_not_found", "The chat was not found.");
        if (chat.IsDeletionProtected) return Failure("protected_chat_delete_denied", "This agent-instance conversation cannot be deleted.");
        if (chat.Kind == ConversationKind.DirectHumanAgent) return Failure("direct_chat_delete_denied", "Direct chats cannot be deleted.");
        if (!CanManage(chat, actor)) return Failure("not_authorized", "You do not have permission to delete this chat.");

        chat.ArchivedAt = DateTimeOffset.UtcNow;
        chat.UpdatedAt = chat.ArchivedAt.Value;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("communication.chat.archived", "Conversation", chat.Id,
            $"{actor.DisplayName} archived #{chat.Title} without deleting its history.", cancellationToken: cancellationToken);
        return Success("Chat archived. Its history was preserved.");
    }

    public async Task<CommunicationMessageSendResponse?> SendAsync(
        Guid organizationId,
        Guid chatId,
        Guid actorOrganizationUserId,
        SendCommunicationMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await ActiveUserAsync(organizationId, actorOrganizationUserId, cancellationToken);
        var chat = await db.CoreConversations
            .Include(x => x.Participants)
            .SingleOrDefaultAsync(x => x.Id == chatId && x.OrganizationId == organizationId && x.ArchivedAt == null &&
                x.Participants.Any(p => p.OrganizationUserId == actorOrganizationUserId && p.LeftAt == null), cancellationToken);
        if (actor is null || chat is null || string.IsNullOrWhiteSpace(request.Content)) return null;

        var suppliedIdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? null
            : request.IdempotencyKey.Trim();
        if (suppliedIdempotencyKey?.Length > 160) return null;
        var idempotencyKey = suppliedIdempotencyKey is null
            ? null
            : $"communication-message:{actor.Id:N}:{suppliedIdempotencyKey}";
        if (idempotencyKey is not null)
        {
            var existing = await db.CoreConversationMessages.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ConversationId == chat.Id &&
                    x.SenderOrganizationUserId == actor.Id && x.IdempotencyKey == idempotencyKey, cancellationToken);
            if (existing is not null)
                return new CommunicationMessageSendResponse(
                    MapMessage(existing, new Dictionary<Guid, OrganizationUser> { [actor.Id] = actor }),
                    existing.ChatTurnId.HasValue
                        ? await turns.GetAsync(organizationId, existing.ChatTurnId.Value, cancellationToken)
                        : null);
        }

        var targetAgentId = actor.EmployeeType == EmployeeType.Human &&
            chat.Kind == ConversationKind.DirectHumanAgent &&
            chat.AgentOrganizationUserId.HasValue &&
            chat.Participants.Any(x => x.OrganizationUserId == chat.AgentOrganizationUserId.Value && x.LeftAt == null)
                ? chat.AgentOrganizationUserId
                : null;
        if (targetAgentId.HasValue)
        {
            var started = await turns.StartForAgentAsync(
                organizationId,
                chat.Id,
                targetAgentId.Value,
                request.Content,
                actor.Id,
                CommunicationProviderKeys.InApp,
                idempotencyKey: idempotencyKey,
                cancellationToken: cancellationToken);
            if (started is null) return null;
            var response = new CommunicationMessageSendResponse(
                new CommunicationHubMessageResponse(
                    started.UserMessage.Id,
                    started.UserMessage.Sequence,
                    chat.Id,
                    actor.Id,
                    actor.DisplayName,
                    actor.EmployeeType.ToString(),
                    started.UserMessage.Content,
                    started.UserMessage.CreatedAt,
                    started.Turn.Id),
                started.Turn);
            await WriteMessageAuditAsync(
                organizationId, actor, chat, response.Message,
                started.UserMessage.CorrelationId, cancellationToken);
            return response;
        }

        var now = DateTimeOffset.UtcNow;
        var message = new ConversationMessage
        {
            Id = Guid.NewGuid(), ConversationId = chat.Id, SenderOrganizationUserId = actor.Id,
            Role = actor.EmployeeType == EmployeeType.Agent ? ConversationRole.Assistant : ConversationRole.User,
            Content = request.Content.Trim(), CorrelationId = Guid.NewGuid(), DeliveryIntent = CommunicationDeliveryIntent.Inform,
            SourceProvider = "InApp", IdempotencyKey = idempotencyKey, CreatedAt = now
        };
        chat.UpdatedAt = now;
        db.CoreConversationMessages.Add(message);
        await db.SaveChangesAsync(cancellationToken);
        var sent = new CommunicationMessageSendResponse(
            new CommunicationHubMessageResponse(message.Id, message.Sequence, chat.Id, actor.Id, actor.DisplayName,
                actor.EmployeeType.ToString(), message.Content, message.CreatedAt, message.ChatTurnId));
        await WriteMessageAuditAsync(
            organizationId, actor, chat, sent.Message, message.CorrelationId, cancellationToken);
        return sent;
    }

    private async Task WriteMessageAuditAsync(
        Guid organizationId,
        OrganizationUser actor,
        Conversation chat,
        CommunicationHubMessageResponse message,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        var recipientIds = chat.Participants
            .Where(x => x.LeftAt == null && x.OrganizationUserId != actor.Id)
            .Select(x => x.OrganizationUserId)
            .Distinct()
            .ToList();
        var recipients = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => recipientIds.Contains(x.Id))
            .Select(x => new
            {
                x.Id,
                x.DisplayName,
                EmployeeType = x.EmployeeType.ToString(),
                x.AgentInstallationId
            })
            .OrderBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);
        var directRecipient = recipients.Count == 1 ? recipients[0] : null;
        var targetName = directRecipient?.DisplayName ??
            (!string.IsNullOrWhiteSpace(chat.Title) ? $"#{chat.Title}" : $"{recipients.Count} recipients");
        var contentBytes = Encoding.UTF8.GetBytes(message.Content);

        await audit.AppendAsync(new AuditEventWriteRequest(
            "communication.message.sent",
            "Communication",
            "Outbound",
            "Delivered",
            organizationId,
            "ConversationMessage",
            message.Id,
            $"{actor.DisplayName} sent a message to {targetName}.",
            JsonSerializer.Serialize(new
            {
                chatId = chat.Id,
                chatKind = chat.Kind.ToString(),
                message.Sequence,
                message.ChatTurnId,
                recipients,
                contentBytes = contentBytes.Length,
                contentSha256 = Convert.ToHexString(SHA256.HashData(contentBytes))
            }),
            ExternalMessageId: message.Id.ToString("D"),
            CorrelationId: correlationId.ToString("D"),
            Actor: new AuditActor(
                actor.EmployeeType == EmployeeType.Agent ? "Agent" : "Human",
                true,
                actor.ApplicationUserId,
                actor.Id,
                actor.DisplayName,
                actor.EmployeeType == EmployeeType.Agent ? actor.DisplayName : null,
                actor.AgentInstallationId),
            Target: new AuditTarget(
                directRecipient?.EmployeeType ?? "Conversation",
                targetName,
                directRecipient?.EmployeeType == EmployeeType.Agent.ToString()
                    ? directRecipient.DisplayName
                    : null,
                directRecipient?.AgentInstallationId),
            ContentType: "text/plain"),
            cancellationToken);
    }

    private Task<OrganizationUser?> ActiveUserAsync(Guid organizationId, Guid userId, CancellationToken token) =>
        db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.Id == userId && x.OrganizationId == organizationId && x.IsActive, token);

    private Task<bool> IsActiveMemberAsync(Guid organizationId, Guid chatId, Guid userId, CancellationToken token) =>
        db.CoreConversations.AnyAsync(x => x.Id == chatId && x.OrganizationId == organizationId && x.ArchivedAt == null &&
            x.Participants.Any(p => p.OrganizationUserId == userId && p.LeftAt == null), token);

    private async Task<HashSet<Guid>> ExpandMembersAsync(Guid organizationId, Guid actorId, IReadOnlyList<Guid>? directIds,
        IReadOnlyList<Guid>? roleIds, IReadOnlyList<Guid>? workstreamIds, CancellationToken token)
    {
        var ids = (directIds ?? []).Append(actorId).ToHashSet();
        if (roleIds?.Count > 0)
            ids.UnionWith(await db.CoreOrganizationUsers.Where(x => x.OrganizationId == organizationId && x.IsActive &&
                x.RoleId != null && roleIds.Contains(x.RoleId.Value)).Select(x => x.Id).ToListAsync(token));
        if (workstreamIds?.Count > 0)
        {
            ids.UnionWith(await db.Responsibilities.Where(x => x.OrganizationId == organizationId && x.Status == "Active" &&
                x.WorkstreamId != null && workstreamIds.Contains(x.WorkstreamId.Value)).Select(x => x.OrganizationUserId).ToListAsync(token));
            ids.UnionWith(await db.Workstreams.Where(x => x.OrganizationId == organizationId && workstreamIds.Contains(x.Id) &&
                x.AccountableManagerOrganizationUserId != null).Select(x => x.AccountableManagerOrganizationUserId!.Value).ToListAsync(token));
        }
        return ids;
    }

    private async Task<CommunicationHubActionResponse?> ValidateMembersAsync(Guid organizationId, HashSet<Guid> ids, bool direct, CancellationToken token)
    {
        if (direct && ids.Count != 2) return Failure("direct_participant_count", "A direct chat must contain exactly two people.");
        if (!direct && ids.Count < 2) return Failure("group_participant_count", "A group chat must contain at least two people.");
        if (ids.Count > 250) return Failure("participant_limit", "A chat cannot contain more than 250 people.");
        var validCount = await db.CoreOrganizationUsers.CountAsync(x => x.OrganizationId == organizationId && x.IsActive && ids.Contains(x.Id), token);
        return validCount == ids.Count ? null : Failure("invalid_participant", "Every participant must be an active member of this organization.");
    }

    private static bool CanManage(Conversation chat, OrganizationUser actor) =>
        actor.PermissionLevel >= OrganizationPermissionLevel.Manager ||
        chat.Participants.Any(x => x.OrganizationUserId == actor.Id && x.LeftAt == null && x.Role == ConversationParticipantRole.Coordinator);

    private static CommunicationChatResponse MapChat(Conversation chat, OrganizationUser actor)
    {
        var active = chat.Participants.Where(x => x.LeftAt == null).ToList();
        var direct = chat.Kind == ConversationKind.DirectHumanAgent;
        var title = chat.Title;
        if (direct)
            title = active.FirstOrDefault(x => x.OrganizationUserId != actor.Id)?.OrganizationUser?.DisplayName
                ?? active.FirstOrDefault()?.OrganizationUser?.DisplayName ?? "Direct message";
        var last = chat.Messages.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        var membership = active.FirstOrDefault(x => x.OrganizationUserId == actor.Id);
        var unreadCount = membership is null ? 0 : chat.Messages.Count(x =>
            x.Sequence > membership.LastReadMessageSequence && x.SenderOrganizationUserId != actor.Id);
        return new CommunicationChatResponse(chat.Id, title ?? "Untitled chat", chat.Description, direct, chat.IsPrivate,
            chat.IsDeletionProtected, !direct && !chat.IsDeletionProtected && CanManage(chat, actor), chat.UpdatedAt,
            active.Select(x => new CommunicationParticipantResponse(x.OrganizationUserId,
                x.OrganizationUser?.DisplayName ?? "Unknown", x.OrganizationUser?.EmployeeType.ToString() ?? "Unknown", x.Role.ToString())).ToList(),
            last?.Content, last?.CreatedAt, unreadCount);
    }

    private static CommunicationHubMessageResponse MapMessage(
        ConversationMessage message,
        IReadOnlyDictionary<Guid, OrganizationUser> users,
        IReadOnlyDictionary<Guid, ExecutiveDecisionCardResponse>? decisions = null,
        IReadOnlyList<SuggestedUserActionResponse>? actions = null,
        Contracts.Core.ResourceChangeRequestResponse? resourceChange = null)
    {
        var sender = message.SenderOrganizationUserId.HasValue && users.TryGetValue(message.SenderOrganizationUserId.Value, out var user) ? user : null;
        var isSystemAction = string.Equals(
            message.SourceProvider,
            CommunicationMessageTypes.SystemAction,
            StringComparison.Ordinal);
        return new CommunicationHubMessageResponse(message.Id, message.Sequence, message.ConversationId, message.SenderOrganizationUserId,
            isSystemAction ? "C-Sweet" : sender?.DisplayName ?? (message.Role == ConversationRole.Assistant ? "Assistant" : "Unknown"),
            isSystemAction ? "System" : sender?.EmployeeType.ToString() ?? (message.Role == ConversationRole.Assistant ? "Agent" : "Human"),
            message.Content, message.CreatedAt, message.ChatTurnId,
            !isSystemAction && message.Role == ConversationRole.Assistant && message.ChatTurnId.HasValue &&
            decisions?.TryGetValue(message.ChatTurnId.Value, out var decision) == true ? decision : null,
            actions ?? [],
            resourceChange)
        {
            MessageType = resourceChange is not null
                ? CommunicationMessageTypes.ResourceChangeApproval
                : isSystemAction
                ? CommunicationMessageTypes.SystemAction
                : CommunicationMessageTypes.Standard
        };
    }

    private static SuggestedUserActionResponse ToAction(SuggestedUserAction action) =>
        new(action.Id, action.WorkflowType, action.Label, action.Description, action.NavigationUri,
            action.Status, action.CreatedAt);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static CommunicationHubActionResponse Success(string message, CommunicationChatResponse? chat = null) => new(true, null, message, chat);
    private static CommunicationHubActionResponse Failure(string code, string message) => new(false, code, message);
}
