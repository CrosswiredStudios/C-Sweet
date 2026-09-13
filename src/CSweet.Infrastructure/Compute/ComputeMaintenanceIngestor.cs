using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

/// <summary>Appends authenticated historical maintenance evidence; never grants authority or releases resources.</summary>
public sealed class ComputeMaintenanceIngestor(CSweetDbContext db, ComputeMaintenanceVerifier verifier, IAuditEventWriter audit)
{
    public async Task<Guid> ApplyAsync(SignedComputeMaintenanceDelivery envelope, CancellationToken token)
    {
        try
        {
            var (delivery, evidence) = await verifier.VerifyAsync(envelope, token);
            var recorded = await db.ComputeOperations.AsNoTracking().Where(x => x.Id == evidence.ProvisionOperationId)
                .Join(db.ComputeEnvironments.AsNoTracking(), x => x.EnvironmentId, x => x.Id,
                    (operation, environment) => new { Operation = operation, Environment = environment }).SingleOrDefaultAsync(token)
                ?? throw new UnauthorizedAccessException("Original provisioning history is unavailable.");
            var operation = recorded.Operation; var environment = recorded.Environment;
            var grants = JsonSerializer.Deserialize<ComputeActionAuthorization[]>(operation.AuthorityJson, ComputeProtocol.Json);
            if (operation.Action != InfrastructureActions.Provision || operation.Attempts < 1 ||
                operation.OrganizationId != evidence.OrganizationId || operation.InstallationId != evidence.InstallationId ||
                environment.OrganizationId != evidence.OrganizationId || environment.InstallationId != evidence.InstallationId ||
                environment.Id != evidence.EnvironmentId || environment.ProviderNodeId != evidence.NodeId || environment.ProviderId != evidence.ProviderId ||
                evidence.Generation < operation.Generation || evidence.Generation > environment.Generation ||
                evidence.SpecificationDigest != ComputeProtocol.Digest(environment.SpecificationJson) || evidence.Persistence != environment.Persistence ||
                evidence.LeaseExpiresAt != environment.LeaseExpiresAt || evidence.OccurredAt < operation.CreatedAt.AddSeconds(-30) ||
                environment.ProviderResourceId is { } known && evidence.ResourceId is not null && evidence.ResourceId != known ||
                grants is null || !grants.OrderBy(x => x.Action, StringComparer.Ordinal).SequenceEqual(evidence.ProvisionGrants.OrderBy(x => x.Action, StringComparer.Ordinal)))
                throw new UnauthorizedAccessException("Maintenance evidence does not match original provisioning terms.");
            // Delivery timestamps/signatures are deliberately omitted: reconnect may re-sign identical historical evidence.
            return await audit.AppendAsync(new("compute.provider-maintenance.v1", Category: "Infrastructure", Direction: "Inbound",
                Outcome: evidence.Phase.ToString(), OrganizationId: evidence.OrganizationId, EntityType: "ComputeEnvironment", EntityId: evidence.EnvironmentId,
                Actor: new("ComputeProvider", InstallationId: evidence.InstallationId), OccurredAt: evidence.OccurredAt,
                TraceId: evidence.EventId, UseAmbientOrganization: false, EventId: evidence.EventId, ErrorCode: evidence.FailureCode,
                MetadataJson: JsonSerializer.Serialize(new { environment.WorkstreamId, delivery.EventJson, delivery.EventDigest })), token);
        }
        catch (UnauthorizedAccessException)
        {
            await audit.AppendAsync(new("compute.provider-maintenance.rejected.v1", Category: "Infrastructure", Outcome: "Rejected",
                Actor: new("ComputeProvider", IdentityVerified: false), ErrorCode: "invalid_provider_evidence", UseAmbientOrganization: false), token);
            throw;
        }
    }
}
