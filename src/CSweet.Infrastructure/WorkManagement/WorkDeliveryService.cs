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

    public Task<WorkDeliveryPlanResponse> ConfigureAsync(Guid org, Guid actorId, ConfigureWorkDeliveryPlanRequest request, CancellationToken ct = default)
    {
        if (request.TicketWorkflows.Count == 0)
            throw new ArgumentException("Declare workflow tickets, their dependencies and assigned steps before configuring delivery.");
        return ConfigureTicketsAsync(org, actorId, request, ct);
    }

    public async Task<WorkDeliveryPlanResponse> ControlAsync(Guid org, Guid actorId, ControlWorkDeliveryPlanRequest request, CancellationToken ct = default)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var plan = await GetAuthorizedAsync(org, actorId, request.PlanId, WorkDeliveryCapabilities.Control, ct);
        var actor = await MemberAsync(org, actorId, ct);
        var ownerAmendmentControl = actor.PermissionLevel == OrganizationPermissionLevel.Owner &&
            request.Action is "pause" or "resume" or "retry-validation";
        if (actor.Id != plan.ManagerOrganizationUserId && !ownerAmendmentControl)
            throw new UnauthorizedAccessException("Only the assigned release manager controls the plan; an authorized organization owner may pause or resume it for scope amendment.");
        var replay = await ReplayAsync(org, actorId, request.IdempotencyKey, "control", request, ct);
        if (replay is not null) return Response(replay);
        if (plan.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("The plan changed.");
        switch (request.Action)
        {
            case "activate" when plan.Status == "Draft":
                await ValidateCurrentScopeAsync(plan, ct);
                await PinTaskDeliveryAsync(plan, ct);
                var ticketIds = Scopes(plan).Select(s => s.TicketWorkflow!.TicketId).ToArray();
                var otherPlans = await db.WorkDeliveryPlans.AsNoTracking().Where(x => x.OrganizationId == org && x.Id != plan.Id &&
                    (x.Status == "Active" || x.Status == "Paused")).Select(x => x.ScopesJson).ToListAsync(ct);
                if (otherPlans.Any(x => Decode<WorkDeliveryScopeSnapshot[]>(x).Any(s => s.TicketWorkflow is { } w && ticketIds.Contains(w.TicketId))))
                    throw new InvalidOperationException("A workflow ticket already belongs to an activated plan.");
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
            case "retry-validation" when plan.Status == "Active":
                await ValidateCurrentScopeAsync(plan,ct);
                var stopped=plan.Executions.Where(e=>e.ScopeRevision==plan.ScopeRevision&&e.Status=="Blocked"&&e.AcceptanceJson==null).ToArray();
                if(stopped.Length==0)throw new InvalidOperationException("There is no failed validation to retry.");
                foreach(var execution in stopped) {
                    await RequireCandidateCurrentAsync(plan,execution,ct);
                    foreach(var finding in plan.Findings.Where(f=>f.ExecutionId==execution.Id&&f.Status=="Open")) {
                        finding.Status="Superseded";finding.ResolvedAt=clock.GetUtcNow();finding.ResolvedByOrganizationUserId=actor.Id;
                        finding.ResolutionEvidence="Validation retried by the release manager or owner. The prior failure remains recorded; no criterion is accepted. "+request.Reason;
                    }
                    foreach(var stage in execution.Stages.Where(s=>s.Status!=WorkStageExecutionStatus.Completed)) {
                        stage.Status=WorkStageExecutionStatus.Cancelled;await RevokeDeliveryGrantsAsync(execution,stage,ct);
                    }
                    var retryScope=Scopes(plan).Single(s=>s.Scope==execution.Scope&&s.ItemId==execution.WorkItemId);
                    await CreateReviewAsync(plan,execution,retryScope,execution.CurrentStageKey,ct);
                    execution.BlockedReason=null;execution.Revision++;
                }
                break;
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
        foreach (var execution in plan.Executions.Where(e=>e.ScopeRevision==plan.ScopeRevision)) await SynchronizeTicketAsync(plan,execution,ct);
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
        if (member.Id != CurrentDeliveryStage(execution).OrganizationUserId)
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
        var stage = CurrentDeliveryStage(execution);
        if (Handler(scope,stage.StageKey) is not ("manager-review" or "acceptance")) throw new InvalidOperationException("The current step is not declared acceptance.");
        stage.Status = WorkStageExecutionStatus.Completed; stage.LastOutcomeCode = request.Approved ? "approved" : "rejected";
        stage.LastSummary = request.Summary; stage.CompletedAt = clock.GetUtcNow();
        RecordHumanReview(stage, new(request.CandidateDigest, request.Approved, request.Summary, request.Criteria, request.Findings));
        await RevokeDeliveryGrantsAsync(execution, stage, ct);
        if (!request.Approved) await RecordFindingsAsync(plan, execution, request.Findings, ct);
        else await AdvanceTicketAsync(plan, execution, scope, stage, ct);
        execution.Revision++; plan.Revision++; plan.UpdatedAt = clock.GetUtcNow();
        await SynchronizeTicketAsync(plan,execution,ct);
        Receipt(plan, actorId, request.IdempotencyKey, "accept", request); Queue(plan, "acceptance.decided");
        await SaveDeliveryAsync(ct); if (tx is not null) await tx.CommitAsync(ct); return Response(plan);
    }

    public async Task<WorkDeliveryPlanResponse> RecoverAsync(Guid org, Guid actorId, RecoverWorkDeliveryRequest request, CancellationToken ct = default)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var plan = await GetAuthorizedAsync(org, actorId, request.PlanId, WorkDeliveryCapabilities.Recover, ct);
        var member = await MemberAsync(org, actorId, ct);
        var execution = plan.Executions.Single(x => x.Id == request.ExecutionId);
        if (member.Id != plan.ManagerOrganizationUserId && member.PermissionLevel != OrganizationPermissionLevel.Owner)
            throw new UnauthorizedAccessException("Only the release manager or an authorized owner may request review-processing recovery.");
        var replay = await ReplayAsync(org, actorId, request.IdempotencyKey, "recover", request, ct);
        if (replay is not null) return Response(replay);
        var legacyComputeCorrection = member.PermissionLevel == OrganizationPermissionLevel.Owner && request.Resolutions.Count == 0 &&
            (await IsLegacyComputeCursorFailureAsync(plan, execution, ct) || await IsLegacyMissingDependencyFailureAsync(plan, execution, ct) ||
             await IsInterruptedManagedRestoreAsync(plan, execution, ct) || await IsTruncatedComputeValidationAsync(plan, execution, ct) ||
             await IsFailedProvisionCleanupAsync(plan, execution, ct));
        var ownerProcessingRecovery = member.PermissionLevel == OrganizationPermissionLevel.Owner && request.Resolutions.Count == 0 &&
            execution.CurrentStageKey is "technical-review" or "manager-review" &&
            (execution.BlockedReason?.StartsWith("agent-failure:v1;code=agent.invalid_operation;", StringComparison.Ordinal) == true ||
             execution.BlockedReason == "The prior runtime lease expired.") &&
            !plan.Findings.Any(x => x.Status == "Open");
        if (member.Id != plan.ManagerOrganizationUserId && !ownerProcessingRecovery && !legacyComputeCorrection)
            throw new UnauthorizedAccessException("Only the release manager may recover delivery; an authorized owner may restart a failed review processor without resolving findings or granting acceptance.");
        if (plan.Status != "Active" || execution.Revision != request.ExpectedRevision || execution.Status is not ("Blocked" or "PartiallyPromoted"))
            throw new DbUpdateConcurrencyException("Recovery requires the current stopped execution.");
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ArgumentException("Explain the recovery action.");
        await ValidateCurrentScopeAsync(plan, ct);
        if (legacyComputeCorrection)
        {
            await RequireCandidateCurrentAsync(plan, execution, ct);
            foreach (var finding in plan.Findings.Where(f => f.Status == "Open"))
            {
                finding.Status = "Superseded";
                finding.ResolutionEvidence = "Owner-authorized C-Sweet compute setup or transport processing correction. The exact prior operation and failure remain recorded. " +
                    "This historical processing failure does not establish a game defect or pass any criterion. Fresh build readiness, QA, technical review and manager acceptance remain mandatory. " + request.Reason;
                finding.ResolvedByOrganizationUserId = member.Id; finding.ResolvedAt = clock.GetUtcNow();
            }
        }
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
            var last = execution.Stages.Count == 0 ? null : CurrentDeliveryStage(execution);
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
            x.Promotions.Select(p => new WorkDeliveryPromotionReceipt(p.RepositoryId, p.SourceCommitSha, p.TargetCommitSha, p.Status, p.MergeCommitSha, p.Error)).ToArray())
            { WorkflowTicketId = Scopes(plan).SingleOrDefault(s => s.Scope == x.Scope && s.ItemId == x.WorkItemId)?.TicketWorkflow?.TicketId }).ToArray(),
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
