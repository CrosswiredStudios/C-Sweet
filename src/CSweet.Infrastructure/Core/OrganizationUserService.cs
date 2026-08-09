using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using CSweet.Domain.Communications;
using CSweet.Application.Communications;
using CSweet.Infrastructure.Communications;
using CSweet.Application.WorkManagement;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Core;

public sealed class OrganizationUserService : IOrganizationUserService
{
    private readonly CSweetDbContext _dbContext;
    private readonly IAuditEventWriter _auditEventWriter;
    private readonly IAgentCommunicationOnboardingService _agentOnboarding;
    private readonly IAgentRuntimeManager? _agentRuntimeManager;
    private readonly ILogger<OrganizationUserService>? _logger;
    private readonly IPersonalTodoService _personalTodo;

    public OrganizationUserService(CSweetDbContext dbContext, IAuditEventWriter auditEventWriter,
        IAgentCommunicationOnboardingService? agentOnboarding = null,
        IAgentRuntimeManager? agentRuntimeManager = null,
        ILogger<OrganizationUserService>? logger = null,
        IPersonalTodoService? personalTodo = null)
    {
        _dbContext = dbContext;
        _auditEventWriter = auditEventWriter;
        _agentOnboarding = agentOnboarding ?? new AgentCommunicationOnboardingService(dbContext);
        _agentRuntimeManager = agentRuntimeManager;
        _logger = logger;
        _personalTodo = personalTodo ?? new PersonalTodoService(dbContext, TimeProvider.System);
    }

    public async Task<IReadOnlyList<OrganizationUserResponse>> ListByOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var users = await _dbContext.CoreOrganizationUsers
            .Where(x => x.OrganizationId == organizationId && x.IsActive)
            .Include(x => x.AgentInstallation!)
                .ThenInclude(x => x.Grant)
            .Include(x => x.AgentInstallation!)
                .ThenInclude(x => x.PackageVersion)
            .OrderBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);
        return users.Select(x => x.ToResponse()).ToList();
    }

    public async Task<OrganizationUserResponse?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.CoreOrganizationUsers
            .Include(x => x.AgentInstallation!)
                .ThenInclude(x => x.Grant)
            .Include(x => x.AgentInstallation!)
                .ThenInclude(x => x.PackageVersion)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        return user?.ToResponse();
    }

    public async Task<CoreActionResponse> CreateAsync(Guid organizationId, CreateOrganizationUserRequest request,
        CancellationToken cancellationToken = default, Guid? hiringApplicationUserId = null,
        string hiringSource = "Manual")
    {
        if (!await _dbContext.CoreOrganizations.AnyAsync(x => x.Id == organizationId, cancellationToken))
        {
            return Failure("organization_not_found", "Organization was not found.");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Failure("validation_error", "Display name is required.");
        }

        if (!Enum.IsDefined(typeof(EmployeeType), request.EmployeeType))
        {
            return Failure("validation_error", "Employee type is invalid.");
        }

        if (request.EmployeeType == (int)EmployeeType.Agent &&
            !request.AgentInstallationId.HasValue && !request.AgentDefinitionId.HasValue)
        {
            return Failure("agent_definition_required", "An available installed agent definition must be selected for an agent employee.");
        }
        if (request.EmployeeType == (int)EmployeeType.Agent && !request.ReportsToOrganizationUserId.HasValue)
        {
            return Failure("manager_required", "A managing employee must be selected for an agent employee.");
        }

        AgentInstallation? hiredInstallation = null;
        if (request.AgentDefinitionId.HasValue)
        {
            var definition = await _dbContext.AgentDefinitions
                .Include(x => x.Configuration)
                .Include(x => x.PackageVersion)
                .SingleOrDefaultAsync(x => x.Id == request.AgentDefinitionId && x.IsAvailableForHire,
                    cancellationToken);
            if (definition is null || definition.PackageVersion?.Status != AgentPackageVersionStatus.Built ||
                string.IsNullOrWhiteSpace(definition.PackageVersion.PackageDigest) ||
                string.IsNullOrWhiteSpace(definition.PackageVersion.ArtifactSignature))
            {
                return Failure("agent_definition_unavailable",
                    "The selected agent definition is not built, signed, configured, and available for hire.");
            }

            hiredInstallation = CreateHiredInstallation(definition, organizationId, DateTimeOffset.UtcNow);
            _dbContext.AgentInstallations.Add(hiredInstallation);
            request = request with { AgentInstallationId = hiredInstallation.Id };
        }

        if (request.AgentInstallationId.HasValue)
        {
            var installation = hiredInstallation ?? await _dbContext.AgentInstallations
                .Include(x => x.PackageVersion)
                .Include(x => x.Grant)
                .SingleOrDefaultAsync(
                    x => x.Id == request.AgentInstallationId && x.IsEnabled,
                    cancellationToken);
            if (installation is null)
            {
                return Failure("invalid_agent_instance", "The selected agent installation is not available.");
            }

            if (await _dbContext.CoreOrganizationUsers.AnyAsync(
                x => x.AgentInstallationId == request.AgentInstallationId,
                cancellationToken))
            {
                return Failure("agent_instance_in_use", "The selected agent installation already belongs to another employee.");
            }

            var organizationKey = organizationId.ToString("D");
            if (!string.Equals(installation.BusinessId, organizationKey, StringComparison.OrdinalIgnoreCase))
            {
                return Failure("invalid_agent_instance",
                    "Agent installations are business-scoped and cannot be reassigned between organizations.");
            }
        }

        if (request.ReportsToOrganizationUserId.HasValue)
        {
            var managerExists = await _dbContext.CoreOrganizationUsers
                .AnyAsync(
                    x => x.Id == request.ReportsToOrganizationUserId &&
                         x.OrganizationId == organizationId &&
                         x.IsActive,
                    cancellationToken);

            if (!managerExists)
            {
                return Failure("invalid_manager", "Reporting manager must be an active employee in the same organization.");
            }
        }

        var managedUserIds = (request.ManagedOrganizationUserIds ?? [])
            .Distinct()
            .ToArray();

        if (request.ReportsToOrganizationUserId.HasValue && managedUserIds.Contains(request.ReportsToOrganizationUserId.Value))
        {
            return Failure("invalid_hierarchy", "An employee cannot both manage and report to the same person.");
        }

        var managedUsers = managedUserIds.Length == 0
            ? []
            : await _dbContext.CoreOrganizationUsers
                .Where(x => managedUserIds.Contains(x.Id) && x.OrganizationId == organizationId)
                .ToListAsync(cancellationToken);

        if (managedUsers.Count != managedUserIds.Length)
        {
            return Failure("invalid_subordinate", "Every managed employee must belong to the same organization.");
        }

        if (request.RoleId.HasValue)
        {
            var roleExists = await _dbContext.CoreRoles
                .AnyAsync(x => x.Id == request.RoleId && x.OrganizationId == organizationId, cancellationToken);

            if (!roleExists)
            {
                return Failure("invalid_role", "Role must belong to the same organization.");
            }
        }

        if (request.WorkerId.HasValue)
        {
            var worker = await _dbContext.CoreWorkers
                .SingleOrDefaultAsync(x => x.Id == request.WorkerId && (x.OrganizationId == organizationId || x.OrganizationId == null), cancellationToken);

            if (worker is null)
            {
                return Failure("invalid_worker", "Worker must belong to the same organization or be global.");
            }

            if (request.EmployeeType == (int)EmployeeType.Agent &&
                (!worker.IsEnabled || !IsAgentWorkerType(worker.WorkerType)))
            {
                return Failure("invalid_agent", "The selected worker is not an available agent.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        await using var transaction = _dbContext.Database.IsRelational() &&
            _dbContext.Database.CurrentTransaction is null
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var user = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ReportsToOrganizationUserId = request.ReportsToOrganizationUserId,
            RoleId = request.RoleId,
            WorkerId = request.WorkerId,
            AgentInstallationId = request.AgentInstallationId,
            DisplayName = request.DisplayName.Trim(),
            Email = TrimOrNull(request.Email),
            EmployeeType = (EmployeeType)request.EmployeeType,
            PermissionLevel = (OrganizationPermissionLevel)request.PermissionLevel,
            CreatedAt = now
        };

        _dbContext.CoreOrganizationUsers.Add(user);
        if (user.EmployeeType == EmployeeType.Agent)
        {
            var connectionIds = await ActiveCommunicationConnectionIdsAsync(organizationId, cancellationToken);
            _dbContext.CommunicationDeliveries.AddRange(connectionIds.Select(connectionId =>
                CreateEmployeeDelivery(user, connectionId, CommunicationDeliveryKind.ProvisionEmployee, now)));
        }
        foreach (var managedUser in managedUsers)
        {
            managedUser.ReportsToOrganizationUserId = user.Id;
        }
        AgentCommunicationOnboardingResult? onboarding = null;
        if (user.EmployeeType == EmployeeType.Agent)
        {
            var lifecycleReady = hiredInstallation?.SetupState == PluginSetupState.Ready ||
                await _dbContext.AgentInstallations.AnyAsync(x =>
                    x.Id == user.AgentInstallationId!.Value && x.SetupState == PluginSetupState.Ready,
                    cancellationToken);
            onboarding = await _agentOnboarding.EnsureAsync(
                organizationId,
                user,
                hiringApplicationUserId,
                queueLifecycleEvent: lifecycleReady,
                cancellationToken: cancellationToken);
            if (!onboarding.Succeeded) return Failure(onboarding.ErrorCode!, onboarding.Message);
        }
        var hiringOrganizationUserId = hiringApplicationUserId.HasValue
            ? await _dbContext.CoreOrganizationUsers.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId &&
                            x.ApplicationUserId == hiringApplicationUserId &&
                            x.IsActive)
                .Select(x => (Guid?)x.Id)
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        var roleTitle = request.RoleId.HasValue
            ? await _dbContext.CoreRoles.AsNoTracking()
                .Where(x => x.Id == request.RoleId && x.OrganizationId == organizationId)
                .Select(x => x.Name)
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        var hiredEventId = Guid.NewGuid();
        var hiredEvent = new EmployeeHiredEvent(
            organizationId,
            user.Id,
            user.EmployeeType.ToString(),
            user.RoleId,
            roleTitle,
            user.AgentInstallationId,
            user.WorkerId,
            user.ReportsToOrganizationUserId,
            hiringOrganizationUserId,
            string.IsNullOrWhiteSpace(hiringSource) ? "Manual" : hiringSource.Trim(),
            now);
        _dbContext.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
        {
            Id = hiredEventId,
            OrganizationId = organizationId,
            EventType = HiringEvents.EmployeeHired,
            DataJson = JsonSerializer.Serialize(hiredEvent),
            IdempotencyKey = $"employee-hired:{user.Id:D}",
            Status = AgentPlatformEventOutboxStatus.Pending,
            NextAttemptAt = now,
            OccurredAt = now
        });
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (user.EmployeeType == EmployeeType.Agent)
            await _personalTodo.EnsureBoardAsync(organizationId, user.Id, cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        if (onboarding is not null)
        {
            _logger?.LogInformation(
                "Persisted agent hire onboarding event {OnboardingEventId} for organization {OrganizationId}, employee {AgentOrganizationUserId}, installation {InstallationId}, and conversation {ConversationId}.",
                onboarding.EventId,
                organizationId,
                user.Id,
                user.AgentInstallationId,
                onboarding.ConversationId);
        }

        if (user.AgentInstallationId.HasValue && _agentRuntimeManager is not null &&
            await _dbContext.AgentSchedules.AsNoTracking().AnyAsync(x =>
                x.AgentInstallationId == user.AgentInstallationId.Value && x.IsEnabled &&
                x.ActivationMode == ActivationMode.AlwaysOn, cancellationToken))
        {
            try
            {
                var queued = await _agentRuntimeManager.EnsureRuntimeQueuedAsync(
                    user.AgentInstallationId.Value,
                    "Started after the always-on agent was hired and committed.",
                    interactive: false,
                    cancellationToken);
                _logger?.LogInformation(
                    "Requested the permitted always-on runtime after hire for event {OnboardingEventId}, organization {OrganizationId}, employee {AgentOrganizationUserId}, installation {InstallationId}. New runtime queued: {RuntimeQueued}.",
                    onboarding?.EventId,
                    organizationId,
                    user.Id,
                    user.AgentInstallationId,
                    queued);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger?.LogWarning(
                    exception,
                    "Could not queue the initial onboarding runtime for agent employee {OrganizationUserId} installation {InstallationId}.",
                    user.Id,
                    user.AgentInstallationId.Value);
            }
        }

        await _auditEventWriter.WriteAsync(
            "organization_user.created",
            "OrganizationUser",
            user.Id,
            $"User '{user.DisplayName}' added to organization {organizationId}.",
            cancellationToken: cancellationToken);

        return new CoreActionResponse(
            true,
            null,
            "User added successfully.",
            OrganizationUser: user.ToResponse() with { InitialConversationId = onboarding?.ConversationId });
    }

    public async Task<CoreActionResponse> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.CoreOrganizationUsers
            .Include(x => x.AgentInstallation!)
                .ThenInclude(x => x.Grant)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (user is null)
        {
            return Failure("not_found", "User was not found.");
        }

        if (user.ApplicationUserId.HasValue)
        {
            return Failure("cannot_delete_self", "The administrator membership cannot be removed.");
        }

        var name = user.DisplayName;
        var installationId = user.AgentInstallationId;
        if (await _dbContext.OrganizationTeams.AnyAsync(x =>
                x.OrganizationId == user.OrganizationId &&
                x.LeadOrganizationUserId == user.Id &&
                x.ArchivedAt == null,
                cancellationToken))
        {
            return Failure(
                "active_team_lead",
                "Assign another lead or archive every team led by this employee before removing them.");
        }
        var directReports = await _dbContext.CoreOrganizationUsers
            .Where(x => x.ReportsToOrganizationUserId == id)
            .ToListAsync(cancellationToken);
        foreach (var directReport in directReports)
        {
            directReport.ReportsToOrganizationUserId = null;
        }

        var now = DateTimeOffset.UtcNow;
        var memberships = await _dbContext.TeamMemberships
            .Where(x => x.OrganizationId == user.OrganizationId &&
                        x.OrganizationUserId == user.Id &&
                        x.EndedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var membership in memberships) membership.EndedAt = now;
        var membershipTeamIds = memberships.Select(x => x.TeamId).Distinct().ToList();
        var membershipTeams = await _dbContext.OrganizationTeams
            .Where(x => membershipTeamIds.Contains(x.Id))
            .ToListAsync(cancellationToken);
        foreach (var team in membershipTeams)
        {
            team.Revision++;
            team.UpdatedAt = now;
        }
        var teamScopedGrants = await _dbContext.ScopedActionGrants.Where(x =>
                x.OrganizationId == user.OrganizationId &&
                x.ScopeKind == CSweet.Domain.Security.GrantScopeKind.Team &&
                x.RevokedAt == null &&
                ((x.SubjectKind == CSweet.Domain.Security.GrantSubjectKind.OrganizationUser &&
                  x.SubjectId == user.Id) ||
                 (installationId.HasValue &&
                  x.SubjectKind == CSweet.Domain.Security.GrantSubjectKind.AgentInstallation &&
                  x.SubjectId == installationId.Value)))
            .ToListAsync(cancellationToken);
        foreach (var grant in teamScopedGrants) grant.RevokedAt = now;
        user.IsActive = false;
        user.ArchivedAt = now;
        user.AgentInstallationId = null;
        if (user.EmployeeType == EmployeeType.Agent)
        {
            var protectedChats = await _dbContext.CoreConversations
                .Where(x => x.AgentOrganizationUserId == user.Id && x.IsDeletionProtected && x.ArchivedAt == null)
                .ToListAsync(cancellationToken);
            foreach (var chat in protectedChats)
            {
                chat.ArchivedAt = now;
                chat.UpdatedAt = now;
            }
            var connectionIds = await ActiveCommunicationConnectionIdsAsync(user.OrganizationId, cancellationToken);
            _dbContext.CommunicationDeliveries.AddRange(connectionIds.Select(connectionId =>
                CreateEmployeeDelivery(user, connectionId, CommunicationDeliveryKind.ArchiveEmployee, now)));
        }
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (user.EmployeeType == EmployeeType.Agent)
        {
            _logger?.LogInformation(
                "Archived agent employee {AgentOrganizationUserId} in organization {OrganizationId} and detached installation {InstallationId}. A later rehire will create a new employee and onboarding event.",
                user.Id,
                user.OrganizationId,
                installationId);
        }

        await _auditEventWriter.WriteAsync(
            "organization_user.deleted",
            "OrganizationUser",
            user.Id,
            $"User '{name}' removed from organization.",
            cancellationToken: cancellationToken);

        return new CoreActionResponse(true, null, "User archived successfully.");
    }

    public async Task<CoreActionResponse> UpdateRoleAsync(
        Guid organizationId,
        Guid id,
        UpdateOrganizationUserRoleRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.CoreOrganizationUsers
            .SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId, cancellationToken);
        if (user is null)
        {
            return Failure("not_found", "User was not found.");
        }

        if (request.RoleId.HasValue && !await _dbContext.CoreRoles.AnyAsync(
                x => x.Id == request.RoleId.Value && x.OrganizationId == organizationId,
                cancellationToken))
        {
            return Failure("invalid_role", "Role must belong to the same organization.");
        }

        user.RoleId = request.RoleId;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _auditEventWriter.WriteAsync(
            "organization_user.role_updated",
            "OrganizationUser",
            user.Id,
            $"User '{user.DisplayName}' changed company role.",
            cancellationToken: cancellationToken);

        return new CoreActionResponse(true, null, "Role updated successfully.", OrganizationUser: user.ToResponse());
    }

    internal static AgentInstallation CreateHiredInstallation(
        AgentDefinition definition,
        Guid organizationId,
        DateTimeOffset now)
    {
        var manifest = AgentConfigurationRules.DeserializeManifest(definition.PackageVersion!.ManifestJson);
        var needsSetup = manifest.Setup?.Required == true;
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(),
            AgentDefinitionId = definition.Id,
            PackageVersionId = definition.PackageVersionId,
            BusinessId = organizationId.ToString("D"),
            Scope = PluginInstallationScope.Organization,
            IsEnabled = true,
            SetupState = needsSetup ? PluginSetupState.NeedsSetup : PluginSetupState.Ready,
            SetupFlowId = needsSetup ? manifest.Setup!.EntryFlow : null,
            SetupStepId = needsSetup
                ? manifest.Setup!.Flows.First(x => x.Id == manifest.Setup.EntryFlow).Steps.First().Id
                : null,
            DesiredConfigurationRevision = 1,
            AppliedConfigurationRevision = 0,
            ConfigurationSyncStatus = AgentConfigurationSyncStatus.PendingNextStart,
            CreatedAt = now,
            UpdatedAt = now
        };
        installation.InstallationKey = installation.Id;
        installation.Grant = new AgentInstallationGrant
        {
            Id = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            NetworkAccessJson = definition.DefaultNetworkAccessJson,
            ProvidedCapabilitiesJson = definition.DefaultProvidedCapabilitiesJson,
            RequiredCapabilitiesJson = definition.DefaultRequiredCapabilitiesJson,
            EventSubscriptionsJson = definition.DefaultEventSubscriptionsJson,
            ResourceLimitsJson = JsonSerializer.Serialize(new
            {
                MaxRuntimeSeconds = definition.DefaultMaxRuntimeSeconds,
                MemoryMb = definition.DefaultMemoryMb,
                CpuPercent = definition.DefaultCpuPercent
            }),
            GrantRevision = 1,
            MaxRuntimeSeconds = definition.DefaultMaxRuntimeSeconds,
            MemoryMb = definition.DefaultMemoryMb,
            CpuPercent = definition.DefaultCpuPercent,
            ApprovedAt = now
        };
        installation.Schedule = new AgentSchedule
        {
            Id = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            ActivationMode = definition.DefaultActivationMode,
            TickFrequencySeconds = definition.DefaultTickFrequencySeconds,
            NextTickAt = definition.DefaultActivationMode switch
            {
                ActivationMode.AlwaysOn => now,
                ActivationMode.Periodic => now.AddSeconds(definition.DefaultTickFrequencySeconds),
                _ => null
            },
            MaxRuntimeSeconds = definition.DefaultMaxRuntimeSeconds,
            MaxRetriesPerTick = 0,
            OverlapPolicy = definition.DefaultOverlapPolicy,
            IsEnabled = true
        };
        installation.Configuration = new AgentInstallationConfiguration
        {
            Id = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            SchemaVersion = definition.Configuration?.SchemaVersion ?? "1",
            SettingsJson = "{}",
            Revision = 0,
            CreatedAt = now,
            UpdatedAt = now
        };
        return installation;
    }

    static string? TrimOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    static bool IsAgentWorkerType(WorkerType workerType) => workerType is
        WorkerType.LocalAgent or
        WorkerType.RemoteAgent or
        WorkerType.MarketplaceProxy or
        WorkerType.BuiltInSystem;

    static CoreActionResponse Failure(string errorCode, string message) =>
        new CoreActionResponse(false, errorCode, message);

    private async Task<IReadOnlyList<Guid>> ActiveCommunicationConnectionIdsAsync(Guid organizationId, CancellationToken cancellationToken) =>
        await _dbContext.CommunicationConnections.Where(x => x.OrganizationId == organizationId &&
                x.Status != CommunicationConnectionStatus.Disconnected)
            .Select(x => x.Id).ToListAsync(cancellationToken);

    static CommunicationDelivery CreateEmployeeDelivery(OrganizationUser user, Guid connectionId, CommunicationDeliveryKind kind, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = user.OrganizationId,
        ConnectionId = connectionId,
        OrganizationUserId = user.Id,
        Kind = kind,
        Status = CommunicationDeliveryStatus.Pending,
        IdempotencyKey = $"employee:{user.Id:D}:{kind}:{now.ToUnixTimeMilliseconds()}",
        PayloadJson = JsonSerializer.Serialize(new
        {
            employeeId = user.Id,
            user.DisplayName,
            employeeType = user.EmployeeType.ToString(),
            user.RoleId,
            user.ReportsToOrganizationUserId,
            isActive = kind != CommunicationDeliveryKind.ArchiveEmployee
        }),
        NextAttemptAt = now,
        CreatedAt = now,
        UpdatedAt = now
    };
}
