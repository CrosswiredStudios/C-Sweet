using System.Text.Json;
using CSweet.Application.BusinessOnboarding;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.BusinessOnboarding;
using CSweet.Contracts.Core;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Core;
using CSweet.Domain.Notifications;
using CSweet.Domain.Setup;
using CSweet.Application.Communications;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.BusinessOnboarding;

public sealed class BusinessOnboardingService : IBusinessOnboardingService, IBusinessOnboardingOperationService
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
    private readonly IAgentDefinitionService? _agentDefinitions;

    public BusinessOnboardingService(
        ICoreOrganizationService organizationService,
        IRoleService roleService,
        IStrategicObjectiveService objectiveService,
        IWorkerService workerService,
        IAuditEventWriter auditEventWriter,
        IExecutiveBriefingService executiveBriefings,
        CSweetDbContext dbContext,
        IAgentCommunicationOnboardingService? agentOnboarding = null,
        IAgentRuntimeManager? agentRuntimeManager = null,
        IAgentDefinitionService? agentDefinitions = null)
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
        _agentDefinitions = agentDefinitions;
    }

    public Task<BusinessOnboardingActionResponse> CompleteAsync(
        CompleteBusinessOnboardingRequest request,
        CancellationToken cancellationToken = default,
        Guid? applicationUserId = null) =>
        CompleteCoreAsync(request, cancellationToken, applicationUserId, null);

    private async Task<BusinessOnboardingActionResponse> CompleteCoreAsync(
        CompleteBusinessOnboardingRequest request,
        CancellationToken cancellationToken,
        Guid? applicationUserId,
        BusinessOnboardingOperation? durableOperation)
    {
        if (string.IsNullOrWhiteSpace(request.BusinessName))
        {
            return Failure("validation_error", "Business name is required.");
        }

        if (request.ChiefAgentDefinitionId == Guid.Empty)
        {
            return Failure("chief_agent_required", "Select and approve a Chief of Staff agent before creating the business.");
        }

        var chiefDisplayName = TrimOrNull(request.ChiefDisplayName);
        if (chiefDisplayName is { Length: > 160 })
        {
            return Failure("validation_error", "Chief of Staff name cannot exceed 160 characters.");
        }

        var chiefValidation = await ValidateChiefDefinitionAsync(request.ChiefAgentDefinitionId, cancellationToken);
        if (!chiefValidation.Succeeded)
            return Failure(chiefValidation.ErrorCode!, chiefValidation.Message!);

        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var mission = TrimOrNull(request.MissionStatement);
        var initialObjectiveTitle = mission ?? "Establish the first operating plan";

        Organization organization;
        if (durableOperation?.ResultOrganizationId is { } existingOrganizationId)
        {
            organization = await _dbContext.CoreOrganizations.SingleOrDefaultAsync(
                x => x.Id == existingOrganizationId,
                cancellationToken) ?? throw new InvalidOperationException("The business created for this onboarding operation could not be found.");
        }
        else
        {
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

            organization = await _dbContext.CoreOrganizations.SingleAsync(
                x => x.Id == organizationResult.Organization.Id,
                cancellationToken);
        }

        var organizationId = organization.Id;
        organization.Status = OrganizationStatus.Active;
        organization.UpdatedAt = DateTimeOffset.UtcNow;

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
            request.ChiefAgentDefinitionId,
            chiefDisplayName,
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
            $"Business onboarding completed for '{organization.Name}'.",
            cancellationToken: cancellationToken);

        var nextRoute = $"/organizations/{organizationId}/communications/{assignment.ConversationId:D}";
        if (durableOperation is not null)
        {
            durableOperation.Status = BusinessOnboardingOperationStatus.Succeeded;
            durableOperation.ResultOrganizationId = organizationId;
            durableOperation.ResultActionUri = nextRoute;
            durableOperation.Error = null;
            durableOperation.CompletedAt = DateTimeOffset.UtcNow;
            durableOperation.UpdatedAt = durableOperation.CompletedAt.Value;
            durableOperation.LeaseOwner = null;
            durableOperation.LeaseUntil = null;
            await QueueOperationChangedAsync(durableOperation, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        var runtimeWarning = await QueueChiefRuntimeAsync(
            assignment.AgentInstallationId!.Value,
            cancellationToken);
        if (runtimeWarning is not null)
            chiefReadinessWarnings.Add(runtimeWarning);

        var response = new CompleteBusinessOnboardingResponse(
            organizationId,
            roles.Count,
            0,
            workerResult.Worker.Id,
            nextRoute)
        {
            OrganizationActivated = true,
            ChiefOrganizationUserId = chiefOrganizationUserId,
            ChiefReadinessWarnings = chiefReadinessWarnings
        };

        return new BusinessOnboardingActionResponse(true, null, "Business onboarding completed.", response);
    }

    public async Task<BusinessOnboardingOperationResponse> StartAsync(
        StartBusinessOnboardingRequest request,
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        if (applicationUserId == Guid.Empty)
            throw new UnauthorizedAccessException("A signed-in user is required to start business onboarding.");
        if (string.IsNullOrWhiteSpace(request.BusinessName))
            throw new ArgumentException("Business name is required.");
        if (request.BusinessName.Trim().Length > 256)
            throw new ArgumentException("Business name cannot exceed 256 characters.");
        if (request.Industry?.Trim().Length > 160)
            throw new ArgumentException("Industry cannot exceed 160 characters.");
        if (request.MissionStatement?.Trim().Length > 4096)
            throw new ArgumentException("Mission statement cannot exceed 4096 characters.");
        if (request.ChiefDisplayName?.Trim().Length > 160)
            throw new ArgumentException("Chief of Staff name cannot exceed 160 characters.");
        if (request.ChiefAgentPackageVersionId == Guid.Empty)
            throw new ArgumentException("A Chief of Staff package is required.");
        if (request.ChiefAgentInstallRequest is null)
            throw new ArgumentException("Chief of Staff installation settings are required.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("An idempotency key is required.");

        var idempotencyKey = request.IdempotencyKey.Trim();
        if (idempotencyKey.Length > 160)
            throw new ArgumentException("The idempotency key cannot exceed 160 characters.");
        var existing = await _dbContext.BusinessOnboardingOperations.SingleOrDefaultAsync(
            x => x.InitiatedByApplicationUserId == applicationUserId && x.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (existing is not null)
            return await ToOperationAsync(existing, cancellationToken);

        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            var organizationResult = await _organizationService.CreateAsync(
                new CreateOrganizationRequest(
                    request.BusinessName,
                    TrimOrNull(request.Industry),
                    TrimOrNull(request.MissionStatement),
                    null,
                    null,
                    null),
                cancellationToken,
                applicationUserId);
            if (!organizationResult.Succeeded || organizationResult.Organization is null)
                throw new InvalidOperationException(
                    organizationResult.Message ?? "The business could not be created.");

            var organization = await _dbContext.CoreOrganizations.SingleAsync(
                x => x.Id == organizationResult.Organization.Id,
                cancellationToken);
            organization.Status = OrganizationStatus.Active;
            organization.UpdatedAt = DateTimeOffset.UtcNow;

            var now = DateTimeOffset.UtcNow;
            var operation = new BusinessOnboardingOperation
            {
                Id = Guid.NewGuid(),
                InitiatedByApplicationUserId = applicationUserId,
                IdempotencyKey = idempotencyKey,
                BusinessName = request.BusinessName.Trim(),
                Industry = TrimOrNull(request.Industry),
                MissionStatement = TrimOrNull(request.MissionStatement),
                ChiefDisplayName = TrimOrNull(request.ChiefDisplayName),
                ChiefAgentPackageVersionId = request.ChiefAgentPackageVersionId,
                ChiefAgentInstallRequestJson = JsonSerializer.Serialize(request.ChiefAgentInstallRequest, JsonOptions),
                Status = BusinessOnboardingOperationStatus.Starting,
                ResultOrganizationId = organization.Id,
                ResultActionUri = $"/organizations/{organization.Id:D}/command-center",
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.BusinessOnboardingOperations.Add(operation);
            await QueueOperationChangedAsync(operation, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return await ToOperationAsync(operation, cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
            }
            _dbContext.ChangeTracker.Clear();
            existing = await _dbContext.BusinessOnboardingOperations.SingleOrDefaultAsync(
                x => x.InitiatedByApplicationUserId == applicationUserId && x.IdempotencyKey == idempotencyKey,
                cancellationToken);
            if (existing is null) throw;
            return await ToOperationAsync(existing, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<BusinessOnboardingOperationResponse>> ListForUserAsync(
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        var operations = await _dbContext.BusinessOnboardingOperations.AsNoTracking()
            .Where(x => x.InitiatedByApplicationUserId == applicationUserId && x.DismissedAt == null)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        var responses = new List<BusinessOnboardingOperationResponse>(operations.Count);
        foreach (var operation in operations)
            responses.Add(await ToOperationAsync(operation, cancellationToken));
        return responses;
    }

    public async Task<BusinessOnboardingOperationResponse?> GetForUserAsync(
        Guid operationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        var operation = await AuthorizedOperationAsync(operationId, applicationUserId, cancellationToken);
        return operation is null ? null : await ToOperationAsync(operation, cancellationToken);
    }

    public async Task<BusinessOnboardingOperationResponse?> RetryAsync(
        Guid operationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        var operation = await AuthorizedOperationAsync(operationId, applicationUserId, cancellationToken);
        if (operation is null) return null;
        if (operation.Status is not (BusinessOnboardingOperationStatus.Failed or BusinessOnboardingOperationStatus.NeedsSetup))
            throw new InvalidOperationException("Only failed or setup-blocked business onboarding can be retried.");

        operation.RetryCount++;
        operation.Error = null;
        operation.CompletedAt = null;
        operation.DismissedAt = null;
        operation.Status = operation.ChiefAgentDefinitionId.HasValue
            ? BusinessOnboardingOperationStatus.BuildingAgent
            : BusinessOnboardingOperationStatus.Starting;
        operation.UpdatedAt = DateTimeOffset.UtcNow;
        if (operation.ChiefAgentDefinitionId.HasValue && _agentDefinitions is not null)
        {
            var definition = await _agentDefinitions.GetAsync(operation.ChiefAgentDefinitionId.Value, cancellationToken);
            if (definition?.Build?.Status is "Failed" or "Cancelled")
                await _agentDefinitions.RetryBuildAsync(operation.ChiefAgentDefinitionId.Value, cancellationToken);
        }
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await ToOperationAsync(operation, cancellationToken);
    }

    public async Task<BusinessOnboardingOperationResponse?> DismissAsync(
        Guid operationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        var operation = await AuthorizedOperationAsync(operationId, applicationUserId, cancellationToken);
        if (operation is null) return null;
        if (operation.Status is not (BusinessOnboardingOperationStatus.Succeeded or BusinessOnboardingOperationStatus.NeedsSetup or BusinessOnboardingOperationStatus.Failed))
            throw new InvalidOperationException("Active business onboarding cannot be dismissed.");
        operation.DismissedAt = DateTimeOffset.UtcNow;
        operation.UpdatedAt = operation.DismissedAt.Value;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await ToOperationAsync(operation, cancellationToken);
    }

    public async Task<bool> ProcessNextAsync(string leaseOwner, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var candidateId = await _dbContext.BusinessOnboardingOperations.AsNoTracking()
            .Where(x => x.DismissedAt == null &&
                        (x.Status == BusinessOnboardingOperationStatus.Starting ||
                         x.Status == BusinessOnboardingOperationStatus.InstallingAgent ||
                         x.Status == BusinessOnboardingOperationStatus.BuildingAgent ||
                         x.Status == BusinessOnboardingOperationStatus.CreatingBusiness) &&
                        (!x.LeaseUntil.HasValue || x.LeaseUntil < now))
            .OrderBy(x => x.CreatedAt)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (!candidateId.HasValue) return false;

        BusinessOnboardingOperation operation;
        if (_dbContext.Database.IsRelational())
        {
            var claimed = await _dbContext.BusinessOnboardingOperations
                .Where(x => x.Id == candidateId.Value && (!x.LeaseUntil.HasValue || x.LeaseUntil < now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.LeaseOwner, leaseOwner)
                    .SetProperty(x => x.LeaseUntil, now.AddMinutes(2)), cancellationToken);
            if (claimed == 0) return true;
            operation = await _dbContext.BusinessOnboardingOperations.SingleAsync(x => x.Id == candidateId.Value, cancellationToken);
        }
        else
        {
            operation = await _dbContext.BusinessOnboardingOperations.SingleAsync(x => x.Id == candidateId.Value, cancellationToken);
            operation.LeaseOwner = leaseOwner;
            operation.LeaseUntil = now.AddMinutes(2);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        var completedAtomically = false;
        try
        {
            if (_agentDefinitions is null)
                throw new InvalidOperationException("The agent definition service is unavailable.");

            if (!operation.ChiefAgentDefinitionId.HasValue)
            {
                operation.Status = BusinessOnboardingOperationStatus.InstallingAgent;
                operation.UpdatedAt = DateTimeOffset.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);
                var installRequest = JsonSerializer.Deserialize<InstallAgentRequest>(
                    operation.ChiefAgentInstallRequestJson, JsonOptions)
                    ?? throw new InvalidOperationException("The saved Chief of Staff configuration is invalid.");
                var imported = await _agentDefinitions.ImportAsync(
                    operation.ChiefAgentPackageVersionId, installRequest, cancellationToken);
                operation.ChiefAgentDefinitionId = imported.Id;
            }

            var definition = await _agentDefinitions.GetAsync(operation.ChiefAgentDefinitionId.Value, cancellationToken)
                ?? throw new InvalidOperationException("The imported Chief of Staff definition could not be found.");
            if (definition.Build?.Status is "Failed" or "Cancelled" || definition.Status == AgentDefinitionStatus.BuildFailed.ToString())
            {
                operation.Status = BusinessOnboardingOperationStatus.Failed;
                operation.Error = BuildFailure(definition);
                operation.CompletedAt = DateTimeOffset.UtcNow;
            }
            else if (!definition.IsAvailableForHire &&
                     (definition.Build?.Status == "Succeeded" || definition.Status == AgentDefinitionStatus.NeedsConfiguration.ToString()))
            {
                operation.Status = BusinessOnboardingOperationStatus.NeedsSetup;
                operation.Error = $"The {definition.AgentName} build completed, but its required defaults are incomplete.";
                operation.CompletedAt = DateTimeOffset.UtcNow;
            }
            else if (!definition.IsAvailableForHire)
            {
                operation.Status = BusinessOnboardingOperationStatus.BuildingAgent;
            }
            else
            {
                operation.Status = BusinessOnboardingOperationStatus.CreatingBusiness;
                operation.Error = null;
                operation.UpdatedAt = DateTimeOffset.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);
                var result = await CompleteCoreAsync(
                    new CompleteBusinessOnboardingRequest(
                        operation.BusinessName,
                        operation.Industry,
                        operation.MissionStatement,
                        operation.ChiefAgentDefinitionId.Value,
                        operation.ChiefDisplayName),
                    cancellationToken,
                    operation.InitiatedByApplicationUserId,
                    operation);
                if (!result.Succeeded)
                    throw new InvalidOperationException(result.Message ?? "Business onboarding could not be completed.");
                completedAtomically = true;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _dbContext.ChangeTracker.Clear();
            operation = await _dbContext.BusinessOnboardingOperations.SingleAsync(x => x.Id == candidateId.Value, CancellationToken.None);
            operation.Status = BusinessOnboardingOperationStatus.Failed;
            operation.Error = Truncate(exception.Message, 2048);
            operation.CompletedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            if (!completedAtomically)
            {
                operation.LeaseOwner = null;
                operation.LeaseUntil = null;
                operation.UpdatedAt = DateTimeOffset.UtcNow;
                await _dbContext.SaveChangesAsync(CancellationToken.None);
            }
        }
        return true;
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

        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var assignment = await CreateChiefAssignmentAsync(organizationId, request.AgentDefinitionId, null, cancellationToken);
        if (!assignment.Succeeded)
            return new(false, assignment.ErrorCode, assignment.Message);

        organization.Status = OrganizationStatus.Active;
        organization.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _executiveBriefings.QueueActivationAsync(organizationId, assignment.OrganizationUserId!.Value, cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        var warnings = assignment.Warnings.ToList();
        var runtimeWarning = await QueueChiefRuntimeAsync(assignment.AgentInstallationId!.Value, cancellationToken);
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

    private async Task<ChiefAssignmentResult> ValidateChiefDefinitionAsync(
        Guid definitionId,
        CancellationToken cancellationToken)
    {
        var definition = await _dbContext.AgentDefinitions
            .Include(x => x.PackageVersion)
            .Include(x => x.Configuration)
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == definitionId, cancellationToken);
        if (definition?.PackageVersion is null)
            return ChiefAssignmentResult.Failure("chief_agent_not_found", "The selected Chief of Staff agent definition was not found.");
        if (!IsDefinitionHireable(definition))
            return ChiefAssignmentResult.Failure("chief_agent_unavailable",
                "The selected Chief of Staff definition is not built, signed, configured, and available for hire.");
        return ChiefAssignmentResult.ValidationSuccess();
    }

    private async Task<ChiefAssignmentResult> CreateChiefAssignmentAsync(
        Guid organizationId,
        Guid definitionId,
        string? displayName,
        CancellationToken cancellationToken)
    {
        var definition = await _dbContext.AgentDefinitions
            .Include(x => x.PackageVersion)
            .Include(x => x.Configuration)
            .SingleOrDefaultAsync(x => x.Id == definitionId, cancellationToken);
        if (definition is null || definition.PackageVersion is null)
        {
            return ChiefAssignmentResult.Failure("chief_agent_not_found", "The selected Chief agent definition was not found.");
        }

        if (!IsDefinitionHireable(definition))
        {
            return ChiefAssignmentResult.Failure("chief_agent_unavailable",
                "The selected Chief agent definition is not built, signed, configured, and available for hire.");
        }

        var now = DateTimeOffset.UtcNow;
        var installation = OrganizationUserService.CreateHiredInstallation(definition, organizationId, now);
        _dbContext.AgentInstallations.Add(installation);
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
            DisplayName = displayName ?? definition.PackageVersion.AgentName,
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
            $"Assigned '{chief.DisplayName}' as Chief of Staff.",
            cancellationToken: cancellationToken);

        return ChiefAssignmentResult.Success(
            chief.Id,
            onboarding.ConversationId!.Value,
            installation.Id,
            GetReadinessWarnings(definition.PackageVersion.ManifestJson));
    }

    private static bool IsDefinitionHireable(AgentDefinition definition) =>
        definition.IsAvailableForHire &&
        definition.Status == AgentDefinitionStatus.Available &&
        definition.PackageVersion is
        {
            PluginKind: PluginKind.Agent,
            Status: AgentPackageVersionStatus.Built
        } package &&
        !string.IsNullOrWhiteSpace(package.PackageDigest) &&
        !string.IsNullOrWhiteSpace(package.ArtifactSignature);

    private async Task<string?> QueueChiefRuntimeAsync(
        Guid installationId,
        CancellationToken cancellationToken)
    {
        if (_agentRuntimeManager is null)
            return null;

        var alwaysOn = await _dbContext.AgentSchedules.AsNoTracking().AnyAsync(x =>
            x.AgentInstallationId == installationId && x.IsEnabled && x.ActivationMode == ActivationMode.AlwaysOn,
            cancellationToken);
        if (!alwaysOn)
            return null;

        try
        {
            await _agentRuntimeManager.EnsureRuntimeQueuedAsync(
                installationId,
                "Started after the always-on Chief of Staff was hired and committed.",
                interactive: false,
                cancellationToken);
            // Do not leave an eligible always-on hire waiting for the schedule worker's
            // next poll. Reconcile only the runtime that was permitted after commit.
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

    private Task<BusinessOnboardingOperation?> AuthorizedOperationAsync(
        Guid operationId,
        Guid applicationUserId,
        CancellationToken cancellationToken) =>
        _dbContext.BusinessOnboardingOperations.SingleOrDefaultAsync(
            x => x.Id == operationId && x.InitiatedByApplicationUserId == applicationUserId,
            cancellationToken);

    private async Task<BusinessOnboardingOperationResponse> ToOperationAsync(
        BusinessOnboardingOperation operation,
        CancellationToken cancellationToken)
    {
        AgentDefinitionResponse? definition = null;
        if (operation.ChiefAgentDefinitionId.HasValue && _agentDefinitions is not null)
            definition = await _agentDefinitions.GetAsync(operation.ChiefAgentDefinitionId.Value, cancellationToken);
        var agentName = definition?.AgentName ?? await _dbContext.AgentPackageVersions.AsNoTracking()
            .Where(x => x.Id == operation.ChiefAgentPackageVersionId)
            .Select(x => x.AgentName)
            .SingleOrDefaultAsync(cancellationToken) ?? "Chief of Staff";
        var steps = definition?.Build?.Steps ?? [];
        var completedSteps = steps.Count(x => x.Status == AgentBuildStepStatuses.Succeeded);
        var activeStep = steps.FirstOrDefault(x => x.Status == AgentBuildStepStatuses.InProgress);
        var phase = operation.Status switch
        {
            BusinessOnboardingOperationStatus.Starting => "Starting onboarding",
            BusinessOnboardingOperationStatus.InstallingAgent => "Preparing Chief of Staff",
            BusinessOnboardingOperationStatus.BuildingAgent => "Building Chief of Staff",
            BusinessOnboardingOperationStatus.CreatingBusiness => "Finishing business setup",
            BusinessOnboardingOperationStatus.Succeeded => "Business ready",
            BusinessOnboardingOperationStatus.NeedsSetup => "Chief setup required",
            _ => "Onboarding interrupted"
        };
        var detail = operation.Status switch
        {
            BusinessOnboardingOperationStatus.Starting => $"{operation.BusinessName} is active. Preparing its Chief of Staff…",
            BusinessOnboardingOperationStatus.InstallingAgent => $"Importing and configuring {agentName}…",
            BusinessOnboardingOperationStatus.BuildingAgent when activeStep is not null =>
                string.IsNullOrWhiteSpace(activeStep.Detail) ? activeStep.Label : $"{activeStep.Label}: {activeStep.Detail}",
            BusinessOnboardingOperationStatus.BuildingAgent => definition?.Build?.Status == "Queued"
                ? $"{agentName} build queued…"
                : $"Building {agentName}…",
            BusinessOnboardingOperationStatus.CreatingBusiness =>
                $"Completing {operation.BusinessName}'s operating structure and Chief assignment…",
            BusinessOnboardingOperationStatus.Succeeded => $"{operation.BusinessName} is ready.",
            BusinessOnboardingOperationStatus.NeedsSetup =>
                operation.Error ?? $"Finish configuring {agentName} to continue.",
            _ => operation.Error ?? $"{operation.BusinessName} could not be created."
        };
        var actionUri = operation.Status switch
        {
            BusinessOnboardingOperationStatus.Succeeded => operation.ResultActionUri,
            _ when BusinessOnboardingOperationStatuses.IsActive(operation.Status.ToString()) && operation.ResultOrganizationId.HasValue =>
                operation.ResultActionUri ?? $"/organizations/{operation.ResultOrganizationId:D}/command-center",
            _ when operation.ChiefAgentDefinitionId.HasValue =>
                $"/settings/agents?definitionId={operation.ChiefAgentDefinitionId:D}",
            _ when operation.ResultOrganizationId.HasValue =>
                operation.ResultActionUri ?? $"/organizations/{operation.ResultOrganizationId:D}/command-center",
            _ => null
        };
        return new BusinessOnboardingOperationResponse(
            operation.Id,
            operation.BusinessName,
            agentName,
            operation.Status.ToString(),
            phase,
            detail,
            completedSteps,
            steps.Count,
            operation.ChiefAgentDefinitionId,
            operation.ResultOrganizationId,
            actionUri,
            operation.Error,
            operation.UpdatedAt);
    }

    private async Task QueueOperationChangedAsync(
        BusinessOnboardingOperation operation,
        CancellationToken cancellationToken)
    {
        if (!operation.ResultOrganizationId.HasValue) return;
        var recipientId = await _dbContext.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == operation.ResultOrganizationId &&
                        x.ApplicationUserId == operation.InitiatedByApplicationUserId &&
                        x.IsActive && x.PermissionLevel == OrganizationPermissionLevel.Owner)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (!recipientId.HasValue) return;
        var now = DateTimeOffset.UtcNow;
        _dbContext.ApplicationRealtimeOutbox.Add(new ApplicationRealtimeOutboxItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = operation.ResultOrganizationId,
            RecipientOrganizationUserId = recipientId,
            EventType = AppRealtimeEvents.BusinessOnboardingOperationChanged,
            Subject = $"business-onboarding/{operation.Id:D}",
            DataJson = JsonSerializer.Serialize(
                new BusinessOnboardingOperationChangedEvent(
                    operation.Id, operation.ResultOrganizationId.Value, operation.Status.ToString()),
                JsonOptions),
            Status = ApplicationRealtimeOutboxStatus.Pending,
            NextAttemptAt = now,
            OccurredAt = now
        });
    }

    private static string BuildFailure(AgentDefinitionResponse definition)
    {
        var failedStep = definition.Build?.Steps?.FirstOrDefault(x =>
            x.Status is AgentBuildStepStatuses.Failed or AgentBuildStepStatuses.Cancelled ||
            !string.IsNullOrWhiteSpace(x.Error));
        return failedStep?.Error ?? definition.Build?.FailureMessage ??
            $"The {definition.AgentName} build did not complete.";
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

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
        Guid? AgentInstallationId,
        IReadOnlyList<string> Warnings)
    {
        public static ChiefAssignmentResult ValidationSuccess() =>
            new(true, null, null, null, null, null, []);

        public static ChiefAssignmentResult Success(
            Guid organizationUserId,
            Guid conversationId,
            Guid agentInstallationId,
            IReadOnlyList<string> warnings) =>
            new(true, null, null, organizationUserId, conversationId, agentInstallationId, warnings);

        public static ChiefAssignmentResult Failure(string errorCode, string message) =>
            new(false, errorCode, message, null, null, null, []);
    }

    private static BusinessOnboardingActionResponse Failure(string errorCode, string message) =>
        new(false, errorCode, message);
}
