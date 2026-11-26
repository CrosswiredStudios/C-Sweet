using System.Text.Json;
using CSweet.Api.Auth;
using CSweet.Application.Compute;
using CSweet.Application.Setup;
using CSweet.Compute.Contracts;
using CSweet.Contracts.Compute;
using CSweet.Contracts.Setup;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Compute;

public static class ComputeManagementEndpoints
{
    public static IEndpointRouteBuilder MapComputeManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/compute").RequireAuthorization();
        group.MapPost("/setup", async (Guid organizationId, HttpContext http, CSweetDbContext db,
            IWebHostEnvironment environment, IConfiguration configuration, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } user) return Results.Unauthorized();
            try
            {
                var enabled = ComputeLocalSetupWorker.InteractiveSetupEnabled(environment, configuration);
                return await StartSetupAsync(db, organizationId, user, enabled, token)
                    ? Results.Accepted()
                    : Results.Conflict(new { error = "Local compute setup is disabled on this Headquarters." });
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });
        group.MapGet("/creation-options", async (Guid organizationId, HttpContext http, CSweetDbContext db,
            ComputeDefaultsService defaults, IConfiguration configuration, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } user) return Results.Unauthorized();
            try
            {
                await RequireOwnerAsync(db, organizationId, user, token);
                var lifetime = configuration.GetValue<int>("CSweet:Compute:Defaults:MaximumLifetimeSeconds");
                if (lifetime < 0) return Results.Conflict();
                return Results.Ok(new ComputeCreationOptions(await defaults.ListCreationOptionsAsync(organizationId, token), lifetime));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });
        group.MapPost("/instances", async (Guid organizationId, CreateLinuxInstance request, HttpContext http, CSweetDbContext db,
            ComputeDefaultsService defaults, IComputeBroker broker, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } user) return Results.Unauthorized();
            try { return Results.Ok(await CreateAsync(db, defaults, broker, organizationId, user, request, token)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "Use a name starting with a lowercase letter, containing only lowercase letters, numbers and hyphens (up to 64 characters), and an allowed lifetime." }); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "Compute availability, the instance name, capacity or project permissions changed. Refresh Compute and check the agent's existing instances." }); }
        });
        group.MapPost("/requests/{requestId:guid}/recheck", async (Guid organizationId, Guid requestId, HttpContext http,
            CSweetDbContext db, ComputePreparationMode preparation, TimeProvider clock, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } user) return Results.Unauthorized();
            try
            {
                var queued = await RecheckAsync(db, organizationId, user, requestId, preparation.LegacyInteractive, clock, token);
                var result = new ComputeActionResult(queued ? "The existing QA assignment is queued for a recheck." :
                    "This request is already due, or it still needs a browser-ready Office.");
                return queued ? Results.Accepted(value: result) : Results.Conflict(result);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });
        group.MapPost("/instances/{environmentId:guid}/provisioning/{operationId:guid}/recheck", async
            (Guid organizationId, Guid environmentId, Guid operationId, HttpContext http, CSweetDbContext db, TimeProvider clock, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } user) return Results.Unauthorized();
            try
            {
                var queued = await RecheckProvisioningAsync(db, organizationId, user, environmentId, operationId, clock, token);
                return queued ? Results.Accepted(value: new ComputeActionResult("The existing provision operation is queued for recheck."))
                    : Results.Conflict(new ComputeActionResult("The operation is already due, its placement is unavailable, or its instance has changed."));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new ComputeActionResult("The instance changed. Refresh Compute.")); }
        });
        group.MapPost("/offices/{officeId:guid}/reassign", async (Guid organizationId, Guid officeId, HttpContext http,
            CSweetDbContext db, IExecutionFleetService fleet, CancellationToken token) =>
        {
            if (http.User.GetApplicationUserId() is not { } user) return Results.Unauthorized();
            try { await RequireOwnerAsync(db, organizationId, user, token); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            var queued = await fleet.CreateLocalSetupSessionAsync(new("custom", 0, 0, 0, officeId,
                Repair: true, RecoverComputeForBusinessId: organizationId), user, token);
            if (!queued.Succeeded || queued.Session is not { } session)
                return Results.Conflict(new ComputeActionResult(queued.Message));
            if (session.AdministratorApprovalRequestedAt != null)
                return Results.Accepted(value: new ComputeActionResult("The Office reassignment is already in progress."));
            if (session.LaunchUri is null)
                return Results.Conflict(new ComputeActionResult(queued.Message));
            var launched = await fleet.LaunchLocalSetupSessionAsync(session.Id, user, new(session.LaunchUri), token);
            return launched.Succeeded
                ? Results.Accepted(value: new ComputeActionResult("Office reassignment requested. Compute becomes available after the Office verifies its new assignment."))
                : Results.Conflict(new ComputeActionResult(launched.Message));
        }).RequireAuthorization("HostAdministration");
        return endpoints;
    }

    internal static bool CanRecheckProvisioning(ComputeEnvironment environment, ComputeOperation operation, DateTimeOffset now) =>
        operation.Action == InfrastructureActions.Provision && operation.Generation == environment.Generation && operation.CompletedAt == null &&
        environment.DesiredState == ComputeDesiredState.Running && environment.TeardownConfirmedAt == null && environment.LeaseExpiresAt > now &&
        OfficeComputePlacement.IsOfficeProvider(environment.ProviderId) && environment.ProviderNodeId.HasValue &&
        (operation.DispatchLeaseExpiresAt == null || operation.DispatchLeaseExpiresAt <= now) &&
        ((operation.Status is "Pending" or "Reconciling" && operation.NextAttemptAt > now) ||
         operation is { Status: "Blocked", Attempts: 0, FailureCode: "node-unavailable" });

    internal static async Task<bool> RecheckProvisioningAsync(CSweetDbContext db, Guid business, Guid user,
        Guid environmentId, Guid operationId, TimeProvider clock, CancellationToken token)
    {
        await RequireOwnerAsync(db, business, user, token);
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token) : null;
        var environment = await db.ComputeEnvironments.SingleOrDefaultAsync(e => e.OrganizationId == business && e.Id == environmentId, token);
        var operation = await db.ComputeOperations.SingleOrDefaultAsync(o => o.OrganizationId == business &&
            o.Id == operationId && o.EnvironmentId == environmentId, token);
        var now = clock.GetUtcNow();
        if (environment == null || operation == null || !CanRecheckProvisioning(environment, operation, now)) return false;
        var specification = JsonSerializer.Deserialize<ComputeSpecification>(environment.SpecificationJson, ComputeProtocol.Json);
        if (specification is null || await OfficeComputePlacement.FindAsync(db, business, now, token,
            specification.TemplateId, environment.ProviderNodeId) == null) return false;
        if (operation.Status == "Blocked") operation.Status = "Pending";
        operation.NextAttemptAt = now;
        operation.Revision++;
        environment.NextAttemptAt = now;
        QueueOwnerAudit(db, business, user, operation.Id, "compute.provision.recheck-requested.v1");
        await db.SaveChangesAsync(token);
        if (tx != null) await tx.CommitAsync(token);
        return true;
    }

    internal static async Task<bool> RecheckAsync(CSweetDbContext db, Guid business, Guid user, Guid requestId,
        bool legacy, TimeProvider clock, CancellationToken token)
    {
        await RequireOwnerAsync(db, business, user, token);
        var actor = await db.CoreOrganizationUsers.AsNoTracking().Where(u => u.OrganizationId == business &&
            u.ApplicationUserId == user && u.IsActive && u.ArchivedAt == null && u.EmployeeType == EmployeeType.Human)
            .Select(u => u.Id).SingleAsync(token);
        var now = clock.GetUtcNow();
        var names = await db.Workstreams.AsNoTracking().Where(w => w.OrganizationId == business).ToDictionaryAsync(w => w.Id, w => w.Name, token);
        var requests = await OverviewIssueReader.ReadComputeRequestsAsync(db, new(db, new ScopedActionAuthorizationService(db), clock),
            business, actor, names, legacy, now, token);
        if (!requests.Any(r => r.Id == requestId && r.CanRecheck)) return false;
        var wait = db.WorkStageExecutions.Where(s => s.Id == requestId && s.LastOutcomeCode == WorkOrchestrator.ComputePreparationWait &&
            (s.RetryAt == null || s.RetryAt > now) &&
            (s.Status == WorkStageExecutionStatus.Backoff && s.ItemExecution != null &&
                s.ItemExecution.CurrentStageKey == s.StageKey && s.ItemExecution.Traversal == s.Traversal &&
                s.ItemExecution.SprintExecution!.OrganizationId == business && s.ItemExecution.SprintExecution.Status == WorkSprintExecutionStatus.Active ||
             s.DeliveryExecution != null && s.DeliveryExecution.Status == "WaitingForEvidence" &&
                s.DeliveryExecution.CurrentStageKey == s.StageKey && s.DeliveryExecution.Plan!.OrganizationId == business &&
                s.DeliveryExecution.Plan.Status == "Active" && s.DeliveryExecution.ScopeRevision == s.DeliveryExecution.Plan.ScopeRevision &&
                !s.DeliveryExecution.Stages.Any(stage => stage.StageKey == s.StageKey && stage.Traversal > s.Traversal)));
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        if (db.Database.IsRelational())
        {
            if (await wait.ExecuteUpdateAsync(update => update.SetProperty(s => s.RetryAt, now), token) == 0) return false;
        }
        else
        {
            var stage = await wait.SingleOrDefaultAsync(token);
            if (stage is null) return false;
            stage.RetryAt = now;
        }
        QueueOwnerAudit(db, business, user, requestId, "compute.workspace.recheck-requested.v1");
        await db.SaveChangesAsync(token);
        if (tx is not null) await tx.CommitAsync(token);
        return true;
    }

    internal static async Task RequireOwnerAsync(CSweetDbContext db, Guid business, Guid user, CancellationToken token)
    {
        if (!await db.CoreOrganizationUsers.AnyAsync(member => member.OrganizationId == business && member.ApplicationUserId == user &&
            member.EmployeeType == EmployeeType.Human && member.PermissionLevel == OrganizationPermissionLevel.Owner &&
            member.IsActive && member.ArchivedAt == null, token)) throw new UnauthorizedAccessException();
    }

    internal static async Task<bool> StartSetupAsync(CSweetDbContext db, Guid business, Guid user,
        bool interactiveSetupEnabled, CancellationToken token)
    {
        await RequireOwnerAsync(db, business, user, token);
        if (!interactiveSetupEnabled) return false;
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token) : null;
        await new CSweet.Infrastructure.Core.ProjectWorkPolicy(db, TimeProvider.System).LockAsync(business, token);
        if (!await db.Set<ComputeLocalSetup>().AnyAsync(setup => setup.OrganizationId == business, token))
        {
            var now = DateTimeOffset.UtcNow;
            var setup = new ComputeLocalSetup { Id = Guid.NewGuid(), OrganizationId = business, CreatedAt = now, UpdatedAt = now };
            db.Add(setup);
            QueueOwnerAudit(db, business, user, setup.Id, "compute.workspace.preparation-requested.v1");
            await db.SaveChangesAsync(token);
        }
        if (transaction is not null) await transaction.CommitAsync(token);
        return true;
    }

    internal static async Task<ComputeEnvironmentView> CreateAsync(CSweetDbContext db, ComputeDefaultsService defaults,
        IComputeBroker broker, Guid business, Guid user, CreateLinuxInstance request, CancellationToken token)
    {
        await RequireOwnerAsync(db, business, user, token);
        if (request.RequestId == Guid.Empty || request.InstallationId == Guid.Empty || request.ProjectId == Guid.Empty ||
            !ComputeSpecification.Identifier(request.Name) || request.LifetimeSeconds < 0) throw new ArgumentException();
        var policy = await defaults.RequestProjectWorkspaceAsync(business, request.InstallationId, request.ProjectId, token);
        if (policy.State != "Ready" || policy.TemplateId is null) throw new InvalidOperationException();
        if (policy.MaximumLifetimeSeconds > 0 && (request.LifetimeSeconds == 0 || request.LifetimeSeconds > policy.MaximumLifetimeSeconds))
            throw new ArgumentException();
        QueueOwnerAudit(db, business, user, request.InstallationId, "compute.instance.owner-requested.v1");
        return await broker.RequestAsync(business, request.InstallationId,
            new(request.ProjectId, request.Name, $"owner:{user:N}:{request.RequestId:N}",
                new("linux", policy.Architecture, policy.TemplateId, new(2, 2048, 20480), request.LifetimeSeconds)), token);
    }

    internal static void QueueOwnerAudit(CSweetDbContext db, Guid business, Guid user, Guid entity, string eventType)
    {
        var eventId = Guid.NewGuid();
        db.AuditOutbox.Add(new()
        {
            Id = eventId, CreatedAt = DateTimeOffset.UtcNow,
            RequestJson = JsonSerializer.Serialize(new AuditEventWriteRequest(eventType, Category: "Infrastructure", Outcome: "Requested",
                OrganizationId: business, EntityType: "Compute", EntityId: entity, Actor: new("Human", ApplicationUserId: user),
                EventId: eventId, OccurredAt: DateTimeOffset.UtcNow, UseAmbientOrganization: false))
        });
    }
}
