using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkDeliveryService
{
    private async Task ValidateAssignmentsAsync(Guid org, WorkDeliveryScopeSnapshot scope, CancellationToken ct,
        IReadOnlyList<WorkDeliveryTaskAssignment>? proposals = null)
    {
        var board = await db.WorkBoards.AsNoTracking().SingleOrDefaultAsync(x => x.Id == scope.BoardId && x.OrganizationId == org && x.ArchivedAt == null, ct)
            ?? throw new InvalidOperationException("The scope board is unavailable.");
        if (scope.Planning.AcceptanceCriteria.Count == 0 || scope.Stages.Select(x => x.StageKey).Distinct().Count() != scope.Stages.Count)
            throw new ArgumentException("Acceptance criteria and unique assignments are required.");
        foreach (var assignment in scope.Stages)
        {
            var key = Handler(scope, assignment.StageKey);
            if (assignment.PrincipalKind == "AgentInstallation" && assignment.AgentInstallationId is { } installationId && assignment.OrganizationUserId is { } employeeId)
            {
                var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).SingleOrDefaultAsync(x => x.Id == installationId &&
                    x.BusinessId == org.ToString() && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, ct);
                if (installation?.PackageVersion?.ManifestJson is not { } manifest || !ProvidesExecution(manifest) ||
                    !await db.CoreOrganizationUsers.AnyAsync(x => x.Id == employeeId && x.OrganizationId == org && x.AgentInstallationId == installationId && x.IsActive && x.ArchivedAt == null, ct))
                    throw new InvalidOperationException("Every reviewer needs an active V2 installation and matching employee identity.");
                if (!await db.ProjectParticipants.AnyAsync(x => x.OrganizationId == org && x.WorkstreamId == board.WorkstreamId &&
                    x.OrganizationUserId == employeeId && x.RemovedAt == null, ct)) throw new UnauthorizedAccessException("Reviewers must be assigned to the project.");
            }
            else if (assignment.PrincipalKind != "Human" || assignment.OrganizationUserId is not { } human || assignment.AgentInstallationId.HasValue ||
                !await db.CoreOrganizationUsers.AnyAsync(x => x.Id == human && x.OrganizationId == org && x.EmployeeType == EmployeeType.Human && x.IsActive && x.ArchivedAt == null, ct))
                throw new ArgumentException("A review needs an exact eligible agent or human assignment.");
            await AuthorizeBoardAsync(org, assignment.AgentInstallationId ?? assignment.OrganizationUserId!.Value, scope.BoardId,
                CSweet.Contracts.WorkManagement.WorkItemActions.Read, ct);
            if (!await db.ProjectParticipants.AnyAsync(x => x.OrganizationId == org && x.WorkstreamId == board.WorkstreamId &&
                x.OrganizationUserId == assignment.OrganizationUserId && x.RemovedAt == null, ct))
                throw new UnauthorizedAccessException("Every assigned reviewer must be an explicit project participant.");
            var descendants = scope.ChildIds.ToHashSet();
            var pending = descendants.ToArray();
            while (pending.Length > 0)
            {
                var inputs = await db.CoreWorkTasks.AsNoTracking().Where(x => x.OrganizationId == org && pending.Contains(x.Id)).ToListAsync(ct);
                var next=new List<Guid>();
                foreach(var input in inputs) {
                    if(input.BoardId is {} inputBoard)await AuthorizeBoardAsync(org,assignment.AgentInstallationId??assignment.OrganizationUserId!.Value,
                        inputBoard,CSweet.Contracts.WorkManagement.WorkItemActions.Read,ct);
                    var declaration=input.PlanningSpecificationJson is {} json?Decode<WorkItemPlanningSpecification>(json).Workflow:null;
                    foreach(var child in declaration?.EvidenceItemIds??[])if(descendants.Add(child))next.Add(child);
                }
                pending=next.ToArray();
            }
            if (key == "quality")
            {
                var authors = await db.WorkItemStageAssignments.AsNoTracking().Where(x => descendants.Contains(x.WorkItemId) &&
                    (x.StageKey == "development" || x.StageKey == "specialist-execution") &&
                    (x.OrganizationUserId == assignment.OrganizationUserId || assignment.AgentInstallationId.HasValue &&
                        x.AgentInstallationId == assignment.AgentInstallationId)).ToListAsync(ct);
                foreach (var author in authors)
                {
                    var task = await db.CoreWorkTasks.AsNoTracking().SingleAsync(x => x.Id == author.WorkItemId && x.OrganizationId == org, ct);
                    var taskBoard = await db.WorkBoards.AsNoTracking().SingleAsync(x => x.Id == task.BoardId && x.OrganizationId == org, ct);
                    var review = await db.WorkItemStageAssignments.AsNoTracking().SingleOrDefaultAsync(x =>
                        x.WorkItemId == task.Id && x.StageKey == "quality", ct);
                    var delivery = task.DeliverySpecificationJson is { } deliveryJson ? Decode<WorkItemDeliverySpecification>(deliveryJson) : null;
                    var planning = task.PlanningSpecificationJson is { } planningJson ? Decode<WorkItemPlanningSpecification>(planningJson) : null;
                    var requirements = author.RequirementsJson is { } requirementsJson ? Decode<WorkAssignmentRequirements>(requirementsJson) : null;
                    var proposed = proposals?.SingleOrDefault(x => x.ItemId == task.Id);
                    var proposedAuthor = proposed?.Stages.SingleOrDefault(x => x.StageKey == author.StageKey);
                    var proposedReview = proposed?.Stages.SingleOrDefault(x => x.StageKey == "quality");
                    // A draft can validate the manager review that will be finalized after its ID exists.
                    // A proposal never grants execution; activation and runtime use persisted assignments only.
                    var draftManagerReview = delivery is null && proposedAuthor is not null && proposedReview is not null &&
                        proposedAuthor.OrganizationUserId == author.OrganizationUserId && proposedAuthor.AgentInstallationId == author.AgentInstallationId &&
                        proposedAuthor.Requirements?.RequiredRoleKey == requirements?.RequiredRoleKey &&
                        proposedReview.OrganizationUserId == taskBoard.ManagerOrganizationUserId &&
                        proposedReview.OrganizationUserId != author.OrganizationUserId &&
                        (!author.AgentInstallationId.HasValue || proposedReview.AgentInstallationId != author.AgentInstallationId) &&
                        await IsCurrentManagerReviewAsync(org, taskBoard, proposedReview, ct);
                    // A manager must independently check QA's exact report before it can
                    // contribute to aggregate acceptance; task self-review stays prohibited.
                    var reviewedQaEvidence = taskBoard.ManagerOrganizationUserId is not null && CSweet.Agent.SDK.DeliveryReviewIndependence.IsQaEvidenceArtifact(delivery?.DeliveryKind ?? planning?.DeliveryKind, requirements?.RequiredRoleKey) &&
                        (draftManagerReview || review is not null && review.OrganizationUserId == taskBoard.ManagerOrganizationUserId && review.OrganizationUserId != author.OrganizationUserId &&
                        (!author.AgentInstallationId.HasValue || review.AgentInstallationId != author.AgentInstallationId));
                    if (!reviewedQaEvidence)
                        throw new InvalidOperationException($"Task '{task.Title}' ({task.Id}) conflicts with aggregate QA reviewer {assignment.OrganizationUserId}. Product authors cannot review their own work. QA-authored Artifact reports require a separate quality review by board manager {taskBoard.ManagerOrganizationUserId}; include that proposed assignment when configuring the draft, then finalize it before activation.");
                }
            }
        }
    }

    private async Task<bool> IsCurrentManagerReviewAsync(Guid org, WorkBoard board, WorkStageAssignment review, CancellationToken ct)
    {
        var manager = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == board.ManagerOrganizationUserId &&
            x.OrganizationId == org && x.IsActive && x.ArchivedAt == null, ct);
        if (manager is null || !await db.ProjectParticipants.AnyAsync(x => x.OrganizationId == org && x.WorkstreamId == board.WorkstreamId &&
            x.OrganizationUserId == manager.Id && x.RemovedAt == null, ct)) return false;
        if (manager.EmployeeType == EmployeeType.Human)
            return review.PrincipalKind == "Human" && review.AgentInstallationId is null;
        if (review.PrincipalKind != "AgentInstallation" || manager.AgentInstallationId != review.AgentInstallationId || review.AgentInstallationId is null) return false;
        var manifest = await db.AgentInstallations.AsNoTracking().Where(x => x.Id == review.AgentInstallationId && x.BusinessId == org.ToString() &&
            x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active).Select(x => x.PackageVersion!.ManifestJson).SingleOrDefaultAsync(ct);
        return manifest is not null && ProvidesExecution(manifest);
    }

    private static bool ProvidesExecution(string manifest)
    {
        try
        {
            using var document = JsonDocument.Parse(manifest);
            return document.RootElement.TryGetProperty("provides", out var provides) && provides.ValueKind == JsonValueKind.Array &&
                provides.EnumerateArray().Any(x => x.TryGetProperty("name", out var name) && name.GetString() == WorkManagementCapabilityNames.ExecutionRun);
        }
        catch (JsonException) { return false; }
    }

    private Task ValidateBranchesAsync(Guid org, IReadOnlyList<WorkDeliveryScopeSnapshot> scopes, IReadOnlyList<WorkDeliveryBranchBinding> branches, CancellationToken ct) =>
        ValidateDeclaredBranchesAsync(org, scopes, branches, ct);

    internal async Task ValidateCurrentScopeAsync(WorkDeliveryPlan plan, CancellationToken ct)
    {
        foreach (var scope in Scopes(plan))
        {
            var board = await db.WorkBoards.AsNoTracking().SingleAsync(x => x.Id == scope.BoardId && x.OrganizationId == plan.OrganizationId, ct);
            if (board.WorkstreamId != plan.WorkstreamId || board.ArchivedAt.HasValue) throw new InvalidOperationException("A participating board is no longer in this active project.");
            if (scope.TicketWorkflow is null) throw new InvalidOperationException("Every execution must originate from a declared workflow ticket.");
            await ValidateDeclaredTicketAsync(plan, scope, ct);
            foreach (var id in scope.ChildDeliveryDigests.Keys)
            {
                var task = await db.CoreWorkTasks.AsNoTracking().SingleAsync(t => t.OrganizationId == plan.OrganizationId && t.Id == id, ct);
                await ValidateTaskDeliveryAsync(plan, scope, task, ct);
            }
            await ValidateAssignmentsAsync(plan.OrganizationId, scope, ct);
        }
        await ValidateBranchesAsync(plan.OrganizationId, Scopes(plan), Branches(plan), ct);
    }

    private async Task ValidateTaskDeliveryAsync(WorkDeliveryPlan plan, WorkDeliveryScopeSnapshot story, WorkTask task, CancellationToken ct)
    {
        var delivery = Decode<WorkItemDeliverySpecification>(task.DeliverySpecificationJson ?? "null");
        if (delivery?.DeliveryPlanId != plan.Id || delivery.DeliveryKind is not ("Artifact" or "Code"))
            throw new InvalidOperationException("Finalize every task against this delivery plan before activation.");
        var assignments = await db.WorkItemStageAssignments.AsNoTracking().Where(x => x.WorkItemId == task.Id).ToListAsync(ct);
        if (story.ChildDeliveryDigests.TryGetValue(task.Id, out var pinned) && pinned != TaskDeliveryDigest(task, assignments))
            throw new InvalidOperationException("Task delivery or staffing changed; pause and amend the delivery plan before aggregate validation.");
        var author = assignments.SingleOrDefault(x => x.StageKey is "development" or "specialist-execution")
            ?? throw new InvalidOperationException("Every task needs an exact deliverable author.");
        if (author.PrincipalKind == WorkOrchestrationPrincipalKind.AgentInstallation)
        {
            var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion)
                .SingleOrDefaultAsync(x => x.Id == author.AgentInstallationId && x.BusinessId == plan.OrganizationId.ToString() &&
                    x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, ct);
            if (installation?.PackageVersion?.ManifestJson is not { } manifest || !ProvidesExecution(manifest) ||
                !await db.CoreOrganizationUsers.AnyAsync(x => x.Id == author.OrganizationUserId && x.OrganizationId == plan.OrganizationId &&
                    x.AgentInstallationId == author.AgentInstallationId && x.IsActive && x.ArchivedAt == null, ct))
                throw new InvalidOperationException("The task author needs an active V2 installation and matching employee assignment.");
        }
        else if (author.PrincipalKind != WorkOrchestrationPrincipalKind.Human || author.AgentInstallationId.HasValue ||
            !await db.CoreOrganizationUsers.AnyAsync(x => x.Id == author.OrganizationUserId && x.OrganizationId == plan.OrganizationId &&
                x.EmployeeType == EmployeeType.Human && x.IsActive && x.ArchivedAt == null, ct))
            throw new InvalidOperationException("The task author must be an eligible assigned human or V2 agent.");
        if (!await db.ProjectParticipants.AnyAsync(x => x.WorkstreamId == plan.WorkstreamId && x.OrganizationId == plan.OrganizationId &&
            x.OrganizationUserId == author.OrganizationUserId && x.RemovedAt == null, ct))
            throw new UnauthorizedAccessException("The deliverable author must be an explicit project participant.");
        await AuthorizeBoardAsync(plan.OrganizationId, author.AgentInstallationId ?? author.OrganizationUserId!.Value, story.BoardId,
            CSweet.Contracts.WorkManagement.WorkItemActions.Read, ct);
        var reviews = assignments.Where(a => a.StageKey is "quality" or "technical-review").ToArray();
        if (delivery.DeliveryKind == "Code")
        {
            if (!Branches(plan).Any(x => x.Scope == story.Scope && x.ItemId == story.ItemId &&
                x.RepositoryId == delivery.RepositoryId && x.SourceBranch == delivery.BaseBranch))
                throw new UnauthorizedAccessException("Code tasks must target their explicitly declared source branch.");
        }
        else if (delivery.RepositoryId != Guid.Empty || !string.IsNullOrEmpty(delivery.BaseBranch))
            throw new ArgumentException("Artifact delivery needs no repository or Git branch.");
        foreach (var review in reviews)
        {
            if (review.OrganizationUserId == author.OrganizationUserId || review.AgentInstallationId.HasValue && review.AgentInstallationId == author.AgentInstallationId)
                throw new InvalidOperationException("Deliverable authors cannot review or QA their own work.");

        }
    }

    internal static string TaskDeliveryDigest(WorkTask task, IReadOnlyList<WorkItemStageAssignment> assignments) => Digest(new
    {
        task.DeliverySpecificationJson, task.AccountableOrganizationUserId,
        Assignments = assignments.OrderBy(x => x.StageKey).Select(x => new { x.StageKey, x.PrincipalKind, x.OrganizationUserId,
            x.AgentInstallationId, x.PlatformAction, x.RequirementsJson, x.SelectionEvidenceJson })
    });

    private async Task PinTaskDeliveryAsync(WorkDeliveryPlan plan, CancellationToken ct)
    {
        var scopes = Scopes(plan).ToArray();
        for (var index = 0; index < scopes.Length; index++)
        {
            var digests = new Dictionary<Guid, string>();
            foreach (var id in scopes[index].ChildIds)
            {
                var task = await db.CoreWorkTasks.AsNoTracking().SingleAsync(x => x.Id == id, ct);
                if (!task.IsExecutable) continue;
                await ValidateTaskDeliveryAsync(plan, scopes[index], task, ct);
                var assignments = await db.WorkItemStageAssignments.AsNoTracking().Where(x => x.WorkItemId == id).ToListAsync(ct);
                digests.Add(id, TaskDeliveryDigest(task, assignments));
            }
            scopes[index] = scopes[index] with { ChildDeliveryDigests = digests };
        }
        plan.ScopesJson = Encode(scopes);
    }

    private async Task ProvisionBranchesAsync(WorkDeliveryPlan plan, CancellationToken ct)
    {
        var branches = Branches(plan);
        var ready = new HashSet<(Guid, string)>();
        async Task Ensure(WorkDeliveryBranchBinding branch)
        {
            if (!ready.Add((branch.RepositoryId, branch.SourceBranch))) return;
            foreach (var parent in branches.Where(b => b.RepositoryId == branch.RepositoryId && b.SourceBranch == branch.TargetBranch && b.SourceBranch != branch.SourceBranch))
                await Ensure(parent);
            var repository = await RepositoryAsync(plan.OrganizationId, branch.RepositoryId, ct);
            if (branch.TargetBaseBranch is { } baseline)
                await sourceControl.DeliveryBranchAsync(Operation(plan, repository,
                    branch with { SourceBranch = branch.TargetBranch, TargetBranch = baseline }, "ensure", $"delivery:{plan.Id:N}:target:{branch.RepositoryId:N}:{branch.TargetBranch}"), ct);
            await sourceControl.DeliveryBranchAsync(Operation(plan, repository, branch, "ensure", $"delivery:{plan.Id:N}:{branch.Scope}:{branch.ItemId}:{branch.RepositoryId:N}"), ct);
        }
        foreach (var branch in branches) await Ensure(branch);
    }
    internal async Task<SourceControlRepository> RepositoryAsync(Guid org, Guid id, CancellationToken ct) =>
        await db.SourceControlRepositories.AsNoTracking().Include(x => x.Connection).SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == org &&
            x.ArchivedAt == null && x.Status == SourceControlRepositoryStatus.Ready && x.Connection != null && x.Connection.Status == SourceControlConnectionStatus.Connected, ct)
        ?? throw new UnauthorizedAccessException("Restore project access to the active repository.");
    public static DeliveryBranchOperation Operation(WorkDeliveryPlan plan, SourceControlRepository repository, WorkDeliveryBranchBinding branch,
        string operation, string key, string? source = null, string? target = null, string? candidate = null) => new(plan.OrganizationId, repository.Id,
            repository.Connection!.Provider.ToString(), repository.Connection.SourceAccessInstallationId, repository.Owner, repository.Name,
            operation, branch.SourceBranch, branch.TargetBranch, key, source, target, candidate);

    internal async Task RequireCandidateCurrentAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution, CancellationToken ct)
    {
        var candidate = Candidate(execution) ?? throw new InvalidOperationException("The aggregate has no current candidate.");
        if (candidate.ScopeRevision != plan.ScopeRevision) throw new InvalidOperationException("The candidate's scope is stale.");
        var buildIds = candidate.Repositories.Where(x => x.BuildId.HasValue).Select(x => x.BuildId!.Value).ToArray();
        if (buildIds.Length > 0) await ValidateBuildIdentitiesAsync(plan, execution, buildIds, true, ct);
        await ValidateCurrentComputeTestAsync(plan, execution, ct);
        foreach (var entry in candidate.Repositories)
        {
            var repository = await RepositoryAsync(plan.OrganizationId, entry.RepositoryId, ct);
            var branch = new WorkDeliveryBranchBinding(entry.RepositoryId, execution.Scope, execution.WorkItemId, entry.SourceBranch, entry.TargetBranch);
            var refs = await sourceControl.DeliveryBranchAsync(Operation(plan, repository, branch, "inspect", "inspect:" + execution.Id.ToString("N")), ct);
            if (!execution.Promotions.Any(x => x.RepositoryId == entry.RepositoryId && x.Status == "Completed") &&
                refs.SourceCommitSha == entry.SourceCommitSha && refs.TargetCommitSha == entry.CandidateCommitSha && entry.SourceBranch != entry.TargetBranch)
            {
                var recovered = await sourceControl.DeliveryBranchAsync(Operation(plan, repository, branch, "receipt",
                    $"promote:{execution.Id:N}:{repository.Id:N}:{candidate.Digest}", entry.SourceCommitSha, entry.TargetCommitSha, entry.CandidateCommitSha), ct);
                if (recovered.Promoted)
                {
                    var promoted = execution.Promotions.SingleOrDefault(x => x.RepositoryId == entry.RepositoryId);
                    if (promoted is null)
                    {
                        promoted = new() { Id = Guid.NewGuid(), ExecutionId = execution.Id, RepositoryId = entry.RepositoryId,
                            SourceCommitSha = entry.SourceCommitSha, TargetCommitSha = entry.TargetCommitSha };
                        execution.Promotions.Add(promoted); db.WorkDeliveryPromotions.Add(promoted);
                    }
                    promoted.Status = "Completed"; promoted.MergeCommitSha = recovered.CandidateCommitSha; promoted.Error = null;
                }
            }
            if (execution.Promotions.SingleOrDefault(x => x.RepositoryId == entry.RepositoryId && x.Status == "Completed") is { } receipt)
            {
                if (refs.SourceCommitSha != entry.SourceCommitSha || refs.TargetCommitSha != receipt.MergeCommitSha)
                    throw new InvalidOperationException("A promoted repository changed after its accepted candidate. Preserve its receipt and create a follow-up release for the changed content.");
                continue;
            }
            if (refs.SourceCommitSha != entry.SourceCommitSha || refs.TargetCommitSha != entry.TargetCommitSha)
                throw new InvalidOperationException("The candidate source or target changed; fresh regression and acceptance are required.");
        }
        foreach (var document in candidate.Documents)
        {
            if (!await db.ArtifactRevisions.AnyAsync(x => x.Id == document.RevisionId && x.ArtifactId == document.ArtifactId &&
                x.OrganizationId == plan.OrganizationId && x.ContentSha256 == document.Sha256, ct))
                throw new InvalidOperationException("The accepted document revision is unavailable.");
            var latest = await db.WorkItemExecutions.AsNoTracking().Include(x => x.Stages).ThenInclude(x => x.Attempts)
                .Where(x => x.WorkItemId == document.ItemId).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            var delivered = latest?.Stages.Where(x => x.Traversal == latest.Traversal && x.StageKey is "development" or "specialist-execution")
                .SelectMany(x => x.Attempts).Where(x => x.Status == WorkExecutionAttemptStatus.Completed && x.ResultJson != null)
                .OrderByDescending(x => x.CreatedAt).FirstOrDefault();
            if (delivered is null || !TryDocument(Decode<WorkExecutionOutcomeV1>(delivered.ResultJson!).Output, out var artifact, out var revision, out var digest) ||
                artifact != document.ArtifactId || revision != document.RevisionId || digest != document.Sha256)
                throw new InvalidOperationException("The task's delivered document changed; fresh aggregate validation and acceptance are required.");
        }
    }
}
