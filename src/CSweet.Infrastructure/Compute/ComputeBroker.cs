using CSweet.Compute.Contracts;
using System.Data;
using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Application.Setup;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

/// <summary>Records authorized desired state. It never executes workload code or hypervisor commands.</summary>
public sealed partial class ComputeBroker(CSweetDbContext db, IComputeTemplateCatalog templates, TimeProvider clock, IAuditEventWriter audit) : IComputeBroker
{
    internal static readonly JsonSerializerOptions Json = ComputeProtocol.Json;

    public async Task<ComputeEnvironmentView> RequestAsync(Guid organizationId, Guid installationId,
        RequestComputeEnvironment request, CancellationToken token)
    {
        try { return await RequestCoreAsync(organizationId, installationId, request, token); }
        catch (Exception error) when (error is UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // RequestCore disposes its transaction before recording the rejected attempt.
            // Never store caller keys, specifications or exception text in the ledger.
            await audit.AppendAsync(new AuditEventWriteRequest("compute.admission.rejected.v1",
                Category: "Infrastructure", Outcome: "Rejected", OrganizationId: organizationId,
                EntityType: "ComputeEnvironment", Actor: new("Agent", IdentityVerified: false, InstallationId: installationId),
                ErrorCode: error is UnauthorizedAccessException ? "authority_denied" : "request_rejected",
                OccurredAt: clock.GetUtcNow(), UseAmbientOrganization: false), token);
            throw;
        }
    }

    private async Task<ComputeEnvironmentView> RequestCoreAsync(Guid organizationId, Guid installationId,
        RequestComputeEnvironment request, CancellationToken token)
    {
        if (request.WorkstreamId == Guid.Empty || !Key(request.DesiredEnvironmentKey) || !Key(request.IdempotencyKey) ||
            request.Specification is not { IsValid: true }) throw new ArgumentException("A bounded compute specification and stable keys are required.");
        // Normalize equivalent network requests before binding both idempotency and logical identity.
        var spec = request.Specification with { Network = request.Specification.NetworkPolicy with
            { PublishedPorts = request.Specification.NetworkPolicy.Ports.Order().ToArray() } };
        var specJson = JsonSerializer.Serialize(spec, Json);
        var digest = Digest(JsonSerializer.Serialize(new { request.WorkstreamId, request.DesiredEnvironmentKey, specification = spec }, Json));
        // Callers retry the same key after concurrency/serialization failure; never manufacture a new key.
        try
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
            await RequireActorAsync(organizationId, installationId, request.WorkstreamId, InfrastructureActions.Provision, token);
            var currentGrants = await GrantsAsync(organizationId, installationId, request.WorkstreamId, token);
            RequireAction(currentGrants, InfrastructureActions.Provision, clock.GetUtcNow());
            var admission = await db.ComputeAdmissions.SingleOrDefaultAsync(x => x.InstallationId == installationId, token);
            if (admission is null) db.ComputeAdmissions.Add(admission = new() { OrganizationId = organizationId, InstallationId = installationId });
            if (admission.OrganizationId != organizationId) throw new UnauthorizedAccessException("The compute admission scope is unavailable.");
            admission.Revision++;
            var receipt = await db.ComputeRequestReceipts.SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
                x.InstallationId == installationId && x.IdempotencyKey == request.IdempotencyKey, token);
            if (receipt is not null)
            {
                if (receipt.RequestDigest != digest) throw new InvalidOperationException("This request key already has different terms.");
                var prior = await db.ComputeEnvironments.SingleAsync(x => x.Id == receipt.EnvironmentId &&
                    x.OrganizationId == organizationId && x.InstallationId == installationId, token);
                // No side effect or additional reservation is needed for a replay.
                db.Entry(admission).State = EntityState.Unchanged;
                return View(prior);
            }
            var existing = await db.ComputeEnvironments.SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
                x.InstallationId == installationId && x.DesiredEnvironmentKey == request.DesiredEnvironmentKey, token);
            if (existing is not null)
            {
                if (existing.RequestDigest != digest) throw new InvalidOperationException("The desired environment already has different terms.");
                AddReceipt(existing, request.IdempotencyKey, digest);
                QueueAudit(existing, "Reused", currentGrants.Where(x => x.Action == InfrastructureActions.Provision)
                    .Select(x => new ComputeActionAuthorization(x.Id, x.Revision, x.Action, x.ExpiresAt!.Value)).ToArray());
                await db.SaveChangesAsync(token);
                if (transaction is not null) await transaction.CommitAsync(token);
                return View(existing);
            }
            var registered = await templates.ResolveAsync(organizationId, spec.TemplateId, token)
                ?? throw new InvalidOperationException("No approved compute template is available.");
            if (!ComputeSpecification.Identifier(registered.ProviderId) || registered.NodeId == Guid.Empty)
                throw new InvalidOperationException("The compute template has no registered provider placement.");
            var active = await db.ComputeEnvironments.CountAsync(x => x.OrganizationId == organizationId &&
                x.InstallationId == installationId && x.TeardownConfirmedAt == null, token);
            var now = clock.GetUtcNow();
            var authority = new List<ComputeActionAuthorization>();
            // Every contributing action must independently accept the complete requested boundary.
            // Never merge an OS allowlist from one grant with resource limits from another.
            foreach (var action in ComputePolicy.RequiredProvisionActions(spec))
            {
                ScopedActionGrant? selected = null;
                foreach (var grant in currentGrants.Where(x => x.Action == action && x.Id != Guid.Empty && x.Revision > 0 && x.ExpiresAt >= spec.ExpiresAt(now)))
                {
                    ComputeGrantConstraints? constraints;
                    try { constraints = JsonSerializer.Deserialize<ComputeGrantConstraints>(grant.ConstraintsJson, Json); }
                    catch (Exception error) when (error is JsonException or NotSupportedException) { continue; }
                    if (constraints?.Allows(spec, active) == true) { selected = grant; break; }
                }
                if (selected is null) throw new UnauthorizedAccessException("Current infrastructure grants do not cover this request.");
                authority.Add(new(selected.Id, selected.Revision, action, selected.ExpiresAt!.Value));
                // Installation capabilities are a separate ceiling for each composed permission.
                await RequireActorAsync(organizationId, installationId, request.WorkstreamId, action, token);
            }
            if (!registered.Template.Matches(spec)) throw new InvalidOperationException("The approved template does not match the requested operating system and architecture.");
            var environment = new ComputeEnvironment
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, InstallationId = installationId,
                WorkstreamId = request.WorkstreamId, DesiredEnvironmentKey = request.DesiredEnvironmentKey,
                IdempotencyKey = request.IdempotencyKey, RequestDigest = digest, SpecificationJson = specJson,
                Persistence = spec.Persistence, ProviderId = registered.ProviderId, ProviderNodeId = registered.NodeId,
                CreatedAt = now, UpdatedAt = now, LeaseExpiresAt = spec.ExpiresAt(now), NextAttemptAt = now
            };
            db.ComputeEnvironments.Add(environment);
            db.ComputeOperations.Add(new()
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, InstallationId = installationId,
                EnvironmentId = environment.Id, Generation = environment.Generation, Action = InfrastructureActions.Provision,
                AuthorityJson = JsonSerializer.Serialize(authority, Json), TemplateJson = JsonSerializer.Serialize(registered.Template, Json),
                CreatedAt = now, NextAttemptAt = now
            });
            AddReceipt(environment, request.IdempotencyKey, digest);
            QueueAudit(environment, "Accepted", authority);
            // SaveChanges captures the semantic wake in this same transaction.
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return View(environment);
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    internal async Task RequireActorAsync(Guid organizationId, Guid installationId, Guid workstreamId, string action, CancellationToken token, Guid? environmentId = null)
    {
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant).SingleOrDefaultAsync(x => x.Id == installationId &&
            x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active && x.SetupState == PluginSetupState.Ready, token);
        if (installation is null || !Guid.TryParse(installation.BusinessId, out var business) || business != organizationId ||
            JsonSerializer.Deserialize<string[]>(installation.Grant?.RequiredCapabilitiesJson ?? "[]")?.Contains(action, StringComparer.Ordinal) != true)
            throw new UnauthorizedAccessException("The installation does not have this compute capability.");
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.AgentInstallationId == installationId && x.IsActive && x.ArchivedAt == null && x.EmployeeType == EmployeeType.Agent, token)
            ?? throw new UnauthorizedAccessException("A current agent employee is required.");
        var workstream = await db.Workstreams.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workstreamId && x.OrganizationId == organizationId, token)
            ?? throw new UnauthorizedAccessException("The Workstream is unavailable.");
        var projectPolicy = new CSweet.Infrastructure.Core.ProjectWorkPolicy(db, clock);
        var legacyEnvironment = environmentId.HasValue && await db.LegacyProjectComputeAuthorizations.AnyAsync(x => x.OrganizationId == organizationId && x.InstallationId == installationId && x.EnvironmentId == environmentId, token) &&
            await db.CoreWorkTasks.AnyAsync(t => t.OrganizationId == organizationId && t.AssignedAgentInstallationId == installationId && t.ArchivedAt == null &&
                t.Status != CSweet.Domain.Core.WorkTaskStatus.Completed && t.Status != CSweet.Domain.Core.WorkTaskStatus.Cancelled &&
                db.LegacyDevelopmentAuthorizations.Any(a => a.WorkItemId == t.Id && a.OrganizationId == organizationId), token);
        if (action is not (InfrastructureActions.Read or InfrastructureActions.List or InfrastructureActions.Stop or InfrastructureActions.Destroy) &&
            !legacyEnvironment && await projectPolicy.RequiresProjectAsync(organizationId, installationId, token))
        {
            var board = await db.WorkBoards.Where(x => x.OrganizationId == organizationId && x.WorkstreamId == workstreamId && x.ArchivedAt == null).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(token);
            if (board is null) throw new InvalidOperationException("project.required: Select an active project before provisioning or executing compute.");
            await projectPolicy.RequireAsync(organizationId, actor.Id, board.Value, token);
        }
        if (workstream.AccountableManagerOrganizationUserId == actor.Id || await db.WorkstreamSupervisionAssignments.AsNoTracking()
            .AnyAsync(x => x.WorkstreamId == workstreamId && x.SupervisorOrganizationUserId == actor.Id && x.EndsAt == null, token)) return;
        var teams = await db.WorkstreamTeamAssignments.AsNoTracking().Where(x => x.WorkstreamId == workstreamId && x.EndsAt == null)
            .Select(x => x.TeamId).ToListAsync(token);
        if (!await db.TeamMemberships.AsNoTracking().AnyAsync(x => teams.Contains(x.TeamId) && x.OrganizationUserId == actor.Id && x.EndedAt == null, token))
            throw new UnauthorizedAccessException("The Workstream is outside the employee scope.");
    }

    internal Task<List<ScopedActionGrant>> GrantsAsync(Guid organizationId, Guid installationId, Guid workstreamId, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        // Delegated infrastructure authority is unavailable until ancestor constraints can be verified.
        return db.ScopedActionGrants.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            x.SubjectKind == GrantSubjectKind.AgentInstallation && x.SubjectId == installationId && x.ParentGrantId == null &&
            x.RevokedAt == null && x.GrantedAt <= now && x.ExpiresAt > now &&
            (x.ScopeKind == GrantScopeKind.Organization || x.ScopeKind == GrantScopeKind.Workstream && x.ScopeId == workstreamId))
            .OrderBy(x => x.Id).ToListAsync(token);
    }

    private static void RequireAction(IEnumerable<ScopedActionGrant> grants, string action, DateTimeOffset now)
    {
        if (!grants.Any(x => x.Action == action && x.Id != Guid.Empty && x.Revision > 0 && x.ExpiresAt > now))
            throw new UnauthorizedAccessException("A current scoped infrastructure action grant is required.");
    }

    private void QueueAudit(ComputeEnvironment environment, string outcome, IReadOnlyList<ComputeActionAuthorization> authority, string action = InfrastructureActions.Provision)
    {
        var id = Guid.NewGuid();
        var request = new AuditEventWriteRequest(action == InfrastructureActions.Provision ? "compute.admission.v1" : "compute.lifecycle.v1", Category: "Infrastructure", Outcome: outcome,
            OrganizationId: environment.OrganizationId, EntityType: "ComputeEnvironment", EntityId: environment.Id,
            Actor: new("Agent", InstallationId: environment.InstallationId), OccurredAt: clock.GetUtcNow(),
            MetadataJson: JsonSerializer.Serialize(new { action,
                workstreamId = environment.WorkstreamId, environment.Generation, environment.RequestDigest, grants = authority }),
            UseAmbientOrganization: false, EventId: id);
        db.AuditOutbox.Add(new() { Id = id, CreatedAt = clock.GetUtcNow(), RequestJson = JsonSerializer.Serialize(request) });
    }

    private void AddReceipt(ComputeEnvironment environment, string key, string digest) => db.ComputeRequestReceipts.Add(new()
    {
        Id = Guid.NewGuid(), OrganizationId = environment.OrganizationId, InstallationId = environment.InstallationId,
        EnvironmentId = environment.Id, IdempotencyKey = key, RequestDigest = digest, CreatedAt = clock.GetUtcNow()
    });

    internal static string Digest(string text) => ComputeProtocol.Digest(text);
    internal static ComputeEnvironmentView View(ComputeEnvironment environment) => new(environment.Id, environment.Revision,
        environment.Generation, environment.DesiredState, environment.State, environment.Persistence,
        environment.CreatedAt, environment.LeaseExpiresAt, environment.LastFailureCode, environment.DesiredEnvironmentKey);
    private static bool Key(string? value) => value is { Length: > 0 and <= 160 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
}
