using CSweet.Domain.Core;
using CSweet.Domain.Notifications;
using CSweet.Domain.Communications;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthService
{
    public async Task DeliverAsync(CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var pending = await db.ProjectIncidentDeliveries.Where(x => x.DeliveredAt == null && x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt).Take(50).ToListAsync(token);
        foreach (var delivery in pending)
        {
            var incident = await db.ProjectIncidents.SingleAsync(x => x.Id == delivery.IncidentId, token);
            var recipient = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.Id == delivery.RecipientId && x.OrganizationId == incident.OrganizationId &&
                x.IsActive && x.EmployeeType == EmployeeType.Human, token);
            if (recipient is null) { delivery.NextAttemptAt = now.AddMinutes(15); continue; }
            if (!await CanReadAsync(incident.OrganizationId, recipient.Id, incident.WorkstreamId, incident, token))
            { delivery.NextAttemptAt = now.AddMinutes(15); continue; }
            var sender = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.Id == delivery.SenderId && x.OrganizationId == incident.OrganizationId, token);
            sender ??= await db.CoreOrganizationUsers.SingleAsync(x => x.Id == incident.ProducerEmployeeId && x.OrganizationId == incident.OrganizationId, token);
            var key = Conversation.ParticipantKey(sender.Id, recipient.Id);
            var chat = await db.CoreConversations.Include(x => x.Participants).SingleOrDefaultAsync(x =>
                x.OrganizationId == incident.OrganizationId && x.DirectParticipantKey == key && x.MergedIntoConversationId == null, token);
            if (chat is null)
            {
                chat = new() { Id = Guid.NewGuid(), OrganizationId = incident.OrganizationId, AgentOrganizationUserId = sender.Id,
                    InitiatedByOrganizationUserId = recipient.Id, Kind = ConversationKind.DirectHumanAgent,
                    Title = "Project health reports", IsPrivate = true, IsDeletionProtected = true, CreatedAt = now, UpdatedAt = now };
                foreach (var person in new[] { sender.Id, recipient.Id }) chat.Participants.Add(new() {
                    Id = Guid.NewGuid(), OrganizationUserId = person, JoinedAt = now, Role = ConversationParticipantRole.Member });
                db.CoreConversations.Add(chat);
            }
            chat.ArchivedAt = null; chat.UpdatedAt = now;
            foreach (var member in chat.Participants.Where(x => x.OrganizationUserId == recipient.Id || x.OrganizationUserId == sender.Id)) member.LeftAt = null;
            var messageKey = $"project-health:{delivery.Id:N}";
            var presentation = await PresentAsync(incident, token);
            delivery.Markdown = presentation.Markdown;
            var existing = await db.CoreConversationMessages.Where(x => x.ConversationId == chat.Id &&
                x.CorrelationId == incident.Id && x.SenderOrganizationUserId == sender.Id &&
                x.IdempotencyKey != null && x.IdempotencyKey.StartsWith("project-health:"))
                .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(token);
            if (incident.Status is "Resolved" or "Superseded")
            {
                var keys = await db.ProjectIncidentDeliveries.Where(x => x.IncidentId == incident.Id && x.RecipientId == recipient.Id)
                    .Select(x => x.Id).ToArrayAsync(token);
                var notificationKeys = keys.Select(x => $"project-health:{x:N}").ToArray();
                foreach (var notification in await db.UserNotifications.Where(x => x.OrganizationId == incident.OrganizationId &&
                    x.RecipientOrganizationUserId == recipient.Id && x.Category == "ProjectHealth" && x.DismissedAt == null &&
                    notificationKeys.Contains(x.DeduplicationKey!)).ToListAsync(token))
                    notification.DismissedAt = now;
            }
            // Evidence-only revisions do not need another human message. A changed recovery
            // state gets a concise update; preserve prior messages and their memory/audit lineage.
            if ((existing is not null || incident.Status == "Open") && (existing is null || existing.Content != delivery.Markdown))
            {
                db.CoreConversationMessages.Add(new() { Id = Guid.NewGuid(), ConversationId = chat.Id, Role = ConversationRole.Assistant,
                    Content = delivery.Markdown, CreatedAt = now, SenderOrganizationUserId = sender.Id, CorrelationId = incident.Id,
                    DeliveryIntent = CommunicationDeliveryIntent.Inform, SourceProvider = "InApp", IdempotencyKey = messageKey });
                db.UserNotifications.Add(new() { Id = Guid.NewGuid(), OrganizationId = incident.OrganizationId,
                    RecipientOrganizationUserId = recipient.Id, OriginatingAgentOrganizationUserId = sender.Id,
                    Severity = incident.Status == "Resolved" ? NotificationSeverity.Routine : NotificationSeverity.Important,
                    Category = "ProjectHealth", Title = incident.Status == "Resolved" ? "Project incident resolved" : "Project needs management attention",
                    Body = presentation.Summary, ActionUri = $"/organizations/{incident.OrganizationId}/communications/{chat.Id}",
                    DeduplicationKey = messageKey, CreatedAt = now });
            }
            delivery.ConversationId = chat.Id; delivery.DeliveredAt = now;
            QueueAttentionChange(incident, recipient.Id);
            // Message, unread/realtime capture, notification and receipt commit together.
            await db.SaveChangesAsync(token);
        }
        await db.SaveChangesAsync(token);
    }
}
