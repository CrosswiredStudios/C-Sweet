using CSweet.Domain.Core;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkItemMutationEngine
{
    internal async Task ProjectChiefAssessmentAsync(OrganizationUser chief, ChiefCoverageRecord assessment, CancellationToken token,
        string? executionBlock = null)
    {
        var correlation = $"chief-assessment:{assessment.Id:N}";
        var card = assessment.WorkItemId is { } id ? await db.CoreWorkTasks.Include(x => x.Board).SingleOrDefaultAsync(x =>
            x.Id == id && x.OrganizationId == chief.OrganizationId && x.AssignedAgentInstallationId == chief.AgentInstallationId, token) : null;
        if (card is null)
        {
            // A replacement Chief gets a new projection. Historical records/cards stay intact.
            var existing = await db.CoreWorkTasks.Include(x => x.Board).SingleOrDefaultAsync(x =>
                x.OrganizationId == chief.OrganizationId && x.AssignedAgentInstallationId == chief.AgentInstallationId &&
                x.CorrelationId == $"leadership-coverage:{assessment.ProfileKey}:{assessment.DomainId}" && x.IsExecutable, token);
            if (existing is not null) card = existing;
            else
            {
                var created = await AddAsync(chief.OrganizationId, new(chief.Id, chief.AgentInstallationId),
                    new AddPersonalTodoItemRequest($"Assess {assessment.Label} leadership coverage", assessment.Description,
                        WorkPriorities.Medium, null, correlation, CorrelationId: correlation) { StartInBacklog = true }, token);
                card = await db.CoreWorkTasks.Include(x => x.Board).SingleAsync(x => x.Id == created.Id, token);
            }
            assessment.WorkItemId = card.Id;
        }
        var status = assessment.ExecutionStatus switch
        {
            "Ready" => WorkTaskStatus.Ready,
            "Assessing" or "WaitingHire" or "WaitingDecision" => WorkTaskStatus.Running,
            "Complete" => WorkTaskStatus.Completed,
            "Blocked" => WorkTaskStatus.Blocked,
            _ => WorkTaskStatus.Backlog
        };
        var blockReason = status == WorkTaskStatus.Blocked ? assessment.BlockCode + ": " + assessment.Rationale : null;
        if (status == WorkTaskStatus.Ready && executionBlock is not null)
        {
            status = WorkTaskStatus.Blocked;
            blockReason = executionBlock;
        }
        var reason = assessment.ExecutionStatus is "WaitingHire" or "WaitingDecision" ? assessment.Rationale : null;
        if (card.Status == status && card.WaitingReason == reason && card.ResultSummary == assessment.Rationale && card.BlockReason == blockReason) return;
        await db.Entry(card.Board!).Collection(x => x.Columns).LoadAsync(token);
        card.Status = status; card.BoardColumnId = ColumnForStatus(card.Board!, status).Id;
        card.ClaimEventId = null; card.ClaimExpiresAt = null;
        card.WaitingReason = reason; card.NextReviewAt = null;
        card.BlockReason = blockReason;
        card.ResultSummary = assessment.Rationale; card.Revision++; card.UpdatedAt = clock.GetUtcNow();
        await QueuePersonalRealtimeAsync(card, token);
    }
}
