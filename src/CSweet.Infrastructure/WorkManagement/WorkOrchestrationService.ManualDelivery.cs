using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkOrchestrationService
{
    private async Task ValidateManualDeliveryAsync(WorkStageExecution stage, CompleteManualWorkStageRequest request, CancellationToken ct)
    {
        var item = stage.ItemExecution!.WorkItem!;
        if (stage.Status != WorkStageExecutionStatus.WaitingForHuman ||
            stage.ItemExecution.SprintExecution!.Status != WorkSprintExecutionStatus.Active ||
            stage.ItemExecution.CurrentStageKey != stage.StageKey || stage.ItemExecution.Traversal != stage.Traversal)
            throw new InvalidOperationException("Only the current human stage in an active sprint may be completed.");
        await WorkDeliveryTaskAuthorization.RequireAsync(db, item, ct);
        var delivery = JsonSerializer.Deserialize<WorkItemDeliverySpecification>(item.DeliverySpecificationJson!, JsonOptions)!;
        if (delivery.DeliveryKind == "Artifact" && stage.StageKey is "development" or "specialist-execution")
        {
            if (request.OutcomeCode != "artifact-delivered" ||
                !WorkDeliveryService.TryDocument(request.Output, out var artifactId, out var revisionId, out var digest))
                throw new InvalidOperationException("Deliver an exact document revision and digest for independent QA.");
            var authored = await db.ArtifactRevisions.AsNoTracking().Include(x => x.Artifact).SingleOrDefaultAsync(x =>
                x.Id == revisionId && x.ArtifactId == artifactId && x.OrganizationId == item.OrganizationId, ct);
            if (authored?.Artifact is not { ArchivedAt: null } artifact || artifact.OriginWorkItemId != item.Id ||
                authored.CreatedByOrganizationUserId != stage.OrganizationUserId || authored.CreatedByAgentInstallationId is not null ||
                authored.ContentSha256 != digest || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(authored.Content))) != digest)
                throw new InvalidOperationException("The document revision does not match the current task, author or content digest.");
            return;
        }
        var authors = stage.ItemExecution.Stages.Where(x => x.StageKey is "development" or "specialist-execution").ToArray();
        if (stage.StageKey is "quality" or "technical-review" && authors.Any(x => x.OrganizationUserId == stage.OrganizationUserId))
            throw new InvalidOperationException("An author cannot review or QA their own deliverable.");
        if (stage.StageKey is not ("quality" or "technical-review")) return;
        if (delivery.DeliveryKind == "Artifact")
        {
            var delivered = authors.Where(x => x.Traversal == stage.Traversal).SelectMany(x => x.Attempts)
                .Where(x => x.Status == WorkExecutionAttemptStatus.Completed && x.ResultJson != null).OrderByDescending(x => x.CreatedAt).FirstOrDefault();
            if (stage.StageKey != "quality" || delivered is null ||
                !WorkDeliveryService.TryDocument(JsonSerializer.Deserialize<WorkExecutionOutcomeV1>(delivered.ResultJson!, JsonOptions)!.Output,
                    out var artifact, out var revision, out var sha)) throw new InvalidOperationException("Artifact QA needs the exact delivered revision.");
            var result = request.Output.Deserialize<WorkArtifactQualityResult>(JsonOptions) ?? throw new InvalidOperationException("Supply criterion-level artifact QA.");
            var exact = await db.ArtifactRevisions.AsNoTracking().Include(x => x.Artifact).SingleOrDefaultAsync(x =>
                x.Id == revision && x.ArtifactId == artifact && x.OrganizationId == item.OrganizationId, ct);
            var author = authors.Single(x => x.Id == delivered.StageExecutionId);
            if (result.ArtifactId != artifact || result.RevisionId != revision || result.Sha256 != sha || result.Passed != (request.OutcomeCode == "passed") ||
                exact?.Artifact is not { ArchivedAt: null } document || document.OriginWorkItemId != item.Id ||
                exact.CreatedByOrganizationUserId != author.OrganizationUserId || exact.CreatedByAgentInstallationId != author.AgentInstallationId ||
                exact.ContentSha256 != sha || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(exact.Content))) != sha)
                throw new InvalidOperationException("QA evidence differs from the exact delivered artifact.");
            WorkDeliveryService.ValidateCriteria(delivery.AcceptanceCriteria, result.Passed, result.Summary, result.Criteria, result.Findings);
            return;
        }
        var publication = await (from p in db.SourceControlPublications join w in db.SourceControlWorkspaces on p.WorkspaceId equals w.Id
            where w.WorkItemId == item.Id && w.AssignmentRevision == item.AssignmentRevision && p.Status != SourceControlPublicationStatus.Superseded
            orderby p.CreatedAt descending select p).FirstOrDefaultAsync(ct) ?? throw new InvalidOperationException("Review requires the current publication.");
        var commit = request.Evidence.SingleOrDefault(x => x.Kind == "commit")?.Value;
        if (stage.StageKey == "technical-review")
        {
            var target = request.Evidence.SingleOrDefault(x => x.Kind == "target-commit")?.Value;
            if (commit != publication.CommitSha || target is not { Length: 40 or 64 } || target.Any(x => !Uri.IsHexDigit(x)) || string.IsNullOrWhiteSpace(request.Summary))
                throw new InvalidOperationException("Technical Review must bind the published source and exact story target.");
            return;
        }
        var integrated = await db.WorkTaskIntegrationReceipts.Where(x => x.PublicationId == publication.Id && x.Status == "Completed")
            .Select(x => x.CandidateCommitSha).SingleOrDefaultAsync(ct);
        var quality = request.Output.Deserialize<WorkDeliveryReviewResult>(JsonOptions) ?? throw new InvalidOperationException("Supply criterion-level integrated task QA.");
        if (commit != integrated || integrated is null || quality.CandidateDigest != integrated || quality.Approved != (request.OutcomeCode == "passed"))
            throw new InvalidOperationException("Task QA must test the exact story commit produced by trusted integration.");
        WorkDeliveryService.ValidateCriteria(delivery.AcceptanceCriteria, quality.Approved, quality.Summary, quality.Criteria, quality.Findings);
        WorkDeliveryService.ValidateQualityEvidence(new(quality.CandidateDigest, 1,
            [new(publication.RepositoryId, publication.TicketBranch, publication.TargetBranch, publication.CommitSha, integrated, integrated)], []), quality);
    }
}
