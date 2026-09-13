using CSweet.Compute.Contracts;
using System.Data;
using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Application.Setup;
using CSweet.Domain.Compute;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

public sealed partial class ComputeBroker
{
    public async Task<ComputeEnvironmentView> ChangeLifecycleAsync(Guid organizationId, Guid installationId,
        ChangeComputeLifecycle request, CancellationToken token)
    {
        try { return await ChangeLifecycleCoreAsync(organizationId, installationId, request, token); }
        catch (Exception error) when (error is UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            await audit.AppendAsync(new AuditEventWriteRequest("compute.lifecycle.rejected.v1", Category: "Infrastructure",
                Outcome: "Rejected", OrganizationId: organizationId, EntityType: "ComputeEnvironment",
                Actor: new("Agent", IdentityVerified: false, InstallationId: installationId),
                ErrorCode: error is UnauthorizedAccessException ? "authority_denied" : "request_rejected",
                OccurredAt: clock.GetUtcNow(), UseAmbientOrganization: false), token);
            throw;
        }
    }

    private async Task<ComputeEnvironmentView> ChangeLifecycleCoreAsync(Guid organizationId, Guid installationId,
        ChangeComputeLifecycle request, CancellationToken token)
    {
        if (request.EnvironmentId == Guid.Empty || request.ExpectedGeneration < 1 || !Key(request.IdempotencyKey) ||
            request.Action is not (InfrastructureActions.Start or InfrastructureActions.Stop or InfrastructureActions.Restart or InfrastructureActions.Destroy))
            throw new ArgumentException("A supported lifecycle action, generation and stable request key are required.");
        var digest = Digest(JsonSerializer.Serialize(new { request.EnvironmentId, request.ExpectedGeneration, request.Action }, Json));
        try
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
            var environment = await db.ComputeEnvironments.SingleOrDefaultAsync(x => x.Id == request.EnvironmentId &&
                x.OrganizationId == organizationId && x.InstallationId == installationId, token)
                ?? throw new UnauthorizedAccessException("The compute environment is unavailable.");
            await RequireActorAsync(organizationId, installationId, environment.WorkstreamId, request.Action, token);
            var grants = await GrantsAsync(organizationId, installationId, environment.WorkstreamId, token);
            var now = clock.GetUtcNow();
            RequireAction(grants, request.Action, now);
            var receipt = await db.ComputeOperations.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
                x.InstallationId == installationId && x.IdempotencyKey == request.IdempotencyKey, token);
            if (receipt is not null)
            {
                if (receipt.RequestDigest != digest) throw new InvalidOperationException("This lifecycle key already has different terms.");
                return View(environment);
            }
            if (environment.Generation != request.ExpectedGeneration)
                throw new InvalidOperationException("The environment generation changed; read current state before requesting another action.");
            if (environment.DesiredState == ComputeDesiredState.Destroyed && request.Action != InfrastructureActions.Destroy || environment.TeardownConfirmedAt is not null)
                throw new InvalidOperationException("A destroyed environment cannot accept another lifecycle action.");

            var energizing = request.Action is InfrastructureActions.Start or InfrastructureActions.Restart;
            var authority = new List<ComputeActionAuthorization>();
            if (energizing)
            {
                if (environment.LeaseExpiresAt <= now) throw new UnauthorizedAccessException("The compute lease has expired.");
                var spec = JsonSerializer.Deserialize<ComputeSpecification>(environment.SpecificationJson, Json)
                    ?? throw new InvalidOperationException("The recorded compute specification is unavailable.");
                var reservations = await db.ComputeEnvironments.CountAsync(x => x.OrganizationId == organizationId &&
                    x.InstallationId == installationId && x.TeardownConfirmedAt == null, token);
                // Starting also restores any attached network/persistence capabilities.
                foreach (var action in ComputePolicy.RequiredProvisionActions(spec).Where(x => x != InfrastructureActions.Provision).Prepend(request.Action))
                {
                    await RequireActorAsync(organizationId, installationId, environment.WorkstreamId, action, token);
                    var eligible = grants.Where(x => x.Action == action && x.Id != Guid.Empty && x.Revision > 0 && x.ExpiresAt >= environment.LeaseExpiresAt);
                    var selected = eligible.FirstOrDefault(grant =>
                    {
                        try { return JsonSerializer.Deserialize<ComputeGrantConstraints>(grant.ConstraintsJson, Json)?.Allows(spec, Math.Max(0, reservations - 1)) == true; }
                        catch (Exception error) when (error is JsonException or NotSupportedException) { return false; }
                    }) ?? throw new UnauthorizedAccessException("Current infrastructure grants do not cover activation.");
                    authority.Add(new(selected.Id, selected.Revision, action, selected.ExpiresAt!.Value));
                }
            }
            else
            {
                // Explicit stop/destroy authority remains useful after a lease expires.
                var grant = grants.First(x => x.Action == request.Action && x.Id != Guid.Empty && x.Revision > 0 && x.ExpiresAt > now);
                authority.Add(new(grant.Id, grant.Revision, grant.Action, grant.ExpiresAt!.Value));
            }
            var pending = await db.ComputeOperations.Where(x => x.EnvironmentId == environment.Id &&
                (x.Status == "Pending" || x.Status == "Dispatching" || x.Status == "Reconciling" || x.Status == "Blocked")).ToListAsync(token);
            if (request.Action != InfrastructureActions.Destroy && pending.Count != 0)
                throw new InvalidOperationException("A lifecycle operation is still in progress.");
            if (request.Action == InfrastructureActions.Start && environment.State != ComputeLifecycleState.Stopped ||
                request.Action is InfrastructureActions.Stop or InfrastructureActions.Restart && environment.State is not (ComputeLifecycleState.Ready or ComputeLifecycleState.Busy))
                throw new InvalidOperationException("This lifecycle action does not match the observed environment state.");
            foreach (var previous in pending)
            {
                previous.Status = "Superseded"; previous.CompletedAt = now; previous.Revision++;
            }
            environment.Generation = checked(environment.Generation + 1);
            environment.DesiredState = request.Action switch
            {
                InfrastructureActions.Destroy => ComputeDesiredState.Destroyed,
                InfrastructureActions.Stop => ComputeDesiredState.Stopped,
                _ => ComputeDesiredState.Running
            };
            // Observed state and physical quota remain unchanged until provider confirmation.
            environment.UpdatedAt = now; environment.NextAttemptAt = now;
            db.ComputeOperations.Add(new()
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, InstallationId = installationId,
                EnvironmentId = environment.Id, Generation = environment.Generation, Action = request.Action,
                IdempotencyKey = request.IdempotencyKey, RequestDigest = digest,
                AuthorityJson = JsonSerializer.Serialize(authority, Json), CreatedAt = now, NextAttemptAt = now
            });
            QueueAudit(environment, "Accepted", authority, request.Action);
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return View(environment);
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }
}
