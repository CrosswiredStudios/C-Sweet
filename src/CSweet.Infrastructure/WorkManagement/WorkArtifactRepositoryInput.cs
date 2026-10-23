using CSweet.Application.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Repository inputs for artifact authors are derived from accepted dependencies, never output bindings.</summary>
internal static class WorkArtifactRepositoryInput
{
    internal static async Task<(WorkDeliveryPlan Plan, WorkDeliveryBranchBinding[] Branches, string DependenciesDigest)> RequireAsync(
        CSweetDbContext db, WorkTask item, CancellationToken ct)
    {
        var (plan, _) = await WorkDeliveryTaskAuthorization.RequireAsync(db, item, ct);
        var delivery = WorkDeliveryService.Decode<WorkItemDeliverySpecification>(item.DeliverySpecificationJson!);
        if (delivery.DeliveryKind != "Artifact") throw new UnauthorizedAccessException("Only artifact authors may read task repository inputs.");
        var scopes = WorkDeliveryService.Decode<WorkDeliveryScopeSnapshot[]>(plan.ScopesJson);
        var story = scopes.Single(s => s.Scope == WorkExecutionScopes.Story && s.ItemId == item.ParentWorkTaskId);
        var branches = WorkDeliveryService.Decode<WorkDeliveryBranchBinding[]>(plan.BranchesJson);
        var selected = new Dictionary<Guid, WorkDeliveryBranchBinding>();
        var inputs = new List<object>();
        foreach (var id in delivery.DependencyItemIds.Distinct())
        {
            if (!story.ChildIds.Contains(id)) throw new UnauthorizedAccessException("Repository inputs must belong to declared dependencies in the same approved Story.");
            var dependency = await db.CoreWorkTasks.AsNoTracking().SingleAsync(x => x.Id == id && x.OrganizationId == item.OrganizationId, ct);
            await WorkDeliveryTaskAuthorization.RequireAsync(db, dependency, ct);
            var spec = WorkDeliveryService.Decode<WorkItemDeliverySpecification>(dependency.DeliverySpecificationJson!);
            if (spec.DeliveryKind != "Code") continue;
            var receipt = await db.WorkTaskIntegrationReceipts.AsNoTracking().Where(r => r.OrganizationId == item.OrganizationId && r.PlanId == plan.Id &&
                r.WorkItemId == id && r.RepositoryId == spec.RepositoryId && r.Status == "Completed" && r.CandidateCommitSha == dependency.MergeCommitSha)
                .OrderBy(r => r.Id).FirstOrDefaultAsync(ct);
            if (dependency.Status != WorkTaskStatus.Completed || dependency.MergeStatus != "Merged" || string.IsNullOrWhiteSpace(dependency.MergeCommitSha) || receipt is null)
                throw new InvalidOperationException("Repository input dependencies must have completed trusted integration and QA.");
            inputs.Add(new { dependency.Id, dependency.PlanningRevision, dependency.AssignmentRevision, dependency.MergeCommitSha, integrationReceiptId = receipt.Id });
            var branch = branches.Single(b => b.Scope == WorkExecutionScopes.Story && b.ItemId == story.ItemId && b.RepositoryId == spec.RepositoryId);
            selected[branch.RepositoryId] = branch;
        }
        return (plan, selected.Values.OrderBy(b => b.RepositoryId).ToArray(), WorkDeliveryService.Digest(inputs));
    }

    internal static async Task<SourceControlRepository> RepositoryAsync(CSweetDbContext db, Guid org, Guid board, Guid member, Guid repository, CancellationToken ct)
    {
        var result = await db.SourceControlRepositories.AsNoTracking().Include(r => r.Connection).SingleOrDefaultAsync(r => r.Id == repository &&
            r.OrganizationId == org && r.ArchivedAt == null && r.Status == SourceControlRepositoryStatus.Ready &&
            r.Connection != null && r.Connection.Status == SourceControlConnectionStatus.Connected, ct)
            ?? throw new UnauthorizedAccessException("The repository input connection is unavailable.");
        if (!await db.TeamRepositoryPolicies.AnyAsync(p => p.OrganizationId == org && p.RepositoryId == repository && p.DisabledAt == null &&
            db.WorkBoards.Any(b => b.Id == board && b.OrganizationId == org && b.TeamId == p.TeamId) &&
            db.TeamMemberships.Any(m => m.OrganizationId == org && m.TeamId == p.TeamId && m.OrganizationUserId == member && m.EndedAt == null), ct))
            throw new UnauthorizedAccessException("The artifact author requires current team repository access.");
        return result;
    }

    internal static async Task<WorkDeliveryCandidate?> PinAsync(CSweetDbContext db, ITrustedSourceControlHostClient? host,
        WorkTask item, WorkExecutionAssignment assignment, CancellationToken ct)
    {
        var (plan, branches, dependenciesDigest) = await RequireAsync(db, item, ct);
        if (branches.Length == 0) return null; // Ordinary document-only artifact work retains its existing path.
        if (host is null) throw new InvalidOperationException("The repository input snapshot service is unavailable.");
        var repositories = new List<WorkDeliveryRepositoryCandidate>();
        foreach (var binding in branches)
        {
            var repository = await RepositoryAsync(db, item.OrganizationId, assignment.BoardId, assignment.OrganizationUserId, binding.RepositoryId, ct);
            var head = await host.DeliveryBranchAsync(WorkDeliveryService.Operation(plan, repository, binding, "inspect", $"artifact-input:{assignment.AttemptId:N}"), ct);
            if (head.Error is not null || head.SourceCommitSha.Length != 40 || !head.SourceCommitSha.All(Uri.IsHexDigit))
                throw new InvalidOperationException("The approved Story input could not be pinned to an exact commit.");
            repositories.Add(new(binding.RepositoryId, binding.SourceBranch, binding.SourceBranch,
                head.SourceCommitSha, head.SourceCommitSha, head.SourceCommitSha));
        }
        return new(InputDigest(assignment, repositories, dependenciesDigest), plan.ScopeRevision, repositories, []);
    }

    // Reused by live snapshot reads and completed asynchronous compute handoffs. The latter
    // has already relinquished its inbox lease; identity still comes from the protected assignment.
    internal static async Task<WorkDeliveryCandidate> AuthorizeAssignmentAsync(CSweetDbContext db, WorkTask item,
        WorkExecutionAssignment assignment, CancellationToken ct)
    {
        var (plan, branches, dependenciesDigest) = await RequireAsync(db, item, ct);
        if (assignment.OrganizationId != item.OrganizationId || assignment.TaskItemId != item.Id || assignment.BoardId != item.BoardId ||
            assignment.PlanningRevision != item.PlanningRevision || assignment.AssignmentRevision != item.AssignmentRevision ||
            assignment.DeliveryPlanId != plan.Id || assignment.ScopeRevision != plan.ScopeRevision || assignment.WorkstreamId != plan.WorkstreamId ||
            assignment.Candidate is not { Documents.Count: 0, Repositories.Count: > 0 } candidate || candidate.ScopeRevision != plan.ScopeRevision ||
            candidate.Digest != InputDigest(assignment, candidate.Repositories, dependenciesDigest) || candidate.Repositories.Count != branches.Length ||
            candidate.Repositories.Any(r => !branches.Any(b => b.RepositoryId == r.RepositoryId && b.SourceBranch == r.SourceBranch &&
                r.SourceBranch == r.TargetBranch && r.SourceCommitSha == r.TargetCommitSha && r.SourceCommitSha == r.CandidateCommitSha)))
            throw new UnauthorizedAccessException("The saved repository input no longer belongs to the current accepted assignment.");
        foreach (var repository in candidate.Repositories)
            await RepositoryAsync(db, item.OrganizationId, assignment.BoardId, assignment.OrganizationUserId, repository.RepositoryId, ct);
        return candidate;
    }

    internal static string InputDigest(WorkExecutionAssignment assignment, IReadOnlyList<WorkDeliveryRepositoryCandidate> repositories, string dependenciesDigest) =>
        WorkDeliveryService.Digest(new { purpose = "artifact-repository-input-v1", assignment.DeliveryPlanId, assignment.ScopeRevision,
            assignment.ExecutionId, assignment.StageExecutionId, assignment.AttemptId, assignment.TaskItemId,
            assignment.AssignmentRevision, assignment.PlanningRevision, repositories, dependenciesDigest });
}
