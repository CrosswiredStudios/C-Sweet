using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.Notifications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed class StaffingCorrectionService(CSweetDbContext db)
{
    public async Task<StaffingCorrectionResponse> RequestAsync(Guid organization, Guid recommendationId,
        Guid applicationUser, RequestStaffingCorrection request, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160)
            throw new ArgumentException("A correction reason and idempotency key are required.");
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(token) : null;
        await HiringOrganizationLock.AcquireAsync(db, organization, token);
        var actor = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.OrganizationId == organization &&
            x.ApplicationUserId == applicationUser && x.IsActive && x.ArchivedAt == null, token)
            ?? throw new UnauthorizedAccessException("An active organization member is required.");
        var plan = await db.WorkforcePlans.SingleOrDefaultAsync(x => x.Id == recommendationId && x.OrganizationId == organization, token)
            ?? throw new KeyNotFoundException("The hiring recommendation was not found.");
        var duplicate = await db.StaffingCorrections.SingleOrDefaultAsync(x => x.OrganizationId == organization && x.IdempotencyKey == request.IdempotencyKey, token);
        if (duplicate is not null && (duplicate.RecommendationId != recommendationId || duplicate.RequestedByOrganizationUserId != actor.Id || duplicate.Reason != request.Reason.Trim()))
            throw new InvalidOperationException("This correction key is already bound to another request.");
        var lineage = duplicate is null ? await new ApprovedStaffingLineage(db, organization)
            .ResolveAsync(plan.SourceResourceChangeRequestId, plan.RoleKey, token) : null;
        var source = duplicate is null ? lineage?.Current : await db.ResourceChangeRequests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == organization && x.Id == duplicate.SourceRequestId, token);
        if (source is null) throw new InvalidOperationException("This recommendation has no unambiguous current approved staffing plan to correct. Refresh and review current staffing.");
        if (!CanCorrect(actor, source)) throw new UnauthorizedAccessException("Only the owner or the staffing plan's accountable manager may request correction.");
        var author = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == source.RequesterOrganizationUserId &&
            x.OrganizationId == organization && x.IsActive && x.ArchivedAt == null, token)
            ?? throw new InvalidOperationException("The staffing author is no longer active. The owner must assign current staffing responsibility first.");
        if (actor.PermissionLevel != OrganizationPermissionLevel.Owner && author.ReportsToOrganizationUserId != actor.Id)
            throw new UnauthorizedAccessException("The current reporting manager or owner must request this correction.");
        if (duplicate is not null) return Map(duplicate, source);
        if (source.ContextRevision != request.ExpectedSourceRevision ||
            (request.ExpectedSourceRequestId is { } expectedId ? expectedId != source.Id : source.Id != plan.SourceResourceChangeRequestId))
            throw new DbUpdateConcurrencyException("The staffing source changed. Refresh and review it again.");
        if (source.Status != ResourceChangeRequestStatus.Approved || plan.FulfilledHeadcount >= plan.Headcount ||
            plan.Status is not (ProposalStatus.Pending or ProposalStatus.Approved) || string.IsNullOrWhiteSpace(plan.RoleKey))
            throw new InvalidOperationException("Only an unfilled recommendation from the current approved staffing plan can be corrected.");
        if (await db.StaffingCorrections.AnyAsync(x => x.OrganizationId == organization && x.SourceRequestId == source.Id && x.Status == "Pending", token))
            throw new InvalidOperationException("A correction for this staffing plan is already in progress.");
        var now = DateTimeOffset.UtcNow;
        var correction = new StaffingCorrection { Id = Guid.NewGuid(), OrganizationId = organization, SourceRequestId = source.Id,
            RecommendationId = plan.Id, RoleKey = plan.RoleKey, Reason = request.Reason.Trim(), RequestedByOrganizationUserId = actor.Id,
            IdempotencyKey = request.IdempotencyKey, CreatedAt = now, UpdatedAt = now };
        db.StaffingCorrections.Add(correction);
        var lineageIds = lineage!.SourceIds.ToArray();
        var affected = await db.WorkforcePlans.Where(x => x.OrganizationId == organization && x.SourceResourceChangeRequestId.HasValue && lineageIds.Contains(x.SourceResourceChangeRequestId.Value) &&
            x.RoleKey == correction.RoleKey && x.FulfilledHeadcount < x.Headcount).ToListAsync(token);
        foreach (var item in affected) await HiringRecommendationCancellation.CancelAsync(db, item, correction.Reason, token);
        // Existing pending proposals based on this source must not authorize the disputed slot.
        var drafts = await db.ResourceChangeRequests.Where(x => x.OrganizationId == organization && x.SupersedesRequestId == source.Id &&
            x.Status == ResourceChangeRequestStatus.Pending).ToListAsync(token);
        foreach (var draft in drafts)
        {
            draft.Status = ResourceChangeRequestStatus.RevisionRequested; draft.DecisionComment = correction.Reason;
            draft.DecidedByOrganizationUserId = actor.Id; draft.DecidedAt = draft.UpdatedAt = now;
        }
        QueueWake(db, source, correction, "CorrectionRequested", now);
        db.CoreConversationMessages.Add(new() { Id = Guid.NewGuid(), ConversationId = source.ConversationId,
            Role = ConversationRole.Assistant, Content = $"Staffing correction recorded by {actor.DisplayName} for {plan.Title}: {correction.Reason}",
            SourceProvider = "StaffingCorrection", CorrelationId = correction.Id, IdempotencyKey = $"staffing-correction:{correction.Id:N}", CreatedAt = now });
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return Map(correction, source);
    }

    internal static bool CanCorrect(OrganizationUser actor, ResourceChangeRequestRecord source) =>
        actor.PermissionLevel == OrganizationPermissionLevel.Owner || source.ManagerOrganizationUserId == actor.Id;

    public async Task<IReadOnlyList<StaffingCorrectionResponse>> PendingForUserAsync(Guid organization, Guid applicationUser, CancellationToken token)
    {
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organization &&
            x.ApplicationUserId == applicationUser && x.IsActive && x.ArchivedAt == null, token)
            ?? throw new UnauthorizedAccessException();
        var rows = await (from correction in db.StaffingCorrections.AsNoTracking()
            join source in db.ResourceChangeRequests.AsNoTracking() on correction.SourceRequestId equals source.Id
            where correction.OrganizationId == organization && source.OrganizationId == organization && correction.Status == "Pending" &&
                (actor.PermissionLevel == OrganizationPermissionLevel.Owner || source.ManagerOrganizationUserId == actor.Id || source.RequesterOrganizationUserId == actor.Id)
            orderby correction.CreatedAt select new { Correction = correction, Source = source }).Take(100).ToListAsync(token);
        return rows.Select(x => Map(x.Correction, x.Source)).ToArray();
    }

    public async Task<IReadOnlyList<HiringRecommendationResponse>> WithActionsAsync(Guid organization, Guid applicationUser,
        IReadOnlyList<HiringRecommendationResponse> items, CancellationToken token)
    {
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organization &&
            x.ApplicationUserId == applicationUser && x.IsActive && x.ArchivedAt == null, token);
        if (actor is null) return items;
        var resolver = new ApprovedStaffingLineage(db, organization);
        var pending = await db.StaffingCorrections.Where(x => x.OrganizationId == organization && x.Status == "Pending").Select(x => x.SourceRequestId).ToListAsync(token);
        var result = new List<HiringRecommendationResponse>();
        foreach (var item in items)
        {
            var source = item.RemainingHeadcount > 0 && item.Status is "Pending" or "Approved"
                ? (await resolver.ResolveAsync(item.SourceResourceChangeRequestId, item.RoleKey, token))?.Current : null;
            var allowed = source is not null && !pending.Contains(source.Id) && CanCorrect(actor, source) &&
                await db.CoreOrganizationUsers.AnyAsync(u => u.OrganizationId == organization && u.Id == source.RequesterOrganizationUserId &&
                    u.IsActive && u.ArchivedAt == null && (actor.PermissionLevel == OrganizationPermissionLevel.Owner || u.ReportsToOrganizationUserId == actor.Id), token);
            result.Add(item with { CorrectionSourceRevision = allowed ? source!.ContextRevision : null,
                CorrectionSourceRequestId = allowed ? source!.Id : null });
        }
        return result;
    }

    internal static StaffingCorrectionResponse Map(StaffingCorrection value, ResourceChangeRequestRecord source) =>
        new(value.Id, value.SourceRequestId, value.RecommendationId, source.RequesterOrganizationUserId, source.TeamId, source.WorkstreamId,
            value.RoleKey, value.Reason, value.Status, value.ReplacementRequestId, value.Revision, value.CreatedAt)
            { RequestedByOrganizationUserId = value.RequestedByOrganizationUserId };

    internal static void QueueWake(CSweetDbContext db, ResourceChangeRequestRecord source, StaffingCorrection correction, string status, DateTimeOffset now)
    {
        db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = source.OrganizationId, EventType = ResourceChangeEvents.Decided,
            TargetInstallationId = source.RequesterInstallationId, IdempotencyKey = $"staffing-correction:{correction.Id:N}:{correction.Revision}:{status}",
            DataJson = JsonSerializer.Serialize(new ResourceChangeDecisionEvent(source.Id, source.OrganizationId, source.RequesterOrganizationUserId,
                source.ManagerOrganizationUserId, status, now) { TeamId = source.TeamId, WorkstreamId = source.WorkstreamId }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Status = AgentPlatformEventOutboxStatus.Pending, NextAttemptAt = now, OccurredAt = now });
        db.ApplicationRealtimeOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = source.OrganizationId, EventType = AppRealtimeEvents.HiringRecommendationsChanged,
            Subject = $"organizations/{source.OrganizationId:D}/hiring", DataJson = "{}", Status = ApplicationRealtimeOutboxStatus.Pending, NextAttemptAt = now, OccurredAt = now });
    }
}
