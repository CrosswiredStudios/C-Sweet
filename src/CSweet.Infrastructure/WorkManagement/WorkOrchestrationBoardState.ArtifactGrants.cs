using System.Text.Json;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

internal static partial class WorkOrchestrationBoardState
{
    private static async Task SynchronizeTaskArtifactGrantsAsync(CSweetDbContext db, WorkSprintExecution execution, DateTimeOffset now, CancellationToken ct)
    {
        var stageIds = execution.Items.SelectMany(x => x.Stages).Select(x => x.Id).ToArray();
        var grants = (await db.ScopedActionGrants.Where(x => x.OrganizationId == execution.OrganizationId &&
            x.GrantedBySubjectKind == GrantSubjectKind.AutomationIdentity && x.GrantedBySubjectId.HasValue &&
            stageIds.Contains(x.GrantedBySubjectId.Value) && x.Action == "artifact.read" && x.RevokedAt == null).ToListAsync(ct))
            .Concat(db.ScopedActionGrants.Local.Where(x => x.GrantedBySubjectKind == GrantSubjectKind.AutomationIdentity &&
                x.GrantedBySubjectId.HasValue && stageIds.Contains(x.GrantedBySubjectId.Value) && x.Action == "artifact.read" && x.RevokedAt == null)).DistinctBy(x => x.Id).ToList();
        var desired = new HashSet<(Guid Stage, Guid Subject, Guid Artifact)>();
        foreach (var item in execution.Items)
        {
            if (execution.Status != WorkSprintExecutionStatus.Active || item.WorkItem?.DeliverySpecificationJson is not { } deliveryJson ||
                JsonSerializer.Deserialize<WorkItemDeliverySpecification>(deliveryJson, JsonOptions) is not { DeliveryKind: "Artifact", DeliveryPlanId: not null }) continue;
            try { await WorkDeliveryTaskAuthorization.RequireAsync(db, item.WorkItem, ct); }
            catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or ArgumentException) { continue; }
            var qa = item.Stages.SingleOrDefault(x => x.StageKey == item.CurrentStageKey && x.Traversal == item.Traversal && x.StageKey == "quality" &&
                x.Status is WorkStageExecutionStatus.Pending or WorkStageExecutionStatus.Dispatching or WorkStageExecutionStatus.Running or WorkStageExecutionStatus.WaitingForHuman);
            if (qa?.OrganizationUserId is null) continue;
            var author = item.Stages.SingleOrDefault(x => x.Traversal == item.Traversal && x.StageKey is "development" or "specialist-execution");
            if (author is null || author.OrganizationUserId == qa.OrganizationUserId ||
                author.AgentInstallationId.HasValue && author.AgentInstallationId == qa.AgentInstallationId) continue;
            var delivered = author.Attempts.Where(x => x.Status == WorkExecutionAttemptStatus.Completed && x.ResultJson != null).OrderByDescending(x => x.Attempt).FirstOrDefault();
            if (delivered is null || !WorkDeliveryService.TryDocument(JsonSerializer.Deserialize<WorkExecutionOutcomeV1>(delivered.ResultJson!, JsonOptions)!.Output,
                out var artifact, out var revision, out var digest)) continue;
            if (!await db.ArtifactRevisions.AnyAsync(x => x.Id == revision && x.ArtifactId == artifact && x.OrganizationId == execution.OrganizationId &&
                x.ContentSha256 == digest && x.CreatedByOrganizationUserId == author.OrganizationUserId && x.CreatedByAgentInstallationId == author.AgentInstallationId &&
                x.Artifact!.OriginWorkItemId == item.WorkItemId && x.Artifact.ArchivedAt == null, ct)) continue;
            var subject = qa.AgentInstallationId ?? qa.OrganizationUserId.Value;
            desired.Add((qa.Id, subject, artifact));
            if (!grants.Any(x => x.GrantedBySubjectId == qa.Id && x.SubjectId == subject && x.ScopeId == artifact && (!x.ExpiresAt.HasValue || x.ExpiresAt > now)))
                db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = execution.OrganizationId,
                    SubjectKind = qa.AgentInstallationId.HasValue ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser,
                    SubjectId = subject, Action = "artifact.read", ScopeKind = GrantScopeKind.Artifact, ScopeId = artifact,
                    GrantedBySubjectKind = GrantSubjectKind.AutomationIdentity, GrantedBySubjectId = qa.Id, GrantedAt = now,
                    ExpiresAt = qa.AgentInstallationId.HasValue ? now.AddHours(1) : null });
        }
        foreach (var grant in grants.Where(x => !x.ScopeId.HasValue || !desired.Contains((x.GrantedBySubjectId!.Value, x.SubjectId, x.ScopeId.Value))))
        { grant.RevokedAt = now; grant.Revision++; }
    }
}
