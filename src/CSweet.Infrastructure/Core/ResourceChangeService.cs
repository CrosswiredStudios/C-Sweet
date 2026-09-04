using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed class ResourceChangeService(
    CSweetDbContext db,
    IAuditEventWriter audit,
    ITeamService? teams = null) : IResourceChangeService
{
    public const string MessageSource = "ResourceChangeApproval";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ResourceChangeRequestResponse> ProposeAsync(
        Guid organizationId,
        Guid requesterInstallationId,
        ResourceChangeProposalRequest request,
        CancellationToken cancellationToken = default)
    {
        var key = Required(request.IdempotencyKey, 160, nameof(request.IdempotencyKey));
        var existing = await db.ResourceChangeRequests.AsNoTracking().Include(x => x.Roles)
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
                x.RequesterInstallationId == requesterInstallationId &&
                x.IdempotencyKey == key, cancellationToken);
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

        var conversation = await db.CoreConversations.Include(x => x.Participants)
            .SingleOrDefaultAsync(x => x.Id == request.ConversationId && x.OrganizationId == organizationId &&
                x.ArchivedAt == null, cancellationToken)
            ?? throw new InvalidOperationException("The manager conversation was not found.");
        var participantIds = conversation.Participants.Where(x => x.LeftAt == null)
            .Select(x => x.OrganizationUserId).ToHashSet();
        if (conversation.Kind != ConversationKind.DirectHumanAgent ||
            participantIds.Count != 2 ||
            !participantIds.Contains(requester.Id) ||
            !participantIds.Contains(manager.Id))
            throw new UnauthorizedAccessException("The proposal must be attached to the current manager conversation.");

        if (request.ChatTurnId != Guid.Empty)
        {
            var validTurn = await db.ChatTurns.AsNoTracking().Include(x => x.UserMessage).AnyAsync(x =>
                x.Id == request.ChatTurnId && x.OrganizationId == organizationId &&
                x.ConversationId == conversation.Id && x.TargetAgentOrganizationUserId == requester.Id &&
                x.UserMessage != null && x.UserMessage.SenderOrganizationUserId == manager.Id,
                cancellationToken);
            if (!validTurn)
                throw new UnauthorizedAccessException("The proposal must originate from a current manager turn.");
        }
        else if (manager.EmployeeType != EmployeeType.Agent)
        {
            throw new UnauthorizedAccessException("A human-manager proposal requires an active manager chat turn.");
        }

        OrganizationTeam? scopedTeam = null;
        if (request.TeamId.HasValue)
        {
            scopedTeam = await db.OrganizationTeams.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == request.TeamId && x.OrganizationId == organizationId && x.ArchivedAt == null,
                cancellationToken) ?? throw new ArgumentException("The selected team is not active in this organization.");
            if (scopedTeam.LeadOrganizationUserId != requester.Id)
                throw new UnauthorizedAccessException("Only the current team lead may propose a team-scoped capacity change.");
            if (!request.ExpectedTeamRevision.HasValue || scopedTeam.Revision != request.ExpectedTeamRevision)
                throw new DbUpdateConcurrencyException("The team changed since the capacity proposal was prepared.");
        }
        if (request.WorkstreamId.HasValue)
        {
            var linked = await db.WorkstreamTeamAssignments.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organizationId && x.WorkstreamId == request.WorkstreamId &&
                (!request.TeamId.HasValue || x.TeamId == request.TeamId) && x.EndsAt == null, cancellationToken);
            if (!linked) throw new ArgumentException("The selected workstream is not actively assigned to this team.");
        }
        var evidence = ValidateEvidence(request.Evidence);
        if (request.TeamId.HasValue && (evidence.Count == 0 || string.IsNullOrWhiteSpace(request.ExpectedEffect)))
            throw new ArgumentException("Team-scoped capacity changes require trusted evidence and an expected effect.");
        var desired = ValidateRoles(request.Roles, requester.Id)
            .Select(role => request.TeamId.HasValue ? role with { TeamId = request.TeamId } : role)
            .ToList();
        var previous = await ResolvePreviousAsync(
            organizationId, requester.Id, request.TeamId, request.SupersedesRequestId, cancellationToken);
        var deltas = ComputeDeltas(desired, previous?.Roles.Where(x => x.IsDesired).Select(ToRole).ToList() ?? []);
        if (deltas.Count == 0)
            throw new InvalidOperationException("The proposed team matches the currently approved team.");

        var now = DateTimeOffset.UtcNow;
        var record = new ResourceChangeRequestRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            RequesterOrganizationUserId = requester.Id,
            RequesterInstallationId = requesterInstallationId,
            ManagerOrganizationUserId = manager.Id,
            ConversationId = conversation.Id,
            ChatTurnId = request.ChatTurnId,
            ConversationMessageId = Guid.NewGuid(),
            SupersedesRequestId = previous?.Id,
            ProductGoal = Required(request.ProductGoal, 2048, nameof(request.ProductGoal)),
            TeamId = scopedTeam?.Id,
            WorkstreamId = request.WorkstreamId,
            ExpectedTeamRevision = request.ExpectedTeamRevision,
            TeamKey = scopedTeam?.TeamKey ?? OptionalTeamField(request.TeamKey, 200, nameof(request.TeamKey)),
            TeamName = scopedTeam?.Name ?? OptionalTeamField(request.TeamName, 160, nameof(request.TeamName)),
            TeamDescription = scopedTeam?.Description ?? OptionalTeamField(request.TeamDescription, 2048, nameof(request.TeamDescription)),
            Rationale = Required(request.Rationale, 4096, nameof(request.Rationale)),
            ContextRevision = request.ContextRevision,
            AssumptionsJson = JsonSerializer.Serialize(CleanList(request.Assumptions, 20, 1024), JsonOptions),
            ConstraintsJson = JsonSerializer.Serialize(CleanList(request.Constraints, 20, 1024), JsonOptions),
            EvidenceJson = JsonSerializer.Serialize(evidence, JsonOptions),
            AlternativesConsideredJson = JsonSerializer.Serialize(
                CleanList(request.AlternativesConsidered, 20, 1024), JsonOptions),
            ExpectedEffect = Clean(request.ExpectedEffect, 2048),
            IdempotencyKey = key,
            Status = ResourceChangeRequestStatus.Pending,
            DeliveryStatus = manager.EmployeeType == EmployeeType.Agent ? "QueuedForManagerAgent" : "DeliveredInChat",
            CreatedAt = now,
            UpdatedAt = now
        };
        ValidateTeamProposal(record);
        var deltasByRole = deltas
            .Where(x => x.ChangeKind != "Remove")
            .ToDictionary(x => x.Role.RoleKey, StringComparer.Ordinal);
        foreach (var role in desired)
        {
            record.Roles.Add(ToRecord(
                record.Id,
                deltasByRole.TryGetValue(role.RoleKey, out var delta)
                    ? delta
                    : new ResourceChangeRoleDelta(
                        "Unchanged",
                        role,
                        previous?.Roles.Where(x => x.IsDesired)
                            .Select(ToRole)
                            .SingleOrDefault(x => x.RoleKey == role.RoleKey))));
        }
        foreach (var removed in deltas.Where(x => x.ChangeKind == "Remove"))
            record.Roles.Add(ToRecord(record.Id, removed));

        var summary = BuildSummary(record, deltas);
        db.ResourceChangeRequests.Add(record);
        db.CoreConversationMessages.Add(new ConversationMessage
        {
            Id = record.ConversationMessageId,
            ConversationId = conversation.Id,
            Role = ConversationRole.Assistant,
            Content = summary,
            CreatedAt = now,
            SenderOrganizationUserId = requester.Id,
            CorrelationId = record.Id,
            CausationId = request.ChatTurnId == Guid.Empty ? null : request.ChatTurnId,
            ChatTurnId = request.ChatTurnId == Guid.Empty ? null : request.ChatTurnId,
            DeliveryIntent = CommunicationDeliveryIntent.RequestResponse,
            SourceProvider = MessageSource,
            IdempotencyKey = $"resource-change:{record.Id:N}"
        });
        conversation.UpdatedAt = now;

        if (manager.EmployeeType == EmployeeType.Human)
        {
            db.UserNotifications.Add(new UserNotification
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                RecipientOrganizationUserId = manager.Id,
                OriginatingAgentOrganizationUserId = requester.Id,
                Severity = NotificationSeverity.Important,
                Category = "ResourceChangeApproval",
                Title = "Hiring plan approval needed",
                Body = $"{requester.DisplayName} submitted a team plan for review.",
                ActionUri = $"/organizations/{organizationId:D}/approvals",
                DeduplicationKey = $"resource-change:{record.Id:N}",
                CreatedAt = now
            });
        }
        else if (manager.AgentInstallationId.HasValue)
        {
            db.AgentPlatformEventOutbox.Add(NewEvent(
                organizationId,
                ResourceChangeEvents.Requested,
                new ResourceChangeDecisionEvent(record.Id, organizationId, requester.Id, manager.Id, "Pending", now)
                {
                    TeamId = record.TeamId,
                    WorkstreamId = record.WorkstreamId
                },
                $"resource-change-requested:{record.Id:N}",
                manager.AgentInstallationId));
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("management.resource-change.requested", nameof(ResourceChangeRequestRecord), record.Id,
            $"Requested manager approval for {desired.Count} role(s).", cancellationToken: cancellationToken);
        return ToResponse(record);
    }

    public async Task<ResourceChangeReadResponse> ReadForInstallationAsync(
        Guid organizationId,
        Guid installationId,
        ResourceChangeReadRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == installationId && x.IsActive,
            cancellationToken) ?? throw new UnauthorizedAccessException("The installation is not an active employee.");

        var query = db.ResourceChangeRequests.AsNoTracking().Include(x => x.Roles)
            .Where(x => x.OrganizationId == organizationId &&
                (x.RequesterOrganizationUserId == actor.Id ||
                 x.ManagerOrganizationUserId == actor.Id ||
                 x.Status == ResourceChangeRequestStatus.Approved));
        if (request.RequestId.HasValue) query = query.Where(x => x.Id == request.RequestId.Value);
        if (request.Statuses is { Count: > 0 })
        {
            var statuses = request.Statuses
                .Select(ParseStatus)
                .ToList();
            query = query.Where(x => statuses.Contains(x.Status));
        }
        return new ResourceChangeReadResponse((await query.OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken)).Select(ToResponse).ToList());
    }

    public async Task<ResourceChangeRequestResponse> DecideForInstallationAsync(
        Guid organizationId,
        Guid managerInstallationId,
        ResourceChangeDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var managerId = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.AgentInstallationId == managerInstallationId && x.IsActive)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The deciding installation is not an active employee.");
        return await DecideAsync(organizationId, managerId, request, cancellationToken);
    }

    public async Task<ResourceChangeRequestResponse> DecideForUserAsync(
        Guid organizationId,
        Guid applicationUserId,
        ResourceChangeDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var managerId = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId && x.IsActive)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The deciding user is not an active employee.");
        return await DecideAsync(organizationId, managerId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<ResourceChangeRequestResponse>> ListForDashboardAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default) =>
        (await db.ResourceChangeRequests.AsNoTracking().Include(x => x.Roles)
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken))
        .Select(ToResponse).ToList();

    private async Task<ResourceChangeRequestResponse> DecideAsync(
        Guid organizationId,
        Guid managerId,
        ResourceChangeDecisionRequest request,
        CancellationToken token)
    {
        var record = await db.ResourceChangeRequests.Include(x => x.Roles)
            .SingleOrDefaultAsync(x => x.Id == request.RequestId && x.OrganizationId == organizationId, token)
            ?? throw new ArgumentException("The resource-change request was not found.");
        var requester = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == record.RequesterOrganizationUserId && x.OrganizationId == organizationId && x.IsActive, token);
        if (requester?.ReportsToOrganizationUserId != managerId || record.ManagerOrganizationUserId != managerId)
        {
            await audit.WriteAsync(
                "management.resource-change.decision-denied",
                nameof(ResourceChangeRequestRecord),
                record.Id,
                "A non-current manager attempted to decide the resource-change request.",
                cancellationToken: token);
            throw new UnauthorizedAccessException("Only the requester’s current manager may decide this request.");
        }
        var decisionKey = Required(request.IdempotencyKey, 160, nameof(request.IdempotencyKey));
        if (record.Status != ResourceChangeRequestStatus.Pending)
        {
            if (record.DecisionIdempotencyKey == decisionKey)
            {
                if (record.Status == ResourceChangeRequestStatus.Approved)
                    await ResolveApprovedTeamAsync(record, token);
                if (record.Status == ResourceChangeRequestStatus.Approved &&
                    await EnsureBoardCreationGrantAsync(record, managerId, token))
                {
                    await db.SaveChangesAsync(token);
                    await WriteBoardCreationGrantAuditAsync(record, token);
                }
                return ToResponse(record);
            }
            throw new InvalidOperationException("The resource-change request has already been decided.");
        }

        record.Status = request.Decision switch
        {
            ResourceChangeDecisionKinds.Approve => ResourceChangeRequestStatus.Approved,
            ResourceChangeDecisionKinds.RequestRevision => ResourceChangeRequestStatus.RevisionRequested,
            ResourceChangeDecisionKinds.Reject => ResourceChangeRequestStatus.Rejected,
            _ => throw new ArgumentException("Decision must be Approve, RequestRevision, or Reject.")
        };
        record.DecisionComment = Clean(request.Comment, 4000);
        record.DecidedByOrganizationUserId = managerId;
        record.DecisionIdempotencyKey = decisionKey;
        record.DecidedAt = DateTimeOffset.UtcNow;
        record.UpdatedAt = record.DecidedAt.Value;
        var boardCreationGrantCreated = false;
        if (record.Status == ResourceChangeRequestStatus.Approved)
        {
            await ResolveApprovedTeamAsync(record, token);
            var older = await db.ResourceChangeRequests.Where(x =>
                x.OrganizationId == organizationId &&
                (record.TeamId.HasValue
                    ? x.TeamId == record.TeamId
                    : x.RequesterOrganizationUserId == record.RequesterOrganizationUserId) &&
                x.Id != record.Id &&
                x.Status == ResourceChangeRequestStatus.Approved).ToListAsync(token);
            foreach (var item in older)
            {
                item.Status = ResourceChangeRequestStatus.Superseded;
                item.UpdatedAt = record.UpdatedAt;
            }
            boardCreationGrantCreated = await EnsureBoardCreationGrantAsync(record, managerId, token);
        }

        db.AgentPlatformEventOutbox.Add(NewEvent(
            organizationId,
            ResourceChangeEvents.Decided,
            new ResourceChangeDecisionEvent(
                record.Id, organizationId, record.RequesterOrganizationUserId, managerId,
                record.Status.ToString(), record.DecidedAt.Value)
            {
                TeamId = record.TeamId,
                WorkstreamId = record.WorkstreamId,
                DecidedByOrganizationUserId = record.DecidedByOrganizationUserId
            },
            $"resource-change-decided:{record.Id:N}",
            targetInstallationId: null));
        await db.SaveChangesAsync(token);
        if (boardCreationGrantCreated)
            await WriteBoardCreationGrantAuditAsync(record, token);
        await audit.WriteAsync($"management.resource-change.{record.Status.ToString().ToLowerInvariant()}",
            nameof(ResourceChangeRequestRecord), record.Id,
            $"The resource-change request was {record.Status}.", cancellationToken: token);
        return ToResponse(record);
    }

    private async Task<bool> EnsureBoardCreationGrantAsync(
        ResourceChangeRequestRecord record,
        Guid managerId,
        CancellationToken token)
    {
        if (record.TeamId.HasValue)
        {
            var created = await TeamAgentGrantProvisioner.EnsureAsync(
                db,
                record.OrganizationId,
                record.RequesterInstallationId,
                record.TeamId.Value,
                managerId,
                record.DecidedAt ?? DateTimeOffset.UtcNow,
                token);
            return created.Count > 0;
        }

        var requiredCapabilitiesJson = await (
            from installation in db.AgentInstallations.AsNoTracking()
            join grant in db.AgentInstallationGrants.AsNoTracking()
                on installation.Id equals grant.AgentInstallationId
            where installation.Id == record.RequesterInstallationId &&
                  installation.BusinessId == record.OrganizationId.ToString("D") &&
                  installation.Scope == PluginInstallationScope.Organization &&
                  installation.IsEnabled &&
                  installation.RevisionStatus == PluginRevisionStatus.Active
            select grant.RequiredCapabilitiesJson)
            .SingleOrDefaultAsync(token);
        if (!IncludesCapability(requiredCapabilitiesJson, WorkBoardActions.Create))
            return false;

        var now = DateTimeOffset.UtcNow;
        const GrantScopeKind scopeKind = GrantScopeKind.Organization;
        Guid? scopeId = null;
        var alreadyGranted = await db.ScopedActionGrants.AnyAsync(x =>
            x.OrganizationId == record.OrganizationId &&
            x.SubjectKind == GrantSubjectKind.AgentInstallation &&
            x.SubjectId == record.RequesterInstallationId &&
            x.Action == WorkBoardActions.Create &&
            x.ScopeKind == scopeKind &&
            x.ScopeId == scopeId &&
            x.RevokedAt == null &&
            (!x.ExpiresAt.HasValue || x.ExpiresAt > now), token);
        if (alreadyGranted) return false;

        db.ScopedActionGrants.Add(new ScopedActionGrant
        {
            Id = Guid.NewGuid(),
            OrganizationId = record.OrganizationId,
            SubjectKind = GrantSubjectKind.AgentInstallation,
            SubjectId = record.RequesterInstallationId,
            Action = WorkBoardActions.Create,
            ScopeKind = scopeKind,
            ScopeId = scopeId,
            CanDelegate = false,
            ParentGrantId = null,
            GrantedBySubjectKind = GrantSubjectKind.OrganizationUser,
            GrantedBySubjectId = managerId,
            GrantedAt = record.DecidedAt ?? now
        });
        return true;
    }

    private Task WriteBoardCreationGrantAuditAsync(
        ResourceChangeRequestRecord record,
        CancellationToken token) =>
        audit.WriteAsync(
            "management.resource-change.work-board-create-granted",
            nameof(ScopedActionGrant),
            record.RequesterInstallationId,
            record.TeamId.HasValue
                ? "Granted declared nondelegable team-scoped work actions after manager approval."
                : "Granted organization-scoped work-board creation after manager approval.",
            cancellationToken: token);

    private async Task ResolveApprovedTeamAsync(
        ResourceChangeRequestRecord record,
        CancellationToken token)
    {
        if (record.TeamId.HasValue || string.IsNullOrWhiteSpace(record.TeamKey))
            return;
        var teamService = teams
            ?? throw new InvalidOperationException("Team management is unavailable for this approval.");
        var teamId = await teamService.ResolveApprovedTeamAsync(
            record.OrganizationId,
            record.TeamKey,
            record.TeamName!,
            record.TeamDescription ?? string.Empty,
            record.RequesterOrganizationUserId,
            record.Id,
            token);
        record.TeamId = teamId;
        foreach (var role in record.Roles.Where(x => x.IsDesired))
            role.TeamId = teamId;
    }

    private static bool IncludesCapability(string? json, string capability)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOptions)?
                .Contains(capability, StringComparer.Ordinal) == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<ResourceChangeRequestRecord?> ResolvePreviousAsync(
        Guid organizationId,
        Guid requesterId,
        Guid? teamId,
        Guid? requestedPreviousId,
        CancellationToken token)
    {
        if (requestedPreviousId.HasValue)
        {
            return await db.ResourceChangeRequests.AsNoTracking().Include(x => x.Roles).SingleOrDefaultAsync(x =>
                x.Id == requestedPreviousId.Value && x.OrganizationId == organizationId &&
                (teamId.HasValue ? x.TeamId == teamId : x.RequesterOrganizationUserId == requesterId) &&
                (x.Status == ResourceChangeRequestStatus.Approved ||
                 x.Status == ResourceChangeRequestStatus.Superseded), token)
                ?? throw new ArgumentException("The superseded resource-change request is invalid.");
        }
        return await db.ResourceChangeRequests.AsNoTracking().Include(x => x.Roles)
            .Where(x => x.OrganizationId == organizationId &&
                (teamId.HasValue ? x.TeamId == teamId : x.RequesterOrganizationUserId == requesterId) &&
                x.Status == ResourceChangeRequestStatus.Approved)
            .OrderByDescending(x => x.DecidedAt).FirstOrDefaultAsync(token);
    }

    private static List<ResourceChangeRole> ValidateRoles(IReadOnlyList<ResourceChangeRole> roles, Guid requesterId)
    {
        if (roles.Count is < 1 or > 20) throw new ArgumentException("A resource-change request requires between 1 and 20 roles.");
        var normalized = roles.Select(role =>
        {
            var requestedCapabilities = CleanList(role.RequiredCapabilities, 25, 256);
            var specializationKeys = requestedCapabilities
                .Where(IsSpecializationKey)
                .Select(value => CanonicalRoleKey(value, "role.requiredCapabilities"));
            return role with
            {
                RoleKey = Required(role.RoleKey, 160, "role.roleKey").ToLowerInvariant(),
                RoleCategoryKey = CanonicalRoleKey(role.RoleCategoryKey, "role.roleCategoryKey"),
                PreferredSpecializationKeys = CleanCanonicalKeys(
                    (role.PreferredSpecializationKeys ?? []).Concat(specializationKeys).ToList(),
                    20,
                    "role.preferredSpecializationKeys"),
                Team = Required(role.Team, 160, "role.team"),
                Title = Required(role.Title, 256, "role.title"),
                Purpose = Required(role.Purpose, 2048, "role.purpose"),
                Timing = Required(role.Timing, 32, "role.timing"),
                ReportsToOrganizationUserId = role.ReportsToOrganizationUserId ?? (role.ReportsToRoleKey is null ? requesterId : null),
                ReportsToRoleKey = Clean(role.ReportsToRoleKey, 160)?.ToLowerInvariant(),
                RequiredCapabilities = requestedCapabilities.Where(IsEnforceableCapability).ToList()
            };
        }).ToList();
        if (normalized.Select(x => x.RoleKey).Distinct(StringComparer.Ordinal).Count() != normalized.Count)
            throw new ArgumentException("Role keys must be unique.");
        foreach (var role in normalized)
        {
            if (role.Headcount is < 1 or > 100) throw new ArgumentException("Role headcount must be between 1 and 100.");
            if (role.Priority is < 1 or > 100) throw new ArgumentException("Role priority must be between 1 and 100.");
            if (role.ReportsToOrganizationUserId.HasValue == (role.ReportsToRoleKey is not null))
                throw new ArgumentException($"Role '{role.Title}' must have exactly one reporting target.");
            if (role.ReportsToOrganizationUserId.HasValue && role.ReportsToOrganizationUserId != requesterId)
                throw new ArgumentException("Product-team roles may report only to the requester or another proposed role.");
            if (role.ReportsToRoleKey is not null && !normalized.Any(x => x.RoleKey == role.ReportsToRoleKey))
                throw new ArgumentException($"Role '{role.Title}' reports to an unknown role.");
        }
        EnsureAcyclic(normalized);
        return normalized;
    }

    private static void EnsureAcyclic(IReadOnlyList<ResourceChangeRole> roles)
    {
        var byKey = roles.ToDictionary(x => x.RoleKey, StringComparer.Ordinal);
        foreach (var role in roles)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { role.RoleKey };
            var current = role;
            while (current.ReportsToRoleKey is { } parent)
            {
                if (!seen.Add(parent)) throw new ArgumentException("The proposed reporting structure contains a cycle.");
                current = byKey[parent];
            }
        }
    }

    private static List<ResourceChangeRoleDelta> ComputeDeltas(
        IReadOnlyList<ResourceChangeRole> desired,
        IReadOnlyList<ResourceChangeRole> previous)
    {
        var prior = previous.ToDictionary(x => x.RoleKey, StringComparer.Ordinal);
        var next = desired.ToDictionary(x => x.RoleKey, StringComparer.Ordinal);
        var result = new List<ResourceChangeRoleDelta>();
        foreach (var role in desired.OrderBy(x => x.Priority))
        {
            if (!prior.TryGetValue(role.RoleKey, out var old))
                result.Add(new("Add", role, null));
            else if (role.Headcount > old.Headcount && EquivalentExceptHeadcount(role, old))
                result.Add(new("Increase", role, old));
            else if (!Equivalent(role, old))
                result.Add(new("Modify", role, old));
        }
        foreach (var old in previous.Where(x => !next.ContainsKey(x.RoleKey)).OrderBy(x => x.Priority))
            result.Add(new("Remove", old, old));
        return result;
    }

    private static bool Equivalent(ResourceChangeRole left, ResourceChangeRole right) =>
        JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions);

    private static bool EquivalentExceptHeadcount(ResourceChangeRole left, ResourceChangeRole right) =>
        Equivalent(left with { Headcount = right.Headcount }, right);

    private static ResourceChangeRoleRecord ToRecord(Guid requestId, ResourceChangeRoleDelta delta) => new()
    {
        Id = Guid.NewGuid(),
        ResourceChangeRequestId = requestId,
        RoleKey = delta.Role.RoleKey,
        RoleCategoryKey = delta.Role.RoleCategoryKey,
        PreferredSpecializationKeysJson = JsonSerializer.Serialize(delta.Role.PreferredSpecializationKeys, JsonOptions),
        Team = delta.Role.Team,
        Title = delta.Role.Title,
        Purpose = delta.Role.Purpose,
        Headcount = delta.Role.Headcount,
        Priority = delta.Role.Priority,
        Timing = delta.Role.Timing,
        RequiredCapabilitiesJson = JsonSerializer.Serialize(delta.Role.RequiredCapabilities, JsonOptions),
        HumanRequired = delta.Role.HumanRequired,
        ReportsToOrganizationUserId = delta.Role.ReportsToOrganizationUserId,
        ReportsToRoleKey = delta.Role.ReportsToRoleKey,
        ChangeKind = delta.ChangeKind,
        IsDesired = delta.ChangeKind != "Remove",
        PreviousRoleJson = delta.PreviousRole is null ? null : JsonSerializer.Serialize(delta.PreviousRole, JsonOptions)
        ,
        TeamId = delta.Role.TeamId
    };

    internal static ResourceChangeRequestResponse ToResponse(ResourceChangeRequestRecord record)
    {
        var desired = record.Roles.Where(x => x.IsDesired).Select(ToRole).OrderBy(x => x.Priority).ToList();
        var deltas = record.Roles.Where(x => x.ChangeKind != "Unchanged")
            .OrderBy(x => x.Priority).Select(x => new ResourceChangeRoleDelta(
            x.ChangeKind,
            ToRole(x),
            string.IsNullOrWhiteSpace(x.PreviousRoleJson)
                ? null
                : JsonSerializer.Deserialize<ResourceChangeRole>(x.PreviousRoleJson, JsonOptions))).ToList();
        return new(
            record.Id, record.OrganizationId, record.RequesterOrganizationUserId, record.RequesterInstallationId,
            record.ManagerOrganizationUserId, record.ConversationId, record.ChatTurnId, record.ProductGoal,
            record.Rationale, record.ContextRevision, desired, deltas,
            ReadStrings(record.AssumptionsJson), ReadStrings(record.ConstraintsJson), record.SupersedesRequestId,
            record.Status.ToString(), record.DeliveryStatus, record.DecisionComment, record.CreatedAt, record.DecidedAt)
        {
            TeamId = record.TeamId,
            TeamKey = record.TeamKey,
            TeamName = record.TeamName,
            TeamDescription = record.TeamDescription,
            WorkstreamId = record.WorkstreamId,
            ExpectedTeamRevision = record.ExpectedTeamRevision,
            Evidence = JsonSerializer.Deserialize<IReadOnlyList<ResourceChangeEvidence>>(record.EvidenceJson, JsonOptions) ?? [],
            AlternativesConsidered = ReadStrings(record.AlternativesConsideredJson),
            ExpectedEffect = record.ExpectedEffect,
            DecidedByOrganizationUserId = record.DecidedByOrganizationUserId
        };
    }

    private static ResourceChangeRole ToRole(ResourceChangeRoleRecord role) => new(
        role.RoleKey, role.Team, role.Title, role.Purpose, role.Headcount, role.Priority, role.Timing,
        ReadStrings(role.RequiredCapabilitiesJson), role.HumanRequired,
        role.ReportsToOrganizationUserId, role.ReportsToRoleKey)
    {
        TeamId = role.TeamId,
        RoleCategoryKey = role.RoleCategoryKey,
        PreferredSpecializationKeys = ReadStrings(role.PreferredSpecializationKeysJson)
    };

    private static string CanonicalRoleKey(string value, string name)
    {
        var result = Required(value, 160, name);
        if (!CSweet.Agent.SDK.RoleTaxonomy.IsCanonicalKey(result))
            throw new ArgumentException($"{name} must be a lowercase kebab-case key.");
        return result;
    }

    private static IReadOnlyList<string> CleanCanonicalKeys(
        IReadOnlyList<string>? values,
        int maximumCount,
        string name)
    {
        var result = (values ?? []).Select(value => CanonicalRoleKey(value, name))
            .Distinct(StringComparer.Ordinal).ToList();
        if (result.Count > maximumCount)
            throw new ArgumentException($"{name} may contain at most {maximumCount} values.");
        return result;
    }

    private static bool IsEnforceableCapability(string value) => value.Contains('.', StringComparison.Ordinal);

    private static bool IsSpecializationKey(string value) => !IsEnforceableCapability(value);

    private static AgentPlatformEventOutboxItem NewEvent(
        Guid organizationId,
        string eventType,
        object data,
        string idempotencyKey,
        Guid? targetInstallationId) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        TargetInstallationId = targetInstallationId,
        EventType = eventType,
        DataJson = JsonSerializer.Serialize(data, JsonOptions),
        IdempotencyKey = idempotencyKey,
        Status = AgentPlatformEventOutboxStatus.Pending,
        Attempts = 0,
        NextAttemptAt = DateTimeOffset.UtcNow,
        OccurredAt = DateTimeOffset.UtcNow
    };

    private static string BuildSummary(ResourceChangeRequestRecord record, IReadOnlyList<ResourceChangeRoleDelta> deltas) =>
        $"Resource change approval requested for “{record.ProductGoal}”. " +
        string.Join("; ", deltas.Select(x => $"{x.ChangeKind}: {x.Role.Title} ({x.Role.Headcount})"));

    private static ResourceChangeRequestStatus ParseStatus(string value) =>
        Enum.TryParse<ResourceChangeRequestStatus>(value, true, out var status)
            ? status
            : throw new ArgumentException($"Unknown resource-change status '{value}'.");

    private static IReadOnlyList<string> ReadStrings(string json) =>
        JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonOptions) ?? [];

    private static IReadOnlyList<string> CleanList(IReadOnlyList<string>? values, int maximumCount, int maximumLength) =>
        (values ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Required(x, maximumLength, "list item"))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(maximumCount).ToList();

    private static IReadOnlyList<ResourceChangeEvidence> ValidateEvidence(IReadOnlyList<ResourceChangeEvidence>? evidence)
    {
        if ((evidence?.Count ?? 0) > 50) throw new ArgumentException("A proposal may contain at most 50 evidence points.");
        return (evidence ?? []).Select(item => item with
        {
            Kind = Required(item.Kind, 80, "evidence.kind"),
            SourceRevision = Required(item.SourceRevision, 128, "evidence.sourceRevision"),
            Summary = Required(item.Summary, 2048, "evidence.summary"),
            Unit = Clean(item.Unit, 40)
        }).ToList();
    }

    private static string Required(string? value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.");
        var cleaned = value.Trim();
        if (cleaned.Length > maximum) throw new ArgumentException($"{name} exceeds {maximum} characters.");
        return cleaned;
    }

    private static string? OptionalTeamField(string? value, int maximum, string name)
    {
        var cleaned = Clean(value, maximum);
        return cleaned;
    }

    private static void ValidateTeamProposal(ResourceChangeRequestRecord record)
    {
        var hasAny = record.TeamKey is not null || record.TeamName is not null || record.TeamDescription is not null;
        if (!hasAny) return;
        if (record.TeamKey is null || record.TeamName is null)
            throw new ArgumentException("A team proposal requires both teamKey and teamName.");
        record.TeamKey = record.TeamKey.ToLowerInvariant();
    }

    private static string? Clean(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = value.Trim();
        if (cleaned.Length > maximum) throw new ArgumentException($"Value exceeds {maximum} characters.");
        return cleaned;
    }
}
