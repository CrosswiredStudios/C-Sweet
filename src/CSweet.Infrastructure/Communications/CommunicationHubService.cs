using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Communications;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Communications;

public sealed class CommunicationHubService(
    CSweetDbContext db,
    IAuditEventWriter audit,
    IChatTurnService turns,
    IExecutiveDecisionService? decisions = null,
    IResourceChangeService? resourceChanges = null,
    IHiringService? hiring = null,
    IAgentCommunicationOnboardingService? onboarding = null) : ICommunicationHubService
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
        Guid? perspectiveOrganizationUserId = null,
        CancellationToken cancellationToken = default)
    {
        var actor = await ActiveUserAsync(organizationId, actorOrganizationUserId, cancellationToken);
        if (actor is null) return null;

        if (onboarding is not null &&
            actor.EmployeeType == EmployeeType.Human &&
            (!perspectiveOrganizationUserId.HasValue || perspectiveOrganizationUserId == actor.Id))
            await ReconcileAgentDirectChatsAsync(organizationId, actor, onboarding, cancellationToken);

        var people = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IsActive)
            .Include(x => x.Role)
            .Include(x => x.AgentInstallation)
                .ThenInclude(x => x!.Schedule)
            .OrderBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);
        var viewedUser = ResolveViewedUser(actor, people, perspectiveOrganizationUserId);
        if (viewedUser is null) return null;
        var isReadOnlyPerspective = viewedUser.Id != actor.Id;
        var installationIds = people
            .Where(x => x.EmployeeType == EmployeeType.Agent && x.AgentInstallationId.HasValue)
            .Select(x => x.AgentInstallationId!.Value)
            .Distinct()
            .ToList();
        var runtimeInstances = installationIds.Count == 0
            ? []
            : await db.AgentRuntimeInstances.AsNoTracking()
                .Where(x => installationIds.Contains(x.AgentInstallationId))
                .OrderByDescending(x => x.QueuedAt)
                .ToListAsync(cancellationToken);
        var latestRuntimes = runtimeInstances
            .GroupBy(x => x.AgentInstallationId)
            .ToDictionary(x => x.Key, x => x.First());
        var presences = people.ToDictionary(
            x => x.Id,
            x => ResolvePresence(
                x,
                x.AgentInstallationId.HasValue &&
                latestRuntimes.TryGetValue(x.AgentInstallationId.Value, out var runtime)
                    ? runtime
                    : null));

        var chats = await db.CoreConversations.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ArchivedAt == null &&
                x.Participants.Any(p => p.OrganizationUserId == viewedUser.Id && p.LeftAt == null))
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
            viewedUser.Id,
            isReadOnlyPerspective,
            !isReadOnlyPerspective && actor.PermissionLevel >= OrganizationPermissionLevel.Manager,
            chats.Select(x => MapChat(x, viewedUser, presences, !isReadOnlyPerspective)).ToList(),
            people.Select(x => new CommunicationPersonResponse(
                x.Id, x.DisplayName, x.EmployeeType.ToString(), x.RoleId, x.Role?.Name,
                presences[x.Id].Status, presences[x.Id].Detail)).ToList(),
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
        Guid? perspectiveOrganizationUserId = null,
        CancellationToken cancellationToken = default)
    {
        var actor = await ActiveUserAsync(organizationId, actorOrganizationUserId, cancellationToken);
        if (actor is null) return null;
        var viewedUser = await ResolveViewedUserAsync(
            organizationId, actor, perspectiveOrganizationUserId, cancellationToken);
        if (viewedUser is null ||
            !await IsActiveMemberAsync(organizationId, chatId, viewedUser.Id, cancellationToken))
            return null;

        var users = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var messages = await db.CoreConversationMessages.AsNoTracking()
            .Where(x => x.ConversationId == chatId)
            .Include(x => x.Mentions)
                .ThenInclude(x => x.MentionedOrganizationUser)
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
        var hiringCards = hiring is null
            ? new Dictionary<Guid, Contracts.Core.HiringWorkflowApprovalResponse>()
            : (await hiring.ListApprovalCardsAsync(organizationId, chatId, cancellationToken))
                .ToDictionary(x => x.Key, x => x.Value);
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
                : null,
            x.CorrelationId != Guid.Empty && hiringCards.TryGetValue(x.CorrelationId, out var hiringWorkflow)
                ? hiringWorkflow
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
        var content = request.Content.TrimEnd();
        var mentions = await ValidateMentionsAsync(
            organizationId, chat, actor.Id, content, request.Mentions, cancellationToken);

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
                .Include(x => x.Mentions).ThenInclude(x => x.MentionedOrganizationUser)
                .SingleOrDefaultAsync(x => x.ConversationId == chat.Id &&
                    x.SenderOrganizationUserId == actor.Id && x.IdempotencyKey == idempotencyKey, cancellationToken);
            if (existing is not null)
                return new CommunicationMessageSendResponse(
                    MapMessage(existing, new Dictionary<Guid, OrganizationUser> { [actor.Id] = actor }),
                    existing.ChatTurnId.HasValue
                        ? await turns.GetAsync(organizationId, existing.ChatTurnId.Value, cancellationToken)
                        : null);
        }

        var directRecipientIds = chat.Kind == ConversationKind.DirectHumanAgent
            ? chat.Participants
                .Where(x => x.LeftAt == null && x.OrganizationUserId != actor.Id)
                .Select(x => x.OrganizationUserId)
                .ToList()
            : [];
        var targetAgentId = directRecipientIds.Count == 1
            ? await db.CoreOrganizationUsers
                .Where(x => x.OrganizationId == organizationId &&
                    x.Id == directRecipientIds[0] &&
                    x.EmployeeType == EmployeeType.Agent &&
                    x.IsActive)
                .Select(x => (Guid?)x.Id)
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        if (targetAgentId.HasValue)
        {
            await using var mentionTransaction = mentions.Count > 0 &&
                db.Database.IsRelational() && db.Database.CurrentTransaction is null
                ? await db.Database.BeginTransactionAsync(cancellationToken)
                : null;
            var started = await turns.StartForAgentAsync(
                organizationId,
                chat.Id,
                targetAgentId.Value,
                content,
                actor.Id,
                CommunicationProviderKeys.InApp,
                idempotencyKey: idempotencyKey,
                cancellationToken: cancellationToken);
            if (started is null) return null;
            await PersistMentionsAsync(
                organizationId, chat, actor, started.UserMessage.Id, content, mentions, cancellationToken);
            if (mentionTransaction is not null)
                await mentionTransaction.CommitAsync(cancellationToken);
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
                    started.Turn.Id)
                {
                    Mentions = ToMentionResponses(mentions)
                },
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
            Content = content, CorrelationId = Guid.NewGuid(), DeliveryIntent = CommunicationDeliveryIntent.Inform,
            SourceProvider = "InApp", IdempotencyKey = idempotencyKey, CreatedAt = now
        };
        chat.UpdatedAt = now;
        db.CoreConversationMessages.Add(message);
        AddMentionEntities(organizationId, chat.Id, message.Id, mentions, now);
        QueueMentionDeliveries(organizationId, chat, actor, message.Id, content, mentions, now);
        await db.SaveChangesAsync(cancellationToken);
        var sent = new CommunicationMessageSendResponse(
            new CommunicationHubMessageResponse(message.Id, message.Sequence, chat.Id, actor.Id, actor.DisplayName,
                actor.EmployeeType.ToString(), message.Content, message.CreatedAt, message.ChatTurnId)
            {
                Mentions = ToMentionResponses(mentions)
            });
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

    private async Task<OrganizationUser?> ResolveViewedUserAsync(
        Guid organizationId,
        OrganizationUser actor,
        Guid? perspectiveOrganizationUserId,
        CancellationToken token)
    {
        if (!perspectiveOrganizationUserId.HasValue ||
            perspectiveOrganizationUserId.Value == actor.Id)
            return actor;
        if (actor.EmployeeType != EmployeeType.Human) return null;
        return await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == perspectiveOrganizationUserId.Value &&
            x.OrganizationId == organizationId &&
            x.IsActive &&
            x.EmployeeType == EmployeeType.Agent, token);
    }

    private static OrganizationUser? ResolveViewedUser(
        OrganizationUser actor,
        IReadOnlyList<OrganizationUser> people,
        Guid? perspectiveOrganizationUserId)
    {
        if (!perspectiveOrganizationUserId.HasValue ||
            perspectiveOrganizationUserId.Value == actor.Id)
            return actor;
        if (actor.EmployeeType != EmployeeType.Human) return null;
        return people.SingleOrDefault(x =>
            x.Id == perspectiveOrganizationUserId.Value &&
            x.EmployeeType == EmployeeType.Agent);
    }

    private Task<bool> IsActiveMemberAsync(Guid organizationId, Guid chatId, Guid userId, CancellationToken token) =>
        db.CoreConversations.AnyAsync(x => x.Id == chatId && x.OrganizationId == organizationId && x.ArchivedAt == null &&
            x.Participants.Any(p => p.OrganizationUserId == userId && p.LeftAt == null), token);

    private async Task ReconcileAgentDirectChatsAsync(
        Guid organizationId,
        OrganizationUser actor,
        IAgentCommunicationOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        if (!actor.ApplicationUserId.HasValue && actor.PermissionLevel != OrganizationPermissionLevel.Owner)
            return;

        var missingAgents = await db.CoreOrganizationUsers
            .Where(agent =>
                agent.OrganizationId == organizationId &&
                agent.IsActive &&
                agent.EmployeeType == EmployeeType.Agent &&
                agent.AgentInstallationId.HasValue &&
                (agent.ReportsToOrganizationUserId == actor.Id ||
                 actor.PermissionLevel == OrganizationPermissionLevel.Owner) &&
                !db.CoreConversations.Any(chat =>
                    chat.OrganizationId == organizationId &&
                    chat.Kind == ConversationKind.DirectHumanAgent &&
                    chat.InitiatedByOrganizationUserId == actor.Id &&
                    chat.AgentOrganizationUserId == agent.Id))
            .Include(agent => agent.AgentInstallation)
            .ToListAsync(cancellationToken);

        foreach (var agent in missingAgents)
        {
            var lifecycleReady = agent.AgentInstallation?.SetupState == PluginSetupState.Ready;
            var result = await onboardingService.EnsureAsync(
                organizationId,
                agent,
                actor.ApplicationUserId,
                queueLifecycleEvent: lifecycleReady,
                cancellationToken: cancellationToken);
            if (!result.Succeeded)
                throw new InvalidOperationException(result.Message);
        }

        if (missingAgents.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
    }

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

    private static CommunicationChatResponse MapChat(
        Conversation chat,
        OrganizationUser actor,
        IReadOnlyDictionary<Guid, CommunicationPresence>? presences = null,
        bool allowManagement = true)
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
            chat.IsDeletionProtected,
            allowManagement && !direct && !chat.IsDeletionProtected && CanManage(chat, actor),
            chat.UpdatedAt,
            active.Select(x =>
            {
                var presence = presences is not null &&
                    presences.TryGetValue(x.OrganizationUserId, out var resolved)
                        ? resolved
                        : CommunicationPresence.Available;
                return new CommunicationParticipantResponse(
                    x.OrganizationUserId,
                    x.OrganizationUser?.DisplayName ?? "Unknown",
                    x.OrganizationUser?.EmployeeType.ToString() ?? "Unknown",
                    x.Role.ToString(),
                    presence.Status,
                    presence.Detail);
            }).ToList(),
            last?.Content, last?.CreatedAt, unreadCount);
    }

    private static CommunicationPresence ResolvePresence(
        OrganizationUser person,
        AgentRuntimeInstance? latestRuntime)
    {
        if (person.EmployeeType != EmployeeType.Agent)
            return CommunicationPresence.Available;

        var installation = person.AgentInstallation;
        if (installation is null ||
            !installation.IsEnabled ||
            installation.RevisionStatus != PluginRevisionStatus.Active)
            return CommunicationPresence.Unhealthy(
                "The agent installation is disabled or unavailable.");

        if (installation.SetupState == PluginSetupState.SetupFailed)
            return CommunicationPresence.Unhealthy(
                "The agent setup failed and requires attention.");

        if (installation.SetupState != PluginSetupState.Ready)
            return new(
                CommunicationPresenceStatuses.Starting,
                $"Agent setup is {installation.SetupState}.");

        var schedule = installation.Schedule;
        if (schedule?.AutomaticStartSuppressedAt is not null)
        {
            var latestFailure = latestRuntime?.Reason;
            return CommunicationPresence.Unhealthy(
                $"Automatic startup is suppressed after {schedule.ConsecutiveStartupFailures} consecutive failure(s)." +
                (string.IsNullOrWhiteSpace(latestFailure) ? string.Empty : $" Last failure: {latestFailure}"));
        }

        // Presence answers whether the agent can communicate now. A running runtime is
        // available even while a background configuration refresh is converging.
        if (latestRuntime?.Status == AgentRuntimeStatus.Running)
            return CommunicationPresence.Available;

        if (installation.ConfigurationSyncStatus == AgentConfigurationSyncStatus.Failed)
            return CommunicationPresence.Unhealthy(
                installation.ConfigurationSyncLastError ?? "The agent configuration could not be applied.");

        if (installation.ConfigurationSyncStatus is
            AgentConfigurationSyncStatus.Refreshing or AgentConfigurationSyncStatus.Restarting)
            return new(
                CommunicationPresenceStatuses.Starting,
                $"Agent configuration is {installation.ConfigurationSyncStatus}.");

        if (latestRuntime is null &&
            (installation.ConfigurationSyncStatus == AgentConfigurationSyncStatus.PendingNextStart ||
             schedule?.ActivationMode == ActivationMode.AlwaysOn))
            return new(
                CommunicationPresenceStatuses.Starting,
                "The agent is waiting for its first runtime activation.");

        if (latestRuntime is null)
            return CommunicationPresence.Offline("The agent runtime is not running.");

        return latestRuntime.Status switch
        {
            AgentRuntimeStatus.Running => CommunicationPresence.Available,
            AgentRuntimeStatus.Queued or
            AgentRuntimeStatus.Starting or
            AgentRuntimeStatus.WaitingForMcpSession or
            AgentRuntimeStatus.CompletionReported or
            AgentRuntimeStatus.Stopping => new(
                CommunicationPresenceStatuses.Starting,
                $"Agent runtime is {latestRuntime.Status}."),
            AgentRuntimeStatus.StartFailed or
            AgentRuntimeStatus.McpSessionTimedOut or
            AgentRuntimeStatus.RuntimeTimedOut or
            AgentRuntimeStatus.ExitedWithoutCompletion or
            AgentRuntimeStatus.Failed or
            AgentRuntimeStatus.PolicyDenied => CommunicationPresence.Unhealthy(
                latestRuntime.Reason ?? $"The agent runtime ended in {latestRuntime.Status}."),
            _ => CommunicationPresence.Offline(
                latestRuntime.Reason ?? $"Agent runtime is {latestRuntime.Status}.")
        };
    }

    private sealed record CommunicationPresence(string Status, string? Detail)
    {
        public static CommunicationPresence Available { get; } =
            new(CommunicationPresenceStatuses.Available, null);

        public static CommunicationPresence Unhealthy(string detail) =>
            new(CommunicationPresenceStatuses.Unhealthy, detail);

        public static CommunicationPresence Offline(string detail) =>
            new(CommunicationPresenceStatuses.Offline, detail);
    }

    private static CommunicationHubMessageResponse MapMessage(
        ConversationMessage message,
        IReadOnlyDictionary<Guid, OrganizationUser> users,
        IReadOnlyDictionary<Guid, ExecutiveDecisionCardResponse>? decisions = null,
        IReadOnlyList<SuggestedUserActionResponse>? actions = null,
        Contracts.Core.ResourceChangeRequestResponse? resourceChange = null,
        Contracts.Core.HiringWorkflowApprovalResponse? hiringWorkflow = null)
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
            resourceChange,
            hiringWorkflow)
        {
            CoordinationSessionId = message.CoordinationSessionId,
            Mentions = message.Mentions
                .OrderBy(x => x.Offset)
                .Select(x => new CommunicationMessageMentionResponse(
                    x.MentionedOrganizationUserId,
                    x.MentionedOrganizationUser?.DisplayName ?? x.DisplayText.TrimStart('@'),
                    x.MentionedOrganizationUser?.EmployeeType.ToString() ?? "Unknown",
                    x.Offset, x.Length, x.DisplayText))
                .ToList(),
            MessageType = hiringWorkflow is not null
                ? CommunicationMessageTypes.HiringWorkflowApproval
                : resourceChange is not null
                ? CommunicationMessageTypes.ResourceChangeApproval
                : isSystemAction
                ? CommunicationMessageTypes.SystemAction
                : CommunicationMessageTypes.Standard
        };
    }

    private async Task<IReadOnlyList<ValidatedMention>> ValidateMentionsAsync(
        Guid organizationId,
        Conversation chat,
        Guid senderId,
        string content,
        IReadOnlyList<CommunicationMessageMentionInput>? requested,
        CancellationToken cancellationToken)
    {
        if (requested is not { Count: > 0 }) return [];
        if (requested.Count > 50) throw new ArgumentException("A message cannot contain more than 50 mentions.");
        var ordered = requested.OrderBy(x => x.Offset).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var item = ordered[index];
            if (item.Offset < 0 || item.Length < 2 || item.Offset + item.Length > content.Length)
                throw new ArgumentException("A mention range is outside the message content.");
            if (index > 0 && ordered[index - 1].Offset + ordered[index - 1].Length > item.Offset)
                throw new ArgumentException("Mention ranges cannot overlap.");
        }

        var ids = ordered.Select(x => x.OrganizationUserId).Distinct().ToList();
        var users = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IsActive && ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        if (users.Count != ids.Count)
            throw new ArgumentException("Every mentioned identity must be active in this organization.");
        var participantIds = chat.Participants.Where(x => x.LeftAt == null)
            .Select(x => x.OrganizationUserId).ToHashSet();
        var result = new List<ValidatedMention>(ordered.Count);
        foreach (var item in ordered)
        {
            var user = users[item.OrganizationUserId];
            var displayText = content.Substring(item.Offset, item.Length);
            if (!string.Equals(displayText, $"@{user.DisplayName}", StringComparison.Ordinal))
                throw new ArgumentException("Mention text must match the selected person's current display name.");
            result.Add(new ValidatedMention(
                item.OrganizationUserId, user.DisplayName, user.EmployeeType,
                user.AgentInstallationId, item.Offset, item.Length, displayText,
                item.OrganizationUserId != senderId && participantIds.Contains(item.OrganizationUserId)));
        }
        return result;
    }

    private async Task PersistMentionsAsync(
        Guid organizationId,
        Conversation chat,
        OrganizationUser sender,
        Guid messageId,
        string content,
        IReadOnlyList<ValidatedMention> mentions,
        CancellationToken cancellationToken)
    {
        if (mentions.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        AddMentionEntities(organizationId, chat.Id, messageId, mentions, now);
        QueueMentionDeliveries(organizationId, chat, sender, messageId, content, mentions, now);
        await db.SaveChangesAsync(cancellationToken);
    }

    private void AddMentionEntities(
        Guid organizationId,
        Guid chatId,
        Guid messageId,
        IReadOnlyList<ValidatedMention> mentions,
        DateTimeOffset now)
    {
        foreach (var mention in mentions)
        {
            db.ConversationMessageMentions.Add(new ConversationMessageMention
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, ConversationId = chatId,
                MessageId = messageId, MentionedOrganizationUserId = mention.OrganizationUserId,
                Offset = mention.Offset, Length = mention.Length, DisplayText = mention.DisplayText,
                RecipientWasParticipant = mention.RecipientWasParticipant, CreatedAt = now
            });
        }
    }

    private void QueueMentionDeliveries(
        Guid organizationId,
        Conversation chat,
        OrganizationUser sender,
        Guid messageId,
        string content,
        IReadOnlyList<ValidatedMention> mentions,
        DateTimeOffset now)
    {
        foreach (var mention in mentions.Where(x => x.RecipientWasParticipant)
                     .GroupBy(x => x.OrganizationUserId).Select(x => x.First()))
        {
            var mentionId = Guid.NewGuid();
            var payload = new CommunicationMessageMentionedEvent(
                mentionId, messageId, chat.Id, mention.OrganizationUserId, sender.Id,
                sender.DisplayName, content, mention.Offset, mention.Length, now);
            if (mention.EmployeeType == EmployeeType.Agent && mention.AgentInstallationId.HasValue)
            {
                db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
                {
                    Id = mentionId, OrganizationId = organizationId,
                    TargetInstallationId = mention.AgentInstallationId,
                    EventType = CommunicationEvents.MessageMentioned,
                    DataJson = JsonSerializer.Serialize(payload),
                    IdempotencyKey = $"message-mention:{messageId:N}:{mention.OrganizationUserId:N}",
                    Status = AgentPlatformEventOutboxStatus.Pending,
                    NextAttemptAt = now, OccurredAt = now
                });
            }
            else if (mention.EmployeeType == EmployeeType.Human)
            {
                db.UserNotifications.Add(new UserNotification
                {
                    Id = mentionId, OrganizationId = organizationId,
                    RecipientOrganizationUserId = mention.OrganizationUserId,
                    OriginatingAgentOrganizationUserId = sender.EmployeeType == EmployeeType.Agent ? sender.Id : null,
                    Severity = NotificationSeverity.Routine, Category = "Mention",
                    Title = $"{sender.DisplayName} mentioned you",
                    Body = content.Length <= 500 ? content : content[..500],
                    ActionUri = $"/organizations/{organizationId:D}/communications/{chat.Id:D}?message={messageId:D}",
                    DeduplicationKey = $"message-mention:{messageId:N}:{mention.OrganizationUserId:N}",
                    CreatedAt = now
                });
            }
        }
    }

    private static IReadOnlyList<CommunicationMessageMentionResponse> ToMentionResponses(
        IReadOnlyList<ValidatedMention> mentions) =>
        mentions.Select(x => new CommunicationMessageMentionResponse(
            x.OrganizationUserId, x.DisplayName, x.EmployeeType.ToString(),
            x.Offset, x.Length, x.DisplayText)).ToList();

    private sealed record ValidatedMention(
        Guid OrganizationUserId,
        string DisplayName,
        EmployeeType EmployeeType,
        Guid? AgentInstallationId,
        int Offset,
        int Length,
        string DisplayText,
        bool RecipientWasParticipant);

    private static SuggestedUserActionResponse ToAction(SuggestedUserAction action) =>
        new(action.Id, action.WorkflowType, action.Label, action.Description, action.NavigationUri,
            action.Status, action.CreatedAt);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static CommunicationHubActionResponse Success(string message, CommunicationChatResponse? chat = null) => new(true, null, message, chat);
    private static CommunicationHubActionResponse Failure(string code, string message) => new(false, code, message);
}
