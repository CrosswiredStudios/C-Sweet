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
    private async Task ValidateAssignmentsAsync(Guid org, WorkDeliveryScopeSnapshot scope, CancellationToken ct)
    {
        var board = await db.WorkBoards.AsNoTracking().SingleOrDefaultAsync(x => x.Id == scope.BoardId && x.OrganizationId == org && x.ArchivedAt == null, ct)
            ?? throw new InvalidOperationException("The scope board is unavailable.");
        if (scope.Scope == WorkExecutionScopes.Epic)
        {
            var manager = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == board.ManagerOrganizationUserId &&
                x.OrganizationId == org && x.IsActive && x.ArchivedAt == null, ct)
                ?? throw new InvalidOperationException("Assign an active manager for every epic board.");
            if (manager.AgentInstallationId is { } managerInstallation)
            {
                var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).SingleOrDefaultAsync(x =>
                    x.Id == managerInstallation && x.BusinessId == org.ToString() && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, ct);
                if (installation?.PackageVersion?.ManifestJson is not { } manifest || !ProvidesV2(manifest))
                    throw new InvalidOperationException("An epic acceptance manager needs an active V2 execution installation.");
            }
        }
        if (scope.Planning.AcceptanceCriteria.Count == 0 || scope.Stages.Select(x => x.StageKey).Distinct().Count() != scope.Stages.Count)
            throw new ArgumentException("Acceptance criteria and unique assignments are required.");
        var required = scope.Scope == WorkExecutionScopes.Epic ? new[] { "technical-review" } : scope.Scope == WorkExecutionScopes.Story ? new[] { "quality" } : new[] { "quality", "technical-review" };
        if (required.Any(key => !scope.Stages.Any(x => x.StageKey == key)) ||
            scope.Stages.Any(x => x.StageKey is not ("quality" or "technical-review" or "build-readiness")))
            throw new ArgumentException("Assign the required review stages and use only supported aggregate stages.");
        foreach (var assignment in scope.Stages)
        {
            var key = assignment.StageKey;
            if (assignment.PrincipalKind == "AgentInstallation" && assignment.AgentInstallationId is { } installationId && assignment.OrganizationUserId is { } employeeId)
            {
                var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).SingleOrDefaultAsync(x => x.Id == installationId &&
                    x.BusinessId == org.ToString() && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, ct);
                if (installation?.PackageVersion?.ManifestJson is not { } manifest || !ProvidesV2(manifest) ||
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
            for (var depth = 0; depth < 3; depth++)
            {
                var children = await db.CoreWorkTasks.Where(x => x.OrganizationId == org && x.ParentWorkTaskId.HasValue && descendants.Contains(x.ParentWorkTaskId.Value))
                    .Select(x => x.Id).ToListAsync(ct);
                if (children.All(descendants.Contains)) break;
                descendants.UnionWith(children);
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
                    var requirements = author.RequirementsJson is { } requirementsJson ? Decode<WorkAssignmentRequirements>(requirementsJson) : null;
                    // A manager must independently check QA's exact report before it can
                    // contribute to aggregate acceptance; task self-review stays prohibited.
                    var reviewedQaEvidence = review is not null && taskBoard.ManagerOrganizationUserId is not null && CSweet.Agent.SDK.DeliveryReviewIndependence.IsQaEvidenceArtifact(delivery?.DeliveryKind, requirements?.RequiredRoleKey) &&
                        review?.OrganizationUserId == taskBoard.ManagerOrganizationUserId && review?.OrganizationUserId != author.OrganizationUserId &&
                        (!author.AgentInstallationId.HasValue || review?.AgentInstallationId != author.AgentInstallationId);
                    if (!reviewedQaEvidence)
                        throw new InvalidOperationException("The QA reviewer must be independent of scoped product authors; QA evidence artifacts require a separate manager review.");
                }
            }
        }
    }

    private static bool ProvidesV2(string manifest)
    {
        try
        {
            using var document = JsonDocument.Parse(manifest);
            return document.RootElement.TryGetProperty("provides", out var provides) && provides.ValueKind == JsonValueKind.Array &&
                provides.EnumerateArray().Any(x => x.TryGetProperty("name", out var name) && name.GetString() == WorkManagementCapabilityNames.ExecutionRunV2);
        }
        catch (JsonException) { return false; }
    }

    private async Task ValidateBranchesAsync(Guid org, IReadOnlyList<WorkDeliveryScopeSnapshot> scopes, IReadOnlyList<WorkDeliveryBranchBinding> branches, CancellationToken ct)
    {
        if (branches.Count > 500 || branches.Any(x => x.SourceBranch == x.TargetBranch || x.SourceBranch.Length > 255 || x.TargetBranch.Length > 255 ||
            string.IsNullOrWhiteSpace(x.SourceBranch) || string.IsNullOrWhiteSpace(x.TargetBranch)) ||
            branches.Select(x => (x.RepositoryId, x.SourceBranch)).Distinct().Count() != branches.Count)
            throw new ArgumentException("Branch sources must be unique with distinct, valid integration targets.");
        foreach (var branch in branches)
        {
            var scope = scopes.SingleOrDefault(x => x.Scope == branch.Scope && x.ItemId == branch.ItemId)
                ?? throw new ArgumentException("The branch belongs to an unknown delivery scope.");
            var repository = await RepositoryAsync(org, branch.RepositoryId, ct);
            if (branch.Scope != WorkExecutionScopes.Release && (branch.SourceBranch == repository.DefaultBranch || branch.TargetBranch == repository.DefaultBranch))
                throw new ArgumentException("Only release promotion may target the default branch.");
            if (branch.Scope == WorkExecutionScopes.Release && branch.TargetBranch != repository.DefaultBranch)
                throw new ArgumentException("A release must promote to its repository's default branch.");
            if (scope.Stages.Any(x => x.StageKey == "build-readiness") && (branch.Build is not { } build ||
                build.ToolchainDefinitionId == Guid.Empty || build.ProviderInstallationId == Guid.Empty ||
                string.IsNullOrWhiteSpace(build.RecipeKey) || string.IsNullOrWhiteSpace(build.TargetKey) || build.Configuration.ValueKind != JsonValueKind.Object))
                throw new ArgumentException("Bind a certified build provider, recipe, target and configuration for every release repository.");
            if (branch.Scope is not (WorkExecutionScopes.Story or WorkExecutionScopes.Epic or WorkExecutionScopes.Release))
                throw new ArgumentException("Bind story, optional epic, or release branches.");
            var board = await db.WorkBoards.AsNoTracking().SingleAsync(x => x.Id == scope.BoardId && x.OrganizationId == org, ct);
            var participatingBoards = branch.Scope == WorkExecutionScopes.Release
                ? scopes.Where(x => x.Scope == WorkExecutionScopes.Story && branches.Any(b => b.Scope == WorkExecutionScopes.Story && b.ItemId == x.ItemId && b.RepositoryId == branch.RepositoryId))
                    .Select(x => x.BoardId).Distinct().ToArray() : [scope.BoardId];
            var teams = await db.WorkBoards.Where(x => participatingBoards.Contains(x.Id) && x.OrganizationId == org).Select(x => x.TeamId).ToListAsync(ct);
            var allowedTeams = await db.TeamRepositoryPolicies.Where(x => x.OrganizationId == org && teams.Contains(x.TeamId) && x.RepositoryId == branch.RepositoryId && x.DisabledAt == null)
                .Select(x => (Guid?)x.TeamId).ToListAsync(ct);
            if (teams.Count == 0 || teams.Any(team => !team.HasValue || !allowedTeams.Contains(team)))
                throw new UnauthorizedAccessException("The participating team lacks access to this repository.");
            if (branch.Scope == WorkExecutionScopes.Story)
            {
                var parentId = await db.CoreWorkTasks.Where(x => x.Id == branch.ItemId && x.OrganizationId == org).Select(x => x.ParentWorkTaskId).SingleAsync(ct);
                var parentBranch = branches.SingleOrDefault(x => x.RepositoryId == branch.RepositoryId && x.Scope == WorkExecutionScopes.Epic && x.ItemId == parentId)
                    ?? branches.SingleOrDefault(x => x.RepositoryId == branch.RepositoryId && x.Scope == WorkExecutionScopes.Release);
                if (parentBranch is null || parentBranch.SourceBranch != branch.TargetBranch)
                    throw new ArgumentException("Story branches must target their epic branch or shared release.");
            }
            if (branch.Scope == WorkExecutionScopes.Epic && !branches.Any(x => x.RepositoryId == branch.RepositoryId &&
                x.Scope == WorkExecutionScopes.Release && x.SourceBranch == branch.TargetBranch))
                throw new ArgumentException("Epic branches must target the shared release.");
            if (branch.Scope == WorkExecutionScopes.Epic && !scope.Stages.Any(x => x.StageKey == "quality"))
                throw new ArgumentException("An isolated epic branch needs independent QA of its integrated candidate.");
        }
        foreach (var group in branches.GroupBy(x => x.RepositoryId))
        {
            var map = group.ToDictionary(x => x.SourceBranch, x => x.TargetBranch, StringComparer.Ordinal);
            foreach (var source in map.Keys)
            {
                var seen = new HashSet<string>(); var current = source;
                while (map.TryGetValue(current, out var next))
                { if (!seen.Add(current)) throw new ArgumentException("The integration branch graph contains a cycle."); current = next; }
            }
            if (group.Count(x => x.Scope == WorkExecutionScopes.Release) != 1) throw new ArgumentException("Each repository requires one release branch.");
        }
        foreach (var story in scopes.Where(x => x.Scope == WorkExecutionScopes.Story))
        {
            var tasks = await db.CoreWorkTasks.AsNoTracking().Where(x => story.ChildIds.Contains(x.Id)).ToListAsync(ct);
            foreach (var task in tasks.Where(x => x.DeliverySpecificationJson != null))
            {
                var delivery = Decode<WorkItemDeliverySpecification>(task.DeliverySpecificationJson!);
                if (delivery.RepositoryId != Guid.Empty && !branches.Any(x => x.Scope == WorkExecutionScopes.Story && x.ItemId == story.ItemId && x.RepositoryId == delivery.RepositoryId))
                    throw new ArgumentException("Every code repository needs an explicit story integration branch.");
            }
        }
        foreach (var scope in scopes)
        {
            var relevant = new List<WorkDeliveryScopeSnapshot>();
            void Collect(WorkDeliveryScopeSnapshot current)
            {
                relevant.Add(current);
                foreach (var child in current.ChildIds)
                    if (scopes.SingleOrDefault(x => x.ItemId == child) is { } childScope) Collect(childScope);
            }
            Collect(scope);
            var storyIds = relevant.Where(x => x.Scope == WorkExecutionScopes.Story).Select(x => x.ItemId).ToArray();
            var repositories = branches.Where(x => x.Scope == WorkExecutionScopes.Story && storyIds.Contains(x.ItemId)).Select(x => x.RepositoryId).Distinct().ToArray();
            foreach (var reviewer in scope.Stages)
            {
                foreach (var boardId in relevant.Select(x => x.BoardId).Distinct())
                    await AuthorizeBoardAsync(org, reviewer.AgentInstallationId ?? reviewer.OrganizationUserId!.Value, boardId,
                        CSweet.Contracts.WorkManagement.WorkItemActions.Read, ct);
                var teams = await db.TeamMemberships.Where(x => x.OrganizationId == org && x.OrganizationUserId == reviewer.OrganizationUserId && x.EndedAt == null)
                    .Select(x => x.TeamId).ToListAsync(ct);
                var allowed = await db.TeamRepositoryPolicies.Where(x => x.OrganizationId == org && teams.Contains(x.TeamId) && x.DisabledAt == null && repositories.Contains(x.RepositoryId))
                    .Select(x => x.RepositoryId).Distinct().ToListAsync(ct);
                if (repositories.Any(id => !allowed.Contains(id)))
                    throw new UnauthorizedAccessException("Every assigned aggregate reviewer needs explicit board and team repository access to the complete scoped candidate.");
            }
        }
    }

    internal async Task ValidateCurrentScopeAsync(WorkDeliveryPlan plan, CancellationToken ct)
    {
        foreach (var scope in Scopes(plan))
        {
            var board = await db.WorkBoards.AsNoTracking().SingleAsync(x => x.Id == scope.BoardId && x.OrganizationId == plan.OrganizationId, ct);
            if (board.WorkstreamId != plan.WorkstreamId || board.ArchivedAt.HasValue) throw new InvalidOperationException("A participating board is no longer in this active project.");
            if (scope.ItemId is { } id)
            {
                var item = await db.CoreWorkTasks.AsNoTracking().SingleAsync(x => x.Id == id && x.OrganizationId == plan.OrganizationId, ct);
                var children = await db.CoreWorkTasks.AsNoTracking().Where(x => x.ParentWorkTaskId == id && x.ArchivedAt == null).Select(x => x.Id).ToListAsync(ct);
                if (item.ArchivedAt.HasValue || item.PlanningRevision != scope.PlanningRevision ||
                    !scope.ChildIds.Order().SequenceEqual(children.Order())) throw new InvalidOperationException("The approved aggregate scope changed; pause and revise the plan.");
                var childItems = await db.CoreWorkTasks.AsNoTracking().Where(x => scope.ChildIds.Contains(x.Id)).ToListAsync(ct);
                if (childItems.Any(x => !scope.ChildPlanningRevisions.TryGetValue(x.Id, out var revision) || revision != x.PlanningRevision ||
                    x.Status == WorkTaskStatus.Cancelled)) throw new InvalidOperationException("Required child planning changed or was cancelled; amend the approved delivery scope.");
                if (scope.Scope == WorkExecutionScopes.Story)
                    foreach (var task in childItems) await ValidateTaskDeliveryAsync(plan, scope, task, ct);
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
            if (installation?.PackageVersion?.ManifestJson is not { } manifest || !ProvidesV2(manifest) ||
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
        var qa = assignments.SingleOrDefault(x => x.StageKey == "quality")
            ?? throw new InvalidOperationException("Every task needs independent QA; QA cannot be skipped.");
        var reviews = new List<WorkItemStageAssignment> { qa };
        if (delivery.DeliveryKind == "Code")
        {
            reviews.Add(assignments.SingleOrDefault(x => x.StageKey == "technical-review")
                ?? throw new InvalidOperationException("Code tasks need independent Technical Review."));
            if (!assignments.Any(x => x.StageKey == "task-integration" && x.PrincipalKind == WorkOrchestrationPrincipalKind.PlatformAction &&
                x.PlatformAction == HierarchicalWorkflows.TaskIntegrationAction)) throw new InvalidOperationException("Bind the trusted task integration action.");
            if (!Branches(plan).Any(x => x.Scope == WorkExecutionScopes.Story && x.ItemId == story.ItemId &&
                x.RepositoryId == delivery.RepositoryId && x.SourceBranch == delivery.BaseBranch))
                throw new UnauthorizedAccessException("Code tasks must target their explicitly bound story branch.");
        }
        else if (delivery.RepositoryId != Guid.Empty || !string.IsNullOrEmpty(delivery.BaseBranch))
            throw new ArgumentException("Artifact delivery needs no repository or Git branch.");
        foreach (var review in reviews)
        {
            if (review.OrganizationUserId == author.OrganizationUserId || review.AgentInstallationId.HasValue && review.AgentInstallationId == author.AgentInstallationId)
                throw new InvalidOperationException("Deliverable authors cannot review or QA their own work.");
            await ValidateAssignmentsAsync(plan.OrganizationId, new(WorkExecutionScopes.Story, story.ItemId, story.BoardId,
                story.PlanningRevision, [task.Id], story.Planning,
                [new("quality", review.PrincipalKind.ToString(), review.OrganizationUserId, review.AgentInstallationId)]), ct);
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
            if (scopes[index].Scope != WorkExecutionScopes.Story) continue;
            var digests = new Dictionary<Guid, string>();
            foreach (var id in scopes[index].ChildIds)
            {
                var task = await db.CoreWorkTasks.AsNoTracking().SingleAsync(x => x.Id == id, ct);
                var assignments = await db.WorkItemStageAssignments.AsNoTracking().Where(x => x.WorkItemId == id).ToListAsync(ct);
                digests.Add(id, TaskDeliveryDigest(task, assignments));
            }
            scopes[index] = scopes[index] with { ChildDeliveryDigests = digests };
        }
        plan.ScopesJson = Encode(scopes);
    }

    private async Task ProvisionBranchesAsync(WorkDeliveryPlan plan, CancellationToken ct)
    {
        foreach (var branch in Branches(plan).OrderBy(x => x.Scope == WorkExecutionScopes.Release ? 0 : x.Scope == WorkExecutionScopes.Epic ? 1 : 2))
        {
            var repository = await RepositoryAsync(plan.OrganizationId, branch.RepositoryId, ct);
            await sourceControl.DeliveryBranchAsync(Operation(plan, repository, branch, "ensure", $"delivery:{plan.Id:N}:branch:{Digest(branch)}"), ct);
        }
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
