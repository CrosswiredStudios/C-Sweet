using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryService
{
    // Business messages remain valid prompt inputs even when they are not eligible for
    // personal relationship memory. Never manufacture a source-erasure marker for them.
    // Previously captured, changed, or excluded sources still follow strict validation.
    private Task<bool> IsNonPersonalCaptureAsync(Guid messageId, CancellationToken token) =>
        db.CoreConversationMessages.AsNoTracking().AnyAsync(message => message.Id == messageId &&
            message.Conversation != null && message.Conversation.ArchivedAt == null &&
            message.Conversation.MergedIntoConversationId == null &&
            (message.Conversation.Kind != ConversationKind.DirectHumanAgent ||
                db.CoreOrganizationUsers.Any(sender => sender.Id == message.Conversation.InitiatedByOrganizationUserId &&
                    sender.OrganizationId == message.Conversation.OrganizationId && sender.EmployeeType == EmployeeType.Agent &&
                    sender.IsActive && sender.ArchivedAt == null)) &&
            !db.MemorySourceInvalidations.Any(x => x.SourceMessageId == messageId) &&
            !db.MemoryCaptureExclusions.Any(x => x.SourceMessageId == messageId) &&
            !db.MemoryCaptureOutbox.Any(x => x.ConversationMessageId == messageId &&
                (x.EpisodeCapturedAt != null || x.AcceptedExtractionJson != null)), token);
}
