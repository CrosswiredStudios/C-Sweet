using System.Data;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkDeliveryService
{
    private static IReadOnlyList<WorkDeliveryCorrection> Corrections(WorkDeliveryPlan plan) =>
        Decode<WorkDeliveryCorrection[]>(plan.CorrectionsJson);

    // Called inside the same transaction as the negative verdict, findings and outbox.
    // Also called by bounded reconciliation for pre-upgrade retained rejections.
    private void EnsureCorrections(WorkDeliveryPlan plan)
    {
        var corrections = Corrections(plan).ToList();
        foreach (var execution in plan.Executions.Where(e => e.Status == "Blocked"))
        {
            var candidate = Candidate(execution);
            if (candidate is null) continue;
            var findings = plan.Findings.Where(f => f.ExecutionId == execution.Id &&
                f.CandidateDigest == candidate.Digest && f.Status == "Open").Select(f => f.Id).Order().ToArray();
            if (findings.Length == 0 || corrections.Any(c => c.ExecutionId == execution.Id && c.CandidateDigest == candidate.Digest)) continue;
            corrections.Add(new(Guid.NewGuid(), execution.Id, candidate.Digest, execution.BoardId,
                plan.ManagerOrganizationUserId, "Pending", 1, findings));
        }
        if (corrections.Count == Corrections(plan).Count) return;
        plan.CorrectionsJson = Encode(corrections);
        plan.Revision++; plan.UpdatedAt = clock.GetUtcNow();
        Queue(plan, "correction.requested");
    }

    public async Task<WorkDeliveryPlanResponse> CoordinateCorrectionAsync(Guid org, Guid actorId,
        CoordinateWorkDeliveryCorrectionRequest request, CancellationToken ct = default)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var plan = await GetAuthorizedAsync(org, actorId, request.PlanId, WorkDeliveryCapabilities.Correct, ct);
        var member = await MemberAsync(org, actorId, ct);
        if (member.Id != plan.ManagerOrganizationUserId || !await db.ProjectParticipants.AnyAsync(p =>
            p.OrganizationId == org && p.WorkstreamId == plan.WorkstreamId && p.OrganizationUserId == member.Id && p.RemovedAt == null, ct))
            throw new UnauthorizedAccessException("Only the currently authorized participating release manager coordinates correction.");
        var replay = await ReplayAsync(org, actorId, request.IdempotencyKey, "correction", request, ct);
        if (replay is not null) return Response(replay);
        var corrections = Corrections(plan).ToList();
        var index = corrections.FindIndex(c => c.Id == request.CorrectionId);
        if (index < 0) throw new KeyNotFoundException("Correction handoff not found.");
        var correction = corrections[index];
        if (correction.Revision != request.ExpectedRevision || correction.OwnerOrganizationUserId != member.Id ||
            plan.Status is not ("Active" or "Paused")) throw new DbUpdateConcurrencyException("The correction or its owning plan changed.");
        var execution = plan.Executions.Single(e => e.Id == correction.ExecutionId);
        if (Candidate(execution)?.Digest != correction.CandidateDigest || execution.Status != "Blocked" ||
            correction.FindingIds.Any(id => !plan.Findings.Any(f => f.Id == id && f.Status == "Open")))
            throw new DbUpdateConcurrencyException("The rejected candidate or open findings changed.");
        if (request.WaitingReason is { Length: > 2000 }) throw new ArgumentException("Keep the corrective next action bounded.");
        var taskIds = request.RemediationTaskIds ?? correction.RemediationTaskIds ?? [];
        var followUpId = request.FollowUpPlanId ?? correction.FollowUpPlanId;
        var sourceId = request.SourceItemId ?? correction.SourceItemId;
        if (sourceId.HasValue && !Scopes(plan).Any(s => s.ChildIds.Contains(sourceId.Value)))
            throw new UnauthorizedAccessException("The correction source must belong to the rejected plan's declared inputs.");
        if (correction.FollowUpPlanId.HasValue && (followUpId != correction.FollowUpPlanId ||
            !taskIds.Order().SequenceEqual((correction.RemediationTaskIds ?? []).Order()) || sourceId != correction.SourceItemId))
            throw new InvalidOperationException("A bound correction cannot be retargeted; preserve its durable work identity.");
        var status = "NeedsPlanning";
        var retainedWait = request.FollowUpPlanId is null && request.RemediationTaskIds is null && !string.IsNullOrWhiteSpace(request.WaitingReason);
        if (retainedWait) { /* Preserve linked work while recording a current actionable wait, including a cancelled/blocked follow-up. */ }
        else if (followUpId is { } followUp)
        {
            await AuthorizeProjectAsync(org, actorId, plan.WorkstreamId, WorkDeliveryCapabilities.Control, ct);
            var next = await GetAuthorizedAsync(org, actorId, followUp, WorkDeliveryCapabilities.Read, ct);
            var boundPlan = next;
            var visited = new HashSet<Guid> { plan.Id };
            while (next.Status == "Cancelled" && visited.Add(next.Id) && visited.Count <= 8)
            {
                var successor = Corrections(next).Where(c => c.Status == "Completed" && c.FollowUpPlanId.HasValue &&
                    next.Executions.Any(e => e.Id == c.ExecutionId && e.Scope == execution.Scope && e.WorkItemId == execution.WorkItemId)).SingleOrDefault();
                if (successor is null) break;
                next = await GetAuthorizedAsync(org, actorId, successor.FollowUpPlanId!.Value, WorkDeliveryCapabilities.Read, ct);
            }
            if (next.Id == plan.Id || next.WorkstreamId != plan.WorkstreamId || next.ManagerOrganizationUserId != member.Id ||
                next.Status is not ("Draft" or "Active" or "Completed") || sourceId is null || taskIds.Count == 0 || taskIds.Distinct().Count() != taskIds.Count)
                throw new InvalidOperationException("Bind distinct corrective tasks and their same-project, manager-owned follow-up plan.");
            var source = await db.CoreWorkTasks.SingleAsync(t => t.Id == sourceId && t.OrganizationId == org && t.BoardId == correction.BoardId, ct);
            var sourceScope = Scopes(plan).First(s => s.ChildIds.Contains(source.Id));
            await ValidateTaskDeliveryAsync(plan, sourceScope, source, ct);
            var original = Decode<WorkItemDeliverySpecification>(source.DeliverySpecificationJson!);
            var findingTime = plan.Findings.Where(f => correction.FindingIds.Contains(f.Id)).Min(f => f.CreatedAt);
            var oldScope = ScopeFor(plan, execution);
            var acceptance = Scopes(next).SingleOrDefault(s => s.Scope == execution.Scope && s.ItemId == execution.WorkItemId);
            if (acceptance is null || !acceptance.Planning.AcceptanceCriteria.SequenceEqual(oldScope.Planning.AcceptanceCriteria) ||
                !acceptance.Planning.Requirements.SequenceEqual(oldScope.Planning.Requirements) ||
                !acceptance.TicketWorkflow!.CompleteContainerItemIds.Order().SequenceEqual(oldScope.TicketWorkflow!.CompleteContainerItemIds.Order()) ||
                !Branches(plan).Where(b => b.Scope == execution.Scope && b.ItemId == execution.WorkItemId)
                    .All(b => Branches(next).Any(n => n == b)))
                throw new InvalidOperationException("Follow-up acceptance must preserve the rejected requirements, criteria, completion effects and branch targets.");
            var declared = Scopes(boundPlan).SelectMany(s => s.ChildIds).ToHashSet();
            foreach (var id in taskIds)
            {
                var task = await db.CoreWorkTasks.SingleAsync(t => t.Id == id && t.OrganizationId == org && t.BoardId == correction.BoardId, ct);
                var delivery = Decode<WorkItemDeliverySpecification>(task.DeliverySpecificationJson ?? "null");
                if (!declared.Contains(id) || task.Id == source.Id || task.ParentWorkTaskId != source.ParentWorkTaskId ||
                    task.CreatedAt < findingTime || delivery?.DeliveryPlanId != boundPlan.Id || delivery.RepositoryId != original.RepositoryId || delivery.DeliveryKind != original.DeliveryKind ||
                    !original.AcceptanceCriteria.All(delivery.AcceptanceCriteria.Contains) ||
                    !(original.Constraints ?? []).All(c => (delivery.Constraints ?? []).Contains(c)))
                    throw new InvalidOperationException("Corrective work must retain the original source scope, acceptance and constraints.");
            }
            await ValidateCurrentScopeAsync(next, ct);
            // Stop the old writer before follow-up integration can change its branches.
            if (plan.Executions.SelectMany(e => e.Stages).SelectMany(s => s.Attempts)
                .Any(a => a.Status is WorkExecutionAttemptStatus.Pending or WorkExecutionAttemptStatus.Running))
                throw new InvalidOperationException("Drain existing delivery attempts before activating a correction.");
            plan.Status = "Paused";
            status = "Assigned"; // The linked sprint/plan records, not this handoff, establish queued/running execution.
            if (next.Status == "Completed")
            {
                foreach (var id in taskIds)
                    if (!await db.WorkItemExecutions.AnyAsync(e => e.WorkItemId == id && e.Status == WorkItemExecutionStatus.Completed &&
                        e.Stages.Any(s => s.Traversal == e.Traversal && s.StageKey == "quality" && s.LastOutcomeCode == "passed" &&
                            s.Status == WorkStageExecutionStatus.Completed && s.CompletedAt >= findingTime), ct))
                        throw new InvalidOperationException("Corrective work requires completed independent QA before resolving findings.");
                if (next.Findings.Any(f => f.Status == "Open") || !next.Executions.Any(e => e.Scope == execution.Scope &&
                    e.WorkItemId == execution.WorkItemId && e.Status == "Completed" && e.CandidateJson != null && Candidate(e)!.Digest != correction.CandidateDigest))
                    throw new InvalidOperationException("A different exact candidate must complete the follow-up acceptance with no open findings.");
                foreach (var finding in plan.Findings.Where(f => correction.FindingIds.Contains(f.Id)))
                {
                    finding.Status = "Resolved"; finding.RemediationTaskIdsJson = Encode(taskIds);
                    finding.ResolutionEvidence = $"Independent corrective QA and follow-up acceptance completed in plan {next.Id:D}; the rejected candidate and verdict remain historical evidence.";
                    finding.ResolvedByOrganizationUserId = member.Id; finding.ResolvedAt = clock.GetUtcNow();
                }
                execution.Status = "Superseded"; execution.Revision++;
                execution.BlockedReason = $"Superseded by accepted corrective delivery {next.Id:D}. The prior rejection remains recorded.";
                await SynchronizeTicketAsync(plan, execution, ct);
                if (plan.Executions.All(e => e.Status is "Completed" or "Superseded" or "Cancelled")) plan.Status = "Cancelled";
                status = "Completed";
            }
        }
        else if (string.IsNullOrWhiteSpace(request.WaitingReason))
            throw new ArgumentException("Retain a specific planning/staffing next action when no follow-up work is bound.");
        corrections[index] = correction with { Revision = correction.Revision + 1, Status = status,
            SourceItemId = sourceId, FollowUpPlanId = followUpId, RemediationTaskIds = taskIds,
            WaitingReason = status == "Completed" ? null : request.WaitingReason };
        plan.CorrectionsJson = Encode(corrections); plan.Revision++; plan.UpdatedAt = clock.GetUtcNow();
        Receipt(plan, actorId, request.IdempotencyKey, "correction", request); Queue(plan, "correction.changed");
        await SaveDeliveryAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
        return Response(plan);
    }
}
