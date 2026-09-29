using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

public sealed partial class ArtifactCapabilityHandler
{
    // A current manager-review assignment authorizes its exact delivered input, not document browsing.
    // Recheck persisted assignment/provenance on every read; cancellation or reassignment ends access.
    internal async Task<Guid?> AssignedReviewRevisionAsync(
        Guid organizationId, Guid artifactId, ArtifactAgentActor actor, CancellationToken token)
    {
        if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.Id == actor.OrganizationUserId &&
            x.OrganizationId == organizationId && x.AgentInstallationId == actor.InstallationId &&
            x.IsActive && x.EmployeeType == EmployeeType.Agent, token)) return null;
        var artifact = await db.CoreArtifacts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == artifactId && x.OrganizationId == organizationId && x.ArchivedAt == null, token);
        if (artifact?.OriginWorkItemId is not { } workItemId) return null;
        var item = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == workItemId && x.OrganizationId == organizationId && x.ArchivedAt == null &&
            x.Status == WorkTaskStatus.WaitingForApproval, token);
        if (item?.BoardId is not { } boardId) return null;
        var board = await db.WorkBoards.AsNoTracking().SingleOrDefaultAsync(x => x.Id == boardId &&
            x.OrganizationId == organizationId && x.ArchivedAt == null &&
            x.ManagerOrganizationUserId == actor.OrganizationUserId, token);
        if (board is null || artifact.WorkstreamId != board.WorkstreamId || artifact.TeamId != board.TeamId) return null;
        var execution = await db.WorkItemExecutions.AsNoTracking()
            .Include(x => x.SprintExecution).Include(x => x.Stages).ThenInclude(x => x.Attempts)
            .Where(x => x.WorkItemId == workItemId && x.SprintExecution!.OrganizationId == organizationId &&
                x.SprintExecution.BoardId == boardId)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(token);
        if (execution?.Status != WorkItemExecutionStatus.WaitingForApproval ||
            execution.SprintExecution!.Status != WorkSprintExecutionStatus.Active) return null;
        var review = execution.Stages.SingleOrDefault(x => x.StageKey == execution.CurrentStageKey &&
            x.Traversal == execution.Traversal && x.StageType == WorkOrchestrationStageType.ManagerApproval &&
            x.Status == WorkStageExecutionStatus.WaitingForApproval &&
            x.PrincipalKind == WorkOrchestrationPrincipalKind.BoardManager && x.OrganizationUserId == actor.OrganizationUserId);
        if (review is null) return null;
        var producers = execution.Stages.Where(x => x.Traversal == execution.Traversal &&
            x.Status == WorkStageExecutionStatus.Completed && x.OrganizationUserId == artifact.CreatedByOrganizationUserId &&
            x.OrganizationUserId.HasValue && x.AgentInstallationId.HasValue).Take(33).ToArray();
        if (producers.Length > 32) return null;
        foreach (var producer in producers)
        {
            var attempt = producer.Attempts.OrderByDescending(x => x.Attempt).FirstOrDefault();
            if (attempt?.Status != WorkExecutionAttemptStatus.Completed || string.IsNullOrEmpty(attempt.ResultJson)) continue;
            W.WorkExecutionOutcomeV1? outcome;
            DeliveryReviewReference? reference;
            try
            {
                outcome = JsonSerializer.Deserialize<W.WorkExecutionOutcomeV1>(attempt.ResultJson, JsonOptions);
                reference = outcome?.Output.ValueKind == JsonValueKind.Object
                    ? outcome.Output.Deserialize<DeliveryReviewReference>(JsonOptions) : null;
            }
            catch (JsonException) { continue; }
            if (reference?.ArtifactId != artifactId || outcome!.StageExecutionId != producer.Id || outcome.AttemptId != attempt.Id ||
                outcome.Disposition != W.WorkExecutionDispositions.Completed || outcome.OutcomeCode != producer.LastOutcomeCode) continue;
            if (!await db.WorkOrchestrationTransitions.AsNoTracking().AnyAsync(x =>
                x.PolicyRevisionId == execution.SprintExecution.PolicyRevisionId && x.FromStageKey == producer.StageKey &&
                x.ToStageKey == review.StageKey && x.OutcomeCode == outcome.OutcomeCode, token)) continue;
            var revision = await db.ArtifactRevisions.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == reference.RevisionId && x.ArtifactId == artifactId && x.OrganizationId == organizationId &&
                x.CreatedByAgentInstallationId == producer.AgentInstallationId &&
                x.CreatedByOrganizationUserId == producer.OrganizationUserId, token);
            if (revision is null || revision.Status is not (ArtifactRevisionStatus.Submitted or ArtifactRevisionStatus.Accepted) ||
                !string.Equals(revision.ContentSha256, reference.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(revision.Content))),
                    reference.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
            return revision.Id;
        }
        return null;
    }

    private sealed record DeliveryReviewReference(Guid ArtifactId, Guid RevisionId, string Sha256);
}
