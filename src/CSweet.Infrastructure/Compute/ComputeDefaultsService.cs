using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Application.Setup;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CSweet.Infrastructure.Compute;

/// <summary>Application policy for the local, ephemeral Linux workspace. Agents cannot select provider settings.</summary>
public sealed class ComputeDefaultsService(CSweetDbContext db, TimeProvider clock,
    ComputeGrantAdministration grants, IConfiguration? configuration = null) : IComputeDefaults
{
    private static readonly string[] Actions = [InfrastructureActions.Provision, InfrastructureActions.Read,
        InfrastructureActions.List, InfrastructureActions.Execute, InfrastructureActions.Start, InfrastructureActions.Stop,
        InfrastructureActions.Restart, InfrastructureActions.Destroy];

    public async Task EnsureRequestedAsync(Guid installationId, CancellationToken token, bool retryFailed = false)
    {
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant)
            .SingleOrDefaultAsync(x => x.Id == installationId && x.IsEnabled &&
                x.RevisionStatus == PluginRevisionStatus.Active && x.SetupState == PluginSetupState.Ready, token);
        if (installation is null || !Guid.TryParse(installation.BusinessId, out var organizationId) ||
            !Approved(installation).Contains(InfrastructureActions.Provision)) return;
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == installationId && x.IsActive &&
            x.ArchivedAt == null && x.EmployeeType == EmployeeType.Agent, token);
        if (actor is null) return;
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token) : null;
        await new CSweet.Infrastructure.Core.ProjectWorkPolicy(db, clock).LockAsync(organizationId, token);
        var setup = await db.Set<ComputeLocalSetup>().SingleOrDefaultAsync(x => x.OrganizationId == organizationId, token);
        var now = clock.GetUtcNow();
        var changed = setup is null || retryFailed && setup.State == "Failed";
        if (setup is null)
            db.Add(setup = new ComputeLocalSetup { Id = Guid.NewGuid(), OrganizationId = organizationId, CreatedAt = now, UpdatedAt = now });
        else if (retryFailed && setup.State == "Failed")
        {
            setup.State = "Pending"; setup.ErrorCode = null; setup.HandoffHash = null; setup.HandoffExpiresAt = null;
            setup.Revision++; setup.UpdatedAt = now;
        }
        // Host preparation is organization infrastructure, never an agent-owned project.
        // Execution grants are resolved against an authorized existing project in ReadProjectAsync.
        if (!changed)
        {
            if (transaction is not null) await transaction.CommitAsync(token);
            return;
        }
        var auditId = Guid.NewGuid();
        db.AuditOutbox.Add(new() { Id = auditId, CreatedAt = now, RequestJson = JsonSerializer.Serialize(
            new AuditEventWriteRequest("compute.workspace.preparation-requested.v1", Category: "Infrastructure",
                Outcome: "Accepted", OrganizationId: organizationId, EntityType: "AgentInstallation", EntityId: installationId,
                Actor: new("Application", InstallationId: installationId), EventId: auditId, OccurredAt: now,
                UseAmbientOrganization: false)) });
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
    }

    public async Task<ComputeDefaults> ReadAsync(Guid organizationId, Guid installationId, CancellationToken token)
    {
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant).SingleOrDefaultAsync(x =>
            x.Id == installationId && x.BusinessId == organizationId.ToString("D") && x.IsEnabled &&
            x.RevisionStatus == PluginRevisionStatus.Active && x.SetupState == PluginSetupState.Ready, token);
        if (installation is null || !Approved(installation).Contains(InfrastructureActions.Read)) throw new UnauthorizedAccessException();
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == installationId && x.IsActive &&
            x.ArchivedAt == null && x.EmployeeType == EmployeeType.Agent, token)
            ?? throw new UnauthorizedAccessException();
        // Legacy callers may omit the project only when there is one current assignment.
        // Never return a synthetic scope or guess between multiple projects.
        var projects = await ProjectScopes(organizationId, actor.Id).Select(x => x.Id).Take(2).ToListAsync(token);
        if (projects.Count == 1) return await ReadProjectAsync(organizationId, installationId, projects[0], token);
        var setup = await db.Set<ComputeLocalSetup>().AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId, token);
        return new(setup?.State is "Failed" or "Running" ? setup.State : "Pending", null, null,
            setup?.ErrorCode ?? (projects.Count == 0 ? "project_required" : "project_selection_required"));
    }

    public async Task<ComputeDefaults> ReadProjectAsync(Guid organizationId, Guid installationId, Guid projectId, CancellationToken token)
    {
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.AgentInstallationId == installationId && x.IsActive && x.ArchivedAt == null && x.EmployeeType == EmployeeType.Agent, token)
            ?? throw new UnauthorizedAccessException();
        await RequireProjectAsync(organizationId, installationId, actor.Id, projectId, token);
        var installation = await db.AgentInstallations.Include(x => x.Grant).SingleOrDefaultAsync(x => x.Id == installationId &&
            x.BusinessId == organizationId.ToString("D") && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active &&
            x.SetupState == PluginSetupState.Ready, token) ?? throw new UnauthorizedAccessException();
        var approved = Approved(installation);
        if (!approved.Contains(InfrastructureActions.Read)) throw new UnauthorizedAccessException();
        var setup = await db.Set<ComputeLocalSetup>().SingleOrDefaultAsync(x => x.OrganizationId == organizationId, token);
        if (setup is not { State: "Ready", TemplateId: not null }) return new(setup?.State ?? "Pending", projectId, null, setup?.ErrorCode);
        var template = await db.ComputeTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.TemplateId == setup.TemplateId, token);
        if (template is null || JsonSerializer.Deserialize<ComputeTemplate>(template.TemplateJson, ComputeProtocol.Json)?.Features.Contains("docker") != true)
            return new("Pending", projectId, null, "docker_preparation_pending");
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(token) : null;
        await new CSweet.Infrastructure.Core.ProjectWorkPolicy(db, clock).LockAsync(organizationId, token);
        await RequireProjectAsync(organizationId, installationId, actor.Id, projectId, token);
        var lifetime = configuration?.GetValue<int?>("CSweet:Compute:Defaults:MaximumLifetimeSeconds") ?? 0;
        if (lifetime < 0) throw new InvalidOperationException("Compute maximum lifetime must be zero (until released) or positive.");
        var constraints = new ComputeGrantConstraints(1, new(2, 2048, 20480), 1, lifetime, ["linux"], ["x64"], [setup.TemplateId], AllowedPublishedPorts: [8080]);
        foreach (var action in Actions.Where(approved.Contains))
        {
            var id = StableId($"project-compute:{projectId:D}:{installationId:D}:{action}");
            if (!await db.ScopedActionGrants.AnyAsync(x => x.Id == id, token))
                await grants.PutAsync(organizationId, id, new(0, installationId, projectId, action, constraints, lifetime == 0 ? DateTimeOffset.MaxValue : clock.GetUtcNow().AddDays(30)), token);
        }
        if (transaction is not null) await transaction.CommitAsync(token);
        var definition = JsonSerializer.Deserialize<ComputeTemplate>(template.TemplateJson, ComputeProtocol.Json)!;
        return new("Ready", projectId, setup.TemplateId, null)
        {
            Features = definition.Features.Order(StringComparer.Ordinal).ToArray(),
            CachedImages = definition.Features.Contains("docker-apps-v1") ? ["csweet/python:3.12", "csweet/node:22"] : [],
            LinkReachability = "local-machine", MaximumLifetimeSeconds = lifetime
        };
    }

    private IQueryable<Workstream> ProjectScopes(Guid organizationId, Guid employeeId) =>
        db.Workstreams.Where(project => project.OrganizationId == organizationId &&
            (project.Status == WorkstreamStatus.Active || project.Status == WorkstreamStatus.Approved) &&
            (project.AccountableManagerOrganizationUserId == employeeId ||
             db.WorkstreamSupervisionAssignments.Any(x => x.OrganizationId == organizationId && x.WorkstreamId == project.Id &&
                 x.SupervisorOrganizationUserId == employeeId && x.EndsAt == null) ||
             db.WorkstreamTeamAssignments.Any(assignment => assignment.OrganizationId == organizationId && assignment.WorkstreamId == project.Id &&
                 assignment.EndsAt == null && db.OrganizationTeams.Any(team => team.Id == assignment.TeamId &&
                     team.OrganizationId == organizationId && team.ArchivedAt == null) &&
                 db.TeamMemberships.Any(member => member.OrganizationId == organizationId && member.TeamId == assignment.TeamId &&
                     member.OrganizationUserId == employeeId && member.EndedAt == null))));

    private async Task RequireProjectAsync(Guid organizationId, Guid installationId, Guid employeeId, Guid projectId, CancellationToken token)
    {
        if (!await ProjectScopes(organizationId, employeeId).AnyAsync(x => x.Id == projectId, token))
            throw new UnauthorizedAccessException("The active project is outside the employee scope.");
        var policy = new CSweet.Infrastructure.Core.ProjectWorkPolicy(db, clock);
        if (!await policy.RequiresProjectAsync(organizationId, installationId, token)) return;
        // Universal delivery boards do not require the legacy ProjectDeliveryBinding.
        // Resolve only current boards the installation can read; RequireAsync still checks
        // explicit participation, team assignment and current board authority below.
        var now = clock.GetUtcNow();
        var boards = await db.WorkBoards.Where(x => x.OrganizationId == organizationId && x.WorkstreamId == projectId &&
                x.Kind == CSweet.Domain.WorkManagement.WorkBoardKind.Standard && x.ArchivedAt == null &&
                db.ScopedActionGrants.Any(g => g.OrganizationId == organizationId && g.SubjectId == installationId &&
                    g.SubjectKind == GrantSubjectKind.AgentInstallation && g.ScopeKind == GrantScopeKind.Board &&
                    g.ScopeId == x.Id && g.Action == CSweet.Contracts.WorkManagement.WorkItemActions.Read &&
                    g.RevokedAt == null && (g.ExpiresAt == null || g.ExpiresAt > now)))
            .Select(x => x.Id).Take(2).ToListAsync(token);
        if (boards.Count == 0)
            throw new InvalidOperationException("project.required: Restore access to the existing project's delivery board before testing.");
        if (boards.Count > 1)
            throw new InvalidOperationException("project.board_selection_required: Multiple delivery boards are authorized for this project.");
        var board = boards[0];
        await policy.RequireAsync(organizationId, employeeId, board, token);
    }

    public async Task ActivateAccessAsync(ComputeLocalSetup setup, CancellationToken token)
    {
        if (setup.State != "Ready" || setup.TemplateId is null) throw new InvalidOperationException("Compute is not ready.");
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token) : null;
        var accesses = await db.Set<ComputeAgentAccess>().Where(x => x.SetupId == setup.Id).ToListAsync(token);
        foreach (var access in accesses)
        {
            var policyChanged = false;
            // Retire only untouched grants from the previous automatic network defaults.
            foreach (var action in new[] { InfrastructureActions.Inbound, InfrastructureActions.PublishPort })
            {
                var oldId = StableId($"compute-default-grant:{access.Id:D}:{action}");
                var legacy = await db.ScopedActionGrants.SingleOrDefaultAsync(x => x.Id == oldId && x.Revision == 1 && x.RevokedAt == null, token);
                if (legacy is not null)
                {
                    legacy.RevokedAt = clock.GetUtcNow(); legacy.Revision++;
                    var auditId = Guid.NewGuid();
                    db.AuditOutbox.Add(new() { Id = auditId, CreatedAt = clock.GetUtcNow(), RequestJson = JsonSerializer.Serialize(
                        new AuditEventWriteRequest("compute.automatic-network-grant.retired.v1", Category: "Infrastructure", Outcome: "Revoked",
                            OrganizationId: access.OrganizationId, EntityType: "ScopedActionGrant", EntityId: oldId,
                            Actor: new("Application"), EventId: auditId, UseAmbientOrganization: false)) });
                }
            }
            var installation = await db.AgentInstallations.Include(x => x.Grant).SingleAsync(x => x.Id == access.Id, token);
            if (!installation.IsEnabled || installation.RevisionStatus != PluginRevisionStatus.Active) continue;
            var approved = Approved(installation);
            var now = clock.GetUtcNow();
            var lifetime = configuration?.GetValue<int?>("CSweet:Compute:Defaults:MaximumLifetimeSeconds") ?? 0;
            if (lifetime < 0) throw new InvalidOperationException("Compute maximum lifetime must be zero (until released) or positive.");
            var constraints = new ComputeGrantConstraints(1, new(2, 2048, 20480), 1, lifetime, ["linux"], ["x64"],
                [setup.TemplateId], AllowedPublishedPorts: [8080]);
            foreach (var action in Actions.Where(approved.Contains))
            {
                var id = StableId($"compute-default-grant:{access.Id:D}:{action}");
                // Never replace or revive a revoked/edited grant. These defaults are created once.
                var prior = await db.ScopedActionGrants.SingleOrDefaultAsync(x => x.Id == id, token);
                if (prior is not null)
                {
                    // Upgrade only the known, unmodified legacy automatic policy. Never touch
                    // network grants, revoked/expired grants, or administrator-customized terms.
                    var legacy = JsonSerializer.Deserialize<ComputeGrantConstraints>(prior.ConstraintsJson, ComputeProtocol.Json);
                    if (lifetime == 0 && prior.Revision <= 2 && prior.RevokedAt is null && prior.ExpiresAt > now &&
                        Math.Abs((prior.ExpiresAt.Value - prior.GrantedAt).TotalDays - 30) < 0.001 &&
                        legacy is { Version: 1, MaximumLifetimeSeconds: 3600, MaximumConcurrentEnvironments: 1,
                            AllowPersistent: false, AllowOutbound: false, AllowPublicEndpoint: false, EnvironmentId: null } &&
                        legacy.MaximumResources == constraints.MaximumResources &&
                        legacy.OperatingSystems.SetEquals(constraints.OperatingSystems) && legacy.Architectures.SetEquals(constraints.Architectures) &&
                        legacy.Templates.SetEquals(constraints.Templates) && legacy.AllowedPublishedPorts?.SetEquals([8080]) == true)
                    {
                        await grants.PutAsync(access.OrganizationId, id, new(prior.Revision, access.Id, access.WorkstreamId, action,
                            constraints, DateTimeOffset.MaxValue), token);
                        policyChanged = true;
                        continue;
                    }
                    if (access.GrantsCreatedAt is not null) continue;
                    // Replace a template pin only for an untouched automatic default. Preserve
                    // administrator revisions, expiration and revocation across image upgrades.
                    if (prior.Revision == 1 && prior.RevokedAt is null && prior.ExpiresAt > now)
                        await grants.PutAsync(access.OrganizationId, id, new(prior.Revision, access.Id, access.WorkstreamId, action,
                            constraints, prior.ExpiresAt.Value), token);
                    continue;
                }
                if (access.GrantsCreatedAt is not null) continue;
                await grants.PutAsync(access.OrganizationId, id, new(0, access.Id, access.WorkstreamId, action,
                    constraints, lifetime == 0 ? DateTimeOffset.MaxValue : now.AddDays(30)), token);
            }
            if (access.GrantsCreatedAt is not null && !policyChanged) continue;
            access.GrantsCreatedAt = now;
            var eventId = Guid.NewGuid();
            db.AgentPlatformEventOutbox.Add(new() { Id = eventId, OrganizationId = access.OrganizationId,
                TargetInstallationId = access.Id, EventType = "com.csweet.compute.available.v1",
                DataJson = JsonSerializer.Serialize(new { revision = setup.Revision }),
                IdempotencyKey = $"compute-available:{access.Id:D}:{setup.Revision}:{(policyChanged ? "until-release" : "initial")}", Status = AgentPlatformEventOutboxStatus.Pending,
                OccurredAt = now, NextAttemptAt = now });
        }
        // Wake current project employees, including established team assignments without
        // delivery bindings. A stable event identity makes readiness reconciliation idempotent.
        var projectAgents = await db.CoreOrganizationUsers.Where(person => person.OrganizationId == setup.OrganizationId &&
            person.IsActive && person.ArchivedAt == null && person.AgentInstallationId != null &&
            db.Workstreams.Any(project => project.OrganizationId == setup.OrganizationId &&
                (project.Status == WorkstreamStatus.Active || project.Status == WorkstreamStatus.Approved) &&
                (project.AccountableManagerOrganizationUserId == person.Id ||
                 db.ProjectParticipants.Any(participant => participant.OrganizationId == setup.OrganizationId &&
                     participant.WorkstreamId == project.Id && participant.OrganizationUserId == person.Id && participant.RemovedAt == null) ||
                 db.WorkstreamSupervisionAssignments.Any(x => x.OrganizationId == setup.OrganizationId && x.WorkstreamId == project.Id &&
                     x.SupervisorOrganizationUserId == person.Id && x.EndsAt == null) ||
                 db.WorkstreamTeamAssignments.Any(assignment => assignment.OrganizationId == setup.OrganizationId && assignment.WorkstreamId == project.Id &&
                     assignment.EndsAt == null && db.TeamMemberships.Any(member => member.OrganizationId == setup.OrganizationId &&
                         member.TeamId == assignment.TeamId && member.OrganizationUserId == person.Id && member.EndedAt == null)))))
            .Select(person => person.AgentInstallationId!.Value).Distinct().ToListAsync(token);
        foreach (var agent in projectAgents)
        {
            var eventId = StableId($"project-compute-available:{agent:D}:{setup.Revision}");
            if (await db.AgentPlatformEventOutbox.AnyAsync(x => x.Id == eventId, token)) continue;
            db.AgentPlatformEventOutbox.Add(new() { Id = eventId, OrganizationId = setup.OrganizationId, TargetInstallationId = agent,
                EventType = "com.csweet.compute.available.v1", DataJson = JsonSerializer.Serialize(new { revision = setup.Revision }),
                IdempotencyKey = $"project-compute-available:{agent:D}:{setup.Revision}", NextAttemptAt = clock.GetUtcNow(), OccurredAt = clock.GetUtcNow() });
        }
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
    }

    private static HashSet<string> Approved(AgentInstallation installation) =>
        JsonSerializer.Deserialize<HashSet<string>>(installation.Grant?.RequiredCapabilitiesJson ?? "[]") ?? [];
    private static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}
