using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkOrchestrator
{
    private async Task<string?> RecordArtifactQualityAsync(WorkSprintExecution sprint, WorkStageExecution quality,
        WorkExecutionOutcomeV1 outcome, CancellationToken ct)
    {
        var execution = quality.ItemExecution!; var item = execution.WorkItem!;
        var worker = execution.Stages.LastOrDefault(x => x.Traversal == execution.Traversal && x.StageKey is "specialist-execution" or "development" &&
            x.Status == WorkStageExecutionStatus.Completed);
        if (worker is null || worker.OrganizationUserId == quality.OrganizationUserId ||
            worker.AgentInstallationId.HasValue && worker.AgentInstallationId == quality.AgentInstallationId)
            return "Every artifact requires independent QA by a different eligible author identity.";
        var delivered = worker.Attempts.LastOrDefault(x => x.Status == WorkExecutionAttemptStatus.Completed)?.ResultJson;
        if (delivered is null || !WorkDeliveryService.TryDocument(WorkDeliveryService.Decode<WorkExecutionOutcomeV1>(delivered).Output,
            out var artifactId, out var revisionId, out var digest)) return "The author must deliver an exact artifact revision and digest.";
        var result = outcome.Output.Deserialize<WorkArtifactQualityResult>(WorkDeliveryService.Json);
        if (result is null || result.ArtifactId != artifactId || result.RevisionId != revisionId || result.Sha256 != digest ||
            result.Passed != (outcome.OutcomeCode == "passed")) return "Artifact QA must bind the exact delivered revision and consistent verdict.";
        var planning = WorkDeliveryService.Decode<WorkItemPlanningSpecification>(item.PlanningSpecificationJson!);
        try { WorkDeliveryService.ValidateCriteria(planning.AcceptanceCriteria, result.Passed, result.Summary, result.Criteria, result.Findings); }
        catch (InvalidOperationException error) { return error.Message; }
        var revision = await db.ArtifactRevisions.AsNoTracking().Include(x => x.Artifact).SingleOrDefaultAsync(x =>
            x.Id == revisionId && x.ArtifactId == artifactId && x.OrganizationId == sprint.OrganizationId, ct);
        if (revision?.Artifact is not { } artifact || artifact.ArchivedAt.HasValue || artifact.OriginWorkItemId != item.Id ||
            revision.CreatedByOrganizationUserId != worker.OrganizationUserId || revision.CreatedByAgentInstallationId != worker.AgentInstallationId ||
            revision.ContentSha256 != digest || Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(revision.Content))) != digest)
            return "The artifact's identity, origin, author or content differs from the delivered revision.";
        return null;
    }
}
