using System.Text.Json;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using CSweet.Compute.Contracts;
using CSweet.Domain.Setup;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkOrchestrator
{
    internal static int RetryAttemptCount(WorkStageExecution stage) => stage.Attempts.Count(attempt =>
    {
        if (attempt.Status != WorkExecutionAttemptStatus.Completed || attempt.ResultJson is null) return true;
        try { return JsonSerializer.Deserialize<WorkExecutionOutcomeV1>(attempt.ResultJson, JsonOptions)?.OutcomeCode != "awaiting-compute"; }
        catch (JsonException) { return true; }
    });

    internal async Task<string?> AcceptComputeWaitAsync(WorkSprintExecution execution, WorkStageExecution stage,
        WorkExecutionAttempt attempt, WorkExecutionOutcomeV1 outcome, DateTimeOffset now, CancellationToken token)
    {
        if (stage.StageKey is not ("quality" or "specialist-execution") || stage.AgentInstallationId is null || outcome.Disposition != WorkExecutionDispositions.Completed ||
            outcome.StageExecutionId != stage.Id || outcome.AttemptId != attempt.Id)
            return "Only the assigned QA attempt can wait for its browser compute.";
        QaComputeWait? wait;
        try { wait = outcome.Output.Deserialize<QaComputeWait>(JsonOptions); }
        catch (JsonException) { return "QA compute wait evidence is invalid."; }
        if (wait is null || wait.SourceCommitSha is not { Length: 40 or 64 } || wait.SourceCommitSha.Any(character => !Uri.IsHexDigit(character)) ||
            wait.BuildSha256 is not { Length: 64 } || wait.BuildSha256.Any(character => !Uri.IsHexDigit(character)) ||
            wait.EnvironmentId == Guid.Empty || wait.OperationIds is null || wait.OperationIds.Count > 1 ||
            wait.RetryAt <= now || wait.RetryAt > now.AddMinutes(10))
            return "QA compute waits must bind exact source, build and bounded recovery time.";
        var item = stage.ItemExecution!.WorkItem!;
        var delivery = string.IsNullOrWhiteSpace(item.DeliverySpecificationJson) ? null :
            JsonSerializer.Deserialize<WorkItemDeliverySpecification>(item.DeliverySpecificationJson, JsonOptions);
        if (delivery?.DeliveryKind == "Artifact" && stage.StageKey == "specialist-execution")
        {
            var inputError = await ValidateArtifactComputeSourceAsync(execution, stage, attempt, wait.SourceCommitSha, token);
            if (inputError is not null) return inputError;
        }
        else if (!await db.SourceControlWorkspaces.AsNoTracking().AnyAsync(workspace => workspace.OrganizationId == execution.OrganizationId &&
            workspace.AgentInstallationId == stage.AgentInstallationId && workspace.WorkItemId == item.Id &&
            workspace.AssignmentRevision == item.AssignmentRevision && workspace.BaseCommitSha == wait.SourceCommitSha, token))
            return "The browser wait does not identify QA's authorized source snapshot.";
        var projectId = await db.WorkBoards.AsNoTracking().Where(board => board.Id == execution.BoardId && board.OrganizationId == execution.OrganizationId)
            .Select(board => board.WorkstreamId).SingleAsync(token);
        var environment = await db.ComputeEnvironments.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == wait.EnvironmentId &&
            candidate.OrganizationId == execution.OrganizationId && candidate.InstallationId == stage.AgentInstallationId &&
            candidate.WorkstreamId == projectId && candidate.TeardownConfirmedAt == null, token);
        if (environment is null) return "QA compute must belong to the assigned reviewer and project.";
        var operations = await db.ComputeOperations.AsNoTracking().Where(operation => wait.OperationIds.Contains(operation.Id) &&
            operation.EnvironmentId == environment.Id && operation.OrganizationId == execution.OrganizationId &&
            operation.InstallationId == stage.AgentInstallationId && operation.Generation == environment.Generation).ToListAsync(token);
        if (operations.Count != wait.OperationIds.Count) return "The QA compute operation is unavailable or its generation changed.";
        attempt.Status = WorkExecutionAttemptStatus.Completed; attempt.CompletedAt = now;
        attempt.ResultJson = JsonSerializer.Serialize(outcome, JsonOptions);
        await RevokeAttemptGrantsAsync(execution, stage, now, token);
        stage.Status = WorkStageExecutionStatus.Backoff; stage.LastOutcomeCode = outcome.OutcomeCode;
        stage.LastSummary = outcome.Summary; stage.LastError = null; stage.UpdatedAt = now;
        stage.RetryAt = operations.Any(operation => operation.Status is not ("Pending" or "Dispatching" or "Reconciling")) ||
            operations.Count == 0 && environment.State is CSweet.Domain.Compute.ComputeLifecycleState.Ready or CSweet.Domain.Compute.ComputeLifecycleState.Failed
                ? now : wait.RetryAt;
        stage.ItemExecution.Status = WorkItemExecutionStatus.Pending;
        execution.Revision++; execution.UpdatedAt = now;
        AddEvent(execution, stage.ItemExecutionId, stage.Id, attempt.Id, "qa.compute.waiting", new { wait.EnvironmentId, wait.OperationIds });
        return null;
    }

    private async Task<string?> ValidateArtifactComputeSourceAsync(WorkSprintExecution execution, WorkStageExecution stage,
        WorkExecutionAttempt attempt, string commit, CancellationToken ct)
    {
        var itemExecution = stage.ItemExecution!;
        var item = itemExecution.WorkItem!;
        var work = attempt.AgentWorkItemId is { } workId
            ? await db.AgentWorkItems.AsNoTracking().SingleOrDefaultAsync(w => w.Id == workId, ct) : null;
        // A wait is the result of completed work, not a new execution grant or a live-lease read.
        if (inbox is null || execution.Status != WorkSprintExecutionStatus.Active || itemExecution.CurrentStageKey != stage.StageKey ||
            itemExecution.Traversal != stage.Traversal || stage.Status is not (WorkStageExecutionStatus.Dispatching or WorkStageExecutionStatus.Running) ||
            attempt.Status is not (WorkExecutionAttemptStatus.Pending or WorkExecutionAttemptStatus.Running) ||
            work is null || work.Status != AgentWorkStatus.Completed || work.OrganizationId != execution.OrganizationId.ToString() ||
            work.AgentInstallationId != stage.AgentInstallationId || work.SourceType != "WorkStageExecution" || work.SourceId != stage.Id.ToString("D"))
            return "The browser wait has no current completed artifact input assignment.";
        try
        {
            var assignment = inbox.ReadPayload(work).Deserialize<WorkExecutionAssignment>(JsonOptions);
            if (assignment is null || assignment.ExecutionId != itemExecution.Id || assignment.StageExecutionId != stage.Id ||
                assignment.AttemptId != attempt.Id || assignment.SprintExecutionId != execution.Id || assignment.StageKey != stage.StageKey ||
                assignment.AgentInstallationId != stage.AgentInstallationId || assignment.OrganizationUserId != stage.OrganizationUserId)
                return "The browser wait belongs to a different artifact input attempt.";
            if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(member => member.Id == stage.OrganizationUserId &&
                    member.OrganizationId == execution.OrganizationId && member.IsActive && member.AgentInstallationId == stage.AgentInstallationId, ct) ||
                !(await new CSweet.Infrastructure.Security.ScopedActionAuthorizationService(db).AuthorizeAsync(execution.OrganizationId,
                    CSweet.Domain.Security.GrantSubjectKind.AgentInstallation, stage.AgentInstallationId!.Value,
                    CSweet.Contracts.WorkManagement.WorkItemActions.Read, CSweet.Domain.Security.GrantScopeKind.WorkItem, item.Id, ct)).Allowed)
                return "The browser wait's artifact author no longer has current task access.";
            var candidate = await WorkArtifactRepositoryInput.AuthorizeAssignmentAsync(db, item, assignment, ct);
            if (!candidate.Repositories.Any(r => r.CandidateCommitSha == commit))
                return "The browser wait does not identify the pinned artifact input commit.";
            return null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        { return "The browser wait's artifact input is no longer authorized: " + error.Message; }
    }

    internal sealed record QaComputeWait(string SourceCommitSha, string BuildSha256, Guid EnvironmentId,
        IReadOnlyList<Guid> OperationIds, DateTimeOffset RetryAt);

    internal async Task<string?> ValidateBrowserEvidenceAsync(WorkSprintExecution execution, WorkStageExecution stage,
        WorkExecutionOutcomeV1 outcome, string commit, CancellationToken token)
    {
        var bindings = outcome.Evidence.Where(evidence => evidence.Kind.StartsWith("browser-", StringComparison.Ordinal)).ToArray();
        if (bindings.Length == 0) return null;
        var operationIds = bindings.Where(evidence => evidence.Kind == "browser-compute-operation").ToArray();
        var buildHashes = bindings.Where(evidence => evidence.Kind == "browser-build-sha256").ToArray();
        var evidenceHashes = bindings.Where(evidence => evidence.Kind == "browser-evidence-sha256").ToArray();
        if (operationIds.Length != 1 || buildHashes.Length != 1 || evidenceHashes.Length != 1 || bindings.Length != 3 ||
            !Guid.TryParse(operationIds[0].Value, out var operationId)) return "Browser evidence needs one exact compute operation, build hash and evidence hash.";
        var operation = await db.ComputeOperations.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == operationId &&
            candidate.OrganizationId == execution.OrganizationId && candidate.InstallationId == stage.AgentInstallationId && candidate.Status == "Completed", token);
        if (operation?.WorkloadJson is null || operation.ResultJson is null) return "The browser compute receipt is unavailable.";
        try
        {
            var workload = JsonSerializer.Deserialize<ComputeWorkload>(operation.WorkloadJson, ComputeProtocol.Json);
            var result = JsonSerializer.Deserialize<ComputeWorkloadResult>(operation.ResultJson, ComputeProtocol.Json);
            if (workload?.BrowserTest is not { } browser || browser.SourceCommitSha != commit || browser.BuildSha256 != buildHashes[0].Value ||
                result is not { ErrorCode: null, Command: { ExitCode: 0, TimedOut: false, Truncated: false, ErrorCode: null } command } ||
                command.RequestId != browser.RequestId) return "Browser evidence does not match the exact tested source and build.";
            using var report = JsonDocument.Parse(command.StandardOutput);
            var root = report.RootElement;
            if (root.GetProperty("requestId").GetGuid() != browser.RequestId || root.GetProperty("sourceCommitSha").GetString() != commit ||
                root.GetProperty("buildSha256").GetString() != browser.BuildSha256 ||
                root.GetProperty("evidenceSha256").GetString() != evidenceHashes[0].Value ||
                outcome.OutcomeCode == "passed" && !root.GetProperty("passed").GetBoolean())
                return "The QA verdict conflicts with the recorded browser result.";
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return "The recorded browser evidence is malformed."; }
        return null;
    }
}
