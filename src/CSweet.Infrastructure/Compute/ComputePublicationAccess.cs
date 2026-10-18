using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

public sealed class ComputePublicationAccess(CSweetDbContext db, ComputeProviderWorkRequestVerifier verifier, ComputeBroker broker, TimeProvider clock)
{
    public async Task<ComputePublicationPermission> AuthorizeAsync(SignedComputeProviderWorkRequest envelope, CancellationToken token)
    {
        var proof = await verifier.VerifyAsync(envelope, token);
        if (proof.OperationId is not { } id || proof.AfterOperationId is not null || proof.ResultSequence is not null || proof.ResultDigest is not null)
            throw new UnauthorizedAccessException("An exact publication identity is required.");
        var operation = await db.ComputeOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == proof.OrganizationId, token);
        if (operation is null || operation.Action != InfrastructureActions.PublishPort || operation.Status is not ("Dispatching" or "Completed")) return new(id, false);
        var environment = await db.ComputeEnvironments.AsNoTracking().SingleAsync(x => x.Id == operation.EnvironmentId, token);
        if (environment.ProviderNodeId != proof.NodeId || environment.ProviderId != proof.ProviderId || environment.Generation != operation.Generation ||
            environment.LeaseExpiresAt <= clock.GetUtcNow() || environment.DesiredState != ComputeDesiredState.Running ||
            environment.State is not (ComputeLifecycleState.Ready or ComputeLifecycleState.Busy) || environment.TeardownConfirmedAt is not null) return new(id, false);
        try
        {
            var evidence = JsonSerializer.Deserialize<ComputeActionAuthorization[]>(operation.AuthorityJson, ComputeProtocol.Json);
            var specification = JsonSerializer.Deserialize<ComputeSpecification>(environment.SpecificationJson, ComputeProtocol.Json)!;
            var required = new[] { InfrastructureActions.Inbound, InfrastructureActions.PublishPort }
                .Concat(specification.NetworkPolicy.AllowOutbound ? [InfrastructureActions.Outbound] : Array.Empty<string>()).ToHashSet();
            if (evidence is null || evidence.Length != required.Count || !evidence.Select(x => x.Action).ToHashSet().SetEquals(required)) return new(id, false);
            var current = await broker.GrantsAsync(environment.OrganizationId, environment.InstallationId, environment.WorkstreamId, token);
            foreach (var grant in evidence)
            {
                await broker.RequireActorAsync(environment.OrganizationId, environment.InstallationId, environment.WorkstreamId, grant.Action, token, environment.Id);
                if (!current.Any(x => x.Id == grant.GrantId && x.Revision == grant.Revision && x.Action == grant.Action && x.ExpiresAt == grant.ExpiresAt)) return new(id, false);
            }
            return new(id, true);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or JsonException or InvalidOperationException) { return new(id, false); }
    }
}
