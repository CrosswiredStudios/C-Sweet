using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Notifications;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed class StaffingReplenishmentService(
    CSweetDbContext db,
    IAuditEventWriter audit,
    IHiringService hiring) : IStaffingReplenishmentService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<StaffingReplenishmentResponse> ProposeAsync(
        Guid organizationId,
        Guid requesterInstallationId,
        StaffingReplenishmentProposalRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateProposal(request);
        var existing = await db.StaffingReplenishmentRequests.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.RequesterInstallationId == requesterInstallationId &&
            x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (existing is not null) return ToResponse(existing);

        var requester = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == requesterInstallationId && x.IsActive,
            cancellationToken) ?? throw new UnauthorizedAccessException("The requesting installation is not an active employee.");
        var manager = requester.ReportsToOrganizationUserId.HasValue
            ? await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
                x.Id == requester.ReportsToOrganizationUserId.Value && x.OrganizationId == organizationId && x.IsActive,
                cancellationToken)
            : null;
        if (manager is null) throw new InvalidOperationException("The requesting employee has no active manager.");
        var approved = await db.ResourceChangeRequests.AsNoTracking().Include(x => x.Roles).SingleOrDefaultAsync(x =>
            x.Id == request.SourceResourceChangeRequestId && x.OrganizationId == organizationId &&
            x.RequesterInstallationId == requesterInstallationId && x.TeamId == request.TeamId &&
            x.Status == ResourceChangeRequestStatus.Approved, cancellationToken)
            ?? throw new InvalidOperationException("The approved desired-team baseline was not found.");
        var desiredByKey = approved.Roles.Where(x => x.IsDesired).ToDictionary(x => x.RoleKey, StringComparer.Ordinal);
        foreach (var gap in request.Gaps)
        {
            if (!desiredByKey.TryGetValue(gap.RoleKey, out var role) || role.Headcount != gap.DesiredHeadcount)
                throw new InvalidOperationException($"Gap role '{gap.RoleKey}' does not match the approved desired-team baseline.");
        }
        var fulfilledByRole = await db.WorkforcePlans.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                x.SourceResourceChangeRequestId == approved.Id && x.RoleKey != null)
            .GroupBy(x => x.RoleKey!)
            .Select(group => new { RoleKey = group.Key, Headcount = group.Sum(x => x.FulfilledHeadcount) })
            .ToDictionaryAsync(x => x.RoleKey, x => x.Headcount, StringComparer.Ordinal, cancellationToken);
        foreach (var gap in request.Gaps)
        {
            if (fulfilledByRole.GetValueOrDefault(gap.RoleKey) <= gap.EffectiveHeadcount)
                throw new InvalidOperationException(
                    $"Gap role '{gap.RoleKey}' has not lost previously fulfilled capacity. Continue the original approved hiring plan instead.");
        }
        var duplicateGap = await db.StaffingReplenishmentRequests.AsNoTracking().SingleOrDefaultAsync(x =>
            x.SourceResourceChangeRequestId == request.SourceResourceChangeRequestId &&
            x.DecisionFingerprint == request.DecisionFingerprint &&
            x.Status != StaffingReplenishmentRequestStatus.Rejected, cancellationToken);
        if (duplicateGap is not null) return ToResponse(duplicateGap);

        var conversation = await db.CoreConversations.Include(x => x.Participants).SingleOrDefaultAsync(x =>
            x.Id == request.ConversationId && x.OrganizationId == organizationId && x.ArchivedAt == null,
            cancellationToken) ?? throw new InvalidOperationException("The manager conversation was not found.");
        var participants = conversation.Participants.Where(x => x.LeftAt == null)
            .Select(x => x.OrganizationUserId).ToHashSet();
        if (conversation.Kind != ConversationKind.DirectHumanAgent || participants.Count != 2 ||
            !participants.Contains(requester.Id) || !participants.Contains(manager.Id))
            throw new UnauthorizedAccessException("The replenishment proposal must use the current manager conversation.");

        var now = DateTimeOffset.UtcNow;
        var record = new StaffingReplenishmentRequestRecord
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, RequesterOrganizationUserId = requester.Id,
            RequesterInstallationId = requesterInstallationId, ManagerOrganizationUserId = manager.Id,
            SourceResourceChangeRequestId = approved.Id, TeamId = request.TeamId,
            ConversationId = request.ConversationId, GapsJson = JsonSerializer.Serialize(request.Gaps, JsonOptions),
            OperationalImpact = request.OperationalImpact.Trim(),
            InterimControlsJson = JsonSerializer.Serialize(CleanList(request.InterimControls, 20, 1024), JsonOptions),
            DecisionFingerprint = request.DecisionFingerprint, IdempotencyKey = request.IdempotencyKey,
            Status = StaffingReplenishmentRequestStatus.Pending, CreatedAt = now, UpdatedAt = now
        };
        db.StaffingReplenishmentRequests.Add(record);
        db.CoreConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(), ConversationId = conversation.Id, Role = ConversationRole.Assistant,
            SenderOrganizationUserId = requester.Id, CorrelationId = record.Id,
            Content = BuildProposalSummary(record, request.Gaps), CreatedAt = now,
            DeliveryIntent = CommunicationDeliveryIntent.RequestResponse,
            SourceProvider = "StaffingReplenishmentApproval",
            IdempotencyKey = $"staffing-replenishment:{record.Id:N}"
        });
        if (manager.EmployeeType == EmployeeType.Human)
        {
            db.UserNotifications.Add(new UserNotification
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId,
                RecipientOrganizationUserId = manager.Id,
                OriginatingAgentOrganizationUserId = requester.Id,
                Severity = NotificationSeverity.Important, Category = "StaffingReplenishmentApproval",
                Title = "Replacement hiring plan approval needed",
                Body = $"{requester.DisplayName} detected lost team capacity and submitted a replenishment plan.",
                ActionUri = $"/organizations/{organizationId:D}/approvals",
                DeduplicationKey = $"staffing-replenishment:{record.Id:N}", CreatedAt = now
            });
        }
        else if (manager.AgentInstallationId.HasValue)
        {
            db.AgentPlatformEventOutbox.Add(NewEvent(organizationId, StaffingReplenishmentEvents.Requested,
                new StaffingReplenishmentDecisionEvent(record.Id, organizationId, requester.Id, manager.Id, "Pending", now),
                $"staffing-replenishment-requested:{record.Id:N}", manager.AgentInstallationId));
        }
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("management.staffing-replenishment.requested", nameof(StaffingReplenishmentRequestRecord),
            record.Id, $"Requested manager approval for {request.Gaps.Count} staffing gap(s).", cancellationToken: cancellationToken);
        return ToResponse(record);
    }

    public async Task<StaffingReplenishmentReadResponse> ReadForInstallationAsync(
        Guid organizationId, Guid installationId, StaffingReplenishmentReadRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == installationId && x.IsActive,
            cancellationToken) ?? throw new UnauthorizedAccessException("The installation is not an active employee.");
        var query = db.StaffingReplenishmentRequests.AsNoTracking().Where(x =>
            x.OrganizationId == organizationId &&
            (x.RequesterOrganizationUserId == actor.Id || x.ManagerOrganizationUserId == actor.Id));
        if (request.RequestId.HasValue) query = query.Where(x => x.Id == request.RequestId.Value);
        if (request.SourceResourceChangeRequestId.HasValue)
            query = query.Where(x => x.SourceResourceChangeRequestId == request.SourceResourceChangeRequestId.Value);
        if (request.Statuses is { Count: > 0 })
        {
            var statuses = request.Statuses.Select(ParseStatus).ToList();
            query = query.Where(x => statuses.Contains(x.Status));
        }
        return new StaffingReplenishmentReadResponse((await query.OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken)).Select(ToResponse).ToList());
    }

    public async Task<StaffingReplenishmentResponse> DecideForInstallationAsync(
        Guid organizationId, Guid managerInstallationId, StaffingReplenishmentDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var managerId = await db.CoreOrganizationUsers.AsNoTracking().Where(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == managerInstallationId && x.IsActive)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The deciding installation is not an active employee.");
        return await DecideAsync(organizationId, managerId, request, cancellationToken);
    }

    public async Task<StaffingReplenishmentResponse> DecideForUserAsync(
        Guid organizationId, Guid applicationUserId, StaffingReplenishmentDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var managerId = await db.CoreOrganizationUsers.AsNoTracking().Where(x =>
            x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId && x.IsActive)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The deciding user is not an active employee.");
        return await DecideAsync(organizationId, managerId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<StaffingReplenishmentResponse>> ListForDashboardAsync(
        Guid organizationId, CancellationToken cancellationToken = default) =>
        (await db.StaffingReplenishmentRequests.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken)).Select(ToResponse).ToList();

    private async Task<StaffingReplenishmentResponse> DecideAsync(
        Guid organizationId, Guid managerId, StaffingReplenishmentDecisionRequest request, CancellationToken token)
    {
        var record = await db.StaffingReplenishmentRequests.SingleOrDefaultAsync(x =>
            x.Id == request.RequestId && x.OrganizationId == organizationId, token)
            ?? throw new ArgumentException("The staffing-replenishment request was not found.");
        var requester = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == record.RequesterOrganizationUserId && x.IsActive, token);
        if (requester?.ReportsToOrganizationUserId != managerId || record.ManagerOrganizationUserId != managerId)
            throw new UnauthorizedAccessException("Only the requester’s current manager may decide this request.");
        if (record.Status != StaffingReplenishmentRequestStatus.Pending)
        {
            if (record.DecisionIdempotencyKey == request.IdempotencyKey) return ToResponse(record);
            throw new InvalidOperationException("The staffing-replenishment request has already been decided.");
        }
        record.Status = request.Decision switch
        {
            ResourceChangeDecisionKinds.Approve => StaffingReplenishmentRequestStatus.Approved,
            ResourceChangeDecisionKinds.RequestRevision => StaffingReplenishmentRequestStatus.RevisionRequested,
            ResourceChangeDecisionKinds.Reject => StaffingReplenishmentRequestStatus.Rejected,
            _ => throw new ArgumentException("Decision must be Approve, RequestRevision, or Reject.")
        };
        record.DecisionComment = Clean(request.Comment, 4000);
        record.DecisionIdempotencyKey = request.IdempotencyKey;
        record.DecidedByOrganizationUserId = managerId;
        record.DecidedAt = record.UpdatedAt = DateTimeOffset.UtcNow;
        if (record.Status == StaffingReplenishmentRequestStatus.Approved)
        {
            foreach (var gap in ReadGaps(record))
            {
                _ = await hiring.UpsertRecommendationAsync(organizationId, record.RequesterInstallationId,
                    new UpsertHiringRecommendationRequest(
                        $"Replenish {gap.RoleTitle}",
                        $"Restore {gap.MissingHeadcount} approved {gap.RoleTitle} seat(s). {record.OperationalImpact}",
                        null, [], null,
                        $"staffing-replenishment:{record.Id:N}:{gap.RoleKey}")
                    {
                        Priority = 1, RoleKey = gap.RoleKey, Headcount = gap.MissingHeadcount,
                        SourceResourceChangeRequestId = record.SourceResourceChangeRequestId,
                        TeamId = record.TeamId
                    }, token);
            }
        }
        db.AgentPlatformEventOutbox.Add(NewEvent(organizationId, StaffingReplenishmentEvents.Decided,
            new StaffingReplenishmentDecisionEvent(record.Id, organizationId, record.RequesterOrganizationUserId,
                managerId, record.Status.ToString(), record.DecidedAt.Value),
            $"staffing-replenishment-decided:{record.Id:N}", record.RequesterInstallationId));
        await db.SaveChangesAsync(token);
        await audit.WriteAsync($"management.staffing-replenishment.{record.Status.ToString().ToLowerInvariant()}",
            nameof(StaffingReplenishmentRequestRecord), record.Id,
            $"The staffing-replenishment request was {record.Status}.", cancellationToken: token);
        return ToResponse(record);
    }

    private static void ValidateProposal(StaffingReplenishmentProposalRequest request)
    {
        if (request.SourceResourceChangeRequestId == Guid.Empty || request.TeamId == Guid.Empty ||
            request.ConversationId == Guid.Empty || request.Gaps.Count is < 1 or > 20 ||
            string.IsNullOrWhiteSpace(request.OperationalImpact) || request.OperationalImpact.Length > 4096 ||
            string.IsNullOrWhiteSpace(request.DecisionFingerprint) || request.DecisionFingerprint.Length > 128 ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160)
            throw new ArgumentException("The staffing-replenishment proposal is incomplete or exceeds platform bounds.");
        if (request.Gaps.Any(x => string.IsNullOrWhiteSpace(x.RoleKey) || x.RoleKey.Length > 160 ||
            string.IsNullOrWhiteSpace(x.RoleTitle) || x.RoleTitle.Length > 256 ||
            x.DesiredHeadcount < 1 || x.EffectiveHeadcount < 0 || x.MissingHeadcount < 1 ||
            x.MissingHeadcount != x.DesiredHeadcount - x.EffectiveHeadcount ||
            x.EligibilityEvidence.Count > 20 || x.EligibilityEvidence.Any(e => string.IsNullOrWhiteSpace(e) || e.Length > 1024)))
            throw new ArgumentException("One or more staffing gaps are invalid.");
    }

    private static StaffingReplenishmentRequestStatus ParseStatus(string value) =>
        Enum.TryParse<StaffingReplenishmentRequestStatus>(value, true, out var status)
            ? status : throw new ArgumentException($"Unsupported staffing-replenishment status '{value}'.");

    private static string BuildProposalSummary(StaffingReplenishmentRequestRecord record,
        IReadOnlyList<StaffingReplenishmentGap> gaps) =>
        $"Replacement hiring plan `{record.Id:D}` needs approval. " +
        string.Join("; ", gaps.Select(x => $"{x.RoleTitle}: {x.EffectiveHeadcount}/{x.DesiredHeadcount} viable")) +
        $". Impact: {record.OperationalImpact}";

    private static StaffingReplenishmentResponse ToResponse(StaffingReplenishmentRequestRecord record) => new(
        record.Id, record.OrganizationId, record.RequesterOrganizationUserId, record.RequesterInstallationId,
        record.ManagerOrganizationUserId, record.SourceResourceChangeRequestId, record.TeamId,
        record.ConversationId, ReadGaps(record), record.OperationalImpact,
        ReadStrings(record.InterimControlsJson), record.DecisionFingerprint, record.Status.ToString(),
        record.DecisionComment, record.CreatedAt, record.DecidedAt);

    private static IReadOnlyList<StaffingReplenishmentGap> ReadGaps(StaffingReplenishmentRequestRecord record) =>
        JsonSerializer.Deserialize<List<StaffingReplenishmentGap>>(record.GapsJson, JsonOptions) ?? [];
    private static IReadOnlyList<string> ReadStrings(string json) =>
        JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
    private static IReadOnlyList<string> CleanList(IReadOnlyList<string> values, int maximumCount, int maximumLength) =>
        values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Clean(x, maximumLength)!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(maximumCount).ToList();
    private static string? Clean(string? value, int maximumLength) => string.IsNullOrWhiteSpace(value)
        ? null : value.Trim().Length <= maximumLength ? value.Trim() : value.Trim()[..maximumLength];

    private static AgentPlatformEventOutboxItem NewEvent(
        Guid organizationId, string eventType, object payload, string key, Guid? targetInstallationId) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = organizationId, EventType = eventType,
        DataJson = JsonSerializer.Serialize(payload, JsonOptions), IdempotencyKey = key,
        TargetInstallationId = targetInstallationId, Status = AgentPlatformEventOutboxStatus.Pending,
        NextAttemptAt = DateTimeOffset.UtcNow, OccurredAt = DateTimeOffset.UtcNow
    };
}
