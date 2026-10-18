using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkDeliveryService
{
    private async Task<WorkDeliveryEvidenceResponse> ReadTaskInputAsync(Guid org, Guid actor, ReadWorkDeliveryEvidenceRequest request, CancellationToken ct)
    {
        async Task<WorkExecutionAssignment> AuthorizeAsync()
        {
            var member = await MemberAsync(org, actor, ct);
            var execution = await db.WorkItemExecutions.AsNoTracking().Include(e => e.WorkItem).Include(e => e.SprintExecution)
                .Include(e => e.Stages).ThenInclude(s => s.Attempts).SingleOrDefaultAsync(e => e.Id == request.ExecutionId && e.WorkItem!.OrganizationId == org, ct)
                ?? throw new UnauthorizedAccessException("The current task input assignment is unavailable.");
            var item = execution.WorkItem!;
            var stage = execution.Stages.SingleOrDefault(s => s.Traversal == execution.Traversal && s.StageKey == execution.CurrentStageKey);
            var attempt = stage?.Attempts.OrderByDescending(a => a.Attempt).FirstOrDefault();
            if (inbox is null || member.AgentInstallationId is null || stage?.StageKey != "specialist-execution" ||
                stage.OrganizationUserId != member.Id || stage.AgentInstallationId != member.AgentInstallationId ||
                stage.Status is not (WorkStageExecutionStatus.Dispatching or WorkStageExecutionStatus.Running) ||
                execution.SprintExecution?.Status != WorkSprintExecutionStatus.Active ||
                attempt?.Status is not (WorkExecutionAttemptStatus.Pending or WorkExecutionAttemptStatus.Running) || attempt.AgentWorkItemId is null)
                throw new UnauthorizedAccessException("Only the current artifact author attempt may read repository inputs.");
            var work = await db.AgentWorkItems.AsNoTracking().Include(w => w.Attempts).SingleAsync(w => w.Id == attempt.AgentWorkItemId, ct);
            if (work.OrganizationId != org.ToString() || work.AgentInstallationId != member.AgentInstallationId ||
                work.SourceType != "WorkStageExecution" || work.SourceId != stage.Id.ToString("D") ||
                work.Status != AgentWorkStatus.Leased || work.DeadlineAt <= clock.GetUtcNow() ||
                !work.Attempts.Any(a => a.FinishedAt == null && a.LeaseExpiresAt > clock.GetUtcNow()))
                throw new UnauthorizedAccessException("Repository input access requires a live assigned work lease.");
            var assignment = inbox.ReadPayload(work).Deserialize<WorkExecutionAssignment>(Json)
                ?? throw new UnauthorizedAccessException("The durable input assignment is missing.");
            var (plan, branches, dependenciesDigest) = await WorkArtifactRepositoryInput.RequireAsync(db, item, ct);
            if (assignment.OrganizationId != org || assignment.OrganizationUserId != member.Id || assignment.AgentInstallationId != member.AgentInstallationId ||
                assignment.ExecutionId != execution.Id || assignment.StageExecutionId != stage.Id || assignment.AttemptId != attempt.Id ||
                assignment.TaskItemId != item.Id || assignment.BoardId != item.BoardId || assignment.PlanningRevision != item.PlanningRevision ||
                assignment.AssignmentRevision != item.AssignmentRevision || assignment.DeliveryPlanId != request.PlanId || plan.Id != request.PlanId ||
                assignment.ScopeRevision != plan.ScopeRevision || assignment.WorkstreamId != plan.WorkstreamId ||
                assignment.Candidate is not { Documents.Count: 0, Repositories.Count: > 0 } candidate || candidate.ScopeRevision != plan.ScopeRevision ||
                candidate.Digest != WorkArtifactRepositoryInput.InputDigest(assignment, candidate.Repositories, dependenciesDigest) ||
                candidate.Repositories.Count != branches.Length || candidate.Repositories.Any(r => !branches.Any(b => b.RepositoryId == r.RepositoryId &&
                    b.SourceBranch == r.SourceBranch && r.SourceBranch == r.TargetBranch && r.SourceCommitSha == r.TargetCommitSha && r.SourceCommitSha == r.CandidateCommitSha)))
                throw new UnauthorizedAccessException("The saved repository input no longer belongs to the current accepted assignment.");
            if (!(await authorization.AuthorizeAsync(org, CSweet.Domain.Security.GrantSubjectKind.AgentInstallation,
                member.AgentInstallationId.Value, CSweet.Contracts.WorkManagement.WorkItemActions.Read,
                CSweet.Domain.Security.GrantScopeKind.WorkItem, item.Id, ct)).Allowed)
                throw new UnauthorizedAccessException("Current task read access is required for repository inputs.");
            foreach (var repository in candidate.Repositories)
                await WorkArtifactRepositoryInput.RepositoryAsync(db, org, assignment.BoardId, member.Id, repository.RepositoryId, ct);
            return assignment;
        }
        var assignment = await AuthorizeAsync();
        var candidate = assignment.Candidate!;
        if (request.RepositoryId is not { } repositoryId) return new(candidate, []);
        var entry = candidate.Repositories.SingleOrDefault(r => r.RepositoryId == repositoryId)
            ?? throw new UnauthorizedAccessException("The requested repository is outside the saved input snapshot.");
        var repository = await RepositoryAsync(org, repositoryId, ct);
        var key = $"artifact-input:{assignment.AttemptId:N}:{candidate.Digest}";
        var snapshot = repository.Connection!.Provider == SourceControlProvider.InternalGit
            ? await sourceControl.PrepareInternalWorkspaceAsync(new(org, repositoryId, assignment.ExecutionId, entry.SourceBranch,
                entry.SourceBranch, entry.CandidateCommitSha, key), ct)
            : await sourceControl.PrepareWorkspaceAsync(new(repository.Connection.SourceAccessInstallationId!.Value,
                long.Parse(repository.ExternalRepositoryId, System.Globalization.CultureInfo.InvariantCulture), repository.Owner, repository.Name,
                entry.SourceBranch, assignment.ExecutionId, entry.SourceBranch, entry.CandidateCommitSha, key), ct);
        if (snapshot.BaseCommitSha != entry.CandidateCommitSha) throw new InvalidOperationException("The provider returned a different repository input commit.");
        if ((await AuthorizeAsync()).Candidate?.Digest != candidate.Digest)
            throw new UnauthorizedAccessException("Repository input authority changed during the snapshot read.");
        // GitHost's ArtifactSha256 identifies the normalized file manifest, not the ZIP bytes.
        return new(candidate, [], snapshot.Archive, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(snapshot.Archive)), snapshot.BaseCommitSha);
    }
}
