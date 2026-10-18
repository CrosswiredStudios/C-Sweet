using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Contracts.Core;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkOrchestrator
{
    internal const string QaRecoveryDocumentType = ArtifactDocumentTypes.RecoveredQaEvidence;
    // Owner/manager-retained observations are input data only. They confer neither a QA pass nor repository access.
    internal async Task<IReadOnlyList<W.WorkExecutionEvidence>> QaRecoveryEvidenceAsync(
        W.WorkExecutionAssignment assignment, WorkBoard board, CancellationToken ct)
    {
        if (assignment.StageKey is not ("quality" or "specialist-execution") || assignment.Candidate is null) return [];
        if(assignment.OrganizationId!=board.OrganizationId || assignment.BoardId!=board.Id || assignment.WorkstreamId!=board.WorkstreamId)
            throw new UnauthorizedAccessException("Retained QA input requires the exact current board/project assignment.");
        // Legacy human artifact creation retained the origin task but omitted project/team fields.
        // For those documents, verify scope against the protected source assignment below. Explicit mismatches never match.
        var documents = await db.CoreArtifacts.AsNoTracking().Where(a => a.OrganizationId == assignment.OrganizationId &&
            a.OriginWorkItemId == assignment.ItemId && (a.WorkstreamId == null || a.WorkstreamId == board.WorkstreamId) && (a.TeamId == null || a.TeamId == board.TeamId) &&
            a.DocumentType == QaRecoveryDocumentType && a.ArchivedAt == null &&
            (a.SubmittedRevisionId != null || a.Revisions.Any(r => r.Status == ArtifactRevisionStatus.Retained)))
            .OrderByDescending(a => a.CreatedAt).Take(4).ToListAsync(ct);
        var evidence = new List<W.WorkExecutionEvidence>(); var bytes=0;
        void Reject(Guid artifactId,string reason) => evidence.Add(new("qa-recovery-warning.v1","Retained observations require fresh checks",
            $"Retained document {artifactId:D} was not used: {reason} No QA verdict is granted from this input. Execute the necessary checks against the current authorized snapshot."));
        foreach(var document in documents)
        {
            if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(u => u.Id == document.CreatedByOrganizationUserId &&
                u.OrganizationId == assignment.OrganizationId && u.IsActive && u.ArchivedAt == null && u.EmployeeType == EmployeeType.Human &&
                (u.PermissionLevel == OrganizationPermissionLevel.Owner || u.Id == board.ManagerOrganizationUserId), ct)) continue;
            // A later draft never replaces the last explicitly retained, immutable input.
            // Legacy submitted observations remain readable during the lifecycle cutover.
            var revision = await db.ArtifactRevisions.AsNoTracking().Where(r =>
                r.ArtifactId == document.Id && r.OrganizationId == assignment.OrganizationId &&
                (r.Status == ArtifactRevisionStatus.Retained || r.Id == document.SubmittedRevisionId))
                .OrderByDescending(r => r.Number).FirstAsync(ct);
            if(revision.CreatedByOrganizationUserId!=document.CreatedByOrganizationUserId || revision.CreatedByAgentInstallationId.HasValue)
            {Reject(document.Id,"the revision is not the exact human-retained input");continue;}
            QaRecoveryEvidence? saved;
            try {saved=JsonSerializer.Deserialize<QaRecoveryEvidence>(revision.Content,JsonOptions);}
            catch(JsonException){Reject(document.Id,"the retained input is malformed");continue;}
            if(saved is null){Reject(document.Id,"the retained input is empty");continue;}
            if (saved.ItemId != assignment.ItemId ||
                !assignment.Candidate.Repositories.Any(r => r.CandidateCommitSha == saved.SourceCommitSha)) continue;
            var attempt = await db.WorkExecutionAttempts.AsNoTracking().Include(a => a.AgentWorkItem).Include(a => a.StageExecution)!
                .ThenInclude(s => s!.ItemExecution).SingleOrDefaultAsync(a => a.Id == saved.AttemptId, ct);
            var audit = await db.AuditEvents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == saved.AuditEventId &&
                a.OrganizationId == assignment.OrganizationId && a.EventType == "model.call.completed" && a.EntityType == "AgentRunLog", ct);
            if (attempt?.StageExecution?.ItemExecution?.WorkItemId != assignment.ItemId || attempt.StageExecution.Id != saved.StageExecutionId ||
                attempt.Status != WorkExecutionAttemptStatus.Completed || attempt.AgentWorkItem is null || audit?.EntityId is null ||
                !string.Equals(audit.EvidenceSha256, saved.AuditSha256, StringComparison.OrdinalIgnoreCase) ||
                !await db.AgentRunLogs.AsNoTracking().AnyAsync(r => r.Id == audit.EntityId && r.AgentWorkItemId == attempt.AgentWorkItemId &&
                    r.AgentInstallationId == attempt.AgentWorkItem.AgentInstallationId && r.Status == "Completed", ct))
            {Reject(document.Id,"the completed source attempt or audit receipt does not match");continue;}
            if(attempt.AgentWorkItem.MemoryErasedAt.HasValue){Reject(document.Id,"the source input is no longer available");continue;}
            W.WorkExecutionAssignment? original;W.WorkExecutionInputV1? originalInput;
            try {
                original=inbox.ReadPayload(attempt.AgentWorkItem).Deserialize<W.WorkExecutionAssignment>(JsonOptions);
                originalInput=original?.Input.Deserialize<W.WorkExecutionInputV1>(JsonOptions);
            }catch(Exception error) when(error is JsonException or CryptographicException or InvalidOperationException) {
                Reject(document.Id,"the protected source input is unavailable");continue;
            }
            if (original?.OrganizationId != assignment.OrganizationId || original.ItemId != assignment.ItemId ||
                original.BoardId!=board.Id || original.WorkstreamId!=board.WorkstreamId || originalInput?.TeamId!=board.TeamId ||
                original.StageExecutionId != saved.StageExecutionId || original.AttemptId != saved.AttemptId ||
                original.PlanningRevision != saved.PlanningRevision || original.AssignmentRevision != saved.AssignmentRevision ||
                original.Candidate?.Repositories.Any(r => r.CandidateCommitSha == saved.SourceCommitSha) != true ||
                !string.Equals(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(revision.Content))), revision.ContentSha256, StringComparison.OrdinalIgnoreCase))
            {Reject(document.Id,"the source identity, scoped context or document content changed");continue;}
            var size=Encoding.UTF8.GetByteCount(revision.Content);
            if(bytes+size>128000){Reject(document.Id,"the retained input exceeds this assignment's evidence budget");continue;}
            bytes+=size;
            evidence.Add(new("qa-recovered-evidence.v1", "Historical observed QA commands; no verdict granted",
                JsonSerializer.Serialize(new {artifactId=document.Id,revisionId=revision.Id,sha256=revision.ContentSha256,content=saved}, JsonOptions), "application/json"));
        }
        return evidence;
    }

    internal sealed record QaRecoveryEvidence(Guid ItemId,Guid StageExecutionId,Guid AttemptId,long PlanningRevision,
        long AssignmentRevision,string SourceCommitSha,Guid AuditEventId,string AuditSha256,JsonElement Observations);
}
