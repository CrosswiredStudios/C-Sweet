using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    /// <summary>
    /// When a coordination session (estimates, planning, review, refinement) reaches a terminal state,
    /// the participants' waiting personal commitments for the same session, board or project are
    /// re-reviewed now instead of when their fallback timer expires. The wake only moves
    /// <see cref="WorkTask.NextReviewAt"/> forward; personal-todo reconciliation queues the review and
    /// the owner reads authoritative state when it runs, so a spurious wake is harmless.
    /// </summary>
    private async Task CaptureCoordinationWakesAsync(CancellationToken ct = default)
    {
        ChangeTracker.DetectChanges();
        var finished = ChangeTracker.Entries<AgentCoordinationSession>()
            .Where(x => x.State == EntityState.Modified && x.Property(s => s.Status).IsModified &&
                        IsTerminalCoordination(x.Entity.Status) &&
                        !IsTerminalCoordination((AgentCoordinationStatus)x.OriginalValues[nameof(AgentCoordinationSession.Status)]!))
            .Select(x => x.Entity).ToList();
        if (finished.Count == 0) return;

        var now = ExecutionClock.GetUtcNow();
        var participants = finished
            .SelectMany(x => new[] { x.InitiatorOrganizationUserId, x.TargetOrganizationUserId })
            .Distinct().ToArray();
        var waiting = await CoreWorkTasks
            .Where(x => x.AssignedEmployeeId != null && participants.Contains(x.AssignedEmployeeId.Value) &&
                        x.Status == WorkTaskStatus.Running && x.ClaimEventId == null && x.ClaimExpiresAt == null &&
                        x.NextReviewAt != null && x.NextReviewAt > now &&
                        x.ArchivedAt == null && x.PersonalWorkContextJson != null)
            .ToListAsync(ct);
        foreach (var task in waiting)
        {
            // The SQL predicate sees persisted state, but identity resolution can
            // return a tracked task already woken or claimed in this transaction.
            // Do not put a review deadline back onto ready or actively claimed work.
            if (task.Status != WorkTaskStatus.Running || task.ClaimEventId is not null ||
                task.ClaimExpiresAt is not null || task.NextReviewAt is null || task.NextReviewAt <= now)
                continue;
            var sessionId = ReadGuidProperty(task.PersonalWorkContextJson, "coordinationSessionId");
            var boardId = ReadGuidProperty(task.PersonalWorkContextJson, "boardId");
            var workstreamId = ReadGuidProperty(task.PersonalWorkContextJson, "workstreamId");
            if (finished.Any(session =>
                    (session.InitiatorOrganizationUserId == task.AssignedEmployeeId ||
                     session.TargetOrganizationUserId == task.AssignedEmployeeId) &&
                    (session.Id == sessionId ||
                     (session.SourceBoardId.HasValue && session.SourceBoardId == boardId) ||
                     (session.WorkstreamId.HasValue && session.WorkstreamId == workstreamId))))
                task.NextReviewAt = now;
        }
    }

    private static bool IsTerminalCoordination(AgentCoordinationStatus status) =>
        status is AgentCoordinationStatus.Completed or AgentCoordinationStatus.Blocked or
            AgentCoordinationStatus.Cancelled or AgentCoordinationStatus.Failed;
}
