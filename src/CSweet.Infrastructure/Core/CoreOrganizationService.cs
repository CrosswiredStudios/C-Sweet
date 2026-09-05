using CSweet.Domain.Setup;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Core;

public sealed class CoreOrganizationService : ICoreOrganizationService
{
    private readonly CSweetDbContext _dbContext;
    private readonly IAuditEventWriter _auditEventWriter;
    private readonly IRoleService _roleService;
    private readonly IOrganizationDataPurgeService? _purgeService;
    private readonly ILogger<CoreOrganizationService>? _logger;

    public CoreOrganizationService(
        CSweetDbContext dbContext,
        IAuditEventWriter auditEventWriter,
        IRoleService roleService,
        IOrganizationDataPurgeService? purgeService = null,
        ILogger<CoreOrganizationService>? logger = null)
    {
        _dbContext = dbContext;
        _auditEventWriter = auditEventWriter;
        _roleService = roleService;
        _purgeService = purgeService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<OrganizationResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        var organizations = await _dbContext.CoreOrganizations
            .OrderBy(x => x.Name)
            .Select(x => x.ToResponse())
            .ToListAsync(cancellationToken);
        var assigned = await _dbContext.LeadershipAssignments.AsNoTracking()
            .Where(x => x.PositionKey == "chief-of-staff" && x.EndsAt == null)
            .Select(x => x.OrganizationId)
            .ToHashSetAsync(cancellationToken);
        return organizations.Select(x => x with { NeedsChiefSetup = !assigned.Contains(x.Id) }).ToList();
    }

    public async Task<OrganizationResponse?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var org = await _dbContext.CoreOrganizations
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (org is null) return null;
        var hasChief = await _dbContext.LeadershipAssignments.AsNoTracking().AnyAsync(
            x => x.OrganizationId == id && x.PositionKey == "chief-of-staff" && x.EndsAt == null, cancellationToken);
        return org.ToResponse() with { NeedsChiefSetup = !hasChief };
    }

    public async Task<CoreActionResponse> CreateAsync(CreateOrganizationRequest request, CancellationToken cancellationToken = default, Guid? applicationUserId = null)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Failure("validation_error", "Organization name is required.");
        }

        var now = DateTimeOffset.UtcNow;
        var org = new Organization
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Industry = TrimOrNull(request.Industry),
            Mission = TrimOrNull(request.Mission),
            Stage = TrimOrNull(request.Stage),
            PrimaryGoal = TrimOrNull(request.PrimaryGoal),
            ConstraintsJson = request.ConstraintsJson,
            Status = OrganizationStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.CoreOrganizations.Add(org);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await CSweet.Infrastructure.SourceControl.InternalGitProvisioningDefaults.EnsureAsync(_dbContext, org.Id, cancellationToken);

        // Seed default roles for new organization
        await _roleService.EnsureDefaultsAsync(org.Id, cancellationToken);
        await SeedOwnerAsync(org.Id, applicationUserId, cancellationToken);

        await _auditEventWriter.WriteAsync(
            "organization.created",
            "Organization",
            org.Id,
            $"Organization '{org.Name}' created.",
            cancellationToken: cancellationToken);

        return new CoreActionResponse(true, null, "Organization created successfully.", Organization: org.ToResponse());
    }

    public async Task<CoreActionResponse> UpdateAsync(Guid id, UpdateOrganizationRequest request, CancellationToken cancellationToken = default)
    {
        var org = await _dbContext.CoreOrganizations
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (org is null)
        {
            return Failure("not_found", "Organization was not found.");
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
            org.Name = request.Name.Trim();
        if (request.Industry is not null)
            org.Industry = TrimOrNull(request.Industry);
        if (request.Mission is not null)
            org.Mission = TrimOrNull(request.Mission);
        if (request.Stage is not null)
            org.Stage = TrimOrNull(request.Stage);
        if (request.PrimaryGoal is not null)
            org.PrimaryGoal = TrimOrNull(request.PrimaryGoal);
        if (request.ConstraintsJson is not null)
            org.ConstraintsJson = request.ConstraintsJson;

        org.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditEventWriter.WriteAsync(
            "organization.updated",
            "Organization",
            org.Id,
            $"Organization '{org.Name}' updated.",
            cancellationToken: cancellationToken);

        return new CoreActionResponse(true, null, "Organization updated successfully.", Organization: org.ToResponse());
    }

    public async Task<CoreActionResponse> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var org = await _dbContext.CoreOrganizations
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (org is null)
        {
            return Failure("not_found", "Organization was not found.");
        }

        var name = org.Name;
        try
        {
            if (_purgeService is not null)
                await _purgeService.PurgeAsync(id, cancellationToken);
            else
            {
                _dbContext.CoreOrganizations.Remove(org);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (OrganizationDeletionException exception)
        {
            return Failure("deletion_failed", exception.Message);
        }
        catch (DbUpdateException exception)
        {
            _logger?.LogError(exception, "Could not delete organization {OrganizationId}.", id);
            return Failure("deletion_failed", "The business could not be deleted because its data cleanup did not complete. Retry the deletion.");
        }

        try
        {
            await _auditEventWriter.AppendAsync(
                new AuditEventWriteRequest(
                    "organization.deleted",
                    OrganizationId: null,
                    EntityType: "Organization",
                    EntityId: org.Id,
                    Summary: $"Organization '{name}' deleted.",
                    UseAmbientOrganization: false),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The local purge has already committed. Do not report a failed deletion and
            // leave the client displaying a business that no longer exists.
            _logger?.LogError(exception, "Organization {OrganizationId} was deleted, but its audit event could not be written.", id);
        }

        return new CoreActionResponse(true, null, "Organization deleted successfully.");
    }

    static string? TrimOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task SeedOwnerAsync(Guid organizationId, Guid? applicationUserId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var ceoRoleId = await _dbContext.CoreRoles
            .Where(x => x.OrganizationId == organizationId && x.Name == "CEO")
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);

        var account = applicationUserId.HasValue
            ? await _dbContext.Users
                .Where(x => x.Id == applicationUserId.Value)
                .Select(x => new { x.DisplayName, x.Email })
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        var ceo = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ApplicationUserId = applicationUserId,
            RoleId = ceoRoleId,
            DisplayName = account?.DisplayName ?? "Owner",
            Email = account?.Email,
            EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Owner,
            CreatedAt = now
        };

        _dbContext.CoreOrganizationUsers.Add(ceo);
        _dbContext.LeadershipAssignments.Add(new LeadershipAssignment
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            OrganizationUserId = ceo.Id,
            PositionKey = LeadershipPositionKeys.ChiefExecutiveOfficer,
            StartsAt = now
        });
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    static CoreActionResponse Failure(string errorCode, string message) =>
        new CoreActionResponse(false, errorCode, message);
}
