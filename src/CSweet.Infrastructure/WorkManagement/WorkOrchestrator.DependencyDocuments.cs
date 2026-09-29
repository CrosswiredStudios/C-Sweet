using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkOrchestrator
{
    // A completed, manager-accepted work product is input to its explicitly declared direct dependents.
    // This copies only the delivered revision into the assignment; it grants no document browsing or editing.
    internal async Task<IReadOnlyList<Shared.WorkExecutionEvidence>> DependencyDocumentsAsync(
        Guid organizationId, WorkBoard board, WorkTask item, CancellationToken token)
    {
        var dependencyIds = item.Dependencies.Select(x => x.DependsOnWorkItemId).Distinct().Order().ToArray();
        if (dependencyIds.Length > 64) throw new InvalidOperationException("Dependency evidence exceeds the bounded assignment limit.");
        var evidence = new List<Shared.WorkExecutionEvidence>();
        var totalBytes = 0;
        foreach (var dependencyId in dependencyIds)
        {
            var dependency = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == dependencyId && x.OrganizationId == organizationId && x.BoardId == board.Id &&
                x.Status == WorkTaskStatus.Completed && x.ArchivedAt == null, token)
                ?? throw new InvalidOperationException("A dependency is not complete on the same authorized board.");
            var completed = await db.WorkItemExecutions.AsNoTracking()
                .Include(x => x.Stages).ThenInclude(x => x.Attempts)
                .Where(x => x.WorkItemId == dependencyId && x.SprintExecution!.OrganizationId == organizationId &&
                    x.SprintExecution.BoardId == board.Id)
                .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(token);
            if (completed is null) continue; // Legacy/manual work has no orchestration document output.
            if (completed.Status != WorkItemExecutionStatus.Completed)
                throw new InvalidOperationException("The dependency's current execution is not complete.");
            var worker = completed.Stages.SingleOrDefault(x => x.StageKey == "specialist-execution" &&
                x.Traversal == completed.Traversal && x.Status == WorkStageExecutionStatus.Completed);
            var attempt = worker?.Attempts.OrderByDescending(x => x.Attempt).FirstOrDefault();
            if (worker is null) continue;
            if (attempt?.Status != WorkExecutionAttemptStatus.Completed || string.IsNullOrWhiteSpace(attempt.ResultJson))
                throw new InvalidOperationException("The dependency producing stage has no current completed attempt.");
            var outcome = JsonSerializer.Deserialize<Shared.WorkExecutionOutcomeV1>(attempt.ResultJson, JsonOptions);
            if (outcome?.Output.ValueKind != JsonValueKind.Object || !outcome.Output.TryGetProperty("artifactId", out _)) continue;
            if (outcome.StageExecutionId != worker!.Id || outcome.AttemptId != attempt.Id ||
                outcome.Disposition != Shared.WorkExecutionDispositions.Completed || outcome.OutcomeCode != "completed" || worker.LastOutcomeCode != "completed")
                throw new InvalidOperationException("The dependency document does not match its completed producing attempt.");
            if (!completed.Stages.Any(x => x.Traversal == completed.Traversal && x.StageKey == "producer-review" &&
                x.Status == WorkStageExecutionStatus.Completed && x.LastOutcomeCode == "approved" &&
                x.OrganizationUserId == board.ManagerOrganizationUserId && x.OrganizationUserId.HasValue))
                throw new InvalidOperationException("The dependency document has not passed the assigned board manager's acceptance.");
            var reference = outcome.Output.Deserialize<DependencyDocumentReference>(JsonOptions)
                ?? throw new InvalidOperationException("The dependency document reference is missing.");
            var revision = await db.ArtifactRevisions.AsNoTracking().Include(x => x.Artifact).SingleOrDefaultAsync(x =>
                x.Id == reference.RevisionId && x.ArtifactId == reference.ArtifactId && x.OrganizationId == organizationId, token);
            var document = revision?.Artifact;
            if (revision is null || document is null || document.OrganizationId != organizationId || document.ArchivedAt.HasValue ||
                document.OriginWorkItemId != dependencyId || document.WorkstreamId != board.WorkstreamId || document.TeamId != board.TeamId ||
                document.CreatedByOrganizationUserId != worker.OrganizationUserId || !worker.OrganizationUserId.HasValue ||
                revision.CreatedByAgentInstallationId != worker.AgentInstallationId || !worker.AgentInstallationId.HasValue ||
                !string.Equals(revision.ContentSha256, reference.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revision.Content))), reference.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The dependency document does not match its scoped author, origin, revision and content hash.");
            totalBytes += Encoding.UTF8.GetByteCount(revision.Content);
            if (evidence.Count >= 16 || totalBytes > 262144)
                throw new InvalidOperationException("Dependency documents exceed the bounded assignment limit; split the work without dropping its requirements.");
            evidence.Add(new Shared.WorkExecutionEvidence("dependency-document.v1", dependency.Identifier ?? dependency.Title,
                JsonSerializer.Serialize(new { sourceWorkItemId = dependencyId, stageExecutionId = worker.Id,
                    attemptId = attempt.Id, artifactId = reference.ArtifactId, revisionId = reference.RevisionId,
                    sha256 = reference.Sha256, document.Title, revision.Content }, JsonOptions), "application/json"));
        }
        return evidence;
    }

    private sealed record DependencyDocumentReference(Guid ArtifactId, Guid RevisionId, string Sha256);
}
