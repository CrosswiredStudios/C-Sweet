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
        var fence = await ComputeDispatchFence.AuthorizeAsync(db, verifier, broker, clock, envelope, evidencePart: false, token);
        var archive = fence.Workload.BuildArchive ?? throw ComputeDispatchFence.Denied();
        var bytes = await ComputeBuildInputStore.ReadAsync(db, fence.Environment.OrganizationId, fence.Operation.InstallationId, archive, fence.Offset, token);
        return new(fence.Operation.Id, fence.DispatchId, fence.Offset, archive.Length, archive.BuildSha256, bytes);
    }
}

/// <summary>
/// The provider-side transfer authority shared by build input and evidence parts: a node-signed request naming one
/// operation, its live dispatch lease and an offset, for a Busy environment on that node, with every recorded execution
/// grant still current. Nothing here trusts the agent or the request beyond those identities.
/// </summary>
internal static class ComputeDispatchFence
{
    internal sealed record Authorized(ComputeProviderWorkRequest Proof, ComputeOperation Operation, ComputeEnvironment Environment,
        ComputeWorkload Workload, Guid DispatchId, int Offset);

    internal static async Task<Authorized> AuthorizeAsync(CSweetDbContext db, ComputeProviderWorkRequestVerifier verifier,
        ComputeBroker broker, TimeProvider clock, SignedComputeProviderWorkRequest envelope, bool evidencePart, CancellationToken token)
    {
        var proof = await verifier.VerifyAsync(envelope, token, allowBuildInput: !evidencePart, allowEvidencePart: evidencePart);
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
        return new(proof, operation, environment, workload, dispatch, offset);
    }

    internal static UnauthorizedAccessException Denied() => new("Build input is unavailable to this dispatch.");
}
