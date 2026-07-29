using System.Text.Json;
using CSweet.Application.BusinessOnboarding;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.BusinessOnboarding;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Application.Communications;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.BusinessOnboarding;

public sealed class BusinessOnboardingService : IBusinessOnboardingService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ICoreOrganizationService _organizationService;
    private readonly IRoleService _roleService;
    private readonly IStrategicObjectiveService _objectiveService;
    private readonly IWorkerService _workerService;
    private readonly IAuditEventWriter _auditEventWriter;
    private readonly IExecutiveBriefingService _executiveBriefings;
    private readonly CSweetDbContext _dbContext;
    private readonly IAgentCommunicationOnboardingService _agentOnboarding;
    private readonly IAgentRuntimeManager? _agentRuntimeManager;

    public BusinessOnboardingService(
        ICoreOrganizationService organizationService,
        IRoleService roleService,
        IStrategicObjectiveService objectiveService,
        IWorkerService workerService,
        IAuditEventWriter auditEventWriter,
        IExecutiveBriefingService executiveBriefings,
        CSweetDbContext dbContext,
        IAgentCommunicationOnboardingService? agentOnboarding = null,
        IAgentRuntimeManager? agentRuntimeManager = null)
    {
        _organizationService = organizationService;
        _roleService = roleService;
        _objectiveService = objectiveService;
        _workerService = workerService;
        _auditEventWriter = auditEventWriter;
        _executiveBriefings = executiveBriefings;
        _dbContext = dbContext;
        _agentOnboarding = agentOnboarding ?? new AgentCommunicationOnboardingService(dbContext);
        _agentRuntimeManager = agentRuntimeManager;
    }

    public async Task<BusinessOnboardingActionResponse> CompleteAsync(
        CompleteBusinessOnboardingRequest request,
        CancellationToken cancellationToken = default,
        Guid? applicationUserId = null)
    {
        if (string.IsNullOrWhiteSpace(request.BusinessName))
        {
            return Failure("validation_error", "Business name is required.");
        }

        if (request.ChiefAgentInstallationId == Guid.Empty)
        {
            return Failure("chief_agent_required", "Select and approve a Chief of Staff agent before creating the business.");
        }

        var chiefValidation = await ValidateChiefInstallationAsync(request.ChiefAgentInstallationId, cancellationToken);
        if (!chiefValidation.Succeeded)
            return Failure(chiefValidation.ErrorCode!, chiefValidation.Message!);

        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var mission = TrimOrNull(request.MissionStatement);
        var initialObjectiveTitle = mission ?? "Establish the first operating plan";

        var organizationResult = await _organizationService.CreateAsync(
            new CreateOrganizationRequest(
                request.BusinessName,
                TrimOrNull(request.Industry),
                mission,
                null,
                null,
                null),
            cancellationToken,
            applicationUserId);

        if (!organizationResult.Succeeded || organizationResult.Organization is null)
        {
            return Failure(organizationResult.ErrorCode ?? "organization_create_failed", organizationResult.Message ?? "Organization could not be created.");
        }

        var organizationId = organizationResult.Organization.Id;
        var organization = await _dbContext.CoreOrganizations.SingleAsync(x => x.Id == organizationId, cancellationToken);
        organization.Status = OrganizationStatus.Draft;

        _dbContext.BusinessProfiles.Add(new BusinessProfile
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BusinessType = TrimOrNull(request.Industry),
            Description = mission,
            TimeZone = "UTC",
            Completeness = CalculateBootstrapCompleteness(request),
            ProvenanceJson = "{}",
            Revision = 1,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        _dbContext.FinancialOperatingProfiles.Add(new FinancialOperatingProfile
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BaseCurrency = "USD",
            RoutingPreference = "Balanced",
            Revision = 1,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        _dbContext.ManagementCycles.Add(new ManagementCycle
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            TimeZone = "UTC",
            NextReviewAt = NextUtcWeekdayCheckIn(),
            NextExecutiveBriefingAt = NextUtcWeekdayCheckIn()
        });
        await _dbContext.SaveChangesAsync(cancellationToken);
        await WorkBoardProvisioning.EnsureDefaultBoardAsync(
            _dbContext,
            organizationId,
            cancellationToken);

        var roles = await _roleService.ListByOrganizationAsync(organizationId, cancellationToken);

        var objectiveResult = await _objectiveService.CreateAsync(
            organizationId,
            new CreateStrategicObjectiveRequest(
                initialObjectiveTitle,
                "Create a practical operating plan that turns the business mission into immediate actions, risks, owners, and deliverables.",
                (int)ObjectiveStatus.Active,
                DateTimeOffset.UtcNow.AddDays(30)),
            cancellationToken);

        if (!objectiveResult.Succeeded || objectiveResult.StrategicObjective is null)
        {
            return Failure(objectiveResult.ErrorCode ?? "objective_create_failed", objectiveResult.Message ?? "Strategic objective could not be created.");
        }

        var workerResult = await _workerService.CreateAsync(
            organizationId,
            new CreateWorkerRequest(
                "Local Strategy Agent",
                "Default local agent for business planning, operating plans, task breakdown, and risk identification.",
                (int)WorkerType.LocalAgent,
                (int)WorkerExecutionMode.InProcess,
                JsonSerializer.Serialize(new[]
                {
                    "business-planning",
                    "operating-plan",
                    "task-breakdown",
                    "risk-identification"
                }, JsonOptions),
                null,
                null,
                IsEnabled: true,
                RequiresHumanApproval: true),
            cancellationToken);

        if (!workerResult.Succeeded || workerResult.Worker is null)
        {
            return Failure(workerResult.ErrorCode ?? "worker_create_failed", workerResult.Message ?? "Default local strategy worker could not be registered.");
        }

        var assignment = await CreateChiefAssignmentAsync(
            organizationId,
            request.ChiefAgentInstallationId,
            cancellationToken);
        if (!assignment.Succeeded)
        {
            return Failure(
                assignment.ErrorCode ?? "chief_assignment_failed",
                assignment.Message ?? "The selected Chief of Staff agent could not be assigned.");
        }

        var chiefOrganizationUserId = assignment.OrganizationUserId!.Value;
        var chiefReadinessWarnings = assignment.Warnings.ToList();
        organization.Status = OrganizationStatus.Active;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _executiveBriefings.QueueActivationAsync(organizationId, chiefOrganizationUserId, cancellationToken);
        roles = await _roleService.ListByOrganizationAsync(organizationId, cancellationToken);

        await _auditEventWriter.WriteAsync(
            "business_onboarding.completed",
            "Organization",
            organizationId,
            $"Business onboarding completed for '{organizationResult.Organization.Name}'.",
            cancellationToken: cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        var runtimeWarning = await QueueChiefRuntimeAsync(
            request.ChiefAgentInstallationId,
            cancellationToken);
        if (runtimeWarning is not null)
            chiefReadinessWarnings.Add(runtimeWarning);

        var response = new CompleteBusinessOnboardingResponse(
            organizationId,
            roles.Count,
            0,
            workerResult.Worker.Id,
            $"/organizations/{organizationId}/communications/{assignment.ConversationId:D}")
        {
            OrganizationActivated = true,
            ChiefOrganizationUserId = chiefOrganizationUserId,
            ChiefReadinessWarnings = chiefReadinessWarnings
        };

        return new BusinessOnboardingActionResponse(true, null, "Business onboarding completed.", response);
    }

    public async Task<ChiefSetupActionResponse> AssignChiefAsync(
        Guid organizationId,
        CompleteChiefSetupRequest request,
        CancellationToken cancellationToken = default)
    {
        var organization = await _dbContext.CoreOrganizations.SingleOrDefaultAsync(x => x.Id == organizationId, cancellationToken);
        if (organization is null)
            return new(false, "not_found", "The organization was not found.");
        var current = await _dbContext.LeadershipAssignments.AnyAsync(
            x => x.OrganizationId == organizationId && x.PositionKey == "chief-of-staff" && x.EndsAt == null, cancellationToken);
        if (current)
            return new(false, "chief_already_assigned", "The organization already has an active Chief of Staff assignment.");

        var assignment = await CreateChiefAssignmentAsync(organizationId, request.AgentInstallationId, cancellationToken);
        if (!assignment.Succeeded)
            return new(false, assignment.ErrorCode, assignment.Message);

        organization.Status = OrganizationStatus.Active;
        organization.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _executiveBriefings.QueueActivationAsync(organizationId, assignment.OrganizationUserId!.Value, cancellationToken);
        var warnings = assignment.Warnings.ToList();
        var runtimeWarning = await QueueChiefRuntimeAsync(request.AgentInstallationId, cancellationToken);
        if (runtimeWarning is not null)
            warnings.Add(runtimeWarning);
        var response = new CompleteChiefSetupResponse(
            organizationId,
            assignment.OrganizationUserId!.Value,
            warnings,
            $"/organizations/{organizationId}/communications/{assignment.ConversationId:D}");
        return new(true, null, "Chief of Staff setup completed.", response);
    }

    private static string? TrimOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<ChiefAssignmentResult> ValidateChiefInstallationAsync(
        Guid installationId,
        CancellationToken cancellationToken)
    {
        var installation = await _dbContext.AgentInstallations
            .Include(x => x.PackageVersion)
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == installationId, cancellationToken);
        if (installation?.PackageVersion is null)
            return ChiefAssignmentResult.Failure("chief_agent_not_found", "The selected Chief of Staff agent installation was not found.");
        if (!installation.IsEnabled || installation.RevisionStatus != PluginRevisionStatus.Active ||
            installation.PackageVersion.PluginKind != PluginKind.Agent)
            return ChiefAssignmentResult.Failure("chief_agent_unavailable", "The selected installation is not an enabled active agent.");
        if (!string.Equals(installation.BusinessId, "default", StringComparison.OrdinalIgnoreCase))
            return ChiefAssignmentResult.Failure("chief_agent_wrong_organization", "The selected installation is already assigned to another business.");
        return ChiefAssignmentResult.ValidationSuccess();
    }

    private async Task<ChiefAssignmentResult> CreateChiefAssignmentAsync(Guid organizationId, Guid installationId, CancellationToken cancellationToken)
    {
        var installation = await _dbContext.AgentInstallations
            .Include(x => x.PackageVersion)
            .Include(x => x.Grant)
            .SingleOrDefaultAsync(x => x.Id == installationId, cancellationToken);
        if (installation is null || installation.PackageVersion is null)
        {
            return ChiefAssignmentResult.Failure("chief_agent_not_found", "The selected Chief agent installation was not found.");
        }

        if (!installation.IsEnabled || installation.RevisionStatus != PluginRevisionStatus.Active ||
            installation.PackageVersion.PluginKind != PluginKind.Agent)
        {
            return ChiefAssignmentResult.Failure("chief_agent_unavailable", "The selected installation is not an enabled active agent.");
        }

        var organizationKey = organizationId.ToString("D");
        if (!string.Equals(installation.BusinessId, "default", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(installation.BusinessId, organizationKey, StringComparison.OrdinalIgnoreCase))
        {
            return ChiefAssignmentResult.Failure("chief_agent_wrong_organization", "The selected installation belongs to another organization.");
        }

        installation.BusinessId = organizationKey;
        await AgentInstallationConfigurationDefaults.EnsureAsync(
            _dbContext,
            installation,
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var chiefRole = await _dbContext.CoreRoles.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.Name == "Chief of Staff", cancellationToken);
        if (chiefRole is null)
        {
            chiefRole = new Role
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                Name = "Chief of Staff",
                Description = "Coordinates leadership, workstreams, management cadence, and workforce planning on behalf of the CEO.",
                ResponsibilitiesJson = JsonSerializer.Serialize(new[]
                {
                    "Maintain authoritative business understanding",
                    "Coordinate accountable workstream managers",
                    "Surface staffing, financial, capacity, and execution risks"
                }, JsonOptions),
                AuthorityLevel = AuthorityLevel.ExecutionWithApproval,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.CoreRoles.Add(chiefRole);
        }

        var leaders = await _dbContext.CoreOrganizationUsers
            .Include(x => x.Role)
            .Where(x => x.OrganizationId == organizationId && x.IsActive)
            .OrderByDescending(x => x.PermissionLevel)
            .ToListAsync(cancellationToken);
        var ceo = leaders.FirstOrDefault(x => x.Role?.Name == "CEO")
            ?? leaders.FirstOrDefault(x => x.PermissionLevel == OrganizationPermissionLevel.Owner);
        if (ceo is null)
        {
            return ChiefAssignmentResult.Failure("chief_ceo_missing", "A CEO organization user is required before assigning the Chief of Staff.");
        }

        var chief = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ReportsToOrganizationUserId = ceo.Id,
            RoleId = chiefRole.Id,
            AgentInstallationId = installation.Id,
            DisplayName = installation.PackageVersion.AgentName,
            EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Manager,
            CreatedAt = now,
            IsActive = true
        };
        _dbContext.CoreOrganizationUsers.Add(chief);
        _dbContext.LeadershipAssignments.Add(new LeadershipAssignment
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            OrganizationUserId = chief.Id,
            PositionKey = "chief-of-staff",
            StartsAt = now
        });
        var onboarding = await _agentOnboarding.EnsureAsync(organizationId, chief, cancellationToken: cancellationToken);
        if (!onboarding.Succeeded)
            return ChiefAssignmentResult.Failure(onboarding.ErrorCode!, onboarding.Message);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditEventWriter.WriteAsync(
            "leadership_assignment.created",
            "LeadershipAssignment",
            chief.Id,
            $"Assigned '{installation.PackageVersion.AgentName}' as Chief of Staff.",
            cancellationToken: cancellationToken);

        return ChiefAssignmentResult.Success(
            chief.Id,
            onboarding.ConversationId!.Value,
            GetReadinessWarnings(installation.PackageVersion.ManifestJson));
    }

    private async Task<string?> QueueChiefRuntimeAsync(
        Guid installationId,
        CancellationToken cancellationToken)
    {
        if (_agentRuntimeManager is null)
            return null;

        try
        {
            await _agentRuntimeManager.RestartRuntimeAsync(
                installationId,
                "Restarted under the assigned organization for the Chief of Staff's initial onboarding conversation.",
                interactive: true,
                cancellationToken);
            // Do not leave the first-run experience waiting for the schedule worker's
            // next poll. Start reconciling the newly queued interactive runtime now so
            // it can connect and receive its durable onboarding event immediately.
            await _agentRuntimeManager.ReconcileAsync(cancellationToken);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return $"The Chief of Staff was assigned, but its runtime could not be prioritized: {exception.Message}";
        }
    }

    private static IReadOnlyList<string> GetReadinessWarnings(string manifestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            var provided = document.RootElement.TryGetProperty("provides", out var provides) && provides.ValueKind == JsonValueKind.Array
                ? provides.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.TryGetProperty("name", out var name) ? name.GetString() : null)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            var warnings = new List<string>();
            AddReadinessWarning(provided, warnings, "assistant.converse.v1", "conversation");
            AddReadinessWarning(provided, warnings, AgentConfigurationCapabilities.Describe, "configuration");
            AddReadinessWarning(provided, warnings, "management.check-in.v1", "management check-in");
            AddReadinessWarning(provided, warnings, "assistant.plan-work.v1", "planning");
            return warnings;
        }
        catch (JsonException)
        {
            return ["The agent manifest could not be inspected for Chief-of-Staff readiness."];
        }
    }

    private static void AddReadinessWarning(IReadOnlySet<string> provided, ICollection<string> warnings, string capability, string label)
    {
        if (!provided.Contains(capability))
        {
            warnings.Add($"This agent does not advertise {label} capability '{capability}'. Assignment is allowed, but the role may be degraded.");
        }
    }

    private static decimal CalculateBootstrapCompleteness(CompleteBusinessOnboardingRequest request)
    {
        var supplied = new[] { request.BusinessName, request.Industry, request.MissionStatement }
            .Count(x => !string.IsNullOrWhiteSpace(x));
        return decimal.Round(supplied / 3m, 2);
    }

    private static DateTimeOffset NextUtcWeekdayCheckIn()
    {
        var now = DateTimeOffset.UtcNow;
        var next = new DateTimeOffset(now.Year, now.Month, now.Day, 9, 0, 0, TimeSpan.Zero).AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) next = next.AddDays(1);
        return next;
    }

    private sealed record ChiefAssignmentResult(
        bool Succeeded,
        string? ErrorCode,
        string? Message,
        Guid? OrganizationUserId,
        Guid? ConversationId,
        IReadOnlyList<string> Warnings)
    {
        public static ChiefAssignmentResult ValidationSuccess() =>
            new(true, null, null, null, null, []);

        public static ChiefAssignmentResult Success(
            Guid organizationUserId,
            Guid conversationId,
            IReadOnlyList<string> warnings) =>
            new(true, null, null, organizationUserId, conversationId, warnings);

        public static ChiefAssignmentResult Failure(string errorCode, string message) =>
            new(false, errorCode, message, null, null, []);
    }

    private static BusinessOnboardingActionResponse Failure(string errorCode, string message) =>
        new(false, errorCode, message);
}
