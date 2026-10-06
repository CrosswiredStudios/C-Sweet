using System.Data;
using System.Security.Cryptography;
using System.Text;
using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkDeliveryService
{
    public async Task<WorkDeliveryPlanResponse> CompleteReviewAsync(Guid org, Guid actorId,
        CompleteWorkDeliveryReviewRequest request, CancellationToken ct = default)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var plan = await GetAuthorizedAsync(org, actorId, request.PlanId, WorkDeliveryCapabilities.Review, ct);
        var member = await MemberAsync(org, actorId, ct);
        var execution = plan.Executions.Single(x => x.Id == request.ExecutionId);
        var stage = execution.Stages.Single(x => x.Id == request.StageExecutionId);
        if (member.AgentInstallationId.HasValue || stage.PrincipalKind != WorkOrchestrationPrincipalKind.Human || stage.OrganizationUserId != member.Id)
            throw new UnauthorizedAccessException("Only this assigned human reviewer may complete the review.");
        var replay = await ReplayAsync(org, actorId, request.IdempotencyKey, "review", request, ct);
        if (replay is not null) return Response(replay);
        if (plan.Status != "Active" || execution.Status != "WaitingForHuman" || stage.Status != WorkStageExecutionStatus.WaitingForHuman ||
            execution.Revision != request.ExpectedRevision || execution.CurrentStageKey != stage.StageKey ||
            Candidate(execution)?.Digest != request.Result.CandidateDigest)
            throw new DbUpdateConcurrencyException("The review assignment or candidate changed.");
        var scope = Scopes(plan).Single(x => x.Scope == execution.Scope && x.ItemId == execution.WorkItemId);
        ValidateCriteria(scope.Planning.AcceptanceCriteria, request.Result.Approved, request.Result.Summary, request.Result.Criteria, request.Result.Findings);
        if (stage.StageKey == "quality") ValidateQualityEvidence(Candidate(execution)!, request.Result);
        if (stage.StageKey == "build-readiness") await ValidateBuildIdentitiesAsync(plan, execution, request.Result.BuildIds, true, ct);
        await ValidateCurrentScopeAsync(plan, ct); await RequireCandidateCurrentAsync(plan, execution, ct);
        stage.Status = WorkStageExecutionStatus.Completed; stage.LastOutcomeCode = request.Result.Approved ? "approved" : "changes_requested";
        stage.LastSummary = Encode(request.Result); stage.CompletedAt = clock.GetUtcNow(); stage.UpdatedAt = clock.GetUtcNow();
        RecordHumanReview(stage, request.Result);
        await RevokeDeliveryGrantsAsync(execution, stage, ct);
        if (request.Result.Approved) await AdvanceDeliveryAsync(plan, execution, scope, stage, ct);
        else { Stop(execution, request.Result.Summary); await RecordFindingsAsync(plan, execution, request.Result.Findings, ct); }
        execution.Revision++; plan.Revision++; plan.UpdatedAt = clock.GetUtcNow();
        Receipt(plan, actorId, request.IdempotencyKey, "review", request); Queue(plan, "review.completed");
        await SaveDeliveryAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
        return Response(plan);
    }

    private void RecordHumanReview(WorkStageExecution stage, WorkDeliveryReviewResult result)
    {
        var attempt = new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecutionId = stage.Id,
            Attempt = stage.Attempts.Count + 1, Status = WorkExecutionAttemptStatus.Completed,
            IdempotencyKey = $"human-delivery:{stage.Id:N}:{result.CandidateDigest}", CreatedAt = clock.GetUtcNow(), CompletedAt = clock.GetUtcNow() };
        attempt.ResultJson = Encode(new WorkExecutionOutcomeV1(stage.Id, attempt.Id, WorkExecutionDispositions.Completed,
            result.Approved ? "approved" : "changes_requested", result.Summary, System.Text.Json.JsonSerializer.SerializeToElement(result, Json),
            [new("delivery-candidate", "Reviewed complete candidate", result.CandidateDigest)], result.Findings));
        stage.Attempts.Add(attempt); db.WorkExecutionAttempts.Add(attempt);
    }

    public async Task<WorkDeliveryEvidenceResponse> ReadEvidenceAsync(Guid org, Guid actorId,
        ReadWorkDeliveryEvidenceRequest request, CancellationToken ct = default)
    {
        var plan = await Load().SingleOrDefaultAsync(x => x.Id == request.PlanId && x.OrganizationId == org, ct)
            ?? throw new KeyNotFoundException("Delivery plan not found.");
        var member = await MemberAsync(org, actorId, ct);
        var execution = plan.Executions.Single(x => x.Id == request.ExecutionId);
        var stage = execution.Stages.LastOrDefault();
        var assigned = plan.Status == "Active" && execution.ScopeRevision == plan.ScopeRevision &&
            stage?.OrganizationUserId == member.Id && stage.Status is WorkStageExecutionStatus.Pending or
                WorkStageExecutionStatus.Dispatching or WorkStageExecutionStatus.Running or WorkStageExecutionStatus.WaitingForHuman;
        if (!assigned) await AuthorizeProjectAsync(org, actorId, plan.WorkstreamId, WorkDeliveryCapabilities.Evidence, ct);
        var candidate = Candidate(execution) ?? throw new InvalidOperationException("There is no current validation candidate.");
        var taskIds = new HashSet<Guid>();
        var boardIds = new HashSet<Guid>();
        void Collect(WorkDeliveryScopeSnapshot scope)
        {
            boardIds.Add(scope.BoardId);
            foreach (var id in scope.ChildIds)
            {
                var child = Scopes(plan).SingleOrDefault(x => x.ItemId == id);
                if (child is null) taskIds.Add(id); else Collect(child);
            }
        }
        Collect(Scopes(plan).Single(x => x.Scope == execution.Scope && x.ItemId == execution.WorkItemId));
        foreach (var boardId in boardIds)
            await AuthorizeBoardAsync(org, actorId, boardId, CSweet.Contracts.WorkManagement.WorkItemActions.Read, ct);
        var children = new List<WorkDeliveryChildEvidence>();
        foreach (var task in await db.CoreWorkTasks.AsNoTracking().Where(x => taskIds.Contains(x.Id) && x.OrganizationId == org).ToListAsync(ct))
        {
            var latest = await db.WorkItemExecutions.AsNoTracking().Include(x => x.Stages).ThenInclude(x => x.Attempts)
                .Where(x => x.WorkItemId == task.Id).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            WorkExecutionOutcomeV1? Result(string key)
            {
                var json = latest?.Stages.Where(x => x.Traversal == latest.Traversal && x.StageKey == key && x.Status == WorkStageExecutionStatus.Completed)
                    .SelectMany(x => x.Attempts).Where(x => x.Status == WorkExecutionAttemptStatus.Completed && x.ResultJson != null)
                    .OrderByDescending(x => x.CreatedAt).FirstOrDefault()?.ResultJson;
                return json is null ? null : Decode<WorkExecutionOutcomeV1>(json);
            }
            children.Add(new(task.Id, task.PlanningRevision, Decode<WorkItemDeliverySpecification>(task.DeliverySpecificationJson!).DeliveryKind,
                Result("technical-review"), Result("quality"), task.MergeCommitSha));
        }
        var documents = new List<WorkDeliveryDocumentContent>();
        foreach (var entry in candidate.Documents)
        {
            if (!(await authorization.AuthorizeAsync(org, member.AgentInstallationId.HasValue ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser,
                member.AgentInstallationId ?? member.Id, "artifact.read", GrantScopeKind.Artifact, entry.ArtifactId, ct)).Allowed)
                throw new UnauthorizedAccessException("Explicit artifact read access is required for the exact candidate document.");
            var revision = await db.ArtifactRevisions.AsNoTracking().SingleAsync(x => x.Id == entry.RevisionId && x.ArtifactId == entry.ArtifactId && x.OrganizationId == org, ct);
            if (revision.ContentSha256 != entry.Sha256) throw new InvalidOperationException("The document candidate identity changed.");
            documents.Add(new(entry.ArtifactId, entry.RevisionId, entry.Sha256, revision.Content));
        }
        var buildIds = candidate.Repositories.Where(x => x.BuildId.HasValue).Select(x => x.BuildId!.Value).ToArray();
        var builds = await db.DeliveryBuilds.AsNoTracking().Where(x => x.OrganizationId == org && x.WorkstreamId == plan.WorkstreamId && buildIds.Contains(x.Id)).ToListAsync(ct);
        var buildEvidence = builds.Select(x => new DeliveryBuildV2(x.Id, x.WorkstreamId, x.TeamId, x.ToolchainDefinitionId,
            x.ProviderInstallationId, x.RepositoryId, x.SourceRevision, x.RecipeKey, x.TargetKey,
            Decode<System.Text.Json.JsonElement>(x.ConfigurationJson), x.DefinitionDigest, x.Status, x.Attempt, x.MaximumAttempts,
            x.ClaimId, x.ExecutionNodeId, x.LeaseExpiresAt, Decode<BuildOutputManifestEntry[]>(x.OutputsJson),
            Decode<BuildExecutionProvenance>(x.ProvenanceJson), x.FailureCode, x.FailureSummary, x.Revision, x.CreatedAt, x.UpdatedAt)).ToArray();
        if (request.RepositoryId is not { } repositoryId) return new(candidate, documents) { Builds = buildEvidence, Children = children };
        var repositoryCandidate = candidate.Repositories.Single(x => x.RepositoryId == repositoryId);
        var repository = await RepositoryAsync(org, repositoryId, ct);
        var teams = await db.TeamMemberships.Where(x => x.OrganizationId == org && x.OrganizationUserId == member.Id && x.EndedAt == null)
            .Select(x => x.TeamId).ToListAsync(ct);
        if (!await db.TeamRepositoryPolicies.AnyAsync(x => x.OrganizationId == org && x.RepositoryId == repositoryId && teams.Contains(x.TeamId) && x.DisabledAt == null, ct))
            throw new UnauthorizedAccessException("The reviewer requires explicit team access to every requested repository.");
        var branch = repositoryCandidate.SourceBranch;
        if (branch != repositoryCandidate.TargetBranch && repositoryCandidate.CandidateCommitSha != repositoryCandidate.SourceCommitSha)
        {
            var key = $"candidate:{execution.Id:N}:{repositoryCandidate.SourceCommitSha}:{repositoryCandidate.TargetCommitSha}";
            branch = "codex/candidate/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        }
        var snapshot = repository.Connection!.Provider == SourceControlProvider.InternalGit
            ? await sourceControl.PrepareInternalWorkspaceAsync(new(org, repositoryId, execution.Id, repositoryCandidate.TargetBranch,
                branch, repositoryCandidate.CandidateCommitSha, $"delivery-evidence:{execution.Id:N}:{candidate.Digest}"), ct)
            : await sourceControl.PrepareWorkspaceAsync(new(repository.Connection.SourceAccessInstallationId!.Value, long.Parse(repository.ExternalRepositoryId, System.Globalization.CultureInfo.InvariantCulture),
                repository.Owner, repository.Name, repositoryCandidate.TargetBranch, execution.Id, branch, repositoryCandidate.CandidateCommitSha,
                $"delivery-evidence:{execution.Id:N}:{candidate.Digest}"), ct);
        if (snapshot.BaseCommitSha != repositoryCandidate.CandidateCommitSha) throw new InvalidOperationException("The provider returned a different candidate revision.");
        return new(candidate, documents, snapshot.Archive, snapshot.ArtifactSha256, snapshot.BaseCommitSha) { Builds = buildEvidence, Children = children };
    }
}
