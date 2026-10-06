using System.Text.Json;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkDeliveryService
{
    public async Task PulseAsync(CancellationToken ct = default)
    {
        var ids = await db.WorkDeliveryPlans.AsNoTracking().Where(x => x.Status == "Active").OrderBy(x => x.CreatedAt).Select(x => x.Id).Take(100).ToListAsync(ct);
        foreach (var id in ids)
        {
            await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct) : null;
            var plan = await Load().SingleAsync(x => x.Id == id, ct);
            if (plan.Status != "Active") continue;
            try
            {
                await ValidateCurrentScopeAsync(plan, ct);
                foreach (var execution in plan.Executions.Where(x => x.ScopeRevision == plan.ScopeRevision &&
                    x.Status is not ("Completed" or "Cancelled" or "Superseded" or "Blocked" or "PartiallyPromoted")).OrderBy(x =>
                        x.Scope == WorkExecutionScopes.Story ? 0 : x.Scope == WorkExecutionScopes.Epic ? 1 : 2).ToList())
                {
                    try { await ReconcileDeliveryAsync(plan, execution, ct); }
                    catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or JsonException or HttpRequestException)
                    { Stop(execution, error.Message); }
                }
                if (plan.Executions.Any(x => x.Scope == WorkExecutionScopes.Release && x.ScopeRevision == plan.ScopeRevision && x.Status == "Completed"))
                    plan.Status = "Completed";
            }
            catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException)
            {
                foreach (var execution in plan.Executions.Where(x => x.Status != "Completed")) Stop(execution, error.Message);
            }
            foreach (var stopped in plan.Executions.Where(x => x.Status is "Blocked" or "PartiallyPromoted"))
                foreach (var stage in stopped.Stages)
                {
                    foreach (var attempt in stage.Attempts.Where(x => x.Status is WorkExecutionAttemptStatus.Pending or WorkExecutionAttemptStatus.Running))
                    {
                        if (attempt.AgentWorkItemId is { } workId && inbox is not null)
                            await inbox.CancelAsync(workId, "Delivery validation stopped; inspect current evidence before recovery", ct);
                        attempt.Status = WorkExecutionAttemptStatus.Failed; attempt.CompletedAt = clock.GetUtcNow();
                    }
                    if (stage.Status is WorkStageExecutionStatus.Dispatching or WorkStageExecutionStatus.Running or WorkStageExecutionStatus.Pending)
                        stage.Status = WorkStageExecutionStatus.Blocked;
                    await RevokeDeliveryGrantsAsync(stopped, stage, ct);
                }
            db.ChangeTracker.DetectChanges();
            if (db.ChangeTracker.Entries().Any(x => x.State is EntityState.Added or EntityState.Modified))
            { plan.Revision++; plan.UpdatedAt = clock.GetUtcNow(); Queue(plan, "execution.changed"); }
            await SaveDeliveryAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
        }
    }

    private async Task ReconcileDeliveryAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution, CancellationToken ct)
    {
        var scope = Scopes(plan).Single(x => x.Scope == execution.Scope && x.ItemId == execution.WorkItemId);
        if (execution.CandidateJson is not null && execution.Status != "WaitingForChildren")
            await RequireCandidateCurrentAsync(plan, execution, ct);
        if (execution.Status == "WaitingForEvidence")
        {
            await RequireCandidateCurrentAsync(plan, execution, ct);
            var pending = Decode<WorkExecutionOutcomeV1>(execution.Stages.Last().Attempts.Last().ResultJson!).Output.Deserialize<WorkDeliveryBuildPending>(Json)!;
            var builds = await db.DeliveryBuilds.AsNoTracking().Where(x => pending.BuildIds.Contains(x.Id) && x.OrganizationId == plan.OrganizationId).ToListAsync(ct);
            if (builds.Count != pending.BuildIds.Count) throw new InvalidOperationException("The pending candidate build is unavailable.");
            if (builds.Any(x => x.Status is "Queued" or "Claimed" or "Running")) return;
            if (builds.Any(x => x.Status != "Succeeded")) throw new InvalidOperationException("A candidate build failed; inspect its certified-provider evidence and authorize remediation.");
            await CreateReviewAsync(plan, execution, scope, "build-readiness", ct);
        }
        if (execution.Status == "WaitingForChildren")
        {
            if (plan.Findings.Any(x => x.Status == "Open"))
                throw new InvalidOperationException("Linked remediation findings remain open. The manager must authorize and complete remediation before fresh aggregate validation.");
            if (!await ChildrenCompleteAsync(plan, scope, ct)) return;
            execution.CandidateJson = Encode(await PrepareCandidateAsync(plan, execution, ct));
            var initial = scope.Scope == WorkExecutionScopes.Epic && !Branches(plan).Any(x => x.Scope == scope.Scope && x.ItemId == scope.ItemId)
                ? "technical-review" : scope.Stages.Any(x => x.StageKey == "build-readiness") ? "build-readiness" : "quality";
            await CreateReviewAsync(plan, execution, scope, initial, ct);
        }
        if (execution.Status == "Ready")
        {
            var current = execution.Stages.Last();
            if (current.Status == WorkStageExecutionStatus.Completed)
            {
                if (current.LastOutcomeCode != "approved") throw new InvalidOperationException("A failed review cannot authorize promotion; obtain fresh validation.");
                await AdvanceDeliveryAsync(plan, execution, scope, current, ct);
            }
            else await DispatchDeliveryAsync(plan, execution, scope, current, ct);
        }
        if (execution.Status == "Running" && inbox is not null)
        {
            var stage = execution.Stages.Last(); var attempt = stage.Attempts.Last();
            if (attempt.AgentWorkItemId is not { } workId) throw new InvalidOperationException("The delivery attempt has no inbox work.");
            var state = await inbox.ReadStateAsync(workId, ct);
            if (state.Status is AgentWorkStatus.Pending or AgentWorkStatus.Leased && attempt.CreatedAt.AddHours(1) <= clock.GetUtcNow())
            { Stop(execution, "The delivery review deadline expired; restore reviewer availability and request recovery."); return; }
            if (state.Status is AgentWorkStatus.Pending or AgentWorkStatus.Leased) return;
            if (state.Status != AgentWorkStatus.Completed || state.Completion?.Succeeded != true || state.Completion.Value is not { } value)
            { Stop(execution, state.Error ?? state.Completion?.Error ?? "The delivery reviewer did not complete."); attempt.Status = WorkExecutionAttemptStatus.Failed; return; }
            var outcome = value.Deserialize<WorkExecutionOutcomeV1>(Json) ?? throw new InvalidOperationException("The delivery result is missing.");
            if (outcome.StageExecutionId != stage.Id || outcome.AttemptId != attempt.Id || outcome.Disposition != WorkExecutionDispositions.Completed)
                throw new InvalidOperationException(outcome.Summary.Length > 0 ? outcome.Summary : "The result does not match its authoritative attempt.");
            if (stage.StageKey == "build-readiness" && outcome.OutcomeCode == "awaiting-build")
            {
                var pending = outcome.Output.Deserialize<WorkDeliveryBuildPending>(Json)!;
                if (pending.CandidateDigest != Candidate(execution)!.Digest || pending.BuildIds.Count == 0 ||
                    pending.BuildIds.Distinct().Count() != pending.BuildIds.Count) throw new InvalidOperationException("Pending builds must identify the complete current candidate.");
                await ValidateBuildIdentitiesAsync(plan, execution, pending.BuildIds, false, ct);
                attempt.Status = WorkExecutionAttemptStatus.Completed; attempt.ResultJson = Encode(outcome); attempt.CompletedAt = clock.GetUtcNow();
                stage.Status = WorkStageExecutionStatus.Completed; stage.LastOutcomeCode = "awaiting-build";
                execution.Status = "WaitingForEvidence"; execution.Revision++;
                return;
            }
            var review = outcome.Output.Deserialize<WorkDeliveryReviewResult>(Json) ?? throw new InvalidOperationException("The review needs criterion evidence.");
            if (review.CandidateDigest != Candidate(execution)!.Digest ||
                outcome.OutcomeCode != (review.Approved ? "approved" : "changes_requested"))
                throw new InvalidOperationException("The review result does not bind the current candidate and outcome.");
            ValidateCriteria(scope.Planning.AcceptanceCriteria, review.Approved, review.Summary, review.Criteria, review.Findings);
            if (stage.StageKey == "quality") ValidateQualityEvidence(Candidate(execution)!, review);
            if (stage.StageKey == "build-readiness") await ValidateBuildIdentitiesAsync(plan, execution, review.BuildIds, true, ct);
            await RequireCandidateCurrentAsync(plan, execution, ct);
            attempt.Status = WorkExecutionAttemptStatus.Completed; attempt.ResultJson = Encode(outcome); attempt.CompletedAt = clock.GetUtcNow();
            stage.Status = WorkStageExecutionStatus.Completed; stage.CompletedAt = clock.GetUtcNow();
            stage.LastOutcomeCode = outcome.OutcomeCode; stage.LastSummary = outcome.Summary;
            await RevokeDeliveryGrantsAsync(execution, stage, ct);
            if (!review.Approved)
            {
                Stop(execution, review.Summary);
                await RecordFindingsAsync(plan, execution, review.Findings, ct);
            }
            else await AdvanceDeliveryAsync(plan, execution, scope, stage, ct);
        }
        if (execution.Status == "Promoting") await PromoteDeliveryAsync(plan, execution, ct);
    }

    private async Task<bool> ChildrenCompleteAsync(WorkDeliveryPlan plan, WorkDeliveryScopeSnapshot scope, CancellationToken ct)
    {
        if (scope.Scope == WorkExecutionScopes.Story)
        {
            var tasks = await db.CoreWorkTasks.AsNoTracking().Where(x => scope.ChildIds.Contains(x.Id) && x.OrganizationId == plan.OrganizationId && x.ArchivedAt == null).ToListAsync(ct);
            if (tasks.Count != scope.ChildIds.Count || tasks.Any(x => x.Status != WorkTaskStatus.Completed)) return false;
            // Manual completion cannot substitute for authoritative independent task QA.
            foreach (var task in tasks)
            {
                var latest = await db.WorkItemExecutions.AsNoTracking().Include(x => x.Stages).ThenInclude(x => x.Attempts)
                    .Where(x => x.WorkItemId == task.Id).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
                if (latest?.Status != WorkItemExecutionStatus.Completed || !latest.Stages.Any(x => x.Traversal == latest.Traversal &&
                    x.StageKey == "quality" && x.Status == WorkStageExecutionStatus.Completed && x.LastOutcomeCode == "passed")) return false;
            }
            return true;
        }
        var childScope = scope.Scope == WorkExecutionScopes.Epic ? WorkExecutionScopes.Story : WorkExecutionScopes.Epic;
        return scope.ChildIds.All(id => plan.Executions.Any(x => x.ScopeRevision == plan.ScopeRevision && x.Scope == childScope && x.WorkItemId == id && x.Status == "Completed"));
    }

    private async Task<WorkDeliveryCandidate> PrepareCandidateAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution, CancellationToken ct)
    {
        var repositoryCandidates = new List<WorkDeliveryRepositoryCandidate>();
        var selected = Branches(plan).Where(x => x.Scope == execution.Scope && x.ItemId == execution.WorkItemId).ToList();
        // Shared-release epics accept the integrated release revision; they perform no separate Git promotion.
        if (execution.Scope == WorkExecutionScopes.Epic && selected.Count == 0)
        {
            var stories = Scopes(plan).Single(x => x.ItemId == execution.WorkItemId && x.Scope == WorkExecutionScopes.Epic).ChildIds;
            selected = Branches(plan).Where(x => x.Scope == WorkExecutionScopes.Story && stories.Contains(x.ItemId!.Value))
                .Select(x => x with { SourceBranch = x.TargetBranch, Scope = execution.Scope, ItemId = execution.WorkItemId })
                .DistinctBy(x => x.RepositoryId).ToList();
        }
        foreach (var binding in selected.OrderBy(x => x.RepositoryId))
        {
            var completed = execution.Promotions.SingleOrDefault(x => x.RepositoryId == binding.RepositoryId && x.Status == "Completed");
            if (completed is not null)
            {
                var prior = Candidate(execution)?.Repositories.Single(x => x.RepositoryId == binding.RepositoryId)
                    ?? throw new InvalidOperationException("A completed promotion is missing its candidate evidence.");
                repositoryCandidates.Add(prior);
                continue;
            }
            var repository = await RepositoryAsync(plan.OrganizationId, binding.RepositoryId, ct);
            var refs = await sourceControl.DeliveryBranchAsync(Operation(plan, repository, binding, "inspect", "inspect:" + execution.Id.ToString("N")), ct);
            var candidate = binding.SourceBranch == binding.TargetBranch ? refs.SourceCommitSha :
                (await sourceControl.DeliveryBranchAsync(Operation(plan, repository, binding, "candidate",
                    $"candidate:{execution.Id:N}:{refs.SourceCommitSha}:{refs.TargetCommitSha}", refs.SourceCommitSha, refs.TargetCommitSha), ct)).CandidateCommitSha
                ?? throw new InvalidOperationException("The provider could not prepare an integrated validation candidate.");
            repositoryCandidates.Add(new(binding.RepositoryId, binding.SourceBranch, binding.TargetBranch, refs.SourceCommitSha, refs.TargetCommitSha, candidate));
        }
        var descendants = new HashSet<Guid>();
        void Collect(WorkDeliveryScopeSnapshot scope)
        {
            foreach (var id in scope.ChildIds)
            {
                var child = Scopes(plan).SingleOrDefault(x => x.ItemId == id);
                if (child is null) descendants.Add(id); else Collect(child);
            }
        }
        Collect(Scopes(plan).Single(x => x.Scope == execution.Scope && x.ItemId == execution.WorkItemId));
        var documents = new List<WorkDeliveryDocumentCandidate>();
        foreach (var task in await db.CoreWorkTasks.AsNoTracking().Where(x => descendants.Contains(x.Id)).ToListAsync(ct))
        {
            var stages = await db.WorkItemExecutions.AsNoTracking().Include(x => x.Stages).ThenInclude(x => x.Attempts)
                .Where(x => x.WorkItemId == task.Id).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            var worker = stages?.Stages.Where(x => x.Traversal == stages.Traversal && x.StageKey is "specialist-execution" or "development")
                .OrderByDescending(x => x.CreatedAt).FirstOrDefault()?.Attempts.OrderByDescending(x => x.Attempt).FirstOrDefault();
            if (worker?.Status != WorkExecutionAttemptStatus.Completed || worker.ResultJson is null) continue;
            var outcome = Decode<WorkExecutionOutcomeV1>(worker.ResultJson);
            if (!TryDocument(outcome.Output, out var artifact, out var revision, out var digest)) continue;
            if (!await db.ArtifactRevisions.AnyAsync(x => x.Id == revision && x.ArtifactId == artifact && x.OrganizationId == plan.OrganizationId && x.ContentSha256 == digest, ct))
                throw new InvalidOperationException("A delivered document does not match its exact revision.");
            documents.Add(new(task.Id, artifact, revision, digest));
        }
        var unsigned = new WorkDeliveryCandidate("", plan.ScopeRevision, repositoryCandidates, documents.OrderBy(x => x.ItemId).ToArray());
        return unsigned with { Digest = Digest(unsigned) };
    }

    internal static bool TryDocument(JsonElement output, out Guid artifact, out Guid revision, out string digest)
    {
        artifact = revision = Guid.Empty; digest = "";
        if (output.ValueKind != JsonValueKind.Object) return false;
        JsonElement Field(string name) => output.EnumerateObject().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
        return Field("artifactId").ValueKind == JsonValueKind.String && Field("artifactId").TryGetGuid(out artifact) &&
            Field("revisionId").ValueKind == JsonValueKind.String && Field("revisionId").TryGetGuid(out revision) &&
            Field("sha256").ValueKind == JsonValueKind.String && (digest = Field("sha256").GetString()!) is { Length: 64 };
    }

    private async Task CreateReviewAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution, WorkDeliveryScopeSnapshot scope, string key, CancellationToken ct)
    {
        WorkStageAssignment assignment;
        if (key == "manager-review")
        {
            var managerId = scope.Scope == WorkExecutionScopes.Release ? plan.ManagerOrganizationUserId :
                await db.WorkBoards.Where(x => x.Id == scope.BoardId).Select(x => x.ManagerOrganizationUserId).SingleAsync(ct)
                ?? throw new InvalidOperationException("The epic has no assigned board manager.");
            var manager = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.Id == managerId && x.OrganizationId == plan.OrganizationId && x.IsActive, ct);
            assignment = new(key, manager.AgentInstallationId.HasValue ? "AgentInstallation" : "Human", manager.Id, manager.AgentInstallationId);
        }
        else assignment = scope.Stages.Single(x => x.StageKey == key);
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), DeliveryExecutionId = execution.Id, StageKey = key,
            Traversal = execution.Stages.Count(x => x.StageKey == key), StageType = assignment.AgentInstallationId.HasValue ? WorkOrchestrationStageType.AgentExecution : WorkOrchestrationStageType.ManualWork,
            PrincipalKind = assignment.AgentInstallationId.HasValue ? WorkOrchestrationPrincipalKind.AgentInstallation : WorkOrchestrationPrincipalKind.Human,
            OrganizationUserId = assignment.OrganizationUserId, AgentInstallationId = assignment.AgentInstallationId,
            Status = assignment.AgentInstallationId.HasValue ? WorkStageExecutionStatus.Pending : WorkStageExecutionStatus.WaitingForHuman,
            CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
        execution.Stages.Add(stage); db.WorkStageExecutions.Add(stage); execution.CurrentStageKey = key;
        foreach (var document in Candidate(execution)!.Documents.DistinctBy(x => x.ArtifactId))
            db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = plan.OrganizationId,
                SubjectKind = assignment.AgentInstallationId.HasValue ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser,
                SubjectId = assignment.AgentInstallationId ?? assignment.OrganizationUserId!.Value, Action = "artifact.read",
                ScopeKind = GrantScopeKind.Artifact, ScopeId = document.ArtifactId,
                GrantedBySubjectKind = GrantSubjectKind.AutomationIdentity, GrantedBySubjectId = execution.Id,
                GrantedAt = clock.GetUtcNow(), ExpiresAt = assignment.AgentInstallationId.HasValue ? clock.GetUtcNow().AddHours(1) : null });
        execution.Status = assignment.AgentInstallationId.HasValue ? "Ready" : key == "manager-review" ? "WaitingForApproval" : "WaitingForHuman";
        execution.UpdatedAt = clock.GetUtcNow(); execution.Revision++;
        await SetAggregateCardAsync(execution, key == "manager-review" ? "Producer Review" : key == "quality" ? "QA" : "Technical Review", WorkTaskStatus.WaitingForApproval, ct);
    }

    private async Task DispatchDeliveryAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution, WorkDeliveryScopeSnapshot scope, WorkStageExecution stage, CancellationToken ct)
    {
        if (inbox is null || stage.AgentInstallationId is not { } installation) return;
        await RequireCandidateCurrentAsync(plan, execution, ct);
        if (stage.Attempts.Count >= 3) { Stop(execution, "The bounded review retry budget is exhausted."); return; }
        // Aggregate work shares the installation's inbox lease capacity with sprint work.
        if (await db.AgentWorkItems.AnyAsync(x => x.AgentInstallationId == installation && (x.Status == AgentWorkStatus.Pending || x.Status == AgentWorkStatus.Leased), ct)) return;
        var policy = await db.WorkOrchestrationPolicies.AsNoTracking().Where(x => x.BoardId == execution.BoardId)
            .SelectMany(x => x.Revisions.Where(r => r.Id == x.PublishedRevisionId)).SingleOrDefaultAsync(ct);
        var active = new[] { WorkStageExecutionStatus.Dispatching, WorkStageExecutionStatus.Running };
        if (await db.WorkStageExecutions.CountAsync(x => active.Contains(x.Status), ct) >= (policy?.GlobalConcurrencyLimit ?? 100) ||
            await db.WorkStageExecutions.CountAsync(x => active.Contains(x.Status) &&
                (x.ItemExecution!.SprintExecution!.OrganizationId == plan.OrganizationId || x.DeliveryExecution!.Plan!.OrganizationId == plan.OrganizationId), ct) >= (policy?.OrganizationConcurrencyLimit ?? 25) ||
            await db.WorkStageExecutions.CountAsync(x => active.Contains(x.Status) &&
                (x.ItemExecution!.SprintExecution!.BoardId == execution.BoardId || x.DeliveryExecution!.BoardId == execution.BoardId), ct) >= (policy?.BoardConcurrencyLimit ?? 10) ||
            await db.WorkStageExecutions.CountAsync(x => active.Contains(x.Status) && x.StageKey == stage.StageKey &&
                (x.ItemExecution!.SprintExecution!.BoardId == execution.BoardId || x.DeliveryExecution!.BoardId == execution.BoardId), ct) >= (policy?.DefaultStageConcurrencyLimit ?? 5)) return;
        await AuthorizeBoardAsync(plan.OrganizationId, installation, execution.BoardId, CSweet.Contracts.WorkManagement.WorkItemActions.Read, ct);
        var attempt = new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecutionId = stage.Id, Attempt = stage.Attempts.Count + 1,
            Status = WorkExecutionAttemptStatus.Pending, CreatedAt = clock.GetUtcNow(), IdempotencyKey = $"delivery:{stage.Id:N}:{stage.Attempts.Count + 1}" };
        var assignment = new WorkExecutionAssignmentV2(execution.Id, stage.Id, attempt.Id, plan.OrganizationId, plan.WorkstreamId,
            execution.BoardId, execution.WorkItemId, scope.Scope, plan.Id, plan.ScopeRevision, null, null, execution.Revision,
            execution.WorkItemId?.ToString("D") ?? plan.Name, null, stage.StageKey, stage.Traversal, attempt.Attempt,
            clock.GetUtcNow().AddHours(1), stage.StageKey == "quality" ? "Run full testing and regression against the exact candidate; validate every acceptance criterion including documents." :
                stage.StageKey == "manager-review" ? "Accept the delivered epic or release against every criterion using the completed technical and QA evidence." :
                "Verify integration and technical readiness against every criterion using the exact candidate and completed task reviews.",
            JsonSerializer.SerializeToElement(scope, Json), JsonSerializer.SerializeToElement(new WorkExecutionInputV1(plan.WorkstreamId, null, scope.PlanningRevision, scope.Planning)
                { AllowedOutcomeCodes = ["approved", "changes_requested"] }, Json),
            execution.Stages.SelectMany(x => x.Attempts).Where(x => x.Status == WorkExecutionAttemptStatus.Completed && x.ResultJson != null)
                .Select(x => Decode<WorkExecutionOutcomeV1>(x.ResultJson!))
                .Where(x => x.Output.ValueKind == JsonValueKind.Object && x.Output.TryGetProperty("candidateDigest", out var digest) &&
                    digest.GetString() == Candidate(execution)!.Digest).ToArray(), [], Candidate(execution))
            { OrganizationUserId = stage.OrganizationUserId!.Value, AgentInstallationId = installation,
              PlanningRevision = scope.PlanningRevision, PermittedOutcomes = stage.StageKey == "build-readiness"
                  ? ["approved", "changes_requested", "awaiting-build"] : ["approved", "changes_requested"] };
        // Persist the canonical stage before admission checks read it. The outer
        // transaction commits the stage, inbox work and event outbox together.
        await SaveDeliveryAsync(ct);
        var work = await inbox.EnqueueAsync(plan.OrganizationId.ToString(), installation, AgentWorkKind.Capability, WorkManagementCapabilityNames.ExecutionRunV2,
            JsonSerializer.SerializeToElement(assignment, Json), attempt.IdempotencyKey, assignment.Deadline, plan.Id.ToString("N"), stage.Id.ToString("N"),
            "WorkDeliveryStage", stage.Id.ToString("D"), 1, ct);
        attempt.AgentWorkItemId = work.Id; stage.Attempts.Add(attempt); db.WorkExecutionAttempts.Add(attempt); stage.Status = WorkStageExecutionStatus.Dispatching;
        execution.Status = "Running"; execution.Revision++;
        if (runtimes is not null) await runtimes.EnsureRuntimeQueuedAsync(installation, "Delivery review " + plan.Name, cancellationToken: ct);
    }

    private async Task AdvanceDeliveryAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution, WorkDeliveryScopeSnapshot scope, WorkStageExecution completed, CancellationToken ct)
    {
        if (completed.StageKey == "build-readiness")
        {
            var result = Decode<WorkExecutionOutcomeV1>(completed.Attempts.Last().ResultJson!).Output.Deserialize<WorkDeliveryReviewResult>(Json)!;
            var builds = await db.DeliveryBuilds.AsNoTracking().Where(x => result.BuildIds.Contains(x.Id) && x.OrganizationId == plan.OrganizationId).ToListAsync(ct);
            var candidate = Candidate(execution)! with { Repositories = Candidate(execution)!.Repositories
                .Select(r => r with { BuildId = builds.Single(b => b.RepositoryId == r.RepositoryId && b.SourceRevision == r.CandidateCommitSha).Id }).ToArray(), Digest = "" };
            execution.CandidateJson = Encode(candidate with { Digest = Digest(candidate) });
            await CreateReviewAsync(plan, execution, scope, "quality", ct);
        }
        else if (completed.StageKey == "quality")
        {
            if (scope.Scope == WorkExecutionScopes.Story) execution.Status = "Promoting";
            else await CreateReviewAsync(plan, execution, scope, "technical-review", ct);
        }
        else if (completed.StageKey == "technical-review") await CreateReviewAsync(plan, execution, scope, "manager-review", ct);
        else if (completed.StageKey == "manager-review")
        { execution.AcceptanceJson = completed.Attempts.LastOrDefault()?.ResultJson ?? completed.LastSummary; execution.Status = "Promoting"; }
        execution.Revision++; execution.UpdatedAt = clock.GetUtcNow();
    }

    private async Task PromoteDeliveryAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution, CancellationToken ct)
    {
        await RequireCandidateCurrentAsync(plan, execution, ct);
        foreach (var candidate in Candidate(execution)!.Repositories.Where(x => x.SourceBranch != x.TargetBranch))
        {
            var receipt = execution.Promotions.SingleOrDefault(x => x.RepositoryId == candidate.RepositoryId);
            if (receipt?.Status == "Completed") continue;
            receipt ??= new WorkDeliveryPromotion { Id = Guid.NewGuid(), ExecutionId = execution.Id, RepositoryId = candidate.RepositoryId,
                SourceCommitSha = candidate.SourceCommitSha, TargetCommitSha = candidate.TargetCommitSha };
            if (!execution.Promotions.Contains(receipt)) { execution.Promotions.Add(receipt); db.WorkDeliveryPromotions.Add(receipt); }
            receipt.SourceCommitSha = candidate.SourceCommitSha; receipt.TargetCommitSha = candidate.TargetCommitSha;
            var repository = await RepositoryAsync(plan.OrganizationId, candidate.RepositoryId, ct);
            var binding = new WorkDeliveryBranchBinding(repository.Id, execution.Scope, execution.WorkItemId, candidate.SourceBranch, candidate.TargetBranch);
            var result = await sourceControl.DeliveryBranchAsync(Operation(plan, repository, binding, "promote",
                $"promote:{execution.Id:N}:{repository.Id:N}:{Candidate(execution)!.Digest}", candidate.SourceCommitSha, candidate.TargetCommitSha, candidate.CandidateCommitSha), ct);
            receipt.Status = result.Promoted ? "Completed" : "Failed"; receipt.MergeCommitSha = result.Promoted ? result.CandidateCommitSha : null;
            receipt.Error = result.Error; receipt.Revision++;
            if (!result.Promoted)
            { Stop(execution, result.Error ?? "The provider did not confirm promotion."); return; }
        }
        execution.Status = "Completed"; execution.BlockedReason = null; execution.Revision++; execution.UpdatedAt = clock.GetUtcNow();
        await SetAggregateCardAsync(execution, "Done", WorkTaskStatus.Completed, ct);
    }

    private async Task SetAggregateCardAsync(WorkDeliveryExecution execution, string name, WorkTaskStatus status, CancellationToken ct)
    {
        if (execution.WorkItemId is not { } id) return;
        var item = await db.CoreWorkTasks.SingleAsync(x => x.Id == id, ct);
        var columns = await db.WorkBoardColumns.Where(x => x.BoardId == execution.BoardId).ToListAsync(ct);
        var column = columns.FirstOrDefault(x => x.Name == name || name == "Producer Review" && x.Name == "Manager Review") ??
            (status == WorkTaskStatus.Completed ? columns.FirstOrDefault(x => x.Category == WorkBoardColumnCategory.Done) : null);
        item.BoardColumnId = column?.Id ?? item.BoardColumnId; item.Status = status; item.Revision++; item.UpdatedAt = clock.GetUtcNow();
    }
    private void Stop(WorkDeliveryExecution execution, string reason)
    {
        execution.Status = execution.Promotions.Any(x => x.Status == "Completed") ? "PartiallyPromoted" : "Blocked";
        execution.BlockedReason = reason.Length > 4096 ? reason[..4096] : reason; execution.Revision++; execution.UpdatedAt = clock.GetUtcNow();
    }
    private async Task RevokeDeliveryGrantsAsync(WorkDeliveryExecution execution, WorkStageExecution stage, CancellationToken ct)
    {
        var subject = stage.AgentInstallationId ?? stage.OrganizationUserId;
        var grants = await db.ScopedActionGrants.Where(x => x.GrantedBySubjectKind == GrantSubjectKind.AutomationIdentity && x.GrantedBySubjectId == execution.Id && x.SubjectId == subject && x.RevokedAt == null).ToListAsync(ct);
        foreach (var grant in grants) { grant.RevokedAt = clock.GetUtcNow(); grant.Revision++; }
    }
    private async Task RecordFindingsAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution, IReadOnlyList<string> findings, CancellationToken ct)
    {
        foreach (var summary in findings.Distinct(StringComparer.Ordinal))
        {
            var digest = Digest(summary);
            if (!plan.Findings.Any(x => x.ExecutionId == execution.Id && x.CandidateDigest == Candidate(execution)!.Digest && x.FindingDigest == digest))
            {
                var finding = new WorkDeliveryFinding { Id = Guid.NewGuid(), OrganizationId = plan.OrganizationId, PlanId = plan.Id, ExecutionId = execution.Id,
                    CandidateDigest = Candidate(execution)!.Digest, FindingDigest = digest, Summary = summary, CreatedAt = clock.GetUtcNow() };
                plan.Findings.Add(finding); db.Set<WorkDeliveryFinding>().Add(finding);
            }
        }
        var itemIds = execution.WorkItemId is { } item ? new[] { item } : Scopes(plan).Single(x => x.Scope == WorkExecutionScopes.Release).ChildIds.ToArray();
        foreach (var id in itemIds)
        {
        var marker = $"delivery-finding:{execution.Id:N}:{Candidate(execution)!.Digest}";
        if (await db.WorkItemComments.AnyAsync(x => x.WorkItemId == id && x.Body.Contains(marker), ct)) continue;
        db.WorkItemComments.Add(new() { Id = Guid.NewGuid(), OrganizationId = plan.OrganizationId, WorkItemId = id,
            AuthorKind = GrantSubjectKind.AutomationIdentity, AuthorSubjectId = execution.Id, AuthorDisplayName = "Delivery orchestration",
            Kind = "delivery-remediation", ArtifactDigest = Candidate(execution)!.Digest, IdempotencyKey = marker,
            Body = marker + "\n" + string.Join("\n", findings), CreatedAt = clock.GetUtcNow() });
        }
    }
}
