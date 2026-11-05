using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

/// <summary>Node-authenticated input pages fenced by the exact live dispatch and current execution grants.</summary>
public sealed class ComputeBuildInputAccess(CSweetDbContext db, ComputeProviderWorkRequestVerifier verifier,
    ComputeBroker broker, TimeProvider clock)
{
    public async Task<ComputeBuildInputPage> ReadAsync(SignedComputeProviderWorkRequest envelope, CancellationToken token)
    {
        var proof = await verifier.VerifyAsync(envelope, token, allowBuildInput: true);
        if (proof.OperationId is not { } id || proof.InputOffset is not { } offset || proof.DispatchId is not { } dispatch)
            throw new UnauthorizedAccessException("An exact input operation and dispatch are required.");
        var now = clock.GetUtcNow();
        var operation = await db.ComputeOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == proof.OrganizationId, token);
        if (operation is null || operation.Action != InfrastructureActions.Execute || operation.Status != "Dispatching" ||
            operation.DispatchLeaseId != dispatch || operation.DispatchLeaseExpiresAt is null || operation.DispatchLeaseExpiresAt <= now) throw Denied();
        var environment = await db.ComputeEnvironments.AsNoTracking().SingleAsync(x => x.Id == operation.EnvironmentId, token);
        if (environment.OrganizationId != proof.OrganizationId || environment.ProviderNodeId != proof.NodeId || environment.ProviderId != proof.ProviderId ||
            environment.Generation != operation.Generation || environment.InstallationId != operation.InstallationId ||
            environment.LeaseExpiresAt <= now || environment.DesiredState != ComputeDesiredState.Running ||
            environment.State != ComputeLifecycleState.Busy || environment.TeardownConfirmedAt is not null) throw Denied();
        var spec = JsonSerializer.Deserialize<ComputeSpecification>(environment.SpecificationJson, ComputeProtocol.Json)!;
        var workload = JsonSerializer.Deserialize<ComputeWorkload>(operation.WorkloadJson!, ComputeProtocol.Json)!.Validate(spec.OperatingSystem);
        var archive = workload.BuildArchive ?? throw Denied();
        var recorded = JsonSerializer.Deserialize<ComputeActionAuthorization[]>(operation.AuthorityJson, ComputeProtocol.Json)!;
        var required = workload.RequiredActions.Concat(spec.NetworkPolicy.AllowOutbound ? [InfrastructureActions.Outbound] : Array.Empty<string>()).Distinct().ToArray();
        if (recorded.Length != required.Length || !recorded.Select(x => x.Action).ToHashSet().SetEquals(required)) throw Denied();
        var current = await broker.GrantsAsync(environment.OrganizationId, environment.InstallationId, environment.WorkstreamId, token);
        foreach (var grant in recorded)
        {
            await broker.RequireActorAsync(environment.OrganizationId, environment.InstallationId, environment.WorkstreamId, grant.Action, token, environment.Id);
            var value = current.SingleOrDefault(x => x.Id == grant.GrantId && x.Revision == grant.Revision && x.Action == grant.Action && x.ExpiresAt == grant.ExpiresAt);
            var constraints = value is null ? null : JsonSerializer.Deserialize<ComputeGrantConstraints>(value.ConstraintsJson, ComputeProtocol.Json);
            if (constraints?.Allows(spec, 0) != true || value!.ExpiresAt <= now ||
                constraints.EnvironmentId is { } target && target != environment.Id) throw Denied();
        }
        var bytes = await ComputeBuildInputStore.ReadAsync(db, environment.OrganizationId, operation.InstallationId, archive, offset, token);
        return new(id, dispatch, offset, archive.Length, archive.BuildSha256, bytes);
    }
    private static UnauthorizedAccessException Denied() => new("Build input is unavailable to this dispatch.");
}
