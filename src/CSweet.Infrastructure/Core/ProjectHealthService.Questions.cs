using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthService
{
    private async Task CloseIncidentQuestionsAsync(ProjectIncident incident, CancellationToken token)
    {
        var questions = await (from decision in db.ExecutiveDecisions
            join message in db.CoreConversationMessages on decision.ConversationMessageId equals message.Id
            where decision.OrganizationId == incident.OrganizationId && decision.Status == ExecutiveDecisionStatus.Pending &&
                message.CorrelationId == incident.Id
            select decision).ToListAsync(token);
        foreach (var question in questions)
            if (ExecutiveDecisionServiceIncidentBinding(question)?.IncidentId == incident.Id)
            {
                question.Status = ExecutiveDecisionStatus.Cancelled;
                question.UpdatedAt = clock.GetUtcNow();
            }
        // SaveChanges captures DecisionUpdated alongside incident changes. Answered decisions
        // and unrelated questions retain their original outcome and conversation history.
    }

    private async Task ReconcileClosedIncidentQuestionsAsync(CancellationToken token)
    {
        // Reconnect/rollout repair is bounded by pending questions, rather than re-scanning
        // the same old closed incidents forever. Message correlation is only a discovery hint.
        var rows = await (from decision in db.ExecutiveDecisions
            join message in db.CoreConversationMessages on decision.ConversationMessageId equals message.Id
            join incident in db.ProjectIncidents on message.CorrelationId equals incident.Id
            where decision.Status == ExecutiveDecisionStatus.Pending && incident.Status != "Open" &&
                decision.OrganizationId == incident.OrganizationId && decision.IdempotencyKey.StartsWith("incident-choice:")
            orderby decision.CreatedAt, decision.Id
            select new { Decision = decision, Incident = incident }).Take(50).ToListAsync(token);
        foreach (var row in rows)
            if (ExecutiveDecisionServiceIncidentBinding(row.Decision)?.IncidentId == row.Incident.Id)
            {
                row.Decision.Status = ExecutiveDecisionStatus.Cancelled;
                row.Decision.UpdatedAt = clock.GetUtcNow();
                QueueAttentionChange(row.Incident, row.Incident.CurrentRecipientId);
            }
        await db.SaveChangesAsync(token);
    }
}
