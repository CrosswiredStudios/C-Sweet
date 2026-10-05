using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

public sealed partial class WorkManagementCapabilityHandler
{
    // Once dispatched, an assignment remains evidence. A manager must cancel/replan
    // running work rather than transferring a lease to a different installation.
    internal static bool IsStartedAssignment(WorkItemExecution item, string key) =>
        item.Stages.Any(s => s.StageKey == key && (s.Attempts.Count > 0 ||
            s.Status is not (WorkStageExecutionStatus.Pending or WorkStageExecutionStatus.Blocked)));

    internal static void ValidateActiveAssignmentRepair(WorkSprintExecution execution, WorkTask item,
        Wire.FinalizeWorkItemDeliveryRequest request)
    {
        var current = execution.Items.Single(x => x.WorkItemId == item.Id);
        if (current.Status is WorkItemExecutionStatus.Completed or WorkItemExecutionStatus.Cancelled)
            throw new InvalidOperationException("Terminal work cannot be restaffed.");
        var delivery = JsonSerializer.Deserialize<Wire.WorkItemDeliverySpecification>(item.DeliverySpecificationJson ?? "null", JsonOptions);
        if (delivery is null || JsonSerializer.Serialize(delivery, JsonOptions) != JsonSerializer.Serialize(request.Delivery, JsonOptions) ||
            item.AccountableOrganizationUserId != request.AccountableOrganizationUserId)
            throw new InvalidOperationException("Active assignment recovery must preserve the approved delivery and accountable owner.");
        var snapshot = JsonSerializer.Deserialize<List<ExecutionAssignmentSnapshot>>(execution.AssignmentSnapshotJson, JsonOptions) ?? [];
        foreach (var assignment in request.StageAssignments.Where(a => IsStartedAssignment(current, a.StageKey)))
            if (!snapshot.Any(a => a.WorkItemId == item.Id && a.StageKey == assignment.StageKey))
                throw new InvalidOperationException($"Started stage '{assignment.StageKey}' has no authoritative assignment snapshot; replan is required.");
        foreach (var previous in snapshot.Where(x => x.WorkItemId == item.Id))
        {
            var next = request.StageAssignments.SingleOrDefault(x => x.StageKey == previous.StageKey)
                ?? throw new InvalidOperationException("Assignment recovery cannot remove an existing assignment.");
            if (IsStartedAssignment(current, previous.StageKey) &&
                (next.PrincipalKind != previous.PrincipalKind.ToString() || next.OrganizationUserId != previous.OrganizationUserId ||
                 next.AgentInstallationId != previous.AgentInstallationId || next.PlatformAction != previous.PlatformAction ||
                 !SameJson(SerializeOptional(next.Requirements), previous.RequirementsJson) ||
                 !SameJson(SerializeOptional(next.SelectionEvidence), previous.SelectionEvidenceJson)))
                throw new InvalidOperationException($"Stage '{previous.StageKey}' has started. Cancel or replan before changing its assignment.");
        }
    }

    private static string? SerializeOptional<T>(T? value) => value is null ? null : JsonSerializer.Serialize(value, JsonOptions);
    private static bool SameJson(string? left, string? right) => left == right ||
        left is not null && right is not null && JsonElement.DeepEquals(
            JsonSerializer.Deserialize<JsonElement>(left), JsonSerializer.Deserialize<JsonElement>(right));

    private void ReconcileAssignmentRepair(WorkSprintExecution execution, WorkTask item,
        IReadOnlyList<Wire.WorkStageAssignment> assignments, Guid actorInstallationId, string idempotencyKey)
    {
        var current = execution.Items.Single(x => x.WorkItemId == item.Id);
        var previousJson = execution.AssignmentSnapshotJson;
        var snapshot = JsonSerializer.Deserialize<List<ExecutionAssignmentSnapshot>>(previousJson, JsonOptions) ?? [];
        var now = DateTimeOffset.UtcNow;
        foreach (var assignment in assignments.Where(a => !IsStartedAssignment(current, a.StageKey)))
        {
            snapshot.RemoveAll(x => x.WorkItemId == item.Id && x.StageKey == assignment.StageKey);
            var principal = Enum.Parse<WorkOrchestrationPrincipalKind>(assignment.PrincipalKind);
            snapshot.Add(new(item.Id, assignment.StageKey, principal, assignment.OrganizationUserId,
                assignment.AgentInstallationId, assignment.PlatformAction, SerializeOptional(assignment.Requirements),
                SerializeOptional(assignment.SelectionEvidence)));
            var stage = current.Stages.SingleOrDefault(s => s.StageKey == current.CurrentStageKey &&
                s.StageKey == assignment.StageKey && s.Traversal == current.Traversal);
            if (stage is null) continue;
            stage.PrincipalKind = principal;
            stage.OrganizationUserId = assignment.OrganizationUserId;
            stage.AgentInstallationId = assignment.AgentInstallationId;
            stage.PlatformAction = assignment.PlatformAction;
            // Only missing staffing is automatically cleared. Other blockers retain
            // their evidence and require the normal explicit retry/decision operation.
            if (stage.Status == WorkStageExecutionStatus.Blocked && stage.LastError == "staffing.assignment_missing")
            {
                var human = stage.StageType == WorkOrchestrationStageType.ManualWork ||
                    stage.StageType == WorkOrchestrationStageType.MemberExecution && principal == WorkOrchestrationPrincipalKind.Human;
                stage.Status = human ? WorkStageExecutionStatus.WaitingForHuman : WorkStageExecutionStatus.Pending;
                current.Status = human ? WorkItemExecutionStatus.WaitingForHuman : WorkItemExecutionStatus.Pending;
                stage.LastError = null;
                stage.LastSummary = "The board manager restored the stage assignment; ready to resume.";
                current.BlockedReason = null;
                item.BlockReason = null;
                item.Status = human ? WorkTaskStatus.Assigned : WorkTaskStatus.Ready;
            }
            stage.UpdatedAt = now;
            current.UpdatedAt = now;
        }
        execution.AssignmentSnapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions);
        execution.Revision++;
        execution.UpdatedAt = now;
        db.WorkOrchestrationEvents.Add(new()
        {
            Id = Guid.NewGuid(), OrganizationId = execution.OrganizationId, BoardId = execution.BoardId,
            SprintExecutionId = execution.Id, ItemExecutionId = current.Id,
            EventType = "orchestration.assignments.reconciled", IdempotencyKey = idempotencyKey,
            DataJson = JsonSerializer.Serialize(new { itemId = item.Id, actorInstallationId,
                assignmentRevision = execution.Revision, previous = JsonSerializer.Deserialize<JsonElement>(previousJson),
                current = JsonSerializer.Deserialize<JsonElement>(execution.AssignmentSnapshotJson) }, JsonOptions),
            OccurredAt = now
        });
    }
}
