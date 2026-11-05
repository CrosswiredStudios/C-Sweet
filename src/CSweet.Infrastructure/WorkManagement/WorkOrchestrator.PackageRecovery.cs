using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkOrchestrator
{
    // An installed executor update is a meaningful recovery condition. A manager wake alone is not.
    // Keep this list narrow: an update never waives a product finding, approval or authority failure.
    internal static bool IsPackageRecoverable(WorkStageExecution stage)
    {
        if (stage.StageKey == "quality" && stage.LastError ==
            "The QA compute operation is unavailable or its generation changed.") return true;
        if (stage.StageKey == "quality" && AgentWorkFailure.IsRetryableTransport(stage.LastError)) return true;
        if (stage.StageKey != "specialist-execution") return false;
        var last = stage.Attempts.OrderByDescending(x => x.Attempt).FirstOrDefault();
        if (last?.ResultJson is null) return false;
        try
        {
            var result = JsonSerializer.Deserialize<Shared.WorkExecutionOutcomeV1>(last.ResultJson, JsonOptions);
            return result is { Disposition: Shared.WorkExecutionDispositions.Blocked,
                Summary: "The MCP service rejected the request: Internal Server Error" };
        }
        catch (JsonException) { return false; }
    }

    internal async Task RecoverUpdatedExecutorsAsync(WorkSprintExecution execution,
        WorkOrchestrationPolicyRevision policy, DateTimeOffset now, CancellationToken token)
    {
        if (execution.Status != WorkSprintExecutionStatus.Active) return;
        foreach (var stage in execution.Items
                     .Where(item => item.Status is WorkItemExecutionStatus.Blocked or WorkItemExecutionStatus.Failed)
                     .Select(WorkOrchestrationBoardState.CurrentStage).Where(s =>
                     (s.Status is WorkStageExecutionStatus.Blocked or WorkStageExecutionStatus.Failed) &&
                     s.PrincipalKind == WorkOrchestrationPrincipalKind.AgentInstallation && s.AgentInstallationId.HasValue))
        {
            if (stage.ItemExecution?.WorkItem?.Status is not (WorkTaskStatus.Blocked or WorkTaskStatus.Failed) ||
                !IsPackageRecoverable(stage))
                continue;
            var last = stage.Attempts.OrderByDescending(a => a.Attempt).FirstOrDefault();
            if (last?.AgentWorkItemId is not { } workId || last.CompletedAt is null) continue;
            var installation = await db.AgentInstallations.Include(i => i.PackageVersion).SingleOrDefaultAsync(i =>
                i.Id == stage.AgentInstallationId && i.BusinessId == execution.OrganizationId.ToString() && i.IsEnabled &&
                i.RevisionStatus == PluginRevisionStatus.Active && i.SetupState == PluginSetupState.Ready &&
                (i.ConfigurationSyncStatus == AgentConfigurationSyncStatus.Current ||
                 i.ConfigurationSyncStatus == AgentConfigurationSyncStatus.PendingNextStart), token);
            if (installation?.PackageVersion is not { } package || installation.UpdatedAt <= last.CompletedAt) continue;
            // An idle updated installation applies its package on the next start. Requiring
            // Current here deadlocks it: no work is queued to cause that start. Never hand
            // recovery to an existing runtime that has not applied the updated configuration.
            if (installation.ConfigurationSyncStatus == AgentConfigurationSyncStatus.PendingNextStart &&
                await db.AgentRuntimeInstances.AnyAsync(r => r.AgentInstallationId == installation.Id &&
                    (r.Status == AgentRuntimeStatus.Queued || r.Status == AgentRuntimeStatus.Starting ||
                     r.Status == AgentRuntimeStatus.WaitingForMcpSession || r.Status == AgentRuntimeStatus.Running ||
                     r.Status == AgentRuntimeStatus.CompletionReported || r.Status == AgentRuntimeStatus.Stopping), token)) continue;
            var priorVersion = await db.AgentRunLogs.AsNoTracking().Where(l => l.AgentWorkItemId == workId &&
                    l.AgentInstallationId == installation.Id && l.AgentPackageVersion != null)
                .OrderByDescending(l => l.StartedAt).Select(l => l.AgentPackageVersion).FirstOrDefaultAsync(token);
            if (string.IsNullOrWhiteSpace(priorVersion) || !IsNewerExecutor(package.Version, priorVersion)) continue;
            var key = $"executor-update:{stage.Id:N}:{last.Id:N}:{package.Id:N}";
            if (await db.WorkOrchestrationEvents.AnyAsync(e => e.OrganizationId == execution.OrganizationId && e.IdempotencyKey == key, token)) continue;
            // Normal dispatch rechecks assignment, project membership, grants, source and capacity.
            // SaveBoardChangesAsync persists this state and its notification outbox in one transaction.
            stage.Status = WorkStageExecutionStatus.Pending; stage.LastError = null; stage.RetryAt = now; stage.UpdatedAt = now;
            var item = stage.ItemExecution!;
            item.Status = WorkItemExecutionStatus.Pending; item.BlockedReason = null; item.UpdatedAt = now;
            item.WorkItem!.Status = WorkTaskStatus.Ready; item.WorkItem.BlockReason = null;
            item.WorkItem.ClaimEventId = null; item.WorkItem.ClaimExpiresAt = null;
            item.WorkItem.WaitingReason = null; item.WorkItem.NextReviewAt = null; item.WorkItem.WaitingOnOrganizationUserId = null;
            item.WorkItem.UpdatedAt = now; item.WorkItem.Revision++;
            execution.UpdatedAt = now; execution.Revision++;
            db.WorkOrchestrationEvents.Add(new() { Id = Guid.NewGuid(), OrganizationId = execution.OrganizationId,
                BoardId = execution.BoardId, SprintExecutionId = execution.Id, ItemExecutionId = item.Id,
                StageExecutionId = stage.Id, AttemptId = last.Id, EventType = "stage.executor-update.recovery",
                IdempotencyKey = key, OccurredAt = now,
                DataJson = JsonSerializer.Serialize(new { previousVersion = priorVersion, package.Version,
                    packageVersionId = package.Id, retainedAssignmentRevision = item.WorkItem.AssignmentRevision }, JsonOptions) });
        }
    }

    private static bool IsNewerExecutor(string current, string prior)
    {
        // Historical telemetry is optional and may predate validated package versions.
        if (current.Split('+', 2)[0].Split('-', 2)[0].Split('.').Length != 3 ||
            prior.Split('+', 2)[0].Split('-', 2)[0].Split('.').Length != 3) return false;
        try { return SemanticVersionComparer.Compare(current, prior) > 0; }
        catch (FormatException) { return false; }
    }
}
