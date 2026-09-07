using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Native conversation projection of the one authoritative action decision.</summary>
public sealed class ConnectorApprovalConversationService(CSweetDbContext db)
{
    public const string LinkKind = "host-connector-approval-conversation";
    public const string MessageSource = "ConnectorApproval";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Link(Guid ConversationId, Guid MessageId, Guid ApproverId);

    public async Task EnsureAsync(ActionProposal proposal, ConnectorActionApprovalService.Binding binding, CancellationToken ct)
    {
        if (proposal.Status != ProposalStatus.Pending) return;
        var key = proposal.Id.ToString("N");
        if (await db.PluginOperationalStates.AnyAsync(x => x.OrganizationId == proposal.OrganizationId &&
            x.AgentInstallationId == proposal.AgentInstallationId && x.Kind == LinkKind && x.ExternalKey == key, ct)) return;
        var approver = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.Id == binding.ApproverOrganizationUserId &&
            x.OrganizationId == proposal.OrganizationId && x.IsActive && x.EmployeeType == EmployeeType.Human, ct);
        if (approver is null) return; // Agent approvers receive exact-installation semantic events.
        var requester = await db.CoreOrganizationUsers.SingleAsync(x => x.AgentInstallationId == proposal.AgentInstallationId &&
            x.OrganizationId == proposal.OrganizationId && x.IsActive, ct);
        var conversation = await db.CoreConversations.Include(x => x.Participants).Where(x => x.OrganizationId == proposal.OrganizationId &&
            x.Kind == ConversationKind.DirectHumanAgent && x.IsPrivate && x.IsDeletionProtected &&
            x.Participants.Count(p => p.LeftAt == null) == 2 &&
            x.Participants.Any(p => p.LeftAt == null && p.OrganizationUserId == requester.Id) &&
            x.Participants.Any(p => p.LeftAt == null && p.OrganizationUserId == approver.Id)).OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        var now = DateTimeOffset.UtcNow;
        if (conversation is null)
        {
            var conversationId = StableId($"connector-review:{proposal.OrganizationId:D}:{requester.Id:D}:{approver.Id:D}");
            conversation = new Conversation { Id = conversationId, OrganizationId = proposal.OrganizationId,
                AgentOrganizationUserId = requester.Id, InitiatedByOrganizationUserId = approver.Id,
                Kind = ConversationKind.DirectHumanAgent, IsPrivate = true, IsDeletionProtected = true, CreatedAt = now, UpdatedAt = now,
                Participants = [new() { Id = Guid.NewGuid(), ConversationId = conversationId, OrganizationUserId = approver.Id,
                    Role = ConversationParticipantRole.Coordinator, JoinedAt = now },
                    new() { Id = Guid.NewGuid(), ConversationId = conversationId, OrganizationUserId = requester.Id,
                        Role = ConversationParticipantRole.Member, JoinedAt = now }] };
            db.CoreConversations.Add(conversation);
        }
        var messageId = StableId($"connector-review-message:{proposal.Id:D}");
        db.CoreConversationMessages.Add(new() { Id = messageId, ConversationId = conversation.Id,
            Role = ConversationRole.Assistant, SenderOrganizationUserId = requester.Id,
            Content = "Please review this proposed change. Nothing will be changed until it is approved.",
            CorrelationId = proposal.Id, SourceProvider = MessageSource, IdempotencyKey = $"connector-review:{proposal.Id:N}",
            DeliveryIntent = CommunicationDeliveryIntent.Inform, CreatedAt = now });
        conversation.ArchivedAt = null; conversation.UpdatedAt = now;
        db.PluginOperationalStates.Add(new() { Id = Guid.NewGuid(), OrganizationId = proposal.OrganizationId,
            AgentInstallationId = proposal.AgentInstallationId, Kind = LinkKind, ExternalKey = key,
            PayloadJson = JsonSerializer.Serialize(new Link(conversation.Id, messageId, approver.Id), Json),
            Revision = 1, CreatedAt = now, UpdatedAt = now });
        db.UserNotifications.Add(new() { Id = Guid.NewGuid(), OrganizationId = proposal.OrganizationId,
            RecipientOrganizationUserId = approver.Id, OriginatingAgentOrganizationUserId = requester.Id,
            Severity = NotificationSeverity.Important, Category = "ConnectorActionApproval", Title = "A change needs your review",
            Body = $"{requester.DisplayName} has a proposed action ready for your decision.",
            ActionUri = $"/organizations/{proposal.OrganizationId:D}/communications/{conversation.Id:D}",
            DeduplicationKey = $"connector-review:{proposal.Id:N}", CreatedAt = now });
        await db.SaveChangesAsync(ct); // Message, protected participants, link and notification share a commit.
    }

    public async Task<IReadOnlyDictionary<Guid, ConnectorActionApprovalCardResponse>> ReadCardsAsync(Guid organizationId,
        Guid conversationId, Guid actorId, IReadOnlyList<ConversationMessage> messages, CancellationToken ct)
    {
        var result = new Dictionary<Guid, ConnectorActionApprovalCardResponse>();
        var candidates = messages.Where(x => x.ConversationId == conversationId && x.SourceProvider == MessageSource)
            .ToDictionary(x => x.Id);
        if (candidates.Count == 0) return result;
        var conversation = await db.CoreConversations.AsNoTracking().Include(x => x.Participants).SingleOrDefaultAsync(x =>
            x.Id == conversationId && x.OrganizationId == organizationId && x.IsPrivate && x.IsDeletionProtected &&
            x.Kind == ConversationKind.DirectHumanAgent, ct);
        if (conversation is null || conversation.Participants.Count(x => x.LeftAt == null) != 2) return result;
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actorId &&
            x.OrganizationId == organizationId && x.IsActive, ct);
        if (actor is null || actor.PermissionLevel != OrganizationPermissionLevel.Owner &&
            !conversation.Participants.Any(x => x.OrganizationUserId == actor.Id && x.LeftAt == null)) return result;
        var ids = candidates.Values.Select(x => x.CorrelationId).Distinct().ToArray();
        var keys = ids.Select(x => x.ToString("N")).ToArray();
        var links = await db.PluginOperationalStates.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            x.Kind == LinkKind && keys.Contains(x.ExternalKey)).ToArrayAsync(ct);
        foreach (var row in links)
        {
            var link = JsonSerializer.Deserialize<Link>(row.PayloadJson, Json)!;
            if (link.ConversationId != conversationId || !candidates.TryGetValue(link.MessageId, out var message) ||
                !conversation.Participants.Any(x => x.OrganizationUserId == link.ApproverId && x.LeftAt == null)) continue;
            var proposal = await db.ActionProposals.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
                x.Id == message.CorrelationId && x.AgentInstallationId == row.AgentInstallationId &&
                x.ActionType == ConnectorActionApprovalService.ActionType, ct);
            if (proposal is null || row.ExternalKey != proposal.Id.ToString("N")) continue;
            var binding = ConnectorActionApprovalService.Parse(proposal);
            var execution = await db.ConnectorExecutions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == binding.PlanId &&
                x.OrganizationId == organizationId && x.RequesterInstallationId == proposal.AgentInstallationId && x.ApprovalId == proposal.Id, ct);
            var review = ApprovalDashboardService.ReadManagedAction(proposal);
            if (execution is null || review is null || binding.ApproverOrganizationUserId != link.ApproverId) continue;
            var receipt = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
                x.AgentInstallationId == proposal.AgentInstallationId && x.Kind == ConnectorActionApprovalService.ReceiptKind &&
                x.ExternalKey == proposal.Id.ToString("N"), ct);
            var comment = receipt is null ? null : JsonSerializer.Deserialize<ConnectorActionApprovalService.DecisionReceipt>(receipt.PayloadJson, Json)?.Comment;
            result[message.Id] = new(review, proposal.Summary, proposal.Status.ToString(), execution.Status,
                actorId == link.ApproverId && proposal.Status == ProposalStatus.Pending && execution.Status == "AwaitingApproval" &&
                binding.ExpiresAt > DateTimeOffset.UtcNow, comment);
        }
        return result;
    }

    private static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}
