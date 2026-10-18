using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

/// <summary>Fresh node proof plus current ownership, installation and exact recorded grants before public package ingress.</summary>
public sealed class ComputeDependencyAccess(CSweetDbContext db, ComputeProviderWorkRequestVerifier verifier, ComputeBroker broker, TimeProvider clock)
{
    public async Task<ComputePublicationPermission> AuthorizeAsync(SignedComputeProviderWorkRequest envelope, CancellationToken token)
    {
        var proof = await verifier.VerifyAsync(envelope, token);
        if (proof.OperationId is not { } id || proof.AfterOperationId is not null || proof.ResultSequence is not null || proof.ResultDigest is not null)
            throw new UnauthorizedAccessException("An exact dependency operation is required.");
        var operation = await db.ComputeOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == proof.OrganizationId, token);
        if (operation is null || operation.Action != InfrastructureActions.Execute || operation.Status != "Dispatching") return new(id, false);
        var environment = await db.ComputeEnvironments.AsNoTracking().SingleAsync(x => x.Id == operation.EnvironmentId, token);
        if (environment.ProviderNodeId != proof.NodeId || environment.ProviderId != proof.ProviderId ||
            environment.Generation != operation.Generation || environment.InstallationId != operation.InstallationId ||
            environment.LeaseExpiresAt <= clock.GetUtcNow() || environment.DesiredState != ComputeDesiredState.Running ||
            environment.State != ComputeLifecycleState.Busy || environment.TeardownConfirmedAt is not null) return new(id, false);
        try
        {
            var specification = JsonSerializer.Deserialize<ComputeSpecification>(environment.SpecificationJson, ComputeProtocol.Json)!;
            var workload = JsonSerializer.Deserialize<ComputeWorkload>(operation.WorkloadJson!, ComputeProtocol.Json)!.Validate(specification.OperatingSystem);
            if (workload.NodeDependencies is null) return new(id, false);
            var evidence = JsonSerializer.Deserialize<ComputeActionAuthorization[]>(operation.AuthorityJson, ComputeProtocol.Json)!;
            if (evidence.Length != 2 || !evidence.Select(x => x.Action).ToHashSet().SetEquals(workload.RequiredActions)) return new(id, false);
            var current = await broker.GrantsAsync(environment.OrganizationId, environment.InstallationId, environment.WorkstreamId, token);
            foreach (var grant in evidence)
            {
                await broker.RequireActorAsync(environment.OrganizationId, environment.InstallationId, environment.WorkstreamId, grant.Action, token, environment.Id);
                var value = current.SingleOrDefault(x => x.Id == grant.GrantId && x.Revision == grant.Revision && x.Action == grant.Action && x.ExpiresAt == grant.ExpiresAt);
                var constraints = value is null ? null : JsonSerializer.Deserialize<ComputeGrantConstraints>(value.ConstraintsJson, ComputeProtocol.Json);
                if (constraints?.Allows(specification, 0) != true || value!.ExpiresAt <= clock.GetUtcNow() ||
                    constraints.EnvironmentId is { } target && target != environment.Id || grant.Action == InfrastructureActions.Outbound &&
                    (!constraints.AllowOutbound || (constraints.AllowedDependencyRegistries is not null && !constraints.AllowedDependencyRegistries.Contains(ComputeNodeDependencies.Registry)))) return new(id, false);
            }
            return new(id, true);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException) { return new(id, false); }
    }
}
