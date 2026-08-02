using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CSweet.Infrastructure.Core;

public sealed class TeamService(
    CSweetDbContext db,
    IAuditEventWriter audit,
    TimeProvider timeProvider) : ITeamService
{
    private const int MaximumInitialMembers = 100;

    public async Task<TeamDirectoryResponse> ListAsync(
        Guid organizationId,
        Guid applicationUserId,
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        var actor = await RequireActorAsync(organizationId, applicationUserId, cancellationToken);
        var query = TeamQuery(organizationId);
        if (!includeArchived) query = query.Where(x => x.ArchivedAt == null);
        var teams = await query.OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var teamIds = teams.Select(x => x.Id).ToList();
        var boards = await db.WorkBoards.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                x.TeamId.HasValue &&
                teamIds.Contains(x.TeamId.Value) &&
                x.ArchivedAt == null)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new { TeamId = x.TeamId!.Value, x.Id, x.WorkstreamId })
            .ToListAsync(cancellationToken);
        var firstBoards = boards.GroupBy(x => x.TeamId).ToDictionary(x => x.Key, x => x.First());
        return new TeamDirectoryResponse(
            actor.Id,
            actor.PermissionLevel >= OrganizationPermissionLevel.Manager,
            teams.Select(team =>
            {
                firstBoards.TryGetValue(team.Id, out var board);
                return ToSummary(team) with
                {
                    BoardId = board?.Id,
                    WorkstreamId = board?.WorkstreamId
                };
            }).ToList());
    }

    public async Task<TeamDetailResponse?> GetAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireActorAsync(organizationId, applicationUserId, cancellationToken);
        var team = await TeamQuery(organizationId).SingleOrDefaultAsync(x => x.Id == teamId, cancellationToken);
        return team is null ? null : await ReloadAsync(organizationId, teamId, cancellationToken);
    }

    public async Task<TeamDetailResponse> CreateAsync(
        Guid organizationId,
        Guid applicationUserId,
        CreateTeamRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await RequireManagerAsync(organizationId, applicationUserId, cancellationToken);
        await using var transaction = await BeginMutationTransactionAsync(cancellationToken);
        var name = Required(request.Name, 160, nameof(request.Name));
        var normalizedName = NormalizeName(name);
        if (await db.OrganizationTeams.AnyAsync(x =>
                x.OrganizationId == organizationId &&
                x.NormalizedName == normalizedName &&
                x.ArchivedAt == null,
                cancellationToken))
            throw new ArgumentException("An active team with this name already exists.");

        var requestedMembers = (request.Members ?? []).ToList();
        if (requestedMembers.Count > MaximumInitialMembers)
            throw new ArgumentException($"A team may be created with at most {MaximumInitialMembers} initial members.");
        if (requestedMembers.Select(x => x.OrganizationUserId).Distinct().Count() != requestedMembers.Count)
            throw new ArgumentException("Initial team members must be unique.");

        var lead = await RequireActiveEmployeeAsync(
            organizationId, request.LeadOrganizationUserId, cancellationToken);
        var memberIds = requestedMembers.Select(x => x.OrganizationUserId)
            .Append(lead.Id).Distinct().Order().ToList();
        var employees = await db.CoreOrganizationUsers
            .Where(x => x.OrganizationId == organizationId && memberIds.Contains(x.Id) && x.IsActive)
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        if (employees.Count != memberIds.Count)
            throw new ArgumentException("Every team member must be an active employee in this organization.");
        await ValidateRolesAsync(
            organizationId,
            requestedMembers.Where(x => x.TeamRoleId.HasValue).Select(x => x.TeamRoleId!.Value),
            cancellationToken);

        var now = timeProvider.GetUtcNow();
        var team = new OrganizationTeam
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            TeamKey = $"manual:{Guid.NewGuid():N}",
            NormalizedName = normalizedName,
            Name = name,
            Description = Clean(request.Description, 2048),
            LeadOrganizationUserId = lead.Id,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.OrganizationTeams.Add(team);
        foreach (var employeeId in memberIds)
        {
            var requested = requestedMembers.SingleOrDefault(x => x.OrganizationUserId == employeeId);
            await UpsertMembershipCoreAsync(
                team,
                employees[employeeId],
                requested?.TeamRoleId,
                "TeamAdministration",
                actor.Id,
                now,
                cancellationToken);
        }
        await SaveWithConcurrencyAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        await audit.WriteAsync(
            "organization_team.created",
            nameof(OrganizationTeam),
            team.Id,
            $"Created team '{team.Name}'.",
            cancellationToken: cancellationToken);
        return await ReloadAsync(organizationId, team.Id, cancellationToken);
    }

    public async Task<TeamDetailResponse> UpdateAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid teamId,
        UpdateTeamRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await RequireManagerAsync(organizationId, applicationUserId, cancellationToken);
        await using var transaction = await BeginMutationTransactionAsync(cancellationToken);
        var team = await RequireTeamAsync(organizationId, teamId, cancellationToken);
        RequireRevision(team, request.ExpectedRevision);
        var name = Required(request.Name, 160, nameof(request.Name));
        var normalizedName = NormalizeName(name);
        if (await db.OrganizationTeams.AnyAsync(x =>
                x.OrganizationId == organizationId &&
                x.Id != team.Id &&
                x.NormalizedName == normalizedName &&
                x.ArchivedAt == null,
                cancellationToken))
            throw new ArgumentException("An active team with this name already exists.");
        var lead = await RequireActiveEmployeeAsync(
            organizationId, request.LeadOrganizationUserId, cancellationToken);
        var existingLeadMembership = await db.TeamMemberships.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TeamId == team.Id && x.OrganizationUserId == lead.Id, cancellationToken);
        var now = timeProvider.GetUtcNow();
        await UpsertMembershipCoreAsync(
            team,
            lead,
            existingLeadMembership?.TeamRoleId ?? lead.RoleId,
            "TeamAdministration",
            actor.Id,
            now,
            cancellationToken);
        team.Name = name;
        team.NormalizedName = normalizedName;
        team.Description = Clean(request.Description, 2048);
        team.LeadOrganizationUserId = lead.Id;
        Touch(team, now);
        await SaveWithConcurrencyAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        await audit.WriteAsync(
            "organization_team.updated",
            nameof(OrganizationTeam),
            team.Id,
            $"Updated team '{team.Name}'.",
            cancellationToken: cancellationToken);
        return await ReloadAsync(organizationId, team.Id, cancellationToken);
    }

    public async Task<TeamDetailResponse> ArchiveAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid teamId,
        TeamRevisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await RequireManagerAsync(organizationId, applicationUserId, cancellationToken);
        var team = await RequireTeamAsync(organizationId, teamId, cancellationToken);
        RequireRevision(team, request.ExpectedRevision);
        if (team.ArchivedAt is null)
        {
            var now = timeProvider.GetUtcNow();
            team.ArchivedAt = now;
            Touch(team, now);
            var grants = await db.ScopedActionGrants.Where(x =>
                x.OrganizationId == organizationId &&
                x.ScopeKind == GrantScopeKind.Team &&
                x.ScopeId == team.Id &&
                x.RevokedAt == null).ToListAsync(cancellationToken);
            foreach (var grant in grants) grant.RevokedAt = now;
            await SaveWithConcurrencyAsync(cancellationToken);
        }
        await audit.WriteAsync(
            "organization_team.archived",
            nameof(OrganizationTeam),
            team.Id,
            $"Archived team '{team.Name}' by employee {actor.Id:D}.",
            cancellationToken: cancellationToken);
        return await ReloadAsync(organizationId, team.Id, cancellationToken);
    }

    public async Task<TeamDetailResponse> RestoreAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid teamId,
        TeamRevisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await RequireManagerAsync(organizationId, applicationUserId, cancellationToken);
        var team = await RequireTeamAsync(organizationId, teamId, cancellationToken);
        RequireRevision(team, request.ExpectedRevision);
        if (team.ArchivedAt is not null)
        {
            if (await db.OrganizationTeams.AnyAsync(x =>
                    x.OrganizationId == organizationId &&
                    x.Id != team.Id &&
                    x.NormalizedName == team.NormalizedName &&
                    x.ArchivedAt == null,
                    cancellationToken))
                throw new ArgumentException("Another active team now uses this name. Rename the archived team before restoring it.");
            var leadIsActive = await db.CoreOrganizationUsers.AnyAsync(x =>
                x.Id == team.LeadOrganizationUserId &&
                x.OrganizationId == organizationId &&
                x.IsActive,
                cancellationToken);
            if (!leadIsActive)
                throw new InvalidOperationException("Assign an active lead before restoring this team.");
            team.ArchivedAt = null;
            Touch(team, timeProvider.GetUtcNow());
            await SaveWithConcurrencyAsync(cancellationToken);
        }
        await audit.WriteAsync(
            "organization_team.restored",
            nameof(OrganizationTeam),
            team.Id,
            $"Restored team '{team.Name}' by employee {actor.Id:D}; revoked grants were not restored.",
            cancellationToken: cancellationToken);
        return await ReloadAsync(organizationId, team.Id, cancellationToken);
    }

    public async Task<TeamDetailResponse> UpsertMemberAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid teamId,
        Guid organizationUserId,
        UpsertTeamMembershipRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await RequireManagerAsync(organizationId, applicationUserId, cancellationToken);
        await using var transaction = await BeginMutationTransactionAsync(cancellationToken);
        var team = await RequireActiveTeamAsync(organizationId, teamId, cancellationToken);
        RequireRevision(team, request.ExpectedRevision);
        var employee = await RequireActiveEmployeeAsync(organizationId, organizationUserId, cancellationToken);
        await ValidateRolesAsync(
            organizationId,
            request.TeamRoleId.HasValue ? [request.TeamRoleId.Value] : [],
            cancellationToken);
        await UpsertMembershipCoreAsync(
            team,
            employee,
            request.TeamRoleId,
            "TeamAdministration",
            actor.Id,
            timeProvider.GetUtcNow(),
            cancellationToken);
        await EnsureMemberTeamGrantsAsync(
            team, employee, actor.Id, timeProvider.GetUtcNow(), cancellationToken);
        Touch(team, timeProvider.GetUtcNow());
        await SaveWithConcurrencyAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        await audit.WriteAsync(
            "organization_team.member-upserted",
            nameof(TeamMembership),
            employee.Id,
            $"Added or updated an employee in team {team.Id:D}.",
            cancellationToken: cancellationToken);
        return await ReloadAsync(organizationId, team.Id, cancellationToken);
    }

    public async Task<TeamDetailResponse> RemoveMemberAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid teamId,
        Guid organizationUserId,
        TeamRevisionRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireManagerAsync(organizationId, applicationUserId, cancellationToken);
        var team = await RequireActiveTeamAsync(organizationId, teamId, cancellationToken);
        RequireRevision(team, request.ExpectedRevision);
        if (team.LeadOrganizationUserId == organizationUserId)
            throw new InvalidOperationException("Assign another team lead before removing the current lead.");
        var membership = await db.TeamMemberships.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.TeamId == teamId &&
            x.OrganizationUserId == organizationUserId,
            cancellationToken) ?? throw new KeyNotFoundException("The employee is not a member of this team.");
        if (membership.EndedAt is null)
        {
            membership.EndedAt = timeProvider.GetUtcNow();
            var installationId = await db.CoreOrganizationUsers.AsNoTracking()
                .Where(x => x.Id == organizationUserId && x.OrganizationId == organizationId)
                .Select(x => x.AgentInstallationId)
                .SingleOrDefaultAsync(cancellationToken);
            if (installationId.HasValue)
                await TeamAgentGrantProvisioner.RevokeAsync(
                    db, organizationId, installationId.Value, teamId,
                    membership.EndedAt.Value, cancellationToken);
            Touch(team, membership.EndedAt.Value);
            await SaveWithConcurrencyAsync(cancellationToken);
        }
        await audit.WriteAsync(
            "organization_team.member-removed",
            nameof(TeamMembership),
            membership.Id,
            $"Ended membership in team {team.Id:D}.",
            cancellationToken: cancellationToken);
        return await ReloadAsync(organizationId, team.Id, cancellationToken);
    }

    public async Task<Guid> ResolveApprovedTeamAsync(
        Guid organizationId,
        string teamKey,
        string name,
        string description,
        Guid leadOrganizationUserId,
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginMutationTransactionAsync(cancellationToken);
        var key = Required(teamKey, 200, nameof(teamKey)).ToLowerInvariant();
        var teamName = Required(name, 160, nameof(name));
        var lead = await RequireActiveEmployeeAsync(organizationId, leadOrganizationUserId, cancellationToken);
        var team = await db.OrganizationTeams.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.TeamKey == key, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (team is null)
        {
            var normalizedName = NormalizeName(teamName);
            if (await db.OrganizationTeams.AnyAsync(x =>
                    x.OrganizationId == organizationId &&
                    x.NormalizedName == normalizedName &&
                    x.ArchivedAt == null,
                    cancellationToken))
                throw new InvalidOperationException("An active team with the approved name already exists under another team key.");
            team = new OrganizationTeam
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                TeamKey = key,
                NormalizedName = normalizedName,
                Name = teamName,
                Description = Clean(description, 2048),
                LeadOrganizationUserId = lead.Id,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.OrganizationTeams.Add(team);
        }
        else
        {
            var normalizedName = NormalizeName(teamName);
            if (await db.OrganizationTeams.AnyAsync(x =>
                    x.OrganizationId == organizationId &&
                    x.Id != team.Id &&
                    x.NormalizedName == normalizedName &&
                    x.ArchivedAt == null,
                    cancellationToken))
                throw new InvalidOperationException("Another active team already uses the approved name.");
            if (team.ArchivedAt is not null)
                throw new InvalidOperationException("The approved team is archived and cannot receive staffing changes.");
            team.Name = teamName;
            team.NormalizedName = normalizedName;
            team.Description = Clean(description, 2048);
            team.LeadOrganizationUserId = lead.Id;
            Touch(team, now);
        }
        await UpsertMembershipCoreAsync(
            team, lead, lead.RoleId, "ApprovedResourceChange", sourceId, now, cancellationToken);
        await SaveWithConcurrencyAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return team.Id;
    }

    public async Task AssignFromWorkflowAsync(
        Guid organizationId,
        Guid teamId,
        Guid organizationUserId,
        Guid? teamRoleId,
        string sourceType,
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginMutationTransactionAsync(cancellationToken);
        var team = await RequireActiveTeamAsync(organizationId, teamId, cancellationToken);
        var employee = await RequireActiveEmployeeAsync(organizationId, organizationUserId, cancellationToken);
        await ValidateRolesAsync(
            organizationId,
            teamRoleId.HasValue ? [teamRoleId.Value] : [],
            cancellationToken);
        await UpsertMembershipCoreAsync(
            team,
            employee,
            teamRoleId,
            Required(sourceType, 80, nameof(sourceType)),
            sourceId,
            timeProvider.GetUtcNow(),
            cancellationToken);
        await EnsureMemberTeamGrantsAsync(
            team, employee, team.LeadOrganizationUserId,
            timeProvider.GetUtcNow(), cancellationToken);
        Touch(team, timeProvider.GetUtcNow());
        await SaveWithConcurrencyAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

    private async Task EnsureMemberTeamGrantsAsync(
        OrganizationTeam team,
        OrganizationUser employee,
        Guid grantedByOrganizationUserId,
        DateTimeOffset grantedAt,
        CancellationToken cancellationToken)
    {
        if (!employee.AgentInstallationId.HasValue) return;
        await TeamAgentGrantProvisioner.EnsureAsync(
            db,
            team.OrganizationId,
            employee.AgentInstallationId.Value,
            team.Id,
            grantedByOrganizationUserId,
            grantedAt,
            cancellationToken);
    }

    private async Task UpsertMembershipCoreAsync(
        OrganizationTeam team,
        OrganizationUser employee,
        Guid? teamRoleId,
        string sourceType,
        Guid? sourceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (employee.EmployeeType == EmployeeType.Agent)
            await LockAgentAssignmentAsync(employee.Id, cancellationToken);

        var existing = await db.TeamMemberships.SingleOrDefaultAsync(x =>
            x.TeamId == team.Id && x.OrganizationUserId == employee.Id, cancellationToken);
        if (employee.EmployeeType == EmployeeType.Agent)
        {
            var otherTeam = await db.TeamMemberships.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == team.OrganizationId &&
                x.OrganizationUserId == employee.Id &&
                x.TeamId != team.Id,
                cancellationToken);
            if (otherTeam)
                throw new InvalidOperationException(
                    "This AI employee instance has already belonged to another team. Hire a new agent installation and employee identity for this team.");
        }
        if (existing is null)
        {
            db.TeamMemberships.Add(new TeamMembership
            {
                Id = Guid.NewGuid(),
                OrganizationId = team.OrganizationId,
                TeamId = team.Id,
                OrganizationUserId = employee.Id,
                TeamRoleId = teamRoleId,
                ExclusiveAgentEmployeeId = employee.EmployeeType == EmployeeType.Agent ? employee.Id : null,
                SourceType = sourceType,
                SourceId = sourceId,
                JoinedAt = now
            });
        }
        else
        {
            existing.TeamRoleId = teamRoleId;
            existing.SourceType = sourceType;
            existing.SourceId = sourceId;
            existing.EndedAt = null;
            if (employee.EmployeeType == EmployeeType.Agent)
                existing.ExclusiveAgentEmployeeId = employee.Id;
        }
    }

    private IQueryable<OrganizationTeam> TeamQuery(Guid organizationId) =>
        db.OrganizationTeams.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .Include(x => x.LeadOrganizationUser)
            .Include(x => x.Memberships)
                .ThenInclude(x => x.OrganizationUser)
            .Include(x => x.Memberships)
                .ThenInclude(x => x.TeamRole)
            .AsSplitQuery();

    private async Task<TeamDetailResponse> ReloadAsync(
        Guid organizationId,
        Guid teamId,
        CancellationToken cancellationToken)
    {
        var team = await TeamQuery(organizationId).SingleAsync(x => x.Id == teamId, cancellationToken);
        var board = await db.WorkBoards.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.TeamId == teamId && x.ArchivedAt == null)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new { x.Id, x.WorkstreamId })
            .FirstOrDefaultAsync(cancellationToken);
        return new TeamDetailResponse(
            ToSummary(team) with
            {
                WorkstreamId = board?.WorkstreamId,
                BoardId = board?.Id
            },
            board?.WorkstreamId,
            board?.Id);
    }

    private static TeamSummaryResponse ToSummary(OrganizationTeam team)
    {
        var memberships = team.Memberships.Where(x => x.OrganizationUser is not null)
            .OrderByDescending(x => x.OrganizationUserId == team.LeadOrganizationUserId)
            .ThenBy(x => x.EndedAt.HasValue)
            .ThenBy(x => x.TeamRole?.Name)
            .ThenBy(x => x.OrganizationUser!.DisplayName)
            .Select(x => new TeamMembershipResponse(
                x.Id,
                x.OrganizationUserId,
                x.OrganizationUser!.DisplayName,
                x.OrganizationUser.EmployeeType.ToString(),
                x.TeamRoleId,
                x.TeamRole?.Name,
                x.OrganizationUserId == team.LeadOrganizationUserId,
                x.JoinedAt,
                x.EndedAt))
            .ToList();
        var active = memberships.Where(x => x.EndedAt is null).ToList();
        return new TeamSummaryResponse(
            team.Id,
            team.TeamKey,
            team.Name,
            team.Description,
            team.LeadOrganizationUserId,
            team.LeadOrganizationUser?.DisplayName ?? "Unknown",
            team.Revision,
            team.ArchivedAt.HasValue,
            active.Count,
            active.Count(x => x.EmployeeType == EmployeeType.Human.ToString()),
            active.Count(x => x.EmployeeType == EmployeeType.Agent.ToString()),
            memberships);
    }

    private async Task<OrganizationUser> RequireActorAsync(
        Guid organizationId,
        Guid applicationUserId,
        CancellationToken cancellationToken) =>
        await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId &&
            x.IsActive,
            cancellationToken)
        ?? throw new UnauthorizedAccessException("The current user is not an active member of this organization.");

    private async Task<OrganizationUser> RequireManagerAsync(
        Guid organizationId,
        Guid applicationUserId,
        CancellationToken cancellationToken)
    {
        var actor = await RequireActorAsync(organizationId, applicationUserId, cancellationToken);
        if (actor.PermissionLevel < OrganizationPermissionLevel.Manager)
            throw new UnauthorizedAccessException("Only organization managers and owners may manage teams.");
        return actor;
    }

    private async Task<OrganizationUser> RequireActiveEmployeeAsync(
        Guid organizationId,
        Guid organizationUserId,
        CancellationToken cancellationToken) =>
        await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.Id == organizationUserId && x.OrganizationId == organizationId && x.IsActive,
            cancellationToken)
        ?? throw new ArgumentException("The employee must be active and belong to this organization.");

    private async Task<OrganizationTeam> RequireTeamAsync(
        Guid organizationId,
        Guid teamId,
        CancellationToken cancellationToken) =>
        await db.OrganizationTeams.SingleOrDefaultAsync(x =>
            x.Id == teamId && x.OrganizationId == organizationId, cancellationToken)
        ?? throw new KeyNotFoundException("The team was not found.");

    private async Task<OrganizationTeam> RequireActiveTeamAsync(
        Guid organizationId,
        Guid teamId,
        CancellationToken cancellationToken)
    {
        var team = await RequireTeamAsync(organizationId, teamId, cancellationToken);
        if (team.ArchivedAt is not null)
            throw new InvalidOperationException("The team is archived.");
        return team;
    }

    private async Task ValidateRolesAsync(
        Guid organizationId,
        IEnumerable<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        var ids = roleIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var count = await db.CoreRoles.CountAsync(x =>
            x.OrganizationId == organizationId && ids.Contains(x.Id), cancellationToken);
        if (count != ids.Count)
            throw new ArgumentException("Every team role must belong to this organization.");
    }

    private static void RequireRevision(OrganizationTeam team, long expected)
    {
        if (team.Revision != expected)
            throw new DbUpdateConcurrencyException(
                $"The team changed after it was loaded. Expected revision {expected}, current revision is {team.Revision}.");
    }

    private static void Touch(OrganizationTeam team, DateTimeOffset now)
    {
        team.Revision++;
        team.UpdatedAt = now;
    }

    private async Task SaveWithConcurrencyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw;
        }
        catch (DbUpdateException exception)
        {
            throw new InvalidOperationException(
                "The team change conflicted with another team or membership update.", exception);
        }
    }

    private async Task<IDbContextTransaction?> BeginMutationTransactionAsync(
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
            return null;

        return await db.Database.BeginTransactionAsync(cancellationToken);
    }

    private static async Task CommitAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    private async Task LockAgentAssignmentAsync(
        Guid employeeId,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational() ||
            !string.Equals(
                db.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
            return;

        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "AI team assignment requires an active database transaction.");

        // The transaction-scoped lock serializes every team assignment for this employee.
        // The lifetime-unique membership key remains the final fail-closed constraint.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({employeeId.ToString("D")}, 0))",
            cancellationToken);
    }

    private static string Required(string? value, int maximum, string field)
    {
        var result = value?.Trim();
        if (string.IsNullOrWhiteSpace(result))
            throw new ArgumentException($"{field} is required.");
        if (result.Length > maximum)
            throw new ArgumentException($"{field} may contain at most {maximum} characters.");
        return result;
    }

    private static string Clean(string? value, int maximum)
    {
        var result = value?.Trim() ?? string.Empty;
        if (result.Length > maximum)
            throw new ArgumentException($"The value may contain at most {maximum} characters.");
        return result;
    }

    private static string NormalizeName(string value) =>
        string.Join(' ', value.Trim().Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
