using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Security;
using CSweet.Application.SourceControl;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.SourceControl;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Setup;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkDeliveryService(CSweetDbContext db, IScopedActionAuthorizationService authorization,
    ITrustedSourceControlHostClient sourceControl, TimeProvider clock, AgentWorkInbox? inbox = null,
    IAgentRuntimeManager? runtimes = null) : IWorkDeliveryService
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static T Decode<T>(string json) => JsonSerializer.Deserialize<T>(json, Json)!;
    internal static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    internal static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Encode(value)))).ToLowerInvariant();

    public async Task<IReadOnlyList<WorkDeliveryPlanResponse>> ReadAsync(Guid org, Guid actorId, ReadWorkDeliveryPlansRequest request, CancellationToken ct = default)
    {
        await AuthorizeProjectAsync(org, actorId, request.WorkstreamId, WorkDeliveryCapabilities.Read, ct);
        var plans = await Load().Where(x => x.OrganizationId == org && x.WorkstreamId == request.WorkstreamId &&
            (!request.PlanId.HasValue || x.Id == request.PlanId) && (!request.AfterId.HasValue || x.Id.CompareTo(request.AfterId.Value) > 0))
            .OrderBy(x => x.Id).Take(Math.Clamp(request.Limit, 1, 100)).ToListAsync(ct);
        var result = new List<WorkDeliveryPlanResponse>();
        foreach (var plan in plans)
        {
            await AuthorizeBoardsAsync(org, actorId, plan, WorkItemActions.Read, ct);
            result.Add(Response(plan));
        }
        return result;
    }

    public async Task<WorkDeliveryPlanResponse> ConfigureAsync(Guid org, Guid actorId, ConfigureWorkDeliveryPlanRequest request, CancellationToken ct = default)
    {
        await AuthorizeProjectAsync(org, actorId, request.WorkstreamId, WorkDeliveryCapabilities.Configure, ct);
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var replay = await ReplayAsync(org, actorId, request.IdempotencyKey, "configure", request, ct);
        if (replay is not null) return Response(replay);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 160 || request.EpicItemIds.Count is < 1 or > 100 ||
            request.EpicItemIds.Distinct().Count() != request.EpicItemIds.Count) throw new ArgumentException("Provide a release name and unique epic scope (maximum 100 epics).");
        var project = await db.Workstreams.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.WorkstreamId && x.OrganizationId == org &&
            (x.Status == WorkstreamStatus.Active || x.Status == WorkstreamStatus.Approved), ct)
            ?? throw new InvalidOperationException("An approved, active project is required.");
        var manager = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ManagerOrganizationUserId &&
            x.OrganizationId == org && x.IsActive && x.ArchivedAt == null, ct) ?? throw new InvalidOperationException("The release manager must be an active project participant.");
        if (!await db.ProjectParticipants.AnyAsync(x => x.WorkstreamId == project.Id && x.OrganizationId == org &&
            x.OrganizationUserId == manager.Id && x.RemovedAt == null, ct)) throw new UnauthorizedAccessException("Assign the release manager to the project first.");
        if (manager.AgentInstallationId is { } managerInstallation)
        {
            var manifest = await db.AgentInstallations.Where(x => x.Id == managerInstallation && x.BusinessId == org.ToString() &&
                x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active).Select(x => x.PackageVersion!.ManifestJson).SingleOrDefaultAsync(ct);
            if (manifest is null || !ProvidesV2(manifest)) throw new InvalidOperationException("An agent release manager needs an active V2 execution installation.");
        }
        var plan = request.PlanId.HasValue ? await Load().SingleAsync(x => x.Id == request.PlanId && x.OrganizationId == org, ct) : new WorkDeliveryPlan
        { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = request.WorkstreamId, CreatedAt = clock.GetUtcNow() };
        var configurator = await MemberAsync(org, actorId, ct);
        var managerApproved = configurator.Id == project.AccountableManagerOrganizationUserId || configurator.Id == manager.Id;
        if (!managerApproved)
        {
            // Architects may configure topology within their explicit grants, but
            // cannot approve new scope or replace its accountable manager.
            if (!request.PlanId.HasValue || plan.ManagerOrganizationUserId != manager.Id || plan.Name != request.Name ||
                !Decode<Guid[]>(plan.EpicItemIdsJson).Order().SequenceEqual(request.EpicItemIds.Order()) ||
                Scopes(plan).Any(s => !request.Assignments.Any(a => a.Scope == s.Scope && a.ItemId == s.ItemId &&
                    a.BoardId == s.BoardId && Encode(a.Stages) == Encode(s.Stages))) ||
                request.ReleasePlanning is not null && Encode(request.ReleasePlanning) != Encode(Scopes(plan).Single(s => s.Scope == WorkExecutionScopes.Release).Planning))
                throw new UnauthorizedAccessException("Only the assigned project or release manager may approve scope, staffing or manager ownership. Architects may amend existing topology within their grants.");
        }
        if (plan.WorkstreamId != request.WorkstreamId || plan.Status is "Completed" or "Cancelled" ||
            request.PlanId.HasValue && plan.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("The release changed or is terminal.");
        if (plan.Status is "Active" or "Promoting") throw new InvalidOperationException("Pause the delivery plan before changing its approved scope.");
        if (plan.Executions.Any(x => x.Scope == WorkExecutionScopes.Release && x.Promotions.Any(p => p.Status == "Completed")))
            throw new InvalidOperationException("Scope with completed default-branch promotions is immutable; create a follow-up release for additional work.");
        var items = await db.CoreWorkTasks.AsNoTracking().Include(x => x.Board).Where(x => x.OrganizationId == org &&
            x.Board != null && x.Board.WorkstreamId == project.Id && x.ArchivedAt == null).ToListAsync(ct);
        var epics = request.EpicItemIds.Select(id => items.SingleOrDefault(x => x.Id == id && x.Kind == WorkItemKind.Epic && !x.IsExecutable)
            ?? throw new ArgumentException("Every release member must be a container epic on this project.")).ToList();
        var scopes = new List<WorkDeliveryScopeSnapshot>();
        foreach (var epic in epics)
        {
            var stories = items.Where(x => x.ParentWorkTaskId == epic.Id).ToList();
            if (stories.Count == 0 || stories.Any(x => x.Kind != WorkItemKind.Story || x.IsExecutable || x.BoardId != epic.BoardId))
                throw new ArgumentException("Epics require board-local container stories.");
            foreach (var story in stories)
            {
                var tasks = items.Where(x => x.ParentWorkTaskId == story.Id).ToList();
                if (tasks.Count == 0 || tasks.Any(x => !x.IsExecutable || x.BoardId != story.BoardId || x.Kind is WorkItemKind.Epic or WorkItemKind.Story))
                    throw new ArgumentException("Stories require board-local executable tasks.");
                scopes.Add(Snapshot(story, WorkExecutionScopes.Story, tasks.Select(x => x.Id).ToArray()));
            }
            scopes.Add(Snapshot(epic, WorkExecutionScopes.Epic, stories.Select(x => x.Id).ToArray()));
        }
        var releaseAssignment = request.Assignments.SingleOrDefault(x => x.Scope == WorkExecutionScopes.Release && x.ItemId is null)
            ?? throw new ArgumentException("Assign release QA and technical review.");
        var releasePlanning = request.ReleasePlanning ?? new WorkItemPlanningSpecification(
            scopes.Where(x => x.Scope == WorkExecutionScopes.Epic).SelectMany(x => x.Planning.Requirements).Distinct().ToArray(),
            scopes.Where(x => x.Scope == WorkExecutionScopes.Epic).SelectMany(x => x.Planning.AcceptanceCriteria).Distinct().ToArray());
        scopes.Add(new(WorkExecutionScopes.Release, null, releaseAssignment.BoardId, 1, epics.Select(x => x.Id).ToArray(), releasePlanning, releaseAssignment.Stages));
        if (!managerApproved && (scopes.Count != Scopes(plan).Count || scopes.Any(scope =>
            Scopes(plan).SingleOrDefault(old => old.Scope == scope.Scope && old.ItemId == scope.ItemId) is not { } previous ||
            previous.PlanningRevision != scope.PlanningRevision || Encode(previous.Planning) != Encode(scope.Planning) ||
            !previous.ChildIds.Order().SequenceEqual(scope.ChildIds.Order()) ||
            !previous.ChildPlanningRevisions.OrderBy(x => x.Key).SequenceEqual(scope.ChildPlanningRevisions.OrderBy(x => x.Key)))))
            throw new UnauthorizedAccessException("An architect cannot approve changed child membership or acceptance criteria; the assigned manager must amend scope.");
        foreach (var scope in scopes)
        {
            await AuthorizeBoardAsync(org, actorId, scope.BoardId, WorkBoardActions.Configure, ct);
            await ValidateAssignmentsAsync(org, scope, ct);
        }
        await ValidateBranchesAsync(org, scopes, request.Branches, ct);
        if (request.PlanId.HasValue) await RevokeTaskArtifactGrantsAsync(plan, ct);
        plan.Name = request.Name.Trim(); plan.ManagerOrganizationUserId = manager.Id;
        plan.EpicItemIdsJson = Encode(request.EpicItemIds); plan.ScopesJson = Encode(scopes); plan.BranchesJson = Encode(request.Branches);
        if (request.PlanId.HasValue)
        {
            plan.ScopeRevision++; plan.Revision++;
            foreach (var execution in plan.Executions)
            {
                foreach (var stage in execution.Stages)
                {
                    foreach (var attempt in stage.Attempts.Where(x => x.Status is WorkExecutionAttemptStatus.Pending or WorkExecutionAttemptStatus.Running))
                    {
                        if (attempt.AgentWorkItemId is { } workId && inbox is not null)
                            await inbox.CancelAsync(workId, "Delivery scope superseded", ct);
                        attempt.Status = WorkExecutionAttemptStatus.Cancelled;
                    }
                    if (stage.Status != WorkStageExecutionStatus.Completed) stage.Status = WorkStageExecutionStatus.Cancelled;
                    await RevokeDeliveryGrantsAsync(execution, stage, ct);
                }
                execution.Status = "Superseded";
            }
        }
        else db.WorkDeliveryPlans.Add(plan);
        plan.Status = "Draft"; plan.UpdatedAt = clock.GetUtcNow();
        Receipt(plan, actorId, request.IdempotencyKey, "configure", request); Queue(plan, "scope.changed");
        await SaveDeliveryAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
        return Response(plan);

        WorkDeliveryScopeSnapshot Snapshot(WorkTask item, string scope, Guid[] children)
        {
            var planning = Decode<WorkItemPlanningSpecification>(item.PlanningSpecificationJson ?? "null")
                ?? throw new ArgumentException("Every story and epic needs accepted planning and acceptance criteria.");
            if (planning.AcceptanceCriteria.Count == 0) throw new ArgumentException("Every aggregate requires acceptance criteria.");
            var assignment = request.Assignments.SingleOrDefault(x => x.Scope == scope && x.ItemId == item.Id && x.BoardId == item.BoardId)
                ?? throw new ArgumentException("Assign every aggregate's review stages.");
            return new(scope, item.Id, item.BoardId!.Value, item.PlanningRevision, children, planning, assignment.Stages)
                { ChildPlanningRevisions = children.ToDictionary(id => id, id => items.Single(x => x.Id == id).PlanningRevision) };
        }
    }

    public async Task<WorkDeliveryPlanResponse> ControlAsync(Guid org, Guid actorId, ControlWorkDeliveryPlanRequest request, CancellationToken ct = default)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var plan = await GetAuthorizedAsync(org, actorId, request.PlanId, WorkDeliveryCapabilities.Control, ct);
        var actor = await MemberAsync(org, actorId, ct);
        if (actor.Id != plan.ManagerOrganizationUserId) throw new UnauthorizedAccessException("Only the assigned release manager controls the plan.");
        var replay = await ReplayAsync(org, actorId, request.IdempotencyKey, "control", request, ct);
        if (replay is not null) return Response(replay);
        if (plan.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("The plan changed.");
        switch (request.Action)
        {
            case "activate" when plan.Status == "Draft":
                await ValidateCurrentScopeAsync(plan, ct);
                await PinTaskDeliveryAsync(plan, ct);
                var epicIds = Decode<Guid[]>(plan.EpicItemIdsJson);
                var otherPlans = await db.WorkDeliveryPlans.AsNoTracking().Where(x => x.OrganizationId == org && x.Id != plan.Id &&
                    (x.Status == "Active" || x.Status == "Paused")).Select(x => x.EpicItemIdsJson).ToListAsync(ct);
                if (otherPlans.Any(x => Decode<Guid[]>(x).Intersect(epicIds).Any()))
                    throw new InvalidOperationException("An epic already belongs to an activated delivery plan.");
                await ProvisionBranchesAsync(plan, ct);
                foreach (var scope in Scopes(plan))
                {
                    var execution = new WorkDeliveryExecution { Id = Guid.NewGuid(), PlanId = plan.Id, Scope = scope.Scope, WorkItemId = scope.ItemId, BoardId = scope.BoardId,
                        ScopeRevision = plan.ScopeRevision, CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
                    plan.Executions.Add(execution); db.WorkDeliveryExecutions.Add(execution);
                }
                plan.Status = "Active"; break;
            case "pause" when plan.Status is "Active" or "Promoting": plan.Status = "Paused"; break;
            case "resume" when plan.Status == "Paused": await ValidateCurrentScopeAsync(plan, ct); plan.Status = "Active"; break;
            case "cancel" when plan.Status is not ("Completed" or "Cancelled"):
                plan.Status = "Cancelled";
                await RevokeTaskArtifactGrantsAsync(plan, ct);
                foreach (var execution in plan.Executions.Where(x => x.Status != "Completed"))
                {
                    execution.Status = "Cancelled";
                    foreach (var stage in execution.Stages.Where(x => x.Status != WorkStageExecutionStatus.Completed))
                    {
                        stage.Status = WorkStageExecutionStatus.Cancelled;
                        foreach (var attempt in stage.Attempts.Where(x => x.Status is WorkExecutionAttemptStatus.Pending or WorkExecutionAttemptStatus.Running))
                        {
                            if (attempt.AgentWorkItemId is { } workId && inbox is not null) await inbox.CancelAsync(workId, "Delivery plan cancelled", ct);
                            attempt.Status = WorkExecutionAttemptStatus.Cancelled;
                        }
                        await RevokeDeliveryGrantsAsync(execution, stage, ct);
                    }
                }
                break;
            default: throw new InvalidOperationException("The delivery control is not valid in the current state.");
        }
        plan.Revision++; plan.UpdatedAt = clock.GetUtcNow();
        Receipt(plan, actorId, request.IdempotencyKey, "control", request); Queue(plan, request.Action);
        await SaveDeliveryAsync(ct); if (tx is not null) await tx.CommitAsync(ct); return Response(plan);
    }

    public async Task<WorkDeliveryPlanResponse> AcceptAsync(Guid org, Guid actorId, DecideWorkDeliveryAcceptanceRequest request, CancellationToken ct = default)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var plan = await GetAuthorizedAsync(org, actorId, request.PlanId, WorkDeliveryCapabilities.Accept, ct);
        var member = await MemberAsync(org, actorId, ct);
        var execution = plan.Executions.Single(x => x.Id == request.ExecutionId);
        var scope = Scopes(plan).Single(x => x.Scope == execution.Scope && x.ItemId == execution.WorkItemId);
        var boardManager = await db.WorkBoards.Where(x => x.Id == execution.BoardId).Select(x => x.ManagerOrganizationUserId).SingleAsync(ct);
        if (member.Id != (execution.Scope == WorkExecutionScopes.Release ? plan.ManagerOrganizationUserId : boardManager))
            throw new UnauthorizedAccessException("Only the assigned aggregate manager can accept this delivery.");
        var replay = await ReplayAsync(org, actorId, request.IdempotencyKey, "accept", request, ct);
        if (replay is not null) return Response(replay);
        if (plan.Status != "Active" || execution.Status != "WaitingForApproval" || execution.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException("The current aggregate is not waiting for this acceptance.");
        if (Candidate(execution)?.Digest != request.CandidateDigest) throw new InvalidOperationException("Acceptance must bind the current candidate.");
        ValidateCriteria(scope.Planning.AcceptanceCriteria, request.Approved, request.Summary, request.Criteria, request.Findings);
        await ValidateCurrentScopeAsync(plan, ct); await RequireCandidateCurrentAsync(plan, execution, ct);
        execution.AcceptanceJson = Encode(request); execution.Status = request.Approved ? "Promoting" : "Blocked";
        execution.BlockedReason = request.Approved ? null : request.Summary;
        var stage = execution.Stages.Last(x => x.StageKey == "manager-review");
        stage.Status = WorkStageExecutionStatus.Completed; stage.LastOutcomeCode = request.Approved ? "approved" : "rejected";
        stage.LastSummary = request.Summary; stage.CompletedAt = clock.GetUtcNow();
        RecordHumanReview(stage, new(request.CandidateDigest, request.Approved, request.Summary, request.Criteria, request.Findings));
        await RevokeDeliveryGrantsAsync(execution, stage, ct);
        if (!request.Approved) await RecordFindingsAsync(plan, execution, request.Findings, ct);
        execution.Revision++; plan.Revision++; plan.UpdatedAt = clock.GetUtcNow();
        Receipt(plan, actorId, request.IdempotencyKey, "accept", request); Queue(plan, "acceptance.decided");
        await SaveDeliveryAsync(ct); if (tx is not null) await tx.CommitAsync(ct); return Response(plan);
    }

    public async Task<WorkDeliveryPlanResponse> RecoverAsync(Guid org, Guid actorId, RecoverWorkDeliveryRequest request, CancellationToken ct = default)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var plan = await GetAuthorizedAsync(org, actorId, request.PlanId, WorkDeliveryCapabilities.Recover, ct);
        var member = await MemberAsync(org, actorId, ct);
        if (member.Id != plan.ManagerOrganizationUserId) throw new UnauthorizedAccessException("Only the release manager may recover delivery.");
        var replay = await ReplayAsync(org, actorId, request.IdempotencyKey, "recover", request, ct);
        if (replay is not null) return Response(replay);
        var execution = plan.Executions.Single(x => x.Id == request.ExecutionId);
        if (plan.Status != "Active" || execution.Revision != request.ExpectedRevision || execution.Status is not ("Blocked" or "PartiallyPromoted"))
            throw new DbUpdateConcurrencyException("Recovery requires the current stopped execution.");
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ArgumentException("Explain the recovery action.");
        await ValidateCurrentScopeAsync(plan, ct);
        foreach (var resolution in request.Resolutions)
        {
            var finding = plan.Findings.SingleOrDefault(x => x.Id == resolution.FindingId && x.Status == "Open")
                ?? throw new InvalidOperationException("Resolve a current open finding on this delivery plan.");
            if (resolution.RemediationTaskIds.Count == 0 || resolution.RemediationTaskIds.Distinct().Count() != resolution.RemediationTaskIds.Count ||
                string.IsNullOrWhiteSpace(resolution.Evidence)) throw new ArgumentException("Link the completed, manager-authorized remediation tasks and their evidence.");
            var requiredTasks = Scopes(plan).Where(x => x.Scope == WorkExecutionScopes.Story).SelectMany(x => x.ChildIds).ToHashSet();
            foreach (var id in resolution.RemediationTaskIds)
            {
                if (!requiredTasks.Contains(id) || !await db.CoreWorkTasks.AnyAsync(x => x.Id == id && x.IsExecutable && x.OrganizationId == org && x.Status == WorkTaskStatus.Completed, ct) ||
                    !await db.WorkItemExecutions.AnyAsync(x => x.WorkItemId == id && x.Status == WorkItemExecutionStatus.Completed &&
                        x.Stages.Any(s => s.Traversal == x.Traversal && s.StageKey == "quality" && s.LastOutcomeCode == "passed" &&
                            s.Status == WorkStageExecutionStatus.Completed && s.CompletedAt >= finding.CreatedAt), ct))
                    throw new InvalidOperationException("Every remediation task must belong to the approved scope and have completed independent QA.");
            }
            finding.RemediationTaskIdsJson = Encode(resolution.RemediationTaskIds); finding.ResolutionEvidence = resolution.Evidence;
            finding.Status = "Resolved"; finding.ResolvedByOrganizationUserId = member.Id; finding.ResolvedAt = clock.GetUtcNow();
        }
        if (plan.Findings.Any(x => x.Status == "Open")) throw new InvalidOperationException("Resolve all linked remediation findings before recovering aggregate validation.");
        try
        {
            await RequireCandidateCurrentAsync(plan, execution, ct);
            var last = execution.Stages.LastOrDefault();
            if (last is null || last.LastOutcomeCode is "rejected" or "changes_requested" ||
                last.Status is WorkStageExecutionStatus.Failed or WorkStageExecutionStatus.Blocked or WorkStageExecutionStatus.Cancelled)
                ResetValidation();
            else execution.Status = execution.AcceptanceJson is null ? "Ready" : "Promoting";
        }
        catch (InvalidOperationException)
        {
            ResetValidation();
        }
        execution.BlockedReason = null; execution.Revision++; plan.Revision++;
        Receipt(plan, actorId, request.IdempotencyKey, "recover", request); Queue(plan, "recovery.requested");
        await SaveDeliveryAsync(ct); if (tx is not null) await tx.CommitAsync(ct); return Response(plan);

        void ResetValidation()
        {
            // Preserve successful repository receipts while requiring fresh regression
            // and acceptance of the full set before resuming unfinished promotions.
            execution.AcceptanceJson = null; execution.Status = "WaitingForChildren";
            foreach (var stage in execution.Stages.Where(x => x.Status != WorkStageExecutionStatus.Completed))
                stage.Status = WorkStageExecutionStatus.Cancelled;
        }
    }

    internal IQueryable<WorkDeliveryPlan> Load() => db.WorkDeliveryPlans.Include(x => x.Findings).Include(x => x.Executions).ThenInclude(x => x.Stages).ThenInclude(x => x.Attempts)
        .Include(x => x.Executions).ThenInclude(x => x.Promotions);
    internal static IReadOnlyList<WorkDeliveryScopeSnapshot> Scopes(WorkDeliveryPlan plan) => Decode<WorkDeliveryScopeSnapshot[]>(plan.ScopesJson);
    internal static WorkDeliveryCandidate? Candidate(WorkDeliveryExecution execution) => execution.CandidateJson is null ? null : Decode<WorkDeliveryCandidate>(execution.CandidateJson);
    internal static IReadOnlyList<WorkDeliveryBranchBinding> Branches(WorkDeliveryPlan plan) => Decode<WorkDeliveryBranchBinding[]>(plan.BranchesJson);
    internal static WorkDeliveryPlanResponse Response(WorkDeliveryPlan plan) => new(plan.Id, plan.WorkstreamId, plan.Name, plan.ManagerOrganizationUserId,
        plan.Status, plan.Revision, plan.ScopeRevision, Decode<Guid[]>(plan.EpicItemIdsJson), Branches(plan), Scopes(plan),
        plan.Executions.OrderBy(x => x.CreatedAt).Select(x => new WorkDeliveryExecutionResponse(x.Id, x.Scope, x.WorkItemId, x.BoardId,
            x.Status, x.CurrentStageKey, x.Revision, Candidate(x), x.BlockedReason,
            x.Stages.OrderBy(s => s.CreatedAt).Select(s => new WorkStageExecutionResponse(s.Id, s.StageKey, s.StageType.ToString(), s.Traversal,
                s.Status.ToString(), s.PrincipalKind.ToString(), s.OrganizationUserId, s.AgentInstallationId, s.PlatformAction, s.Attempts.Count,
                s.LastOutcomeCode, s.LastSummary, s.LastError, s.RetryAt, s.UpdatedAt)
                { LatestOutcome = s.Attempts.Where(a => a.Status == WorkExecutionAttemptStatus.Completed && a.ResultJson != null)
                    .OrderByDescending(a => a.Attempt).Select(a => Decode<WorkExecutionOutcomeV1>(a.ResultJson!)).FirstOrDefault() }).ToArray(),
            x.Promotions.Select(p => new WorkDeliveryPromotionReceipt(p.RepositoryId, p.SourceCommitSha, p.TargetCommitSha, p.Status, p.MergeCommitSha, p.Error)).ToArray())).ToArray(),
        plan.CreatedAt, plan.UpdatedAt)
        { Findings = plan.Findings.Select(x => new WorkDeliveryFindingResponse(x.Id, x.ExecutionId, x.CandidateDigest, x.Summary,
            x.Status, Decode<Guid[]>(x.RemediationTaskIdsJson), x.ResolutionEvidence)).ToArray() };

    private async Task<WorkDeliveryPlan> GetAuthorizedAsync(Guid org, Guid actor, Guid id, string action, CancellationToken ct)
    {
        var plan = await Load().SingleOrDefaultAsync(x => x.OrganizationId == org && x.Id == id, ct) ?? throw new KeyNotFoundException("Release not found.");
        await AuthorizeProjectAsync(org, actor, plan.WorkstreamId, action, ct);
        await AuthorizeBoardsAsync(org, actor, plan, WorkItemActions.Read, ct); return plan;
    }
    private async Task<OrganizationUser> MemberAsync(Guid org, Guid actor, CancellationToken ct) =>
        await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.OrganizationId == org && (x.Id == actor || x.ApplicationUserId == actor || x.AgentInstallationId == actor) &&
            x.IsActive && x.ArchivedAt == null, ct) ?? throw new UnauthorizedAccessException("An active organization member is required.");
    private async Task AuthorizeProjectAsync(Guid org, Guid actor, Guid project, string action, CancellationToken ct)
    {
        var member = await MemberAsync(org, actor, ct);
        if (!(await authorization.AuthorizeAsync(org, member.AgentInstallationId.HasValue ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser,
            member.AgentInstallationId ?? member.Id, action, GrantScopeKind.Workstream, project, ct)).Allowed)
            throw new UnauthorizedAccessException("The project delivery grant is required: " + action);
    }
    private async Task AuthorizeBoardAsync(Guid org, Guid actor, Guid board, string action, CancellationToken ct)
    {
        var member = await MemberAsync(org, actor, ct);
        if (!(await authorization.AuthorizeAsync(org, member.AgentInstallationId.HasValue ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser,
            member.AgentInstallationId ?? member.Id, action, GrantScopeKind.Board, board, ct)).Allowed) throw new UnauthorizedAccessException("Explicit participating-board access is required.");
    }
    private async Task AuthorizeBoardsAsync(Guid org, Guid actor, WorkDeliveryPlan plan, string action, CancellationToken ct)
    { foreach (var board in Scopes(plan).Select(x => x.BoardId).Distinct()) await AuthorizeBoardAsync(org, actor, board, action, ct); }
    private async Task<WorkDeliveryPlan?> ReplayAsync<T>(Guid org, Guid actor, string key, string operation, T request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200) throw new ArgumentException("A bounded idempotency key is required.");
        var receipt = await db.WorkDeliveryMutationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org && x.IdempotencyKey == key, ct);
        if (receipt is null) return null;
        if (receipt.ActorId != actor || receipt.Operation != operation || receipt.RequestDigest != Digest(request)) throw new InvalidOperationException("The idempotency key was reused for another request.");
        return await Load().SingleAsync(x => x.Id == receipt.PlanId && x.OrganizationId == org, ct);
    }
    private void Receipt<T>(WorkDeliveryPlan plan, Guid actor, string key, string operation, T request) => db.WorkDeliveryMutationReceipts.Add(new()
    { Id = Guid.NewGuid(), OrganizationId = plan.OrganizationId, PlanId = plan.Id, ActorId = actor, Operation = operation, IdempotencyKey = key,
        RequestDigest = Digest(request), CreatedAt = clock.GetUtcNow() });
    internal void Queue(WorkDeliveryPlan plan, string change)
    {
        var now = clock.GetUtcNow();
        var wake = new GenericResourceEvent(Guid.NewGuid(), now, new(plan.OrganizationId, plan.WorkstreamId, null, null, null, null, null, plan.Id, null, null),
            "WorkDeliveryPlan", plan.Id, plan.Revision, "work-delivery-plan", change,
            JsonSerializer.SerializeToElement(new { planId = plan.Id, plan.ScopeRevision, plan.Status }, Json));
        db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = plan.OrganizationId, EventType = WorkDeliveryCapabilities.Changed,
            DataJson = Encode(wake), IdempotencyKey = $"delivery:{plan.Id:N}:{plan.Revision}:{change}", OccurredAt = now, NextAttemptAt = now });
        db.ApplicationRealtimeOutbox.Add(new()
        {
            Id = Guid.NewGuid(), OrganizationId = plan.OrganizationId,
            RecipientOrganizationUserIdsJson = Encode(Scopes(plan).SelectMany(x => x.Stages).Where(x => x.OrganizationUserId.HasValue)
                .Select(x => x.OrganizationUserId!.Value).Append(plan.ManagerOrganizationUserId).Distinct().ToArray()),
            EventType = CSweet.Contracts.Realtime.AppRealtimeEvents.WorkDeliveryChanged,
            Subject = $"organizations/{plan.OrganizationId:D}/projects/{plan.WorkstreamId:D}/delivery",
            DataJson = Encode(new { projectId = plan.WorkstreamId, planId = plan.Id, revision = plan.Revision, change }),
            Status = CSweet.Domain.Notifications.ApplicationRealtimeOutboxStatus.Pending, OccurredAt = now, NextAttemptAt = now
        });
        db.QueueAudit(new("work.delivery." + change, "Work", OrganizationId: plan.OrganizationId,
            EntityType: nameof(WorkDeliveryPlan), EntityId: plan.Id, Summary: plan.Name + ": " + change,
            MetadataJson: Encode(new { plan.Revision, plan.ScopeRevision, plan.Status }), OccurredAt: now,
            Employees: [new(plan.ManagerOrganizationUserId, "Accountable")]));
    }
    internal static void ValidateCriteria(IReadOnlyList<string> criteria, bool approved, string summary,
        IReadOnlyList<WorkDeliveryCriterionResult> results, IReadOnlyList<string> findings)
    {
        if (string.IsNullOrWhiteSpace(summary) || results.Count != criteria.Count ||
            !criteria.Order(StringComparer.Ordinal).SequenceEqual(results.Select(x => x.Criterion).Order(StringComparer.Ordinal)) ||
            results.Any(x => string.IsNullOrWhiteSpace(x.Evidence)) || findings.Any(string.IsNullOrWhiteSpace) ||
            approved && (results.Any(x => !x.Satisfied) || findings.Count > 0) || !approved && findings.Count == 0)
            throw new InvalidOperationException("Address every criterion with evidence; approval requires no findings and rejection requires actionable findings.");
    }
    internal static void ValidateQualityEvidence(WorkDeliveryCandidate candidate, WorkDeliveryReviewResult result)
    {
        if (result.Validations.Any(x => string.IsNullOrWhiteSpace(x.Command) || string.IsNullOrWhiteSpace(x.Output) ||
            !candidate.Repositories.Any(r => r.RepositoryId == x.RepositoryId && r.CandidateCommitSha == x.CommitSha)) ||
            result.Approved && (candidate.Repositories.Any(r => !result.Validations.Any(x => x.RepositoryId == r.RepositoryId)) ||
                result.Validations.Any(x => !x.Succeeded || x.ExitCode != 0)))
            throw new InvalidOperationException("QA must supply actual command evidence bound to every repository's exact candidate commit.");
    }
}
