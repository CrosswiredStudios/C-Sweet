using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CSweet.Infrastructure.Core;

public sealed class EmployeeDetailsService(
    CSweetDbContext db,
    IEmployeeHierarchyAccessService hierarchy,
    ITeamService teams,
    IAgentAttentionInvalidationService? attention = null) : IEmployeeDetailsService
{
    public async Task<EmployeeDetailsResponse> GetAsync(Guid organizationId, Guid employeeId,
        Guid applicationUserId, CancellationToken cancellationToken = default)
    {
        var actor = await RequireActorAsync(organizationId, applicationUserId, cancellationToken);
        var employee = await EmployeeQuery(organizationId).SingleOrDefaultAsync(x =>
            x.Id == employeeId && x.IsActive, cancellationToken)
            ?? throw new KeyNotFoundException("The employee was not found.");
        var descendants = await hierarchy.GetSelfAndDescendantsAsync(organizationId, actor.Id,
            cancellationToken);
        var ownerOverride = actor.PermissionLevel == OrganizationPermissionLevel.Owner;
        var sensitive = descendants.Contains(employee.Id);
        var self = actor.Id == employee.Id;
        var directory = await teams.ListAsync(organizationId, applicationUserId, false, cancellationToken);
        var employeeTeams = directory.Teams.Where(x =>
            x.LeadOrganizationUserId == employee.Id ||
            x.Members.Any(m => m.OrganizationUserId == employee.Id && m.EndedAt == null)).ToList();
        var reports = await EmployeeQuery(organizationId).Where(x =>
            x.ReportsToOrganizationUserId == employee.Id && x.IsActive)
            .OrderBy(x => x.DisplayName).ToListAsync(cancellationToken);
        var manager = employee.ReportsToOrganizationUserId.HasValue
            ? await EmployeeQuery(organizationId).SingleOrDefaultAsync(x =>
                x.Id == employee.ReportsToOrganizationUserId.Value, cancellationToken)
            : null;
        return new EmployeeDetailsResponse(employee.ToResponse(), employee.Role?.ToResponse(),
            employee.Worker?.ToResponse(), manager?.ToResponse(), reports.Select(x => x.ToResponse()).ToList(),
            employeeTeams, new EmployeeDetailsPermissions(sensitive, self || ownerOverride || sensitive,
                ownerOverride || (!self && descendants.Contains(employee.Id)), sensitive,
                self, self && employee.EmployeeType == EmployeeType.Human));
    }

    public async Task<EmployeeDetailsResponse> UpdateProfileAsync(Guid organizationId, Guid employeeId,
        Guid applicationUserId, UpdateEmployeeProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await RequireActorAsync(organizationId, applicationUserId, cancellationToken);
        var employee = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.Id == employeeId && x.OrganizationId == organizationId && x.IsActive, cancellationToken)
            ?? throw new KeyNotFoundException("The employee was not found.");
        if (employee.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException("The employee profile changed since it was loaded.");
        var descendants = await hierarchy.GetSelfAndDescendantsAsync(organizationId, actor.Id,
            cancellationToken);
        var ownerOverride = actor.PermissionLevel == OrganizationPermissionLevel.Owner;
        var self = actor.Id == employee.Id;
        var canManage = ownerOverride || (!self && descendants.Contains(employee.Id));
        if (!self && !canManage)
            throw new UnauthorizedAccessException("This employee is outside your reporting hierarchy.");
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 160)
            throw new ArgumentException("Display name is required and cannot exceed 160 characters.");
        if ((request.Email?.Trim().Length ?? 0) > 320)
            throw new ArgumentException("Email cannot exceed 320 characters.");
        if (!canManage && (request.RoleId != employee.RoleId ||
            request.ReportsToOrganizationUserId != employee.ReportsToOrganizationUserId))
            throw new UnauthorizedAccessException("Employees may edit only their own name and email.");
        if (canManage && request.RoleId.HasValue && !await db.CoreRoles.AsNoTracking().AnyAsync(x =>
            x.Id == request.RoleId && x.OrganizationId == organizationId, cancellationToken))
            throw new ArgumentException("The role must belong to this organization.");
        if (canManage && request.ReportsToOrganizationUserId.HasValue)
        {
            var proposedManager = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == request.ReportsToOrganizationUserId.Value && x.OrganizationId == organizationId &&
                x.IsActive, cancellationToken)
                ?? throw new ArgumentException("The manager must be active in this organization.");
            var employeeSubtree = await hierarchy.GetSelfAndDescendantsAsync(organizationId, employee.Id,
                cancellationToken);
            if (employeeSubtree.Count == 0 || employeeSubtree.Contains(proposedManager.Id))
                throw new ArgumentException("This reporting change would create a hierarchy cycle.");
            if (!ownerOverride && !descendants.Contains(proposedManager.Id))
                throw new UnauthorizedAccessException("The new manager is outside your authority subtree.");
        }
        else if (canManage && !ownerOverride && !request.ReportsToOrganizationUserId.HasValue &&
            employee.ReportsToOrganizationUserId.HasValue)
        {
            throw new UnauthorizedAccessException("Only an organization owner may move an employee outside the reporting tree.");
        }
        var previousManagerId = employee.ReportsToOrganizationUserId;
        var previousRoleId = employee.RoleId;
        employee.DisplayName = request.DisplayName.Trim();
        employee.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        if (canManage)
        {
            employee.RoleId = request.RoleId;
            employee.ReportsToOrganizationUserId = request.ReportsToOrganizationUserId;
        }
        employee.Revision++;
        var materialWorkforceChange = previousManagerId != employee.ReportsToOrganizationUserId ||
                                      previousRoleId != employee.RoleId;
        var managerTargets = materialWorkforceChange
            ? await db.CoreOrganizationUsers.AsNoTracking()
                .Where(x => x.IsActive && x.AgentInstallationId.HasValue &&
                    ((previousManagerId.HasValue && x.Id == previousManagerId.Value) ||
                     (employee.ReportsToOrganizationUserId.HasValue && x.Id == employee.ReportsToOrganizationUserId.Value)))
                .Select(x => x.AgentInstallationId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken)
            : [];
        var workforceEventId = Guid.NewGuid();
        foreach (var target in managerTargets)
        {
            db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, EventType = WorkforceEvents.Changed,
                DataJson = JsonSerializer.Serialize(new WorkforceChangedEvent(
                    organizationId, employee.Id,
                    previousManagerId != employee.ReportsToOrganizationUserId ? "ReportingLineChanged" : "RoleChanged",
                    [], previousManagerId, employee.ReportsToOrganizationUserId, DateTimeOffset.UtcNow)),
                IdempotencyKey = $"workforce-changed:{employee.Id:N}:profile:{employee.Revision}:{target:N}",
                TargetInstallationId = target, Status = AgentPlatformEventOutboxStatus.Pending,
                NextAttemptAt = DateTimeOffset.UtcNow, OccurredAt = DateTimeOffset.UtcNow
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        if (attention is not null && managerTargets.Count > 0)
            await attention.InvalidateAsync(managerTargets, "workforce.profile-changed", workforceEventId, cancellationToken);
        return await GetAsync(organizationId, employeeId, applicationUserId, cancellationToken);
    }

    private async Task<OrganizationUser> RequireActorAsync(Guid organizationId, Guid applicationUserId,
        CancellationToken token) =>
        await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId && x.IsActive, token)
        ?? throw new UnauthorizedAccessException("You are not an active member of this organization.");

    private IQueryable<OrganizationUser> EmployeeQuery(Guid organizationId) =>
        db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .Include(x => x.Role).Include(x => x.Worker).Include(x => x.AgentInstallation!)
            .ThenInclude(x => x.PackageVersion);
}
