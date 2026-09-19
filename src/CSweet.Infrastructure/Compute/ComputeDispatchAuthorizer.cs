using CSweet.Compute.Contracts;
using System.Data;
using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Application.Setup;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

/// <summary>Commits an exclusive, short-lived dispatch claim before returning signed provider authority.</summary>
public sealed class ComputeDispatchAuthorizer(CSweetDbContext db, ComputeBroker broker, IComputeDispatchSigner signer, TimeProvider clock)
{
    public Task<ComputeDispatchPacket?> ClaimAsync(Guid operationId, CancellationToken token) => ClaimCoreAsync(operationId, null, token);

    internal Task<ComputeDispatchPacket?> ClaimForNodeAsync(Guid operationId, ComputeProviderWorkRequest node, CancellationToken token) =>
        ClaimCoreAsync(operationId, node, token);

    private async Task<ComputeDispatchPacket?> ClaimCoreAsync(Guid operationId, ComputeProviderWorkRequest? requestingNode, CancellationToken token)
    {
        try
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
            var now = clock.GetUtcNow();
            var operation = await db.ComputeOperations.SingleOrDefaultAsync(x => x.Id == operationId, token);
            if (operation is null || operation.Status is not ("Pending" or "Dispatching" or "Reconciling") ||
                operation.NextAttemptAt > now || operation.DispatchLeaseExpiresAt > now) return null;
            var environment = await db.ComputeEnvironments.SingleAsync(x => x.Id == operation.EnvironmentId, token);
            if (requestingNode is not null && (environment.OrganizationId != requestingNode.OrganizationId ||
                environment.ProviderNodeId != requestingNode.NodeId || environment.ProviderId != requestingNode.ProviderId))
                throw new UnauthorizedAccessException("The operation belongs to another provider placement.");
            if (environment.Generation != operation.Generation || environment.TeardownConfirmedAt is not null) return null;
            var node = await db.ComputeNodes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == environment.ProviderNodeId &&
                x.OrganizationId == environment.OrganizationId && x.ProviderId == environment.ProviderId && x.Enabled, token);
            if (node is null) return await BlockAsync("node-unavailable");
            ComputeSpecification spec;
            ComputeTemplate? template = null;
            ComputeWorkload? workload = null;
            var authority = new List<ComputeActionAuthorization>();
            var mode = operation.Attempts > 0 ? ComputeDispatchMode.Observe : ComputeDispatchMode.Execute;
            var expires = now.AddMinutes(1);
            try
            {
                spec = JsonSerializer.Deserialize<ComputeSpecification>(environment.SpecificationJson, ComputeBroker.Json)
                    ?? throw new UnauthorizedAccessException();
                if (!spec.IsValid || operation.OrganizationId != environment.OrganizationId || operation.InstallationId != environment.InstallationId)
                    throw new UnauthorizedAccessException();
                if (mode == ComputeDispatchMode.Execute)
                {
                    var workloadAction = operation.Action is InfrastructureActions.Execute or InfrastructureActions.PublishPort;
                    if (workloadAction) {
                        if (environment.LeaseExpiresAt <= now.AddSeconds(45) || operation.WorkloadJson is null || environment.ProviderResourceId is null) throw new UnauthorizedAccessException();
                        workload = JsonSerializer.Deserialize<ComputeWorkload>(operation.WorkloadJson, ComputeBroker.Json)?.Validate(spec.OperatingSystem) ?? throw new UnauthorizedAccessException();
                        if ((workload.Command is not null) != (operation.Action == InfrastructureActions.Execute)) throw new UnauthorizedAccessException();
                    }
                    var energizing = operation.Action is InfrastructureActions.Provision or InfrastructureActions.Start or InfrastructureActions.Restart;
                    if (energizing && environment.LeaseExpiresAt <= now) throw new UnauthorizedAccessException();
                    var required = energizing
                        ? ComputePolicy.RequiredProvisionActions(spec).Where(x => x != InfrastructureActions.Provision).Prepend(operation.Action).ToArray()
                        : operation.Action == InfrastructureActions.PublishPort ? new[] { operation.Action, InfrastructureActions.Inbound } : new[] { operation.Action };
                    if (operation.Action is not (InfrastructureActions.Provision or InfrastructureActions.Start or InfrastructureActions.Restart or
                        InfrastructureActions.Stop or InfrastructureActions.Destroy or InfrastructureActions.Execute or InfrastructureActions.PublishPort)) throw new UnauthorizedAccessException();
                    var recorded = JsonSerializer.Deserialize<ComputeActionAuthorization[]>(operation.AuthorityJson, ComputeBroker.Json)
                        ?? throw new UnauthorizedAccessException();
                    if (recorded.Length != required.Length || recorded.Select(x => x.Action).Distinct().Count() != required.Length)
                        throw new UnauthorizedAccessException();
                    var current = await broker.GrantsAsync(environment.OrganizationId, environment.InstallationId, environment.WorkstreamId, token);
                    var reservations = await db.ComputeEnvironments.CountAsync(x => x.OrganizationId == environment.OrganizationId &&
                        x.InstallationId == environment.InstallationId && x.TeardownConfirmedAt == null, token);
                    foreach (var action in required)
                    {
                        await broker.RequireActorAsync(environment.OrganizationId, environment.InstallationId, environment.WorkstreamId, action, token, environment.Id);
                        var evidence = recorded.SingleOrDefault(x => x.Action == action) ?? throw new UnauthorizedAccessException();
                        var grant = current.SingleOrDefault(x => x.Id == evidence.GrantId && x.Revision == evidence.Revision &&
                            x.Action == action && x.ExpiresAt == evidence.ExpiresAt) ?? throw new UnauthorizedAccessException();
                        if (energizing && (grant.ExpiresAt < environment.LeaseExpiresAt ||
                            JsonSerializer.Deserialize<ComputeGrantConstraints>(grant.ConstraintsJson, ComputeBroker.Json)?.Allows(spec, Math.Max(0, reservations - 1)) != true))
                            throw new UnauthorizedAccessException();
                        if (workloadAction) {
                            var constraints = JsonSerializer.Deserialize<ComputeGrantConstraints>(grant.ConstraintsJson, ComputeBroker.Json);
                            if (grant.ExpiresAt < environment.LeaseExpiresAt || constraints?.Allows(spec, Math.Max(0, reservations - 1)) != true ||
                                constraints.EnvironmentId is { } target && target != environment.Id ||
                                workload!.PublishPort is { } port && constraints.AllowedPublishedPorts?.Contains(port) != true) throw new UnauthorizedAccessException();
                        }
                        if (grant.ExpiresAt < expires) expires = grant.ExpiresAt!.Value;
                        authority.Add(evidence);
                    }
                    if (energizing)
                    {
                        if (environment.LeaseExpiresAt < expires) expires = environment.LeaseExpiresAt;
                        var approved = await db.ComputeTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == environment.OrganizationId &&
                            x.TemplateId == spec.TemplateId && x.Enabled, token) ?? throw new UnauthorizedAccessException();
                        if (!await db.ComputeTemplatePlacements.AsNoTracking().AnyAsync(x => x.TemplateRegistrationId == approved.Id &&
                            x.NodeId == node.Id && x.OrganizationId == environment.OrganizationId && x.Enabled, token)) throw new UnauthorizedAccessException();
                        if (operation.Action == InfrastructureActions.Provision)
                        {
                            if (approved.TemplateJson != operation.TemplateJson) throw new UnauthorizedAccessException();
                            template = JsonSerializer.Deserialize<ComputeTemplate>(operation.TemplateJson, ComputeBroker.Json);
                            if (template?.Matches(spec) != true) throw new UnauthorizedAccessException();
                        }
                        else if (environment.ProviderResourceId is null) throw new UnauthorizedAccessException();
                    }
                }
            }
            catch (Exception error) when (error is UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
            { return await BlockAsync("dispatch-authority-denied"); }

            var claimId = Guid.NewGuid();
            var authorization = new ComputeDispatchAuthorization(claimId, operation.Id, environment.Id, environment.OrganizationId,
                environment.InstallationId, node.Id, node.ProviderId, environment.Generation, operation.Action, mode,
                ComputeBroker.Digest(environment.SpecificationJson), template is null ? null : ComputeBroker.Digest(operation.TemplateJson),
                environment.ProviderResourceId, now, expires, environment.LeaseExpiresAt, authority, workload is null ? null : ComputeBroker.Digest(JsonSerializer.Serialize(workload, ComputeBroker.Json)));
            var signed = await signer.SignAsync(authorization, token);
            operation.DispatchLeaseId = claimId; operation.DispatchLeaseExpiresAt = expires;
            operation.Attempts++; operation.Revision++; operation.Status = "Dispatching"; operation.NextAttemptAt = expires;
            operation.FailureCode = null;
            environment.AttemptCount++; environment.LastAttemptAt = now; environment.NextAttemptAt = expires;
            environment.LastFailureCode = null; environment.UpdatedAt = now;
            QueueAudit("Authorized", null, mode);
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(signed, spec, template, workload);

            async Task<ComputeDispatchPacket?> BlockAsync(string code)
            {
                operation.Status = "Blocked"; operation.FailureCode = code; operation.Revision++;
                environment.LastFailureCode = code; environment.UpdatedAt = now;
                QueueAudit("Blocked", code, null);
                await db.SaveChangesAsync(token);
                if (transaction is not null) await transaction.CommitAsync(token);
                return null;
            }

            void QueueAudit(string outcome, string? code, ComputeDispatchMode? dispatchMode)
            {
                var id = Guid.NewGuid();
                var evidence = new AuditEventWriteRequest("compute.dispatch.v1", Category: "Infrastructure", Outcome: outcome,
                    OrganizationId: environment.OrganizationId, EntityType: "ComputeEnvironment", EntityId: environment.Id,
                    Actor: new("Platform"), OccurredAt: now, UseAmbientOrganization: false, EventId: id, ErrorCode: code,
                    MetadataJson: JsonSerializer.Serialize(new { operation.Id, operation.Action, operation.Generation, operation.DispatchLeaseId,
                        environment.InstallationId, environment.ProviderNodeId, dispatchMode, operation.AuthorityJson }));
                db.AuditOutbox.Add(new() { Id = id, CreatedAt = now, RequestJson = JsonSerializer.Serialize(evidence) });
            }
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }
}
